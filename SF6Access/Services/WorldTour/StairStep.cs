using System;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>Walking a flight of stairs one spoken step at a time — the pure logic, no engine.</b>
/// The owner's report, 2026-10-05, verbatim: <i>"现在的右手扶强不完善，有那种窄梯子他是没有
/// 带我走上去的，注意，我说的是不用交互的梯子，请修复"</i> — F4's right-hand wall following
/// did not carry them up the narrow metal stairs, the ones walked up with no action button at
/// all.</para>
///
/// <para><b>Why this file exists separately from <see cref="WallFollow"/>.</b> Wall following
/// asks the ground two questions and both are about a DROP: is there a floor under the line
/// ahead (<see cref="NarrowPath"/> read that), and is there floor out to the right or only air
/// (a cliff). A stair is the ground RISE, and a walk with no word for "up" in it walks past
/// every flight it meets — which is exactly what the owner heard. This class is the missing
/// word: given the ground under one straight line, is it a flight of steps the player can walk
/// up, and if so how far does it run?</para>
///
/// <para><b>It answers with a LENGTH, because that is what a sentence needs.</b> The count of
/// risers and the height climbed are worth a log line, but the number spoken to the player is
/// how many metres to walk before the ground is looked at again — and on a stair that number is
/// the whole trick. <see cref="WallFollow"/>'s ordinary step is <c>StepM</c> = 2.5 m and its
/// candidates must have open space ahead for <c>BlockM</c> = <c>StepM + 0.3</c> = 2.8 m; a
/// flight of stairs presents a RISER 0.3 m in, so every ordinary candidate is refused and the
/// walk turns away from the stair instead of up it. A stair step is therefore measured against
/// a different rule — the ground's own climb, not the space above it — and is deliberately
/// SHORT: <see cref="MaxStepM"/> is one tread's worth, so the next sentence comes while the
/// player is still on this tread rather than at the top of a flight nobody measured past.</para>
///
/// <para><b>Both halves are needed, and that is not an accident.</b> <see cref="NarrowPath"/>
/// is handed a line and asked whether it is a flight; <see cref="Walkable"/> is handed the same
/// line's ground and asked whether every metre of it can be stood on. A flight read from a line
/// with a hole in the middle is not a way up, and a line that merely climbs steeply without
/// risers is a ramp — the two answers together are what make a stair.
/// </para>
///
/// <para><b>Both ways, since 2026-10-05.</b> The owner's correction: <i>"导航不能只认得出上行的梯子
/// 或斜坡，也要能认出下行的斜坡或梯子"</i>. This reader was up-only, and a walk with no word for
/// ground FALLING in steps walked past every down-flight exactly as it used to walk past an
/// up-flight. The flight now carries its <see cref="NarrowPath.Flight.Way"/>, and a descent is
/// refused unless its sides were measured and neither is air
/// (<see cref="NarrowPath.SafeToDescend"/>, handed in as <c>sidesSafe</c>).</para>
///
/// <para><b>Nothing here touches the engine</b>, so every rule below is tested without a game
/// running (<c>tests/Navigation.Tests</c>). The casting stays in <see cref="FieldRayCaster"/>.
/// </para>
/// </summary>
internal static class StairStep
{
    /// <summary>How far apart the samples along a stair's line are. 0.4 m, the same figure
    /// <see cref="JumpLinkCatalog"/> and <see cref="StairCatalog"/> both measure a flight
    /// with: a tread is a stride long and a sample inside a tread must not be mistaken for a
    /// riser, so this is as coarse as a flight may honestly be read at.</summary>
    internal const float SampleStepM = 0.4f;

    /// <summary>Where the first sample sits, in metres from the player's feet. About a
    /// stride: a crossing must begin before the body is on it, and a riser 0.4 m away is
    /// something the player is already standing against rather than walking to.</summary>
    internal const float FirstSampleM = 0.5f;

    /// <summary>How many samples one line carries. 0.5 m to 7.3 m — past the five metres a
    /// twelve-step flight occupies, so a flight is never cut in half, and inside the reach at
    /// which a sentence about the ground ahead stops being a sentence about where the player
    /// is standing.</summary>
    internal const int Samples = 18;

    /// <summary><b>How far a stair step is walked.</b> Short on purpose and read from
    /// <see cref="NarrowPath"/>'s own tread: a flight is walked a tread at a time, with the
    /// ground measured again between treads, because the one thing a walk on a stair must not
    /// do is speak a number longer than the line it measured.
    ///
    /// <para><b>And it clears <see cref="MoveGateM"/>.</b> Every step F4 hands over has to be
    /// answered by walking it — nothing is credited until the feet have moved at least that
    /// far — so a stair step SHORTER than the gate would be a sentence the player walks
    /// clean past while F4 still waits for the walk it never got: they would stand on the
    /// next tread and be told again about the one behind them. One tread is comfortably
    /// inside a stride, which is what lets the two agree.</para></summary>
    internal const float MaxStepM = 1.2f;

    /// <summary>Below this there is no point speaking: the player would be asked to shuffle
    /// ten centimetres and listen again. It is <see cref="RoundStep.SqueezeMinM"/>, the
    /// shortest hop this project already considers worth walking.</summary>
    internal const float MinStepM = 0.35f;

    /// <summary><b>How far the player must actually have walked before F4 hands over the next
    /// step</b> — a LOWER bound, not a ceiling. The same number as <c>WallFollow.MoveM</c>,
    /// written here because the pure tests have to hold <see cref="MaxStepM"/> over it and
    /// cannot read a constant that lives behind the engine.</summary>
    internal const float MoveGateM = 0.7f;

    /// <summary>The widest ordinary step F4 may speak — <c>WallFollow.StepM</c>, restated here
    /// for the same reason as <see cref="MoveGateM"/>. <see cref="MaxStepM"/> must be under
    /// it, and the test asserts it.</summary>
    internal const float StepM = 2.5f;

    /// <summary><b>How far a stair may run before it stops being something to walk up now.</b>
    /// The samples reach 7.3 m; this is the honest ceiling on what those samples may CLAIM.
    /// <see cref="NarrowPath"/> refuses outright — rather than truncating — a flight longer
    /// than its reach, which is the right way to be wrong about a stair: half a flight spoken
    /// as if it were the whole of it is a sentence that ends in mid-air.</summary>
    internal const float ReachM = 6.5f;

    /// <summary>How much the ground may rise at the far end of the step and still be part of
    /// the same run of steps. One riser, the <see cref="JumpLink.StepM"/> the owner set on
    /// 2026-10-03 and the same number the ladder, crossing and stair catalogues all climb by.
    /// A rise taller than this is a wall, and the step ends before it.</summary>
    internal static float StepUpM => JumpLink.StepM;

    /// <summary><b>Is the ground under this line a flight of steps the player walks?</b> Up or
    /// down — the owner's correction of 2026-10-05 (<i>"导航不能只认得出上行的梯子或斜坡，也要能认出
    /// 下行的斜坡或梯子"</i>) applies to this reader exactly as it does to the planner's, because a
    /// walk that has no word for ground FALLING in steps walks past a down-flight the same way it
    /// used to walk past an up-flight.
    ///
    /// <para><paramref name="floorAt"/> answers the floor at sample <paramref name="i"/>, NaN
    /// for no floor at all, and <paramref name="measuredAt"/> says whether sample
    /// <paramref name="i"/> was measured: "not measured" and "no floor" are different things
    /// everywhere in this project, and a flight read from an unmeasured sample is a flight
    /// invented in the gap.</para>
    ///
    /// <para>The flight itself is <see cref="NarrowPath"/>'s reading and is not duplicated
    /// here. What this adds is that the line must ALSO be walkable for its whole length —
    /// see <see cref="Walkable"/> — because a stair is not a stair if there is no ground
    /// between the player and it.</para>
    ///
    /// <para>True with <paramref name="walkM"/> = the distance at which the walk stops and
    /// the ground is measured again, which is where the line's first riser is, plus the run
    /// of risers actually walked on a flat tread, capped at <see cref="MaxStepM"/>. Never
    /// past the first fall, hole or unmeasured sample. <paramref name="flight"/> carries
    /// <see cref="NarrowPath.Flight.Way"/>, so the caller knows whether the sentence needs the
    /// word "up" or "down" — and <see cref="NarrowPath.Flight.GoesDown"/> is what it reads.</para></summary>
    internal static bool TryRead(Func<int, float> floorAt, Func<int, bool> measuredAt,
                                int samples, float playerY, float stepM, float reachM,
                                out float walkM, out NarrowPath.Flight flight,
                                bool sidesSafe = true)
    {
        walkM = 0f;
        flight = default;
        if (samples < 3 || !float.IsFinite(playerY) || !(stepM > 0f)) return false;

        if (!NarrowPath.TryRead(floorAt, measuredAt, samples, playerY, stepM, reachM, out flight))
            return false;
        // Nothing solid anywhere between the feet and the first riser: the foot of the stair
        // is not the ground the player is standing on, so this is not their stair.
        if (flight.FirstStep <= 0) return false;
        // A descent is only walked when its sides were measured and neither is air — the same
        // guard the planner's sweep applies (NarrowPath.SafeToDescend), and for the same reason:
        // following a down-flight off a ledge is the mistake that cannot be undone. The caller
        // owns the side probes (WallFollow), so it hands the verdict in; a caller with no side
        // reading at all leaves this true and is trusted to have refused a descent itself.
        if (flight.GoesDown && !sidesSafe) return false;
        if (!Walkable(floorAt, measuredAt, flight.Top, playerY, stepM, out int walkTo)) return false;

        float riserAt = SampleAt(flight.FirstStep, stepM);
        float last = SampleAt(Math.Min(walkTo, flight.Top), stepM);
        float len = last - riserAt;
        if (!(len >= MinStepM))
        {
            // Not even half a tread of firm ground past the first riser: the step below is the
            // last place to stop and look, so the walk stops short of the riser entirely and
            // measures again from there.
            len = riserAt;
        }
        if (!(len >= MinStepM)) return false;

        walkM = MathF.Min(len, MaxStepM);
        return true;
    }

    /// <summary>The distance of sample <paramref name="i"/> from the player's feet.</summary>
    internal static float SampleAt(int i, float stepM) => FirstSampleM + i * stepM;

    /// <summary><b>Can the player stand on every sample from their own feet to
    /// <paramref name="to"/>?</b> True with <paramref name="walkTo"/> = the last sample that
    /// passed, so the caller only ever walks ground that was proved.
    ///
    /// <para>The rule is the one <see cref="WalkSteps"/> refuses a guided leg with:
    /// <see cref="JumpLink.StepM"/> of rise or fall from the sample before. A stair changes level
    /// by less than that at every riser — up OR down, which is what makes a down-flight walkable
    /// here rather than refused as a drop — a tread is level with itself, and a hole (no floor) or
    /// an unmeasured sample stops the walk where it is: the two things a stair can never
    /// legitimately have between the player and their own next step.</para></summary>
    internal static bool Walkable(Func<int, float> floorAt, Func<int, bool> measuredAt, int to,
                                 float playerY, float stepM, out int walkTo)
    {
        walkTo = -1;
        if (to < 1 || !float.IsFinite(playerY)) return false;

        float prev = playerY;
        int last = -1;
        for (int i = 0; i < to; i++)
        {
            if (!measuredAt(i)) break;
            float f = floorAt(i);
            if (!float.IsFinite(f)) break;                  // a hole is not a tread
            if (MathF.Abs(f - prev) > StepUpM) break;       // too tall to step, or a drop away
            last = i;
            prev = f;
        }
        if (last < 0) return false;
        walkTo = last;
        return true;
    }
}
