using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using REFrameworkNET;
using static SF6Access.Services.WorldTour.LevelRoutePlanner;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>The narrow metal stairs of the scene, found by BEFORE a route is planned — so the plan
/// can go up them.</b> The owner's correction, 2026-10-05: "我要的不是我都站在楼梯前面了他才
/// 认出来，我要的是规划路线的时候就认出来，然后归航音带我走到目标处." It is not enough to
/// recognise a stair when the player is already standing at the foot of it; the stair has to be
/// one of the connections the planner may route through, exactly as a ladder is, so the plan
/// reads "walk to the stair, up it, walk on" and the homing cue leads there.
///
/// <para><b>Why a sweep of the scene and not a line in front of the player.</b>
/// <see cref="JumpLinkCatalog"/> measures one line, from the player toward the objective: it
/// finds a stair only when the player happens to be facing one, which is too late to be planned
/// with and says nothing about the stairs off to the side. A ladder is found by reading the
/// scene's <c>Om011000</c> gimmicks; a metal stair has no gimmick, so it has to be found the
/// only other way there is — by measuring the ground. This sweeps the ground AROUND the player:
/// sixteen directions, a coarse pass to find which ways the world rises, then a fine pass along
/// each of those to read the flight (<see cref="NarrowPath"/>).</para>
///
/// <para><b>It produces links, and proves nothing itself.</b> Each find is an ordinary
/// <see cref="LevelRoutePlanner.Link"/> — the same shape a ladder is — and whether the player
/// can walk to its foot, and whether its top leads to the objective, are questions the planner
/// asks the navmesh, which is the only judge this project trusts. A stair that cannot be walked
/// to is simply never chosen; a stair the navmesh already covers costs one question and is
/// harmless. This is additions only: a route that already worked is left alone.</para>
///
/// <para><b>Cost.</b> Sixteen coarse rays, then up to six fine lines of twenty, then a handful of
/// sideways probes: a few hundred casts, spread over frames at <see cref="RaysPerPump"/> a frame,
/// and not one navmesh query. It runs once per ladder search, in the gap the ladder sweep
/// already leaves, before the planner is built.</para>
///
/// <para><b>Down as well as up, since 2026-10-05.</b> The owner's correction: <i>"导航不能只认得出
/// 上行的梯子或斜坡，也要能认出下行的斜坡或梯子 … 很窄的不用交互的，让导航在规划路线的时候能规划进去"</i>.
/// The coarse pass used to keep only the directions in which the world RISES, so a flight leaving
/// a walkway downward was never measured at all and never entered a plan. It now keeps the ways
/// the world CHANGES LEVEL, either way, and reads the flight downward with the same
/// <see cref="NarrowPath"/> that reads it upward. A down-flight is admitted to the plan only when
/// its sides were both measured and neither is air (<see cref="NarrowPath.SafeToDescend"/>) —
/// walking a blind player off a ledge is the one mistake this project does not make twice.</para>
///
/// <para><b>Turning stairs are WALKED, not crossed with a line, since 2026-10-05.</b> The owner's
/// correction: <i>"我要的是导航一定能认出这种窄梯子，无论他远或者近，另外，梯子也是可能转弯的"</i>.
/// Every pass above lays STRAIGHT lines of floor samples, and a stair that turns leaves the line
/// at the corner — into the wall, or into the open side — so the flight reads as nothing, or as a
/// stub that ends mid-stair. The live case was exactly that: the player stood at the head of a
/// descending stair that turned, the sweep nominated its direction, the straight fine line read
/// <c>0 stairs</c>, and the navigation told a blind player to <i>find an edge to jump down</i>.
/// After the fine pass, the sweep now finds the MOUTH of a flight — the last flat ground before
/// the first riser, in ANY of the sixteen directions, nominated or not — and walks the flight
/// with <see cref="StairFollow"/>: one tread at a time, turning when the stair turns, ending on
/// the landing the flight arrives at. A landing is connected ground; a reading cut short ends on
/// the disconnected treads, where the planner's navmesh question fails and the route stops
/// exactly at the mouth. A followed flight that reads deeper than the straight line's reading of
/// the same stair replaces it — see <see cref="AddFollowed"/>.</para>
/// </summary>
internal static class StairCatalog
{
    /// <summary>How many directions the coarse pass looks. Sixteen is 22.5° apart: fine
    /// enough that a stair is never missed between two of them, coarse enough that the pass
    /// stays one frame's worth of rays.</summary>
    internal const int Directions = 16;

    /// <summary>The coarse pass: from just outside the player's own feet outward. It only has
    /// to answer "does the world change level this way", so it need not reach far.
    ///
    /// <para><b>It must reach past the lip, and that is the 2026-10-05 lesson.</b> Six samples a
    /// stride apart span 0.8 m to 2.8 m, which finds a flight the player is standing AT — but a
    /// player standing at the HEAD of a descending flight has LEVEL ground ahead of them for a
    /// stride or two before the first step down, and the change of level then falls outside a
    /// window that ends at 2.8 m. The direction was never nominated for the fine pass, so the
    /// down-flight was never measured — and "找任务目标的时候找不到下行楼梯" is exactly that.
    /// Ten samples reach 4.4 m, which is over the lip of any landing a player can stand on, and
    /// cost sixteen rays more than before in a pass that is one frame's work.</para>
    ///
    /// <para><b>Widened again to reach the far end of the fine pass, 2026-10-05.</b> Ten samples
    /// still gated a stair whose FIRST change of level is beyond 4.4 m: the fine pass reads out to
    /// 8.1 m, but a direction whose coarse window ends before the stair begins is never nominated
    /// for it. The owner stood on a roof with the descent 6.07 m away and the sweep answered
    /// "0 of 16 directions change level" — the stair was inside the fine pass and outside the
    /// coarse one, so it could not be nominated. Sixteen samples reach 6.8 m, which still costs
    /// sixteen rays in a pass that is one frame's work, and it covers the whole fine window.</para>
    ///
    /// <para><b>Widened to ten metres, 2026-10-05, because the owner's stair was further than
    /// that.</b> Standing three metres from the objective on an office floor, the sweep answered
    /// "1 of 16 directions change level, 0 stairs" — the flight they had walked before was not
    /// inside 6.8 m of where they stood, so no pass could nominate it. Their own words: <i>"我要
    /// 的是导航一定能认出这种窄梯子，无论他远或者近"</i>. Twenty-four samples reach
    /// <see cref="CoarseFromM"/> + 23 x <see cref="CoarseStepM"/> = 10.0 m — twenty-four frames
    /// at <see cref="RaysPerPump"/> rays, under a second, inside the search the player is already
    /// waiting on.</para></summary>
    private const float CoarseFromM = 0.8f;
    private const float CoarseStepM = 0.4f;
    private const int CoarseSamples = 24;

    /// <summary>A direction is worth the fine pass when the world changes level by this much
    /// across the coarse one, in EITHER direction. Below it the ground is level or a kerb,
    /// neither of which is a stair.</summary>
    private const float MinPromisingRiseM = 0.5f;

    /// <summary>The fine pass: from a stride out to the far end of a flight. A dozen steps at
    /// a stride apiece is about five metres, and this reaches past that so a flight is never
    /// cut in half. The coarse pass now nominates directions whose change of level begins as far
    /// out as <see cref="CoarseFromM"/> + (CoarseSamples-1) x <see cref="CoarseStepM"/> = 6.8 m,
    /// so the line must start at the feet and reach past it: <see cref="FineFromM"/> is a stride
    /// and the samples carry it past 10.5 m, covering every window the coarse pass can open —
    /// which now reaches 10.0 m, so a flight seen at the far edge of the coarse pass is not then
    /// lost because the line stops short of it.</summary>
    private const float FineFromM = 0.5f;
    private const float FineStepM = 0.4f;
    private const int FineSamples = 26;

    /// <summary>How many directions the fine pass follows. The nearest changing ways are
    /// the ones a route would use; the rest cost rays for links the planner will not pick.</summary>
    private const int MaxFineDirs = 6;

    /// <summary>How many mouths the follow pass may walk.
    ///
    /// <para><b>Eight, not two — found live on 2026-10-05.</b> The mouths were taken in order of
    /// distance and only the nearest two were walked, so the ground by the player's own feet was
    /// always first in line: a kerb six centimetres high, a slope across an office floor. Both
    /// were walked, both stopped after one riser, and the budget was spent — the flight the owner
    /// had walked before was further out and was never walked at all. The log said <c>0 of 2
    /// followed flight(s)</c> every single pass.</para>
    ///
    /// <para>A mouth that is not a stair costs almost nothing now that <see cref="
    /// StairFollow.MinTreadM"/> refuses to walk on uneven ground: one riser, a landing or two,
    /// and the follow stops. So the budget can be spent where the answer is. Eight walks a stair
    /// at hand, the flight beside it, and the ones beyond — an up-flight and a down-flight sharing
    /// a landing is the shape a city block is full of, and all of them are connections a route
    /// may want.</para></summary>
    private const int MaxFollows = 8;

    /// <summary>How many mouths one direction may offer. <b>One was not enough, 2026-10-05:</b>
    /// the scan stopped at the first change of height it met, so a kerb a metre from the feet hid
    /// the flight four metres further along the very same ray. A kerb is <see cref="
    /// JumpLinkCatalog"/>'s business and is not a mouth we want — but we must walk PAST it to
    /// find the stair behind it.</summary>
    private const int MouthsPerDir = 3;

    /// <summary>The follow's stride — deliberately the coarse pass's own stride, so the first
    /// probe from a mouth found in the coarse data lands exactly on the coarse sample that
    /// showed the first riser: the first step of a follow is guaranteed by construction, not
    /// hoped for.</summary>
    private const float FollowStepM = CoarseStepM;

    /// <summary>Rays per frame. The coarse pass is one frame; the fine pass and the sideways
    /// probes follow over a few, inside the search the player is already waiting on.</summary>
    private const int RaysPerPump = 16;

    /// <summary>Every how-manyth step of a flight is probed sideways. The reading is about the
    /// stair as a whole, not about one tread, so a stride of steps is enough.</summary>
    private const int SideProbeStride = 3;

    /// <summary>Two finds this close at the foot are one stair seen from two directions.</summary>
    private const float SameStairM = 1.5f;

    /// <summary>The floor probe: it starts this far above the step and looks this far down,
    /// the same geometry <see cref="JumpLinkCatalog"/> uses.
    ///
    /// <para><b>The drop has to reach the bottom of a DESCENT, and 4.0 m did not.</b> The probe
    /// starts <see cref="ProbeAboveM"/> above the player's own floor and looks down from there, so
    /// a 4 m drop reaches only 2.4 m BELOW the feet. A down-flight sampled on the coarse window —
    /// which now runs out to 4.4 m — is easily lower than that by its far end: a landing a flight
    /// of steps leads down to sits a storey below, and every sample over it came back "no floor",
    /// which reads as a HOLE and ends the run before the descent is ever recognised. 7.0 m reaches
    /// 5.4 m below the feet: a full flight down and its landing, and still short of a real cliff,
    /// which is what the drop counter (<see cref="NarrowPath.MaxFallsBeforeGivingUp"/>) is for.</para>
    ///
    /// <para><b>Raised to 9.0 m on 2026-10-05, because 5.4 m was measured too tight live.</b> The
    /// owner stood on a roof with the floor to reach 5.70 m below their feet (73.7 down to 68.0),
    /// and the landing sat just outside a 5.4 m reach — so the sample over it came back "no floor"
    /// and the descent was never seen. Nine metres reaches 7.4 m below the feet: a full storey plus
    /// a flight, which is what a roof or an upper landing actually drops by. It stays well short of
    /// the kind of fall <see cref="NarrowPath.MaxFallsBeforeGivingUp"/> exists to reject.</para>
    ///
    /// <para>Raising this only ever finds MORE floor — it can never invent one — so nothing that
    /// reads correctly at 4.0 m changes its reading at 9.0 m; a descent simply stops looking like
    /// a hole.</para></summary>
    private const float ProbeAboveM = 1.6f;
    private const float ProbeDropM = 9.0f;

    /// <summary>How far out a side of a stair is probed, and how far below the step still
    /// counts as floor beside it. Both as in <see cref="JumpLinkCatalog"/>.</summary>
    private const float SideReachM = 1.2f;
    private const float SideFloorM = JumpLink.StepM;

    /// <summary>Where a sideways probe leaves the step: the middle of a standing body.</summary>
    private const float WaistAboveFloorM = 0.9f;

    private enum Phase { Idle, Coarse, Fine, Follow, Sides, Done }

    private static readonly List<Link> Found = new();
    private static readonly List<NarrowPath.Sides> Sides = new();
    private static readonly float[] Coarse = new float[Directions * CoarseSamples];
    private static readonly bool[] CoarseOk = new bool[Directions * CoarseSamples];
    // EVERY array below is indexed by DIRECTION (0 .. Directions-1), not by the fine
    // slot. The two numbers are different things and confusing them is what broke the
    // sweep on 2026-10-05: FineDir[k] holds a direction, so a slot-sized array is read
    // out of range the moment any direction past the sixth is the one that rises — an
    // IndexOutOfRangeException thrown inside the per-frame tick, every frame, which
    // took the homing cue down with it. Sixteen directions is 320 floats: nothing.
    private static readonly float[] Fine = new float[Directions * FineSamples];
    private static readonly bool[] FineOk = new bool[Directions * FineSamples];
    private static readonly int[] FineDir = new int[MaxFineDirs];
    private static readonly NarrowPath.Flight[] Flights = new NarrowPath.Flight[Directions];
    private static readonly bool[] HasFlight = new bool[Directions];
    private static readonly NarrowPath.Side[] ProbeLeft = new NarrowPath.Side[Directions * FineSamples];
    private static readonly NarrowPath.Side[] ProbeRight = new NarrowPath.Side[Directions * FineSamples];

    private static Phase _phase = Phase.Idle;
    private static Spot _player;
    private static int _coarseAt, _fineCount, _fineDir, _fineAt, _sideSlot, _sideDir, _followAt;
    private static bool _logged;

    /// <summary>One follow in flight: the follower itself, the mouth it stands on (for the
    /// same-stair dedup), and the per-hand tallies of every side probed along its treads. The
    /// sides are tallied AS the treads are stepped onto, each against the TREAD's own heading —
    /// on a turning stair the cliff turns with it, and a side probed against a stale heading is
    /// a side not measured.</summary>
    private sealed class FollowJob
    {
        internal readonly StairFollow.Follower F;
        internal readonly Spot Mouth;
        internal int LFloor, LDrop, LWall, RFloor, RDrop, RWall;
        internal FollowJob(StairFollow.Follower f, Spot mouth) { F = f; Mouth = mouth; }
    }

    private static readonly List<FollowJob> _followJobs = new();
    private static readonly List<(StairFollow.Followed F, NarrowPath.Sides Sides)> _followed = new();

    /// <summary>The stairs found, as planner links. Empty until <see cref="Sweep"/> has returned true.</summary>
    internal static IReadOnlyList<Link> Links => Found;

    /// <summary>Per link, same order as <see cref="Links"/>: which hand the cliff and the wall
    /// are on. Default (nothing known) when the index is stale.</summary>
    internal static NarrowPath.Sides SidesOf(int link) => link >= 0 && link < Sides.Count ? Sides[link] : default;

    /// <summary>How many stairs the last sweep found. Zero is an honest answer: no stair was
    /// measured, and the planner then routes over ladders and crossings alone.</summary>
    internal static int Count => Found.Count;

    /// <summary>Drop the sweep. A new search measures the ground again: the player has moved,
    /// or the objective has, and a stair belongs to where the player stands.</summary>
    internal static void Reset()
    {
        Found.Clear();
        Sides.Clear();
        _followJobs.Clear();
        _followed.Clear();
        _phase = Phase.Idle;
        _coarseAt = _fineCount = _fineDir = _fineAt = _sideSlot = _sideDir = _followAt = 0;
        RefusedDown = 0;
        _logged = false;
    }

    /// <summary>Starts a sweep around the player. Called where <see cref="LadderCatalog.BeginSweep"/>
    /// is called: both are the connections a search may route through, and both are read before
    /// the planner is built.</summary>
    internal static void BeginSweep(Spot player)
    {
        Reset();
        _player = player;
        _phase = Phase.Coarse;
        for (int i = 0; i < Coarse.Length; i++) { Coarse[i] = float.NaN; CoarseOk[i] = false; }
        for (int i = 0; i < Fine.Length; i++) { Fine[i] = float.NaN; FineOk[i] = false; }
        for (int i = 0; i < HasFlight.Length; i++) { HasFlight[i] = false; Flights[i] = default; }
        for (int i = 0; i < ProbeLeft.Length; i++) { ProbeLeft[i] = ProbeRight[i] = NarrowPath.Side.Unknown; }
    }

    /// <summary>Advances the sweep by a batch of rays; true once every stair in reach has been
    /// measured and <see cref="Links"/> is final.</summary>
    internal static bool Sweep()
    {
        switch (_phase)
        {
            case Phase.Coarse: PumpCoarse(); break;
            case Phase.Fine: PumpFine(); break;
            case Phase.Follow: PumpFollow(); break;
            case Phase.Sides: PumpSides(); break;
            case Phase.Done: case Phase.Idle: return true;
        }
        return _phase == Phase.Done;
    }

    /// <summary>True while a sweep is running, so a caller can hold before reading <see cref="Links"/>.</summary>
    internal static bool Sweeping => _phase != Phase.Done && _phase != Phase.Idle;

    // ---- pass 1: which ways does the world rise ----

    private static void PumpCoarse()
    {
        int budget = RaysPerPump;
        while (budget-- > 0 && _coarseAt < Coarse.Length)
        {
            int i = _coarseAt++;
            int d = i / CoarseSamples, s = i % CoarseSamples;
            float t = CoarseFromM + s * CoarseStepM;
            Dir(d, out float dx, out float dz);
            if (FieldRayCaster.TryFloorBelow(_player.X + dx * t, _player.Y + ProbeAboveM,
                                             _player.Z + dz * t, ProbeDropM, out float floor))
            {
                Coarse[i] = floor;
                CoarseOk[i] = true;
            }
            else { Coarse[i] = float.NaN; CoarseOk[i] = false; }
        }
        if (_coarseAt < Coarse.Length) return;

        // Keep the directions that CHANGE LEVEL most: the ways a stair could be, whether it
        // climbs away from the player or descends away from them. Ranking by the size of the
        // change and not by its sign is the whole of the 2026-10-05 fix — a down-flight is a
        // connection a route may take, and a coarse pass that only kept the rising ways could
        // never have seen one.
        var ranked = new List<(int dir, float change)>();
        for (int d = 0; d < Directions; d++)
        {
            float first = float.NaN, last = float.NaN;
            for (int s = 0; s < CoarseSamples; s++)
            {
                int i = d * CoarseSamples + s;
                if (!CoarseOk[i]) continue;
                if (!float.IsFinite(first)) first = Coarse[i];
                last = Coarse[i];
            }
            if (float.IsFinite(first) && float.IsFinite(last) && MathF.Abs(last - first) >= MinPromisingRiseM)
                ranked.Add((d, MathF.Abs(last - first)));
        }
        ranked.Sort((a, b) => b.change.CompareTo(a.change));
        _fineCount = Math.Min(ranked.Count, MaxFineDirs);
        for (int k = 0; k < _fineCount; k++) FineDir[k] = ranked[k].dir;
        _fineDir = _fineAt = 0;
        LogProfile();
        CollectMouths();
        _phase = _fineCount > 0 ? Phase.Fine
               : _followJobs.Count > 0 ? Phase.Follow
               : Finish();
    }

    /// <summary><b>The ground around the player, written to the log as it was measured.</b> One
    /// line per direction whose ground is not flat, every sample from the feet outwards, so a
    /// stair that was not read can be SEEN in the log and the reading that missed it proved —
    /// the difference between "the flight is not there" and "the flight is there and we looked
    /// the wrong way" is the whole of the repair, and only the samples can tell them apart.
    /// Flat directions are left out: sixteen lines a pass, every few seconds, is a log nobody
    /// reads.</summary>
    private static void LogProfile()
    {
        for (int d = 0; d < Directions; d++)
        {
            float lo = float.NaN, hi = float.NaN;
            for (int s = 0; s < CoarseSamples; s++)
            {
                int i = d * CoarseSamples + s;
                if (!CoarseOk[i]) continue;
                if (!float.IsFinite(lo) || Coarse[i] < lo) lo = Coarse[i];
                if (!float.IsFinite(hi) || Coarse[i] > hi) hi = Coarse[i];
            }
            if (!float.IsFinite(lo) || hi - lo < ProfileFlatM) continue;
            var sb = new StringBuilder();
            for (int s = 0; s < CoarseSamples; s++)
            {
                int i = d * CoarseSamples + s;
                sb.Append(' ').Append(CoarseOk[i]
                    ? Coarse[i].ToString("F2", CultureInfo.InvariantCulture) : "hole");
            }
            API.LogInfo(FormattableString.Invariant(
                $"[SF6Access] Stair profile: direction {d}, {hi - lo:F2} m of change, feet outwards every {CoarseStepM:F2} m:{sb}"));
        }
    }

    /// <summary>A direction this flat has nothing to say — see <see cref="LogProfile"/>.</summary>
    private const float ProfileFlatM = 0.3f;

    // ---- pass 2: read the flight along each rising way ----

    private static void PumpFine()
    {
        int budget = RaysPerPump;
        while (budget-- > 0 && _fineDir < _fineCount)
        {
            // The samples are stored under the DIRECTION they were cast along, because
            // that is how every later pass reads them (Ends, MajoritySides, ProbeOne).
            // Writing them under the fine slot instead is the second half of the
            // 2026-10-05 fault: the flight would then be read back out of slots that
            // were never measured.
            int d = FineDir[_fineDir];
            int slot = d * FineSamples + _fineAt;
            float t = FineFromM + _fineAt * FineStepM;
            Dir(d, out float dx, out float dz);
            if (FieldRayCaster.TryFloorBelow(_player.X + dx * t, _player.Y + ProbeAboveM,
                                             _player.Z + dz * t, ProbeDropM, out float floor))
            {
                Fine[slot] = floor;
                FineOk[slot] = true;
            }
            else { Fine[slot] = float.NaN; FineOk[slot] = false; }

            if (++_fineAt < FineSamples) continue;
            _fineAt = 0;
            HasFlight[d] = NarrowPath.TryRead(s => Fine[d * FineSamples + s], s => FineOk[d * FineSamples + s],
                                              FineSamples, _player.Y, FineStepM,
                                              FineFromM + FineSamples * FineStepM, out var flight);
            Flights[d] = flight;
            if (!HasFlight[d])
            {
                // A direction the coarse pass said changes level, whose straight line reads no
                // flight — that is the TURNING-STAIR SIGNATURE: the line leaves the treads at the
                // corner and meets wall or air. The samples themselves go to the log, so "the
                // stair is there and was not read" can be proved from the log alone.
                var sb = new StringBuilder();
                for (int s = 0; s < FineSamples; s++)
                    sb.Append(' ').Append(FineOk[d * FineSamples + s]
                        ? Fine[d * FineSamples + s].ToString("F2", CultureInfo.InvariantCulture) : "NaN");
                API.LogInfo(FormattableString.Invariant(
                    $"[SF6Access] Stair sweep: straight read found no flight in direction {d}; line:{sb}"));
            }
            _fineDir++;
        }
        if (_fineDir < _fineCount) return;

        _sideDir = _sideSlot = 0;
        _phase = _followJobs.Count > 0 ? Phase.Follow
               : AnyFlight() ? Phase.Sides
               : Finish();
    }

    private static bool AnyFlight()
    {
        for (int d = 0; d < _fineCount; d++) if (HasFlight[FineDir[d]]) return true;
        return false;
    }

    // ---- pass 2b: walk the flight from its mouth, turning when the stair turns ----

    /// <summary><b>Find the mouths of flights in the coarse data — every direction, nominated or
    /// not.</b> A mouth is the last flat sample before the first RISER-like change: a step within
    /// the tread limits, up or down. The first probe a follow makes from that mouth lands exactly
    /// on the sample that showed the riser (the strides match), so the follow's first step is a
    /// tread by construction. A cliff is not a mouth — a change beyond the step limit is a drop,
    /// and following it would be walking off an edge. A gap in the samples resets the scan: the
    /// ground after a hole is not known to be a continuation of the ground before it.</summary>
    private static void CollectMouths()
    {
        _followJobs.Clear();
        var mouths = new List<(float x, float y, float z, float dx, float dz, bool down, float dist)>();
        for (int d = 0; d < Directions; d++)
        {
            Dir(d, out float dx, out float dz);
            // The virtual first pair is the player's own floor against sample 0: a player
            // standing AT a stairhead has the first tread inside the first coarse stride.
            float prev = _player.Y;
            bool prevOk = true;
            int taken = 0;
            for (int s = 0; s < CoarseSamples; s++)
            {
                int i = d * CoarseSamples + s;
                if (!CoarseOk[i]) { prevOk = false; continue; }
                float f = Coarse[i];
                if (prevOk)
                {
                    float change = f - prev;
                    if (StairFollow.IsTread(change, change < 0f))
                    {
                        // s == 0: the flat ground is the player's own stand, half a stride back.
                        float t = s == 0 ? CoarseFromM - CoarseStepM : CoarseFromM + (s - 1) * CoarseStepM;
                        mouths.Add((_player.X + dx * t, s == 0 ? _player.Y : prev, _player.Z + dz * t,
                                    dx, dz, change < 0f, t));
                        // KEEP SCANNING THIS DIRECTION. One mouth per ray spent the whole budget
                        // on the ground by the player's feet and never reached the flight beyond
                        // it — a kerb in front of a stair is not a reason to stop looking.
                        if (++taken >= MouthsPerDir) break;
                    }
                }
                prev = f;
                prevOk = true;
            }
        }
        mouths.Sort((a, b) => a.dist.CompareTo(b.dist));
        foreach (var m in mouths)
        {
            if (_followJobs.Count >= MaxFollows) break;
            bool dup = false;
            var spot = new Spot(m.x, m.y, m.z);
            for (int j = 0; j < _followJobs.Count && !dup; j++)
                if (Near(_followJobs[j].Mouth, spot)) dup = true;
            if (dup) continue;   // one stair seen from two directions is one stair to walk
            _followJobs.Add(new FollowJob(
                new StairFollow.Follower(FloorOf, m.x, m.y, m.z, m.dx, m.dz, m.down, FollowStepM), spot));
        }
    }

    /// <summary>The floor under a point for a follow: the same probe geometry every other pass of
    /// this sweep uses — from just above the standing height, down the full drop — so a tread the
    /// coarse and fine passes could see, the follow can see too.</summary>
    private static float FloorOf(float x, float standingY, float z)
        => FieldRayCaster.TryFloorBelow(x, standingY + ProbeAboveM, z, ProbeDropM, out float floor)
            ? floor : float.NaN;

    private static void PumpFollow()
    {
        int budget = RaysPerPump;
        while (budget > 0 && _followAt < _followJobs.Count)
        {
            var job = _followJobs[_followAt];
            var f = job.F;
            int before = f.Casts;
            bool stepped = f.Step();
            budget -= f.Casts - before;
            if (!stepped) { Keep(job); _followAt++; continue; }
            var t = f.Treads[f.Treads.Count - 1];
            if ((f.Treads.Count - 1) % SideProbeStride == 0)
            {
                Tally(ProbeSideAt(t.X, t.Y, t.Z, t.DirX, t.DirZ, true),
                      ref job.LFloor, ref job.LDrop, ref job.LWall);
                Tally(ProbeSideAt(t.X, t.Y, t.Z, t.DirX, t.DirZ, false),
                      ref job.RFloor, ref job.RDrop, ref job.RWall);
                budget -= 4;
            }
        }
        if (_followAt < _followJobs.Count) return;
        _phase = AnyFlight() ? Phase.Sides : Finish();
    }

    /// <summary>Keep one finished follow: too short is dropped (a kerb is
    /// <see cref="JumpLinkCatalog"/>'s business), and a real flight is kept with the sides its own
    /// treads reported — written to the log in full, because a followed flight is the one reading
    /// this whole pass exists for and it has to be provable from the log alone.</summary>
    private static void Keep(FollowJob job)
    {
        var r = job.F.ToFollowed();
        r.Treads.AddRange(job.F.Treads);
        if (!r.Ok)
        {
            API.LogInfo(FormattableString.Invariant(
                $"[SF6Access] Stair follow: stopped at ({r.EndX:F1}, {r.EndY:F1}, {r.EndZ:F1}) after {r.Risers} riser(s) - too short to be a stair"));
            return;
        }
        var sides = NarrowPath.ReadSides(HandVerdict(job.LFloor, job.LDrop, job.LWall),
                                         HandVerdict(job.RFloor, job.RDrop, job.RWall));
        _followed.Add((r, sides));
        API.LogInfo(FormattableString.Invariant(
            $"[SF6Access] Stair follow: mouth ({r.MouthX:F1}, {r.MouthY:F1}, {r.MouthZ:F1}) -> end ({r.EndX:F1}, {r.EndY:F1}, {r.EndZ:F1}); {r.Risers} risers, {r.Turns} turn(s), rise {r.RiseM:F2} m"));
    }

    // ---- pass 3: what is beside each flight ----

    private static void PumpSides()
    {
        int budget = RaysPerPump / 2;
        while (budget-- > 0 && _sideDir < _fineCount)
        {
            int d = FineDir[_sideDir];
            if (!HasFlight[d]) { _sideDir++; _sideSlot = 0; continue; }
            var f = Flights[d];
            int step = f.FirstStep + _sideSlot * SideProbeStride;
            if (step > f.Top) { _sideDir++; _sideSlot = 0; continue; }

            ProbeOne(d, step, true);
            ProbeOne(d, step, false);
            _sideSlot++;
        }
        if (_sideDir < _fineCount) return;
        _phase = Finish();
    }

    private static void ProbeOne(int d, int stepIdx, bool left)
    {
        float t = FineFromM + stepIdx * FineStepM;
        Dir(d, out float dx, out float dz);
        var side = ProbeSideAt(_player.X + dx * t, Fine[d * FineSamples + stepIdx], _player.Z + dz * t,
                               dx, dz, left);
        if (left) ProbeLeft[d * FineSamples + stepIdx] = side;
        else ProbeRight[d * FineSamples + stepIdx] = side;
    }

    /// <summary><b>What is beside one standing point, on one hand.</b> The two measurements every
    /// catalogue makes — a cast across at waist height for a wall, a floor probe at the reach for
    /// ground — folded through the shared classifier, so a side read here cannot disagree with a
    /// side read anywhere else. The heading is the point's OWN heading: on a turning stair the
    /// cliff turns with it, and a side probed against a stale heading is a side not measured.</summary>
    internal static NarrowPath.Side ProbeSideAt(float px, float floorY, float pz, float hx, float hz, bool left)
    {
        if (!float.IsFinite(floorY)) return NarrowPath.Side.Unknown;
        float sx = left ? -hz : hz;
        float sz = left ? hx : -hx;
        var dir = new FieldDirectionService.FlatDir(sx, sz, true);

        if (!FieldRayCaster.TryCastAcross(px, floorY + WaistAboveFloorM, pz, dir, SideReachM, out var hit))
            return NarrowPath.Side.Unknown;
        if (!FieldRayCaster.TryFloorBelow(px + sx * SideReachM, floorY + ProbeAboveM,
                                          pz + sz * SideReachM, ProbeDropM, out float far))
            return NarrowPath.Side.Unknown;

        bool wall = hit.Hit && hit.Class == HitClass.Wall
                    && float.IsFinite(hit.Distance) && hit.Distance <= SideReachM;
        return NarrowPath.ClassifySide(wall, far, floorY, SideFloorM);
    }

    // ---- the finds ----

    private static Phase Finish()
    {
        for (int d = 0; d < _fineCount; d++)
        {
            if (!HasFlight[FineDir[d]]) continue;
            var f = Flights[FineDir[d]];
            var (low, high) = Ends(FineDir[d], f);
            var sides = MajoritySides(FineDir[d], f);
            // A DESCENT is only offered when its sides were measured and neither is air. Going
            // up, a mis-read flight costs a few metres; going down it walks a blind player off an
            // edge, and that is the one mistake this project does not make twice. The refusal is
            // logged rather than silent, so "the stair is there and unused" can be told from
            // "the stair was never found" — see Describe.
            if (f.GoesDown && !NarrowPath.SafeToDescend(sides)) { RefusedDown++; continue; }
            Add(low, high, sides);
        }
        // The followed flights, after the straight lines': a followed flight that reaches deeper
        // than the line's reading of the same stair replaces it, and its sides replace the line's
        // with the ones measured along the turning path.
        foreach (var (r, sides) in _followed)
        {
            if (r.Down && !NarrowPath.SafeToDescend(sides)) { RefusedDown++; continue; }
            AddFollowed(r, sides);
        }
        _phase = Phase.Done;
        if (!_logged) { _logged = true; API.LogInfo(Describe()); }
        return Phase.Done;
    }

    /// <summary><b>How many down-flights the last sweep measured but would not route down.</b>
    /// A descent whose sides are air, or were not measured at all, is refused
    /// (<see cref="NarrowPath.SafeToDescend"/>). Counted so the log can say so: a stair that was
    /// found and deliberately not used looks exactly like a stair that was missed, and the two
    /// want opposite repairs.</summary>
    internal static int RefusedDown { get; private set; }

    /// <summary>The two standing points of a flight: its foot — the last sample on the floor
    /// the player is standing on, so the walk to it is the walk the player is already on — and
    /// its top, the last tread it reached.</summary>
    private static (Spot low, Spot high) Ends(int d, NarrowPath.Flight f)
    {
        int foot = -1;
        for (int j = f.FirstStep - 1; j >= 0; j--)
            if (FineOk[d * FineSamples + j] && float.IsFinite(Fine[d * FineSamples + j])) { foot = j; break; }

        float footY = foot >= 0 ? Fine[d * FineSamples + foot] : _player.Y;
        float footT = foot >= 0 ? FineFromM + foot * FineStepM : 0f;
        float topT = FineFromM + f.Top * FineStepM;
        Dir(d, out float dx, out float dz);
        return (new Spot(_player.X + dx * footT, footY, _player.Z + dz * footT),
                new Spot(_player.X + dx * topT, Fine[d * FineSamples + f.Top], _player.Z + dz * topT));
    }

    /// <summary>The stair's sides, taken over every step that was probed: a cliff on every
    /// tread but one is the stair's cliff, and one odd tread must not rename it.</summary>
    private static NarrowPath.Sides MajoritySides(int d, NarrowPath.Flight f)
    {
        int floor = 0, drop = 0, wall = 0;
        for (int s = f.FirstStep; s <= f.Top; s += SideProbeStride)
        {
            Tally(ProbeLeft[d * FineSamples + s], ref floor, ref drop, ref wall);
            Tally(ProbeRight[d * FineSamples + s], ref floor, ref drop, ref wall);
        }
        // "Which hand" is read per hand over its own probes, so a stair can be a cliff on the
        // left and a wall on the right — the shape the owner asked to have recognised.
        var left = Hand(d, f, ProbeLeft);
        var right = Hand(d, f, ProbeRight);
        return NarrowPath.ReadSides(left, right);
    }

    private static void Tally(NarrowPath.Side s, ref int floor, ref int drop, ref int wall)
    {
        switch (s)
        {
            case NarrowPath.Side.Floor: floor++; break;
            case NarrowPath.Side.Drop: drop++; break;
            case NarrowPath.Side.Wall: wall++; break;
        }
    }

    private static NarrowPath.Side Hand(int d, NarrowPath.Flight f, NarrowPath.Side[] side)
    {
        int floor = 0, drop = 0, wall = 0;
        for (int s = f.FirstStep; s <= f.Top; s += SideProbeStride) Tally(side[d * FineSamples + s], ref floor, ref drop, ref wall);
        return HandVerdict(floor, drop, wall);
    }

    /// <summary>One hand's verdict from its tallies: a hazard present anywhere wins, and a hand
    /// that was never measured anywhere stays unknown — "not measured" is never read as ground.</summary>
    private static NarrowPath.Side HandVerdict(int floor, int drop, int wall)
    {
        if (drop >= floor && drop >= wall && drop > 0) return NarrowPath.Side.Drop;
        if (wall >= floor && wall >= drop && wall > 0) return NarrowPath.Side.Wall;
        if (floor > 0) return NarrowPath.Side.Floor;
        return NarrowPath.Side.Unknown;
    }

    private static void Add(Spot low, Spot high, NarrowPath.Sides sides)
    {
        // One stair seen from two directions is one stair. Both ends are compared now, not just
        // the lower one: a down-flight and an up-flight whose foot samples happen to sit close
        // together are two different connections, and folding them into one would delete the
        // descent this exists to keep.
        for (int i = 0; i < Found.Count; i++)
        {
            if (Near(Found[i].Low, low) && Near(Found[i].High, high)) return;
            if (Near(Found[i].Low, high) && Near(Found[i].High, low)) return;
        }
        // The ends are named by HEIGHT — Low is the lower standing point, so the planner's two
        // directions of travel stay "up" and "down" and nothing downstream has to know which way
        // the ground happened to run away from the player who measured it.
        var link = high.Y < low.Y ? new Link(high, low, false, true) : new Link(low, high, false, true);
        Found.Add(link);
        Sides.Add(sides);
    }

    private static bool Near(Spot a, Spot b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return dx * dx + dz * dz <= SameStairM * SameStairM;
    }

    /// <summary><b>One link for a FOLLOWED flight, merged into the finds the straight lines
    /// already made.</b> The same stair read two ways — a line that stopped at the corner, a
    /// follow that walked round it — is ONE stair, and the deeper reading is the true one: the
    /// line's link ends mid-flight, on the disconnected treads the planner cannot verify, and a
    /// route through it stops exactly where the owner was left standing. A find is the same
    /// stair when both ends match, or when it shares the mouth end and its other end stands on
    /// the follow's treads — the corner the line died on IS a tread of the followed flight.</summary>
    private static void AddFollowed(StairFollow.Followed r, NarrowPath.Sides sides)
    {
        var mouth = new Spot(r.MouthX, r.MouthY, r.MouthZ);
        var end = new Spot(r.EndX, r.EndY, r.EndZ);
        var low = end.Y < mouth.Y ? end : mouth;
        var high = end.Y < mouth.Y ? mouth : end;
        float rise = MathF.Abs(high.Y - low.Y);
        for (int i = 0; i < Found.Count; i++)
        {
            var e = Found[i];
            bool same = (Near(e.Low, low) && Near(e.High, high)) || (Near(e.Low, high) && Near(e.High, low))
                     || (Near(e.High, high) && OnTreads(r, e.Low))
                     || (Near(e.Low, low) && OnTreads(r, e.High));
            if (!same) continue;
            if (rise > MathF.Abs(e.High.Y - e.Low.Y) + 0.05f)
            {
                Found[i] = new Link(low, high, false, true);
                Sides[i] = sides;
            }
            return;
        }
        Found.Add(new Link(low, high, false, true));
        Sides.Add(sides);
    }

    /// <summary>Does this standing point sit on the followed flight's treads? How a partial
    /// straight reading is recognised as the same stair the follow walked: its far end is ON the
    /// flight — at the corner — not merely near its ends.</summary>
    private static bool OnTreads(StairFollow.Followed r, Spot s)
    {
        foreach (var t in r.Treads)
            if (Near(new Spot(t.X, t.Y, t.Z), s)) return true;
        return false;
    }

    /// <summary>Direction <paramref name="d"/> of the sweep as a world ground-plane unit vector.</summary>
    private static void Dir(int d, out float x, out float z)
    {
        float a = d * (2f * MathF.PI / Directions);
        x = MathF.Cos(a);
        z = MathF.Sin(a);
    }

    /// <summary>One line per sweep: how many stairs were found, where, how far each one changes
    /// level, which way it runs and what is beside it. The tester cannot see the screen, so this
    /// is how a stair that was or was not used in a plan gets proved from the log alone —
    /// including a DOWN-flight, which the log names as such rather than as a negative climb.</summary>
    internal static string Describe()
    {
        var sb = new StringBuilder("[SF6Access] Stair sweep: player ").Append(Show(_player));
        sb.Append(FormattableString.Invariant(
            $"; {_fineCount} of {Directions} directions change level; {Found.Count} stairs"));
        if (_followJobs.Count > 0)
            sb.Append(FormattableString.Invariant(
                $"; {_followed.Count} of {_followJobs.Count} followed flight(s) walked from a mouth"));
        if (RefusedDown > 0)
            sb.Append(FormattableString.Invariant($"; {RefusedDown} down-flight(s) refused (sides not measured clear)"));
        for (int i = 0; i < Found.Count; i++)
        {
            var l = Found[i];
            var s = Sides[i];
            // Which way this run goes from the PLAYER who measured it: the sweep is run from where
            // they stand, so a stair whose far end is below them is one they would walk down.
            bool down = l.Low.Y < _player.Y;
            sb.Append(FormattableString.Invariant(
                $"; #{i} {Show(l.Low)} -> {Show(l.High)} {(down ? "down" : "up")} {MathF.Abs(l.High.Y - l.Low.Y):F2} m"));
            sb.Append(", sides L=").Append(Word(s.CliffLeft ? NarrowPath.Side.Drop : s.WallLeft ? NarrowPath.Side.Wall : s.Known ? NarrowPath.Side.Floor : NarrowPath.Side.Unknown));
            sb.Append(" R=").Append(Word(s.CliffRight ? NarrowPath.Side.Drop : s.WallRight ? NarrowPath.Side.Wall : s.Known ? NarrowPath.Side.Floor : NarrowPath.Side.Unknown));
            if (s.CliffAndWall) sb.Append(" (cliff and wall)");
        }
        return sb.ToString();
    }

    private static string Word(NarrowPath.Side s) => s switch
    {
        NarrowPath.Side.Floor => "floor",
        NarrowPath.Side.Drop => "CLIFF",
        NarrowPath.Side.Wall => "wall",
        _ => "?",
    };

    private static string Show(Spot s) => FormattableString.Invariant($"({s.X:F1}, {s.Y:F1}, {s.Z:F1})");
}
