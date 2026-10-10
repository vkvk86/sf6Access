using System;
using System.Globalization;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;
using SF6Access.Services;
using SF6Access.Services.WorldTour;

namespace SF6Access.Hooks.WorldTour;

/// <summary>Automatic route bearing, route-state notices and contact-based arrival.</summary>
public class MissionBeaconHooks
{
    // ---------------------------------------------------------------------------------
    // AUTO-WALK IS OFF — and stays off until the owner asks for it by name.
    //
    // The owner, 2026-09-29 22:2x: "我进入游戏选择目的地后还是会有自动导航，他不是 f12 的问题，
    // 是之前我们设计的按 a 键寻路的问题，请关掉这个自动导航，他有时候会和我抢手柄."
    //
    // The report is exact and it names the mechanism, not a symptom: nothing here is the
    // retired F12 trial (TRIAL_ENABLED is false in NaviTrialHooks). This is the auto-walk
    // that starts the moment the player picks a destination — the beacon resolves a target
    // and then drives the avatar itself. It "fights for the controller" because that is
    // literally what it does: AutoWalk registers an AvatarInputController at FieldEvent
    // priority, which outranks the player's own Default controller, so the player's stick
    // is competing with a driver that re-issues its order every frame. For a blind player
    // that is the one thing the codebase's own safety rule forbids — the mod must never
    // fight them for their own avatar.
    //
    // WHAT STAYS ON, deliberately: everything that only SPEAKS. The homing cue, the route
    // state notices ("已到达", "正在搜索路线"), the ladder grip line and the turn-by-turn
    // "右转 / 前方 12 米" guidance are all reads, and they are the half of this feature the
    // twenty-seventh finding chose to keep. Turning auto-walk off must not turn the
    // guidance off with it — a player who was being walked to the target now has to walk
    // there themselves, which makes the spoken directions MORE necessary, not less.
    //
    // HOW TO TURN IT BACK ON: flip this to true and nothing else. The driver underneath
    // (AutoWalk.Drive) is untouched and still documented; re-read docs/wt-autowalk.md
    // first, because the last three crashes all came from this feature writing into the
    // engine, and SetChaseWalk's semantics were never actually settled.
    // ---------------------------------------------------------------------------------
    private static readonly bool AutoWalkEnabled = false;

    internal const float ARRIVED_M = 4f; // Existing proximity band; no longer confirms interaction.
    private const long ReaderHoldMs = 1200;
    private const long ResolveMs = 500; // Two route refreshes/sec, independent of frame rate.
    private const long RepeatMs = 15000;
    private const long DirectionHoldMs = 4000;
    // How often the "nothing walkable reaches it" sentence is repeated while the player stands at
    // the end of the walk. Often enough to be found again after a lap round whatever blocks them,
    // far enough apart that it is not chatter.
    private const long FootRepeatMs = 12000;
    // The objective has sat inside the arrival band this long without the game's own interactable
    // list ever holding it: the interaction is not coming from where the player stands, and the
    // ride-along "look for a door on another side" line starts (see the near-limbo hint below).
    // Long enough that simply walking the last steps and pressing the button is never talked over.
    private const long NearLimboMs = 8000;
    // A ladder search can run up to its question budget (about thirty seconds): say the elapsed
    // time this often so silence never reads as a hang.
    private const long SearchNoteMs = 6000;
    // A route is followed by ear, so even far away a turn must be heard within a step or two
    // (tester 2026-09-26: at 2.5 s it was "impossible to follow"). The cadence still speeds up near.
    private const int CueNearGapMs = 100, CueFarGapMs = 500;
    private static readonly HomingCue Cue = new("mission beacon.mp3", ARRIVED_M, 60f, CueNearGapMs, CueFarGapMs);
    private const long FaultLogMs = 10000;
    private static long _nextResolve, _nextWords, _nextTurn;
    private static long _nextFaultLog;
    private static string _faultKey;
    private static long _nextFoot, _nextSearchNote;
    // When the objective entered the "near but the game offers no interaction" limbo (0 = not in it).
    private static long _nearNoContactSince;
    private static int _searchNotesSpoken;
    private static string _key, _lastState;
    private static int _lastHour;
    private static int _lastTurnPhase;
    private static float _lastMeters;
    private static bool _haveFix, _atContact, _ladderOffered;
    private static bool _autoWalkToldOff, _autoWalkAnnounced;
    // The player's own last position and the tick it was read at, so "is the PLAYER
    // moving the avatar" can be answered from the game's own reading of the player
    // rather than by asking an engine method (see the note at the auto-walk call).
    private static float LastPlayerX, LastPlayerY, LastPlayerZ;
    private static long _lastPlayerTick;
    /// <summary>A second, independent position cache for the per-frame safety check in
    /// <see cref="Tick"/>. Kept separate from the Resolve cache on purpose — see the note
    /// where it is used.</summary>
    private static float LastFastX, LastFastZ;
    private static long _lastFastTick;
    private static BeaconDestination _fix;
    /// <summary>Is the homing cue allowed to sound. Separate from <see cref="_haveFix"/> on
    /// purpose: a fix is a destination the planner has finished deciding, and things that DRIVE
    /// from it (the step-by-step walk, the other beacons yielding the air) must wait for that.
    /// The cue is not a driver — it is the answer to "which way", and it has one even mid-search.
    /// <see cref="Resolve(long)"/> sets it; <see cref="Hush"/> clears it.</summary>
    private static bool _cueOn;
    /// <summary>Whether the cue is currently pointing at a landing rather than the aim, so the
    /// switch is logged once and not once per frame.</summary>
    private static bool _cueToLanding;
    /// <summary>Whether the cue has been swung off a leg that no walk reaches onto the objective —
    /// again so the switch is logged once and not once per frame.</summary>
    private static bool _cueToGoal;
    internal static bool Homing => _haveFix;

    [PluginEntryPoint]
    public static void Initialize() => API.LogInfo("[SF6Access] MissionBeaconHooks initialized (automatic route status and contact arrival)");

    [Callback(typeof(LateUpdateBehavior), CallbackType.Post)]
    public static void Tick()
    {
        FieldPresenceService.Refresh();
        if (!FieldPresenceService.CanSpeak) { Reset(); return; }
        if (PadGuideHooks.Active || SpTalkNovelHooks.DialogueActive) { Hush(); return; }
        // EVERY SENSING CALL BELOW IS GUARDED, and that is the point rather than defensive
        // decoration. On 2026-10-05 a single out-of-range index inside LevelRoute.Pump threw
        // on every frame from 13:31:05.906 to the end of the session. Nothing crashed and
        // nothing was logged but one line of REFramework boilerplate — and the homing cue,
        // the route notices and the whole spoken walk simply stopped, because they all sit
        // further down THIS method and an exception here means they are never reached.
        // For a player who navigates by ear that is the one failure the project exists to
        // prevent: silence, with a target still held. So a fault is caught, named once in
        // the log, and the tick goes on to Ping() — the bearing survives its own sensor.
        try { LevelRoute.Pump(); }
        catch (Exception ex) { Fault("LevelRoute.Pump", ex); LevelRoute.Reset(); }
        // The step-by-step walk: its ray search has to be affordable inside about half a second,
        // because a search whose root the player has already walked away from describes somewhere
        // they no longer are — the same reason RoundPlanner casts from here rather than from
        // Resolve's twice-a-second grid.
        // The step-by-step walk needs a destination that has finished being decided. While the
        // planner is still asking questions the fix moves under it, so it waits — but the CUE
        // does not (see <see cref="_cueOn"/>): a silent beacon is the one failure this project
        // exists to prevent, and it is what the owner reported on 2026-10-07 ("voice only, the
        // homing sound was not taking me there").
        if (_haveFix && !_fix.Searching)
        {
            var step = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
            if (step.ok)
            {
                try { WalkSteps.Pump(_fix, step.x, step.y, step.z); }
                catch (Exception ex) { Fault("WalkSteps.Pump", ex); }
            }
        }
        // Remembered while walking rather than read on demand: the picker then offers what has
        // been within arm's length lately, not only what the single frame of opening sees.
        try { InteractableCatalog.Pump(); } catch (Exception ex) { Fault("InteractableCatalog.Pump", ex); }
        // A whole-field sweep, one object family per tick: this is what makes an object you are
        // not standing next to appear in the picker at all. Asked while walking rather than on
        // demand — see SceneTargetCatalog.
        try { SceneTargetCatalog.Pump(); } catch (Exception ex) { Fault("SceneTargetCatalog.Pump", ex); }
        // The lifts, every couple of seconds and not only when the list is opened: a lift exists
        // in the scene only while its section is streamed in, so the moment it does appear has
        // to be caught whether or not the player is holding the picker open at the time.
        try { LiftCatalog.Pump(); } catch (Exception ex) { Fault("LiftCatalog.Pump", ex); }
        // Following whatever has been caught moving, several times a second — but only the
        // object families that have already proved they hold something that moves, so the cost
        // is a handful of reads rather than a second scene walk. See MotionWatch.
        try { MotionWatch.Pump(); } catch (Exception ex) { Fault("MotionWatch.Pump", ex); }
        try { SwitchLink.Pump(); } catch (Exception ex) { Fault("SwitchLink.Pump", ex); }
        // Listening at anything within arm's reach: what the player pressed at it, and what the
        // object looked like either side of that press. Purely diagnostic — see InteractProbe.
        try { InteractProbe.Pump(); } catch (Exception ex) { Fault("InteractProbe.Pump", ex); }
        long now = Environment.TickCount64;
        // --- the safety rule, every frame -------------------------------------------------
        // Auto-walk is driven from Resolve (twice a second), but the player's right to take
        // their own avatar back is checked HERE, every frame, and that is the point: the
        // 500 ms grid is fine for steering, and far too coarse for a hand-over. A blind
        // player who pushes the stick and feels nothing for half a second will push harder
        // and conclude the game has frozen. This does not issue orders — it only ever ends
        // one — so it stays cheap and cannot fight the walk.
        if (AutoWalk.Active)
        {
            var here = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
            if (here.ok)
            {
                // Its OWN position cache, separate from Resolve's — and that separation is
                // the fix rather than a detail. Both readings are "how fast did the player
                // move between two observations", and a per-frame check sharing Resolve's
                // cache would keep the window at one frame (so a 3 m/s walk reads as a huge
                // spike, while a slow push reads as noise), and Resolve sharing this one's
                // would shrink its window to a frame too. Two observers, two windows.
                long delta = _lastFastTick == 0 ? 0 : now - _lastFastTick;
                float speed = PlayerSpeedMps(here, delta, LastFastX, LastFastZ);
                LastFastX = here.x; LastFastZ = here.z; _lastFastTick = now;
                // LooksLikePlayerMovement, not the bare speed: a walk the mod started moves the
                // avatar at this speed too, and cancelling it here is what killed the first
                // working navi walk.
                if (AutoWalk.LooksLikePlayerMovement(speed))
                {
                    var av = FieldRayCaster.PlayerAvatar();
                    AutoWalk.TakeOver(av);
                    return;
                }
            }
        }
        // The cue is a short sound, not speech: it keeps playing under other readers' lines.
        Ping();
        if (now - ScreenReaderService.LastInterruptTick < ReaderHoldMs) return;
        // F12 was pressed a moment ago and is still owed its first line of guidance. Answered
        // here, on the per-frame tick, rather than on the twice-a-second grid below: "按 F12 后
        // 立刻开始引导". A press answered half a second later is a press the player repeats.
        if (_haveFix && WalkSteps.PendingImmediate)
        {
            var at = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
            if (at.ok && WalkSteps.SpeakNow(_fix, at.x, at.y, at.z, now)) return;
        }
        if (now >= _nextResolve)
        {
            _nextResolve = now + ResolveMs;
            // Same reason as the guards above: Resolve decides WHAT the beacon is pointing
            // at, but the cue is sounded above and must not depend on this succeeding.
            try { Resolve(now); }
            catch (Exception ex) { Fault("Resolve", ex); }
        }
    }

    /// <summary><b>Name a fault once, in words a tester can find, and carry on.</b> The
    /// whole reason this exists is that the alternative is silence: an exception thrown
    /// inside a per-frame tick is caught by REFramework, reported as one boilerplate line
    /// that names neither the plugin nor the method, and the rest of the tick is never
    /// run — which on 2026-10-05 cost the player their homing cue for the rest of the
    /// session without a single word in the log saying why. One line per fault per ten
    /// seconds: enough to be found, not enough to bury everything else.</summary>
    private static void Fault(string where, Exception ex)
    {
        long now = Environment.TickCount64;
        string key = where + ": " + ex.GetType().Name;
        if (key == _faultKey && now < _nextFaultLog) return;
        _faultKey = key;
        _nextFaultLog = now + FaultLogMs;
        API.LogInfo($"[SF6Access] FAULT {key} — {ex.Message}; the bearing and the homing cue keep running");
    }

    private static void Resolve(long now)
    {
        var dest = BeaconDestination.Resolve();
        if (!dest.Ok) { Reset(); return; }
        var player = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        if (!player.ok) { Hush(); return; }
        var avatar = FieldRayCaster.PlayerAvatar();
        if (dest.Key != _key)
        {
            _key = dest.Key; _lastState = null; _lastHour = 0; _lastTurnPhase = 0; _nextWords = _nextTurn = 0; _atContact = false;
            _nearNoContactSince = 0; // a fresh destination starts with a clean limbo slate
            // A DIFFERENT destination means the player made a fresh choice — pressed A on
            // something new. That is the one moment a previous "this reading does not drive
            // the avatar" verdict stops being valid: the verdict was measured against the
            // old target's point, and the first walk of a session often goes out before the
            // city has finished loading in, which says nothing about the method. Every other
            // verdict is genuinely about the engine and is kept.
            AutoWalk.BeginWalk();
        }
        if (dest.Holding) return;
        float distance = MathF.Sqrt(FieldAccessCatalog.DistanceSq(dest.TargetX, dest.TargetY, dest.TargetZ,
            player.x, player.y, player.z));
        bool contact = !dest.IsTracked && !dest.IsApproach && !dest.Ladder && FieldActionReader.AtMissionContact();
        if (contact)
        {
            Hush();
            if (!_atContact) ScreenReaderService.Speak(ExplorationText.Get("mission_contact", "Objective within interaction range"), interrupt: true);
            _atContact = true;
            _nearNoContactSince = 0; // interaction confirmed: the limbo is over
            return;
        }
        _atContact = false;
        // THE CUE MUST NEVER GO QUIET WHILE A TARGET IS HELD. A first attempt at this code muted it
        // whenever no walkable link reached the objective ("the bearing points through a wall, so
        // stop pointing"), and the owner's verdict was immediate: "没有雷达让我怎么找" — for a blind
        // player the bearing and the cue are the ONLY instrument that exists for the last metres.
        // A bearing that meets a wall is still the whole answer to "which way is it"; what was
        // missing was not silence but the one extra fact: something stands between. So the cue
        // keeps playing and the hint rides ALONGSIDE the bearing, never in place of it (2026-10-02).
        _fix = dest; _haveFix = !dest.Searching;
        // THE CUE OUTLIVES THE SEARCH. Until 2026-10-07 this line's predecessor was the whole
        // of the cue's on-switch, and a search — up to 120 navmesh questions, i.e. half a minute
        // at the shared throttle — turned the beacon OFF for as long as it ran. What the player
        // got in that half minute was speech and nothing else, which is precisely the state the
        // comment above this one was written to forbid.
        //
        // A search is not a reason to stop pointing. Nothing has been decided yet, so what the
        // cue points at is the objective itself — the plain bearing, no route, no bend — which is
        // exactly what the bearing is for and exactly what "which way is it" means. Once the
        // planner answers, the aim narrows to the leg it chose and the cue follows it there.
        _cueOn = true;
        // Leaving a "near" state takes twice the distance, so standing at the edge does not repeat it.
        bool near = distance <= (RouteNotice.IsNear(_lastState) ? 2 * ARRIVED_M : ARRIVED_M);
        string state = RouteNotice.State(dest, near);
        bool stateChanged = state != _lastState;
        string previous = _lastState;
        _lastState = state;
        if (stateChanged)
        {
            string status = RouteNotice.Text(state, previous, dest, distance, player.y);
            if (status != null)
            {
                ScreenReaderService.Speak(status, interrupt: true);
                API.LogInfo("[SF6Access] Mission navigation: " + status);
                _nextWords = now + DirectionHoldMs;
                return;
            }
        }
        // THE GRAND TOUR (T key). Its arrivals, its skips and its "this stop cannot be
        // reached" verdicts are decided from the destination this resolver already holds:
        // one observer, no second route planner, no new engine calls. True when the tour
        // spoke, taking the same yield-the-tick convention every speech site here follows.
        if (TourService.Observe(dest, player.x, player.y, player.z, now)) return;
        // F4 — THE WALL ON THE RIGHT (owner, 2026-10-04). It was switched on by hand, so while it
        // is running it takes the sentence: the player asked to be walked along the wall, and what
        // it says is a step like any other. The bearing and the homing cue are untouched by it —
        // nothing here switches a cue off, which is the one thing that must never happen.
        if (WallFollow.TrySpeak(player.x, player.y, player.z, now))
        {
            _nextWords = now + DirectionHoldMs;
            return;
        }
        if (AnnounceSearchProgress(now)) return;
        if (dest.Searching) return;
        // AUTO-WALK: while a target is selected, the mod drives the avatar toward the
        // current aim itself, using the game's own walk driver (AutoWalk). The beacon's
        // turn-by-turn speech is suppressed while driving — telling the player "turn
        // right in 3 m" while the mod is already turning them is noise, and a blind
        // player cannot tell which of the two is in control. The homing cue keeps
        // playing so the direction is still audible if they take over.
        //
        // The player's own stick is detected from the game's own reading of their own
        // world position — the same source every other reader in this mod uses. An
        // earlier version read AvatarBase.GetMoveVec() instead, and that method returns
        // a REFERENCE type: REFramework passes a returned reference through the callee's
        // out slot, and the call left the game in "VMContext: Caught exception code
        // c0000005" with the avatar unable to move. Nothing about "is the player
        // steering" is worth another call shaped like that one, so the player's own
        // displacement between two reads is the whole test. The comparison must be made
        // BEFORE the stored reading is replaced — the delta is last frame versus this
        // one, and overwriting first would make it a constant zero, i.e. "the player is
        // never steering", which is the exact opposite of the rule this exists to keep.
        long playerDeltaMs = _lastPlayerTick == 0 ? 0 : now - _lastPlayerTick;
        float playerSpeedMps = PlayerSpeedMps(player, playerDeltaMs);
        LastPlayerX = player.x; LastPlayerY = player.y; LastPlayerZ = player.z;
        _lastPlayerTick = now;
        // TWO points, and the difference matters (see AutoWalk.AimSlackM):
        //   dest.X/Y/Z        — the beacon's AIM point, pushed off the objective so the
        //                       homing sound is not pinned inside it;
        //   dest.TargetX/Y/Z  — the current ROUTE LEG's real end, i.e. where the player
        //                       is actually going (a ladder foot, an approach point, the
        //                       objective when nothing is in the way).
        // Auto-walk must drive to the leg end; walking to the aim point walks nowhere,
        // which is exactly what the first build did — 4.6 m of aim against a ladder foot
        // 17.1 m away, so the avatar would cross a couple of metres and report Arrived.
        // AutoWalkEnabled (see the field declaration): while it is false this whole block is
        // skipped, so no input controller is ever created against the player's own. The
        // guidance below is what the player gets instead.
        if (AutoWalkEnabled && AutoWalk.Drive(avatar, dest.X, dest.Y, dest.Z, dest.TargetX, dest.TargetY, dest.TargetZ,
                           player.x, player.y, player.z, playerSpeedMps))
        {
            AnnounceAutoWalkStart();
            _nextWords = now + DirectionHoldMs;
            return;
        }
        if (AutoWalk.LastStop == AutoWalk.Stop.PlayerTookOver && !_autoWalkToldOff)
        {
            _autoWalkToldOff = true;
            API.LogInfo("[SF6Access] Auto walk: player took over");
        }
        else if (AutoWalk.LastStop != AutoWalk.Stop.PlayerTookOver)
        {
            _autoWalkToldOff = false;
        }
        if (AnnounceLadderGrip(dest, near, player.x, player.y, player.z)) { _nextWords = now + DirectionHoldMs; return; }
        // THE WALK ITSELF, one step at a time. The bearing says WHICH WAY the objective is; this
        // says HOW: "第 2 步：朝 5 点方向走 12 米", and when no route reaches it at all, a measured
        // direction to TRY that lands nearer, re-searched on arrival so the player is walked
        // through a sequence of real tries instead of being handed a bearing and a shrug.
        // (Owner, 2026-10-03: "让导航找不到路也要带我尝试不同的路，而不是让我自己找".)
        // It never displaces the bearing — that is the owner's one instrument for the last metres —
        // it only shares the cadence with it, exactly as the turn instructions already do.
        if (WalkSteps.TrySpeak(dest, player.x, player.y, player.z, now))
        {
            _nextWords = now + DirectionHoldMs;
            return;
        }
        var forward = FieldDirectionService.GetCameraForward();
        float toCorner = MathF.Sqrt(FieldAccessCatalog.DistanceSq(dest.X, player.y, dest.Z,
            player.x, player.y, player.z));
        bool turnNow = toCorner <= FieldRayCaster.CapsuleRadius();
        int turnPhase = dest.Turn * (turnNow ? 2 : 1);
        if (turnPhase != 0 && turnPhase != _lastTurnPhase
            && (now >= _nextTurn || (turnNow && Math.Abs(_lastTurnPhase) == 1)))
        {
            string side = dest.Turn > 0 ? ExplorationText.Get("right", "right") : ExplorationText.Get("left", "left");
            string instruction = turnNow
                ? string.Format(ExplorationText.Get("turn_now", "Turn {0} now"), side)
                : string.Format(ExplorationText.Get("turn_in", "Turn {0} in {1} meters"), side,
                    Math.Max(1, (int)MathF.Round(toCorner)));
            ScreenReaderService.Speak(instruction, interrupt: false);
            _lastTurnPhase = turnPhase; _nextWords = _nextTurn = now + DirectionHoldMs;
            return;
        }
        if (turnPhase == 0) _lastTurnPhase = 0;
        // THE BEARING FOLLOWS THE CUE. When no walk reaches the leg, both point at the objective
        // instead — the sound and the sentence have to agree, or the player is pulled one way and
        // told another. The metres are then the straight line to the objective too, and the name is
        // the objective's own rather than the stairs they cannot reach.
        bool offLeg = dest.AimUnreachable && dest.GoalY != 0f;
        float bx = offLeg ? dest.GoalX : dest.X, bz = offLeg ? dest.GoalZ : dest.Z;
        int hour = FieldDirectionService.ClockHour(forward, bx - player.x, bz - player.z);
        float meters = offLeg
            ? MathF.Sqrt(FieldAccessCatalog.DistanceSq(dest.GoalX, dest.GoalY, dest.GoalZ,
                  player.x, player.y, player.z))
            : dest.RouteM > 0 ? dest.RouteM : distance;
        bool changed = hour != _lastHour || meters < _lastMeters / 2 || meters > _lastMeters * 2;
        if (now < _nextWords || (!changed && now < _nextWords + RepeatMs - DirectionHoldMs)) return;
        string what = offLeg && !string.IsNullOrEmpty(dest.GoalWord) ? dest.GoalWord
                                                                     : RouteNotice.Heading(dest);
        string text = hour > 0 ? LocalizedText.AtClockMeters(what, hour, (int)MathF.Round(meters))
                              : LocalizedText.AtMeters(what, (int)MathF.Round(meters));
        // THE WALK IS OVER and the player is at its end: the bearing still names a direction they
        // cannot march along, because whatever blocked the navmesh stands in it. SAY THAT — after
        // the bearing, in a handful of words, and not every time (FootRepeatMs): "任务目标，在10点钟
        // 方向，3米。走不过去，它在下方2米" reads as information, whereas replacing the bearing with
        // a paragraph reads as being abandoned and, worse, leaves them with no direction at all.
        //
        // THE NEAR LIMBO is the second way a walk ends, and the first build of this left it silent:
        // the objective is inside the arrival band, the game's own interactable list does NOT hold
        // it, and the engine may still claim a path (a snap artifact the wall sonar contradicts),
        // so AtEndOfWalk never fired — 2026-10-02 22:38, the SiRN building site: two metres, a site
        // wall, 交互未确认, and then the same bare bearing for over a minute while the player pushed
        // the wall. The game's own list refusing the target IS the fact "you cannot interact from
        // here"; after NearLimboMs of it, say what that fact means — alongside the bearing, on the
        // same slow cadence, never instead of it.
        bool limboNear = near && !dest.IsApproach && !dest.Ladder && !dest.RelayHop;
        if (limboNear) { if (_nearNoContactSince == 0) _nearNoContactSince = now; }
        else _nearNoContactSince = 0;
        bool nearWithoutContact = limboNear && now - _nearNoContactSince >= NearLimboMs;
        bool endOfWalk = RouteNotice.AtEndOfWalk(dest);
        if ((endOfWalk || nearWithoutContact) && now >= _nextFoot)
        {
            _nextFoot = now + FootRepeatMs;
            // Both facts at once (at the foot AND the interaction will not confirm): the end-of-walk
            // hint knows the height, so it wins.
            //
            // "Follow the wall round" was advice a blind player could not act on: it names neither
            // how far the wall is nor which way the open end lies. Both are measured here
            // (WayRound), so the sentence carries them when the measurement has them.
            if (nearWithoutContact)
                WayRound.Probe(player.x, player.z, dest.TargetX, dest.TargetZ);
            string hint = endOfWalk ? RouteNotice.EndHint(dest) : RouteNotice.ContactHint();
            if (hint != null) text += "。" + hint;
        }
        ScreenReaderService.Speak(text, interrupt: false);
        API.LogInfo("[SF6Access] Mission navigation: " + text);
        _lastHour = hour; _lastMeters = meters; _nextWords = now + DirectionHoldMs;
    }

    /// <summary><b>How fast the player themselves is moving the avatar, in metres per
    /// second</b>, from two readings of the game's own player position and the wall
    /// time between them. This is how the mod answers "is the player steering?" without
    /// asking the engine: the alternative (AvatarBase.GetMoveVec) returns a reference
    /// type and crashed the input VM on 2026-09-29, and a device poll cannot tell the
    /// player's stick from the stick value the game is synthesising for us.
    ///
    /// <para>Horizontal only, deliberately: a walk slides along the ground, so the
    /// vertical component is kerb and slope noise, and treating it as input would cancel
    /// the walk the moment the avatar stepped up something. A genuinely wrong walk is a
    /// full-speed horizontal march, which this sees at once.</para>
    ///
    /// <para>0 while there is no previous reading (the first frame after a target is
    /// picked) — "no information" must never read as "the player is walking".</para>
    ///
    /// <para><b>The horizontal displacement is what matters, and the threshold is a
    /// SPEED, not a distance.</b> At the beacon's 500 ms resolve cadence a walk of
    /// 3 m/s is 1.5 m per sample and a run several times that, while a player standing
    /// still jitters by centimetres — so the two are orders of magnitude apart and the
    /// test does not need to be delicate. What it does need is to not confuse
    /// "the avatar is following the order the mod just issued" with "the player pushed
    /// the stick": it cannot, because the reading is taken before the order and one
    /// frame after the previous one, so only the player can have caused it.</para></summary>
    private static float PlayerSpeedMps(in (float x, float y, float z, bool ok) player, long deltaMs)
    {
        if (deltaMs <= 0 || _lastPlayerTick == 0) return 0f;
        float dx = player.x - LastPlayerX, dz = player.z - LastPlayerZ;
        return MathF.Sqrt(dx * dx + dz * dz) * 1000f / deltaMs;
    }

    /// <summary>The same measurement against an explicit cache, for the per-frame check
    /// (which must not disturb Resolve's window — see <see cref="LastFastX"/>).</summary>
    private static float PlayerSpeedMps(in (float x, float y, float z, bool ok) player, long deltaMs,
                                       float lastX, float lastZ)
    {
        if (deltaMs <= 0) return 0f;
        float dx = player.x - lastX, dz = player.z - lastZ;
        return MathF.Sqrt(dx * dx + dz * dz) * 1000f / deltaMs;
    }

    /// <summary>Say once, when the mod first takes the avatar, that the player still
    /// holds the controls. Auto-walk moving a blind player's character without saying
    /// so would be the worst kind of surprise.</summary>
    private static void AnnounceAutoWalkStart()
    {
        if (_autoWalkAnnounced) return;
        _autoWalkAnnounced = true;
        ScreenReaderService.Speak(AutoWalkText.Started(), interrupt: false);
        API.LogInfo("[SF6Access] Auto walk: started (player input cancels it)");
    }

    /// <summary>While a ladder search runs, say how long it has been going, so a long search is not
    /// indistinguishable from a hung mod. The first sentence comes from the route state change; this
    /// repeats it with a count every few seconds. True when it spoke (the caller then yields).</summary>
    private static bool AnnounceSearchProgress(long now)
    {
        if (!LevelRoute.Searching)
        {
            _nextSearchNote = 0;
            _searchNotesSpoken = 0;
            return false;
        }
        if (now < _nextSearchNote) return true; // still searching, not this hook's turn to speak
        int seconds = (int)(LevelRoute.SearchElapsedMs / 1000);
        _nextSearchNote = now + SearchNoteMs;
        // A short search says nothing extra: the state change already announced it.
        if (_searchNotesSpoken == 0 && seconds < 3) return true;
        _searchNotesSpoken++;
        int shown = seconds < 1 ? 1 : seconds;
        ScreenReaderService.Speak(RouteNotice.SearchingStill(shown), interrupt: false);
        API.LogInfo($"[SF6Access] Mission navigation: searching for a route, {shown} seconds");
        return true;
    }

    /// <summary>At a planned ladder: says once when the game starts offering it, and logs what the game offers.</summary>
    private static bool AnnounceLadderGrip(in BeaconDestination dest, bool near, float x, float y, float z)    {
        if (!dest.Ladder || !near) { _ladderOffered = false; return false; }
        LadderAccess.Diagnose(dest.LadderAccess, dest.BottomRungM, x, y, z);
        bool offered = LadderAccess.Offered(dest.LadderAccess);
        bool fresh = offered && !_ladderOffered;
        _ladderOffered = offered;
        if (fresh) ScreenReaderService.Speak(RouteNotice.LadderOffered(), interrupt: true);
        return fresh;
    }

    private static void Ping()
    {
        if (!_cueOn || !Cue.Due) return;
        var player = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        if (!player.ok) return;
        float direct = MathF.Sqrt(FieldAccessCatalog.DistanceSq(_fix.TargetX, _fix.TargetY, _fix.TargetZ,
            player.x, player.y, player.z));
        // Continue to guide the final approach until contact, including inside the old 4 m band.
        //
        float ax = _fix.X, az = _fix.Z;

        // THE CUE POINTS WHERE THE NEXT STEP GOES. Not at the objective — at the place the player
        // is walking to: a bend in the measured route while there is one, the leg's own end when a
        // plan is being followed, the bare objective only when nothing has been decided. (Owner,
        // 2026-10-07: "我要的是归航音永远指向我下一步该走的方向，而不是告诉我从这里走下去，
        // 那我怎么知道往哪个方向走下去". Telling someone the ground falls away is not pointing
        // them anywhere; the sound has to say WHICH WAY, every metre, including the one that goes
        // over an edge.)
        //
        // AND THEN THE LAST METRE. Standing on the lip, the aim is underfoot: horizontal offset
        // zero, pan back to centre, and the direction vanishes at the one moment it matters most —
        // one step forward is the way down and one step back is nothing. So near the aim the cue
        // swings to the LANDING, the spot the rays actually measured below. The player walks
        // toward the sound, steps off toward it, and the next plan turns it to the objective.
        //
        // THE TEST IS GEOMETRY, NOT WHETHER A PLAN EXISTED. An earlier version of this fired only
        // when the planner had produced a drop leg — and on the very field this was written for
        // the planner produced no plan at all ("no plan after 6 questions"), so the cue never
        // turned and the player was left at the lip with a centred sound and a sentence. Whether
        // the goal is underneath you is a fact about two heights and needs nobody's permission:
        // <see cref="BeaconDestination.GoalY"/> against the player's own. A planner that failed
        // is exactly the case where the sound is the only thing left.
        //
        // NB: `out _` will not compile in this namespace — `_` is a namespace here (CS0118) —
        // so every discarded out gets a real name.
        // THE AIM CANNOT BE WALKED TO. Then it is not the next step and the sound must not pull at
        // it — that is what "42 metres of stairs" across a wall felt like: the cue dragged the
        // player at a place with no walk to it, and the wall was the first evidence they got.
        // Point at the OBJECTIVE instead. It is not a route either, and it may lie through the same
        // wall, but it is the thing being looked for, and a bearing is never switched off — this
        // only decides which of the two the bearing is about.
        if (_fix.AimUnreachable && _fix.GoalY != 0f)
        {
            ax = _fix.GoalX; az = _fix.GoalZ;
            if (!_cueToGoal)
            {
                _cueToGoal = true;
                API.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "[SF6Access] Homing cue: no walk reaches the leg at ({0:F1}, {1:F1}) — " +
                    "pointing at the objective ({2:F1}, {3:F1}) instead",
                    _fix.X, _fix.Z, ax, az));
            }
            Cue.Sound(FieldDirectionService.GetCameraForward(), ax - player.x, az - player.z,
                MathF.Sqrt(FieldAccessCatalog.DistanceSq(_fix.GoalX, _fix.GoalY, _fix.GoalZ,
                    player.x, player.y, player.z)));
            return;
        }
        if (_cueToGoal) _cueToGoal = false;

        bool below = _fix.GoalY != 0f && player.y - _fix.GoalY >= LevelRoute.LevelM;
        if (below)
        {
            float flat = MathF.Sqrt((ax - player.x) * (ax - player.x) + (az - player.z) * (az - player.z));
            if (flat <= LipAimM)
            {
                DescentScan.Ensure();
                if (DescentScan.TryLandingNear(player.x, player.y, player.z,
                                               out float gx, out float gy, out float gz))
                {
                    ax = gx; az = gz;
                    // Said once per turn rather than once per frame: the cue sounds several times
                    // a second up close and this line has to stay findable in the log.
                    if (!_cueToLanding)
                    {
                        _cueToLanding = true;
                        API.LogInfo(string.Format(CultureInfo.InvariantCulture,
                            "[SF6Access] Homing cue: the aim is underfoot and the goal is {0:F1} m below — " +
                            "pointing at the landing ({1:F1}, {2:F1}, {3:F1}) instead",
                            player.y - _fix.GoalY, gx, gy, gz));
                    }
                }
                else if (_cueToLanding) _cueToLanding = false;
            }
            else if (_cueToLanding) _cueToLanding = false;
        }
        else if (_cueToLanding) _cueToLanding = false;

        Cue.Sound(FieldDirectionService.GetCameraForward(), ax - player.x, az - player.z,
            _fix.RouteM > 0 ? _fix.RouteM : direct);
    }

    /// <summary>How near the aim has to be for "the aim is underfoot" to be true. Smaller than
    /// <see cref="ARRIVED_M"/> on purpose: this is not arrival, it is the last metre before a
    /// step the player has to choose to take.</summary>
    private const float LipAimM = 2.5f;

    private static void Hush() { _haveFix = false; _cueOn = false; Cue.Reset(); }
    private static void Reset()
    {
        Hush(); _key = _lastState = null; _nextWords = 0; _atContact = _ladderOffered = false; _lastTurnPhase = 0;
        _nextSearchNote = 0; _nextFoot = 0; _searchNotesSpoken = 0; _nearNoContactSince = 0;
        _autoWalkToldOff = _autoWalkAnnounced = false;
        LastPlayerX = LastPlayerY = LastPlayerZ = 0f; _lastPlayerTick = 0;
        LastFastX = LastFastZ = 0f; _lastFastTick = 0;
        AutoWalk.Reset();
        // The spoken walk belongs to the spot it was measured in; another field or another
        // objective leaves none of it standing.
        WalkSteps.Reset();
        // The trial walk holds an engine-visible request and route buffer, so a field change
        // must drop them here as surely as it drops the auto-walk controller: a leftover
        // request pointing at memory from the previous city is the stale-wrapper crash this
        // codebase documents in docs/sf6-architecture.md.
        NaviTrial.Reset();
        // Another field: the objects the sweep remembered belong to somewhere else.
        SceneTargetCatalog.Reset();
        // The lifts are written down before the set is cleared — which lifts this city has is
        // exactly what must survive a loading screen.
        LiftCatalog.Reset();
        // Another field's movers: an address here is an object there, and a family that travels
        // in one yard is not assumed to travel in the next.
        MotionWatch.Reset();
        SwitchLink.Reset();
        InteractProbe.Reset();
        // A wall being followed is a wall in the field that is gone: the walk ends with it
        // rather than carrying a handhold across a loading screen.
        WallFollow.Reset();
        NaviTrialHooks.Reset();
        BeaconDestination.Reset();
    }
}
