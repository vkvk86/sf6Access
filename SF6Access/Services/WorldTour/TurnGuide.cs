using System;
using System.Collections.Generic;
using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>Turn-by-turn guidance from the route the engine ALREADY computed — the player
/// walks, the mod talks.</b>
///
/// <para><b>Where the data comes from, and what it costs.</b> The beacon's resolve asks
/// the engine pathfinder about the current leg end twice a second, and
/// <see cref="FieldNavPath"/> keeps the whole bend list of the most recent successful
/// query. This guide only ever RECALLS that list (<see cref="FieldNavPath.TryRecallRoute"/>)
/// — it never runs a query of its own, never writes to any engine structure, and never
/// touches the input slot. Every crash this project has paid for came from WRITING
/// engine-owned memory (the navi trial, three times); this guide is read-only and
/// carries no crash surface. That is the deal the owner chose on 2026-09-29: no more
/// auto-navigation, the original guidance made genuinely good instead.</para>
///
/// <para><b>What this says that the beacon could not say before.</b> The beacon's own
/// aim (<see cref="RouteLookAhead"/>) stops reading at the first bend or four metres,
/// whichever comes first — enough to point a homing sound, not enough to walk by ear.
/// The recalled bend list runs all the way to the leg end, so the guide can answer the
/// question a blind player actually asks: <i>do I keep straight, and for how long, and
/// then which way?</i> "Turn right in 12 meters" ten metres early is the difference
/// between following a route and tripping over it.</para>
///
/// <para><b>What counts as a turn.</b> A bearing change of at least
/// <see cref="MinTurnDeg"/> — one clock sector. String-pulled routes carry micro-bends
/// of a few degrees that the player straightens out without noticing; announcing them
/// would be noise. Consecutive micro-bends ACCUMULATE toward the threshold, so a gentle
/// curve that really does change the heading is still announced.</para>
///
/// <para><b>When the route cannot be recalled, this guide says nothing</b> and the
/// beacon's original four-metre logic runs instead — a stale cache or a rate-limited
/// query must degrade into the old behaviour, never into a guess.</para>
/// </summary>
internal static class TurnGuide
{
    /// <summary>The three answers a call can give: it spoke, it knows the route and has
    /// nothing to say, or it does not know the route (the caller's fallback then runs).</summary>
    internal enum Outcome { NoRoute, Quiet, Spoke }

    /// <summary>Bearing change that counts as a turn worth announcing, in degrees.
    /// Matches the spoken clock sector (12 hours over 360°): anything smaller is
    /// "keep straight". <see cref="RouteLookAhead"/> uses half of this because a homing
    /// SOUND can track a gentle bend; a spoken instruction cannot — saying "turn" for
    /// 20° teaches the player to ignore the word.</summary>
    private const double MinTurnDeg = 30.0;

    /// <summary>Further than this from EVERY route segment, the player is not on the
    /// route at all, and a turn announcement ("straight ahead") would be a lie. The
    /// heading speech and the homing cue are what bring them back.</summary>
    private const float OffRouteM = 4f;

    /// <summary>Inside this flat distance the bend is announced as "turn now". The
    /// beacon's own now-zone is the capsule radius; using the same radius keeps
    /// "turn now" and the collision capsule in agreement.</summary>
    private static float TurnNowM => FieldRayCaster.CapsuleRadius();

    /// <summary>Minimum pause between two guidance lines from this guide. The caller
    /// applies its own longer hold on top; this only stops the distance-halving rule
    /// from out-talking a fast walk.</summary>
    private const long SpeakHoldMs = 1200;

    /// <summary>A breakable whose perpendicular distance to some UPCOMING route segment
    /// is within this is plausibly ON the walking line. The navmesh does not model
    /// breakables at all (see <see cref="LevelRoutePlanner"/>), so the string-pulled
    /// line goes where it wants; a prop within a body width of that line is the thing
    /// the player is about to walk into.</summary>
    private const float BreakableOnRouteM = 0.9f;

    /// <summary>Don't preview obstacles farther away than this: beyond it the warning
    /// is forgotten before it is useful, and the route may not survive that long anyway.</summary>
    private const float BreakableMaxM = 20f;

    /// <summary>Closer than this the player is already on top of the prop — the collision
    /// cue, not a preview, is the right voice there.</summary>
    private const float BreakableMinM = 1.5f;

    /// <summary>A breakable more than this far above or below the player is on another
    /// floor and not theirs to smash.</summary>
    private const float BreakableFloorM = 3f;

    /// <summary>Announced props are remembered by rounded coordinate so each is named
    /// once per target, never lectured.</summary>
    private const int MaxRememberedBreakables = 32;

    private static string _bendKey;
    private static float _announcedM;
    private static bool _saidNow;
    private static long _nextSpeak;
    private static readonly List<(float X, float Z)> _breakablesTold = new();

    /// <summary>A new target, or a field change: forget the bend that was being counted
    /// down and every prop already named.</summary>
    internal static void Reset()
    {
        _bendKey = null;
        _announcedM = 0f;
        _saidNow = false;
        _nextSpeak = 0;
        _breakablesTold.Clear();
    }

    /// <summary><b>Announce the next real turn along the recalled route, when there is
    /// one worth announcing.</b> The policy, in order:</summary>
    /// <list type="bullet">
    /// <item>A NEW bend (the player passed the previous one, or the route changed):
    /// "turn {side} in {N} meters", N the flat distance to the bend.</item>
    /// <item>The SAME bend at HALF the distance last announced: said again with the new
    /// number, so the countdown stays honest without becoming a metronome.</item>
    /// <item>The bend inside <see cref="TurnNowM"/>: "turn {side} now", once.</item>
    /// </list>
    internal static Outcome AnnounceTurn(in BeaconDestination dest,
                                         float px, float py, float pz, long now)
    {
        if (!FieldNavPath.TryRecallRoute(dest.TargetX, dest.TargetY, dest.TargetZ, out var corners))
            return Outcome.NoRoute;
        if (corners == null || corners.Length < 1) return Outcome.NoRoute;

        // Straight to the leg end (one corner, the leg end itself): nothing to turn for.
        if (corners.Length < 2) return Outcome.Quiet;

        if (!TryLocate(corners, px, pz, out int firstCandidate)) return Outcome.Quiet; // off route

        // Walk the candidates in order, accumulating micro-bends, until the heading has
        // really changed. corners[n-1] is the leg end and has no outgoing segment, so the
        // last turn candidate is corners[n-2].
        double inBearing = firstCandidate == 0
            ? Bearing(px, pz, corners[0].X, corners[0].Z)
            : Bearing(corners[firstCandidate - 1].X, corners[firstCandidate - 1].Z,
                      corners[firstCandidate].X, corners[firstCandidate].Z);
        int bend = -1;
        double bendAngle = 0;
        for (int k = firstCandidate; k <= corners.Length - 2; k++)
        {
            double outBearing = Bearing(corners[k].X, corners[k].Z, corners[k + 1].X, corners[k + 1].Z);
            double angle = Math.IEEERemainder(outBearing - inBearing, 360.0);
            if (Math.Abs(angle) >= MinTurnDeg) { bend = k; bendAngle = angle; break; }
            // Below the threshold on its own: treated as straight, but the NEXT bend is
            // still measured against the ORIGINAL incoming bearing, so three 15° wiggles
            // that really swing the heading add up to the announcement they deserve.
        }
        if (bend < 0) return Outcome.Quiet;

        var bp = corners[bend];
        float dist = Flat(px, pz, bp.X, bp.Z);
        // RouteLookAhead's calibrated basis: cross = oldDX*newDZ - oldDZ*newDX is NEGATIVE
        // for a left swing (its 90°->180° case), and dest.Turn > 0 means right. With
        // angle = out - in measured the same way, left is angle > 0 and right is angle < 0.
        string side = ExplorationText.Get(bendAngle < 0 ? "right" : "left",
                                          bendAngle < 0 ? "right" : "left");
        bool isNow = dist <= TurnNowM;
        string key = $"{bp.X:F0},{bp.Z:F0}";

        if (key != _bendKey)
        {
            _bendKey = key;
            _announcedM = dist;
            _saidNow = isNow;
            Speak(isNow
                ? string.Format(ExplorationText.Get("turn_now", "Turn {0} now"), side)
                : string.Format(ExplorationText.Get("turn_in", "Turn {0} in {1} meters"), side,
                                Math.Max(1, (int)MathF.Round(dist))));
            _nextSpeak = now + SpeakHoldMs;
            return Outcome.Spoke;
        }

        if (isNow)
        {
            if (_saidNow) return Outcome.Quiet;
            _saidNow = true;
            Speak(string.Format(ExplorationText.Get("turn_now", "Turn {0} now"), side));
            _nextSpeak = now + SpeakHoldMs;
            return Outcome.Spoke;
        }

        // Same bend, half the distance: the countdown refresh. Hold-gated so a fast walk
        // does not produce a second line hard on the first.
        if (now >= _nextSpeak && dist <= _announcedM * 0.5f && dist > TurnNowM)
        {
            _announcedM = dist;
            Speak(string.Format(ExplorationText.Get("turn_in", "Turn {0} in {1} meters"), side,
                                Math.Max(1, (int)MathF.Round(dist))));
            _nextSpeak = now + SpeakHoldMs;
            return Outcome.Spoke;
        }
        return Outcome.Quiet;
    }

    /// <summary><b>Name the nearest intact breakable lying ON the walking line ahead —
    /// once per prop per target.</b> The navmesh does not model breakables, so a route
    /// can run straight through a crate the player must smash with a master action
    /// (owner, 2026-09-29: "途中还要用师傅教的动作…还有破坏障碍"). The route cannot know
    /// the prop is there, but <see cref="BreakableCatalog"/> can — and a prop within a
    /// body width of the line is the one the player is about to be stopped by. Saying so
    /// BEFORE the bump is the difference between "break it here" and "why did I stop".</summary>
    ///
    /// <para><b>The catalogue only holds INTACT props</b> (a broken one drops out on the
    /// next scan), so this never points at rubble. Each prop is named once per target:
    /// being told, not lectured, is the rule <see cref="ObstacleHold"/> set and it is
    /// kept here.</para></summary>
    internal static Outcome AnnounceBreakable(in BeaconDestination dest,
                                              float px, float py, float pz, long now)
    {
        if (now < _nextSpeak) return Outcome.Quiet;
        if (!FieldNavPath.TryRecallRoute(dest.TargetX, dest.TargetY, dest.TargetZ, out var corners))
            return Outcome.NoRoute;
        if (corners == null || corners.Length < 1) return Outcome.NoRoute;
        if (!TryLocate(corners, px, pz, out int firstCandidate)) return Outcome.Quiet;
        var all = BreakableCatalog.All();
        if (all.Count == 0) return Outcome.Quiet;

        // The walking line from here on: the player's own position to the first upcoming
        // corner, then the corner chain to the leg end.
        int from = Math.Max(0, firstCandidate - 1);
        BreakableCatalog.Breakable? best = null;
        float bestDist = float.PositiveInfinity;
        foreach (var prop in all)
        {
            if (Math.Abs(prop.Y - py) > BreakableFloorM) continue;
            float flat = Flat(px, pz, prop.X, prop.Z);
            if (flat < BreakableMinM || flat > BreakableMaxM || flat >= bestDist) continue;
            if (AlreadyTold(prop)) continue;
            if (!OnRoute(corners, from, px, pz, prop)) continue;
            best = prop;
            bestDist = flat;
        }
        if (best == null) return Outcome.Quiet;

        var b = best.Value;
        Remember(b);
        Speak(string.Format(LangFile.Get("wt.route.breakable_ahead",
                "Breakable obstacle {0} meters ahead: a master action can smash it"),
            Math.Max(1, (int)MathF.Round(bestDist))));
        API.LogInfo($"[SF6Access] Route guidance: breakable on the route at ({b.X:F2}, {b.Y:F2}, {b.Z:F2}), " +
                    $"{bestDist:F1} m ahead");
        _nextSpeak = now + SpeakHoldMs;
        return Outcome.Spoke;
    }

    /// <summary><b>Where on the route is the player?</b> Finds the route segment with the
    /// smallest perpendicular distance to the player and returns the index of the first
    /// turn candidate: the corner that ends that segment — or corner 0 when the player
    /// has not reached the route's first corner yet. False when the player is more than
    /// <see cref="OffRouteM"/> from every segment: a turn called from out there would be
    /// called from the wrong street.</summary>
    private static bool TryLocate((float X, float Y, float Z)[] corners, float px, float pz,
                                  out int firstCandidate)
    {
        firstCandidate = 0;
        float bestPerp = float.PositiveInfinity;
        int bestSeg = -1;
        float bestT = 0f;
        for (int i = 0; i <= corners.Length - 2; i++)
        {
            float abx = corners[i + 1].X - corners[i].X, abz = corners[i + 1].Z - corners[i].Z;
            float len2 = abx * abx + abz * abz;
            if (len2 < 1e-6f) continue;
            float t = ((px - corners[i].X) * abx + (pz - corners[i].Z) * abz) / len2;
            float tc = Math.Clamp(t, 0f, 1f);
            float qx = corners[i].X + tc * abx, qz = corners[i].Z + tc * abz;
            float perp = Flat(px, pz, qx, qz);
            if (perp < bestPerp) { bestPerp = perp; bestSeg = i; bestT = t; }
        }
        if (bestSeg < 0 || bestPerp > OffRouteM) return false;
        // Before the route's first corner, that corner is the candidate; past any other
        // segment's start, the corner at its END is. A corner the player is standing on
        // is not skipped — it is exactly the "turn now" case.
        firstCandidate = bestSeg == 0 && bestT < 0f ? 0 : bestSeg + 1;
        return firstCandidate <= corners.Length - 2 || corners.Length >= 2;
    }

    /// <summary>Is this prop within a body width of any upcoming segment? The check runs
    /// from the segment the player is on (or its predecessor, so a prop beside the
    /// player's own feet can still count) to the leg end.</summary>
    private static bool OnRoute((float X, float Y, float Z)[] corners, int from,
                                float px, float pz, in BreakableCatalog.Breakable prop)
    {
        // The approach segment first: player -> first upcoming corner.
        float ax = px, az = pz;
        for (int i = from; i <= corners.Length - 1; i++)
        {
            float bx = corners[i].X, bz = corners[i].Z;
            if (PerpDistance(px: ax, pz: az, bx, bz, prop.X, prop.Z) <= BreakableOnRouteM) return true;
            ax = bx; az = bz;
        }
        return false;
    }

    /// <summary>Perpendicular XZ distance from point (x,z) to the SEGMENT a->b.</summary>
    private static float PerpDistance(float px, float pz, float bx, float bz, float x, float z)
    {
        float abx = bx - px, abz = bz - pz;
        float len2 = abx * abx + abz * abz;
        if (len2 < 1e-6f) return Flat(x, z, px, pz);
        float t = Math.Clamp(((x - px) * abx + (z - pz) * abz) / len2, 0f, 1f);
        return Flat(x, z, px + t * abx, pz + t * abz);
    }

    private static bool AlreadyTold(in BreakableCatalog.Breakable prop)
    {
        foreach (var t in _breakablesTold)
            if (Math.Abs(t.X - prop.X) < 1f && Math.Abs(t.Z - prop.Z) < 1f) return true;
        return false;
    }

    private static void Remember(in BreakableCatalog.Breakable prop)
    {
        if (_breakablesTold.Count >= MaxRememberedBreakables) _breakablesTold.RemoveAt(0);
        _breakablesTold.Add((prop.X, prop.Z));
    }

    /// <summary>The bearing convention the rest of this mod already calibrated:
    /// Atan2(dx, dz), degrees — the same one <see cref="RouteLookAhead"/> and the navi
    /// trial's bend log use, so a "right" here is the same right the beacon means.</summary>
    private static double Bearing(float ax, float az, float bx, float bz)
        => Math.Atan2(bx - ax, bz - az) * 180.0 / Math.PI;

    private static float Flat(float ax, float az, float bx, float bz)
    {
        float dx = bx - ax, dz = bz - az;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static void Speak(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        ScreenReaderService.Speak(text, interrupt: false);
        API.LogInfo("[SF6Access] Route guidance: " + text);
    }
}
