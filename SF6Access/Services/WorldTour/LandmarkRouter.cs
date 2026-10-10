using System;
using System.Collections.Generic;
using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>The city router. It answers "how do I get there" without ever touching the navmesh.</b>
///
/// <para><b>Why this exists.</b> The owner's requirement is that the FIRST visit must be
/// enough: <i>第一次来也一定能找到目标，因为明眼玩家都能走到</i>. Reading the engine's navmesh
/// was the natural way to get what sighted players get by looking, and it was tried twice on
/// 2026-10-03. Generation 1 of the probe died reading a node's fields; generation 2 died
/// enumerating the node's TYPE. Two unrelated operations, one shared input — so what came back
/// from <c>queryClosestNode</c> is not a usable engine reference, and that whole route is
/// retired (see <c>NavMeshIgnition.RETIRED</c>).</para>
///
/// <para><b>What is left is the pathfinder itself</b> — <see cref="FieldNavPath"/>, the engine's
/// own <c>queryPathSync</c>, which has never crashed in this codebase and which already answers
/// with real multi-point routes. It cannot hand us the map, but it CAN answer one question we
/// can build a map from: <i>can I walk from here to there?</i></para>
///
/// <para><b>The graph.</b> The nodes are places this mod already knows by name — fast travel
/// points, doors, ladder ends, map icons — plus where the player is and where the objective is.
/// Two kinds of edge join them:</para>
/// <list type="bullet">
/// <item><description><b>Portal edges, and these are the whole point.</b> A door or a ladder
/// publishes BOTH of its ends (<c>AccessPointCatalog.Point</c> carries <c>OtherX/Y/Z</c>), so the
/// connection through it costs nothing to know and nothing to verify. These are exactly the links
/// the navmesh does not model — the reason today's very failure reads
/// <c>PathNotFound ... it is on a DISCONNECTED piece of mesh - an upper floor or roof entered
/// through a door, lift or scripted transition</c>. Crossing floors here is free.</description></item>
/// <item><description><b>Walk edges</b>, discovered by asking <c>FieldNavPath.TryConnect</c> about
/// a pair that is close enough to be worth asking about. Every answer is cached, so a pair is
/// never asked twice.</description></item>
/// </list>
///
/// <para><b>Why best-first and not plain breadth-first.</b> Every edge costs a rate-limited query
/// (250 ms), so the search order decides whether an answer arrives in seconds or never. Nodes are
/// always expanded nearest-to-the-objective first, which marches the frontier toward the goal
/// instead of fanning out in all directions. The search still covers everything it can afford:
/// it is the ORDER that is greedy, not the acceptance. A path that must first walk away from the
/// objective — round a U-shaped fence, through a far door — is still found, because nothing here
/// requires each step to get closer. That is precisely the completeness the old try-forward
/// search lacked.</para>
///
/// <para><b>Laziness is mandatory, not an optimisation.</b> The whole thing runs one query per
/// limiter window during the ordinary per-frame pump, so it never stalls the frame and never
/// starves anything else. It is asked only when everything else has answered "no route", so the
/// lookups it spends are ones nothing else was going to use.</para>
///
/// <para><b>Read-only.</b> Not one engine object is written and nothing about the avatar is
/// touched. This file asks questions and returns coordinates.</para>
/// </summary>
internal static class LandmarkRouter
{
    /// <summary>What sort of place a node is. Only ever used to SAY what the player is walking
    /// to: a door is worth naming, a bare coordinate is not.</summary>
    internal enum Kind { Start, Objective, Portal, FastTravel, MapIcon }

    /// <summary>One place to walk to, already named.</summary>
    internal readonly record struct Hop(string Name, float X, float Y, float Z, Kind What);

    /// <summary>How far apart two places may be and still be worth asking the pathfinder about.
    /// Was 90 m and the search never left the starting place: in this city a mission objective
    /// sits 89 to 321 m from wherever the player stands, so with 90 m most of the nodes were out
    /// of range of everything, the frontier emptied after one place and the search reported
    /// exhaustion having not actually looked at anything.
    /// Raised to 220 m on 2026-10-04. The pathfinder answers anyway, so a too-long guess costs a
    /// window that comes back "no"; a too-short one costs the whole search.</summary>
    private const float EDGE_M = 220f;

    /// <summary>How far the objective must be above the walker before going DOWN starts costing,
    /// and how far below them a place must be before it is charged. Two metres — this codebase's
    /// own definition of a change of floor (<see cref="LevelRoute.LevelM"/>).</summary>
    private static float LevelM => LevelRoute.LevelM;

    /// <summary>One metre of going the wrong way in height, counted as metres of flat walking.
    /// A place six metres below is treated as eighteen metres further off: enough to lose to an
    /// ordinary alternative that stays level, not enough to disappear entirely.</summary>
    private const float HEIGHT_WEIGHT = 3f;

    /// <summary>Doors, fast travel points and map icons number in the hundreds across a city; a
    /// search over all of them is not affordable, and most are irrelevant to one journey. Only the
    /// places nearest this particular journey take part.</summary>
    private const int MAX_NODES = 40;

    /// <summary>Two places within this are the same place as far as walking is concerned — the
    /// top of a ladder and the spawn point beside it, for instance.</summary>
    private const float MERGE_M = 3f;

    /// <summary>How far the player may move before the search is rebuilt. All its cached answers
    /// stay valid — they are about pairs of places, not about the player — but the START node is
    /// the player, and a graph rooted ten metres behind no longer describes where they are.</summary>
    private const float REBUILD_M = 12f;

    /// <summary>A hop the player is already standing on is not a hop.</summary>
    private const float ARRIVE_M = 3f;

    /// <summary>The ceiling on queries for one build. It is what turns "the graph did not contain
    /// the answer" from an endless background search into a verdict the player can be told.</summary>
    private const int MAX_QUERIES = 200;

    private static readonly List<Hop> _nodes = new();
    private static readonly List<(int A, int B, string Name)> _portals = new();

    /// <summary>Every walkability answer so far, including the NOs. The negative answers are the
    /// more valuable half: without them the same doomed pair would be re-asked every pass.</summary>
    private static readonly Dictionary<long, bool> _canWalk = new();

    private static readonly List<int> _frontier = new();
    private static int[] _parent = Array.Empty<int>();
    private static bool[] _seen = Array.Empty<bool>();

    private static long _nextQueryAt;
    private static int _queries;
    private static bool _built, _found, _exhausted, _logged;
    private static readonly HashSet<int> _refused = new();
    private static float _anchorX, _anchorZ, _tx, _tz;
    // The objective's and the player's heights, kept because Key() needs to know which way is
    // UP for this particular journey. Recording them here rather than passing them down keeps
    // every sorting decision in one place.
    private static float _ty, _py;
    private static int _reached = -1;

    /// <summary>The search has run out of things it can afford to ask. This is a PROVEN verdict
    /// about the graph as built, not a shrug: within these landmarks and these edges, no route
    /// exists.</summary>
    internal static bool Exhausted => _exhausted;

    /// <summary>True once the objective itself has been reached by the search.</summary>
    internal static bool Found => _found;

    /// <summary>How much of the graph the search has actually walked, for the log.</summary>
    internal static int Seen { get; private set; }

    /// <summary>
    /// <b>The one entry point: where should the player walk next.</b>
    ///
    /// <para>Called every frame. It builds the graph when it has none, spends at most one
    /// rate-limited query per call, and returns the next hop it currently knows — so the caller
    /// gets the best answer available NOW rather than waiting for a complete search that may take
    /// tens of seconds. The answer improves as the search goes on.</para>
    /// </summary>
    internal static bool TryHop(float px, float py, float pz, float tx, float ty, float tz, out Hop hop)
    {
        hop = default;
        if (!FieldNavPath.Available) { Reset(); return false; }

        long now = Environment.TickCount64;
        if (!_built || Flat(px, pz, _anchorX, _anchorZ) > REBUILD_M
                    || Flat(tx, tz, _tx, _tz) > REBUILD_M)
        {
            Build(px, py, pz, tx, ty, tz);
        }

        Step(now);
        return BestHop(px, pz, out hop);
    }

    /// <summary>Drop everything. Called on a new objective, a scene change, and whenever the
    /// pathfinder itself is unavailable.</summary>
    internal static void Reset()
    {
        _built = _found = _exhausted = _logged = false;
        _nodes.Clear();
        _portals.Clear();
        _canWalk.Clear();
        _frontier.Clear();
        _parent = Array.Empty<int>();
        _seen = Array.Empty<bool>();
        _queries = 0;
        _nextQueryAt = 0;
        _reached = -1;
        _refused.Clear();
        Seen = 0;
    }

    /// <summary>Strike a place off the plan because it cannot be WALKED to even though the
    /// pathfinder could find nothing wrong with it — most often a spot whose straight line from
    /// here runs out over a ledge. Without this the search keeps offering the same leg, the
    /// warning keeps repeating, and the player stands still waiting for something new.</summary>
    internal static void Refuse(float x, float z)
    {
        if (!_built) return;
        for (int i = 0; i < _nodes.Count; i++)
        {
            var what = _nodes[i].What;
            if (what == Kind.Start || what == Kind.Objective) continue;
            if (Flat(_nodes[i].X, _nodes[i].Z, x, z) <= ARRIVE_M) _refused.Add(i);
        }
    }

    // ---------------------------------------------------------------------------------
    // building the graph
    // ---------------------------------------------------------------------------------

    /// <summary>Collect every place worth taking part in THIS journey, thin them out to what is
    /// affordable, and record the portal edges — the ones that cost nothing and cross floors.</summary>
    private static void Build(float px, float py, float pz, float tx, float ty, float tz)
    {
        Reset();
        _anchorX = px; _anchorZ = pz;
        _tx = tx; _tz = tz;
        // Kept for the height cost: which way is up is decided once per journey, not per node.
        _ty = ty; _py = py;

        var raw = new List<Hop>();
        var wanted = new List<(int A, int B, string Name)>();

        try
        {
            foreach (var p in FastTravelCatalog.Points())
                AddUnique(raw, new Hop(p.Name, p.X, p.Y, p.Z, Kind.FastTravel));
        }
        catch (Exception ex) { API.LogWarning("[SF6Access] City router: fast travel unreadable: " + ex.Message); }

        long now = Environment.TickCount64;
        try
        {
            foreach (var a in AccessPointCatalog.Near(px, py, pz, now))
            {
                if (!float.IsFinite(a.OtherX) || !float.IsFinite(a.OtherZ)) continue;
                // Measured in three dimensions, and that is the whole point of this line. A
                // ladder's two ends share their ground position exactly and differ only in
                // height — the level author files them as low (-45.9, 17.1, 24.5) and high
                // (-45.9, 23.4, 24.5). Judge them on the flat and every ladder in the city
                // reads as "one place listed twice", gets discarded as a dead end, and the
                // graph loses the only thing it has that changes floor.
                float span = Apart3(a.X, a.Y, a.Z, a.OtherX, a.OtherY, a.OtherZ);
                if (!float.IsFinite(span) || span < MERGE_M) continue;
                int near = AddUnique(raw, new Hop(a.Text, a.X, a.Y, a.Z, Kind.Portal));
                int far = AddUnique(raw, new Hop(a.Text, a.OtherX, a.OtherY, a.OtherZ, Kind.Portal));
                if (near != far) wanted.Add((near, far, a.Text));
            }
        }
        catch (Exception ex) { API.LogWarning("[SF6Access] City router: access points unreadable: " + ex.Message); }

        try
        {
            foreach (var e in MapIconSnapshot.For(WorldTourStateService.CityId))
            {
                if (string.IsNullOrEmpty(e.Name)) continue;
                AddUnique(raw, new Hop(e.Name, e.X, e.Y, e.Z, Kind.MapIcon));
            }
        }
        catch (Exception ex) { API.LogWarning("[SF6Access] City router: map icons unreadable: " + ex.Message); }

        // Which places take part. PORTAL ENDS COME FIRST, unconditionally: nothing else in the
        // graph describes a change of floor, and dropping one for being slightly further away
        // would silently delete the only route. The rest fill whatever room is left, nearest this
        // particular journey first.
        var order = new List<int>();
        foreach (var (a, b, _) in wanted) { AddOrder(order, a); AddOrder(order, b); }
        var remaining = new List<int>();
        for (int i = 0; i < raw.Count; i++) remaining.Add(i);
        remaining.Sort((i, j) => Score(raw[i]).CompareTo(Score(raw[j])));
        foreach (int i in remaining) AddOrder(order, i);

        _nodes.Add(new Hop("", px, py, pz, Kind.Start));        // 0: always the player
        _nodes.Add(new Hop("", tx, ty, tz, Kind.Objective));    // 1: always the goal
        var placed = new Dictionary<int, int>();
        foreach (int i in order)
        {
            if (_nodes.Count >= MAX_NODES) break;
            placed[i] = _nodes.Count;
            _nodes.Add(raw[i]);
        }
        foreach (var (a, b, name) in wanted)
        {
            if (placed.TryGetValue(a, out int na) && placed.TryGetValue(b, out int nb))
                _portals.Add((na, nb, name));
        }

        _parent = new int[_nodes.Count];
        _seen = new bool[_nodes.Count];
        for (int i = 0; i < _parent.Length; i++) _parent[i] = -1;
        _seen[0] = true;
        _frontier.Add(0);
        _built = true;
        Seen = 1;

        // The walk is read out of the log, so whether this journey is an upward one — and
        // therefore whether anywhere below the walker is being charged for it — has to be said.
        float climb = _ty - _py;
        API.LogInfo(FormattableString.Invariant(
            $"[SF6Access] City router: {_nodes.Count} places take part, {_portals.Count} of them ways through a door or up a ladder") +
            (climb >= LevelM
                ? FormattableString.Invariant($"; objective {climb:F1} m above you, so going down costs")
                : ""));

        float Score(Hop h) => MathF.Min(Flat(h.X, h.Z, px, pz), Flat(h.X, h.Z, tx, tz));
    }

    private static void AddOrder(List<int> order, int i)
    {
        if (!order.Contains(i)) order.Add(i);
    }

    /// <summary>Two records of the same place are one node. Returns where it lives in
    /// <paramref name="raw"/>, so a caller can go on to pair it with something else.</summary>
    private static int AddUnique(List<Hop> raw, Hop h)
    {
        if (!float.IsFinite(h.X) || !float.IsFinite(h.Z)) return -1;
        for (int i = 0; i < raw.Count; i++)
        {
            // Height counts. The foot of a ladder and its top are the same patch of ground to a
            // flat map and two different floors to whoever has to climb them.
            if (Apart3(raw[i].X, raw[i].Y, raw[i].Z, h.X, h.Y, h.Z) < MERGE_M) return i;
        }
        raw.Add(h);
        return raw.Count - 1;
    }

    // ---------------------------------------------------------------------------------
    // the search
    // ---------------------------------------------------------------------------------

    /// <summary>One affordable slice of the search. At most one query, at most every limiter
    /// window, so this can be called every frame without ever costing one.</summary>
    private static void Step(long now)
    {
        if (!_built || _found || _exhausted) return;
        if (now < _nextQueryAt) return;

        while (_frontier.Count > 0)
        {
            int u = PickFrontier();
            int v = PickCandidate(u);
            if (v < 0)
            {
                // Nothing left worth asking about this place: everything reachable from it has
                // been reached, or refused, or is out of walking range.
                _frontier.Remove(u);
                continue;
            }
            _nextQueryAt = now + FieldNavPath.MinQueryIntervalMs + 50;
            _queries++;
            Test(u, v);
            return;
        }

        _exhausted = true;
        if (!_logged)
        {
            _logged = true;
            API.LogWarning(FormattableString.Invariant(
                $"[SF6Access] City router: searched {Seen} of {_nodes.Count} places in {_queries} questions and did not reach the objective"));
        }
    }

    /// <summary>Nearest the objective first. This is the whole difference between an answer in
    /// seconds and an answer never: spending every window on the place closest to the goal keeps
    /// the frontier aimed at it.</summary>
    private static int PickFrontier()
    {
        int best = _frontier[0];
        float bestKey = Key(best);
        for (int i = 1; i < _frontier.Count; i++)
        {
            float k = Key(_frontier[i]);
            if (k < bestKey) { bestKey = k; best = _frontier[i]; }
        }
        return best;
    }

    private static float Key(int i)
        => Flat(_nodes[i].X, _nodes[i].Z, _tx, _tz) + HeightCost(_nodes[i].Y);

    /// <summary>What walking DOWN costs when the objective is UP.
    ///
    /// <para>Found in game on 2026-10-04, 13:00:37. The objective was eleven metres above the
    /// player (<c>no walk from (-38.4, 22.7, 35.0) to (-54.2, 34.3, 34.4) (+11.6 m vertical)</c>)
    /// and this router sent them to a ladder going DOWN, two metres away, followed by the
    /// reaches sentence telling them the ground ran out two metres along. Nothing in the
    /// ordering was wrong by its own rules: the ladder was the nearest node on the flat, and
    /// <see cref="Key"/> used to measure nothing but the flat.</para>
    ///
    /// <para><b>It is a cost, not a ban.</b> Some routes genuinely do go down before they go up
    /// — down a stair, along a passage, up another — and a ban would delete those routes
    /// outright, which is the one kind of repair this codebase never makes. A cost only loses
    /// to something better; with nothing else on offer the downward ladder is still what gets
    /// said.</para>
    /// </summary>
    private static float HeightCost(float y)
    {
        float climb = _ty - _py;                       // how far the objective is ABOVE the walker
        if (climb < LevelM) return 0f;                 // not an upward journey: height is free
        float down = _py - y;                          // how far this place is BELOW the walker
        if (down < LevelM) return 0f;                  // level with here, or higher: no cost
        return down * HEIGHT_WEIGHT;
    }

    /// <summary>The next place worth asking about from <paramref name="u"/>: unvisited, in
    /// walking range, not already answered, nearest the objective first.</summary>
    private static int PickCandidate(int u)
    {
        int best = -1;
        float bestKey = float.MaxValue;
        for (int v = 0; v < _nodes.Count; v++)
        {
            if (v == u || _seen[v]) continue;
            if (_refused.Contains(v)) continue;
            long cacheKey = Pair(u, v);
            if (_canWalk.ContainsKey(cacheKey)) continue;
            if (Flat(_nodes[u].X, _nodes[u].Z, _nodes[v].X, _nodes[v].Z) > EDGE_M) continue;
            float k = Key(v);
            if (k < bestKey) { bestKey = k; best = v; }
        }
        return best;
    }

    private static void Test(int u, int v)
    {
        var a = _nodes[u];
        var b = _nodes[v];
        var answer = FieldNavPath.TryConnect(a.X, a.Y, a.Z, b.X, b.Y, b.Z);

        // Throttled is not No. It is the limiter refusing to answer, and asking again later is
        // the only honest response; caching it as a refusal would close a door that was never
        // actually tried.
        if (!answer.Ok || answer.Throttled) { _nextQueryAt = Environment.TickCount64 + FieldNavPath.MinQueryIntervalMs; return; }

        _canWalk[Pair(u, v)] = answer.Exists;
        if (!answer.Exists) return;

        Reach(v, u);
        SpreadPortals();
        if (v == 1) _found = true;
        if (_queries >= MAX_QUERIES) _exhausted = true;
    }

    private static void Reach(int v, int from)
    {
        _seen[v] = true;
        _parent[v] = from;
        _frontier.Add(v);
        Seen++;
        if (_reached < 0 || Key(v) < Key(_reached)) _reached = v;
    }

    /// <summary>The edges that cost nothing, taken every time a place is reached, cascading: a
    /// door reached leads to the room behind it, and from there perhaps to a second door. This is
    /// how a route crosses a floor the navmesh cannot see.</summary>
    private static void SpreadPortals()
    {
        bool moved = true;
        while (moved)
        {
            moved = false;
            foreach (var (a, b, _) in _portals)
            {
                if (_seen[a] && !_seen[b]) { Reach(b, a); moved = true; }
                else if (_seen[b] && !_seen[a]) { Reach(a, b); moved = true; }
            }
        }
    }

    // ---------------------------------------------------------------------------------
    // the answer
    // ---------------------------------------------------------------------------------

    /// <summary>Where to walk now. Along the found route when there is one; toward the closest
    /// place the search has actually reached when there is not, which is still progress and
    /// never silence.</summary>
    private static bool BestHop(float px, float pz, out Hop hop)
    {
        hop = default;
        if (!_built) return false;

        int goal = _found ? 1 : _reached;
        if (goal <= 0) return false;

        // Walk the tree back to the player, then take the first place along it that is not
        // already under their feet.
        var chain = new List<int>();
        for (int i = goal; i > 0 && i < _parent.Length; i = _parent[i]) chain.Add(i);
        chain.Reverse();

        for (int i = 0; i < chain.Count; i++)
        {
            var h = _nodes[chain[i]];
            if (Flat(h.X, h.Z, px, pz) <= ARRIVE_M) continue;
            hop = h;
            return true;
        }
        return false;
    }

    private static long Pair(int i, int j)
    {
        int a = Math.Min(i, j), b = Math.Max(i, j);
        return ((long)a << 32) | (uint)b;
    }

    private static float Flat(float ax, float az, float bx, float bz)
    {
        float dx = ax - bx, dz = az - bz;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Distance with the height left in. Anything that decides whether two records are
    /// the same place has to use this, because "same spot on the ground" and "same place you can
    /// stand" stop being the same thing the moment the world has more than one floor.</summary>
    private static float Apart3(float ax, float ay, float az, float bx, float by, float bz)
    {
        float dx = ax - bx, dy = ay - by, dz = az - bz;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
