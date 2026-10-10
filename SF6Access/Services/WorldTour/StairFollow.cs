using System;
using System.Collections.Generic;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>Following a flight of narrow stairs the way feet do — one tread at a time, turning when
/// the stair turns.</b> The owner's correction, 2026-10-05: <i>"我要的是导航一定能认出这种窄
/// 梯子，无论他远或者近，另外，梯子也是可能转弯的"</i>. Every reader before this laid ONE
/// STRAIGHT LINE of floor samples and read the flight out of it — fine for a straight stair,
/// blind to the L-shaped one: the line leaves the treads at the corner, meets the wall or the
/// open side, and the flight ends mid-stair or never starts. The live case was exactly that:
/// the player stood at the top of the staircase, the sweep nominated its direction, the straight
/// fine line read <c>0 stairs</c>, and the navigation told a blind player to <i>find an edge to
/// jump down</i>.
///
/// <para><b>This class walks the stair instead of sampling a line across it.</b> From the mouth
/// — the last flat ground before the first riser — it steps one tread at a time: probe the floor
/// one stride ahead; if it is a tread (a step within the same limits <see cref="NarrowPath"/>
/// judges every flight by), step onto it; if it is not — a wall, a hole, the wrong height — try
/// the same step turned through the gentler angles first, and take the first direction that IS
/// a tread. A stair that turns is followed round its corner; a straight one is followed exactly
/// as the line reader read it, because the tread limits are the same numbers.</para>
///
/// <para><b>The rules the owner set are kept whole.</b> The first continuation off the mouth
/// must be a RISER — a flat corridor is not a stair, and a follow that started on one would
/// wander the floor. At least two risers make a flight; one is a kerb, which is
/// <see cref="JumpLinkCatalog"/>'s business and is not duplicated here. A landing may be
/// crossed, but only a few, and never before the first riser. Everything is bounded: the tread
/// count by <see cref="NarrowPath.MaxSteps"/>, the landings by <see cref="MaxLandings"/>. The
/// treads are recorded with their own headings so the descent guard can probe the sides of each
/// tread the way it faces — on a turning stair the cliff turns with it, and a side probed
/// against a stale heading is a side not measured.</para>
///
/// <para><b>Pure, like every rule this project can test.</b> The floor is a delegate
/// <see cref="FloorAt"/> (x, the current standing height, z) → floor height or NaN, so
/// <c>tests/Navigation.Tests</c> can lay out synthetic stairs — straight, L-shaped,
/// switchback — and prove the follow. The engine side supplies the ray, cast from just above
/// the standing height so an up-flight's next tread is inside it exactly as a down-flight's is;
/// nothing here allocates an engine object or asks a query.</para>
/// </summary>
internal static class StairFollow
{
    /// <summary>The most treads one follow may step onto. A follow that is still stepping onto
    /// treads is still ON its stair — every step was judged before it was taken — so this bound
    /// is about cost, not about runaway ground, and it is TWICE the straight reader's
    /// <see cref="NarrowPath.MaxSteps"/> on purpose. A line that runs past its stair is reading
    /// the world beyond it, which is what that smaller cap guards against; but the live flight
    /// of 2026-10-05 fell 5.7 m at roughly a third of a metre a tread — seventeen-odd treads
    /// before its corner — and a follow capped at sixteen would have ended a riser short of the
    /// landing. A link whose far end stands mid-stair ends on the disconnected treads, where
    /// the planner's navmesh question fails, and the route stops exactly where the owner was
    /// left standing: at the mouth, told to find some other way. The follow must reach the
    /// landing the flight arrives at, and thirty-two treads is two storeys of them.</summary>
    internal const int MaxTreads = 32;

    /// <summary>How many LEVEL steps in A ROW may be crossed mid-flight — a landing. Three is
    /// 1.2 m of flat walking between two risers, which is what a landing is; a floor that stays
    /// level forever is not a stair at all, and this is what tells those apart.
    ///
    /// <para><b>It counts a run of level steps, and every riser clears it.</b> Counted over the
    /// whole flight instead, a switchback spends its landing budget crossing the landing in the
    /// middle and is then refused the landing at the bottom — so it ends on the last tread, one
    /// step short of the connected ground the planner's navmesh question needs. Found in the
    /// tests, 2026-10-05, with a stair that turns twice.</para></summary>
    internal const int MaxLandings = 3;

    /// <summary>A follow with fewer risers than this is not a flight. One riser is a kerb —
    /// a single step <see cref="JumpLinkCatalog"/> already handles — and offering it here as
    /// well would give the planner two words for one thing.</summary>
    internal const int MinRisers = 2;

    /// <summary><b>How big a change of height has to be before it is a TREAD rather than the
    /// ground being uneven.</b> The owner's own limits (<see cref="JumpLink.MinRiseM"/>,
    /// <see cref="NarrowPath.MinDescendM"/>) are five centimetres, and they are right for what
    /// they are for: whether a foot may STEP somewhere. They are wrong for what a follow does,
    /// which is decide whether the ground is a stair at all — and five centimetres of uneven
    /// floor is not a stair, it is a floor.
    ///
    /// <para><b>Found live on 2026-10-05, and it is why the follow never found the flight.</b>
    /// Standing on an office floor, the sweep's follow reported
    /// <c>32 risers, 2 turn(s), rise 3.06 m</c> from a mouth to an end thirteen metres away
    /// — a flight that does not exist, walked step by step across ordinary ground whose every
    /// stride happened to differ by six to ten centimetres. Three metres over thirty-two treads
    /// is nine centimetres a tread: a slope, not a stair. The same five-centimetre gate let a
    /// kerb by the player's feet be taken for a mouth, which then spent the whole follow budget
    /// (<c>MaxFollows</c>) so the real flight further out was never walked at all.</para>
    ///
    /// <para><b>Twelve centimetres cannot be reached by uneven ground and cannot be missed by a
    /// real stair.</b> The flight this whole pass exists for falls 5.7 m; even spread over every
    /// one of the <see cref="MaxTreads"/> treads a follow may walk, that is eighteen centimetres
    /// a tread, and a real stair is nearer a third of a metre. Anything gentler than twelve
    /// centimetres a step is ground, and a follow that refuses to walk on ground is a follow
    /// that stops where it should.</para>
    ///
    /// <para>This is a NEW number for a NEW question, added alongside the owner's; nothing that
    /// decides whether a foot may step somewhere changes.</para></summary>
    internal const float MinTreadM = 0.12f;

    /// <summary><b>Is this change of height a tread in the direction <paramref name="down"/>
    /// is following?</b> One answer for the follow and for every pass that looks for the mouth
    /// of a flight, so a mouth found in a sweep's coarse data is a mouth the follow can step
    /// onto — the two disagreed on 2026-10-05 and the follow stopped one tread past the mouth.
    /// The upper limit is the owner's step, unchanged: more than that is a drop or a wall, and
    /// following it would be walking off an edge.</summary>
    internal static bool IsTread(float change, bool down)
        => down ? change <= -MinTreadM && -change <= JumpLink.StepM
                : change >= MinTreadM && change <= JumpLink.StepM;

    /// <summary>The angles tried, in order, when the step straight ahead is not a tread. The
    /// gentler turns come first: a stair corner is usually a quarter turn and a switchback a
    /// half, and trying those first keeps the follow from hopping sideways onto a neighbouring
    /// flight it was never on.</summary>
    internal static readonly float[] TurnsDeg = { 45f, -45f, 90f, -90f, 135f, -135f, 180f };

    /// <summary>The floor under a point of the world, or NaN when there is none within reach.
    /// The <paramref name="standingY"/> is the height the follower is standing at: the engine
    /// casts from just above it, which is what keeps an up-flight's next tread visible while a
    /// down-flight's remains so.</summary>
    internal delegate float FloorAt(float x, float standingY, float z);

    /// <summary>One tread the follow stepped onto, with the heading it was stepped along — the
    /// heading the descent guard must probe the sides against.</summary>
    internal readonly record struct Tread(float X, float Y, float Z, float DirX, float DirZ);

    /// <summary>A flight that was followed rather than read off a line: its mouth (the flat
    /// ground it leaves), its far end, how many risers and turns it took, and every tread with
    /// its own heading.</summary>
    internal sealed class Followed
    {
        internal bool Ok;
        internal bool Down;
        internal float MouthX, MouthY, MouthZ;
        internal float EndX, EndY, EndZ;
        internal int Risers, Turns;
        /// <summary>The signed height change from the mouth to the far end: negative is a
        /// descent, exactly as <see cref="NarrowPath.Flight.SignedM"/> signs a read flight.</summary>
        internal float RiseM;
        internal readonly List<Tread> Treads = new();
    }

    /// <summary>Is this floor a tread continuation from a stand at <paramref name="curY"/>?
    /// 1 = a riser (a step in the followed direction, within the limits every flight is judged
    /// by); 0 = level ground, a landing — allowed only after the first riser and only for
    /// <paramref name="landings"/> level steps IN A ROW (<see cref="MaxLandings"/>, cleared by
    /// the next riser); -1 = not a tread (a wall, a hole, a drop, or a step the wrong way).</summary>
    internal static int Verdict(float f, float curY, bool down, bool firstRiserTaken, int landings)
    {
        if (!float.IsFinite(f)) return -1;
        float change = f - curY;
        if (IsTread(change, down)) return 1;
        // Level ground is a landing — but only ground that is actually LEVEL. Uneven floor a
        // few centimetres up or down is neither a tread nor a landing: it is where a follow
        // that has left its stair stops, instead of walking the floor for thirty treads.
        if (MathF.Abs(change) < JumpLink.MinRiseM && firstRiserTaken && landings < MaxLandings)
            return 0;
        return -1;
    }

    /// <summary>Rotate a heading by <paramref name="degrees"/>, clockwise on the ground plane.</summary>
    internal static (float X, float Z) Rotate(float hx, float hz, float degrees)
    {
        float rad = degrees * MathF.PI / 180f;
        float cos = MathF.Cos(rad), sin = MathF.Sin(rad);
        return (hx * cos - hz * sin, hx * sin + hz * cos);
    }

    /// <summary>The resumable follow. The caller drives <see cref="Step"/> within its own ray
    /// budget — <see cref="Casts"/> counts every probe this follow has spent, so a frame's
    /// worth of rays is the caller's decision, not this class's.</summary>
    internal sealed class Follower
    {
        private readonly FloorAt _floorAt;
        private readonly float _stepM;
        private readonly bool _down;
        private readonly float _mx, _my, _mz;   // the mouth, kept apart from the moving stand
        private float _hx, _hz;
        private float _cx, _cy, _cz;
        private bool _firstRiserTaken;
        private int _landings;              // LEVEL steps in a row since the last riser

        internal bool Done { get; private set; }
        internal int Casts { get; private set; }
        internal int Risers { get; private set; }
        internal int Turns { get; private set; }
        internal readonly List<Tread> Treads = new();

        internal Follower(FloorAt floorAt, float mouthX, float mouthY, float mouthZ,
                          float dirX, float dirZ, bool down, float stepM)
        {
            _floorAt = floorAt;
            _stepM = stepM;
            _down = down;
            _mx = mouthX; _my = mouthY; _mz = mouthZ;
            _cx = mouthX; _cy = mouthY; _cz = mouthZ;
            float len = MathF.Sqrt(dirX * dirX + dirZ * dirZ);
            if (!(len > 1e-3f) || !float.IsFinite(mouthY) || !(stepM > 0f))
            {
                // No heading to follow or no mouth to stand on: the follow is over before it
                // began, and the honest answer is the empty one.
                Done = true;
                _hx = dirX; _hz = dirZ;
                return;
            }
            _hx = dirX / len; _hz = dirZ / len;
        }

        /// <summary>Advance ONE tread (probing the turn directions first if the step ahead is
        /// not a tread). False when the flight has ended — no tread in any direction — or the
        /// bounds are reached.</summary>
        internal bool Step()
        {
            if (Done) return false;
            if (Risers >= MaxTreads) { Done = true; return false; }

            float nx = _cx + _hx * _stepM, nz = _cz + _hz * _stepM;
            float f = Cast(nx, nz);
            int v = Verdict(f, _cy, _down, _firstRiserTaken, _landings);

            if (v < 0)
            {
                // Not a tread ahead. Try the turns, gentle first.
                bool turned = false;
                foreach (float a in TurnsDeg)
                {
                    var (tx, tz) = Rotate(_hx, _hz, a);
                    float px = _cx + tx * _stepM, pz = _cz + tz * _stepM;
                    float f2 = Cast(px, pz);
                    int tv = Verdict(f2, _cy, _down, _firstRiserTaken, _landings);
                    if (tv >= 0)
                    {
                        _hx = tx; _hz = tz; nx = px; nz = pz; f = f2; v = tv;
                        Turns++;
                        turned = true;
                        break;
                    }
                }
                if (!turned) { Done = true; return false; }
            }

            _cx = nx; _cz = nz; _cy = f;
            if (v == 1) { Risers++; _firstRiserTaken = true; _landings = 0; }
            else _landings++;
            Treads.Add(new Tread(_cx, _cy, _cz, _hx, _hz));
            return true;
        }

        private float Cast(float x, float z)
        {
            Casts++;
            return _floorAt(x, _cy, z);
        }

        /// <summary>The flight this follow walked. <see cref="Followed.Ok"/> is false when
        /// fewer than <see cref="MinRisers"/> risers were stepped — a kerb, not a stair.</summary>
        internal Followed ToFollowed() => new()
        {
            Ok = Risers >= MinRisers,
            Down = _down,
            MouthX = _mx, MouthY = _my, MouthZ = _mz,
            EndX = _cx, EndY = _cy, EndZ = _cz,
            Risers = Risers, Turns = Turns,
            RiseM = _cy - _my,
        };
    }

    /// <summary>The whole follow in one call — for the tests, and for any caller that has rays
    /// to spend in one go. The engine callers drive a <see cref="Follower"/> across frames
    /// instead.</summary>
    internal static Followed Follow(FloorAt floorAt, float mouthX, float mouthY, float mouthZ,
                                    float dirX, float dirZ, bool down, float stepM)
    {
        var f = new Follower(floorAt, mouthX, mouthY, mouthZ, dirX, dirZ, down, stepM);
        while (f.Step()) { }
        var r = f.ToFollowed();
        r.Treads.AddRange(f.Treads);
        return r;
    }
}
