using System;
using System.Collections.Generic;
using static SF6Access.Services.WorldTour.LevelRoutePlanner;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>Where a route the engine laid out changes storey — the pure geometry, no engine.</b> The
/// owner's correction, 2026-10-05: <i>"我要的是他在规划路线的时候，就把下行的梯子规划出来，跟那个梯子
/// 距离玩家多远没有关系。假设玩家离梯子很远，但是要经过这个梯子怎么办呢？"</i> A sweep of rays around
/// the player can only ever find the stairs under their own feet; a stair at the far end of a long
/// walk is found by reading the route the engine already computed, because that route must pass
/// through it.
///
/// <para><b>This is the half that can be proved without a game running.</b> Finding the answer
/// needs <see cref="FieldNavPath"/> and <see cref="FieldRayCaster"/>; deciding WHICH points of a
/// route are worth measuring needs nothing but arithmetic, so it lives here and is tested by
/// <c>tests/Navigation.Tests</c>. <see cref="RouteStairFinder"/> does the casting and the
/// judging, exactly as <see cref="JumpLinkCatalog"/> does for <see cref="NarrowPath"/>.</para>
///
/// <para><b>A change of level is the engine saying "up or down here".</b> Two consecutive route
/// corners far apart in height are not a tilt in the mesh — they are a storey. That is where a
/// stair, a ramp or a drop is, and it is the only hint this project has about stairs it cannot
/// reach with a ray. Whether the hint is a stair is still decided by measuring the ground; nothing
/// here assumes it.</para>
/// </summary>
internal static class RouteBend
{
    /// <summary><b>A bend of a route that changes level.</b> <paramref name="At"/> is the point on
    /// the route the level changes at, <paramref name="DirX"/>/<paramref name="DirZ"/> the route's
    /// own direction through it as a unit vector on the ground plane, and
    /// <paramref name="RiseM"/> the signed change — positive UP.</summary>
    internal readonly record struct Bend(Spot At, float DirX, float DirZ, float RiseM);

    /// <summary><b>Which points of a route are a change of LEVEL.</b> A route is a list of corners
    /// in order; two consecutive corners this far apart in height are the engine sending the
    /// player up or down something there, and that is where a stair may be, however far from the
    /// player the corner is.
    ///
    /// <para><b>The deepest bends are kept when there are too many.</b> A city route bends at
    /// every corner and only some bends change storeys; the cap must not spend itself on the
    /// player's own block and leave the far stair unmeasured. Corners are taken from the END of
    /// the route backwards for that reason — the far end is the one a sweep around the player
    /// could never have reached, which is the whole point of looking here.</para>
    ///
    /// <para><b>A bend with no ground direction is skipped.</b> A purely vertical corner (a point
    /// exactly above the one before it) gives no line to lay the floor samples along, and a stair
    /// whose foot cannot be found from the route is one this has no honest way to measure. Skipping
    /// it is the honest answer, not a guess.</para>
    ///
    /// <para><b>Nothing here reads anything.</b> A null or single-corner route yields no bends, and
    /// no input produces an exception: a route that could not be recalled must degrade into "no
    /// stair found", never into a crash inside the per-frame tick.</para></summary>
    internal static List<Bend> Find((float X, float Y, float Z)[] corners, float riseM, int max)
    {
        var bends = new List<Bend>();
        if (corners == null || corners.Length < 2 || max <= 0) return bends;

        var rising = new List<(int i, float rise)>();
        for (int i = 0; i < corners.Length - 1; i++)
        {
            float rise = corners[i + 1].Y - corners[i].Y;
            if (MathF.Abs(rise) >= riseM) rising.Add((i, rise));
        }
        if (rising.Count > max) rising.RemoveRange(0, rising.Count - max);

        foreach (var (i, rise) in rising)
        {
            var a = corners[i];
            var b = corners[i + 1];
            float dx = b.X - a.X, dz = b.Z - a.Z;
            float len = MathF.Sqrt(dx * dx + dz * dz);
            if (!(len > 1e-3f)) continue;   // a purely vertical step: no line to read along
            // The bend is the MIDDLE of the rising segment: the route's own line, so floor samples
            // fall on the ground the engine intends the player to cross.
            bends.Add(new Bend(new Spot((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f, (a.Z + b.Z) * 0.5f),
                               dx / len, dz / len, rise));
        }
        return bends;
    }

    /// <summary>A point on the route this far along from a bend, on the bend's own ground line.
    /// The samples of a flight are absolute world positions on the ROUTE — that is what makes this
    /// work at any distance, since nothing about the player's own position enters it.</summary>
    internal static Spot Along(in Bend b, float metres, float y) =>
        new(b.At.X + b.DirX * metres, y, b.At.Z + b.DirZ * metres);

    /// <summary><b>Where a LINE OF FLOOR SAMPLES changes storey — the fallback when the engine
    /// will not give a route.</b> The owner's report, 2026-10-05: <i>"他之前提示我找不到路，但是
    /// 我自己摸索到楼梯边他却告诉我就是这个楼梯，我要的是规划的时候就看到并带我走."</i> The
    /// engine's own route was the first answer to "find the far stair", and the log proved why it
    /// cannot be the only one: on the building the owner tested, the engine answered
    /// <c>PathNotFound … DISCONNECTED piece of mesh</c> thirty times over — it does not model the
    /// narrow stair at all, so it produces NO route, so there is nothing to recall. The stair was
    /// right there and the engine refused to admit it.
    ///
    /// <para><b>So a straight line of ground is measured instead, from the player toward the
    /// objective.</b> That line is what the player is going to walk anyway; sampling the ground
    /// along it costs one ray per sample and finds the stair whether or not the engine believes
    /// it exists. It is a LINE and not a circle, which is the whole economy of it: a sweep wide
    /// enough to reach a stair two hundred metres away would be a hundred and fifty times the
    /// rays, while a line that far is only two hundred metres divided by the sample spacing.</para>
    ///
    /// <para><paramref name="sampleAt"/> answers the floor at sample <paramref name="i"/>, or NaN
    /// for none, and <paramref name="measuredAt"/> says whether it was measured at all — "not
    /// measured" is never "no floor". Samples are spaced <paramref name="stepM"/> apart from
    /// <paramref name="firstM"/> along the line leaving <paramref name="from"/>, and each sample's
    /// world point is <c>from + dir * (firstM + i * stepM)</c> — the caller's own cast positions,
    /// so no position is ever reconstructed here. A run where the floor changes level by at least
    /// <paramref name="riseM"/> between consecutive GOOD samples is a storey change, reported as a
    /// <see cref="Bend"/> at that point with the line's own direction.</para>
    ///
    /// <para><b>Only the first few are kept, and the near ones win here.</b> A straight line to a
    /// destination crosses one city block after another; the first storey change on it is the one
    /// the player reaches first, and offering the planner a dozen far ones would spend its question
    /// budget on stairs the walk never gets to. This is the opposite of <see cref="Find"/>, which
    /// keeps the DEEPEST bends of a route — a route is already known to be walkable end to end, so
    /// the far end is free to keep, while a straight line is a guess about direction and only its
    /// beginning can be trusted.</para></summary>
    internal static List<Bend> AlongLine(Spot from, Func<int, float> sampleAt, Func<int, bool> measuredAt,
                                         int count, float firstM, float stepM, float riseM,
                                         int max, float dirX, float dirZ)
    {
        var bends = new List<Bend>();
        if (count < 2 || max <= 0) return bends;

        float prev = float.NaN;
        for (int i = 0; i < count && bends.Count < max; i++)
        {
            if (!measuredAt(i)) { prev = float.NaN; continue; }      // unmeasured breaks the run
            float f = sampleAt(i);
            if (!float.IsFinite(f)) { prev = float.NaN; continue; }  // a hole breaks it too
            if (float.IsFinite(prev) && MathF.Abs(f - prev) >= riseM)
            {
                float aM = firstM + (i - 1) * stepM, bM = firstM + i * stepM;
                float mid = (aM + bM) * 0.5f;
                bends.Add(new Bend(new Spot(from.X + dirX * mid, (prev + f) * 0.5f, from.Z + dirZ * mid),
                                   dirX, dirZ, f - prev));
            }
            prev = f;
        }
        return bends;
    }
}
