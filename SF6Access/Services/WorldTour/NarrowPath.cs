using System;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>Reading a flight of narrow metal stairs out of the ground under it — the pure logic,
/// no engine.</b> The owner's ask, 2026-10-05: "目标是让导航能带我走上楼梯，另外有时候楼梯是一面
/// 悬崖一面高墙，他也要能认出来." — the navigation must walk them UP a stair, and must
/// recognise the stair that is a cliff on one side and a wall on the other.
///
/// <para><b>Why this is not <see cref="JumpLinkCatalog"/> as it was.</b> That file produced
/// at most ONE crossing — the first riser — and stopped ("a step up … break"). A flight of
/// stairs is a RUN: several risers, a landing, a direction, and — on the metal stairs this is
/// about — open air on one side and a wall on the other. A single link cannot describe any of
/// that, so the navigation could name the first step and never the stair. This class turns the
/// same downward floor samples into a <see cref="Flight"/>: the index range that is one run of
/// steps, the step size it climbs at, and the sides.</para>
///
/// <para><b>Every number here is read from the samples or handed in; none is invented.</b>
/// The two limits that are not geometry — how far a flight may reach, and how far to probe
/// sideways — are the owner's own ask scaled to the player's stride, and they are the numbers
/// to move if a stair is missed or a flat walk is called a stair. The step limits themselves
/// are <see cref="JumpLink"/>'s, which the owner gave on 2026-10-03.</para>
///
/// <para><b>Nothing here touches the engine</b>, so a flight can be tested without a game
/// running (<c>tests/Navigation.Tests</c>). The casting stays in <see cref="FieldRayCaster"/>
/// and the reading in <see cref="JumpLinkCatalog"/>.</para>
///
/// <para><b>Both ways, since 2026-10-05.</b> The reader first looked only for a riser ABOVE
/// the player, so the navigation recognised an up-flight and was blind to a down-flight — the
/// owner's correction: <i>"导航不能只认得出上行的梯子或斜坡，也要能认出下行的斜坡或梯子 … 很窄的
/// 不用交互的，让导航在规划路线的时候能规划进去"</i>. A run now carries a
/// <see cref="Way"/>: the same samples read downward are a descent, and the planner may route
/// down it exactly as it routes up a climb. See <see cref="Flight.GoesDown"/>.</para>
/// </summary>
internal static class NarrowPath
{
    /// <summary>The most steps one flight may be read as. A flight is walked, not jumped,
    /// and the stairs this is for are climbed in a dozen strides at the most; past this the
    /// samples have run away from the stair the player is on and a longer reading would be a
    /// different kind of route, not this one.</summary>
    internal const int MaxSteps = 16;

    /// <summary>How many consecutive samples the floor may keep falling while a step is
    /// still not reached before the run is abandoned. A stair does not descend; a floor that
    /// keeps dropping is a slope or an edge, and reading it as a stair would send the player
    /// up nothing.</summary>
    internal const int MaxFallsBeforeGivingUp = 3;

    /// <summary><b>A RUNNING DOWNHILL flight is still a flight — the owner's ask, 2026-10-05:
    /// "导航不能只认得出上行的梯子或斜坡，也要能认出下行的斜坡或梯子 … 很窄的不用交互的，让导航
    /// 在规划路线的时候能规划进去".</b>
    ///
    /// <para>The reader used to look only for a riser ABOVE the player's own floor, so a
    /// down-flight — a stairway leaving a landing, a ramp descending off a walkway — was
    /// invisible to the plan, and the navigation would walk to the head of it and stop. The same
    /// run is now read downward as well as upward: a sample this far BELOW the last one is the
    /// next tread down, exactly as a sample above it is the next tread up.</para>
    ///
    /// <para><b>The whole of <see cref="JumpLink.StepM"/> and not the 5 cm step threshold</b>:
    /// this is a direction of travel, not the "same floor" tolerance. A tread of a down-flight is
    /// the same riser as a tread of an up-flight, so a sample more than a whisker below the last
    /// is a step down and the run follows it; the fall counter, not this, is what ends a real
    /// drop.</para></summary>
    internal const float MinDescendM = 0.05f;

    /// <summary>And when the run turns back UP by this much, it is a new flight rather than more
    /// of this one: a landing with steps leading up off it is one connection down and another
    /// one up, and reading them as a single run would put a bend in a flight the player walks
    /// straight. Same size as a riser, for the same reason.</summary>
    internal const float MinTurnM = 0.05f;

    /// <summary>One flight of a stair run.</summary>
    internal enum Way
    {
        /// <summary>Nothing was read.</summary>
        None = 0,
        /// <summary>Steps up from where the player stands. The original reading.</summary>
        Up,
        /// <summary>Steps down from where the player stands — the owner's ask.</summary>
        Down,
    }

    /// <summary>One side of the stair: what the sideways ray found.</summary>
    internal enum Side
    {
        /// <summary>Not measured — the probe could not run. Never read as open.</summary>
        Unknown = 0,
        /// <summary>A floor level with the stair out to the probe's reach: you could step
        /// off here. A stair with this on a side is not the cliff-sided kind.</summary>
        Floor,
        /// <summary>No floor within the probe's drop: air. This is the cliff.</summary>
        Drop,
        /// <summary>A wall's worth of contact before the probe's reach, higher than a step:
        /// you cannot step off here. This is the wall.</summary>
        Wall,
    }

    /// <summary>A run of steps read from the floor samples: the samples from the player's
    /// own floor — the run's highest end — to <see cref="Top"/> — its far end, which is the
    /// highest tread of an up-flight and the lowest of a down-flight — changing level at a
    /// riser apiece.
    ///
    /// <para><paramref name="Top"/> is named for where the run ENDS, not for a height, because
    /// since <see cref="Way.Down"/> exists a flight has two ends and only one of them is up.
    /// For an up-flight it is the top; for a down-flight it is the bottom.</para></summary>
    internal readonly record struct Flight(int FirstStep, int Top, int RiserCount,
                                           float RiserM, float TotalRiseM, float RunM,
                                           Way Way = Way.Up)
    {
        /// <summary>Every sample index the flight stands on, its foot samples included.</summary>
        internal bool Covers(int i) => i >= FirstStep && i <= Top;

        /// <summary>The run leaves the player's own floor going DOWN: they walk down it, and
        /// the connection is a descent rather than a climb.</summary>
        internal bool GoesDown => Way == Way.Down;

        /// <summary>The signed change in height over the treads that were counted, from the
        /// player's own floor to the last tread: negative is a flight that goes down. This is
        /// the number a caller walks by, and it is signed on purpose — a down-flight whose
        /// <see cref="TotalRiseM"/> was reported as a positive "rise" would put "up" in the one
        /// sentence that must say "down".</summary>
        internal float SignedM => GoesDown ? -MathF.Abs(TotalRiseM) : MathF.Abs(TotalRiseM);
    }

    /// <summary><b>Read a flight from one floor per sample.</b> <paramref name="floorAt"/>
    /// answers the floor at sample <paramref name="i"/>, or NaN for no floor; it must also
    /// say whether the sample was measured at all (<paramref name="measuredAt"/>), because
    /// "not measured" must never be read as "no floor".
    ///
    /// <para><b>A flight starts at the first riser</b> — the first sample whose floor differs
    /// from the player's own by more than <see cref="JumpLink.MinRiseM"/> and no more than
    /// <see cref="JumpLink.StepM"/>, in EITHER direction — and <b>ends at the last sample that is
    /// still changing level or has come level at the far end.</b> A run that stops changing level
    /// and stays level is the landing; a run that turns back the way it came is left behind. The
    /// floor is the PLAYER'S OWN throughout: a flight is steps away from where they stand, never
    /// from a sample of its own.
    ///
    /// <para><b>Both directions of travel are read</b> (<see cref="Way"/>), which is the owner's
    /// ask of 2026-10-05: "导航不能只认得出上行的梯子或斜坡，也要能认出下行的斜坡或梯子 … 让导航在
    /// 规划路线的时候能规划进去". A sample below the last one by more than
    /// <see cref="MinDescendM"/> and no more than a step is the next tread DOWN, exactly as a
    /// sample above it is the next tread up. The first such step fixes the run's direction, and a
    /// run that turns back on itself — up again after going down, or down again after climbing —
    /// ENDS there, because a landing with stairs off it is one connection each way and a bent
    /// flight is not a flight.</para>
    ///
    /// <para><b>The foot samples are included.</b> The flight's <see cref="Flight.FirstStep"/>
    /// is the first riser and the samples before it are the player's own floor, so a caller
    /// walking to <see cref="Flight.FirstStep"/> is walking the ground they are already on —
    /// which is what makes the route honest.</para>
    ///
    /// <para>False means no flight was found: no riser in range, or only one sample changing
    /// level (a kerb, not a stair — one riser is <see cref="JumpLinkCatalog"/>'s single step and
    /// this class does not duplicate it).</para></summary>
    internal static bool TryRead(Func<int, float> floorAt, Func<int, bool> measuredAt,
                                 int samples, float playerY, float sampleStepM,
                                 float flightReachM, out Flight flight)
    {
        flight = default;
        if (samples < 3 || !float.IsFinite(playerY) || !(sampleStepM > 0f)) return false;

        // Find the first riser: the first sample off the player's own floor by a step — up OR
        // down. Which of the two it is decides the way the whole run goes.
        int start = -1;
        Way way = Way.None;
        float foot = playerY, end = float.NaN;
        for (int i = 0; i < samples; i++)
        {
            if (!measuredAt(i)) continue;               // not measured: keep looking
            float f = floorAt(i);
            if (!float.IsFinite(f)) continue;           // a hole: no riser here
            if (start < 0)
            {
                if (JumpLink.StepsUp(f, playerY)) { start = i; end = f; way = Way.Up; }
                else if (playerY - f >= MinDescendM && playerY - f <= JumpLink.StepM)
                { start = i; end = f; way = Way.Down; }
                continue;
            }
        }
        if (start < 0) return false;

        // Walk on from the riser: keep taking samples that change level the way the run is
        // going, or hold the new level, and stop at the first that turns back or falls away.
        // The last sample held is the far end.
        int far = start, risers = 1, falls = 0;
        float riserSum = 0f;
        for (int i = start + 1; i < samples && risers < MaxSteps; i++)
        {
            if (!measuredAt(i)) break;                  // the run ends where measuring did
            float f = floorAt(i);
            if (!float.IsFinite(f)) break;              // a hole ends the flight (a gap is not a step)
            float change = f - end;                     // positive is a step up, negative a step down
            if (way == Way.Up)
            {
                if (change >= JumpLink.MinRiseM)
                {
                    if (change > JumpLink.StepM) break; // too tall to step: the flight is over
                    risers++; riserSum += change; end = f; far = i; falls = 0;
                }
                else if (change >= -JumpLink.MinRiseM)
                {
                    far = i; falls = 0;                 // level with the last step: the landing
                }
                else if (change <= -MinTurnM)
                {
                    // The run has turned downhill: the steps down from here are a SECOND flight,
                    // and one connection is one flight. This one ends on the level it reached.
                    break;
                }
                else
                {
                    // Falling by less than a riser: a tread may undulate a little.
                    if (++falls > MaxFallsBeforeGivingUp) break;
                }
            }
            else
            {
                if (change <= -MinDescendM)
                {
                    if (-change > JumpLink.StepM) break; // a drop, not a tread: the run is over
                    risers++; riserSum -= change; end = f; far = i; falls = 0;
                }
                else if (change <= JumpLink.MinRiseM)
                {
                    far = i; falls = 0;                 // level with the last step: the landing
                }
                else if (change >= MinTurnM)
                {
                    // ...and the same the other way round: up again after a descent is another
                    // flight, not this one.
                    break;
                }
                else
                {
                    if (++falls > MaxFallsBeforeGivingUp) break;
                }
            }
        }

        float run = (far + 1) * sampleStepM;
        if (run > flightReachM) return false;           // beyond the reach this may not claim
        if (risers < 2) return false;                   // one riser is a step, not a flight

        // The riser is always positive — how tall a tread is — and the height climbed (or
        // dropped) carries the sign of the way the run goes.
        float riser = riserSum / (risers - 1);
        flight = new Flight(start, far, risers, MathF.Abs(riser), MathF.Abs(end - playerY), run, way);
        return true;
    }

    /// <summary><b>What one hand's two measurements mean.</b> Shared by every probe, because
    /// a cliff is an absence and the two readings must always be read together: a wall says
    /// "you cannot step off here" whatever the floor does, and no wall with no floor out
    /// there is air.
    ///
    /// <para><paramref name="wall"/> — a contact classified as a wall within the probe's
    /// reach. <paramref name="floorY"/> — the floor at the probe's far end, NaN when there
    /// is none. Either measurement failing is the caller's business and must never reach
    /// here as a value: <see cref="Side.Unknown"/> is decided there, not guessed at.</para>
    ///
    /// <param name="floorTolM">How far below the step counts as the same floor — the step
    /// limit, so the two agree on what "level with the stair" means.</param></summary>
    internal static Side ClassifySide(bool wall, float floorY, float stepY, float floorTolM)
    {
        if (wall) return Side.Wall;
        if (float.IsFinite(floorY) && float.IsFinite(stepY) && MathF.Abs(floorY - stepY) <= floorTolM)
            return Side.Floor;
        return Side.Drop;
    }

    /// <summary><b>Which side of a flight is which.</b> The owner's ask: a stair may be a
    /// cliff on one side and a wall on the other, and the navigation must recognise it.
    ///
    /// <para><paramref name="left"/> and <paramref name="right"/> are the two probes'
    /// readings (<see cref="Side"/>), and this answers whether the flight is sided at all —
    /// a stair with a wall one way and air the other, which is the one that must be walked
    /// carefully and cannot be left sideways.</para>
    ///
    /// <para>A <see cref="Side.Unknown"/> on either hand leaves the answer FALSE: a probe
    /// that could not run is not evidence of a cliff, and offering a cliff-sided warning on
    /// no evidence would be the same class of lie as reporting a wall that is not there.</para></summary>
    internal static Sides ReadSides(Side left, Side right)
    {
        bool known = left != Side.Unknown && right != Side.Unknown;
        return new Sides(known,
            Cliff: known && (left == Side.Drop || right == Side.Drop),
            Wall: known && (left == Side.Wall || right == Side.Wall),
            CliffLeft: known && left == Side.Drop,
            WallLeft: known && left == Side.Wall,
            WallRight: known && right == Side.Wall,
            CliffRight: known && right == Side.Drop);
    }

    /// <summary>What the two probes said, and which hand each hazard is on.
    /// <see cref="Known"/> is false whenever either hand could not be measured, and EVERY
    /// verdict below is then false — including <see cref="Open"/>. Calling an unmeasured
    /// side "open" would be the same lie as calling an unmeasured gap a crossing.</summary>
    internal readonly record struct Sides(bool Known, bool Cliff, bool Wall,
                                          bool CliffLeft, bool CliffRight,
                                          bool WallLeft, bool WallRight)
    {
        /// <summary>A stair with nothing on either side, and both sides measured: open
        /// ground both ways. Never true of a side that was not measured.</summary>
        internal bool Open => Known && !Cliff && !Wall;

        /// <summary><b>The metal stair the owner described</b> — a cliff on one side and a
        /// wall on the other. The one shape that must be walked along and cannot be
        /// side-stepped out of.</summary>
        internal bool CliffAndWall => Cliff && Wall;
    }

    /// <summary><b>May a route send the player down this flight?</b> The one guard on a
    /// descent, and it is deliberately stricter than the one on a climb.
    ///
    /// <para>Going up, a mis-read stair costs the player a few wasted metres and the ground
    /// is re-measured at the top. Going down, the same mis-read walks a blind player off an
    /// edge they cannot see, and this project's whole history of "look for an edge to drop
    /// from" says the same thing: a fall is the one move that must never be offered on a
    /// guess. So a down-flight is only guided when BOTH of its sides were actually MEASURED —
    /// <see cref="Sides.Known"/> — and neither hand is air. A wall on one side and a floor on
    /// the other is the narrow stair this exists for; air on either side means the flight is a
    /// ledge, and a ledge is not something to be walked down by ear.</para>
    ///
    /// <para>An unmeasured hand is refused here as everywhere else: "not measured" is not
    /// evidence of solid ground.</para></summary>
    internal static bool SafeToDescend(in Sides sides)
        => sides.Known && !sides.Cliff;
}
