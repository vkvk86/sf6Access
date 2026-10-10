using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using REFrameworkNET;
using static SF6Access.Services.WorldTour.LevelRoutePlanner;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>The crossings the navmesh does not know about: a ledge a stride up, a gap a stride
/// wide, and — the owner's ask, 2026-10-05 — the narrow metal STAIR you walk up, read as a
/// run of steps with a cliff on one side and a wall on the other.</b>
///
/// <para><b>Why measurement and not a query.</b> The engine's answer to "can I walk
/// there?" is one bit, and everything a player actually gets over — a kerb, a step, a
/// stair, a hole in a scaffold — comes back as the same bit as a fence they cannot.
/// Asking again never produces a second bit. The ground itself has the numbers, so this
/// reads the ground: rays straight down at a line of samples from the player toward the
/// objective, and the floor heights they come back with are what <see cref="JumpLink"/>
/// and <see cref="NarrowPath"/> judge. A sample with no floor under it at all is the hole
/// itself, and the distance between the two solid samples either side of it is the gap.</para>
///
/// <para><b>What the first version could not do, and this one can.</b> It produced at most
/// one crossing — the first riser — and stopped. A flight of stairs is a RUN of risers, so
/// the navigation could name the first step of a stair and never the stair; once the player
/// stood on the landing the next flight was invisible (2026-10-05 log: `crossings step` at
/// 12:37:00, then `23.71 x8 … crossings none` from 12:38:01). The line of samples now runs
/// the whole flight and every riser becomes its own link, so the planner walks them one at a
/// time; and every step of the flight is probed sideways, so a stair with air on one hand
/// and a wall on the other is recognised as such (<see cref="NarrowPath.ReadSides"/>).</para>
///
/// <para><b>It produces links, not routes.</b> What comes out is ordinary
/// <see cref="LevelRoutePlanner.Link"/>s — the same shape a ladder is — so the existing
/// planner can put a step, a gap or a stair into a route exactly as it puts a climb into
/// one, and prove every half by asking the engine: walk to the near edge, up, walk on.
/// Nothing about the search changes; only the set of things it may connect through
/// grows.</para>
///
/// <para><b>Only ever run when the walk has already failed.</b> A route that works is
/// left alone. That is the rule the whole navigation is held to: additions only.</para>
///
/// <para><b>Cost.</b> Eight floor rays plus up to twenty-four sideways rays per search,
/// all in two-a-frame pumps, and not one navmesh query — the planner's own questions verify
/// the landing. The floor rays are the call the radar makes thirty times a second; the
/// sideways rays are <see cref="FieldRayCaster.TryCastAcross"/>, the same call the round
/// search already makes a thousand of. A wider scan on a route that has just failed is
/// still less than one round sweep.</para>
/// </summary>
internal static class JumpLinkCatalog
{
    /// <summary>The first sample, in metres from the player's feet: about a stride, so a
    /// crossing begins before the body is on it.</summary>
    private const float FirstSampleM = 0.5f;

    /// <summary>Distance between samples. Finer than <see cref="JumpLink.GapM"/> on
    /// purpose: a gap is only measured by the two solid samples either side of it, and
    /// a coarse sample would call a two-metre hole a metre-wide one.</summary>
    private const float SampleStepM = 0.4f;

    /// <summary>How many samples: 0.5 m to 9.7 m. Far enough to read a full flight of
    /// narrow stairs — a dozen steps at a stride apiece is about five metres — and near
    /// enough that a route through it is still the route the player is walking. Raised
    /// from 8 (3.3 m) on 2026-10-05: the old line could not reach the second flight of a
    /// stair, which is why the landing read `crossings none`.</summary>
    private const int Samples = 24;

    /// <summary>The ray starts this far above the player's feet, so a floor above them
    /// (the ledge) is inside the cast and not behind its origin.</summary>
    private const float ProbeAboveM = 1.6f;

    /// <summary>...and looks this far down. A ledge to jump up onto is a floor the
    /// player's own floor is below; a hole is no floor at all within this.
    ///
    /// <para><b>Deepened from 4.0 m on 2026-10-05, for the down-flight.</b> With
    /// <see cref="ProbeAboveM"/> the old drop reached 2.4 m below the feet, and
    /// <see cref="NarrowPath.TryRead"/> treats a sample with no floor as the END of the run
    /// ("a hole ends the flight"). So a flight descending more than 2.4 m — a dozen treads at
    /// a riser apiece is already past it — was read only as far as the probe could see, and the
    /// walk down stopped in mid-air with a "hole" under it. Seven metres reaches 5.4 m below
    /// the feet, which covers the whole of any flight a player walks rather than jumps.</para>
    ///
    /// <para>This can only ever find MORE floor: "no floor within this drop" stays true of a
    /// genuine hole however deep the drop is made, and whether a change of level is a tread or
    /// a drop is decided by <see cref="JumpLink.StepM"/> on the samples, never by this ceiling.
    /// Deepening it cannot invent a tread and cannot hide a fall.</para></summary>
    private const float ProbeDropM = 9.0f;

    /// <summary>Rays per frame, floor and side together, so a search still finishes in a
    /// few frames instead of a dozen and the player is not kept waiting on a route that is
    /// failing anyway.</summary>
    private const int RaysPerPump = 6;

    /// <summary>How far out a side of a stair is probed, in metres — the corridor within
    /// which "is there a floor beside me or only air" is asked. Two body-widths: close
    /// enough that the answer is about the stair underfoot, wide enough to be off it. This
    /// is the owner's "一面悬崖一面高墙" resolution and the number to move if a stair that
    /// has a narrow ledge beside it reads as a cliff.</summary>
    private const float SideReachM = 1.2f;

    /// <summary>A floor this far below the step counts as air beside it: a drop, not a
    /// ledge to step off onto. Sized to the step limit so the two agree on what "level with
    /// the stair" means.</summary>
    private const float SideFloorM = JumpLink.StepM;

    /// <summary>The most crossings one search may offer. A twelve-step flight is a dozen
    /// links; the cap is what keeps a pathological stair from crowding the planner's
    /// question budget with its own risers.</summary>
    private const int MaxLinks = 24;

    /// <summary>How far a flight may reach, in metres, summed over its steps. Beyond this
    /// the samples have run past the stair the player is on. The owner's ask is a stair
    /// "you walk up"; this is where walking it turns into another route.</summary>
    private const float FlightReachM = 6.0f;

    /// <summary>Where a sideways probe leaves the step, in metres above its floor: the
    /// middle of a standing body, the same height the radar's own beams leave the avatar.
    /// Read, not tuned — <see cref="FieldRayMetrics.WaistAboveFeet"/> measures the live
    /// capsule, and this restates its answer as a floor-relative offset for a ray that has
    /// no avatar to measure.</summary>
    private const float WaistAboveFloorM = 0.9f;

    private static readonly List<Link> Found = new();
    private static readonly float[] Floor = new float[Samples];
    private static readonly bool[] Measured = new bool[Samples];

    // --- the sideways probes: one per step of the flight, both hands ---
    private const int MaxProbes = 20;
    private static readonly int[] ProbeI = new int[MaxProbes];
    private static readonly bool[] ProbeLeft = new bool[MaxProbes];
    private static readonly NarrowPath.Side[] SideLeft = new NarrowPath.Side[Samples];
    private static readonly NarrowPath.Side[] SideRight = new NarrowPath.Side[Samples];
    private static int _probeCursor, _probeCount;
    private static NarrowPath.Flight _flight;
    private static bool _hasFlight;

    /// <summary>Both hands' readings for every step of the flight, for the log and for
    /// anything that wants to speak the stair. Empty until <see cref="Done"/>.</summary>
    private static readonly List<(int i, NarrowPath.Side left, NarrowPath.Side right)> FlightSides = new();

    private static Spot _player, _target;
    private static float _dirX, _dirZ;
    private static int _cursor;
    private static bool _scanning, _done, _sidesDone;

    /// <summary>The crossings and stair steps found, as planner links. Empty until <see cref="Done"/>.</summary>
    internal static IReadOnlyList<Link> Links => Found;

    /// <summary>True when a search has been started and has not finished.</summary>
    internal static bool Scanning => _scanning && !_done;

    /// <summary>True when there is nothing more to wait for: finished, or never started.</summary>
    internal static bool Done => !_scanning || _done;

    /// <summary>Starts a search along the line from the player to the objective.</summary>
    internal static void Begin(Spot player, Spot target)
    {
        Found.Clear();
        FlightSides.Clear();
        _player = player;
        _target = target;
        _cursor = 0;
        _probeCursor = _probeCount = 0;
        _hasFlight = false;
        _sidesDone = false;
        _scanning = true;
        _done = false;
        for (int i = 0; i < Samples; i++) { Floor[i] = float.NaN; Measured[i] = false; SideLeft[i] = SideRight[i] = NarrowPath.Side.Unknown; }

        float dx = target.X - player.X, dz = target.Z - player.Z;
        float len = MathF.Sqrt(dx * dx + dz * dz);
        if (len > 0.01f) { _dirX = dx / len; _dirZ = dz / len; }
        else { _dirX = 0f; _dirZ = 1f; }
    }

    /// <summary>Drop the search. A new objective, or a new level, has its own ground.</summary>
    internal static void Reset()
    {
        Found.Clear();
        FlightSides.Clear();
        _scanning = _done = _sidesDone = _hasFlight = false;
        _cursor = _probeCursor = _probeCount = 0;
    }

    /// <summary>Advance the search by a few rays: the floor line first, then the sides of
    /// whatever flight it read. Does nothing once it is done.</summary>
    internal static void Pump()
    {
        if (!_scanning || _done) return;

        if (_cursor < Samples) { PumpFloors(); return; }
        if (!_sidesDone) { PumpSides(); return; }

        _done = true;
        Build();
        Log();
    }

    /// <summary>The floor line: two samples a frame until the line is full.</summary>
    private static void PumpFloors()
    {
        for (int k = 0; k < RaysPerPump && _cursor < Samples; k++)
        {
            int i = _cursor++;
            float t = At(i);
            if (!FieldRayCaster.TryFloorBelow(_player.X + _dirX * t, _player.Y + ProbeAboveM,
                                              _player.Z + _dirZ * t, ProbeDropM, out float floor))
            {
                // The sensor could not run: not "no floor", but "no information", and a
                // crossing guessed from no information is exactly what must never be offered.
                Measured[i] = false;
                Floor[i] = float.NaN;
                _done = true;
                _sidesDone = true;
                API.LogInfo("[SF6Access] Jump scan: the floor probe could not run; no crossing is offered");
                return;
            }
            Measured[i] = true;
            Floor[i] = floor;
        }
        if (_cursor < Samples) return;

        // The line is in: read the flight, then queue one sideways probe pair per step.
        _hasFlight = NarrowPath.TryRead(i => Floor[i], i => Measured[i], Samples, _player.Y,
                                        SampleStepM, FlightReachM, out _flight);
        if (_hasFlight)
            for (int i = _flight.FirstStep; i <= _flight.Top && _probeCount + 1 < MaxProbes; i++)
            {
                ProbeI[_probeCount] = i; ProbeLeft[_probeCount] = true; _probeCount++;
                ProbeI[_probeCount] = i; ProbeLeft[_probeCount] = false; _probeCount++;
            }
        if (_probeCount == 0) _sidesDone = true;
    }

    /// <summary>The sides of the flight: one ray a frame from each step out to the left and
    /// to the right, folded through the same classifier the radar uses. A wall reads as a
    /// wall; a floor level with the step reads as a floor; nothing at all within the probe's
    /// drop is the cliff.</summary>
    private static void PumpSides()
    {
        for (int k = 0; k < RaysPerPump && _probeCursor < _probeCount; k++)
        {
            int p = _probeCursor++;
            int i = ProbeI[p];
            bool left = ProbeLeft[p];
            var side = ProbeSide(i, left);
            if (left) SideLeft[i] = side; else SideRight[i] = side;
        }
        if (_probeCursor < _probeCount) return;
        _sidesDone = true;

        // Roll the two hands up per step, for the log and for the caller that speaks the stair.
        if (_hasFlight)
            for (int i = _flight.FirstStep; i <= _flight.Top; i++)
                FlightSides.Add((i, SideLeft[i], SideRight[i]));
    }

    /// <summary><b>What is beside step <paramref name="i"/>, on one hand.</b> Two
    /// measurements, because a cliff is an absence and an absence of a wall is not a
    /// floor: a horizontal ray says whether anything stands out there, and a floor probe at
    /// the probe's reach says whether anything can be stood on out there.
    ///
    /// <list type="bullet">
    /// <item>A contact before the reach that classifies as a wall (not a step, not ground)
    ///   is <see cref="NarrowPath.Side.Wall"/> — you cannot step off here.</item>
    /// <item>No wall, and a floor within <see cref="SideFloorM"/> of the step, is
    ///   <see cref="NarrowPath.Side.Floor"/> — level with the stair, a place you could
    ///   stand.</item>
    /// <item>No wall and no such floor is <see cref="NarrowPath.Side.Drop"/> — air. This is
    ///   the cliff.</item>
    /// <item>A probe that could not run is <see cref="NarrowPath.Side.Unknown"/>, which no
    ///   caller may read as either.</item>
    /// </list></summary>
    private static NarrowPath.Side ProbeSide(int i, bool left)
    {
        float t = At(i);
        float px = _player.X + _dirX * t, pz = _player.Z + _dirZ * t;
        // The ray leaves at the step's own floor plus a body's waist — the same height the
        // radar's own beams leave — so "a wall" means a wall beside the stair at walking
        // height, not a kerb the step already clears.
        float oy = Floor[i] + WaistAboveFloorM;

        // Left and right of the direction of travel, in the world ground plane.
        float dx = left ? -_dirZ : _dirZ;
        float dz = left ? _dirX : -_dirX;
        var dir = new FieldDirectionService.FlatDir(dx, dz, true);
        if (!FieldRayCaster.TryCastAcross(px, oy, pz, dir, SideReachM, out var hit))
            return NarrowPath.Side.Unknown;

        // The far point of the probe, where the floor question is asked.
        float fx = px + dx * SideReachM, fz = pz + dz * SideReachM;
        if (!FieldRayCaster.TryFloorBelow(fx, Floor[i] + ProbeAboveM, fz, ProbeDropM, out float far))
            return NarrowPath.Side.Unknown;   // could not measure the floor out there

        // A wall blocks the way off. hit.Distance is 0 when the ray met nothing.
        bool wall = hit.Hit && hit.Class == HitClass.Wall && float.IsFinite(hit.Distance) && hit.Distance <= SideReachM;
        if (wall) return NarrowPath.Side.Wall;

        // Nothing in the way: a floor level with the step is standable, anything else is air.
        if (float.IsFinite(far) && MathF.Abs(far - Floor[i]) <= SideFloorM) return NarrowPath.Side.Floor;
        return NarrowPath.Side.Drop;
    }

    /// <summary>Turn the measured floors into crossings: the first step up, then every gap,
    /// and then — the owner's ask — the whole flight of a stair, one link per riser, each of
    /// whose sides has been read.</summary>
    private static void Build()
    {
        // --- a step up: a floor within the owner's limit, ahead of the player ---
        for (int i = 0; i < Samples; i++)
        {
            if (!Measured[i] || float.IsNaN(Floor[i])) continue;
            if (!JumpLink.StepsUp(Floor[i], _player.Y)) continue;
            // The near end is the last sample that was still the player's own floor, so
            // the walk to it is the walk the player is already on.
            int before = -1;
            for (int j = i - 1; j >= 0; j--)
                if (Measured[j] && !float.IsNaN(Floor[j]) && MathF.Abs(Floor[j] - _player.Y) < JumpLink.MinRiseM)
                { before = j; break; }
            var near = before >= 0 ? Sample(before, Floor[before]) : _player;
            Add(near, Sample(i, Floor[i]));
            break;
        }

        // --- a gap: a stretch with no floor, between two floors within a jump ---
        int hole = -1;
        for (int i = 0; i < Samples; i++)
        {
            bool solid = Measured[i] && !float.IsNaN(Floor[i]);
            if (!solid) { if (hole < 0) hole = i; continue; }
            if (hole < 0) continue;

            int before = hole - 1;
            if (before >= 0 && Measured[before] && !float.IsNaN(Floor[before]))
            {
                // The two solid samples bound the hole, so the distance between them is
                // the widest the hole can be — the honest direction to be wrong in.
                float width = (i - before) * SampleStepM;
                if (JumpLink.Crossable(width) && JumpLink.Landable(Floor[i], Floor[before])
                    && MathF.Abs(Floor[before] - _player.Y) <= JumpLink.StepM)
                    Add(Sample(before, Floor[before]), Sample(i, Floor[i]));
            }
            hole = -1;
        }

        // --- the flight of a stair: one link per riser, from the player's own floor up ---
        if (_hasFlight) AddFlight();
    }

    /// <summary>One link per riser of the flight, so the planner walks the stair a step at a
    /// time. The links are added only where the flight's own sides say this is a stair the
    /// player can be sent up; a flight with its head in a wall is left to the single step
    /// already offered, which is the honest reading of "somewhere up there, not a way".
    ///
    /// <para><b>A down-flight emits its links downward, since 2026-10-05.</b> This used to
    /// keep only the samples that RISED over the one before it (<c>y - prevY &lt; MinRiseM</c>),
    /// which is true of every tread of a climb and false of every tread of a descent — so a
    /// down-flight was measured, logged, and then silently dropped from the plan with no link
    /// at all. It now follows the flight's own <see cref="NarrowPath.Flight.GoesDown"/>: the
    /// riser test is signed the same way the flight is, and the link is a stair link rather
    /// than a jump, so the route can say "down" in the one sentence that must say "down".</para>
    ///
    /// <para>A descent's links are still only offered when both of its sides were measured and
    /// neither is air (<see cref="NarrowPath.SafeToDescend"/>) — the same guard the whole
    /// project holds to, because a mis-read climb costs metres and a mis-read descent walks a
    /// blind player off an edge.</para></summary>
    private static void AddFlight()
    {
        var f = _flight;
        bool down = f.GoesDown;
        if (down)
        {
            var sides = FlightSidesRead();
            if (!NarrowPath.SafeToDescend(sides))
            {
                API.LogInfo("[SF6Access] Jump scan: a down-flight ahead, refused - a side is air or was not measured");
                return;
            }
        }

        // The foot is the player's own floor: the last sample below the first riser.
        int foot = -1;
        for (int j = f.FirstStep - 1; j >= 0; j--)
            if (Measured[j] && float.IsFinite(Floor[j])) { foot = j; break; }

        float prevY = foot >= 0 ? Floor[foot] : _player.Y;
        float prevX = foot >= 0 ? Sample(foot, 0f).X : _player.X;
        float prevZ = foot >= 0 ? Sample(foot, 0f).Z : _player.Z;

        for (int i = f.FirstStep; i <= f.Top && Found.Count < MaxLinks; i++)
        {
            if (!Measured[i] || !float.IsFinite(Floor[i])) continue;
            float y = Floor[i];
            float change = y - prevY;
            // The riser test follows the way the flight goes: a tread of a climb is higher
            // than the one before it, a tread of a descent is lower.
            if (down ? -change < JumpLink.MinRiseM : change < JumpLink.MinRiseM) continue;
            AddStair(new Spot(prevX, prevY, prevZ), Sample(i, y), down);
            prevY = y;
            prevX = Sample(i, 0f).X;
            prevZ = Sample(i, 0f).Z;
        }
    }

    /// <summary>The flight's two hands, rolled up over its own treads: a side counts as a
    /// drop only if some tread of the flight has air on it, and as unmeasured if any tread
    /// could not be probed. Both are the honest direction to be wrong in — one drop anywhere
    /// along the flight is enough to make it a ledge.</summary>
    private static NarrowPath.Sides FlightSidesRead()
    {
        bool known = FlightSides.Count > 0;
        bool cliff = false, wall = false;
        foreach (var (_, left, right) in FlightSides)
        {
            if (left == NarrowPath.Side.Unknown || right == NarrowPath.Side.Unknown) { known = false; break; }
            if (left == NarrowPath.Side.Drop || right == NarrowPath.Side.Drop) cliff = true;
            if (left == NarrowPath.Side.Wall || right == NarrowPath.Side.Wall) wall = true;
        }
        return new NarrowPath.Sides(known, cliff, wall, false, false, false, false);
    }

    /// <summary>A stair's link, which the planner must be told is a STAIR and not a jump, and
    /// whose ends it names by height. A descent's near end is the higher one, so the ordering
    /// flips with the way the flight goes.</summary>
    private static void AddStair(Spot near, Spot far, bool down)
    {
        if (Found.Count >= MaxLinks) return;
        var link = down ? new Link(far, near, false, true) : new Link(near, far, false, true);
        Found.Add(link);
    }

    private static void Add(Spot near, Spot far)
    {
        if (Found.Count >= MaxLinks) return;
        // The planner names the two ends by height; which way the player crosses is its
        // own question, asked and answered with real queries, not assumed here.
        var link = far.Y < near.Y ? new Link(far, near, true) : new Link(near, far, true);
        Found.Add(link);
    }

    private static float At(int i) => FirstSampleM + i * SampleStepM;

    private static Spot Sample(int i, float y) =>
        new(_player.X + _dirX * At(i), y, _player.Z + _dirZ * At(i));

    /// <summary>One line with every measurement and every crossing, so a route through a
    /// jump or a stair can be checked from the log alone — the tester cannot see the screen,
    /// and a crossing guessed wrong is worse than none.</summary>
    private static void Log()
    {
        var sb = new StringBuilder("[SF6Access] Jump scan: player ");
        sb.Append(Show(_player)).Append(" to ").Append(Show(_target)).Append("; floors");
        for (int i = 0; i < Samples; i++)
            sb.Append(FormattableString.Invariant($" {At(i):F1}m=")).Append(
                !Measured[i] ? "unread" : float.IsNaN(Floor[i]) ? "HOLE" : Floor[i].ToString("F2"));
        sb.Append("; crossings ");
        sb.Append(Found.Count == 0 ? "none" : string.Join("; ", Found.ConvertAll(l =>
            FormattableString.Invariant($"{(JumpLink.IsStepUp(l.Low.Y, l.High.Y) ? "step" : "gap")} {Show(l.Low)} -> {Show(l.High)}"))));
        sb.Append(StairText());
        API.LogInfo(sb.ToString());
    }

    /// <summary>The stair half of the line: how many risers were read, how tall they climb,
    /// and which hand the cliff and the wall are on. Empty when no flight was read.</summary>
    private static string StairText()
    {
        if (!_hasFlight) return "";
        var f = _flight;
        var sides = NarrowPath.ReadSides(SidesOf(f, SideLeft), SidesOf(f, SideRight));
        var sb = new StringBuilder();
        sb.Append(FormattableString.Invariant(
            $"; stair {f.RiserCount} risers x {f.RiserM:F2} m, {(f.GoesDown ? "down" : "up")} {MathF.Abs(f.TotalRiseM):F2} m over {f.RunM:F1} m"));
        // The two hands, named as the owner asked: a cliff and a wall, or neither.
        sb.Append(", sides L=").Append(SideWord(SidesOf(f, SideLeft)));
        sb.Append(" R=").Append(SideWord(SidesOf(f, SideRight)));
        if (sides.CliffAndWall) sb.Append(" (cliff and wall)");
        return sb.ToString();
    }

    /// <summary>The commonest hand reading across the flight's steps: a side that is a cliff
    /// on every step is the stair's cliff, and one odd step of noise must not rename it. A
    /// tie falls back to the first step's reading.</summary>
    private static NarrowPath.Side SidesOf(NarrowPath.Flight f, NarrowPath.Side[] side)
    {
        int floor = 0, drop = 0, wall = 0, unknown = 0;
        for (int i = f.FirstStep; i <= f.Top; i++)
            switch (side[i])
            {
                case NarrowPath.Side.Floor: floor++; break;
                case NarrowPath.Side.Drop: drop++; break;
                case NarrowPath.Side.Wall: wall++; break;
                default: unknown++; break;
            }
        int n = f.Top - f.FirstStep + 1;
        if (drop >= floor && drop >= wall && drop >= unknown) return NarrowPath.Side.Drop;
        if (wall >= floor && wall >= drop && wall >= unknown) return NarrowPath.Side.Wall;
        if (floor >= unknown) return NarrowPath.Side.Floor;
        return NarrowPath.Side.Unknown;
    }

    private static string SideWord(NarrowPath.Side s) => s switch
    {
        NarrowPath.Side.Floor => "floor",
        NarrowPath.Side.Drop => "CLIFF",
        NarrowPath.Side.Wall => "wall",
        _ => "?",
    };

    private static string Show(Spot s) => FormattableString.Invariant($"({s.X:F1}, {s.Y:F1}, {s.Z:F1})");
}
