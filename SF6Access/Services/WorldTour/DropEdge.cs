using System;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>The judgement behind a fall: when the ground BELOW the player is the way to the objective.</b>
///
/// <para><b>The hole this fills.</b> A navmesh connects places a walker can walk between, and a
/// fall is not a walk. Where the objective stands on a floor the player can only reach by stepping
/// off the one they are on, the engine's answer is the same one bit it gives a hoarding:
/// <c>PathNotFound</c>. Asking again never produces a second bit, so the mod has said "look for an
/// edge to drop from" and left the player to find it by touch. The edge is measurable — the ground
/// under a sample is a ray away — and this is the rule that turns that measurement into an offer.</para>
///
/// <para><b>Three numbers decide it, and all three are about the LANDING, not the fall.</b> A drop
/// is only worth offering when the floor at the bottom is the floor the objective stands on: a fall
/// that lands on a different storey buys height and nothing else, and a fall that lands in the
/// objective's own locked yard buys the last metres. Depth only bounds it — short enough to be a
/// step the navmesh already walks, short enough that the landing is a drop and not a leap.</para>
///
/// <para><b>Pure judgement, no engine.</b> Every member here is a number in, a verdict out, so the
/// rules can be tested without a game and read without one. Whoever measures the ground is
/// <see cref="DropEdgeFinder"/>; whoever walks the player to it is the ordinary route.</para>
/// </summary>
internal static class DropEdge
{
    /// <summary>A fall shorter than this is a kerb or a step, which the navmesh already walks as
    /// ground. Below it there is nothing to offer: the player can walk down.</summary>
    internal const float MinDropM = 1.5f;

    /// <summary>A fall deeper than this is not a way down, it is a leap of faith — and the deeper
    /// the ray has to look, the likelier it is measuring a shaft the player cannot stand in.</summary>
    internal const float MaxDropM = 12f;

    /// <summary>How far a sample's floor may sit from the floor the player stands on and still be
    /// called the SAME platform. The standing point is only safe to walk to because every sample
    /// between the player and it was this platform, unbroken.</summary>
    internal const float SameFloorM = 1.0f;

    /// <summary>The ray starts this far above the player's feet. Above them, so the platform they
    /// are standing on is inside the cast and not behind its origin; only just, so a floor a body
    /// height up (a ledge, a crate) does not read as the ground under the sample.</summary>
    internal const float ProbeAboveM = 0.6f;

    /// <summary>...and looks this far down: deeper than <see cref="MaxDropM"/>, so a drop inside the
    /// limit is MEASURED rather than merely detected. A sample with no floor at all within this is
    /// a void, and a void has no landing to judge.</summary>
    internal const float ProbeDropM = 14f;

    /// <summary>Directions the search looks in: twice the compass the bearings are spoken in,
    /// because an edge between two of the eight sectors is the common case — a corner of a roof,
    /// the end of a hoarding — and a coarse ring walks straight over it.</summary>
    internal const int Sectors = 16;

    /// <summary>The first sample, in metres from the player's feet: a stride, so the edge is found
    /// while the player can still see the walk to it.</summary>
    internal const float FirstSampleM = 1.0f;

    /// <summary>Distance between samples.</summary>
    internal const float SampleStepM = 1.0f;

    /// <summary>How many samples per direction: 1 m to 9 m. Far enough for the far side of a yard
    /// and near enough that a walk to the edge is still the walk the player is walking.</summary>
    internal const int Samples = 9;

    /// <summary><b>A marker this far off the floor beneath it is hanging, not standing on anything
    /// — and that is a fact about the marker, not about the world.</b>
    ///
    /// <para>An objective's Y is a point on some object's own origin chain: the soles, the chest,
    /// or the middle of the air. Nothing in the game promises there is floor under it, and nothing
    /// promises there is not three metres of it either. Below this distance the two are the same
    /// thing and every question goes on being asked about exactly what it was asked about.</para></summary>
    internal const float HangM = 0.5f;

    /// <summary><b>Beyond this the marker is not hanging — it is somewhere else.</b> A marker whose
    /// nearest floor is twenty metres below is a rooftop marker, and moving the question down would
    /// be asking how to walk to the street instead of how to walk to the roof. Six metres is two
    /// storeys: wide enough for a chest-height marker on a ledge, tight enough that another floor
    /// is never mistaken for the one going spare.</summary>
    internal const float HangLimitM = 6f;

    /// <summary>
    /// <b>Which floor a marker that stands on nothing actually belongs to.</b>
    ///
    /// <para>Two answers are measured and one of them is right. <paramref name="below"/> is the floor
    /// beneath a marker reading a point in the air above its own ground — the common case, and the
    /// one a downward ray finds at once. <paramref name="above"/> is the floor a marker buried
    /// INSIDE the object it belongs to is standing under: live on 2026-10-07 the objective read
    /// y=-9.8 while the walkable floor above it was -8.0, so asking about the marker itself asked
    /// about a metre of nothing and every route ended there.</para>
    ///
    /// <para>The nearer of the two wins, because the marker's own floor is the NEAREST one by
    /// definition — everything else in that column is a storey somebody else lives on. NaN means
    /// neither measurement is this marker's business, and the caller then asks about the marker
    /// exactly as it always did.</para>
    /// </summary>
    internal static float FloorOf(float markerY, float below, float above)
    {
        float pick = float.NaN, best = float.MaxValue;
        Consider(below, markerY - below);
        Consider(above, above - markerY);
        return pick;

        void Consider(float floorY, float rise)
        {
            if (!float.IsFinite(floorY) || !float.IsFinite(rise)) return;
            float gap = MathF.Abs(rise);
            if (gap < HangM || gap > HangLimitM || gap >= best) return;
            best = gap;
            pick = floorY;
        }
    }

    /// <summary>True when a marker reading <paramref name="markerY"/> belongs to the floor at
    /// <paramref name="floorY"/> rather than standing on nothing at all — the test behind
    /// <see cref="FloorOf"/>, kept apart so it can be read without a ray.</summary>
    internal static bool Hangs(float markerY, float floorY)
    {
        float gap = MathF.Abs(markerY - floorY);
        return float.IsFinite(gap) && gap >= HangM && gap <= HangLimitM;
    }

    /// <summary>True when a floor this far below the player's feet is a drop worth offering.</summary>
    internal static bool IsDrop(float standY, float floorY)
    {
        float drop = standY - floorY;
        return drop >= MinDropM && drop <= MaxDropM;
    }

    /// <summary>True when this sample's floor is the platform the player is standing on.</summary>
    internal static bool SameFloor(float standY, float playerY)
        => MathF.Abs(standY - playerY) <= SameFloorM;

    /// <summary>True when a floor this high is the floor the objective stands on — <b>the whole
    /// point of the drop</b>: a landing on the objective's own storey is what turns "you are five
    /// metres above it" into "you are on its level".</summary>
    internal static bool LandsOn(float landingY, float targetY)
        => MathF.Abs(landingY - targetY) <= LevelRoutePlanner.LevelM;

    /// <summary>How good a landing is: the flat distance its foot leaves between the player and the
    /// objective. Height is settled by <see cref="LandsOn"/> before this is ever asked — once both
    /// are on the same floor, the metres that matter are the ones left to walk.</summary>
    internal static float Score(float landX, float landZ, float targetX, float targetZ)
    {
        float dx = landX - targetX, dz = landZ - targetZ;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Whether an edge is worth walking to and stepping off: an unbroken platform under
    /// the standing point, a fall inside the limit, and a landing on the objective's own floor.
    /// All three, or it is not an offer — a guide that proposes a fall into a yard the objective is
    /// not in has replaced an honest "there is no way" with a confident walk to the wrong place.</summary>
    internal static bool Worth(float standY, float playerY, float landingY, float targetY)
        => SameFloor(standY, playerY) && IsDrop(standY, landingY) && LandsOn(landingY, targetY);

    /// <summary>The distance from the player to sample <paramref name="i"/>, in metres.</summary>
    internal static float SampleM(int i) => FirstSampleM + i * SampleStepM;
}
