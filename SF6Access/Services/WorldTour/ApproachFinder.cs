using System;
using System.Collections.Generic;
using REFrameworkNET;
using SF6Access.Services;
using SF6Access.Hooks.WorldTour;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>When the objective cannot be walked to, find the nearest place that CAN.</b>
///
/// <para><b>The problem this exists for.</b> A World Tour objective can sit on a
/// disconnected island of navmesh — an upper floor or roof reached through a door
/// or lift the navigation graph does not model. Confirmed in game 2026-09-09, 69
/// times: <c>PathNotFound (destNode=10421)</c>, a perfectly real destination node
/// with no walkable link to the player's. The engine is right and there is nothing
/// to fix in the query. But the STREET UNDER that objective is walkable, and
/// standing there turns "there is no way to get there" into "you are at the foot of
/// it" — which is a problem a player can actually solve.</para>
///
/// <para><b>How it searches.</b> Candidates in order of increasing distance from
/// the objective, so the FIRST one that routes is also the closest:</para>
/// <list type="number">
/// <item>the objective projected straight down to the player's own height — the
///   ground directly beneath it, which is the answer nearly every time;</item>
/// <item>a ring of eight points around it at <see cref="RING_A_M"/>;</item>
/// <item>a wider ring at <see cref="RING_B_M"/>, for a building whose footprint
///   swallows the first ring.</item>
/// </list>
/// <para>Eight is <see cref="FieldHeadingService.SECTORS"/> — the compass this mod
/// already speaks in — and the radii are multiples of
/// <see cref="MissionBeaconHooks.ARRIVED_M"/>, the distance it already calls
/// "arrived". Nothing here is a tuned number.</para>
///
/// <para><b>ONE probe per call, and never two queries in a frame.</b> This is the
/// hard-won rule. An earlier attempt ran a second <c>queryPathSync</c> inside the
/// same call, into the SAME <c>PathQueryReport</c>, and killed the game on the
/// loading screen. Each probe here is an ordinary
/// <see cref="FieldNavPath.TryReach"/> — its own fresh report, its own rate limit —
/// spent on a tick where the direct query is already known to be pointless. The
/// search therefore costs nothing extra: it uses the query the beacon was going to
/// make anyway, and it stops the moment something routes.</para>
///
/// <para>Once a substitute is found it is kept for as long as the objective stays
/// put, so the beacon simply guides there with corners, distances and everything
/// else working normally.</para>
/// </summary>
public static class ApproachFinder
{
    /// <summary>First ring: twice the arrival radius out from the objective.</summary>
    private static float RING_A_M => 2f * MissionBeaconHooks.ARRIVED_M;

    /// <summary>Second ring, for a footprint the first one lands inside.</summary>
    private static float RING_B_M => 4f * MissionBeaconHooks.ARRIVED_M;

    /// <summary>How far the objective may drift before the search restarts. An NPC
    /// shifting its weight must not throw away a good answer; one that has walked
    /// somewhere else must. The arrival radius is exactly this mod's own definition
    /// of "still the same place".</summary>
    private static float MOVED_M => MissionBeaconHooks.ARRIVED_M;

    // The objective this search belongs to, and the player's height when it began.
    private static bool _have;
    private static float _tx, _ty, _tz, _py;

    // How far through the candidate list we are, and the answer if we have one.
    private static int _probe;
    /// <summary>How many candidates the list currently being walked actually holds. Set on every
    /// probe from the list itself; see <see cref="Exhausted"/>.</summary>
    private static int _total;
    private static bool _found;
    private static float _ax, _ay, _az;
    private static bool _exhaustedLogged;

    /// <summary>Total candidates per height: the objective's own footprint, then two rings of
    /// <see cref="FieldHeadingService.SECTORS"/> points around it. Asked at the objective's own
    /// height and at the player's — see <see cref="Candidates"/>.</summary>
    private static int FootprintCount => 2 * (1 + 2 * FieldHeadingService.SECTORS);

    /// <summary>True once a reachable stand-in has been found for the current
    /// objective.</summary>
    public static bool HasSubstitute => _found;

    /// <summary>Every candidate this search has to offer has been asked and none of them routed: there
    /// is nothing walkable near the objective at either height.
    ///
    /// <para><b>Counted against the list actually built, never a constant.</b> The candidate list
    /// is one height's worth when the objective is on the player's own floor and two heights'
    /// worth when it is not (see <see cref="Candidates"/>), so <see cref="FootprintCount"/> — the
    /// upper bound — would never be reached on the short list and this would stay false for ever.
    /// Exhausted being false is what keeps the beacon saying "searching" instead of reporting
    /// anything, so getting it wrong here does not misinform, it silences.</para></summary>
    public static bool Exhausted => !_found && _probe >= _total && _probe > 0;

    /// <summary>The player has stood at the stand-in; from then on it has done its job (cleared with the search).</summary>
    public static bool Arrived { get; private set; }

    public static void MarkArrived() => Arrived = true;

    /// <summary>The reachable stand-in, if one has been found.</summary>
    public static bool TryGet(float tx, float ty, float tz, float py,
                              out float ax, out float ay, out float az)
    {
        ax = ay = az = 0f;
        Retarget(tx, ty, tz, py);
        if (!_found) return false;
        ax = _ax; ay = _ay; az = _az;
        return true;
    }

    /// <summary>Spend this tick's one query on the next candidate. Returns true when
    /// that candidate routed and the search is over.</summary>
    public static bool Probe(float tx, float ty, float tz, float px, float py, float pz,
                             float longestM = float.PositiveInfinity)
    {
        Retarget(tx, ty, tz, py);
        if (_found) return true;
        var candidates = Candidates(tx, ty, tz, py);
        _total = candidates.Count;
        if (_probe >= candidates.Count)
        {
            if (!_exhaustedLogged)
            {
                _exhaustedLogged = true;
                API.LogInfo($"[SF6Access] Approach: no reachable stand-in within {RING_B_M:0} m of " +
                            $"({tx:0.0}, {ty:0.0}, {tz:0.0}) after {candidates.Count} probes");
            }
            return false;
        }

        var c = candidates[_probe];
        _probe++;

        // The candidate's OWN height, not the player's. See Candidates: a ring asked only at the
        // player's level answered questions about the roof they were standing on, never about the
        // floor the objective was actually on.
        var answer = FieldNavPath.TryReach(c.x, c.y, c.z);
        // Throttled means the query never ran, and a player off the navmesh (on a ladder,
        // mid-jump) says nothing about the candidate: give it back for the next tick.
        if (answer.Throttled || (answer.Ok && answer.StartOffMesh)) { _probe--; return false; }
        if (!answer.Ok || !answer.Exists || answer.PathM > longestM) return false;

        _found = true;
        _ax = c.x; _ay = c.y; _az = c.z;
        API.LogInfo($"[SF6Access] Approach: probe {_probe} of {candidates.Count} reached " +
                    $"({c.x:0.0}, {c.y:0.0}, {c.z:0.0}) - guiding there instead of the objective, " +
                    "which is on a disconnected piece of navmesh");
        return true;
    }

    /// <summary>Every candidate, in the order they are asked: <b>every one of them at the
    /// objective's own height first, then the same set at the player's</b> — 2026-10-07, and this
    /// is the single change that makes a destination seven metres below reachable at all.
    ///
    /// <para>Until now every candidate was asked at the PLAYER'S height. Stand on the roof of the
    /// thing you are going to and what do you learn? That the roof is walkable, all seventeen
    /// times. The ground the objective stands on — the only question worth asking — was never
    /// put to the engine even once, so "the nearest reachable place" was always a place on the
    /// wrong floor, and a destination a sighted player simply walks down to came back as one
    /// with no route to it.</para>
    ///
    /// <para><b>The player's own height stays in the list, second.</b> Dropping it would have been
    /// cheaper and it would have been a mistake: the objective's Y is whatever its designer gave
    /// the object's origin, and it hangs off the real floor often enough that the old height is
    /// sometimes the ONLY one the navmesh answers. Ask the objective's floor first so it wins
    /// whenever both route, and ask the player's afterwards so nothing the search used to find
    /// can be lost. One tier at a time, each shortest-first inside itself.</para></summary>
    private static List<(float x, float y, float z, float d)> Candidates(float tx, float ty, float tz, float py)
    {
        var list = new List<(float x, float y, float z, float d)>();
        foreach (float y in Heights(ty, py))
        {
            var tier = Tier(tx, y, tz, ty);
            tier.Sort((a, b) => a.d.CompareTo(b.d));
            list.AddRange(tier);
        }
        return list;
    }

    /// <summary>One height's worth of stand-ins: the objective's own footprint, then two rings of
    /// <see cref="FieldHeadingService.SECTORS"/> points around it. Each carries how far it really
    /// is from the objective, so the nearest is asked first.</summary>
    private static List<(float x, float y, float z, float d)> Tier(float tx, float y, float tz, float ty)
    {
        var list = new List<(float x, float y, float z, float d)>
        {
            (tx, y, tz, Dist(tx, y, tz, tx, ty, tz)),
        };

        int n = FieldHeadingService.SECTORS;
        for (int ring = 0; ring < 2; ring++)
            for (int step = 0; step < n; step++)
            {
                float radius = ring == 0 ? RING_A_M : RING_B_M;
                double angle = 2.0 * Math.PI * step / n;
                float x = tx + (float)(Math.Sin(angle) * radius), z = tz + (float)(Math.Cos(angle) * radius);
                list.Add((x, y, z, Dist(x, y, z, tx, ty, tz)));
            }
        return list;
    }

    /// <summary>Heights this close together are one floor: asking the same ring twice buys
    /// nothing and costs a quarter of a second a candidate.</summary>
    private const float SameHeightM = 0.25f;

    /// <summary>The two heights worth asking about: where the objective is, and where the player
    /// is. One rather than two when they are within a few centimetres of each other.</summary>
    private static float[] Heights(float ty, float py)
        => Math.Abs(ty - py) < SameHeightM ? new[] { ty } : new[] { ty, py };

    /// <summary>Start over when the objective is somewhere else, or the player on another
    /// floor (after a ladder): a stand-in found from elsewhere is a confident walk to the
    /// wrong place.</summary>
    private static void Retarget(float tx, float ty, float tz, float py)
    {
        if (_have)
        {
            float dx = tx - _tx, dy = ty - _ty, dz = tz - _tz;
            float moved = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (moved <= MOVED_M && Math.Abs(py - _py) < LevelRoute.LevelM) return;
        }

        _have = true;
        Arrived = false;
        _tx = tx; _ty = ty; _tz = tz; _py = py;
        _probe = 0;
        _total = 0;
        _found = false;
        _exhaustedLogged = false;
    }

    /// <summary>Three-dimensional distance, so the order of the candidates is decided by how far
    /// each one really is from the objective — including the seven metres that separate floors.</summary>
    private static float Dist(float ax, float ay, float az, float bx, float by, float bz)
    {
        float dx = bx - ax, dy = by - ay, dz = bz - az;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Forget everything (left the field, new city).</summary>
    public static void Reset()
    {
        _have = _found = _exhaustedLogged = false;
        Arrived = false;
        _probe = 0;
        _total = 0;
    }
}
