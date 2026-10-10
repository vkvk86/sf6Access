using System;
using System.Collections.Generic;
using System.Globalization;
using REFrameworkNET;
using SF6Access.Hooks.WorldTour;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>The walk itself, spoken one step at a time — and a step to TRY even when no route exists.</b>
///
/// <para><b>The gap this fills, in the owner's own words (2026-10-03 19:28):</b>
/// <i>"导航无法带我到达目标，于是只会给我目标所在地的声音，但我并不知道该怎么过去…
/// 让导航找不到路也要带我尝试不同的路，而不是让我自己找."</i> Everything the beacon had was a
/// BEARING — "目标，10点钟，40米" — plus a hint about why the walk stops. A bearing is the answer
/// to "which way", never to "how". RoundPlanner had already measured a whole route of hops and
/// the beacon summarised it in one sentence ("一共 26 米，跟着走"), which is a bearing with extra
/// words: it says how far, never which way to turn at each corner. And when RoundPlanner found
/// nothing at all, there was no sentence left except "look for an entrance".</para>
///
/// <para><b>What this adds.</b> Two things, both read-only and both pure addition — the bearing
/// and the homing cue keep speaking exactly as they did:</para>
/// <list type="number">
/// <item><b>A measured route is spoken as STEPS.</b> RoundPlanner's hops are announced one at a
/// time with a clock bearing and a distance, and the next one is spoken on arrival — "共 3 步。
/// 第一步：朝 2 点方向走 8 米" … "到了。第二步：朝 5 点方向走 12 米".</item>
/// <item><b>When there is NO route, a TRY step.</b> Twelve rays fan out round the line to the
/// objective; every one of them is measured for open ground and for a floor at the far end, and
/// the direction that lands NEAREST THE OBJECTIVE is offered as the next thing to walk: "试着朝
/// 4 点方向走 9 米，那里离目的地近 6 米，到了我再找一次". Arriving there starts the search again
/// from the new spot, so the player is walked through a sequence of real, measured tries instead
/// of being handed a bearing and a shrug. <b>This is the "尝试不同的路" the owner asked for.</b></item>
/// </list>
///
/// <para><b>Nothing here moves the avatar.</b> The owner chose, when asked, "不动角色，只做逐步
/// 口令语音引导". There is no input controller, no <c>StartNavi</c>, no <c>SetChaseWalk</c>: the
/// only engine calls are the same two read-only casts the radar and RoundPlanner already live on
/// (<see cref="FieldRayCaster.TryCastAcross"/>, <see cref="FieldRayCaster.TryFloorBelow"/>), and
/// no navmesh query at all — so this cannot collide with the single-query rule, cannot inherit the
/// navmesh's disagreement with itself, and cannot fight the player for their own avatar.</para>
///
/// <para><b>Only ever an addition.</b> The homing cue and the bearing are the owner's instrument
/// for the last metres ("没有雷达让我怎么找"); this rides alongside them, never instead of them.</para>
/// </summary>
internal static class WalkSteps
{
    internal enum Kind { None, Round, Landmark, Trial }

    /// <summary>How near the player must come to a step before the next one is handed over. Wider
    /// than RoundPlanner's own 1 m: a route followed by ear is not walked to the centimetre, and
    /// making someone hunt for a point they have already reached is the old "walked past the
    /// target" complaint in a new place.</summary>
    private const float ArriveM = 2.5f;

    /// <summary>Further than this from the step they were given, the player is not walking it —
    /// the search belongs to wherever they are now.</summary>
    private const float OffRouteM = 6f;

    /// <summary>A move smaller than this does not invalidate a search: the player shifting their
    /// feet is not a change of plan.</summary>
    private const float MoveM = 3f;

    /// <summary><b>「有一条能走的路，先走到{梯子/门/肯…}，到了那儿我接着带」的总开关 — 房主 2026-10-05 00:11 要求关掉。</b>
    /// 这是城市路由器的经由段引导：找不到路以后它反复带人去试一个具名落脚点。当晚日志里 31 句
    /// Walk: 全是它，且大半以「那个方向只有X米有地面，再往前就是落差」收场 —— 对房主来说这就是
    /// 「导航找不到路以后还带我去尝试」。关 = PumpLandmark 不再被调用，城市路由器完全不启动
    /// （TryHop 是它唯一入口），零查询、零播报、零步进信标。以下不受影响：量过的绕行
    /// （Kind.Round）、F12 试路（默认本来就关）、归航音、方位播报、30 秒一次的「暂时没有下一步」
    /// 安静看门狗。要恢复：改回 true。</summary>
    private const bool LandmarkStepsOn = false;

    /// <summary>Never two lines closer together than this, whatever else is happening.</summary>
    private const long SpeakHoldMs = 1500;

    /// <summary>How often an unchanged step is repeated. Long enough not to be a metronome, short
    /// enough that a player who lost the sentence finds it again without asking.</summary>
    private const long RepeatMs = 12000;

    /// <summary>How long the place router's leg may stand unchallenged before the try search is
    /// allowed to take the floor. Long enough that a leg being walked is never interrupted,
    /// short enough that a leg nobody is walking does not hold them in place.</summary>
    private const long StallMs = 15000;

    /// <summary>How long the walk may have nothing to say before it says THAT instead. Half a
    /// minute: long enough that a search about to answer is never interrupted, short enough that
    /// standing in a silent street never feels like the guide has stopped.</summary>
    private const long QuietMs = 30000;

    // How often "something else is guiding" may be written to the log.
    private const long WhyEveryMs = 5000;

    // --- the trial search -------------------------------------------------------------------
    /// <summary>The bearings tried, relative to the line to the objective. Positive is to the
    /// RIGHT of it — the handedness this mod confirmed in game and that RoundPlanner uses.
    /// The back half is not a mistake: an objective ringed by hoarding is often enterable only by
    /// first walking AWAY from it, and a fan that may never point backwards cannot find that.</summary>
    private static readonly float[] Fan = { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f, 150f, -150f, 180f };

    /// <summary>How far each trial ray is sent. About eight strides: far enough that one try is
    /// worth the sentence it costs, near enough that the player cannot lose the thread of it.</summary>
    private const float ProbeM = 16f;

    /// <summary>Clearance kept past the far end of a measured hop, and the shortest walk worth
    /// offering. A "try" of one metre is a shuffle.</summary>
    private const float ClearM = 0.5f;
    private const float MinWalkM = 3f;

    /// <summary>How much nearer the objective a try has to land before it counts as NEARER rather
    /// than as a way round. Below this the step is still worth walking — see
    /// <see cref="DetourCost"/> — but it has to be said as a way round and not as progress.</summary>
    private const float MinGainM = 2.5f;

    /// <summary>What a direction loses for not landing nearer, so that every direction that DOES
    /// land nearer beats it. It must not be a refusal — it used to be one, and that refusal is
    /// exactly why a detour could never be tried. The way into a fenced yard or round a hoarding
    /// starts by walking away from the objective, and under the old rule not one such bearing was
    /// ever measured, so the honest report was "nothing gets me nearer" about a route one leg
    /// long. Large enough that a nearer landing always wins, small enough that a wall with one
    /// way round it still gets a sentence.</summary>
    private const float DetourCost = 6f;

    /// <summary>Height of the trial rays, and of the floor probe's start above the floor. The same
    /// values RoundPlanner measured with: chest height clears a kerb and still hits a hoarding.</summary>
    private const float HoverM = 0.9f;
    private const float FloorAboveM = 0.8f;
    private const float FloorDropM = 3.2f;

    // Floor along a step is probed every SampleM, and StepTolM is how much higher or lower the
    // ground under the next probe may sit before it stops counting as the same walk.
    private const float SampleM = 2f;
    private const float StepTolM = 1.2f;

    /// <summary>Two casts a tick: the rate RoundPlanner's own search runs at, so the two together
    /// stay inside what the field sensor was already paying.</summary>
    private const int CastsPerPump = 2;

    /// <summary>How long a trial that answered nothing waits before it may run again.</summary>
    private const long RetryMs = 3000;

    /// <summary>Beyond this, "walk sixteen metres nearer" is not an answer to anything: what
    /// stands between is the city, and no local ray sees round the city.</summary>
    private const float TrialRangeM = 60f;

    /// <summary>Try-steps remembered so the search cannot offer the same spot twice and walk the
    /// player in a circle. Capped, because a memory that grows for ever is a leak.</summary>
    private const int MaxVisited = 24;
    private const float VisitedM = 2.5f;

    // --- not repeating the walk already walked --------------------------------------------
    /// <summary>How far the player has to go before another point is added to the track.</summary>
    private const float TrackM = 4f;

    /// <summary>How near the track a landing spot may be before it counts as ground already
    /// covered. Wider than a point's spacing, because "near where I walked" is a corridor, not
    /// a dot.</summary>
    private const float NearWalkedM = 5f;

    private const int MaxWalked = 48;

    // --- state ------------------------------------------------------------------------------
    private static string _key;
    private static Kind _kind;

    // the step being walked
    private static float _stepX, _stepY, _stepZ;
    private static int _index, _total;
    private static float _gainM;
    private static bool _final;
    private static bool _dirty;      // a step that has not been said yet
    private static bool _advanced;   // this step follows one the player actually walked
    private static string _stepKey;  // identity of the step already said, so a standstill is quiet
    private static string _last;

    /// <summary>What the current place-leg is walking to, named. A leg is only worth walking
    /// towards if it can be NAMED — "the door", "the fast travel board" — because that is what
    /// lets the owner recognise it on arrival instead of hoping the metres ran out.</summary>
    private static string _hopName = "";
    private static LandmarkRouter.Kind _hopKind = LandmarkRouter.Kind.Start;
    private static long _nextSpeak, _nextRepeat;
    private static bool _endPending;

    // RoundPlanner bookkeeping: its hop count is the plan's own odometer.
    private static int _roundHops, _roundTotal;

    // the trial search — off until F12 turns it on (see TrialEnabled)
    private static bool _trialOn;
    private static bool _scanning;
    private static int _dirIdx;
    private static float _scanX, _scanZ, _scanY;
    private static float _bestGain, _bestX, _bestY, _bestZ;
    private static float _bestScore;   // gain less the penalty for ground already used
    private static int _bestIdx;       // which bearing of the fan won
    // The winning bearing does not land nearer the objective: it is a way ROUND. It has to be
    // said differently, because "six metres nearer" about a step that is four metres further is
    // a sentence that stops being trusted the first time the player paces it out.
    private static bool _around;
    // Whether the place router's leg has already been refused by the reaches measurement, and
    // the bookkeeping for deciding it is getting nowhere: when the leg was said, and how far
    // away it was then. See YieldToTrial.
    private static bool _blocked;
    private static long _hopSince;
    private static float _hopDist0;
    // When the walk last had nothing to say. Kept so a long search can be told apart from a guide
    // that has stopped — to the owner walking it they sound identical (see QuietWatchdog).
    private static long _quietSince;
    private static bool _rush;         // this search was asked for a moment ago: cast it in one go
    private static bool _urgent;       // F12 has just been pressed and is waiting for its answer
    // The last try handed over, so the next search does not hand the same one back.
    private static int _lastPick = -1;
    private static float _lastPickX, _lastPickZ;
    private static long _retryAt;
    private static int _tries;
    private static readonly List<(float X, float Z)> _visited = new();

    /// <summary>Where the player has walked while the search was armed, so a new try can be a
    /// NEW one. Points a few metres apart, not a per-frame trace: the question is "have I been
    /// here", not "how did I move".</summary>
    private static readonly List<(float X, float Z)> _walked = new();

    /// <summary>What kind of step is on offer right now, for the log and for callers that must
    /// not talk over a route that already has an answer.</summary>
    internal static Kind Source => _kind;

    /// <summary><b>The trial search is OFF until the owner asks for it — F12 (2026-10-03 22:25).</b>
    /// "找路的这个改成我按 F12 就打开": the owner was walking the city while it ran, and a search
    /// that offers a new direction every time a try is finished is a voice that will not stop
    /// talking in a street full of things to listen to. So it answers the key instead of the
    /// silence. <b>Off means off</b>: no ray is cast, nothing is said, and the bearing and the
    /// homing cue — the owner's own instrument — are untouched either way. What stays on when
    /// this is off is the measured route spoken as steps, which is not a search but an answer
    /// already paid for.</summary>
    internal static bool TrialEnabled => _trialOn;

    /// <summary><b>F12: turn the trial search on or off.</b> Always says which way it went,
    /// because a key that sometimes answers nothing is how a blind player stops trusting keys.</summary>
    internal static void ToggleTrial() => SetTrial(!_trialOn);

    internal static void SetTrial(bool on)
    {
        if (_trialOn == on) return;
        _trialOn = on;
        if (!on)
        {
            // Leaving nothing half-said behind: the step it offered a moment ago belongs to the
            // search that is now switched off, and offering it again would be the search talking
            // after being told to be quiet.
            DropTrial();
            _endPending = false;
            _tries = 0;
            _retryAt = 0;
            _bestGain = 0f;
            _nextRepeat = 0;
            _visited.Clear();
        }
        else
        {
            // Forget where the last search gave up, so switching on asks again instead of
            // inheriting a "nothing works here" that was measured somewhere else.
            _retryAt = 0;
            _endPending = false;
            // "按 F12 后立刻开始引导": the key is not a permission to start looking sometime,
            // it is a request for the first line now. _rush casts the whole fan in one tick and
            // _urgent lets the answer out without waiting for Resolve's twice-a-second grid.
            _urgent = true;
            _rush = true;                                  // the next search answers in one tick
        }
        API.LogInfo("[SF6Access] Walk: trial search " + (on ? "ON (F12)" : "off (F12)"));
        ScreenReaderService.Speak(Text(on ? "on" : "off", on
            ? "Way-finding on. When no route reaches the objective I will measure a direction that lands nearer and offer it to you"
            : "Way-finding off"), interrupt: true);
    }

    /// <summary>True while F12 is still owed its first line. The press itself is answered with
    /// "way-finding on" inside a few milliseconds; this is the real guidance that follows.</summary>
    internal static bool PendingImmediate => _urgent;

    /// <summary><b>Say what F12 asked for, without waiting for the grid.</b> Called from the
    /// beacon's per-frame tick rather than its twice-a-second resolve, because a key pressed in
    /// the street and answered half a second later feels like it was not answered at all.
    /// Returns true when it spoke, so the caller can let it own that moment.</summary>
    internal static bool SpeakNow(in BeaconDestination dest, float px, float py, float pz, long now)
    {
        if (!_urgent) return false;
        if (_scanning) return false;                       // half a frame from an answer
        if (_kind == Kind.None && !_endPending) return false;
        // A key press is not subject to the 1.5 s hold between sentences.
        _nextSpeak = 0;
        if (!TrySpeak(dest, px, py, pz, now)) return false;
        _urgent = false;
        return true;
    }

    /// <summary>True while a ray search for a try-step is in progress. Nothing to say yet.</summary>
    internal static bool Scanning => _scanning;

    /// <summary>Forget everything: another objective, another field, or the same one after a
    /// reload. Nothing measured here survives leaving the place it was measured in.</summary>
    internal static void Reset()
    {
        _key = null;
        _kind = Kind.None;
        _hopName = "";
        _hopKind = LandmarkRouter.Kind.Start;
        _index = _total = 0;
        _gainM = 0f;
        _final = _advanced = _dirty = false;
        _last = null;
        _stepKey = null;
        _nextSpeak = _nextRepeat = 0;
        _endPending = false;
        _roundHops = _roundTotal = 0;
        _scanning = false;
        _dirIdx = 0;
        _bestGain = 0f;
        // Nothing at all, rather than zero: a way ROUND scores negative by design (see
        // DetourCost), and a floor of zero would refuse every one of them — which is the bug
        // this change exists to remove. Only "no such direction exists" may end the search.
        _bestScore = float.MinValue;
        _bestIdx = -1;
        _around = false;
        _blocked = false;
        _hopSince = 0;
        _hopDist0 = 0f;
        _quietSince = 0;
        _rush = false;
        _urgent = false;
        _lastPick = -1;
        _lastPickX = _lastPickZ = 0f;
        _retryAt = 0;
        _tries = 0;
        _visited.Clear();
        _walked.Clear();
        LandmarkRouter.Reset();
        StepBeacon.Reset();
        // _trialOn is NOT cleared: whether the owner has way-finding on is their decision, not
        // a fact about the place, and it must survive changing objective or field.
    }

    /// <summary><b>Spend this tick on the search and on keeping the current step honest.</b>
    /// Called every frame, not only when the beacon resolves: the twelve casts a trial needs have
    /// to be affordable inside about half a second, because a search whose root has been left
    /// behind describes somewhere the player no longer is.</summary>
    internal static void Pump(in BeaconDestination dest, float px, float py, float pz)
    {
        PumpCore(dest, px, py, pz);
        // The sounding half. A step that is only spoken is a sentence the player has to
        // remember while walking; the same step sounded is something they can walk toward
        // without holding it in their head — which is exactly what the owner asked for
        // ("只有语音引导不够，我需要信标引导"). The ray-led try rides F12; a route through known
        // places is not a search the owner asked to be able to silence — it is the answer, and
        // switching it off would be taking away the one thing that reaches the objective.
        //
        // F4 (wall following) owns the beacon while it is on, and this whole block steps aside
        // rather than only the Tick: two callers resetting each other's clock would leave the
        // beacon either silent or chirping off-beat, and a beacon pointing at one step while
        // the words describe another is worse than either alone. The walk along the wall says
        // and sounds THE SAME step, which is the only pairing that can be trusted by ear. The
        // mission homing cue is untouched by any of this — it never is.
        if (WallFollow.On) return;
        if (_kind != Kind.None && (_trialOn || _kind == Kind.Landmark))
            StepBeacon.Tick(px, pz, _stepX, _stepZ);
        else StepBeacon.Reset();
    }

    private static void PumpCore(in BeaconDestination dest, float px, float py, float pz)
    {
        if (!dest.Ok || dest.Searching) { return; }
        if (dest.Key != _key)
        {
            _key = dest.Key;
            Reset();
            _key = dest.Key;
        }
        // Where the player has actually been. Kept only while the search is armed, and used to
        // keep a NEW try from being the ground already walked (see Penalty).
        if (_trialOn) TrackWalk(px, pz);

        // A measured route outranks everything: it has been proved to reach the objective.
        if (PumpRound(dest, px, py, pz)) return;
        if (!Stuck(dest))
        {
            // To the player it is the same silence whether we said nothing or were never given
            // the floor. Only the log can tell those apart, and they need opposite fixes, so
            // record which it was.
            WhyNot(dest);
            DropTrial(); LandmarkRouter.Reset(); DropLandmark(); return;
        }

        // Then a route through places the game itself names. It outranks a try because every hop
        // in it has been VERIFIED reachable by the engine's own pathfinder, while a try only
        // promises to get nearer. It is not behind F12: F12 switches off a SEARCH that would talk
        // while walking down an ordinary street, and this is the answer instead.
        //
        // BUT ONLY WHILE THE LEG IT NAMES CAN ACTUALLY BE WALKED. It used to keep the floor
        // unconditionally, and that quietly took the owner's own F12 away: the router offered a
        // leg, the leg turned out to run out over a ledge, the try-step search never got to run
        // even once, and they stood where they were with way-finding on and nothing being tried.
        // The log of 2026-10-04 says exactly that — 13:00:07 "trial search ON (F12)", and after
        // it not one "trying directions from" line to the end of the session.
        if (LandmarkStepsOn && PumpLandmark(dest, px, py, pz) && !YieldToTrial(px, pz)) return;
        DropLandmark();
        PumpTrial(dest, px, py, pz);
    }

    /// <summary><b>Should the place router hand the floor to the try search?</b>
    ///
    /// <para>Two ways to know it should. The first is certain: the reaches measurement has already
    /// refused this leg — the ground runs out part of the way to it — so offering it again is
    /// repeating a warning, not guidance. The second is a suspicion: the leg has been offered for
    /// a while and the owner is no nearer to it than when it was said.</para>
    ///
    /// <para><b>Only ever with F12 up.</b> With way-finding off, the place router's leg is the
    /// only verbal guidance there is here, and standing it down in favour of a search that has
    /// been switched off is silence — the one failure this mod never allows. Nothing about the
    /// owner's own instrument changes when it is off.</para>
    /// </summary>
    private static bool YieldToTrial(float px, float pz)
    {
        if (!_trialOn) return false;
        if (_blocked) return true;
        long now = Environment.TickCount64;
        if (now - _hopSince < StallMs) return false;
        return Flat(px, pz, _stepX, _stepZ) > _hopDist0 - MoveM;
    }

    private static long _whyAt;

    /// <summary>There is no route, yet nothing of ours spoke: say which other caller still owns
    /// the guiding. Written because a router that never got its turn looks exactly like a router
    /// that failed, and the two want opposite repairs.</summary>
    private static void WhyNot(in BeaconDestination dest)
    {
        if (!dest.NoRoute) return;          // there is a route: nothing of ours was being held back
        long now = Environment.TickCount64;
        if (now - _whyAt < WhyEveryMs) return;
        _whyAt = now;

        var why = new List<string>();
        if (dest.Searching) why.Add("searching");
        if (dest.RelaySearching) why.Add("relay searching");
        if (dest.RelayHop) why.Add("relay hop");
        if (dest.Ladder) why.Add("ladder");
        if (dest.Stair) why.Add("stair");
        if (dest.Jump) why.Add("jump");
        if (dest.Drop) why.Add("drop");
        if (dest.Holding) why.Add("holding");

        API.LogInfo("[SF6Access] Walk: no route, but the guidance belongs to " +
                    (why.Count == 0 ? "the beacon alone" : string.Join(" and ", why)) +
                    "; the place router stands down");
    }

    /// <summary><b>Only where the walk has actually been declared over — and only by somebody
    /// who is actually talking.</b> A try-step offered alongside a route that already works is not
    /// help, it is a second captain: the player would hear "walk 9 metres that way" from this and
    /// "turn left in 12" from the beacon about the same street. Everything that has an ANSWER of
    /// its own — a ladder leg, a relay hop, a drop, a run that has produced a route — keeps the
    /// floor.</summary>
    ///
    /// <para><b>A search is not an answer, and it used to be treated as one.</b>
    /// <c>RelaySearching</c> stood in this list until 2026-10-04, and that single term is why the
    /// owner's report reads <i>"只响了几声就没了，只剩下归航音"</i>. The relay search costs one
    /// rate-limited query per candidate — it can run for tens of seconds — and while it runs it
    /// says NOTHING: it only becomes speech once it has found a hop, at which point
    /// <c>RelayHop</c> takes over and this stands down anyway. So for the whole of that search the
    /// beacon was pointing somewhere, the walk was silent, and the homing cue was all that was
    /// left. The log of that session says it outright:
    /// <c>Walk: no route, but the guidance belongs to relay searching; the place router stands down</c>
    /// — filed at 13:01:12, thirty-five seconds after the last sentence anyone heard.</para>
    ///
    /// <para><c>Searching</c> stays in the list on purpose: that is the DIRECT query, it is one
    /// query long, and if it succeeds the beacon has a full route — worth its half second of
    /// quiet. A search that has already been proved to have no route to find is not.</para>
    /// </summary>
    private static bool Stuck(in BeaconDestination dest)
        => dest.NoRoute && !dest.Searching && !dest.RelayHop
           && !dest.Ladder && !dest.Jump && !dest.Stair && !dest.Drop && !dest.Holding;

    private static void DropTrial()
    {
        if (_kind != Kind.Trial) return;
        _kind = Kind.None;
        _last = null;
        _dirty = false;
        _around = false;
        _scanning = false;
        _dirIdx = 0;
    }

    /// <summary><b>RoundPlanner's hops, one at a time.</b> Its own <c>Path</c> is the plan — hop
    /// zero is what the player walks now — so this reads it rather than keeping a second copy that
    /// could disagree with it, and its <c>Arrived</c>/<c>Abandoned</c> rules stay the only ones.</summary>
    private static bool PumpRound(in BeaconDestination dest, float px, float py, float pz)
    {
        int hops = RoundPlanner.HasHop ? RoundPlanner.Hops : 0;
        if (hops <= 0)
        {
            if (_kind == Kind.Round) { _kind = Kind.None; _last = null; _dirty = false; _stepKey = null; }
            _roundHops = _roundTotal = 0;
            return false;
        }
        // A fresh plan is not only one with MORE hops than before: a re-search can return a shorter
        // route, and counting that as "the player walked a hop" would greet a brand-new plan with
        // "到了" about an arrival that never happened. What really distinguishes them is the jump
        // the step makes — one hop is a couple of metres, a different plan starts somewhere else.
        float jump = _kind == Kind.Round ? Flat(_stepX, _stepZ, RoundPlanner.X, RoundPlanner.Z) : float.MaxValue;
        if (hops > _roundHops || jump > MoveM * 2f) { _roundTotal = hops; _advanced = false; }
        else if (hops < _roundHops) _advanced = true;                      // one hop walked
        _roundHops = hops;

        _kind = Kind.Round;
        _stepX = RoundPlanner.X; _stepY = RoundPlanner.Y; _stepZ = RoundPlanner.Z;
        _index = _roundTotal - hops + 1;
        _total = _roundTotal;
        _final = RoundPlanner.ReachesGoal && hops == 1;
        // A try-step search running alongside is now describing somewhere the route has passed:
        // drop it rather than let two plans argue.
        _scanning = false;
        _dirIdx = 0;
        // Dirty ONLY when the step itself is a new one. Marking it dirty every pump would make
        // TrySpeak fire on its 1.5 s hold for as long as the player stood still — a route repeated
        // every second and a half is not guidance, it is a siren (caught before it shipped,
        // 2026-10-03).
        string key = StepKey(_stepX, _stepZ, _index);
        if (key != _stepKey) { _stepKey = key; _dirty = true; }
        return true;
    }

    /// <summary><b>A way through places the game itself names.</b> The city router answers with
    /// the next place the owner can put their hand on — a door, a ladder foot, a fast travel
    /// board — every hop of which the engine's own pathfinder has already confirmed they can walk.
    /// That makes it stronger than a try in the only way that matters: it reaches the objective
    /// rather than merely getting closer to it.</summary>
    private static bool PumpLandmark(in BeaconDestination dest, float px, float py, float pz)
    {
        if (!LandmarkRouter.TryHop(px, py, pz, dest.TargetX, dest.TargetY, dest.TargetZ,
                                   out var hop))
        {
            DropLandmark();
            return false;
        }
        // Identity is the place, not the plan: the router's idea of the best hop improves while it
        // searches, and re-saying a leg because the plan behind it got better is noise. Only a
        // genuinely different place earns a sentence.
        string key = StepKey(hop.X, hop.Z, 0) + "#" + hop.Name;
        // A leg that FOLLOWS one already walked is an arrival; the first leg is not, however many
        // times the router hands it over while it thinks.
        bool following = _kind == Kind.Landmark && _stepKey != key;

        _kind = Kind.Landmark;
        _stepX = hop.X; _stepY = hop.Y; _stepZ = hop.Z;
        _hopName = hop.Name; _hopKind = hop.What;
        _final = hop.What == LandmarkRouter.Kind.Objective;
        _advanced = following;
        if (_stepKey != key)
        {
            _stepKey = key;
            _dirty = following || !LandmarkRouter.Found;
            // A new leg starts with a clean slate: whatever was wrong with the last one says
            // nothing about this one.
            _hopSince = Environment.TickCount64;
            _hopDist0 = Flat(px, pz, hop.X, hop.Z);
            _blocked = false;
        }
        return true;
    }

    private static void DropLandmark()
    {
        if (_kind != Kind.Landmark) return;
        _kind = Kind.None;
        _last = null;
        _dirty = false;
        _blocked = false;
        _stepKey = null;
        _hopName = "";
    }

    private static string StepKey(float x, float z, int index)
        => FormattableString.Invariant($"{x:F1},{z:F1}#{index}");

    /// <summary><b>When nothing reaches the objective: measure a direction that gets NEARER and
    /// offer it as the next thing to walk.</b> Ray-only, so no navmesh query can be spent here and
    /// nothing the engine says can contradict it.</summary>
    private static void PumpTrial(in BeaconDestination dest, float px, float py, float pz)
    {
        // Switched off until F12: no ray, no sentence, no half-finished search left standing.
        if (!_trialOn) { DropTrial(); return; }
        long now = Environment.TickCount64;

        if (_scanning)
        {
            // Normally two casts a tick, so the search never costs a frame. But a search the
            // player has just pressed a key for has to answer NOW, not six frames from now —
            // twelve rays is a handful of microseconds, and the rate limit exists for the
            // navmesh, which this does not touch.
            int budget = _rush ? Fan.Length : CastsPerPump;
            while (budget > 0 && _dirIdx < Fan.Length)
            {
                budget--;
                TryDirection(_dirIdx++, _scanX, _scanY, _scanZ, dest.TargetX, dest.TargetZ);
            }
            if (_dirIdx >= Fan.Length) { _scanning = false; _rush = false; FinishTrial(now); }
            return;
        }

        if (_kind == Kind.Trial)
        {
            float d = Flat(px, pz, _stepX, _stepZ);
            if (d <= ArriveM)
            {
                // Walked it. Remember the spot so the next search cannot send them straight back,
                // and search again from here — that is the "再试一条" that makes this a sequence of
                // tries rather than one suggestion and silence.
                Remember(_stepX, _stepZ);
                StartTrial(px, py, pz, now);
                return;
            }
            if (d > OffRouteM && Flat(px, pz, _scanX, _scanZ) > MoveM)
            {
                // Wandered off the try they were given: it belongs to wherever they are now.
                _kind = Kind.None;
                _last = null;
                StartTrial(px, py, pz, now);
            }
            return;
        }

        if (!WorthTrying(dest, px, py, pz)) return;
        if (now < _retryAt) return;
        if (Flat(px, pz, _scanX, _scanZ) <= MoveM && _retryAt != 0) return;
        StartTrial(px, py, pz, now);
    }

    /// <summary>A try is worth measuring only where a lateral walk could BE the answer: the
    /// objective on this floor, not already underfoot, and near enough that sixteen metres of
    /// walking is a step towards it rather than a step into the city.</summary>
    private static bool WorthTrying(in BeaconDestination dest, float px, float py, float pz)
    {
        if (MathF.Abs(py - dest.TargetY) >= LevelRoute.LevelM) return false;
        float toGoal = Flat(px, pz, dest.TargetX, dest.TargetZ);
        return toGoal > MissionBeaconHooks.ARRIVED_M && toGoal <= TrialRangeM;
    }

    private static void StartTrial(float px, float py, float pz, long now)
    {
        _scanning = true;
        _dirIdx = 0;
        _scanX = px; _scanY = py; _scanZ = pz;
        _bestGain = 0f;
        _bestScore = float.MinValue;
        _bestIdx = -1;
        _around = false;
        _kind = Kind.None;
        _last = null;
        _dirty = false;
        _stepKey = null;
        _nextRepeat = 0;
        API.LogInfo(FormattableString.Invariant(
            $"[SF6Access] Walk: trying directions from ({px:F1}, {py:F1}, {pz:F1}), try {_tries + 1}"));
    }

    /// <summary>One bearing of the fan: ray it, prove there is a floor at the far end, and keep it
    /// only if landing there is genuinely nearer the objective than standing here.</summary>
    private static void TryDirection(int i, float px, float py, float pz, float tx, float tz)
    {
        FanToGoal(px, pz, tx, tz, Fan[i], out float dx, out float dz);
        var dir = new FieldDirectionService.FlatDir(dx, dz, true);
        if (!dir.Ok) return;
        if (!FieldRayCaster.TryCastAcross(px, py + HoverM, pz, dir, ProbeM, out var hit)) return;

        float len = RoundStep.LengthM(hit.Hit, hit.Distance, ProbeM, ProbeM, ClearM, MinWalkM);
        if (!(len > 0f)) return;

        float cx = px + dir.X * len, cz = pz + dir.Z * len;
        if (!FieldRayCaster.TryFloorBelow(cx, py + FloorAboveM, cz, FloorDropM, out float floorY)) return;
        // No ground at all is a hole, and another storey is not this walk.
        if (!float.IsFinite(floorY) || MathF.Abs(floorY - py) > RoundPlanner.SameFloorM) return;

        float gain = Flat(px, pz, tx, tz) - Flat(cx, cz, tx, tz);

        // "尝试新路线不重复旧路线" (2026-10-03). A try that lands on ground already walked is
        // not a new route, it is the last one again — and a search that keeps proposing the same
        // few metres is what makes the player give up and go back to hunting by bearing.
        //
        // It is a PENALTY and not a ban on purpose: a hard ban can leave every bearing
        // rejected and the walk silent, and silence is the one failure this mod never allows.
        // Ground already covered loses its attraction but can still win if it is the only way
        // on; anything new wins by default.
        float score = gain - Penalty(cx, cz, i);
        // A direction that does not land nearer is a way ROUND, not progress, and it is charged
        // for it so that any direction that lands nearer beats it. It is NOT refused.
        if (gain < MinGainM) score -= DetourCost;
        if (score <= _bestScore) return;

        _bestScore = score;
        _bestGain = gain;
        _bestIdx = i;
        _bestX = cx; _bestY = floorY; _bestZ = cz;
    }

    /// <summary>How much less attractive a landing spot is for having been used already.</summary>
    private static float Penalty(float x, float z, int dirIdx)
    {
        float p = 0f;
        if (AlreadyVisited(x, z)) p += 8f;                                  // a spot already tried
        if (NearWalked(x, z)) p += 5f;                                      // ground already walked
        // The same bearing out of the same spot, once more: the fan is measured from wherever
        // the player stands, so after an arrival this is what would otherwise repeat the leg
        // they just walked, backwards.
        if (dirIdx == _lastPick && Flat(x, z, _lastPickX, _lastPickZ) < 6f) p += 4f;
        return p;
    }

    private static void FinishTrial(long now)
    {
        // Any bearing that can be WALKED is an answer now, nearer or not. It used to take only
        // bearings that landed nearer, and that is why a detour was never tried: for an objective
        // behind a fence the only way in begins by walking away from it, so every bearing was
        // refused and the search reported failure with a route one leg away.
        if (_bestIdx >= 0)
        {
            _kind = Kind.Trial;
            _stepX = _bestX; _stepY = _bestY; _stepZ = _bestZ;
            _gainM = _bestGain;
            _around = _bestGain < MinGainM;
            // Remembered so the NEXT search does not offer this leg again.
            _lastPick = _bestIdx;
            _lastPickX = _bestX; _lastPickZ = _bestZ;
            _index = ++_tries;
            _total = 0;
            _final = false;
            _dirty = true;
            _stepKey = StepKey(_stepX, _stepZ, _index);
            // Which sort of step it is has to be readable: "going round" is the whole fix, and
            // the log is where a blind owner reads it.
            // One interpolated string at a time: two of them joined by a ternary degrade to an
            // ordinary string and FormattableString.Invariant will not take it.
            string how = _around
                ? FormattableString.Invariant($"a way ROUND, {-_gainM:F1} m further from the objective than here")
                : FormattableString.Invariant($"{_gainM:F1} m nearer than here");
            API.LogInfo(FormattableString.Invariant(
                $"[SF6Access] Walk: try {_tries} - ({_stepX:F1}, {_stepY:F1}, {_stepZ:F1}), ") + how);
            return;
        }
        // Every bearing answered and not one of them could be walked at all. Say so ONCE — a
        // negative sentence repeated every twelve seconds is a lecture — and only start again
        // once the player has really moved.
        if (!_endPending)
        {
            _endPending = true;
            API.LogInfo("[SF6Access] Walk: no direction from here can be walked at all");
        }
        _kind = Kind.None;
        _last = null;
        _retryAt = now + RetryMs;
    }

    /// <summary><b>Say the step that is due, or null-to-nothing.</b> Spoken at once when the step
    /// is new or has just been reached, and again on a slow cadence for as long as it stays the
    /// step. Distances are measured at the moment of speaking, so a repeat is never stale.</summary>
    internal static bool TrySpeak(in BeaconDestination dest, float px, float py, float pz, long now)
    {
        if (now < _nextSpeak) return false;

        if (_endPending)
        {
            _endPending = false;
            _kind = Kind.None;
            _last = null;
            _nextSpeak = now + SpeakHoldMs;
            // Not "none of them lands nearer" any more: a way ROUND counts now, so reaching this
            // sentence means no bearing could be walked AT ALL, which is a different thing and
            // has to be said as one.
            Speak(Text("trial_end",
                "I have tried every direction I can measure from here and not one of them can be walked. Walk somewhere else and I will look again"));
            return true;
        }

        if (_kind == Kind.None)
        {
            // Nothing to say is not the same as nothing happening, and to the owner they sound
            // the same. Say that the search is still going rather than let the street go quiet.
            QuietWatchdog(dest, now);
            return false;
        }
        if (!_dirty && now < _nextRepeat) return false;

        string line = Format(px, py, pz);
        if (line == null) return false;
        _dirty = false;
        _last = line;
        _nextSpeak = now + SpeakHoldMs;
        _nextRepeat = now + RepeatMs;
        Speak(line);
        return true;
    }

    /// <summary><b>The last line of defence against a silent street.</b> Everything else here
    /// reports a STEP; this reports the ABSENCE of one, so that a long search is never mistaken
    /// for a guide that has given up. It only ever speaks where there is genuinely no route —
    /// with a route, the beacon is talking and this would be noise over it.</summary>
    private static void QuietWatchdog(in BeaconDestination dest, long now)
    {
        // A route exists, or something else is mid-sentence: not silence, so not our business.
        if (!dest.NoRoute || dest.Searching || dest.RelayHop || dest.Ladder || dest.Jump || dest.Stair || dest.Drop)
        { _quietSince = now; return; }

        if (_quietSince == 0) { _quietSince = now; return; }
        if (now - _quietSince < QuietMs) return;

        _quietSince = now;                       // another spell of quiet before it says it again
        _nextSpeak = now + SpeakHoldMs;
        API.LogInfo(FormattableString.Invariant(
            $"[SF6Access] Walk: nothing to say for {QuietMs / 1000} s while the other searches run - saying so instead of going quiet"));
        Speak(Text("quiet",
            "I have no step for you yet. Keep following the beacon sound and I will speak the moment I have one"));
    }

    /// <summary>How many of the metres towards a step have ground under them. Written because a
    /// guided step sent the owner to the lip of a drop: reachability had been proved along a
    /// POLYLINE, the sentence described a STRAIGHT LINE, and the gap between those two is exactly
    /// a ledge. Nothing here changes the route; it shortens the sentence to the last metre that
    /// can actually be walked, and says what is ahead instead.</summary>
    private static float SafeReach(float px, float py, float pz, float tx, float tz, float want)
    {
        float span = Flat(px, pz, tx, tz);
        if (!float.IsFinite(span) || span < 0.5f) return want;
        if (want > span) want = span;

        float ux = (tx - px) / span, uz = (tz - pz) / span;
        float last = 0f;
        for (float d = SampleM; d <= want; d += SampleM)
        {
            float sx = px + ux * d, sz = pz + uz * d;
            if (!FieldRayCaster.TryFloorBelow(sx, py + FloorAboveM, sz, FloorDropM, out float floorY)) break;
            // No floor at all is a hole; a floor this much higher or lower is a kerb, a step, or
            // another storey, and none of those are "keep walking" either.
            if (!float.IsFinite(floorY) || MathF.Abs(floorY - py) > StepTolM) break;
            last = d;
        }
        return last;
    }

    /// <summary>The sentence for the step being walked: a clock bearing the player can face and a
    /// distance they can count, and for a try, the metres it buys them.</summary>
    private static string Format(float px, float py, float pz)
    {
        var forward = FieldDirectionService.GetCameraForward();
        int hour = FieldDirectionService.ClockHour(forward, _stepX - px, _stepZ - pz);
        if (hour <= 0) hour = 12;
        float span = Flat(px, pz, _stepX, _stepZ);
        float reach = SafeReach(px, py, pz, _stepX, _stepZ, span);
        // Nothing walkable straight ahead at all. This line cannot be walked, so it cannot be
        // said — and silence is not what returning nothing means: the beacon keeps speaking, and
        // whichever search is running will be asked again from where the player actually stands.
        if (reach < SampleM) return null;

        // The ground runs out before the step does. Shorten the sentence to the last safe metre
        // rather than let it walk the owner over the lip of their own ledge.
        bool edge = reach < span - 1f;
        int meters = Math.Max(1, (int)MathF.Round(edge ? reach : span));
        string tail = edge ? " " + string.Format(
            Text("edge", "Careful: there is only {0} metres of ground that way, then it drops. Stop there and I will look again"), meters) : "";
        // Strike it off now, so the next search has something else to offer instead of this same
        // ledge repeated every twelve seconds. And give up the floor: a leg that cannot even be
        // walked to is not guidance, and holding on to it is what kept the try search from ever
        // running when F12 was switched on (see YieldToTrial).
        if (edge && _kind == Kind.Landmark) { LandmarkRouter.Refuse(_stepX, _stepZ); _blocked = true; }

        // A leg towards a named place leads with the NAME. Distance alone leaves the owner walking
        // into a doorway they cannot see and counting metres until they doubt the count; "the north
        // gate, eight metres" can be recognised on arrival, which is the difference between being
        // told a direction and being shown a door.
        if (_kind == Kind.Landmark)
        {
            string where = string.IsNullOrEmpty(_hopName) ? Anonymous(_hopKind) : _hopName;
            string line = _final
                ? string.Format(Text("via_end",
                    "Last leg: {2} meters toward {1} o'clock, and that is the objective"), where, hour, meters)
                : string.Format(Text("via",
                    "There is a walked way through. Go to {0} first: {2} meters toward {1} o'clock, and I will take it from there"),
                    where, hour, meters);
            return (_advanced ? Text("here", "There.") : "") + line + tail;
        }

        string body;
        if (_kind == Kind.Trial)
        {
            if (_around)
            {
                // Said as a way round, plainly and without a distance gain. Reporting "six metres
                // nearer" about a step that is four metres further is the kind of sentence that
                // costs a blind player their trust in every other sentence this mod speaks, and
                // the step is still the right one to walk.
                body = _index <= 1
                    ? string.Format(Text("trial_around",
                        "No route reaches it, so it has to be gone round. Walk {1} meters toward {0} o'clock. That will not get you nearer yet - it gets you past whatever is in the way, and I will look again when you are there"),
                        hour, meters)
                    : string.Format(Text("trial_around_next",
                        "Still going round: {1} meters toward {0} o'clock"), hour, meters);
            }
            else
            {
                int gain = Math.Max(1, (int)MathF.Round(_gainM));
                body = _index <= 1
                    ? string.Format(Text("trial",
                        "No route reaches it. Try walking {1} meters toward {0} o'clock: that spot is {2} meters nearer. I will look again when you are there"),
                        hour, meters, gain)
                    : string.Format(Text("trial_next",
                        "Next try: {1} meters toward {0} o'clock, {2} meters nearer"),
                        hour, meters, gain);
            }
        }
        else if (_final)
        {
            body = string.Format(Text("step_last",
                "Last step: {1} meters toward {0} o'clock, and that is the objective"), hour, meters);
        }
        else if (_index <= 1 && _total > 1)
        {
            body = string.Format(Text("round_start",
                "Found a measured way round, {0} steps. Step one: {2} meters toward {1} o'clock"),
                _total, hour, meters);
        }
        else
        {
            body = string.Format(Text("step", "Step {0}: {2} meters toward {1} o'clock"), _index, hour, meters);
        }
        // "到了" only when the player actually walked the previous one: the first step of a plan is
        // not an arrival, and saying so would announce an arrival that never happened.
        bool arrived = _kind == Kind.Trial ? _index > 1 : _advanced;
        return (arrived ? Text("here", "There.") : "") + body + tail;
    }

    /// <summary>A place the mod knows but cannot name still has to be SAID. "A doorway" is worth
    /// walking towards; silence about where the leg ends is not.</summary>
    private static string Anonymous(LandmarkRouter.Kind kind) => Text(Key(kind), Fallback(kind));

    private static string Key(LandmarkRouter.Kind kind) => kind switch
    {
        LandmarkRouter.Kind.Portal => "anon_portal",
        LandmarkRouter.Kind.FastTravel => "anon_travel",
        LandmarkRouter.Kind.MapIcon => "anon_icon",
        _ => "anon_spot",
    };

    private static string Fallback(LandmarkRouter.Kind kind) => kind switch
    {
        LandmarkRouter.Kind.Portal => "a doorway or a ladder",
        LandmarkRouter.Kind.FastTravel => "a fast travel point",
        LandmarkRouter.Kind.MapIcon => "a place marked on the map",
        _ => "a place along the way",
    };

    /// <summary>The bearing <paramref name="degrees"/> to the right of the line from
    /// (<paramref name="px"/>, <paramref name="pz"/>) to the objective — the same fan and the same
    /// handedness <see cref="RoundPlanner"/> was calibrated with in game.</summary>
    private static void FanToGoal(float px, float pz, float tx, float tz, float degrees, out float dx, out float dz)
    {
        float bx = tx - px, bz = tz - pz;
        float len = MathF.Sqrt(bx * bx + bz * bz);
        if (!(len > 0.01f)) { dx = 1f; dz = 0f; return; }
        float ux = bx / len, uz = bz / len;
        double rad = degrees * Math.PI / 180.0;
        float c = (float)Math.Cos(rad), s = (float)Math.Sin(rad);
        float rx = -uz, rz = ux;                       // rightward = forward x up, confirmed in game
        dx = ux * c + rx * s;
        dz = uz * c + rz * s;
        float n = MathF.Sqrt(dx * dx + dz * dz);
        if (n > 0f) { dx /= n; dz /= n; }
    }

    private static void Remember(float x, float z)
    {
        if (_visited.Count >= MaxVisited) _visited.RemoveAt(0);
        _visited.Add((x, z));
    }

    private static bool AlreadyVisited(float x, float z)
    {
        foreach (var v in _visited)
        {
            float dx = v.X - x, dz = v.Z - z;
            if (dx * dx + dz * dz <= VisitedM * VisitedM) return true;
        }
        return false;
    }

    /// <summary>Add a point to the track of where the player has been. One point every few
    /// metres: the question this answers is "have I walked here", not "how did I move".</summary>
    private static void TrackWalk(float px, float pz)
    {
        if (_walked.Count > 0 && Flat(px, pz, _walked[^1].X, _walked[^1].Z) < TrackM) return;
        if (_walked.Count >= MaxWalked) _walked.RemoveAt(0);
        _walked.Add((px, pz));
    }

    /// <summary>Has the player already walked here? A try that lands on their own track is the
    /// way they came, not a way on.</summary>
    private static bool NearWalked(float x, float z)
    {
        foreach (var w in _walked)
        {
            float dx = w.X - x, dz = w.Z - z;
            if (dx * dx + dz * dz <= NearWalkedM * NearWalkedM) return true;
        }
        return false;
    }

    private static float Flat(float ax, float az, float bx, float bz)
    {
        float dx = bx - ax, dz = bz - az;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static void Speak(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        ScreenReaderService.Speak(text, interrupt: false);
        API.LogInfo("[SF6Access] Walk: " + text);
        // Anything at all having been said, this is not a silent street.
        _quietSince = 0;
    }

    /// <summary>Keys live under <c>wt.steps.*</c>: <c>wt.walk.*</c> is taken by
    /// <see cref="AutoWalkText"/>, and two readers sharing a prefix is how a line gets rewritten
    /// by the wrong feature.</summary>
    private static string Text(string key, string fallback) => LangFile.Get("wt.steps." + key, fallback);
}
