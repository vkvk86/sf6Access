using System;
using System.Collections.Generic;
using System.Globalization;
using REFrameworkNET;
using static SF6Access.Services.WorldTour.LevelRoutePlanner;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>The stairs on the route the ENGINE already chose — found wherever they are, however far
/// away.</b> The owner's correction, 2026-10-05: <i>"我要的是他在规划路线的时候，就把下行的梯子规划
/// 出来，跟那个梯子距离玩家多远没有关系。假设玩家离梯子很远，但是要经过这个梯子怎么办呢？再次强调，
/// 我这里讲的梯子是那种窄梯子，不需要交互的。"</i>
///
/// <para><b>Why <see cref="StairCatalog"/> could never answer that.</b> That sweep is a circle
/// of rays around the PLAYER — sixteen directions reaching 8.1 m. It finds the stairs the player
/// is standing among, which is exactly what it was written for ("规划路线的时候就认出来" — a stair
/// the player is about to use must not be noticed only at its foot). But a blind player asking
/// for a mission two hundred metres away needs the stair at the far end of the walk, or in the
/// middle of it, and a circle drawn around their own feet will never touch it. Widening the
/// circle is not the answer: rays grow with the SQUARE of the radius, and 8 m to 100 m is a
/// hundred and fifty times the work for a pass that is already one frame's worth. The ground
/// near the player is not where the answer is.</para>
///
/// <para><b>The answer was already on screen.</b> <see cref="FieldNavPath"/> asks the engine's
/// own pathfinder for the route from the player to the objective, and keeps its string-pulled
/// bend list (<see cref="FieldNavPath.TryRecallRoute"/>). The engine laid that route out along
/// the walkable world — so wherever the route CHANGES LEVEL, the engine has already decided the
/// player goes up or down something there. That is the location of the stair, however far away
/// it is, and it costs this class nothing to look: the list is recalled from a cache the beacon
/// fills twice a second, never queried, never written.</para>
///
/// <para><b>What this measures, and what it refuses to guess.</b> Finding a change of level in
/// the route says only "something happens here". Whether it is a narrow stair (walked, no action
/// button), a ramp, or a cliff the engine routes around is a question about the GROUND, so this
/// reads the ground: a short line of floor probes along the route at that bend, judged by
/// <see cref="NarrowPath"/> — the same reader the other catalogues use, so all three agree on
/// what a flight is. A bend whose ground does not read as a flight produces no link at all. It
/// never assumes that a change of level in a route is a stair.</para>
///
/// <para><b>Descents are held to the same guard as everywhere else.</b> A flight going DOWN is
/// offered only when both its sides were measured and neither is air
/// (<see cref="NarrowPath.SafeToDescend"/>) — going down is the one move that must never be
/// guided on a guess. A climb keeps the ordinary reading.</para>
///
/// <para><b>Turning stairs are WALKED, not crossed with a line, since 2026-10-05.</b> The owner's
/// correction: <i>"我要的是导航一定能认出这种窄梯子，无论他远或者近，另外，梯子也是可能转弯的"</i>.
/// The line this class lays across a bend is straight, and a stair that turns leaves it at the
/// corner — into the wall or the open side — so the flight reads as nothing, or as a stub that
/// ends mid-stair; the live case read <c>0 stairs</c> with the player standing at the head of the
/// very stair the route needed. Once the line is measured, the mouth on it — the last flat sample
/// before the first riser — is found and the flight is WALKED with <see cref="StairFollow"/>:
/// one tread at a time, turning when the stair turns, ending on the landing the flight arrives
/// at, which is connected ground. The deeper of the two readings becomes the link; a stub link
/// whose far end stands mid-stair ends on the disconnected treads, where the planner's navmesh
/// question fails and the route stops exactly at the mouth.</para>
///
/// <para><b>Read-only, like every guide since the crash.</b> This recalls a cached bend list and
/// casts rays at the ground. It never queries the pathfinder itself, never writes an engine
/// structure, never touches the input slot. When the route cannot be recalled it produces
/// nothing, and the rest of the navigation behaves exactly as before — additions only, and a
/// fallback that is the old behaviour rather than a guess.</para>
/// </summary>
internal static class RouteStairFinder
{
    /// <summary><b>A change of level in the route this large means the engine is sending the
    /// player up or down something here.</b> A flat walk has bends that differ by centimetres as
    /// the mesh tilts; a stair or a ramp changes storeys. Set to the step limit so this agrees
    /// with <see cref="JumpLink"/> on where "level" ends — the same number the floor sampler
    /// below judges treads by.</summary>
    private const float BendRiseM = JumpLink.StepM;

    /// <summary>How far along the route, either side of a rising bend, the ground is measured.
    /// A flight is a handful of metres (<see cref="NarrowPath.MaxSteps"/> treads); this is a
    /// little more than that on each side so the reader can see the foot it starts from and the
    /// landing it arrives at.</summary>
    private const float ReachM = 7f;

    /// <summary>The sample spacing along the route at a bend. The same stride the other
    /// catalogues read a flight at, so a tread is never skipped between two samples.</summary>
    private const float SampleStepM = 0.4f;

    /// <summary>The ray's origin is lifted this far above the route's own height, so the tread
    /// the engine put the point ON is inside the cast rather than behind its origin.</summary>
    private const float ProbeAboveM = 1.6f;

    /// <summary>...and looks this far down. Deep enough for the landing of a descending flight to
    /// be found rather than read as a hole — the 2026-10-05 lesson, applied here as it was in
    /// <see cref="StairCatalog"/> and <see cref="JumpLinkCatalog"/>.</summary>
    private const float ProbeDropM = 9.0f;

    // How far out a flight's side is probed for the descent guard — and how far below the tread
    // still counts as ground beside it — live in <see cref="StairCatalog.ProbeSideAt"/> now, which
    // both catalogues call. Two copies of that corridor is two chances to disagree about what
    // "air beside it" is, and a descent guard that disagrees with itself is no guard at all.

    /// <summary>The most bends one route may contribute, and the most storey changes the straight
    /// line may contribute — the line's cap is smaller because a straight line is a guess about
    /// DIRECTION and only its near end can be trusted.</summary>
    private const int MaxBends = 8;
    private const int MaxLineBends = 4;

    /// <summary>The straight line toward the objective: first sample this far out (a stride, so the
    /// ground under the player's own feet is not re-measured), then as many samples as the distance
    /// needs at <see cref="LineStepM"/> — BUT the stride is shrunk so the LAST sample lands exactly
    /// on the objective. The objective is the one point on the line whose ground must be read, and
    /// a fixed stride leaves it unread whenever the distance is not a whole multiple of the stride.
    /// Found live, 2026-10-05: a 5.7 m descent with the objective 6.07 m away was sampled at 2.0,
    /// 3.5 and 5.0 m — the line stopped a metre short of the objective, so the landing the player
    /// must walk down to was never measured and the whole flight read as a flat slab.</summary>
    private const float LineFirstM = 2f;
    private const float LineStepM = 1.5f;

    /// <summary>How many samples the straight line may take, whatever the distance. This is the
    /// whole economy of measuring a line instead of a circle: a two-hundred-metre objective costs
    /// this many rays and no more, and the reach grows with the objective rather than with the
    /// square of a sweep radius.</summary>
    private const int LineSamples = 160;

    /// <summary>The most links one search may offer, as in every other catalogue.</summary>
    private const int MaxLinks = 24;

    /// <summary>Rays per pump. The whole find is a few dozen casts over a few frames, in the gap
    /// the sweep already leaves, and not one navmesh query.</summary>
    private const int RaysPerPump = 12;

    /// <summary>A bend of the route that changes level — the shape and its finding are pure and
    /// live in <see cref="RouteBend"/>; this class only measures the ground at one.</summary>
    private static readonly List<Link> Found = new();
    private static readonly List<NarrowPath.Sides> Sides = new();

    /// <summary>The bends of the recalled route that change level — found by the pure
    /// <see cref="RouteBend"/>, measured here.</summary>
    private static readonly List<RouteBend.Bend> Bends = new();

    // The measurement of ONE bend, spread over frames. Row-major by sample index.
    private static int _bendAt;
    private static float[] _line = Array.Empty<float>();
    private static bool[] _lineOk = Array.Empty<bool>();
    private static int _samples;
    private static int _sampleAt;
    private static NarrowPath.Flight _flight;
    private static readonly NarrowPath.Side[] _sideLeft = new NarrowPath.Side[64];
    private static readonly NarrowPath.Side[] _sideRight = new NarrowPath.Side[64];
    private static readonly List<(int i, NarrowPath.Side left, NarrowPath.Side right)> _flightSides = new();

    private static bool _scanning, _done = true, _logged;
    private static int _refusedDown;

    // The STRAIGHT-LINE fallback, when the engine refused to produce a route at all. The line runs
    // from the player toward the objective; its samples are measured a few rays a frame.
    private static Spot _target, _player;
    private static float _lineDirX, _lineDirZ;
    private static float _lineStepM = LineStepM;   // fitted so the last sample lands on the objective
    private static int _lineAt = int.MaxValue;     // beyond the end until StartLine lays one
    private static int _lineSamples;

    // The FOLLOW of one bend's flight, spread over frames. The mouth is found on the line once it
    // is measured; the follower walks the flight one tread at a time, turning when the stair
    // turns, and its sides are probed on each third tread against the TREAD's own heading.
    private static StairFollow.Follower _bendFollower;
    private static bool _bendFollowTried;
    private static StairFollow.Followed _bendFollowed;
    private static readonly List<(int tread, NarrowPath.Side left, NarrowPath.Side right)> _followSides = new();

    /// <summary>The stairs found on the route, as planner links. Empty until
    /// <see cref="Done"/> — an honest empty answer when the route could not be recalled or held
    /// no measurable flight.</summary>
    internal static IReadOnlyList<Link> Links => Found;

    /// <summary>Per link, same order as <see cref="Links"/>: which hand the cliff and the wall
    /// are on, for the caller that speaks the stair.</summary>
    internal static NarrowPath.Sides SidesOf(int link) =>
        link >= 0 && link < Sides.Count ? Sides[link] : default;

    /// <summary>How many stairs the last find measured.</summary>
    internal static int Count => Found.Count;

    /// <summary>True once the find has finished — including "there was no route to read".
    /// <see cref="LevelRoute"/> holds the planner back until this, as it does for the other
    /// catalogues.</summary>
    internal static bool Done => _done;

    /// <summary>Down-flights the route ran through that were measured but not offered, because a
    /// side was air or was not measured. Counted so the log can tell "found and deliberately not
    /// used" from "never found" — the two want opposite repairs.</summary>
    internal static int RefusedDown => _refusedDown;

    /// <summary>Drop everything. A new search measures a new route.</summary>
    internal static void Reset()
    {
        Found.Clear();
        Sides.Clear();
        Bends.Clear();
        _scanning = false;
        _done = true;
        _logged = false;
        _refusedDown = 0;
        _bendAt = _sampleAt = _samples = 0;
        _flightSides.Clear();
        _bendFollower = null;
        _bendFollowTried = false;
        _bendFollowed = null;
        _followSides.Clear();
        _lineAt = int.MaxValue;
        _lineSamples = 0;
        _target = _player = default;
    }

    /// <summary><b>Start finding the stairs toward <paramref name="target"/>, however far away they
    /// are.</b> Two sources, tried in order, because the log of 2026-10-05 proved one is not
    /// enough:
    ///
    /// <list type="number">
    /// <item><b>The engine's own route</b>, recalled from the beacon's cache. Exact, and free —
    /// but only exists when the engine admits a route exists, and on the building the owner tested
    /// it answered <c>PathNotFound … DISCONNECTED piece of mesh</c> every single time, producing no
    /// route at all.</item>
    /// <item><b>A straight line of ground from the player toward the objective</b>, measured with
    /// rays. This is the fallback that makes the feature work where the engine refuses: it finds
    /// the stair by measuring the ground, so it does not need anything to admit the stair is a way.
    /// The owner's own words: <i>"他之前提示我找不到路，但是我自己摸索到楼梯边他却告诉我就是这个楼梯，
    /// 我要的是规划的时候就看到并带我走."</i></item>
    /// </list>
    ///
    /// <para>The first source is exact and is used when it is there; the second is a guess about
    /// DIRECTION and only its first few storey changes are trusted, because the player may not be
    /// able to walk a straight line to the objective at all. Both produce ordinary links the
    /// planner verifies; neither ever assumes a change of level is a stair.</para></summary>
    internal static void Begin(Spot target, Spot player)
    {
        Reset();
        _done = false;
        _target = target;
        _player = player;

        if (FieldNavPath.TryRecallRoute(target.X, target.Y, target.Z, out var corners)
            && corners != null && corners.Length >= 2)
        {
            Collect(corners);
            if (Bends.Count > 0)
            {
                _scanning = true;
                _bendAt = _sampleAt = 0;
                API.LogInfo(FormattableString.Invariant(
                    $"[SF6Access] Route stairs: {corners.Length} route points, {Bends.Count} change(s) of level to measure, out to {Show(Farthest(corners, player))}"));
                return;
            }
            API.LogInfo(FormattableString.Invariant(
                $"[SF6Access] Route stairs: {corners.Length} route points, no change of level in them; measuring the straight line instead"));
        }
        else
        {
            // The engine offered no route. That is not permission to give up: the stair the player
            // must walk down is still out there, and the ground will say where it is.
            API.LogInfo("[SF6Access] Route stairs: the engine gave no route (it does not model these stairs as a way) - measuring the straight line toward the objective instead");
        }

        StartLine();
    }

    /// <summary><b>The fallback: a straight line of ground from the player toward the objective,
    /// measured with rays.</b> The line is the direction the player is going anyway, and it costs
    /// one probe per sample — a LINE, not a circle, which is why it can reach as far as the
    /// objective when a sweep around the player never could.
    ///
    /// <para><b>The stride is computed, not fixed.</b> The target is lifted to whole strides and
    /// the stride shrunk to fit (never above <see cref="LineStepM"/>), so the LAST sample lands
    /// exactly ON the objective. A fixed stride would leave the objective's own ground unread
    /// whenever the distance was not a whole multiple of it — found live on 2026-10-05, where a
    /// 5.7 m descent was sampled at 2.0/3.5/5.0 m and the landing, one metre beyond the last
    /// sample, was never seen. Reading the objective's floor is the whole point of the line.</para></summary>
    private static void StartLine()
    {
        float dx = _target.X - _player.X, dz = _target.Z - _player.Z;
        float len = MathF.Sqrt(dx * dx + dz * dz);
        if (!(len > LineFirstM)) { _done = true; _scanning = false; LogDone(); return; }

        _lineDirX = dx / len;
        _lineDirZ = dz / len;

        // How many strides from the first sample to the objective, ROUNDED UP, capped — then the
        // stride is fitted so the last sample sits on the objective instead of short of it.
        int strides = (int)MathF.Ceiling((len - LineFirstM) / LineStepM);
        if (strides < 1) strides = 1;
        bool capped = strides + 1 > LineSamples;
        if (capped) strides = LineSamples - 1;
        _lineSamples = strides + 1;
        _lineStepM = capped ? (len - LineFirstM) / strides : LineStepM;

        _line = new float[_lineSamples];
        _lineOk = new bool[_lineSamples];
        for (int i = 0; i < _lineSamples; i++) { _line[i] = float.NaN; _lineOk[i] = false; }
        _lineAt = 0;
        _scanning = true;
        _bendAt = _sampleAt = 0;
        API.LogInfo(FormattableString.Invariant(
            $"[SF6Access] Route stairs: measuring {_lineSamples} samples every {_lineStepM:F2} m out to {Show(len)} along the straight line to the objective"));
    }

    /// <summary>The straight line's ground samples, a few rays a frame.
    ///
    /// <para><b>The ray is cast from high above the HIGHEST of the two ends.</b> The ground under
    /// the objective can be a storey BELOW the player, and a ray that starts at the player's own
    /// foot height would begin underneath that landing and see nothing — the descent would read as
    /// one flat slab, which is exactly what happened live on 2026-10-05 (every sample returned the
    /// player's own 73.73 on a route that had to fall 5.7 m). Starting above BOTH ends and reaching
    /// <see cref="ProbeDropM"/> down covers the whole drop between them, whatever direction it
    /// runs.</para></summary>
    private static void PumpLineSamples()
    {
        float top = MathF.Max(_player.Y, _target.Y) + ProbeAboveM;
        int budget = RaysPerPump;
        while (budget-- > 0 && _lineAt < _lineSamples)
        {
            int i = _lineAt++;
            float t = LineFirstM + i * _lineStepM;
            float px = _player.X + _lineDirX * t;
            float pz = _player.Z + _lineDirZ * t;
            if (FieldRayCaster.TryFloorBelow(px, top, pz, ProbeDropM, out float floor))
            {
                _line[i] = floor;
                _lineOk[i] = true;
            }
            else { _line[i] = float.NaN; _lineOk[i] = false; }
        }
        if (_lineAt < _lineSamples) return;

        // The line is in: which of its samples change storey? The deciding is pure
        // (RouteBend.AlongLine) so the shape of it is proved by the tests, not by a running game.
        Bends.AddRange(RouteBend.AlongLine(_player, i => _line[i], i => _lineOk[i], _lineSamples,
                                           LineFirstM, _lineStepM, BendRiseM, MaxLineBends,
                                           _lineDirX, _lineDirZ));
        _lineAt = int.MaxValue;          // never taken twice
        _bendAt = _sampleAt = 0;
        if (Bends.Count == 0)
        {
            _scanning = false;
            _done = true;
            LogDone();
        }
    }

    /// <summary>The one closing line: how many storey changes the find measured and how many
    /// stairs it kept, plus the descents it deliberately refused. Written once per find, and
    /// written even when the answer is "none" — the owner needs to tell "no stair on the way" from
    /// "the search never ran", and silence here would read as the latter.</summary>
    private static void LogDone()
    {
        if (_logged) return;
        _logged = true;
        var sb = new System.Text.StringBuilder("[SF6Access] Route stairs: ");
        sb.Append(FormattableString.Invariant($"{Bends.Count} change(s) of level measured, {Found.Count} stair(s) on the route"));
        if (_refusedDown > 0)
            sb.Append(FormattableString.Invariant($"; {_refusedDown} down-flight(s) refused (sides not measured clear)"));
        API.LogInfo(sb.ToString());
    }

    /// <summary>Walk the route's corners and keep the ones where the level changes. The route is
    /// the engine's own, so a change of level in it is the engine saying "up or down here" — which
    /// is the whole reason this works at a distance: no ray this class casts has to reach the
    /// stair, only the stair's own neighbourhood once the route has named it. The deciding is pure
    /// and lives in <see cref="RouteBend"/>; this only casts and judges.</summary>
    private static void Collect((float X, float Y, float Z)[] corners)
    {
        Bends.AddRange(RouteBend.Find(corners, BendRiseM, MaxBends));
    }

    /// <summary>How far away the farthest corner is, in metres — logged so "the stair was beyond
    /// the sweep" can be told from "there was no stair", which is the owner's whole point.</summary>
    private static float Farthest((float X, float Y, float Z)[] corners, Spot player)
    {
        float far = 0f;
        foreach (var c in corners)
        {
            float dx = c.X - player.X, dz = c.Z - player.Z;
            float d = MathF.Sqrt(dx * dx + dz * dz);
            if (d > far) far = d;
        }
        return far;
    }

    /// <summary>Advance the find by a few rays; true once every bend has been measured.</summary>
    internal static bool Pump()
    {
        if (!_scanning) { _done = true; return true; }

        // The straight-line pass comes first when the engine gave no route to read: its samples ARE
        // the bends' source, so there is nothing to measure until the line is in.
        if (_lineAt < _lineSamples)
        {
            PumpLineSamples();
            if (_lineAt < _lineSamples) return false;
            if (Bends.Count == 0) return _done;    // the ground along the line never changed storey
        }

        if (_bendAt >= Bends.Count) { Finish(); return _done; }

        if (_samples == 0) StartBend();
        if (_sampleAt < _samples) { PumpLine(); return false; }

        // The flight is WALKED from its mouth before the straight reading is turned into a link.
        // The follow is the part that reads a turning stair, and it is pumped a few rays a frame
        // like every other measurement here: the owner's stair turned at the corner, and a reading
        // that stops there is the reading that left them standing at the mouth.
        if (!_bendFollowTried) { StartBendFollow(); _bendFollowTried = true; }
        if (_bendFollower != null)
        {
            PumpBendFollow();
            if (_bendFollower != null) return false;
        }

        // The flight is read and its sides probed in one go here; both are bounded by the flight's
        // own tread count, so neither is a per-frame cost worth slicing.
        ReadBend();
        _bendAt++;
        _samples = _sampleAt = 0;
        _flightSides.Clear();
        _followSides.Clear();
        _bendFollowTried = false;
        _bendFollowed = null;
        if (_bendAt >= Bends.Count) Finish();
        return _done;
    }

    /// <summary>Lay the line for the bend now being measured: from <see cref="ReachM"/> before it
    /// to <see cref="ReachM"/> after it, along the route's own direction. The samples are absolute
    /// world positions on the route, so the flight they read belongs to the route and not to
    /// wherever the player happens to be standing.</summary>
    private static void StartBend()
    {
        int n = (int)(2f * ReachM / SampleStepM) + 1;
        _samples = Math.Min(n, 64);
        _line = new float[_samples];
        _lineOk = new bool[_samples];
        _sampleAt = 0;
        for (int i = 0; i < _samples; i++) { _line[i] = float.NaN; _lineOk[i] = false; }
        for (int i = 0; i < _sideLeft.Length; i++) { _sideLeft[i] = _sideRight[i] = NarrowPath.Side.Unknown; }
    }

    /// <summary>The floor line across the bend, a few rays a frame.
    ///
    /// <para><b>The ray starts above the HIGH end of the storey change, not at the bend's own
    /// height.</b> A bend on a descent sits halfway between two storeys, so a ray leaving from its
    /// own height would begin below the landing above it and never see it. Casting from above the
    /// higher of the two floors covers the whole drop, whichever way the flight runs — the same
    /// correction the straight-line pass needed, for the same reason.</para></summary>
    private static void PumpLine()
    {
        var b = Bends[_bendAt];
        // The bend's height is the midpoint of the two floors; half the rise above it is the
        // higher of them, so probe from there.
        float refY = b.At.Y + MathF.Abs(b.RiseM) * 0.5f;
        int budget = RaysPerPump;
        while (budget-- > 0 && _sampleAt < _samples)
        {
            int i = _sampleAt++;
            float t = -ReachM + i * SampleStepM;
            float px = b.At.X + b.DirX * t;
            float pz = b.At.Z + b.DirZ * t;
            if (FieldRayCaster.TryFloorBelow(px, refY + ProbeAboveM, pz, ProbeDropM, out float floor))
            {
                _line[i] = floor;
                _lineOk[i] = true;
            }
            else { _line[i] = float.NaN; _lineOk[i] = false; }
        }
    }

    /// <summary><b>Find the mouth on the line just measured — the last flat sample before the
    /// first riser — and stand a follower on it.</b> The follower walks the flight the way feet
    /// do, one tread at a time, turning when the stair turns; the straight line is blind past a
    /// corner, and the owner was left at the head of a turning stair with nothing but "find an
    /// edge to jump down". A change beyond the step limit is a drop, not a tread, and no mouth is
    /// found in it — following one would be walking off an edge. A gap in the samples resets the
    /// scan: the ground after a hole is not known to continue the ground before it. No mouth on
    /// the line means no flight to follow, and the straight reading stands alone.</summary>
    private static void StartBendFollow()
    {
        _bendFollower = null;
        _bendFollowed = null;
        var b = Bends[_bendAt];
        float prev = float.NaN;
        bool prevOk = false;
        for (int i = 0; i < _samples; i++)
        {
            if (!_lineOk[i]) { prevOk = false; continue; }
            float f = _line[i];
            if (prevOk)
            {
                float change = f - prev;
                // The same gate the follow itself walks by — <see cref="StairFollow.IsTread"/>.
                // A mouth found with a looser gate is a mouth the follow stops one tread past.
                if (StairFollow.IsTread(change, change < 0f))
                {
                    float t = -ReachM + (i - 1) * SampleStepM;
                    _bendFollower = new StairFollow.Follower(FloorOf, b.At.X + b.DirX * t, prev,
                                                             b.At.Z + b.DirZ * t, b.DirX, b.DirZ,
                                                             change < 0f, SampleStepM);
                    return;
                }
            }
            prev = f;
            prevOk = true;
        }
    }

    /// <summary>Advance the bend's follow a few rays a frame, and probe the sides of every third
    /// tread it steps onto — against the TREAD's own heading, because on a turning stair the cliff
    /// turns with it. When the flight ends, the reading is kept and the follower cleared.</summary>
    private static void PumpBendFollow()
    {
        var f = _bendFollower;
        int budget = RaysPerPump;
        while (budget > 0)
        {
            int before = f.Casts;
            bool stepped = f.Step();
            budget -= f.Casts - before;
            if (!stepped) { KeepBendFollow(f); _bendFollower = null; return; }
            var t = f.Treads[f.Treads.Count - 1];
            if ((f.Treads.Count - 1) % 3 == 0)
            {
                var left = StairCatalog.ProbeSideAt(t.X, t.Y, t.Z, t.DirX, t.DirZ, true);
                var right = StairCatalog.ProbeSideAt(t.X, t.Y, t.Z, t.DirX, t.DirZ, false);
                _followSides.Add((f.Treads.Count - 1, left, right));
                budget -= 4;
            }
        }
    }

    private static void KeepBendFollow(StairFollow.Follower f)
    {
        var r = f.ToFollowed();
        r.Treads.AddRange(f.Treads);
        _bendFollowed = r;
        if (r.Ok)
            API.LogInfo(FormattableString.Invariant(
                $"[SF6Access] Route stairs: followed mouth ({r.MouthX:F1}, {r.MouthY:F1}, {r.MouthZ:F1}) -> end ({r.EndX:F1}, {r.EndY:F1}, {r.EndZ:F1}); {r.Risers} risers, {r.Turns} turn(s), rise {r.RiseM:F2} m"));
        else
            API.LogInfo(FormattableString.Invariant(
                $"[SF6Access] Route stairs: follow stopped after {r.Risers} riser(s) - too short to be a stair"));
    }

    /// <summary>The floor under a point for a follow: the same probe geometry the line pass uses —
    /// from just above the standing height, down the full drop — so a tread the line can see, the
    /// follow can see too.</summary>
    private static float FloorOf(float x, float standingY, float z)
        => FieldRayCaster.TryFloorBelow(x, standingY + ProbeAboveM, z, ProbeDropM, out float floor)
            ? floor : float.NaN;

    /// <summary>The sides of the flight just read, one probe pair per tread. Called inline from
    /// <see cref="ReadBend"/> once the floor line is complete, so it walks the whole flight rather
    /// than a slice of it — a half-probed flight would roll up to <c>Unknown</c> on the unmeasured
    /// hand and refuse a descent that is in fact safe. The pair count is bounded by the flight's own
    /// tread count and by <see cref="NarrowPath.MaxSteps"/>, so it cannot run away.</summary>
    private static void PumpSides()
    {
        var b = Bends[_bendAt];
        int guard = NarrowPath.MaxSteps + 1;
        while (guard-- > 0 && _flightSides.Count < _flight.RiserCount)
        {
            int step = _flight.FirstStep + _flightSides.Count;
            if (step > _flight.Top) break;
            var left = ProbeSide(b, step, true);
            var right = ProbeSide(b, step, false);
            _flightSides.Add((step, left, right));
        }
    }

    /// <summary>What is beside one tread of the flight, on one hand. The same two measurements
    /// <see cref="StairCatalog"/> makes — a cast across at waist height for a wall, a floor probe
    /// at the reach for ground — folded through the same classifier, so the two catalogues cannot
    /// disagree about whether a descent is safe.</summary>
    private static NarrowPath.Side ProbeSide(RouteBend.Bend b, int step, bool left)
    {
        float t = -ReachM + step * SampleStepM;
        return StairCatalog.ProbeSideAt(b.At.X + b.DirX * t, _line[step], b.At.Z + b.DirZ * t,
                                        b.DirX, b.DirZ, left);
    }

    /// <summary>Turn the line just measured into a link, or into nothing. The reader is
    /// <see cref="NarrowPath"/>, the same one every other catalogue uses, so a flight is a flight
    /// wherever it was found. A descent must still pass <see cref="NarrowPath.SafeToDescend"/>.</summary>
    private static void ReadBend()
    {
        var b = Bends[_bendAt];

        // (1) The straight line's reading, as it has always been. The flight is read HERE, before
        // its sides are probed, because the side pass needs to know which treads to walk
        // (<see cref="PumpSides"/> reads <c>_flight</c>). Reading it at the end — which is what an
        // earlier revision did — left <c>_flight</c> unassigned, so no side was EVER probed and
        // every descent was refused for "a side is air or was not measured". Found live on
        // 2026-10-05; the compiler's CS0649 "never assigned" was the tell.
        bool lineRead = NarrowPath.TryRead(i => _line[i], i => _lineOk[i], _samples, b.At.Y,
                                          SampleStepM, _samples * SampleStepM, out var f);
        if (lineRead)
        {
            _flight = f;
            PumpSides();                  // fill every probed tread's two hands
        }

        // (2) The follow's reading, walked from the mouth a few frames ago.
        var followed = _bendFollowed;
        var followSides = FollowSides();

        // (3) The DEEPER reading is the true one, and on a turning stair the follow is the only
        // one that is whole: the line stops at the corner, the follow walks round it and ends on
        // the landing — connected ground the planner's navmesh question can verify. A link whose
        // far end stands mid-stair cannot be verified, and the route stops exactly there.
        bool useFollow = followed is { Ok: true }
            && (!lineRead || MathF.Abs(followed.RiseM) > MathF.Abs(f.SignedM) + 0.05f);

        if (useFollow)
        {
            if (followed.Down && !NarrowPath.SafeToDescend(followSides))
            {
                _refusedDown++;
                API.LogInfo("[SF6Access] Route stairs: a followed down-flight, refused - a side is air or was not measured");
                return;
            }
            if (lineRead)
                API.LogInfo(FormattableString.Invariant(
                    $"[SF6Access] Route stairs: the follow read {MathF.Abs(followed.RiseM):F2} m where the straight line stopped at {MathF.Abs(f.SignedM):F2} m - the stair turns"));
            AddFollowed(followed, followSides);
            return;
        }

        if (!lineRead) return;            // not a flight: a ramp, a kerb, or ground we could not read

        var sides = SidesOf(f);
        if (f.GoesDown && !NarrowPath.SafeToDescend(sides))
        {
            _refusedDown++;
            API.LogInfo("[SF6Access] Route stairs: a down-flight on the route, refused - a side is air or was not measured");
            return;
        }
        Add(f, sides, b);
    }

    /// <summary>The flight's two hands, rolled up over its own treads through the SHARED
    /// classifier (<see cref="NarrowPath.ReadSides"/>), so this catalogue and
    /// <see cref="StairCatalog"/> cannot disagree about which hand a hazard is on or whether a
    /// descent is safe. Each hand takes the MAJORITY verdict over the treads it probed — the same
    /// roll-up <see cref="StairCatalog"/> uses, deliberately, because a single odd tread (a gap in
    /// a railing, a doorway beside one step) must not condemn a whole flight, while a hand that
    /// was never measured anywhere stays <see cref="NarrowPath.Side.Unknown"/> and so can never
    /// pass the descent guard.</summary>
    private static NarrowPath.Sides SidesOf(NarrowPath.Flight f)
    {
        if (_flightSides.Count == 0) return default;
        int lFloor = 0, lDrop = 0, lWall = 0, rFloor = 0, rDrop = 0, rWall = 0;
        foreach (var (_, left, right) in _flightSides)
        {
            Tally(left, ref lFloor, ref lDrop, ref lWall);
            Tally(right, ref rFloor, ref rDrop, ref rWall);
        }
        return NarrowPath.ReadSides(Hand(lFloor, lDrop, lWall), Hand(rFloor, rDrop, rWall));
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

    /// <summary>The sides of the FOLLOWED flight, rolled up over the treads it probed — the same
    /// majority roll-up the line flight gets, through the same shared classifier, so the two
    /// readings cannot disagree about which hand a hazard is on or whether a descent is safe.
    /// A hand that was never measured anywhere stays
    /// <see cref="NarrowPath.Side.Unknown"/>, and so can never pass the descent guard.</summary>
    private static NarrowPath.Sides FollowSides()
    {
        if (_followSides.Count == 0) return default;
        int lFloor = 0, lDrop = 0, lWall = 0, rFloor = 0, rDrop = 0, rWall = 0;
        foreach (var (_, left, right) in _followSides)
        {
            Tally(left, ref lFloor, ref lDrop, ref lWall);
            Tally(right, ref rFloor, ref rDrop, ref rWall);
        }
        return NarrowPath.ReadSides(Hand(lFloor, lDrop, lWall), Hand(rFloor, rDrop, rWall));
    }

    /// <summary>One hand's verdict over the flight: a hazard present anywhere wins, and a hand
    /// that was never measured anywhere stays unknown — "not measured" is never read as ground.
    /// Same shape as <see cref="StairCatalog"/>'s per-hand roll-up.</summary>
    private static NarrowPath.Side Hand(int floor, int drop, int wall)
    {
        if (drop >= floor && drop >= wall && drop > 0) return NarrowPath.Side.Drop;
        if (wall >= floor && wall >= drop && wall > 0) return NarrowPath.Side.Wall;
        if (floor > 0) return NarrowPath.Side.Floor;
        return NarrowPath.Side.Unknown;
    }

    /// <summary>One link for the flight, named by HEIGHT at its two ends so the planner's two
    /// directions of travel stay "up" and "down" whatever way the route ran through it. The link
    /// is a STAIR, not a jump: the player walks it, with no action button, and the plan must be
    /// able to say so.</summary>
    private static void Add(NarrowPath.Flight f, NarrowPath.Sides sides, RouteBend.Bend b)
    {
        if (Found.Count >= MaxLinks) return;

        // The flight's own foot and top, as world points along the route's line.
        int foot = -1;
        for (int j = f.FirstStep - 1; j >= 0; j--)
            if (_lineOk[j] && float.IsFinite(_line[j])) { foot = j; break; }
        int footIdx = foot >= 0 ? foot : f.FirstStep;
        float footY = foot >= 0 ? _line[foot] : b.At.Y;

        var a = SpotAt(b, footIdx, footY);
        var c = SpotAt(b, f.Top, _line[f.Top]);

        for (int i = 0; i < Found.Count; i++)
        {
            if (Near(Found[i].Low, a) && Near(Found[i].High, c)) return;
            if (Near(Found[i].Low, c) && Near(Found[i].High, a)) return;
        }

        var link = c.Y < a.Y ? new Link(c, a, false, true) : new Link(a, c, false, true);
        Found.Add(link);
        Sides.Add(sides);
    }

    /// <summary>One link for a FOLLOWED flight, named by height at its two ends so the planner's
    /// two directions of travel stay "up" and "down" whatever way the route ran through it, and
    /// marked a STAIR like any other. The follow's far end stands ON the landing the flight
    /// arrives at — connected ground the planner can verify — where a reading cut short ends
    /// mid-flight, on the disconnected treads, and a route through it stops exactly where the
    /// owner was left standing.</summary>
    private static void AddFollowed(StairFollow.Followed f, NarrowPath.Sides sides)
    {
        if (Found.Count >= MaxLinks) return;
        var mouth = new Spot(f.MouthX, f.MouthY, f.MouthZ);
        var end = new Spot(f.EndX, f.EndY, f.EndZ);
        var low = end.Y < mouth.Y ? end : mouth;
        var high = end.Y < mouth.Y ? mouth : end;
        for (int i = 0; i < Found.Count; i++)
        {
            if (Near(Found[i].Low, low) && Near(Found[i].High, high)) return;
            if (Near(Found[i].Low, high) && Near(Found[i].High, low)) return;
        }
        Found.Add(new Link(low, high, false, true));
        Sides.Add(sides);
    }

    private static Spot SpotAt(RouteBend.Bend b, int i, float y)
    {
        float t = -ReachM + i * SampleStepM;
        return new Spot(b.At.X + b.DirX * t, y, b.At.Z + b.DirZ * t);
    }

    private static bool Near(Spot a, Spot b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return dx * dx + dz * dz <= 1f;
    }

    private static void Finish()
    {
        _scanning = false;
        _done = true;
        LogDone();
    }

    private static string Show(float m) =>
        m.ToString("F0", CultureInfo.InvariantCulture) + " m out";
}
