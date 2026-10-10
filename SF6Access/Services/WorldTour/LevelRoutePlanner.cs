using System;
using System.Collections.Generic;

#nullable enable
namespace SF6Access.Services.WorldTour;

/// <summary>Finds walk-and-ladder routes between disconnected navmesh regions, one connectivity question at a time.
/// When no route reaches the target, the plan climbs as near to the target's height as the ladders allow.</summary>
internal sealed class LevelRoutePlanner
{
    /// <summary>One avatar height: the borrowed navmesh agent's AgentHeight measured 2.0 m (2026-09-09).</summary>
    internal const float LevelM = 2f;
    /// <summary>How far from the target, on the flat, a partial plan may end: about one Metro City block. Chapter 2-2's
    /// right ladders stood 17 and 23 m from the boss; the wrong picks were other buildings 85 and 133 m away (2026-09-26).</summary>
    internal const float NeighbourhoodM = 32f;
    /// <summary>A ladder foot this close and level with a reached spot, with no walk to it, is taken as behind an
    /// obstacle the navmesh does not model. Chapter 2-2's sign-blocked walk between its two ladders is 10.5 m.</summary>
    internal const float BlockedHopM = 16f;
    private static readonly bool[] Directions = { true, false };

    internal readonly record struct Spot(float X, float Y, float Z);

    /// <summary>A connection the player makes with their hands between the standing points
    /// at its two ends: a ladder to climb, or — <see cref="Jump"/> — a crossing they step up
    /// onto or jump across, measured by <see cref="JumpLinkCatalog"/> and judged by
    /// <see cref="JumpLink"/>, or — <see cref="Stair"/> — a flight of narrow stairs found by
    /// <see cref="StairCatalog"/> and walked up. The planner treats all three alike; only the
    /// words differ. Ladder and stair are both climbs, and a stair is the one the player
    /// walks up with no action button at all, which is why it is worth naming apart.</summary>
    /// <see cref="Drop"/>: a place where the ground simply falls away — measured by
    /// <see cref="DescentScan"/> and stepped off, not climbed. Added 2026-10-07: a destination
    /// eight metres below was reported as having no route at all while eight measured drops
    /// stood within twelve metres of the player, because "a lower piece of ground" is not an
    /// object and so appears in no catalogue. Naming it apart matters for the words: telling
    /// someone to step across a nine-metre drop is worse than saying nothing.</summary>
    internal readonly record struct Link(Spot Low, Spot High, bool Jump = false, bool Stair = false, bool Lift = false, bool Drop = false);

    /// <summary>Walk to <see cref="To"/>, then take ladder <see cref="Link"/> to <see cref="Exit"/>; the last leg has Link -1.
    /// <see cref="Blocked"/>: the navmesh has no walk to this ladder's foot, though it stands close and level.
    /// <see cref="Jump"/>: the link is a step or a jump and not a ladder.
    /// <see cref="Stair"/>: the link is a flight of stairs, walked up.</summary>
    /// <see cref="Drop"/>: the leg ends by stepping off a drop and landing below.</summary>
    internal readonly record struct Leg(Spot To, Spot Exit, int Link, bool Up, bool Blocked = false, bool Jump = false, bool Stair = false, bool Lift = false, bool Drop = false);

    /// <summary>"Can the player walk between these two spots?" by spot id.</summary>
    internal readonly record struct Question(int From, int To);

    internal const int Player = 0, Target = 1;

    private readonly IReadOnlyList<Link> _links;
    private readonly Spot[] _spots;
    private readonly int[] _parent;
    private readonly List<(int a, int b)> _apart = new();
    private readonly IEnumerator<Question> _search;
    private readonly int _budget;
    private bool _answer;

    /// <summary>Starts a search. Without <paramref name="askDirect"/> the caller has already found no direct walk;
    /// with it, the target first needs moving onto the navmesh and the direct walk is asked again.</summary>
    internal LevelRoutePlanner(Spot player, Spot target, IReadOnlyList<Link> links, int budget, bool askDirect = false)
    {
        _links = links;
        _budget = budget;
        _spots = new Spot[2 + 2 * links.Count];
        _spots[Player] = player;
        _spots[Target] = target;
        for (int i = 0; i < links.Count; i++)
        {
            _spots[LowId(i)] = links[i].Low;
            _spots[HighId(i)] = links[i].High;
        }
        _parent = new int[_spots.Length];
        for (int i = 0; i < _parent.Length; i++) _parent[i] = i;
        if (!askDirect) _apart.Add((Player, Target));
        _search = Search();
    }

    internal IReadOnlyList<Leg>? Plan { get; private set; }
    /// <summary>The plan ends at the reached spot nearest the target's height, not at the target.</summary>
    internal bool Partial { get; private set; }
    internal bool Finished { get; private set; }
    internal int Asked { get; private set; }
    internal Question? Pending { get; private set; }
    internal int LinkCount => _links.Count;

    internal Spot SpotOf(int id) => _spots[id];
    internal static int LowId(int link) => 2 + 2 * link;
    internal static int HighId(int link) => 3 + 2 * link;

    /// <summary>The question to answer next, or null once the search is over.</summary>
    internal Question? Next()
    {
        if (Pending != null) return Pending;
        if (Finished) return null;
        // Resuming consumes the last answer first, so a plan it completes is kept even at the budget.
        if (!_search.MoveNext() || Asked >= _budget)
        {
            Finished = true;
            return null;
        }
        Pending = _search.Current;
        return Pending;
    }

    /// <summary>Answers the pending question; resolved spots replace endpoints that had to be moved onto the navmesh.</summary>
    internal void Answer(bool connected, Spot? from = null, Spot? to = null)
    {
        if (Pending is not { } q) return;
        Pending = null;
        Asked++;
        if (from is { } f && q.From != Player) _spots[q.From] = f;
        if (to is { } t && q.To != Player) _spots[q.To] = t;
        _answer = connected;
        if (connected) _parent[Find(q.From)] = Find(q.To);
        else _apart.Add((q.From, q.To));
    }

    /// <summary>1 connected, 0 not connected, -1 unknown, from answers so far.</summary>
    internal int Known(int a, int b)
    {
        int ra = Find(a), rb = Find(b);
        if (ra == rb) return 1;
        foreach (var (x, y) in _apart)
        {
            int rx = Find(x), ry = Find(y);
            if ((rx == ra && ry == rb) || (rx == rb && ry == ra)) return 0;
        }
        return -1;
    }

    /// <summary>Which leg the player is on: past a ladder standing at its far end, back again at its near end.</summary>
    internal static int Progress(IReadOnlyList<Leg> plan, int current, Spot player, float reachM, float levelM)
    {
        while (current < plan.Count - 1 && AtEnd(player, plan[current].Exit, plan[current].To, reachM, levelM)) current++;
        while (current > 0 && AtEnd(player, plan[current - 1].To, plan[current - 1].Exit, reachM, levelM)) current--;
        return current;
    }

    private static bool AtEnd(Spot p, Spot end, Spot other, float reachM, float levelM)
        => MathF.Abs(p.Y - end.Y) < MathF.Min(levelM, MathF.Abs(p.Y - other.Y)) && Flat(p, end) <= reachM;

    private IEnumerator<Question> Search()
    {
        int direct = Known(Player, Target);
        if (direct < 0) { yield return new Question(Player, Target); direct = _answer ? 1 : 0; }
        if (direct == 1)
        {
            Plan = new[] { Final() };
            yield break;
        }

        var reachTarget = new List<(int link, bool up)>();
        foreach (var (i, up) in Ordered(Target, Player, -1))
        {
            // Leave half the budget for two-ladder routes once some ladder is known to reach the target.
            if (reachTarget.Count > 0 && Asked >= _budget / 2) break;
            int exit = ExitId(i, up), entry = EntryId(i, up);
            int k = Known(exit, Target);
            if (k < 0) { yield return new Question(exit, Target); k = _answer ? 1 : 0; }
            if (k == 0) continue;
            reachTarget.Add((i, up));
            k = Known(Player, entry);
            if (k < 0) { yield return new Question(Player, entry); k = _answer ? 1 : 0; }
            if (k == 1)
            {
                Plan = new[] { LegOf(i, up), Final() };
                yield break;
            }
        }

        foreach (var (a, aUp) in reachTarget)
        {
            int aEntry = EntryId(a, aUp);
            foreach (var (b, bUp) in Ordered(aEntry, Player, a))
            {
                int exit = ExitId(b, bUp), entry = EntryId(b, bUp);
                int k = Known(exit, aEntry);
                if (k < 0) { yield return new Question(exit, aEntry); k = _answer ? 1 : 0; }
                if (k == 0) continue;
                k = Known(Player, entry);
                if (k < 0) { yield return new Question(Player, entry); k = _answer ? 1 : 0; }
                if (k == 1)
                {
                    Plan = new[] { LegOf(b, bUp), LegOf(a, aUp), Final() };
                    yield break;
                }
            }
        }

        foreach (var q in Bidirectional()) yield return q;
    }

    /// <summary>No pass found a route, so BOTH ends are searched at once. The player's front and
    /// the target's front each grow through the links, one connectivity question at a time,
    /// alternating sides. The moment one front stands in the other's connected component the two
    /// halves are one route and the plan is complete — a search that used to stop at the nearest
    /// partial climb now meets itself halfway. Until they meet (or the questions run out) the
    /// partial plan keeps updating exactly as the one-sided search did: the reached spot nearest
    /// the target's height, plus a blocked hop past an unmodelled obstacle.</summary>
    private IEnumerable<Question> Bidirectional()
    {
        var reachedF = new List<int> { Player };
        var viaF = new Dictionary<int, (int from, int link, bool up)>();
        var takenF = new HashSet<(int link, bool up)>();
        var reachedR = new List<int> { Target };
        var viaR = new Dictionary<int, (int from, int link, bool up)>();
        var takenR = new HashSet<(int link, bool up)>();
        int best = Player;
        bool askF = true;
        while (true)
        {
            // Take every link whose entry the answers so far connect to a reached spot, on both
            // fronts at once: no question is spent, only answers already in hand. The two
            // fronts share the union-find, so every answer either side earns, both sides keep.
            best = GrowFront(reachedF, viaF, takenF, best);
            GrowFront(reachedR, viaR, takenR, Player, trackBest: false);
            // A reached spot on each front sharing a connected component IS the route.
            if (TryMeet(reachedF, reachedR, out int meetF, out int meetR))
            {
                SetFull(meetF, viaF, meetR, viaR);
                yield break;
            }
            // No meeting yet: keep the partial plan honest while the search runs.
            var hop = BlockedHop(reachedF, takenF, best);
            if (hop is { } h) SetPartial(h.from, viaF, h);
            else if (best != Player) SetPartial(best, viaF);

            // Next question, the fronts alternating; whichever side asks, it asks about the
            // link that best continues its own world toward the other side's anchor. A side
            // with nothing left to ask gives way to the other; both empty ends the search.
            var next = (askF ? CheapestOn(reachedF, takenF, Target) : CheapestOn(reachedR, takenR, Player))
                       ?? (askF ? CheapestOn(reachedR, takenR, Player) : CheapestOn(reachedF, takenF, Target));
            if (next is not { } q) yield break;
            askF = !askF;
            yield return q;
        }
    }

    /// <summary>Takes every link direction whose entry the answers so far connect to a reached
    /// spot; each exit joins the front. <paramref name="best"/> tracks the reached spot nearest
    /// the target's height and only the PLAYER'S front tracks it — the target's front is not a
    /// place the player stands, so however high it climbs is no partial plan of theirs.</summary>
    private int GrowFront(List<int> reached, Dictionary<int, (int from, int link, bool up)> via,
                          HashSet<(int link, bool up)> taken, int best, bool trackBest = true)
    {
        for (bool grew = true; grew;)
        {
            grew = false;
            for (int i = 0; i < _links.Count; i++)
                foreach (bool up in Directions)
                {
                    int entry = EntryId(i, up), exit = ExitId(i, up);
                    int at = taken.Contains((i, up)) ? -1 : reached.FindIndex(r => Known(r, entry) == 1);
                    if (at < 0) continue;
                    taken.Add((i, up));
                    if (reached.Exists(r => Known(r, exit) == 1)) continue;
                    reached.Add(exit);
                    via[exit] = (reached[at], i, up);
                    grew = true;
                    if (trackBest && Nearer(exit, best)) best = exit;
                }
        }
        return best;
    }

    /// <summary>A reached spot on each front that shares a connected component: the two worlds
    /// touch. Union-find roots make the touch transitive — two DIFFERENT spots on the same
    /// navmesh region meet just as well as the same spot reached twice.</summary>
    private bool TryMeet(List<int> front, List<int> back, out int meetF, out int meetR)
    {
        var roots = new Dictionary<int, int>();
        foreach (int b in back)
            roots.TryAdd(Find(b), b);
        foreach (int f in front)
            if (roots.TryGetValue(Find(f), out int b)) { meetF = f; meetR = b; return true; }
        meetF = meetR = -1;
        return false;
    }

    /// <summary>Splices the two fronts into one complete plan: the player's ladders out to the
    /// meeting, then the target's ladders replayed backwards — each taken from its other end,
    /// because what the target's front climbed up, the player climbs down. The walk between the
    /// two meeting spots needs no leg of its own: the beacon guides across a connected region.</summary>
    private void SetFull(int meetF, Dictionary<int, (int from, int link, bool up)> viaF,
                         int meetR, Dictionary<int, (int from, int link, bool up)> viaR)
    {
        var legs = new List<Leg>();
        for (int at = meetF; at != Player && viaF.TryGetValue(at, out var step); at = step.from)
            legs.Insert(0, LegOf(step.link, step.up));
        for (int at = meetR; viaR.TryGetValue(at, out var step); at = step.from)
            legs.Add(LegOf(step.link, !step.up));
        legs.Add(Final());
        Plan = legs;
        Partial = false;
    }

    /// <summary>The cheapest unanswered question on one front: a link entry its reached spots
    /// might connect to, ordered by the straight line it would add toward the OTHER side's
    /// anchor — the player's front aims at the target, the target's front aims at the player.</summary>
    private Question? CheapestOn(List<int> reached, HashSet<(int link, bool up)> taken, int anchor)
    {
        Question? next = null;
        float cost = float.MaxValue;
        foreach (int r in reached)
            for (int i = 0; i < _links.Count; i++)
                foreach (bool up in Directions)
                {
                    int entry = EntryId(i, up);
                    if (taken.Contains((i, up)) || Known(r, entry) >= 0) continue;
                    float c = Distance(_spots[r], _spots[entry]) + Distance(_spots[ExitId(i, up)], _spots[anchor]);
                    if (c < cost) { cost = c; next = new Question(r, entry); }
                }
        return next;
    }

    /// <summary>Within the target's neighbourhood: a level nearer the target's height, or the same level horizontally
    /// nearer; never the player's own level. Ladders farther out may still be crossed on the way.</summary>
    private bool Nearer(int spot, int best)
    {
        if (Flat(_spots[spot], _spots[Target]) > NeighbourhoodM) return false;
        float gap = Gap(spot), bestGap = Gap(best);
        if (best == Player) return gap <= bestGap - LevelM;
        if (gap < bestGap - LevelM) return true;
        return MathF.Abs(gap - bestGap) < LevelM && Flat(_spots[spot], _spots[Target]) < Flat(_spots[best], _spots[Target]);
    }

    /// <summary>One ladder past an unmodelled obstacle: its foot level with and near a reached spot the answers say
    /// does not connect, and its top at least a level nearer the target than the best connected end.</summary>
    private (int from, int link, bool up)? BlockedHop(List<int> reached, HashSet<(int link, bool up)> taken, int best)
    {
        (int from, int link, bool up)? hop = null;
        int hopExit = best;
        foreach (int r in reached)
            for (int i = 0; i < _links.Count; i++)
                foreach (bool up in Directions)
                {
                    int entry = EntryId(i, up), exit = ExitId(i, up);
                    if (taken.Contains((i, up)) || Known(r, entry) != 0) continue;
                    if (MathF.Abs(_spots[r].Y - _spots[entry].Y) >= LevelM || Flat(_spots[r], _spots[entry]) > BlockedHopM) continue;
                    if (Flat(_spots[exit], _spots[Target]) > NeighbourhoodM || Gap(exit) > Gap(hopExit) - LevelM) continue;
                    hop = (r, i, up);
                    hopExit = exit;
                }
        return hop;
    }

    /// <summary>The ladders from the player to <paramref name="end"/>, then a blocked ladder if any, then a last leg
    /// that stops at the top of the last ladder.</summary>
    private void SetPartial(int end, Dictionary<int, (int from, int link, bool up)> via, (int from, int link, bool up)? blocked = null)
    {
        var legs = new List<Leg>();
        int stop = end;
        if (blocked is { } b)
        {
            var leg = LegOf(b.link, b.up);
            legs.Add(leg with { Blocked = true });
            stop = ExitId(b.link, b.up);
        }
        for (int at = end; at != Player && via.TryGetValue(at, out var step); at = step.from)
            legs.Insert(0, LegOf(step.link, step.up));
        legs.Add(new Leg(_spots[stop], _spots[stop], -1, false));
        Plan = legs;
        Partial = true;
    }

    private float Gap(int id) => MathF.Abs(_spots[id].Y - _spots[Target].Y);

    /// <summary>Both directions of every ladder, cheapest straight-line detour from start to goal first.</summary>
    private List<(int link, bool up)> Ordered(int goal, int start, int skip)
    {
        var order = new List<(int link, bool up, float cost)>();
        for (int i = 0; i < _links.Count; i++)
        {
            if (i == skip) continue;
            foreach (bool up in Directions)
            {
                Spot entry = _spots[EntryId(i, up)], exit = _spots[ExitId(i, up)];
                order.Add((i, up, Distance(_spots[start], entry) + Distance(entry, exit) + Distance(exit, _spots[goal])));
            }
        }
        order.Sort((x, y) => x.cost.CompareTo(y.cost));
        return order.ConvertAll(o => (o.link, o.up));
    }

    private Leg LegOf(int link, bool up) =>
        new(_spots[EntryId(link, up)], _spots[ExitId(link, up)], link, up, false,
            _links[link].Jump, _links[link].Stair, _links[link].Lift, _links[link].Drop);
    private Leg Final() => new(_spots[Target], _spots[Target], -1, false);
    private static int EntryId(int link, bool up) => up ? LowId(link) : HighId(link);
    private static int ExitId(int link, bool up) => up ? HighId(link) : LowId(link);

    private int Find(int i)
    {
        while (_parent[i] != i) i = _parent[i] = _parent[_parent[i]];
        return i;
    }

    internal static float Distance(Spot a, Spot b)
        => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    internal static float Flat(Spot a, Spot b)
        => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
