using System;
using System.Runtime.InteropServices;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;
using SF6Access.Services;
using SF6Access.Services.WorldTour;

namespace SF6Access.Hooks.WorldTour;

/// <summary>
/// <b>F12 — the spoken way-finding switch (since 2026-10-03 22:25). The StartNavi trial it was
/// built for is retired and never runs.</b>
///
/// <para>Pressing F12 turns the "no route reaches it, so measure a direction that lands nearer
/// and offer it as the next thing to walk" search on, and pressing it again turns it off; it
/// always says which way it went. The work behind it is <see cref="WalkSteps"/>.</para>
///
/// <para>(What the key used to do: press it with a target selected and it asked
/// <c>AvatarBase.StartNavi</c> to walk the avatar to the current destination. That is the part
/// <c>TRIAL_ENABLED</c> keeps switched off, and the machinery below is kept compiled rather than
/// deleted because <c>ObstacleHold</c>, the takeover rule and <c>docs/wt-autowalk.md</c> are
/// written against it.)</para>
///
/// <para><b>Why a separate key and not the feature.</b> Everything up to now has been a read;
/// this is the first call in the auto-walk feature that hands the engine a structure the mod
/// built itself, so its failure modes are unknown. On its own key, a wrong guess costs this key
/// and nothing else: the player's working takeover, the radar, and the auto-walk selection are
/// all left alone. This hook exists to answer exactly one question — <i>will the engine walk the
/// avatar when asked this way?</i> — and once it will, the trial is folded into
/// <see cref="AutoWalk"/> and this key is removed.</para>
///
/// <para><b>The safety rule still applies.</b> The player's own stick always wins: the same
/// per-frame position check that guards auto-walk guards this, and a trial walk ends the moment
/// the player moves. A blind player cannot see what the mod is doing, so the mod must never
/// fight them for the controller.</para>
///
/// <para>Design notes: <c>docs/wt-autowalk.md</c> ("The seventh finding").</para>
/// </summary>
public class NaviTrialHooks
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_F12 = 0x7B;
    private static bool _lastKeyState;
    private static bool _running;

    // ---------------------------------------------------------------------------------
    // THE TWENTY-SEVENTH FINDING — the trial is RETIRED (2026-09-29 21:19).
    //
    // The 21:19 run stored the single route point cleanly (direct store, read back,
    // pointer and count untouched), issued StartNavi — and the game FAULTED 30 ms later
    // inside app.PostZone.lateUpdate with System.ValueType.GetHashCode in the stack, the
    // same fault family as the 16:04 crash. Three crashes in one day, every one of them
    // downstream of this feature WRITING an engine-built structure, while every READ the
    // mod makes (positions, bend lists, catalogs) has been safe for weeks.
    //
    // The owner decided the same hour: stop auto-navigation; perfect the read-only
    // spoken guidance instead. The bends the guidance needs are already available and
    // proven (the 20:51 run recalled a 13-corner route with zero extra queries), so the
    // guidance gets the whole value of the route work without a single engine write.
    //
    // F12 therefore only SAYS the trial is retired. Everything below the early return in
    // Tick is the machinery that would have run — kept compiled because ObstacleHold,
    // the takeover rule, and the docs are written against it — and unreachable while
    // this is false. Do not flip it back without re-reading docs/wt-autowalk.md from
    // the twenty-fifth finding onward.
    // ---------------------------------------------------------------------------------
    private const bool TRIAL_ENABLED = false;

    // There is deliberately NO position cache and NO speed reading in this hook.
    // Takeover is decided by which controller holds the input slot (see
    // NaviTrial.PlayerHasTheController), because speed cannot separate a walk the engine is
    // performing from a player pushing the same way — both read 2–4 m/s, and reading speed
    // here is what cancelled the first working walk 54 ms after it started.

    // Where this walk is going, kept as plain numbers so a walk can be asked for again after
    // an obstacle is broken. Nothing engine-owned is held across frames — the rule this mod
    // has paid for twice.
    //
    // Written by the key handler below and read by the obstacle block, which is currently
    // commented out — so these are assigned and not read, which the compiler rightly flags.
    // Kept rather than deleted because the obstacle handling is the next step and its state
    // should not have to be rebuilt to switch it on; the suppression goes away with the
    // comment block.
#pragma warning disable CS0414 // assigned but never used: read once the obstacle block is enabled
    private static float _goalX, _goalY, _goalZ;

    /// <summary>Is navigation currently paused because something in the world has to be broken
    /// first? Kept here rather than inside <see cref="ObstacleHold"/> because it is this hook
    /// that owns the walk's lifecycle.</summary>
    private static bool _held;
#pragma warning restore CS0414

    [PluginEntryPoint]
    public static void Initialize() =>
        API.LogInfo("[SF6Access] NaviTrialHooks initialized (F12 = switch spoken way-finding on/off; " +
                    "the StartNavi trial itself is retired)");

    /// <summary>
    /// <b>Field change or mod reload: forget every trace of the walk in progress.</b> The
    /// destination numbers matter as much as the obstacle state — a resumed walk that still
    /// holds coordinates from the previous city would ask the engine to navigate to a place
    /// that no longer exists, which is the stale-wrapper failure this codebase documents.
    /// </summary>
    internal static void Reset()
    {
        _held = false;
        _goalX = _goalY = _goalZ = 0f;
        ObstacleHold.Reset();
    }

    [Callback(typeof(LateUpdateBehavior), CallbackType.Post)]
    public static void Tick()
    {
        // The trial is retired (the twenty-seventh finding, above), so no StartNavi is ever
        // asked for: nothing here builds, writes, or calls an engine object.
        //
        // THE KEY, HOWEVER, IS NOT RETIRED — it now belongs to spoken way-finding (2026-10-03
        // 22:25). The owner was out walking the city when the trial search landed, and asked for
        // it to answer a key rather than the silence: "找路的这个改成我按 F12 就打开". F12
        // therefore toggles WalkSteps' trial search — the "when no route reaches it, measure a
        // direction that lands nearer" work — and says which way it went. Same key, same place
        // in the frame, and still nothing that moves the avatar.
        if (!TRIAL_ENABLED)
        {
            bool toggleDown = (GetAsyncKeyState(VK_F12) & 0x8000) != 0;
            bool toggleEdge = toggleDown && !_lastKeyState;
            _lastKeyState = toggleDown;
            if (toggleEdge) WalkSteps.ToggleTrial();
            return;
        }

        // Everything below is unreachable while TRIAL_ENABLED is false. It is kept compiled
        // — not deleted — because ObstacleHold, the takeover rule, and docs/wt-autowalk.md are
        // all written against this exact flow, and a partial delete would rot them silently.
#pragma warning disable CS0162 // unreachable code: deliberate while the trial is retired

        // NaviTrial publishes the yield itself, at the moment it calls StartNavi and at the moment
        // it ends — see NaviTrial.TryWalk and NaviTrial.End. It is deliberately NOT re-derived from
        // NaviTrial.Active here: this hook and the beacon are both LateUpdate.Post, so a per-frame
        // refresh in one plugin can land a frame AFTER the other plugin has already read the flag,
        // and one frame is long enough for AutoWalk to write a straight-line order into a
        // controller while the engine's pathfinder is walking the same avatar. That collision is
        // the eighteenth finding; the fix is to have exactly one writer, at the exact instant of
        // the handover rather than once per frame.

        // Player takeover. Read from the CONTROLLER, not from the avatar's speed, because speed
        // cannot tell a working walk from a player pushing in the same direction — both read
        // 2–4 m/s. What does distinguish them is who holds the input slot: while the engine
        // walks for us the current priority is ours, and the moment the game hands the slot
        // back to the player's own Default controller, the player has taken over. That is the
        // same fact the rest of this codebase already relies on (a registered controller owns
        // the input slot), read rather than inferred.
        if (NaviTrial.Active && NaviTrial.PlayerHasTheController(WorldTourStateService.GetAvatarManager()))
        {
            NaviTrial.TakeOver(FieldRayCaster.PlayerAvatar());
            return;
        }

        // The verdict on whether the engine's pathfinder is actually moving the avatar. This is the
        // trial's whole question, and with AutoWalk standing down (see AutoWalk.Drive) any movement
        // measured here belongs to StartNavi and nothing else — so the reading is unambiguous in a
        // way it has never been before.
        NaviTrial.MeasureWalk();

        // ---------------------------------------------------------------------------------
        // OBSTACLE HOLD — built, compiled, and deliberately NOT WIRED UP YET.
        //
        // The owner's idea (2026-09-29): when reaching the destination needs something broken,
        // no amount of pathfinding gets there, so the mod should stop in front of the obstacle,
        // name it, wait for the player to break it, then resume the same journey. ObstacleHold
        // implements exactly that and is complete.
        //
        // It is not wired in here because it was built in the same session that first made
        // StartNavi actually walk, and that walk had just been cancelled twice by two different
        // bugs. Verifying "the walk now runs" and "the walk now handles obstacles" in one build
        // would make a failure in either indistinguishable from the other. So this build proves
        // the walk and the takeover; the obstacle case goes in next, on its own.
        //
        // To enable: uncomment this block and the ObstacleHold.Reset() calls below. Nothing
        // else is needed — the state it requires (_goalX/Y/Z, _held) is already maintained.
        //
        // NOTE for when it goes in: it runs OUTSIDE the `NaviTrial.Active` block on purpose. A
        // hold clears Active (navigation is no longer being asked to move), so a check nested
        // inside that block would never run again and the journey could never resume.
        //
        // if (_held || NaviTrial.Active)
        // {
        //     switch (ObstacleHold.Tick(FieldRayCaster.PlayerAvatar(), _goalX, _goalY, _goalZ))
        //     {
        //         case ObstacleHold.Verdict.Hold:
        //             NaviTrial.SuspendForObstacle();
        //             _held = true;
        //             return;
        //
        //         case ObstacleHold.Verdict.Resume:
        //             if (_held)
        //             {
        //                 _held = false;
        //                 NaviTrial.ResumeAfterObstacle(FieldRayCaster.PlayerAvatar(), _goalX, _goalY, _goalZ);
        //             }
        //             return;
        //     }
        // }
        // ---------------------------------------------------------------------------------

        bool down = (GetAsyncKeyState(VK_F12) & 0x8000) != 0;
        bool edge = down && !_lastKeyState;
        _lastKeyState = down;
        if (!edge || _running) return;

        if (WorldTourStateService.GetAvatarManager() == null)
        {
            API.LogInfo("[SF6Access] Navi trial: not in the World Tour field (AvatarManager null)");
            ScreenReaderService.Speak(AutoWalkText.Unavailable(), interrupt: false);
            return;
        }

        _running = true;
        try
        {
            var avatar = FieldRayCaster.PlayerAvatar();
            if (avatar == null)
            {
                API.LogWarning("[SF6Access] Navi trial: no player avatar");
                ScreenReaderService.Speak(AutoWalkText.Unavailable(), interrupt: false);
                return;
            }

            var dest = BeaconDestination.Resolve();
            if (!dest.Ok)
            {
                API.LogInfo("[SF6Access] Navi trial: no target selected");
                ScreenReaderService.Speak(AutoWalkText.Unavailable(), interrupt: false);
                return;
            }

            // The ROUTE LEG's end, never the aim point. The aim point is deliberately pushed off
            // the objective so the homing sound is not pinned inside it, and walking to it would
            // stop the avatar short of the destination — the bug that cost test cycle 2.
            API.LogInfo($"[SF6Access] Navi trial: target key={dest.Key}, word={dest.Word}, " +
                        $"aim=({dest.X:F2}, {dest.Y:F2}, {dest.Z:F2}), " +
                        $"legEnd=({dest.TargetX:F2}, {dest.TargetY:F2}, {dest.TargetZ:F2}), ladder={dest.Ladder}, " +
                        $"noRoute={dest.NoRoute}, holding={dest.Holding}, searching={dest.Searching}, " +
                        $"aboveM={dest.AboveM:F2}");

            // A fresh walk: the obstacle watch starts from zero, and it is told where this walk
            // is going so it can ask for the same place again after a break.
            ObstacleHold.Reset();
            _held = false;
            _goalX = dest.TargetX; _goalY = dest.TargetY; _goalZ = dest.TargetZ;

            // `dest` goes in whole, not just its co-ordinates: its NoRoute / Holding flags are the
            // beacon's own cached engine verdict for THIS target, and re-asking would collide with
            // the pathfinder's rate limit (see NaviTrial.TryWalk's remarks — the 15:08 trial lost
            // that race on every press and the gate let every wall-bound walk through).
            NaviTrial.TryWalk(avatar, dest.TargetX, dest.TargetY, dest.TargetZ, dest);
        }
        catch (Exception ex)
        {
            // ToString(), not Message: the trial run that taught this logged only
            // "NullReferenceException: Object reference not set to an instance of an object."
            // with no frame, which turned a one-minute fix into a code-reading exercise. An
            // exception in a key-triggered path is always worth its stack.
            API.LogError($"[SF6Access] Navi trial hook failed: {ex}");
        }
        finally { _running = false; }
#pragma warning restore CS0162
    }
}
