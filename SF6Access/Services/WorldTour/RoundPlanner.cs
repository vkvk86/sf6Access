using System;
using System.Collections.Generic;
using System.Globalization;
using REFrameworkNET;
using SF6Access.Hooks.WorldTour;
using static SF6Access.Services.WorldTour.RoundStep;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>A route ROUND whatever stands between, measured out of the WORLD rather than the navmesh.</b>
///
/// <para><b>Why this exists, in one line of log.</b> 2026-10-03, 13:51, the SiRN building site: the
/// objective three metres away behind hoarding, the engine answering
/// <c>PathNotFound (startNode=23110, destNode=22753)</c>, the plan later answering
/// <c>plan after 1 questions: objective</c> about the same objective, and the player pushing the
/// same wall for two minutes. The two maps disagree here, and "follow the wall round" — the honest
/// sentence <see cref="WayRound"/> was written to add — still leaves a blind player guessing where
/// the wall ENDS. Guessing is not navigation.</para>
///
/// <para><b>What it does instead.</b> A best-first search whose nodes are points the player can
/// stand on and whose edges are straight hops a ray has MEASURED clear. Nothing here asks the
/// engine anything: no <c>queryPathSync</c>, so nothing here can collide with the single-query
/// rule that cost a crash, and nothing here can inherit the navmesh's disagreement with itself.
/// A node enters the tree only after two measurements agree — the ray proves the space between is
/// open, and a floor probe proves there is ground to stand on at the far end.</para>
///
/// <para><b>The searched space is CENTERED ON THE OBJECTIVE'S BEARING.</b> Every node fans seven
/// directions around the line from ITSELF to the objective, straight ahead first and 90° to each
/// side last. That keeps the search from mapping the street behind the player, which is what a
/// grid would do and why a grid is not affordable here.</para>
///
/// <para><b>Paid for a little at a time.</b> Eight casts a tick, nine hundred for a whole search —
/// about the rate the navigating radar already casts at, spread over a second and a half, during
/// which the beacon goes on saying exactly what it said before. The route replaces nothing; it is
/// only offered once the walk has otherwise been declared over.</para>
///
/// <para><b>A published route is kept while the player walks it.</b> Every hop of it was measured
/// from the standing point it was published for, so throwing it away mid-walk would strand the
/// player between two halves of one instruction — the same mistake <see cref="DropEdgeFinder"/>
/// was fixed for. It is re-searched when they leave it, when the objective moves, or when it runs
/// out.</para>
/// </summary>
internal static class RoundPlanner
{
    /// <summary>The twelve bearings every node tries, relative to the line to the objective.
    /// Positive is to the RIGHT of it — the handedness this mod confirmed in game. Straight on
    /// first: the hop that costs the least walking is the one worth taking.
    ///
    /// <para><b>The back half is not a mistake and must not be trimmed.</b> It was ±90° once, and
    /// the first in-game run (2026-10-03, 14:42) died with ONE node expanded and fifteen casts:
    /// an objective ringed by hoarding can only be entered by first walking AWAY from it, and a
    /// fan that may never point backwards cannot discover an entrance that lies behind the
    /// walker. Twelve bearings cost about twenty-four casts a node, which the budget below
    /// pays for; two revolutions round a shed cost less than one honest "I could not find it".
    /// </para></summary>
    private static readonly float[] Fan = { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f, 150f, -150f, 180f };

    /// <summary>How far away an objective may be for this to have anything to say. Beyond a
    /// street's length, what stands between is the city and no local ray sees round it.</summary>
    internal const float RangeM = 26f;

    /// <summary>How near one waypoint has to be for it to count as walked. Twice this is still
    /// less than one hop, so reaching one never skips the next.</summary>
    internal const float ArriveM = 1f;

    /// <summary>How much nearer than the objective a node's own node is allowed to get to it
    /// before the route is called finished, in metres. Whatever is left is the player's own last
    /// step and their own hands.</summary>
    internal const float GoalGapM = 0.4f;

    /// <summary>How far off this floor the ground at the far end of a hop may be and still be
    /// the same floor — a kerb, a ramp, one step either way. <see cref="JumpLink.StepM"/>: the
    /// height already agreed is climbable.</summary>
    private static float FloorTolM => JumpLink.StepM;

    /// <summary>The same floor tolerance, for the try-step search in <see cref="WalkSteps"/>: one
    /// rule for "this is the floor you are standing on", shared rather than restated.</summary>
    internal static float SameFloorM => FloorTolM;

    /// <summary>Where the floor probe starts, above the floor it is checking from: high enough to
    /// clear anything the walker would step over, low enough that a floor two metres down is still
    /// within reach of the cast.</summary>
    private const float FloorAboveM = 0.8f;

    /// <summary>How deep that probe looks. Deeper than this below the floor being walked is not
    /// another place to stand, it is another storey.</summary>
    private const float FloorDropM = 3.2f;

    /// <summary>The height above the floor a hop's ray is cast at. Chest height on an adult: high
    /// enough to clear every kerb and skip, low enough that a fence, a railing or a hoarding is
    /// still something it runs into — and this whole feature exists for hoardings.</summary>
    private const float HoverM = 0.9f;

    /// <summary>Two nodes closer together than this are the same place twice: without this the
    /// tree grows sideways for ever along one open street.</summary>
    private const float DedupeM = 0.8f;

    /// <summary>The whole search has to finish inside about one second of walking, because a
    /// search whose root has been left behind is thrown away (see <see cref="Retarget"/>) — at
    /// 5 m/s the walker leaves its own root in well under a second. Twelve casts a frame is about
    /// what the radar spends; fourteen hundred pays for fifty-plus nodes, which is two laps of a
    /// building site.</summary>
    private const int MaxCasts = 1400;
    private const int CastsPerTick = 24;

    /// <summary>How far the player may walk before the search belongs to another spot. Also how
    /// much their leaving the ROUTE counts as having left it.</summary>
    private const float MoveM = 3f;

    private const long GiveUpMs = 9000;   // a search this old was made somewhere else entirely
    private const long RetryMs = 5000;    // and having found nothing once is not a reason to spin

    // --- the tree: one list per field, so nothing is allocated per node ---
    private static readonly List<float> Nx = new(), Nz = new(), Ny = new(), Ng = new();
    private static readonly List<int> Parent = new(), FanIdx = new();
    private static readonly List<bool> GoalTried = new();
    private static readonly List<int> Open = new();

    // --- what it found ---
    private static readonly List<(float x, float y, float z)> Path = new();

    private static bool _have, _running, _found, _reachesGoal;
    private static int _casts, _endNode;
    private static long _startedAt, _retryAt;

    // Why children were refused, counted rather than guessed at. The first live run proved this
    // was needed: the search stopped after one node and NOTHING in it said which of six refusals
    // had fired. A tester who cannot see the screen can only ever read these out of the log.
    private static int _noRay, _noLen, _noFloor, _hole, _offLevel, _dup, _accepted;
    private static float _ox, _oy, _oz, _tx, _ty, _tz;

    /// <summary>Nothing suspect about this being in progress: nothing before it changes.</summary>
    internal static bool Searching => _running;

    /// <summary>A route to offer, and how MANY hops are left of it — the answer to "is there a way
    /// round", which until now has had to be said without knowing.</summary>
    internal static bool HasHop => _found && Path.Count > 0;

    /// <summary>How many hops the route still has, including the one being walked.</summary>
    internal static int Hops => Path.Count;

    /// <summary>The hop to walk now.</summary>
    internal static float X => Path.Count > 0 ? Path[0].x : 0f;
    internal static float Y => Path.Count > 0 ? Path[0].y : 0f;
    internal static float Z => Path.Count > 0 ? Path[0].z : 0f;

    /// <summary>Whether the route's last hop lands within reach of the objective itself.</summary>
    internal static bool ReachesGoal => _reachesGoal;

    /// <summary><b>Spend this tick's casts on the search.</b> Returns true once a route exists, and
    /// does nothing at all while one already does — including not re-measuring the millionth time
    /// someone stands still where a way round has already been published for them.</summary>
    internal static void Probe(float px, float py, float pz, float tx, float ty, float tz)
    {
        Retarget(px, py, pz, tx, ty, tz);
        if (_found || !_running) return;

        long now = Environment.TickCount64;
        if (now - _startedAt > GiveUpMs) { Stop(now, "ran out of time"); return; }

        int budget = CastsPerTick;
        while (budget > 0 && _casts < MaxCasts)
        {
            int node = Pick(tx, tz);
            if (node < 0) { Stop(now, $"nothing left within {MaxCasts} casts"); break; }

            if (!GoalTried[node]) { budget--; _casts++; TryGoal(node, tx, tz); }
            else if (FanIdx[node] < Fan.Length) { budget -= 2; _casts += 2; TryChild(node, FanIdx[node]++, tx, tz); }
            else Open.Remove(node);

            if (_found) break;
        }
        if (_found) Build(tx, ty, tz);
    }

    /// <summary>The least-costing node still worth looking at: what it has cost to reach plus what
    /// is left to walk. A*'s ordering and no more — with a few hundred nodes a sorted list would be
    /// slower than looking.</summary>
    private static int Pick(float tx, float tz)
    {
        int best = -1;
        float bestF = float.MaxValue;
        for (int i = 0; i < Open.Count; i++)
        {
            int n = Open[i];
            float dx = tx - Nx[n], dz = tz - Nz[n];
            float f = Ng[n] + MathF.Sqrt(dx * dx + dz * dz);
            if (f < bestF) { bestF = f; best = n; }
        }
        return best;
    }

    /// <summary>Can the objective be reached STRAIGHT from this node? One cast along the line to
    /// it, and the whole route is finished if that line is open.</summary>
    private static void TryGoal(int node, float tx, float tz)
    {
        GoalTried[node] = true;
        float dx = tx - Nx[node], dz = tz - Nz[node];
        float to = MathF.Sqrt(dx * dx + dz * dz);
        if (to <= GoalGapM || !(to > 0.01f)) { Win(node, true); return; }

        var dir = new FieldDirectionService.FlatDir(dx / to, dz / to, true);
        if (!FieldRayCaster.TryCastAcross(Nx[node], Ny[node] + HoverM, Nz[node], dir, to, out var hit)) return;
        if (RoundStep.Reaches(hit.Hit, hit.Distance, to, GoalGapM)) Win(node, true);
    }

    /// <summary>One direction of this node's fan: ray it, floor-probe where it got to, and add what
    /// survives both. Two casts, always together, because a hop missing either measurement is a hop
    /// nobody may walk.</summary>
    private static void TryChild(int node, float degrees, float tx, float tz)
    {
        FanToGoal(node, degrees, tx, tz, out float dx, out float dz);
        var dir = new FieldDirectionService.FlatDir(dx, dz, true);
        if (!dir.Ok) return;

        if (!FieldRayCaster.TryCastAcross(Nx[node], Ny[node] + HoverM, Nz[node], dir, ReachM, out var hit))
        { _noRay++; return; }

        float len = RoundStep.LengthM(hit.Hit, hit.Distance, ReachM, HopM, ClearM, MinHopM);
        if (!(len > 0f))
        {
            // Last resort, and the reason it exists is a body pressed into a corner: there IS room
            // to move there, just less than a comfortable stride needs. Refusing it outright is
            // what turns "no comfortable route" into "no route at all", which is a lie. The hop is
            // still backed by a floor probe below, so this can never step into open air.
            len = RoundStep.LengthM(hit.Hit, hit.Distance, ReachM, HopM, SqueezeClearM, SqueezeMinM);
            if (!(len > 0f)) { _noLen++; return; }
        }

        float cx = Nx[node] + dir.X * len, cz = Nz[node] + dir.Z * len;
        if (!FieldRayCaster.TryFloorBelow(cx, Ny[node] + FloorAboveM, cz, FloorDropM, out float floorY))
        { _noFloor++; return; }
        // NaN is a HOLE: the probe ran and found no ground at all, which is a place to fall into,
        // never a place to walk. Anything further from this floor than one step is another storey.
        if (!float.IsFinite(floorY)) { _hole++; return; }
        if (MathF.Abs(floorY - Ny[node]) > FloorTolM) { _offLevel++; return; }
        if (Duplicate(cx, cz)) { _dup++; return; }
        _accepted++;

        Nx.Add(cx); Nz.Add(cz); Ny.Add(floorY); Ng.Add(Ng[node] + len);
        Parent.Add(node); FanIdx.Add(0); GoalTried.Add(false);
        Open.Add(Nx.Count - 1);
    }

    /// <summary>The bearing <paramref name="degrees"/> to the right of the line from this node to
    /// the objective. Everything the search tries points towards the objective — that is the whole
    /// reason it does not spend its casts mapping where the player has already been.</summary>
    private static void FanToGoal(int node, float degrees, float tx, float tz, out float dx, out float dz)
    {
        float bx = tx - Nx[node], bz = tz - Nz[node];
        float len = MathF.Sqrt(bx * bx + bz * bz);
        dx = bz; dz = bx;
        if (!(len > 0.01f)) { dx = 1f; dz = 0f; return; }
        float ux = bx / len, uz = bz / len;
        double rad = degrees * Math.PI / 180.0;
        float c = (float)Math.Cos(rad), s = (float)Math.Sin(rad);
        // rightward = forward x up, the same rule WayRound was built on and confirmed in game.
        float rx = -uz, rz = ux;
        dx = ux * c + rx * s;
        dz = uz * c + rz * s;
        float n = MathF.Sqrt(dx * dx + dz * dz);
        if (n > 0f) { dx /= n; dz /= n; }
    }

    private static bool Duplicate(float x, float z)
    {
        for (int i = 0; i < Nx.Count; i++)
        {
            float dx = Nx[i] - x, dz = Nz[i] - z;
            if (dx * dx + dz * dz <= DedupeM * DedupeM) return true;
        }
        return false;
    }

    private static void Win(int node, bool reachesGoal)
    {
        _found = true;
        _running = false;
        _reachesGoal = reachesGoal;
        _endNode = node;
    }

    /// <summary>Unwind the tree into the hops the player walks: node one is the first thing they
    /// reach from where they are standing, and node zero IS where they are standing.</summary>
    private static void Build(float tx, float ty, float tz)
    {
        Path.Clear();
        int n = _endNode;
        while (n >= 0 && n < Nx.Count)
        {
            Path.Add((Nx[n], Ny[n], Nz[n]));
            if (n == 0) break;
            n = Parent[n];
        }
        Path.Reverse();
        if (Path.Count > 0) Path.RemoveAt(0);
        if (_reachesGoal) Path.Add((tx, ty, tz));
        _running = false;
        API.LogInfo(string.Format(CultureInfo.InvariantCulture,
            "[SF6Access] Round: route found in {0} casts - {1} hops, {2:F1} m of it, {3}",
            _casts, Path.Count, Path.Count > 0 ? Ng[_endNode] : 0f,
            _reachesGoal ? "the last hop reaches the objective"
                         : "the last hop is a stand nearer than anywhere else measured"));
    }

    private static void Stop(long now, string why)
    {
        _running = false;
        _retryAt = now + RetryMs;
        API.LogInfo(string.Format(CultureInfo.InvariantCulture,
            "[SF6Access] Round: no way round measured ({0}); {1} nodes looked at, {2} casts, " +
            "refused - no cast {3}, no room {4}, no floor reading {5}, hole {6}, other floor {7}, " +
            "already known {8}; accepted {9}",
            why, Nx.Count, _casts, _noRay, _noLen, _noFloor, _hole, _offLevel, _dup, _accepted));
    }

    /// <summary>Start over when the standing point is no longer the one this search was made for —
    /// and NOT otherwise, because a route already published must survive the walk it asked for.</summary>
    private static void Retarget(float px, float py, float pz, float tx, float ty, float tz)
    {
        // A published route is never re-planned from here. Its hops were measured from where the
        // walker was standing when it was published, and a walker walking them is SUPPOSED to
        // leave that spot: throwing the route away mid-walk strands them between two halves of one
        // instruction. <see cref="Abandoned"/> and <see cref="Arrived"/> retire it, not this.
        //
        // This is what the first live run got wrong: every hop of every route died the moment the
        // player walked three metres, which at their jogging speed is half a second after the
        // route appeared. It never reached its second hop, and no search ever got past one node.
        if (_found) return;

        bool newObjective = Dist(tx, ty, tz, _tx, _ty, _tz) > MissionBeaconHooks.ARRIVED_M;
        bool newFloor = MathF.Abs(py - _oy) >= LevelRoutePlanner.LevelM;
        if (!_have || newObjective || newFloor) { Start(px, py, pz, tx, ty, tz); return; }

        float moved = Dist(px, py, pz, _ox, _oy, _oz);
        if (_running)
        {
            // Running searches survive small movements and are re-rooted on big ones: every hop is
            // measured from the root, so once the walker has left the root behind the tree is
            // describing somewhere they no longer are. It is also why MaxCasts has to be
            // affordable inside about a second — see its own note.
            if (moved > MoveM) Start(px, py, pz, tx, ty, tz);
            return;
        }
        // Finished and empty-handed. Trying again from the same spot would spin a search that has
        // already answered, so this waits for a real move — or for the player to pick the
        // objective again, which goes through Reset.
        if (moved > MoveM && Environment.TickCount64 >= _retryAt) Start(px, py, pz, tx, ty, tz);
    }

    private static void Start(float px, float py, float pz, float tx, float ty, float tz)
    {
        _have = true;
        _found = false;
        _reachesGoal = false;
        _running = true;
        _casts = 0;
        _endNode = 0;
        _noRay = _noLen = _noFloor = _hole = _offLevel = _dup = _accepted = 0;
        _startedAt = Environment.TickCount64;
        _retryAt = 0;
        _ox = px; _oy = py; _oz = pz;
        _tx = tx; _ty = ty; _tz = tz;

        Nx.Clear(); Nz.Clear(); Ny.Clear(); Ng.Clear();
        Parent.Clear(); FanIdx.Clear(); GoalTried.Clear();
        Open.Clear();
        Path.Clear();

        Nx.Add(px); Nz.Add(pz); Ny.Add(py); Ng.Add(0f);
        Parent.Add(-1); FanIdx.Add(0); GoalTried.Add(false);
        Open.Add(0);
    }

    /// <summary>The player has walked this hop: drop it, and re-search once the route is used up
    /// rather than standing there pointing at a place the player has already reached.</summary>
    internal static void Arrived(float x, float y, float z)
    {
        if (Path.Count > 0) Path.RemoveAt(0);
        if (Path.Count == 0)
        {
            _found = false;
            _retryAt = 0;
            _have = false;   // the next Probe starts fresh from wherever they are standing now
        }
    }

    /// <summary>The player has wandered off the route it was given: nothing in it is where they
    /// are any more, and every hop of it was measured from somewhere else.</summary>
    internal static void Abandoned(float px, float pz)
    {
        if (!_found) return;
        float worst = float.MaxValue;
        float prevX = _ox, prevZ = _oz;
        foreach (var (x, _, z) in Path)
        {
            worst = MathF.Min(worst, PointToSegment(px, pz, prevX, prevZ, x, z));
            prevX = x; prevZ = z;
        }
        if (worst > MoveM) { _found = false; _have = false; _retryAt = 0; Path.Clear(); }
    }

    /// <summary>Distance from a point to a segment, the plain way: the one measurement that says
    /// whether someone is still ON a route rather than merely somewhere along it.</summary>
    private static float PointToSegment(float px, float pz, float ax, float az, float bx, float bz)
    {
        float vx = bx - ax, vz = bz - az;
        float wx = px - ax, wz = pz - az;
        float len2 = vx * vx + vz * vz;
        if (!(len2 > 0f)) return MathF.Sqrt(wx * wx + wz * wz);
        float t = MathF.Max(0f, MathF.Min(1f, (wx * vx + wz * vz) / len2));
        float dx = px - (ax + vx * t), dz = pz - (az + vz * t);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Forget everything: another objective, another field, or the same one after a
    /// reload. Nothing here survives leaving the place it was measured in.</summary>
    internal static void Reset()
    {
        _have = _running = _found = _reachesGoal = false;
        _casts = 0;
        _endNode = 0;
        _startedAt = _retryAt = 0;
        Nx.Clear(); Nz.Clear(); Ny.Clear(); Ng.Clear();
        Parent.Clear(); FanIdx.Clear(); GoalTried.Clear();
        Open.Clear();
        Path.Clear();
    }

    private static float Dist(float ax, float ay, float az, float bx, float by, float bz)
    {
        float dx = ax - bx, dy = ay - by, dz = az - bz;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
