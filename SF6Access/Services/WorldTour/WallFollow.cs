using System;
using System.Collections.Generic;
using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>F4 — 右手扶墙：沿着墙（没有墙就沿着悬崖边）走一圈，一步一步念出来。</b>
///
/// <para><b>What the owner asked for (2026-10-04, verbatim):</b>
/// <i>"做一个右手扶墙的导航，按下f4打开，打开后他带着我贴着墙走，直到一整圈或再次按f4结束，
/// 每次按都从当前所在地开始贴墙走，注意，如果没有墙就贴着悬崖边走，高度大于一点六米才算悬崖，
/// 否则当作平地"</i></para>
///
/// <para><b>Nothing here moves the avatar.</b> The owner's standing choice — "不动角色，只做逐步
/// 口令语音引导" — holds here as it holds everywhere: this is a voice, one sentence per step, and
/// the player walks it. The only engine calls are the two read-only casts the rest of the field
/// navigation already lives on (<see cref="FieldRayCaster.TryCastAcross"/>,
/// <see cref="FieldRayCaster.TryFloorBelow"/>), and not one navmesh question.</para>
///
/// <para><b>How a step is chosen.</b> The hand is on the RIGHT, so the walk asks about the right
/// side first and the ground second:</para>
/// <list type="number">
/// <item><b>What is under the right hand.</b> One sideways ray for a wall, three floor samples for
/// an edge. Whichever is nearer is what the hand is holding; neither is "nothing to hold".</item>
/// <item><b>Which way the hand turns.</b> Holding something: straight on while the way ahead is
/// open; when it is not, LEFT (the thing being held has turned that way), then right, then about.
/// Holding nothing: RIGHT first — that is where the thing being held went — then straight, then
/// left, then about.</item>
/// <item><b>How near to keep it.</b> A wall is kept at <see cref="WallKeepM"/>, a cliff edge at
/// <see cref="CliffKeepM"/> — further in from an edge than from a wall, because the one thing this
/// must never do is walk the owner off the lip of the floor they are standing on. The correction is
/// a slide of at most <see cref="MaxSlideM"/> a step, so a walk never cuts a corner.</item>
/// <item><b>And only then is any of it said.</b> Every step is measured along the STRAIGHT line it
/// describes before a word of it is spoken (<see cref="SampleSafeM"/>), because a route proved along
/// a polyline and a sentence describing a straight line are two different things, and the gap
/// between them has already put the owner on the edge of a platform once. The ground under that
/// line AND the space along it are both measured, and the number spoken is never larger than the
/// smaller of the two.</item>
/// <item><b>A step is answered by walking, not by waiting.</b> No next step, and no next metre of
/// distance, is credited until the player has actually moved (<see cref="PumpReady(float, float, float)"/>) —
/// a walk that hands itself out on the clock invents route nobody walked.</item>
/// </list>
///
/// <para><b>A cliff is the owner's own number.</b> <see cref="CliffM"/> is 1.6 m: deeper than that
/// is an edge worth holding on to, shallower is flat ground and walked straight over. It is
/// deliberately NOT the 1.2 that <see cref="WalkSteps"/> refuses a step over — that one is a safety
/// refusal about one guided leg, this one is the owner's definition of what counts as an edge when
/// there is no wall. Nothing else in the mod reads this number.</para>
///
/// <para><b>Starting, and starting again.</b> The first look-around searches the whole compass for
/// something to hold — a wall by ray, an edge by the ground running out. Found beyond arm's reach,
/// the walk first goes and stands beside it, and only then puts its hand on it and follows it.
/// Nothing within reach at all is said out loud rather than guessed at, and the search repeats
/// while the player walks.</para>
///
/// <para><b>A round ends at the beginning.</b> The start is where F4 was pressed, and the walk ends
/// when the player comes back to it having gone far enough to have gone somewhere
/// (<see cref="MinRoundM"/>). Pressing F4 again ends it anywhere; the next press starts again from
/// wherever the player is then — "每次按都从当前所在地开始".</para>
///
/// <para><b>Nothing is taken away.</b> This speaks where <see cref="WalkSteps"/> speaks and yields
/// the way every other speech site here does; the homing cue and the bearing keep playing exactly as
/// they did. It is an addition, and an addition is the only thing it is allowed to be.</para>
/// </summary>
internal static class WallFollow
{
    // ---------------------------------------------------------------- the owner's number

    /// <summary><b>A drop deeper than this is a cliff; anything shallower is flat ground.</b>
    /// The owner's own figure, 2026-10-04: "高度大于一点六米才算悬崖，否则当作平地". It is the
    /// only 1.6 in the mod, and it is not the 1.2 that refuses a guided step — that one says "I will
    /// not walk you over that", this one says "that is not an edge, walk over it".</summary>
    private const float CliffM = 1.6f;

    // ---------------------------------------------------------------- the walk

    /// <summary>One spoken step: far enough to be worth a sentence, near enough that the player
    /// never loses the thread of it.</summary>
    private const float StepM = 2.5f;

    /// <summary>How far ahead the sideways ray looks. More than a step, so a wall two and a half
    /// metres off is not called open ground.</summary>
    private const float ProbeM = 3.2f;

    /// <summary>A wall nearer than this blocks the step: the step is <see cref="StepM"/> long and
    /// the body walking it is not a point.</summary>
    private const float BlockM = StepM + 0.3f;

    /// <summary>How far the right hand reaches when it asks what it is holding.</summary>
    private const float SideM = 1.8f;

    /// <summary>Where the ground is sampled to the right. A cliff is found by the ground running
    /// out, and one sample cannot tell "runs out at half a metre" from "runs out at one and a
    /// half".</summary>
    private static readonly float[] SideSamples = { 0.7f, 1.1f, 1.5f };

    /// <summary>How far from a WALL the walk keeps. The owner's own figure, tightened
    /// 2026-10-04 ("把扶墙的距离减少到零点五米"): the hand nearly touches it, which is what
    /// following a wall by hand actually feels like. Note it is STILL further than zero —
    /// the body has a radius, and a wall kept at 0 is a wall being walked into.</summary>
    private const float WallKeepM = 0.5f;

    /// <summary>How far from a CLIFF EDGE the walk keeps — deliberately double
    /// <see cref="WallKeepM"/> (the owner's figure, 2026-10-04: 「距离悬崖一米」). This is the
    /// number standing between "following the edge" and "walking the owner off it", and there
    /// is no sentence this mod could say afterwards that would make up for getting it
    /// wrong.</summary>
    private const float CliffKeepM = 1.0f;

    /// <summary>Most a single step may slide sideways towards or away from what the hand holds.
    /// Small, so a step is never a diagonal across ground that was never measured.</summary>
    private const float MaxSlideM = 0.8f;

    /// <summary>How far short of something that blocks it a step must stop. The body is not a
    /// point and a number spoken downwards is a promise that was measured, while one spoken
    /// upwards is a promise invented by rounding. Nothing is ever said past this.</summary>
    private const float NoseM = 0.5f;

    /// <summary>Height of the sideways ray above the feet — the height <see cref="WalkSteps"/>
    /// measured with: chest height clears a kerb and still meets a hoarding.</summary>
    private const float HoverM = 0.9f;

    /// <summary>Where the floor probe starts above the feet, and how far down it looks. Deeper than
    /// <see cref="CliffM"/> by a wide margin, so a drop is MEASURED and not merely noticed.</summary>
    private const float FloorAboveM = 0.8f;
    private const float FloorDropM = 4.0f;

    /// <summary>How many times the ground is sampled along the line a step describes. Fine enough
    /// that a hole between two samples is not walked into.</summary>
    private const int SafeSamples = 5;

    /// <summary>A step with less ground under it than this is not a step at all.</summary>
    private const float MinReachM = 1.0f;

    /// <summary><b>How far the player must actually have walked before the next step is handed
    /// over.</b> A step is answered by walking it, not by waiting — see
    /// <see cref="PumpAt(float, float, float)"/> for what happened without this.</summary>
    private const float MoveM = 0.7f;

    /// <summary>How near the player must come before the next step is handed over. Wider than a
    /// point: a walk followed by ear is not walked to the centimetre. Always read together with
    /// <see cref="MoveM"/>: arriving without moving is standing still.</summary>
    private const float ArriveM = 2.0f;

    /// <summary>Further than this from the step they were given, the player is not walking it —
    /// the walk belongs to wherever they are now.</summary>
    private const float OffRouteM = 8f;

    /// <summary>Back within this of where F4 was pressed, having gone at least
    /// <see cref="MinRoundM"/>, is a whole round.</summary>
    private const float RoundM = 3.0f;
    private const float MinRoundM = 12f;

    /// <summary>How many steps with nothing under the hand before the walk goes looking again.
    /// One is a corner opening out; two is a handhold that is gone.</summary>
    private const int LostSteps = 2;

    /// <summary>Directions the opening look-around tries — the compass the bearings are spoken in,
    /// so "the wall is at 3 o'clock" and "the wall is in sector 2" cannot disagree.</summary>
    private const int SeekSectors = 8;

    /// <summary>How far the opening look-around reaches. Further than the hand does: at the start
    /// there may be nothing within arm's length, and then the walk has to go and find some.</summary>
    private const float SeekM = 6f;

    /// <summary>Where the look-around samples the ground in each direction, near then far.</summary>
    private static readonly float[] SeekFloors = { 1.2f, 2.5f };

    /// <summary>Two casts a tick: the rate <see cref="WalkSteps"/> and <see cref="RoundPlanner"/>
    /// already run at, so this stays inside what the field sensor was already paying.</summary>
    private const int RaysPerPump = 2;

    /// <summary>How often a look-around is repeated while there is nothing at all to hold. Long
    /// enough to cost nothing, short enough that a player who has walked up to a wall is not left
    /// waiting for it.</summary>
    private const long SeekAgainMs = 4000;

    private const long SpeakHoldMs = 1500;
    private const long RepeatMs = 12000;

    /// <summary>Never more than this many directions measured for one step.</summary>
    private const int MaxTries = 4;

    // ---------------------------------------------------------------- stairs (2026-10-05)

    /// <summary><b>How far ahead the stair line is laid.</b> The line is what a flight of steps
    /// is read from (<see cref="StairStep"/>), so it is not a proximity probe but the ground
    /// the sentence is about: <see cref="StairStep.ReachM"/> is the honest ceiling on what it
    /// may claim, and this reaches past it so the samples do not run out before the reach
    /// does.</summary>
    private const float StairLineM = StairStep.FirstSampleM
        + (StairStep.Samples - 1) * StairStep.SampleStepM;

    /// <summary>Where a stair sample starts above the feet. Higher than the ordinary floor probe
    /// (<see cref="FloorAboveM"/> = 0.8 m) because a stair sample is taken a stride and a half
    /// AHEAD, where the ground is already a riser or more above the player: from 0.8 m a probe
    /// would start inside the step it is meant to measure and the cast would miss it entirely.
    /// It is <see cref="StairCatalog"/>'s own 1.6 for the same reason.</summary>
    private const float StairLiftM = 1.6f;

    /// <summary><b>How much the ground may climb or fall between two samples along a stair step
    /// and still be the same flight.</b> It is <see cref="JumpLink.StepM"/> — the step limit the
    /// owner set on 2026-10-03 and the one <see cref="WalkSteps"/> refuses a guided leg over. On
    /// a stair it means "one riser", because a riser is what a stride of samples crosses; on
    /// level ground the ordinary <see cref="CliffM"/> applies instead. Deliberately NOT the same
    /// number as <see cref="CliffM"/>: that one judges a drop from the player's own floor, this
    /// one judges a change from the sample before.</summary>
    private const float StairClimbM = 1.2f;

    /// <summary><b>How much the ground must rise to be a stair rather than a step.</b> One
    /// riser is a kerb — <see cref="WalkSteps"/> and the ordinary step already walk over it —
    /// and naming a kerb a stair would put a stair in every doorway. <see cref="StairStep"/>
    /// enforces this too; it is restated so the log line can say which number refused.</summary>
    private const int StairMinRisers = 2;

    /// <summary>How far to the side of a descending flight the ground is probed before it may be
    /// walked down: two body-widths, the same reach <see cref="StairCatalog"/> and
    /// <see cref="JumpLinkCatalog"/> probe a stair's sides at. Only used to REFUSE a descent
    /// (<see cref="DescentSafe"/>), never to grant one.</summary>
    private const float StairSideReachM = 1.2f;

    // ---------------------------------------------------------------- what the hand holds

    private const int HOLD_NONE = 0, HOLD_WALL = 1, HOLD_CLIFF = 2, HOLD_STAIR = 3;

    private enum Stage { Idle, Seek, Measure, Validate, StairValidate, Ready, Failed }

    /// <summary>One thing to measure. <see cref="Side"/> is a horizontal ray from
    /// (<see cref="X"/>, <see cref="Y"/>, <see cref="Z"/>) along (<see cref="DX"/>, <see cref="DZ"/>)
    /// for <see cref="Dist"/> metres; otherwise it is a floor probe straight down from that point
    /// for <see cref="Dist"/> metres. Both are the casts every other search here uses.
    ///
    /// <para><see cref="Lane"/> is the third kind and the reason the field exists: a floor probe
    /// laid out as ONE LINE of samples along the way ahead, which is what a flight of stairs can
    /// be read from (<see cref="StairStep"/>). It is a floor probe — the same cast — but its
    /// answer is only meaningful as a sequence, so it is queued and settled apart from the
    /// per-direction probes whose indices <see cref="ReadHold"/>/<see cref="OpenAhead"/> read
    /// positionally. Putting the stair line in that list would silently renumber every hand and
    /// every direction, which is how a walk beside a cliff reads the wrong side.</para></summary>
    private readonly struct Probe
    {
        public readonly float X, Y, Z, DX, DZ, Dist;
        public readonly bool Side;

        private Probe(float x, float y, float z, float dx, float dz, float dist, bool side)
        { X = x; Y = y; Z = z; DX = dx; DZ = dz; Dist = dist; Side = side; }

        public static Probe Ray(float x, float y, float z, float dx, float dz, float m)
            => new(x, y, z, dx, dz, m, true);

        public static Probe Floor(float x, float y, float z, float drop)
            => new(x, y, z, 0f, 0f, drop, false);
    }

    // ---------------------------------------------------------------- state

    private static readonly List<Probe> _queue = new();
    private static readonly List<float> _val = new();
    private static readonly List<bool> _ran = new();

    private static Stage _stage = Stage.Idle;
    private static int _cursor;
    /// <summary>Where the measurements of the current stage were taken from: the ground under
    /// the player at the moment the queue was built, which is what every floor reading is
    /// judged against. <see cref="_probeX"/>/<see cref="_probeZ"/> are the same point on the
    /// other two axes — the stair line is cast from the feet, not from the avatar, so that a
    /// reading taken over several frames is taken about one standing place.</summary>
    private static float _probeY, _probeX, _probeZ;

    private static bool _on;
    private static float _startX, _startZ;
    private static float _walkedM;
    private static int _stepIndex;
    private static float _stepX, _stepZ, _stepLenM;
    private static bool _stepEdge, _stepApproach, _stepBlocked;
    // Where the player stood when the step now in their ear was handed to them. Progress is
    // measured from HERE and nowhere else: planned metres belong to the sentence, walked
    // metres belong to their feet.
    private static float _giveX, _giveZ;
    private static bool _waitLogged;
    private static float _fx = 1f, _fz;              // the heading the walk is following
    private static int _hold;                        // what the right hand is holding
    private static float _holdM;                     // ...and how far away it is
    private static int _lostSteps;
    private static long _seekAt;
    private static bool _saidNorail;

    // Standing beside what was found before following it: the walk goes to the wall first.
    private static float _approachM, _apprX, _apprZ;

    // ---------------------------------------------------------------- the stair line
    //
    // The stair's three numbers are kept apart from every other reading in this file on
    // purpose: the five slots the queue above is read out of positionally are the hand and the
    // four turns, and a fifth meaning sharing them would have to be counted in ReadHold,
    // OpenAhead, SettleValidate and Lay all at once. This is its own little line.
    private static readonly float[] _stairFloor = new float[StairStep.Samples];
    private static readonly bool[] _stairOk = new bool[StairStep.Samples];
    private static int _stairAt;
    private static float _stairWalkM;             // how far the stair step is walked
    private static NarrowPath.Flight _stairFlight; // what was read, for the log and the words

    private static readonly float[] Order = new float[MaxTries];
    private static int _tryIdx, _tryCount;
    private static float _tryTurn;
    private static float _tryX, _tryZ, _tryLenM;
    // Which slot of a validation holds the ray along line being walked: it is queued last, so
    // the floor samples keep their own indices and nothing else has to know about it.
    private static int _aheadSlot;

    private static bool _dirty, _arrived;
    private static long _nextSpeak, _nextRepeat;

    /// <summary>F4 is holding the floor: the beacon yields the sentence to it.</summary>
    internal static bool On => _on;

    /// <summary>One line of wall-following wording, for the hook that owns the key.</summary>
    internal static string Text(string key, string fallback) => LangFile.Get("wt.wall." + key, fallback);

    // ---------------------------------------------------------------- the key

    /// <summary>F4 pressed: start from wherever the player stands right now, or stop.</summary>
    internal static void Toggle()
    {
        if (_on)
        {
            _on = false;
            Reset();
            Speak(Text("off", "Wall following off"));
            return;
        }

        var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        if (!me.ok)
        {
            Speak(Text("nopos", "Cannot read where you are standing - press F4 again in a moment"));
            return;
        }

        _on = true;
        Reset();
        _startX = me.x; _startZ = me.z;
        _walkedM = 0f;
        _stepIndex = 0;
        _arrived = false;
        // Facing the way the player is facing: the first sentence has to be a direction they can
        // push, and the camera's forward is the frame every other bearing in this mod is spoken in.
        var forward = FieldDirectionService.GetCameraForward();
        if (forward.Ok) { _fx = forward.X; _fz = forward.Z; }
        else { _fx = 1f; _fz = 0f; }

        API.LogInfo(FormattableString.Invariant(
            $"[SF6Access] Wall: started at ({me.x:F1}, {me.y:F1}, {me.z:F1}) facing ({_fx:F2}, {_fz:F2})"));
        // Said at once, before a single ray has been cast: a blind player pressing a key has to
        // hear that the press landed. What the hand finds is said when it finds it.
        Speak(Text("on", "Wall following on. Looking for something to put your right hand on"));
        BeginSeek(me.x, me.y, me.z);
    }

    /// <summary>A new field, a reload, F4 pressed to stop: forget the walk entirely.</summary>
    internal static void Reset()
    {
        _stage = Stage.Idle;
        _queue.Clear(); _val.Clear(); _ran.Clear();
        _cursor = 0;
        _hold = HOLD_NONE;
        _holdM = float.NaN;
        _stairAt = 0;
        _stairWalkM = 0f;
        _stairFlight = default;
        for (int i = 0; i < _stairOk.Length; i++) { _stairOk[i] = false; _stairFloor[i] = float.NaN; }
        _lostSteps = 0;
        _tryIdx = _tryCount = 0;
        _tryTurn = 0f;
        _approachM = 0f;
        _dirty = _arrived = false;
        _stepApproach = false;
        _stepBlocked = false;
        _waitLogged = false;
        _nextSpeak = _nextRepeat = 0;
        _seekAt = 0;
        _saidNorail = false;
        // The beacon belongs to the walk that owns the step; nothing of it survives the walk.
        StepBeacon.Reset();
    }

    // ---------------------------------------------------------------- per frame

    /// <summary>Advances the measurement by at most <see cref="RaysPerPump"/> casts.</summary>
    internal static void Pump()
    {
        if (!_on) return;
        var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        if (!me.ok) return;
        PumpAt(me.x, me.y, me.z);
        Sound(me.x, me.z);
    }

    /// <summary><b>The sounding half: every step is a beacon too, not only a sentence.</b> The owner
    /// asked for both (2026-10-04: "我不只要语音导航还要信标导航"), and the reason is the one already
    /// written into <see cref="StepBeacon"/>: a step that is only spoken has to be remembered while
    /// walking, and the same step sounded is something that can be walked toward without holding it
    /// in the head. Voice and beacon always name THE SAME point — a beacon pointing one way while
    /// the words describe another is the one pairing that cannot be trusted by ear.</summary>
    private static void Sound(float px, float pz)
    {
        if (_stage == Stage.Ready) StepBeacon.Tick(px, pz, _stepX, _stepZ);
        else StepBeacon.Reset();     // between steps: quiet, and ready to ping at once on the next
    }

    private static void PumpAt(float px, float py, float pz)
    {
        if (_stage == Stage.Ready) { PumpReady(px, py, pz); return; }
        if (_stage == Stage.Failed)
        {
            long now = Environment.TickCount64;
            if (_seekAt == 0 || now < _seekAt) return;
            _seekAt = 0;
            BeginSeek(px, py, pz);
            return;
        }
        if (_stage == Stage.Idle) { BeginMeasure(px, py, pz); return; }

        // The stair line is cast FIRST and by itself, because it is the reading that decides
        // which stage this measurement is even running: a flight of steps under the way ahead
        // is walked up, not asked about sideways. It is one line of floor probes — the cast the
        // radar already makes thirty times a second — and it does not touch _queue/_cursor,
        // which the four directions and the hand are read out of positionally.
        if (_stage == Stage.Measure && _stairAt < StairStep.Samples)
        {
            int budget0 = RaysPerPump;
            while (budget0-- > 0 && _stairAt < StairStep.Samples) CastStair(_stairAt++);
            if (_stairAt < StairStep.Samples) return;
        }

        int budget = RaysPerPump;
        while (budget > 0 && _cursor < _queue.Count)
        {
            Cast(_cursor);
            _cursor++;
            budget--;
        }
        if (_cursor < _queue.Count) return;
        Settle(px, py, pz);
    }

    /// <summary>One sample of the stair line: the ground straight down under a point a stride
    /// and a half ahead of the player. The same probe <see cref="FieldRayCaster.TryFloorBelow"/>
    /// the round planner's hops and the floor of every validated step already come from.</summary>
    private static void CastStair(int i)
    {
        float t = StairStep.SampleAt(i, StairStep.SampleStepM);
        bool ran = FieldRayCaster.TryFloorBelow(_probeX + _fx * t, _probeY + StairLiftM,
                                                _probeZ + _fz * t, StairLiftM + FloorDropM,
                                                out float floorY);
        _stairOk[i] = ran;
        _stairFloor[i] = floorY;   // NaN is a hole, which is one of the two things that ends a flight
    }

    /// <summary><b>A step is answered by walking it, not by waiting.</b> This is the gate every
    /// hand-over now goes through, and it exists because of what the owner reported on
    /// 2026-10-04: <i>「我一步都没动他自己继续往下指」</i>.</para>
    ///
    /// <para>The walk handed out step after step with nobody walking them. Arrival was judged as
    /// "the player is within <see cref="ArriveM"/> of the step's end", and a step shortened to
    /// 1.0 m by the floor probe is shorter than that margin — so the moment it was spoken the
    /// player was already "there", the planned 1.0 m was credited to the distance walked, and the
    /// next step was measured from the same unmoved spot, three or four times a second. In one
    /// standing minute: step number 161, "221 m walked", back at the starting point, and the same
    /// one-metre sentence on a loop. The beacon pointed into a game-generated future because the
    /// walk believed its own plan rather than the player's feet.</para>
    ///
    /// <para>So: nothing is handed over, and no distance is credited, until the feet have moved
    /// <see cref="MoveM"/> from where the step was handed out. Until then the step stands, the
    /// beacon keeps pointing at the SAME point, and the sentence repeats — never silence, and
    /// never a sentence about ground nobody has walked onto. Distance walked is what was walked,
    /// not what was planned.</para></summary>
    private static void PumpReady(float px, float py, float pz)
    {
        float moved = Flat(px, pz, _giveX, _giveZ);
        if (moved < MoveM)
        {
            if (!_waitLogged)
            {
                _waitLogged = true;
                API.LogInfo(FormattableString.Invariant(
                    $"[SF6Access] Wall: waiting - given from ({_giveX:F1}, {_giveZ:F1}), ") +
                    FormattableString.Invariant($"{moved:F1} m moved of the {MoveM:F1} m I need"));
            }
            return;
        }
        _waitLogged = false;

        // The player has left the step they were given: the walk belongs to where they are now.
        if (Flat(px, pz, _stepX, _stepZ) > OffRouteM)
        {
            API.LogInfo("[SF6Access] Wall: left the step that was given - measuring again from here");
            _walkedM += moved;
            _arrived = false;
            BeginMeasure(px, py, pz);
            return;
        }
        if (Flat(px, pz, _stepX, _stepZ) > ArriveM) return;   // walking it - nothing to hand over yet

        _walkedM += moved;
        if (_walkedM >= MinRoundM && Flat(px, pz, _startX, _startZ) <= RoundM)
        {
            API.LogInfo(FormattableString.Invariant(
                $"[SF6Access] Wall: a whole round - {_walkedM:F0} m walked, back at the start"));
            _on = false;
            Reset();
            Speak(Text("round",
                "That is a whole round - you are back where you started. Wall following has ended"));
            return;
        }
        _arrived = true;
        // Standing beside it now: the hand goes on and the following starts.
        if (_approachM > 0f)
        {
            _approachM -= moved;
            if (_approachM > ArriveM)
            {
                SetApproach(px, py, pz, MathF.Min(_approachM, StepM * 2f));
                return;
            }
            _approachM = 0f;
            API.LogInfo("[SF6Access] Wall: beside it now - the hand goes on");
        }
        BeginMeasure(px, py, pz);
    }

    /// <summary>One queued measurement.</summary>
    private static void Cast(int i)
    {
        var p = _queue[i];
        if (p.Side)
        {
            var dir = new FieldDirectionService.FlatDir(p.DX, p.DZ, true);
            bool ok = FieldRayCaster.TryCastAcross(p.X, p.Y, p.Z, dir, p.Dist, out var hit);
            _ran[i] = ok;
            // Nothing met within the reach is an OPEN way, and that has to be a different number
            // from "the sensor could not run": one is walkable, the other is no information.
            _val[i] = !ok ? float.NaN : hit.Hit ? hit.Distance : float.PositiveInfinity;
            return;
        }
        bool ran = FieldRayCaster.TryFloorBelow(p.X, p.Y, p.Z, p.Dist, out float floorY);
        _ran[i] = ran;
        _val[i] = floorY;   // NaN is a hole, which is exactly what the caller is looking for
    }

    // ---------------------------------------------------------------- the look-around

    /// <summary>Opening (and re-opening) search: is there anything at all within reach to put a hand
    /// on? A wall by ray, an edge by the ground running out. Whichever is nearest, in whichever
    /// direction, becomes the handhold — and the heading is then set so that it ends up on the
    /// RIGHT, which is the whole of what "右手扶墙" means.</summary>
    private static void BeginSeek(float px, float py, float pz)
    {
        _stage = Stage.Seek;
        _cursor = 0;
        _probeY = py;
        _queue.Clear(); _val.Clear(); _ran.Clear();

        for (int s = 0; s < SeekSectors; s++)
        {
            Sector(s, out float dx, out float dz);
            Reserve(Probe.Ray(px, py + HoverM, pz, dx, dz, SeekM));
            for (int k = 0; k < SeekFloors.Length; k++)
            {
                float d = SeekFloors[k];
                Reserve(Probe.Floor(px + dx * d, py + FloorAboveM, pz + dz * d, FloorDropM));
            }
        }
        API.LogInfo(FormattableString.Invariant(
            $"[SF6Access] Wall: looking for something to hold within {SeekM:F0} m - {_queue.Count} casts"));
    }

    private static void Sector(int s, out float dx, out float dz)
    {
        double a = 2.0 * Math.PI * s / SeekSectors;
        dx = (float)Math.Cos(a);
        dz = (float)Math.Sin(a);
    }

    private static void Reserve(Probe p)
    {
        _queue.Add(p);
        _val.Add(float.NaN);
        _ran.Add(false);
    }

    // ---------------------------------------------------------------- one step

    /// <summary>Measure the handhold on the right, and whether the way ahead is open, in each of the
    /// turns the walk may take — and lay the stair line along the heading, because a flight of
    /// steps under the way ahead is the one answer that outranks all of them.</summary>
    private static void BeginMeasure(float px, float py, float pz)
    {
        _stage = Stage.Measure;
        _cursor = 0;
        _probeY = py;
        _probeX = px;
        _probeZ = pz;
        _queue.Clear(); _val.Clear(); _ran.Clear();

        // The stair line: fresh every measurement, from the feet, along the heading the walk is
        // following. Cast in the pump, not here — sixteen floor probes in one frame is more than
        // this walk pays for anything.
        for (int i = 0; i < _stairOk.Length; i++) { _stairOk[i] = false; _stairFloor[i] = float.NaN; }
        _stairAt = 0;
        _stairWalkM = 0f;
        _stairFlight = default;

        float rx = -_fz, rz = _fx;                       // rightward = forward x up, confirmed in game
        Reserve(Probe.Ray(px, py + HoverM, pz, rx, rz, SideM));
        for (int i = 0; i < SideSamples.Length; i++)
        {
            float d = SideSamples[i];
            Reserve(Probe.Floor(px + rx * d, py + FloorAboveM, pz + rz * d, FloorDropM));
        }
        for (int k = 0; k < MaxTries; k++)
        {
            Turn(_fx, _fz, Turns(k), out float dx, out float dz);
            Reserve(Probe.Ray(px, py + HoverM, pz, dx, dz, ProbeM));
        }
    }

    /// <summary>The turns the walk may take, in the order it takes them. Holding something: straight
    /// on wins, then LEFT (the handhold has turned that way). Holding nothing: RIGHT wins, because
    /// that is where the handhold went.</summary>
    private static float Turns(int k)
    {
        if (_hold != HOLD_NONE) { float[] held = { 0f, -90f, 90f, 180f }; return held[k]; }
        float[] free = { 90f, 0f, -90f, 180f };
        return free[k];
    }

    /// <summary>Every measurement of one stage is in: act on it.</summary>
    private static void Settle(float px, float py, float pz)
    {
        switch (_stage)
        {
            case Stage.Seek: SettleSeek(px, py, pz); return;
            case Stage.Measure: SettleMeasure(px, py, pz); return;
            case Stage.Validate: SettleValidate(px, py, pz); return;
        }
    }

    private static void SettleSeek(float px, float py, float pz)
    {
        int kind = HOLD_NONE;
        float bestM = float.PositiveInfinity, bx = 0f, bz = 0f;
        int per = 1 + SeekFloors.Length;

        for (int s = 0; s < SeekSectors; s++)
        {
            Sector(s, out float dx, out float dz);
            int ray = s * per;
            if (_ran[ray] && _val[ray] < bestM) { bestM = _val[ray]; kind = HOLD_WALL; bx = dx; bz = dz; }
            for (int k = 0; k < SeekFloors.Length; k++)
            {
                int i = ray + 1 + k;
                if (!_ran[i]) continue;
                float f = _val[i];
                if (float.IsFinite(f) && _probeY - f <= CliffM) continue;     // still the same ground
                float m = SeekFloors[k];
                if (m < bestM) { bestM = m; kind = HOLD_CLIFF; bx = dx; bz = dz; }
                break;
            }
        }

        if (kind == HOLD_NONE || !float.IsFinite(bestM) || bestM > SeekM)
        {
            // Nothing within reach. Not silence and not a guess: say what is true and keep looking,
            // because the player is walking and the next wall is theirs to find.
            _stage = Stage.Failed;
            _seekAt = Environment.TickCount64 + SeekAgainMs;
            API.LogInfo("[SF6Access] Wall: no wall and no edge within reach");
            if (!_saidNorail)
            {
                _saidNorail = true;
                Speak(Text("norail",
                    "There is no wall and no edge within reach here. Walk a little and press F4 again"));
            }
            return;
        }

        _saidNorail = false;
        _hold = kind;
        _holdM = bestM;
        _lostSteps = 0;
        // Put what was found on the RIGHT: rightward is (-fz, fx), so the heading whose right is
        // (bx, bz) is (bz, -bx).
        _fx = bz; _fz = -bx;
        Log(px, py, pz, kind, bestM, -1f);

        float go = bestM - KeepM(kind);
        if (go <= ArriveM) { _approachM = 0f; BeginMeasure(px, py, pz); return; }
        // Beyond arm's reach: go and stand beside it first. Following something six metres away
        // before ever touching it is how a walk ends up describing a wall it is not walking.
        _approachM = go;
        _apprX = bx; _apprZ = bz;
        SetApproach(px, py, pz, MathF.Min(go, StepM * 2f));
    }

    /// <summary>One step straight towards what the look-around found. It is not a following step
    /// yet — the hand is not on it until the walk is standing beside it.</summary>
    private static void SetApproach(float px, float py, float pz, float len)
    {
        _tryTurn = 0f;
        _stepApproach = true;
        Lay(px, py, pz, _apprX, _apprZ, len, 0f);
    }

    private static void SettleMeasure(float px, float py, float pz)
    {
        // ---------------------------------------------------------------- stairs first
        //
        // The owner's report, 2026-10-05: "有那种窄梯子他是没有带我走上去的，注意，我说的是不用
        // 交互的梯子". Every question this walk used to ask was about a DROP — is the way ahead
        // open, is there floor out to the right or only air — and a flight of stairs presents a
        // RISER, so all four directions were refused at BlockM and the walk turned away from the
        // stair rather than up it. This is the one answer that outranks them: the ground under
        // the way ahead is going UP in steps, so the walk goes up with it.
        //
        // It is asked BEFORE the hand and before the directions, because it is a statement about
        // the ground the sentence is about rather than about what the hand is holding, and it is
        // allowed to stand with nothing on either side — the open stair the owner also asked for
        // ("有时候楼梯是一面悬崖一面高墙，他也要能认出来" is the OTHER shape, and this reading
        // does not care which).
        if (ReadStair(px, py, pz)) return;

        ReadHold();
        if (_hold == HOLD_NONE)
        {
            _lostSteps++;
            if (_lostSteps >= LostSteps)
            {
                API.LogInfo("[SF6Access] Wall: nothing under the hand for two steps - looking again");
                _lostSteps = 0;
                Speak(Text("gone", "There is nothing under my hand any more. Looking for it again"));
                BeginSeek(px, py, pz);
                return;
            }
        }
        else _lostSteps = 0;

        _tryCount = 0;
        for (int k = 0; k < MaxTries; k++)
            if (OpenAhead(k)) Order[_tryCount++] = Turns(k);

        if (_tryCount == 0)
        {
            API.LogInfo("[SF6Access] Wall: every turn from here is blocked");
            _on = false;
            Reset();
            Speak(Text("stuck", "Every direction from here is blocked. Wall following has ended"));
            return;
        }
        _tryIdx = 0;
        BeginValidate(px, py, pz);
    }

    /// <summary><b>Is the way ahead a flight of steps the player walks?</b> True once the
    /// step has been laid and is ready to be spoken. Up or down — the owner's correction of
    /// 2026-10-05 (<i>"导航不能只认得出上行的梯子或斜坡，也要能认出下行的斜坡或梯子"</i>) applies here
    /// as much as to the planner's sweep, because a walk that has no word for ground falling in
    /// steps walks past a down-flight exactly as it used to walk past an up-flight.
    ///
    /// <para><b>A descent must have measured sides, and neither may be air.</b> The caller's
    /// standing geometry already probes what is on each hand (<see cref="ReadHold"/> and the
    /// sideways probes at the step's own floor); for a down-flight those readings are not a
    /// courtesy but the whole permission, so they are taken before the step is offered
    /// (<see cref="DescentSafe"/>). Air on either side means a ledge, and a blind player is not
    /// led down one by ear.</para>
    ///
    /// <para>The reading itself is <see cref="StairStep"/>'s and is not duplicated here: what
    /// this owns is the line it is read from (cast from the feet along the heading, one sample a
    /// stride and a half ahead) and the fact that a stair step is walked STRAIGHT ON and never
    /// sideways. Sliding is what a wall walk does to stay a hand's width off a wall, and a slide
    /// on a stair is a step off its edge.</para>
    ///
    /// <para>False means the stair reading said no, and the ordinary wall-and-edge walk carries
    /// on exactly as it did — this is an addition, and the addition is the only thing it is
    /// allowed to be.</para></summary>
    private static bool ReadStair(float px, float py, float pz)
    {
        if (!StairStep.TryRead(i => _stairFloor[i], i => _stairOk[i], StairStep.Samples,
                               _probeY, StairStep.SampleStepM, StairStep.ReachM,
                               out float walkM, out var flight, sidesSafe: true))
            return false;
        if (flight.RiserCount < StairMinRisers) return false;   // one riser is a kerb, not a stair

        // A DESCENT is only walked when its sides were measured and neither is air. This is
        // asked AFTER the flight is read — the treads to probe are the flight's own — and before
        // the step is laid, so a refused descent costs nothing but the rays.
        if (flight.GoesDown && !DescentSafe(px, py, pz, flight))
        {
            API.LogInfo("[SF6Access] Wall: a down-flight ahead, refused - a side is air or was not measured");
            return false;
        }

        _stairFlight = flight;
        _stairWalkM = walkM;
        _hold = HOLD_STAIR;
        // A stair is held by its own rise, not by a wall's distance or a cliff's — but the hand
        // fields must still be HONEST, because Log and the sentence both read them. The nearest
        // riser's height is the thing under the hand here.
        _holdM = flight.RiserM;
        _lostSteps = 0;
        _stepApproach = false;
        _tryTurn = 0f;
        _tryCount = 1;
        Order[0] = 0f;                                          // straight on, always
        BeginStairValidate(px, py, pz);
        return true;
    }

    /// <summary><b>May the player be walked down the flight the line ahead is standing on?</b>
    /// Both hands are probed sideways from the flight's own treads and folded through the same
    /// classifier everything else here uses (<see cref="NarrowPath.ClassifySide"/>); the answer
    /// is <see cref="NarrowPath.SafeToDescend"/> — measured on both sides, and neither one air.
    ///
    /// <para>This is asked of a descent ONLY, and a caller that skipped it would be handing a
    /// blind player an edge. It is a handful of rays, once per stair read, at the same rate the
    /// rest of this walk already casts.</para></summary>
    private static bool DescentSafe(float px, float py, float pz, NarrowPath.Flight flight)
    {
        // Both hands at the first tread and the last: the two ends are where a ledge gives way,
        // and a flight that is walled at its foot and open at its head is exactly the case that
        // must be refused.
        foreach (int i in new[] { flight.FirstStep, flight.Top })
        {
            if (i < 0 || i >= StairStep.Samples) continue;
            if (!float.IsFinite(_stairFloor[i])) return false;
            if (!SideSolid(px, py, pz, i, true)) return false;
            if (!SideSolid(px, py, pz, i, false)) return false;
        }
        return true;
    }

    /// <summary>One hand's reading at tread <paramref name="i"/> of the line ahead: false when
    /// there is no floor out there (air), and false when the probe could not run at all —
    /// "not measured" is not evidence of ground, the rule every probe here is held to.</summary>
    private static bool SideSolid(float px, float py, float pz, int i, bool left)
    {
        float t = StairStep.SampleAt(i, StairStep.SampleStepM);
        float floorY = _stairFloor[i];
        float sx = left ? -_fz : _fz;
        float sz = left ? _fx : -_fx;
        var dir = new FieldDirectionService.FlatDir(sx, sz, true);
        if (!FieldRayCaster.TryCastAcross(px + _fx * t, floorY + StairLiftM * 0.56f, pz + _fz * t,
                                          dir, StairSideReachM, out var hit))
            return false;
        if (!FieldRayCaster.TryFloorBelow(px + _fx * t + sx * StairSideReachM, floorY + StairLiftM,
                                          pz + _fz * t + sz * StairSideReachM,
                                          StairLiftM + FloorDropM, out float far))
            return false;
        bool wall = hit.Hit && hit.Class == HitClass.Wall
                    && float.IsFinite(hit.Distance) && hit.Distance <= StairSideReachM;
        // A wall is solid. Anything else must be a floor level with this tread; nothing under it
        // is air, and air is a refusal.
        return wall || NarrowPath.ClassifySide(false, far, floorY, StairClimbM) == NarrowPath.Side.Floor;
    }

    /// <summary>Lay the stair step down and prove the straight line it describes, exactly as an
    /// ordinary step is proved (<see cref="Lay"/>) — the ground under it and the space along it.
    /// The one difference is the floor samples: on a stair the ground RISES by a riser at every
    /// tread, so a sample is judged against the sample before it rather than against the player's
    /// own floor (see <see cref="StairStep.Walkable"/>), which is the check that already passed
    /// before this stage was entered.</summary>
    private static void BeginStairValidate(float px, float py, float pz)
    {
        Lay(px, py, pz, _fx, _fz, MathF.Min(_stairWalkM, StepM), 0f);
    }

    private static void BeginValidate(float px, float py, float pz)
    {
        float turn = Order[_tryIdx];
        _tryTurn = turn;
        _stepApproach = false;
        Turn(_fx, _fz, turn, out float dx, out float dz);
        // Sliding towards or away from the handhold is only ever done going STRAIGHT ON. A turn has
        // not measured what is under its own right hand yet, so a turn is a plain step and the next
        // measurement settles it — a diagonal across unmeasured ground is exactly what a walk beside
        // a cliff must never cut.
        float slide = turn == 0f && float.IsFinite(_holdM)
            ? Clamp(_holdM - KeepM(_hold), -MaxSlideM, MaxSlideM) : 0f;
        Lay(px, py, pz, dx, dz, StepM, slide);
    }

    /// <summary>Lay one candidate step down and prove the straight line it describes — the ground
    /// UNDER it and the space ALONG it. Nothing is spoken before that line has been measured: the
    /// rule the whole of the field guidance is held to, and the one the owner's report of
    /// 「我面前明明是高墙还要我往前走」 was missing.</summary>
    private static void Lay(float px, float py, float pz, float dx, float dz, float len, float slide)
    {
        float rx = -dz, rz = dx;                         // right of the direction being walked
        _tryX = px + dx * len + rx * slide;
        _tryZ = pz + dz * len + rz * slide;
        _tryLenM = Flat(px, pz, _tryX, _tryZ);

        _stage = Stage.Validate;
        _cursor = 0;
        _queue.Clear(); _val.Clear(); _ran.Clear();
        if (_tryLenM < 0.5f) { SettleValidate(px, py, pz); return; }
        // On a stair the line ahead is HIGHER than the player's own floor by a step or more, so
        // the samples must start above it or every one of them begins inside the tread it is
        // meant to measure.
        float lift = _hold == HOLD_STAIR ? StairLiftM : FloorAboveM;
        for (int i = 1; i <= SafeSamples; i++)
        {
            float t = (float)i / SafeSamples;
            Reserve(Probe.Floor(px + (_tryX - px) * t, _probeY + lift,
                                pz + (_tryZ - pz) * t, lift + FloorDropM));
        }
        // The space ALONG the line that is about to be spoken, and along no other. The four
        // directions were measured before the step chose one, and the step may since have
        // SLID sideways towards the handhold — so the line that gets spoken is not always one
        // of the lines that was asked about. This ray asks about exactly that line: same origin,
        // same height, one more metre than the step is long, so what stopped it is named by the
        // distance rather than treated as absent.
        _aheadSlot = _queue.Count;
        Reserve(Probe.Ray(px, _probeY + HoverM, pz, (_tryX - px) / _tryLenM, (_tryZ - pz) / _tryLenM,
                          _tryLenM + NoseM));
    }

    private static void SettleValidate(float px, float py, float pz)
    {
        if (_tryLenM < 0.5f) { NextTry(px, py, pz); return; }

        float reach = 0f;
        for (int i = 0; i < SafeSamples; i++)
        {
            if (!_ran[i]) break;
            float f = _val[i];
            // No ground at all, or ground further away than a cliff: the floor ends here.
            //
            // ON A STAIR this test is the wrong shape and would refuse every step of the flight:
            // the ground RISES a riser a tread there, so a sample half a metre above the player's
            // own floor is a tread the player is walking up and not a cliff. A stair is judged
            // against the sample before it instead — the ground climbs — and only a fall, a hole
            // or a rise too tall to step ends it. That is <see cref="StairStep.Walkable"/>, the
            // same rule that was checked before this stage was entered.
            float prev = i == 0 ? _probeY : _val[i - 1];
            bool ok = _hold == HOLD_STAIR
                ? float.IsFinite(f) && float.IsFinite(prev) && MathF.Abs(f - prev) <= StairClimbM
                : float.IsFinite(f) && MathF.Abs(f - _probeY) <= CliffM;
            if (!ok) break;
            reach = _tryLenM * (i + 1) / SafeSamples;
        }
        float ground = MathF.Min(_tryLenM, reach);

        // How far along THIS line the body actually gets. Not measured is not open — the rule
        // every other search here is held to — so a ray that could not run is this candidate
        // having no answer, not this candidate being clear.
        //
        // On a stair the space ALONG the line is exactly what is NOT asked: the riser the step
        // climbs is a contact two metres in, and requiring the way ahead to be clear would refuse
        // the stair for the same reason the four directions were refused. The floor's own climb,
        // already measured above, is the whole safety argument here.
        float clear;
        if (_hold == HOLD_STAIR)
        {
            clear = float.PositiveInfinity;
        }
        else
        {
            if (!_ran[_aheadSlot]) { NextTry(px, py, pz); return; }
            clear = _val[_aheadSlot];
            clear = float.IsFinite(clear) ? MathF.Max(0f, clear - NoseM) : float.PositiveInfinity;
        }

        if (ground < MinReachM || clear < MinReachM) { NextTry(px, py, pz); return; }

        // Never longer than what was measured in either direction: the ground under the line, or
        // the space along it. Whichever ran out first is the one that is said.
        _stepLenM = MathF.Min(ground, clear);
        _stepBlocked = !float.IsInfinity(clear) && clear + 0.2f < ground;
        _stepEdge = !_stepBlocked && reach < _tryLenM - 0.2f;
        float u = _stepLenM / _tryLenM;
        _stepX = px + (_tryX - px) * u;
        _stepZ = pz + (_tryZ - pz) * u;

        if (_tryTurn != 0f) Turn(_fx, _fz, _tryTurn, out _fx, out _fz);
        _stepIndex++;
        _dirty = true;
        // Where this step was given from. Everything about whether the answer was walked is
        // measured from here, and nowhere else.
        _giveX = px; _giveZ = pz;
        _waitLogged = false;
        // Sound the new direction AT ONCE rather than after a gap: the beacon's own clock is what
        // spaces its repeats, and a player who has just been told "step three" wants to hear which
        // way step three is now.
        StepBeacon.Reset();
        _stage = Stage.Ready;
        Log(px, py, pz, _hold, _holdM, _stepLenM);
    }

    private static void NextTry(float px, float py, float pz)
    {
        _tryIdx++;
        if (_tryIdx < _tryCount) { BeginValidate(px, py, pz); return; }
        // A stair candidate that failed validation is not the end of the walk: the flight the
        // owner described stands with a wall on one side, and the ordinary wall-and-edge rules
        // are still perfectly good about that wall. Falling through to the ordinary candidates
        // is the addition-only way to be wrong here.
        if (_hold == HOLD_STAIR)
        {
            API.LogInfo("[SF6Access] Wall: the stair line did not prove - walking it as an ordinary step");
            ReadHold();
            _tryCount = 0;
            for (int k = 0; k < MaxTries; k++)
                if (OpenAhead(k)) Order[_tryCount++] = Turns(k);
            if (_tryCount > 0) { _tryIdx = 0; BeginValidate(px, py, pz); return; }
        }
        API.LogInfo("[SF6Access] Wall: no turn from here has ground under it");
        _on = false;
        Reset();
        Speak(Text("stuck", "Every direction from here is blocked. Wall following has ended"));
    }

    /// <summary>What the right hand is holding — a wall by ray, an edge by the ground running out,
    /// whichever is nearer. A reading that never ran is not an answer, so it is skipped rather than
    /// read as "nothing there".</summary>
    private static void ReadHold()
    {
        _hold = HOLD_NONE;
        _holdM = float.NaN;

        if (_ran[0] && float.IsFinite(_val[0]) && _val[0] <= SideM)
        {
            _hold = HOLD_WALL;
            _holdM = _val[0];
        }
        for (int i = 0; i < SideSamples.Length; i++)
        {
            int slot = 1 + i;
            if (!_ran[slot]) continue;
            float f = _val[slot];
            if (float.IsFinite(f) && _probeY - f <= CliffM) continue;     // still the same ground
            float d = SideSamples[i];
            if (_hold != HOLD_WALL || d < _holdM) { _hold = HOLD_CLIFF; _holdM = d; }
            break;
        }
    }

    /// <summary>Whether the turn at index <paramref name="k"/> has a way ahead. Blocked, or never
    /// measured, is not a way: "not measured" must never reach the walk as "open".</summary>
    private static bool OpenAhead(int k)
    {
        int slot = 1 + SideSamples.Length + k;
        return _ran[slot] && _val[slot] >= BlockM;
    }

    /// <summary>How far from what the hand holds the walk tries to keep: further from an edge than
    /// from a wall. The number is unused on a stair, whose step is straight on by construction, but
    /// it is answered rather than left to the wall's figure so that no path can slide a step across
    /// a tread by accident.</summary>
    private static float KeepM(int kind)
        => kind == HOLD_CLIFF ? CliffKeepM : kind == HOLD_STAIR ? 0f : WallKeepM;

    // ---------------------------------------------------------------- the sentence

    /// <summary>Say the step that is due. True when it spoke — the convention every speech site in
    /// the beacon follows.</summary>
    internal static bool TrySpeak(float px, float py, float pz, long now)
    {
        if (!_on) return false;
        if (now < _nextSpeak) return false;
        if (_stage != Stage.Ready) return false;
        if (!_dirty && now < _nextRepeat) return false;

        string line = Format(px, py, pz);
        if (line == null) return false;
        _dirty = false;
        _nextSpeak = now + SpeakHoldMs;
        _nextRepeat = now + RepeatMs;
        Speak(line);
        return true;
    }

    private static string Format(float px, float py, float pz)
    {
        var forward = FieldDirectionService.GetCameraForward();
        int hour = FieldDirectionService.ClockHour(forward, _stepX - px, _stepZ - pz);
        if (hour <= 0) hour = 12;
        // Rounded DOWN, always. A sentence rounded up promises half a metre of ground that was
        // never cast at, and the one place those invented centimetres land is the metre in front
        // of whatever stopped the measurement. Under-promising costs a step; over-promising is
        // how a wall gets met face first.
        int meters = Math.Max(1, (int)MathF.Floor(_stepLenM));

        string body;
        if (_stepApproach)
        {
            body = string.Format(Text("approach",
                "First get beside it: {1} meters toward {0} o'clock"), hour, meters);
        }
        else if (_hold == HOLD_STAIR)
        {
            // The one sentence with "up" (or "down") in it. It says the riser count as well as
            // the metres, because a blind player walking a flight has no other way to know the
            // flight is still going: "tread four" is the shape of the ground, and the metres are
            // what to do about it. Straight on, and said so — a stair is walked the way it points.
            // Which way it points is the flight's own reading (the owner's ask of 2026-10-05), so
            // a down-flight is never announced with the word "up".
            body = string.Format(_stairFlight.GoesDown
                    ? Text("step_stair_down",
                        "Step {0}: walk {2} meters toward {1} o'clock, down the steps")
                    : Text("step_stair",
                        "Step {0}: walk {2} meters toward {1} o'clock, up the steps"),
                _stepIndex, hour, meters);
        }
        else if (_hold == HOLD_CLIFF)
        {
            body = string.Format(Text("step_cliff",
                "Step {0}: walk {2} meters toward {1} o'clock. There is a drop on your right - keep away from the edge"),
                _stepIndex, hour, meters);
        }
        else if (_hold == HOLD_WALL)
        {
            body = string.Format(Text("step",
                "Step {0}: walk {2} meters toward {1} o'clock, keeping the wall on your right"),
                _stepIndex, hour, meters);
        }
        else
        {
            body = string.Format(Text("step_open",
                "Step {0}: walk {2} meters toward {1} o'clock. There is nothing under my hand right now"),
                _stepIndex, hour, meters);
        }

        // Whichever ran out first is what gets said. A wall across the line and no ground under
        // it are not the same thing, and only one of the two was ever true.
        string tail = "";
        if (_stepBlocked) tail = string.Format(Text("front",
            " Careful: something blocks that way about {0} meters along. Stop there and I will look again"), meters);
        else if (_stepEdge) tail = string.Format(Text("edge",
            " Careful: there is only {0} meters of ground that way, then it ends. Stop there and I will look again"), meters);
        // "There." only when the player actually walked the previous one.
        return (_arrived ? Text("here", "There.") : "") + body + tail;
    }

    private static void Speak(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        ScreenReaderService.Speak(text, interrupt: false);
        API.LogInfo("[SF6Access] Wall: " + text);
    }

    private static void Log(float px, float py, float pz, int kind, float holdM, float lenM)
    {
        string what = kind == HOLD_WALL ? "wall"
            : kind == HOLD_CLIFF ? "cliff edge"
            : kind == HOLD_STAIR ? "stairs"
            : "nothing";
        // A stair's "distance" is its riser height, so it is named as one rather than printed as
        // a wall a third of a metre away. The tester cannot see the screen: if the walk reads a
        // flight, the flight's own numbers are what prove it from the log alone. Two separate
        // Invariant calls, never two interpolated strings added together — adding them makes the
        // pair degrade to a plain string and the format is lost before it reaches the API.
        string hand;
        if (kind == HOLD_STAIR)
        {
            hand = FormattableString.Invariant(
                $"stairs {(_stairFlight.GoesDown ? "DOWN" : "up")} ahead: {_stairFlight.RiserCount} risers x {_stairFlight.RiserM:F2} m");
            hand += FormattableString.Invariant(
                $", {(_stairFlight.GoesDown ? "down" : "up")} {MathF.Abs(_stairFlight.TotalRiseM):F2} m over {_stairFlight.RunM:F1} m");
            hand += FormattableString.Invariant($", step {_stairWalkM:F1} m");
        }
        else hand = float.IsFinite(holdM)
            ? FormattableString.Invariant($"{what} {holdM:F1} m on the right")
            : what + " on the right";
        string len = lenM >= 0f ? FormattableString.Invariant($", step {lenM:F1} m") : "";
        API.LogInfo(FormattableString.Invariant(
            $"[SF6Access] Wall: at ({px:F1}, {py:F1}, {pz:F1}) heading ({_fx:F2}, {_fz:F2}), ") + hand + len +
            FormattableString.Invariant($", {_walkedM:F0} m walked"));
    }

    // ---------------------------------------------------------------- geometry

    /// <summary>The heading turned by <paramref name="degrees"/>. Positive is to the RIGHT — the
    /// handedness this mod confirmed in game and that every other fan here uses.</summary>
    private static void Turn(float fx, float fz, float degrees, out float dx, out float dz)
    {
        double rad = degrees * Math.PI / 180.0;
        float c = (float)Math.Cos(rad), s = (float)Math.Sin(rad);
        float rx = -fz, rz = fx;                       // rightward = forward x up, confirmed in game
        dx = fx * c + rx * s;
        dz = fz * c + rz * s;
        float n = MathF.Sqrt(dx * dx + dz * dz);
        if (n > 0f) { dx /= n; dz /= n; }
    }

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

    private static float Flat(float ax, float az, float bx, float bz)
    {
        float dx = bx - ax, dz = bz - az;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
