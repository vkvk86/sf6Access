using System;
using System.Collections.Generic;
using System.Globalization;
using REFrameworkNET;
using SF6Access.Hooks.WorldTour;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>The relay leg: once the walk has run out of route, look for the next place that has one.</b>
///
/// <para><b>The hole this fills.</b> The old guide had exactly two answers to a destination the
/// navmesh cannot walk to: find a ladder (<see cref="LevelRoutePlanner"/>), or — having reached
/// the nearest walkable stand-in — point the sound straight at the objective from there. That
/// last answer aims through whatever stands between, and walking into it is the report the owner
/// filed on 2026-10-02 ("提示我声音直接指向目的地，所以前面直接是高墙，过不去的"). Metronome
/// City's building site is the case: the objective sits two metres away on a piece of navmesh no
/// link reaches, and every line the guide said from there was a distance and a clock bearing
/// toward a wall.</para>
///
/// <para><b>What this asks instead.</b> Not "is there a route to the objective" — the engine has
/// already answered that, definitively, and asking again costs a half-second of blocking A* to be
/// told no. It asks "is there some OTHER standing place I can walk to, from which the objective
/// is nearer?" Same walkable world, different question. A hop that lands the player at the top of
/// the ramp, round the corner of the hoarding, or on the lower floor beside the pit does not
/// solve the last metre, and it never claims to: it turns "there is no way" into "you are where
/// the decision is yours", which is a sentence a player can act on.</para>
///
/// <para><b>One probe per call, never two in one frame.</b> The rule
/// <see cref="ApproachFinder"/> pays for and documents: a second synchronous
/// <c>queryPathSync</c> into the same report killed the game once already. Each candidate here is
/// an ordinary <see cref="FieldNavPath.TryReach"/> with its own fresh report, one per tick, so a
/// search costing twenty probes spreads over five seconds of walking.</para>
///
/// <para><b>What earns a hop is the ROUTE, not the step.</b> This used to demand that every single
/// hop land nearer the objective than the stand you are on, and that one line is why a detour
/// could never be planned: the way into a U-shaped hoarding, a walled compound or a fenced yard
/// STARTS by walking away from the thing you are going to. Every bearing that did not immediately
/// shorten the distance was refused before it was ever measured, so the search honestly reported
/// "nothing gets me nearer" about a route that was one leg long.</para>
///
/// <para>What replaces it is a budget on the DETOUR, not on the step: a hop is allowed when
/// walking via it costs no more than walking straight there would have, plus
/// <see cref="TotalDetourM"/>. Going via a candidate is measured as
/// <c>(here to candidate) + (candidate to objective)</c> against the straight line the search
/// started with. A hop sideways along a wall is nearly free by that measure — it barely lengthens
/// the route — while a hop that genuinely runs away from the objective buys nothing and spends
/// the budget. So detours are found, and going in circles is not: circling lengthens the route
/// every leg and runs the budget out.</para>
///
/// <para>Three-dimensional, because the gain that matters is often HEIGHT: getting down onto the
/// floor the objective is on counts even when the flat distance grows.</para>
///
/// <para><b>Five hops, then it says it stopped.</b> Legitimate destinations exist that no amount
/// of hoping reaches, and a guide that keeps proposing detours forever is one that has quietly
/// stopped being useful. Five rather than three because a way round is longer than a way to: at
/// <see cref="HopM"/> a leg, three hops is twenty-four metres, which is a corner and not a detour.
/// The budget, not the leg count, is what actually stops it.</para>
/// </summary>
internal static class RelayPlanner
{
    /// <summary>How many relay hops may be spent before the guide admits the walk is over.</summary>
    internal const int MaxLegs = 5;

    /// <summary>How far from the ANCHOR each hop is proposed: twice the arrival radius, the same
    /// step the beacon already calls "one move".</summary>
    private static float HopM => 2f * MissionBeaconHooks.ARRIVED_M;

    /// <summary>A ring round the OBJECTIVE, and a wider one for a footprint the first lands
    /// inside. An objective behind a hoarding is often reachable from its far side only.</summary>
    private static float RingAM => 2f * MissionBeaconHooks.ARRIVED_M;
    private static float RingBM => 4f * MissionBeaconHooks.ARRIVED_M;

    /// <summary><b>How much longer than the straight line the whole relay may be.</b> This is the
    /// budget that lets a detour be planned and stops a circle being walked: every leg spends it
    /// by however much going that way lengthened the route, and a leg that shortens nothing
    /// spends it fastest. Ten arrival radii — forty metres, about five hops.</summary>
    private static float TotalDetourM => 10f * MissionBeaconHooks.ARRIVED_M;

    /// <summary>The objective may drift this far before the search restarts: the beacon's own
    /// definition of "still the same place".</summary>
    private static float MovedM => MissionBeaconHooks.ARRIVED_M;

    private readonly record struct Candidate(float X, float Y, float Z, float D);

    private static bool _have;
    private static float _tx, _ty, _tz, _py;
    // Where this search is looking FROM: the stand the player has reached.
    private static float _ax, _ay, _az;
    // The detour budget is measured against the straight line from where the walk BEGAN, so it
    // has to remember that line: a budget re-measured from each new stand would refill itself
    // every hop and never stop anything.
    private static float _startDist;
    private static float _walkedM;
    private static List<Candidate> _candidates = new();
    private static int _probe;
    private static bool _found, _built;
    private static float _bestX, _bestY, _bestZ;
    private static bool _loggedHop, _loggedLost;
    private static int _legs;
    // Hops already walked: proposing one twice is how a guide becomes a carousel.
    private static readonly List<(float X, float Y, float Z)> _spent = new();

    /// <summary>A hop has been found and the beacon should guide to it instead of stopping.</summary>
    public static bool HasHop => _found;

    /// <summary>A search is running and has nothing yet. The caller must not point anywhere
    /// meanwhile: pointing at the objective is the bug this ganze file exists to remove.</summary>
    public static bool Searching => _have && !_found && !Exhausted_;

    /// <summary>Every candidate has been asked and none of them bought anything.</summary>
    public static bool Exhausted => Exhausted_;

    private static bool Exhausted_ => _have && !_found && _built && _probe >= _candidates.Count;

    /// <summary>How many relay hops have been spent on this objective.</summary>
    public static int Legs => _legs;

    public static float X => _bestX;
    public static float Y => _bestY;
    public static float Z => _bestZ;

    /// <summary>The relay hop to guide to, when one has been earned.</summary>
    public static bool TryGet(float tx, float ty, float tz, float py,
                              out float x, out float y, out float z)
    {
        Retarget(tx, ty, tz, py);
        x = _bestX; y = _bestY; z = _bestZ;
        return _found;
    }

    /// <summary><b>Spend this tick's one query on the next candidate.</b> Returns true when a hop
    /// was found and the beacon has somewhere new to go.</summary>
    ///
    /// <para><paramref name="anchorX"/> and friends are where the player currently STANDS, not the
    /// objective: the search needs to know what it must improve on, and the stand-in may have been
    /// proposed from somewhere else entirely.</para>
    public static bool Probe(float tx, float ty, float tz, float ax, float ay, float az)
    {
        Retarget(tx, ty, tz, ay);
        _ax = ax; _ay = ay; _az = az;
        if (_found) return true;
        if (!_built)
        {
            _candidates = Candidates(tx, ty, tz, ax, ay, az);
            _probe = 0;
            _built = true;
            // Set once, on the first build only. Arrived() clears _built to search again from the
            // new stand, and re-measuring here would hand the budget back every hop.
            if (_startDist <= 0f) _startDist = StandDistance();
        }
        if (_probe >= _candidates.Count)
        {
            if (!_loggedLost)
            {
                _loggedLost = true;
                API.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "[SF6Access] Relay: no stand inside the {0:F0} m detour budget after {1} probes at " +
                    "({2:F1}, {3:F1}, {4:F1}); {5:F1} m of it spent - the walk ends here",
                    TotalDetourM, _candidates.Count, ax, ay, az, _walkedM));
            }
            return false;
        }

        var c = _candidates[_probe];
        _probe++;

        // Asked before the query, so a candidate that would overspend the budget costs no navmesh
        // time at all: this is the common case for an objective walled off at arm's length.
        if (!Affordable(c.X, c.Y, c.Z)) return false;

        var answer = FieldNavPath.TryReach(c.X, c.Y, c.Z);
        // Throttled means the query never ran; nothing about a candidate can be read from that.
        if (answer.Throttled || (answer.Ok && answer.StartOffMesh)) { _probe--; return false; }
        if (!answer.Ok || !answer.Exists) return false;
        if (Spent(c)) return false;

        _found = true;
        _bestX = c.X; _bestY = c.Y; _bestZ = c.Z;
        if (!_loggedHop)
        {
            _loggedHop = true;
            float detour = DetourOf(c.X, c.Y, c.Z);
            // Saying whether the leg goes ROUND rather than nearer is the whole point of this
            // change, and the log is the only place a blind owner can read it.
            API.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[SF6Access] Relay: hop {0} to ({1:F1}, {2:F1}, {3:F1}), {4:F1} m from the objective " +
                "against {5:F1} m here, detour {6:F1} m of the {7:F0} m budget{8} - guiding on",
                _legs + 1, c.X, c.Y, c.Z, c.D, StandDistance(), detour, TotalDetourM,
                detour > 0.5f ? " (this leg goes round, not nearer)" : ""));
        }
        return true;
    }

    /// <summary>The player has reached the hop: it becomes the new anchor and the search starts
    /// again from there, so a second relay can follow the first.</summary>
    public static void Arrived(float x, float y, float z)
    {
        if (!_found) return;
        _spent.Add((_bestX, _bestY, _bestZ));
        if (_spent.Count > MaxLegs) _spent.RemoveAt(0);
        // What the leg actually cost, before the anchor moves: this is what spends the budget.
        _walkedM += Dist(_ax, _ay, _az, _bestX, _bestY, _bestZ);
        _ax = x; _ay = y; _az = z;
        _legs++;
        _found = false; _built = false; _probe = 0;
        _loggedHop = _loggedLost = false;
        API.LogInfo(string.Format(CultureInfo.InvariantCulture,
            "[SF6Access] Relay: hop {0} reached at ({1:F1}, {2:F1}, {3:F1}) - searching for the next one",
            _legs, x, y, z));
    }

    /// <summary>Forget everything: a new objective, or another field.</summary>
    public static void Reset()
    {
        _have = _found = _built = _loggedHop = _loggedLost = false;
        _probe = 0; _legs = 0;
        _startDist = 0f; _walkedM = 0f;
        _candidates = new List<Candidate>();
        _spent.Clear();
    }

    /// <summary>Every place worth asking about, ordered by three-dimensional distance from the
    /// objective, so the first one that routes buys the most.</summary>
    ///
    /// <para><b>Two floors, both asked.</b> The hop that matters is very often the one that gets
    /// the player DOWN onto the floor the objective stands on, so every candidate is proposed at
    /// the objective's own height as well as the player's. Heights are the cheapest thing this
    /// planner can change and the most expensive thing to get wrong.</para>
    private static List<Candidate> Candidates(float tx, float ty, float tz, float ax, float ay, float az)
    {
        var list = new List<Candidate>();
        float[] heights = { ty, ay };

        foreach (float y in heights)
        {
            // A ring round the objective: what its other side looks like.
            for (int ring = 0; ring < 2; ring++)
            {
                float radius = ring == 0 ? RingAM : RingBM;
                for (int step = 0; step < FieldHeadingService.SECTORS; step++)
                {
                    double angle = 2.0 * Math.PI * step / FieldHeadingService.SECTORS;
                    float x = tx + (float)(Math.Sin(angle) * radius);
                    float z = tz + (float)(Math.Cos(angle) * radius);
                    list.Add(new Candidate(x, y, z, Dist(x, y, z, tx, ty, tz)));
                }
            }
            // One hop out from where the player stands, in every compass direction: the
            // candidate you actually reach from here, rather than one near the goal.
            for (int step = 0; step < FieldHeadingService.SECTORS; step++)
            {
                double angle = 2.0 * Math.PI * step / FieldHeadingService.SECTORS;
                float x = ax + (float)(Math.Sin(angle) * HopM);
                float z = az + (float)(Math.Cos(angle) * HopM);
                list.Add(new Candidate(x, y, z, Dist(x, y, z, tx, ty, tz)));
            }
        }

        list.Sort((a, b) => a.D.CompareTo(b.D));
        return list;
    }

    /// <summary><b>A candidate earns a hop when going by way of it stays inside the detour budget.</b>
    ///
    /// <para>It does NOT have to land nearer the objective. That was the old rule and it is why a
    /// detour could not be planned: the way round a hoarding begins by walking away from what you
    /// are walking to, and every such bearing was refused unmeasured.</para>
    ///
    /// <para>What decides it instead is the length of the ROUTE. Walking via the candidate costs
    /// <c>(here to candidate) + (candidate to objective)</c>; the straight line from where this
    /// walk began costs <see cref="_startDist"/>. The difference is the detour, and it may not
    /// exceed <see cref="TotalDetourM"/>. A step along a wall buys almost nothing on the flat
    /// distance and costs almost nothing here — which is exactly right, and exactly backwards
    /// under the old rule. A step that leads nowhere lengthens the route and spends the budget,
    /// so circling stops itself.</para>
    /// </summary>
    private static bool Affordable(float cx, float cy, float cz)
    {
        float toC = Dist(_ax, _ay, _az, cx, cy, cz);
        float fromC = Dist(cx, cy, cz, _tx, _ty, _tz);
        return _walkedM + toC + fromC <= _startDist + TotalDetourM;
    }

    /// <summary>How much longer than the straight line the route via a candidate is. Negative means
    /// the candidate lies on the way; it is the number the log reports for a hop that gets
    /// further away, so a detour can be told from a mistake.</summary>
    private static float DetourOf(float cx, float cy, float cz)
        => Dist(_ax, _ay, _az, cx, cy, cz) + Dist(cx, cy, cz, _tx, _ty, _tz) - StandDistance();

    private static float StandDistance() => Dist(_ax, _ay, _az, _tx, _ty, _tz);

    private static bool Spent(in Candidate c)
    {
        foreach (var s in _spent)
            if (Dist(s.X, s.Y, s.Z, c.X, c.Y, c.Z) <= MissionBeaconHooks.ARRIVED_M) return true;
        return false;
    }

    private static float Dist(float ax, float ay, float az, float bx, float by, float bz)
    {
        float dx = bx - ax, dy = by - ay, dz = bz - az;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Start over when the objective has moved, or the player has changed floor after a
    /// ladder: a hop found from somewhere else is a confident walk to the wrong place.</summary>
    private static void Retarget(float tx, float ty, float tz, float py)
    {
        if (_have)
        {
            float moved = Dist(tx, ty, tz, _tx, _ty, _tz);
            if (moved <= MovedM && MathF.Abs(py - _py) < LevelRoute.LevelM) return;
        }
        Reset();
        _have = true;
        _tx = tx; _ty = ty; _tz = tz; _py = py;
    }
}
