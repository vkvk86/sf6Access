using System;
using System.Globalization;
using REFrameworkNET;
using static SF6Access.Services.WorldTour.LevelRoutePlanner;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>The floor an objective marker actually belongs to, and the reason every route question is
/// asked about that floor rather than about the marker.</b>
///
/// <para><b>The thing this fixes, written down while it was still open.</b> 2026-10-07, Metro City
/// training centre: eight measured drops, the lowest three landing at y=-9.8, and an objective whose
/// marker read y=-8.8. One metre of nothing. Not one of those eight routes had a second leg the
/// engine would confirm, and the search wrote <c>no full route</c> in its own log — not because
/// eight ways down were dead ends, but because the point every one of them was asked about was a
/// metre off the ground and no navmesh node lives in the air.</para>
///
/// <para>An objective's Y is whatever point on some object's origin chain its designer picked: the
/// soles, the chest, the centre of a crate, sometimes below the floor it stands on. That is not a
/// fact any guide can argue with, but it is also not a place. So every number below is measured by
/// a ray, nothing is guessed, and — the rule this file is written under — <b>when the floor cannot
/// be measured the marker is handed back exactly as it arrived</b>. A search asked about the wrong
/// point is not made worse by being asked about the same point it was always asked about.</para>
///
/// <para>Correcting the question only ever changes the Y, never the place: the marker's own column
/// of world is where the objective is, whichever bone the marker hangs from.</para>
/// </summary>
internal static class ObjectiveFloor
{
    /// <summary>How far below the marker the downward ray looks. Deep enough for a marker reading
    /// the air over a pit, no deeper — <see cref="DropEdge.ProbeDropM"/> is the number every other
    /// floor answer in this mod already uses, and two numbers for the same question is one too many.</summary>
    private const float ProbeDownM = DropEdge.ProbeDropM;

    /// <summary>How far above the marker the upward ray looks, and why it is short: it is looking
    /// for the underside of the floor a buried marker is standing ON, which is a body height away
    /// at most. Looking twenty metres up would find the ceiling of the floor above and call it
    /// ground.</summary>
    private const float ProbeUpM = 3.5f;

    /// <summary>A marker does not move. One measurement per marker is one measurement per objective,
    /// and anything shorter only spends rays to be told the same thing twice.</summary>
    private const long BetweenMs = 400;

    private static Spot _asked, _aim;
    private static bool _known;
    private static long _probedAt;
    private static string _noted;

    /// <summary>
    /// <b>The point to ask the way to.</b> The floor under (or over) <paramref name="marker"/> when
    /// the world will say what that floor is; the marker itself when it already stands on one, when
    /// its nearest floor is somewhere else entirely, or when no cast could be run at all.
    /// </summary>
    internal static Spot Aim(Spot marker)
    {
        bool same = _asked.Equals(marker);
        _asked = marker;
        if (same)
        {
            if (_known) return _aim;
            // A marker whose floor has not been read yet is worth one try every few frames, and no
            // more: better to ask about the marker this refresh than to spend every refresh asking.
            if (Environment.TickCount64 - _probedAt < BetweenMs) return marker;
        }
        _probedAt = Environment.TickCount64;
        _aim = Measure(marker);
        _known = true;
        if (!_aim.Equals(marker)) Note(marker, _aim);
        return _aim;
    }

    /// <summary><b>The same question without the remembering across objectives</b>: one measurement
    /// of the column a single point stands in, right now. An endpoint whose engine answer came back
    /// <c>off the mesh</c> gets one sideways nudge set and one of these — the sideways steps cannot
    /// reach a point that hangs above its own floor, and moving it down onto that floor can.
    ///
    /// <para>Written twice per column at most, and never more: the caller that asks whether a floor
    /// EXISTS and the caller that then asks where it is share the remembered answer, because both
    /// questions cost the player time on the same shared limiter.</para></summary>
    internal static Spot At(Spot point)
    {
        if (_atKnown && _atPoint.Equals(point)) return _atFloor;
        _atPoint = point;
        _atFloor = Measure(point);
        _atKnown = true;
        return _atFloor;
    }

    /// <summary>Can this point be moved onto a floor of its own? False means the vertical nudge would
    /// be the same question asked again, at the same point, with the same answer — see
    /// <c>LevelRoute.Ask</c>, which drops the try rather than spending a shared query on it.</summary>
    internal static bool HasFloor(Spot point) => !At(point).Equals(point);

    private static Spot _atPoint, _atFloor;
    private static bool _atKnown;

    /// <summary>Two rays, one column, nearer floor wins. Neither running is not the same answer as
    /// neither existing: NaN means "the sensor could not be read", and the marker goes back whole.</summary>
    private static Spot Measure(Spot marker)
    {
        float below = float.NaN, above = float.NaN;
        try
        {
            // DOWN, from just above the marker: the floor it should be standing on.
            if (FieldRayCaster.TryFloorBelow(marker.X, marker.Y + DropEdge.ProbeAboveM, marker.Z,
                                             ProbeDownM, out float under) && float.IsFinite(under))
                below = under;
            // UP: the same cast, aimed at the air just over the marker's head — starting below it
            // and looking down from above, which is another way of saying "looking up". A marker
            // buried inside the thing it belongs to has floor ABOVE it, and only this finds that.
            if (FieldRayCaster.TryFloorBelow(marker.X, marker.Y - DropEdge.ProbeAboveM + ProbeUpM, marker.Z,
                                             ProbeUpM, out float over) && float.IsFinite(over))
                above = over;
        }
        catch (Exception ex)
        {
            API.LogWarning($"[SF6Access] Objective floor: the cast could not run ({ex.GetType().Name}); asking about the marker itself");
            return marker;
        }
        float floorY = DropEdge.FloorOf(marker.Y, below, above);
        return float.IsFinite(floorY) ? marker with { Y = floorY } : marker;
    }

    /// <summary>Written once per marker, in the only form that survives being wrong: the numbers
    /// themselves. "The marker hangs" is a claim; "y -8.8 asked as -9.8" can be checked against a
    /// screenshot, a navmesh log line, or the ground under the player's own feet.</summary>
    private static void Note(Spot marker, Spot aim)
    {
        string key = Show(aim);
        if (key == _noted) return;
        _noted = key;
        API.LogInfo(string.Format(CultureInfo.InvariantCulture,
            "[SF6Access] Objective marker {0} stands on nothing: the floor it belongs to is {1:0.0} m away at {2} " +
            "- every route from here is asked about that floor",
            Show(marker), MathF.Abs(marker.Y - aim.Y), Show(aim)));
    }

    /// <summary>Forgotten when the objective is: a new objective is a new column of world, and the
    /// old answer belongs to the old one.</summary>
    internal static void Reset()
    {
        _asked = default; _known = false; _aim = default; _noted = null;
        _atPoint = default; _atFloor = default; _atKnown = false;
    }

    private static string Show(Spot s) => FormattableString.Invariant($"({s.X:F1}, {s.Y:F1}, {s.Z:F1})");
}
