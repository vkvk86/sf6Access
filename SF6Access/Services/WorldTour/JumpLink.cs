using System;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>Which holes in the world are jumps and which are walls — the owner's own numbers,
/// spoken 2026-10-03: "我们把小于一米的缝隙都算成能跳，小于一点二米的障碍都算成能跳".</b>
///
/// <para>The engine's navmesh answers "walkable" or "not walkable" and nothing in
/// between, so a kerb it did not bake, a ledge a metre up and a metre-wide gap all come
/// back as the same refusal the building-site fence does. These two numbers are what
/// turns the honest refusal back into a route: below them the player gets over it, and
/// a route through it is a route, not a lie.</para>
///
/// <para><b>They are the tester's, not measured.</b> Neither was derived from the
/// collision capsule and neither was confirmed against a jump in game; they are the
/// player's own judgement of what they can clear, which is the only judgement that
/// matters here because the player is the one who has to clear it. If a jump this
/// plans turns out not to be makeable, these are the two numbers to move — not the
/// search around them.</para>
///
/// <para><b>Nothing here touches the engine</b>, so a route built on these numbers can
/// be tested without a game running (<c>tests/Navigation.Tests</c>).</para>
/// </summary>
internal static class JumpLink
{
    /// <summary><b>The widest gap the player jumps.</b> Owner, 2026-10-03. Wider than this
    /// and the far side is somewhere else, however near it looks in a straight line.</summary>
    public const float GapM = 1.0f;

    /// <summary><b>The tallest obstacle the player gets over.</b> Owner, 2026-10-03. It is
    /// a step up onto a ledge, a kerb, a low fence — the things a walk animation refuses
    /// and a jump does not.</summary>
    public const float StepM = 1.2f;

    /// <summary>Below this the two floors are the same floor. The engine walks a level
    /// surface by itself, and calling it a jump would put an instruction on a walk that
    /// needs none — the one thing this file must never do is add noise to a route that
    /// already works.</summary>
    public const float MinRiseM = 0.05f;

    /// <summary>A floor this much higher than the feet is something the player steps up
    /// onto: a jump, not a wall. Anything higher is a climb and belongs to the ladders.</summary>
    public static bool StepsUp(float floorY, float feetY)
    {
        float rise = floorY - feetY;
        return float.IsFinite(rise) && rise >= MinRiseM && rise <= StepM;
    }

    /// <summary>A hole this wide in the floor is crossed, not gone round. A hole of 0 is
    /// not a hole at all — no floor reading at all means "not measured", and an
    /// unmeasured gap must never be offered as a jump.</summary>
    public static bool Crossable(float widthM)
        => float.IsFinite(widthM) && widthM > 0f && widthM <= GapM;

    /// <summary>The far side of a gap is landable when it is within a jump either way:
    /// a ledge a metre below is a hop, one three metres below is a drop the player has to
    /// choose, and the removed descend pipeline already taught us not to guide those.</summary>
    public static bool Landable(float floorY, float feetY)
        => float.IsFinite(floorY) && float.IsFinite(feetY) && MathF.Abs(floorY - feetY) <= StepM;

    /// <summary>Which of the two it is, from the two ends of a crossing — a rise is a step
    /// up, a level span is a gap. Used for the wording, and the wording is the only thing
    /// that tells the player what to do with their hands.</summary>
    public static bool IsStepUp(float fromY, float toY) => toY - fromY >= MinRiseM;
}
