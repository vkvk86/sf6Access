using System;
using System.Collections.Generic;
using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>Auto-walk — stage 2: actually move the avatar.</b>
///
/// <para><b>What it does.</b> While a navigation target is selected, it drives the
/// avatar toward the current route aim using the game's OWN walk driver,
/// <c>AvatarInputController.SetChaseWalk(vec3, vec3, bool)</c> — the method the
/// game itself uses for NPC walk-in interactions. It therefore follows world
/// geometry instead of pushing a raw vector into a wall, which is why this route was
/// chosen over <c>SetMove</c> (see <c>docs/wt-autowalk.md</c>; the API surface was
/// dumped in game 2026-09-29).</para>
///
/// <para><b>How the controller is made.</b> <c>AvatarBase.CreateInputController
/// (PriorityType)</c> exists (confirmed), so the game allocates and registers it:
/// priority <c>FieldEvent</c> sits above the player's <c>Default</c> (so ours wins)
/// but below <c>MenuUI</c> (so the pause menu and phone still win — the player can
/// always take back control by opening a menu).</para>
///
/// <para><b>The safety rule, above all else.</b> A blind player cannot see what the
/// mod is doing, so the mod must never fight them. On 2026-09-29 the first version of
/// this file did, and the way it did is worth keeping written down: it read
/// <c>AvatarBase.GetMoveVec()</c> every frame to detect the player's stick, and that
/// method RETURNS A REFERENCE TYPE. By the rule the rest of this codebase learned the
/// expensive way (<see cref="FieldProbeService"/>, "a by-ref parameter whose type is a
/// REFERENCE type gets NO buffer"), REFramework passes a returned reference through
/// the callee's own out slot — and that one call alone left the game in
/// <c>VMContext: Caught exception code c0000005</c> with the avatar unable to move
/// while the camera still worked. The player flagged it immediately: "I can only turn
/// the camera, not move the character". <b>The read is gone; only the documented,
/// by-value reader of the player's own position is used now, and no leave of this file
/// may reintroduce an engine call that is not a plain by-value read or a by-value
/// actuator call.</b></para>
///
/// <para><b>A failed order must leave nothing behind.</b> If the engine rejects a walk
/// order we destroy the controller we made rather than keep it alive at
/// <c>FieldEvent</c> priority: a registered controller that consumes the player's
/// action slot but does nothing is worse than no controller at all.</para>
/// </summary>
internal static class AutoWalk
{
    private const string BASE_TYPE = "app.worldtour.avatar.AvatarBase";
    private const string CONTROLLER_TYPE = "app.worldtour.avatar.AvatarInputController";
    private const string MANAGER_TYPE = "app.worldtour.avatar.AvatarInputControllerManager";
    private const string PRIORITY_ENUM = "app.worldtour.avatar.AvatarInputController.PriorityType";

    /// <summary>Stop this far from the leg end. Matches the beacon's arrival band, so
    /// "the walk stopped" and "the beacon says you have arrived" agree.</summary>
    private const float StopM = 4f;

    /// <summary><b>What the two vec3 arguments MEAN.</b> The TDB publishes types, not
    /// semantics, and on 2026-09-29 the first two guesses both failed to move the avatar
    /// — so the reading is now a candidate list tried in order, each one measured, with
    /// the winner kept for the rest of the session. This is the only honest way to
    /// settle it from a log.
    ///
    /// <para>The candidates come from the sibling overload, which takes a
    /// <c>GameObject viewObj</c> where this one takes <c>vec3 viewPos</c>. That
    /// substitution says the pair is <b>(a point, the viewpoint the point is measured
    /// from)</b> — the same shape as <c>SetCrowdStand(targetObj, targetRotDir)</c> and
    /// <c>SetSpTalkTurn(lookDir, …)</c> elsewhere in the dump. But a walk order that
    /// already carries an absolute point has no use for a viewpoint, which is the case
    /// for the third candidate: the point is where to GO and the vec3 is the DIRECTION
    /// TO FACE when arriving. That reading is the one unique to a walk that ends facing
    /// something, and it is why the parameter is a bare direction rather than an
    /// object.</para></summary>
    private enum Semantics
    {
        /// <summary>vec = the destination world point (viewPos ignored).</summary>
        AbsolutePoint = 0,
        /// <summary>vec = the destination RELATIVE to viewPos.</summary>
        RelativeVector = 1,
        /// <summary>vec = destination world point; viewPos = the direction to face on arrival.</summary>
        PointThenFace = 2,
    }

    private static readonly Semantics[] SemanticsOrder =
    {
        Semantics.AbsolutePoint, Semantics.PointThenFace, Semantics.RelativeVector,
    };

    /// <summary>How long the straight-line route gets to prove it moves the avatar, in
    /// milliseconds. Same reasoning as <see cref="CALIBRATE_MS"/>: a working route covers
    /// a metre in well under this, and a route that does nothing costs this much once
    /// before the walk is handed back rather than left pulling.</summary>
    private const long STRAIGHT_VERIFY_MS = 1500;

    /// <summary><b>The move-rate values tried, in order.</b> <c>SetSlaveMoveRate(float)</c>'s
    /// unit is not published anywhere this mod can read, and the first guess — <c>1f</c>,
    /// read as "one walk" — was accepted by the engine on 2026-09-29 12:47 and moved the
    /// avatar 0.00 m over 21 s. Rather than guess again in a build, each candidate gets a
    /// verification window and the first one that measurably moves the avatar is kept for
    /// the session. <c>3f</c> is a walk speed if the unit is m/s (the mod's own walk is
    /// about 2–4 m/s), <c>100f</c> is the same value in cm/s.</summary>
    private static readonly float[] StraightRates = { 1f, 3f, 100f };

    /// <summary>How long each candidate gets before it is judged, in milliseconds.
    /// A candidate that walks the avatar the WRONG WAY trips the distance test long
    /// before this; a candidate that does nothing at all costs this much once.</summary>
    private const long CALIBRATE_MS = 1200;

    /// <summary><b>The slack allowed between the beacon's aim point and the point the
    /// avatar is actually driven to.</b> These are not the same point and must not be:
    /// <c>dest.X/Y/Z</c> is pushed out past the capsule so the homing sound is not
    /// pinned inside the objective's own head (a mission marker on the player's exact
    /// position is inaudible), while <c>dest.TargetX/Y/Z</c> is the current ROUTE LEG's
    /// real end. Driving to the aim point — which is what the first build did — asks the
    /// engine to walk to a point that deliberately is not where the player is going:
    /// measured 2026-09-29, it was 4.6 m away while the actual ladder foot was 17.1 m
    /// away, so the order had the avatar cross a couple of metres and then stop with
    /// <c>Arrived</c>. Inside this slack the aim point is used instead, so the landing
    /// spot still matches what the beacon called "you have arrived".</summary>
    private const float AimSlackM = 1.5f;

    /// <summary><b>How fast the player's own avatar has to be moving before that counts
    /// as the player steering, in METRES PER SECOND.</b> The reading is the player's own
    /// world displacement between two frames (computed in
    /// <c>MissionBeaconHooks.Resolve</c> from the game's own player position — the only
    /// source for it that does not involve calling an engine method that returns a
    /// reference type; see the class remarks). A walk is about 2–4 m/s and a run more,
    /// so this is deliberately below a walk: anything at this speed that is not ours is
    /// the player, and the player's claim on their own avatar is absolute.
    ///
    /// <para><b>"That is not ours" is the whole rule, and it used to be free.</b> While the
    /// only actuators this mod had were the ones the engine ignored, the avatar never moved
    /// because of anything the mod did, so ANY movement above this speed really was the
    /// player and the reading could be taken at face value. On 2026-09-29
    /// <c>AvatarBase.StartNavi</c> finally drove the avatar, and the rule turned on itself:
    /// the engine now moves the avatar at exactly the speed the rule blames on the player, so
    /// the moment a walk began the walk was cancelled as a takeover. The owner's report is the
    /// symptom in one line — <i>"这次角色动了，但每次都只动两步就停了"</i> — and the log shows
    /// the cause: <c>StartNavi ... issued</c> followed by <c>ended (player took over)</c>
    /// 54 milliseconds later. <b>Speed is a consequence, not an intent.</b> It has to be
    /// paired with whether the mod is the one producing it, which is what
    /// <see cref="EngineIsDriving"/> is for.</para>
    ///
    /// <para>Horizontal only: a walk slides along the ground, so the vertical component
    /// is kerb and slope noise, and counting it would cancel the walk the moment the
    /// avatar stepped up something. Owner 2026-09-29: "some objects can only be walked
    /// through by climbing a ladder… it needs to plan around them" — a cancel-on-kerb
    /// rule would defeat exactly that.</para></summary>
    private const float PlayerSpeedThresholdMps = 0.8f;

    /// <summary>
    /// <b>Is the ENGINE currently the thing moving the avatar?</b>
    ///
    /// <para>Set by whoever last asked the engine to walk — the navi trial while its request
    /// is live, the straight-line and chase drivers while their orders hold — and cleared the
    /// moment that claim ends. The takeover test reads it to answer the question its speed
    /// reading cannot answer on its own: <i>is this movement mine or the player's?</i></para>
    ///
    /// <para><b>Why a flag and not a cleverer measurement.</b> The alternative is to compare
    /// the avatar's heading against the requested heading, but a player steering straight at
    /// the same destination is indistinguishable from a working walk by that test, and the one
    /// source that would settle it — the player's raw stick vector — is closed to this
    /// codebase: <c>AvatarBase.GetMoveVec()</c> returns a REFERENCE type, and calling it is
    /// what left the game in <c>VMContext: Caught exception code c0000005</c> on 2026-09-29
    /// with the avatar unable to move at all (see the class remarks). A flag the driver itself
    /// sets needs no engine call, cannot crash, and is exactly as accurate as the mod's
    /// knowledge of its own actions.</para>
    ///
    /// <para><b>Whether the engine keeps walking after this is cleared is measured, not
    /// assumed</b> — that is what the navi trial exists to find out, and the log says so.</para>
    /// </summary>
    internal static bool EngineIsDriving { get; set; }

    /// <summary>The takeover speed, published so the per-frame safety check in the beacon
    /// can use the SAME threshold the driver does. Two different numbers for "the player
    /// is steering" would be two different definitions of the safety rule.</summary>
    internal static float TakeoverSpeedMps => PlayerSpeedThresholdMps;

    /// <summary>
    /// <b>Is this movement the player's, rather than something the mod already knows
    /// about?</b> The single place the "who is moving the avatar" question is answered, so
    /// the beacon's per-frame check and the walk driver cannot drift apart.
    /// </summary>
    /// <param name="speedMps">The avatar's own horizontal displacement, in metres/second.</param>
    internal static bool LooksLikePlayerMovement(float speedMps) =>
        speedMps >= PlayerSpeedThresholdMps && !EngineIsDriving;

    /// <summary><b>The player has taken their avatar back.</b> Ends the walk, clears
    /// anything the walk left in the movement component, and gives the input controller
    /// back to the game — in that order, because the controller is the thing that holds
    /// the player's stick hostage. Safe to call when no walk is running.</summary>
    internal static void TakeOver(ManagedObject avatar)
    {
        if (!_active && _controller == null && !_straightWrote) return;
        StopAndCancel(avatar, Stop.PlayerTookOver);
        Speak(AutoWalkText.PlayerTookOver());
    }

    /// <summary>Observations a candidate gets before it is judged, in orders. The window
    /// is whichever of this and <see cref="CALIBRATE_MS"/> ends first.</summary>
    private const int CALIBRATE_SAMPLES = 6;

    private static ManagedObject _controller;
    private static Method _createController, _setChaseWalk, _removeChaseWalk, _destroyController;
    /// <summary>The straight-line escape hatch on <c>AvatarPlayerControll</c>, resolved
    /// but only used after <see cref="_setChaseWalk"/> has measurably failed — see
    /// <see cref="OrderStraightLine"/>.</summary>
    private static Method _setSlaveMoveVec, _setSlaveMoveRate;
    /// <summary>True once every reading of <c>SetChaseWalk</c> has measurably failed on
    /// THIS walk, so orders now go out through the straight-line route instead. Reset
    /// whenever a new walk starts.</summary>
    private static bool _useStraightLine;
    private static FieldOutBuffer _straightVec;
    private static bool _straightLogged;
    /// <summary>Whether the CURRENT walk has ever actually been seen to move the avatar.
    /// The same rule the chase-walk calibration follows (measure the effect, never trust
    /// that the call was accepted) applies to the straight-line route, and on 2026-09-29
    /// it is what caught the route being accepted-and-ignored for 21 s.</summary>
    private static bool _ok;
    private static long _okArmedAt;
    private static float _okDistWhenArmedM;
    /// <summary>True while a non-zero direction is KNOWN to be sitting in the movement
    /// component. Distinct from <see cref="_useStraightLine"/>, which says which route the
    /// current walk is on: this one survives <see cref="BeginWalk"/>, because a direction
    /// left in the component is a real thing in the world and must be cleared by whoever
    /// stops the walk regardless of which target is now selected.</summary>
    private static bool _straightWrote;
    /// <summary>Index into <see cref="StraightRates"/> of the value currently being tried,
    /// and the value itself.</summary>
    private static int _straightRateIndex;
    private static float StraightRate => StraightRates[Math.Min(_straightRateIndex, StraightRates.Length - 1)];
    /// <summary>True once the "no rate works" verdict has been said for this walk, so it
    /// is said once (see the note at the verdict).</summary>
    private static bool _straightDeadLogged;
    private static object[] _createArgs;
    private static int _priorityFieldEvent = -1;
    private static TypeDefinition _vec3Type;
    // Long-lived vec3 argument buffers, created once and reused (see Order). NOT
    // disposed per call: FieldRayCaster and FieldNavPath have always passed their
    // vec3 arguments from buffers held for the life of the process.
    private static FieldOutBuffer _target, _view;
    private static bool _resolved, _loggedUnavailable;
    private static bool _active, _retired;
    /// <summary>The reading currently being tried, and its index in
    /// <see cref="SemanticsOrder"/>. Advances only on a measured failure.</summary>
    private static Semantics _semantics = Semantics.AbsolutePoint;
    private static int _semanticsIndex;
    private static bool _loggedThisTry;

    // Calibration (see Drive): one try per candidate, then never again.
    // _windowOpen marks the moment the order has actually STARTED being issued. It is not
    // the same as _armedAt: `_armedAt` is stamped on the first frame, but the controller
    // is created on that same frame, so the first few orders can be swallowed while the
    // controller is still settling. Judging a candidate from _armedAt would therefore also
    // debit the candidate for the engine's warm-up — and since the FIRST candidate's window
    // contains the whole controller creation, an all-candidates-fail verdict could be a
    // verdict on the warm-up rather than on the semantics. Clearing _distWhenArmedM on the
    // first SUCCESSFUL order restarts the measurement at the true `_distWhenArmedM = _armedAt`,
    // every time.
    private static long _armedAt;
    private static float _distWhenArmedM;
    private static int _ordersSince;
    private static bool _calibrated;
    /// <summary>True once the current candidate's measurement window has actually opened.</summary>
    private static bool _windowOpen;

    /// <summary>True while the mod is driving. The beacon uses this to hush its own
    /// turn-by-turn speech: telling the player "turn right in 3 m" while the mod is
    /// already turning them is noise.</summary>
    internal static bool Active => _active;

    /// <summary><b>True when the mod believes it is driving but nothing it did is
    /// working.</b> Diagnosed from the 2026-09-29 12:47 log, where the straight-line route
    /// reported itself active for 21 s while the avatar's own speed stayed
    /// <c>0.00 m/s</c> and even the collision cue stopped firing — i.e. the component
    /// write was accepted and had no effect. Reporting "active" in that state is worse
    /// than reporting nothing: it is the state in which the player is left unable to take
    /// the controls back, so the walk is ended and the reason is spoken instead.</summary>
    internal static bool BelievedToBeWalkingButIsNot => _active && !_ok;

    /// <summary>Why auto-walk last stopped, for a one-off spoken line.</summary>
    internal enum Stop { None, Arrived, PlayerTookOver, Unavailable, Left }

    internal static Stop LastStop { get; private set; } = Stop.None;

    /// <summary>Feed the current aim. <paramref name="avatar"/> may be null.
    ///
    /// <para><b>The order is issued on EVERY FRAME, and that is a correction rather
    /// than a preference.</b> The first version rate-limited at 250 ms, on the
    /// assumption that the game's walk order is a persistent state. The reading
    /// produced by that assumption — "the walk order did not move the avatar" after
    /// exactly one observation — is also what a REVOKED order looks like: an action
    /// whose sole holder is a request controller is commonly dropped at the end of
    /// the frame it was seen in, and because the order is written after the input
    /// system has already sampled the controller, a given order is in any case not
    /// read until the NEXT frame. So the order is re-issued every frame, which is
    /// correct under either reading and costs one engine call — the same thing
    /// <see cref="FieldRayCaster"/> does 450 times a second.</para>
    ///
    /// <para><paramref name="playerSpeedMps"/> is how fast the player's own avatar moved
    /// between the last two frames — the caller's own reading of the game's own player
    /// position. <b>Nothing else may be read to detect the player's stick:</b> the
    /// method that looks like the natural choice for it returns a reference type and is
    /// what crashed the input VM on 2026-09-29.</para>
    ///
    /// <para>Returns true when the mod issued a walk order this frame.</para></summary>
    internal static bool Drive(ManagedObject avatar, float aimX, float aimY, float aimZ,
                               float legX, float legY, float legZ,
                               float playerX, float playerY, float playerZ, float playerSpeedMps)
    {
        // ---------------------------------------------------------------------------------
        // THE EIGHTEENTH FINDING — the trial and this driver were fighting for the avatar.
        //
        // The F12 trial calls StartNavi, and StartNavi is the engine's own pathfinder. This
        // method is a DIFFERENT way of moving the same avatar: it registers an input controller
        // and writes a vector into it every frame. Both were running at once. The trial log of
        // 2026-09-29 15:35 shows the collision in the open:
        //
        //   15:35:13.932  Auto walk: trying semantics ABSOLUTE point (1/3); destination=(-248.54, …)
        //   15:35:17.162  Navi trial: StartNavi(priority=4 (MenuUI), req) issued
        //   15:35:19.476  Auto walk: STRAIGHT-LINE route in use; to=(-248.57, …)
        //
        // The trial's walk was not the thing driving the avatar — this was, from BEFORE the
        // keypress until long after, to a destination the trial had never chosen. That is why a
        // walk pointing at a target in front of the player pushed him into a wall behind him:
        // this driver still held a leg end from an earlier target and the two orders cancelled
        // into whatever the world had between them.
        //
        // So the yield is total, and it is the mirror of EngineIsDriving: while the engine's own
        // pathfinder owns the avatar, this driver issues nothing, holds no controller, and reports
        // that it is not driving. Anything less is two drivers on one avatar.
        // ---------------------------------------------------------------------------------
        if (EngineIsDriving)
        {
            // Give back anything this driver already holds, ONCE — the controller is the thing
            // that can hold the player's stick hostage, so it must not survive the handover.
            if (_active || _controller != null || _straightWrote)
            {
                StopAndCancel(avatar, Stop.Left);
                API.LogInfo("[SF6Access] Auto walk: standing down — the engine's own pathfinder " +
                            "(StartNavi) owns the avatar; this driver will issue nothing until it ends");
            }
            return false;
        }

        if (avatar == null) { Stop_(Stop.Left); return false; }
        // Once a walk order has actually faulted, this session is over. Re-issuing it
        // and re-allocating a controller every frame against a broken VM is precisely
        // how the 2026-09-29 crash produced ninety repeats.
        if (_retired) return false;

        // --- the safety rule, checked FIRST, before any order is issued ---
        // NOTE the asymmetry with the code below, because it is deliberate: on takeover the
        // walk is stopped (and the controller destroyed) REGARDLESS of _active. _active only
        // records "an order was issued this walk"; a controller can exist without it, and
        // when one does, leaving it registered is what cost the player the controls on
        // 2026-09-29 12:47. The spoken line still fires once per takeover.
        //
        // The test is LooksLikePlayerMovement, not the raw speed: a walk this mod started is
        // also movement at this speed, and treating it as a takeover is what made the first
        // working navi walk cancel itself after 54 ms.
        if (LooksLikePlayerMovement(playerSpeedMps))
        {
            bool wasActive = _active;
            StopAndCancel(avatar, Stop.PlayerTookOver);
            if (wasActive) Speak(AutoWalkText.PlayerTookOver());
            return false;
        }

        // Already routed through the fallback on this walk: keep using it, and do NOT
        // re-create the chase controller (it was destroyed when the fallback took over).
        if (_useStraightLine)
            return DriveStraightLine(avatar, legX, legY, legZ, aimX, aimY, aimZ,
                                     playerX, playerY, playerZ);

        // --- WHICH POINT TO WALK TO ----------------------------------------------------
        // Computed BEFORE the controller is created, and that ordering is the fix for the
        // 2026-09-29 12:47 loss of control rather than a tidy-up. Every check that can
        // abandon a walk is now on this side of the allocation: the destination choice, the
        // arrival test, and the whole calibration window. Creating the controller first and
        // then returning from any of them is what left a FieldEvent controller registered
        // with nobody driving it — and the game keeps giving the input slot to a controller
        // that exists, whatever it does with it. Structurally, from here on: the controller
        // is created only on the path that is certain to issue an order this frame, and
        // every other path never made one.
        //
        // The leg's real end, not the beacon's aim point. The aim point exists to move
        // the homing SOUND off the objective's own position (see AimSlackM) and is
        // routinely nowhere near where the player is actually going, so walking to it
        // walks nowhere. The aim point is used only once the avatar is already inside
        // the slack, where it is the better landing spot.
        float aimFlat = FlatDistance(playerX, playerZ, aimX, aimZ);
        bool useAim = aimFlat <= StopM + AimSlackM;
        float targetX = useAim ? aimX : legX, targetY = useAim ? aimY : legY, targetZ = useAim ? aimZ : legZ;
        float flat = FlatDistance(playerX, playerZ, targetX, targetZ);
        if (flat <= StopM) { StopAndCancel(avatar, Stop.Arrived); return false; }

        long now = Environment.TickCount64;

        // --- SEMANTICS CANDIDATES -------------------------------------------------------
        // The meaning of the (vec3, vec3) pair is not in the TDB. On 2026-09-29 THREE
        // separate in-game measurements — ABSOLUTE point, POINT then face and RELATIVE
        // vector — each closed exactly 0.0 m, and all three were run against the same
        // stale 4.63 m beacon-aim point. See the note on SemanticsOrder: re-running that
        // measurement a fourth time is not worth the player's time; the list is now kept
        // for the case where a candidate DOES work, and the route that does not depend on
        // this method's unknown semantics is `AvatarPlayerControll.SetSlaveMoveVec`.
        //
        // Each candidate is still tried IN GAME and MEASURED rather than trusted, and the
        // measurement is the codebase's own rule (CameraAimService): a read-back through
        // the wrapper you wrote through is NOT a verification — measure the effect. Here
        // the effect is the avatar's own world position.
        //
        // It must not fire on a single sample: the position is read by a DIFFERENT
        // reader than the one this code moves, the city is full of pushing NPCs, and a
        // sample taken mid-corner can be half a metre worse than the one before. So each
        // candidate gets a whole window and the TOTAL has to go the right way. A wrong
        // reading does not creep — it walks the other way at full speed — so a whole
        // window is still an immediate verdict for the cases that matter.
        //
        // A verdict about the SEMANTICS is only valid once the POINT is known to be
        // right, and at 4.63 m the point was wrong (the route's first leg ends 17.13 m
        // away at the foot of ladder #4). So a word of care for whoever reads this next:
        // the three 0.0 m readings are NOT evidence that SetChaseWalk cannot drive this
        // avatar — they are evidence that it does not drive the avatar to a point 4.6 m
        // away that is itself already inside the arrival radius.
        if (!_calibrated)
        {
            if (!_windowOpen)
            {
                _windowOpen = true;
                _armedAt = now;
                _distWhenArmedM = flat;
                _ordersSince = 0;
                LogSemanticsOnce(targetX, targetY, targetZ, playerX, playerY, playerZ);
            }
            else if (_ordersSince >= CALIBRATE_SAMPLES || now - _armedAt >= CALIBRATE_MS)
            {
                float closed = _distWhenArmedM - flat;
                long elapsed = now - _armedAt;
                if (closed > 0.5f)
                {
                    _calibrated = true;
                    API.LogInfo($"[SF6Access] Auto walk: calibration OK - closed {closed:F1} m " +
                                $"({_distWhenArmedM:F1} -> {flat:F1} m) over {elapsed} ms in {_ordersSince} orders. " +
                                $"MEASURED SEMANTICS = {Name(_semantics)} - this reading is kept for the session.");
                }
                else if (NextSemantics())
                {
                    // Name the candidate that just FAILED, so it is still readable after
                    // NextSemantics() has moved on.
                    string failed = Name(SemanticsOrder[_semanticsIndex - 1]);
                    API.LogWarning($"[SF6Access] Auto walk: the {failed} reading did not move the avatar " +
                                   $"({_distWhenArmedM:F1} -> {flat:F1} m over {elapsed} ms, closed {closed:F1} m). " +
                                   $"Trying {Name(_semantics)} next ({_semanticsIndex + 1} of " +
                                   $"{SemanticsOrder.Length}).");
                    _armedAt = now;
                    _distWhenArmedM = flat;
                    _ordersSince = 0;
                    LogSemanticsOnce(targetX, targetY, targetZ, playerX, playerY, playerZ);
                }
                else
                {
                    // Not a dead end any more: fall through to the straight-line route
                    // rather than handing the walk back. The controller is still torn
                    // down — a controller whose sole order this code no longer uses must
                    // not stay registered at FieldEvent, because it would keep winning
                    // the player's own input slot while doing nothing.
                    API.LogWarning($"[SF6Access] Auto walk: CALIBRATION FAILED for every reading of " +
                                   $"SetChaseWalk ({string.Join(", ", AllNames())}) - the last closed {closed:F1} m " +
                                   $"over {elapsed} ms. Removing the controller and switching to the " +
                                   $"straight-line route (SetSlaveMoveVec) for this walk.");
                    RetireController(avatar);
                    _useStraightLine = true;
                    Speak(AutoWalkText.Unavailable());
                    return DriveStraightLine(avatar, legX, legY, legZ, aimX, aimY, aimZ,
                                             playerX, playerY, playerZ);
                }
            }
        }
        // ------------------------------------------------------------------------------

        // The controller is created here, on the only path that is certain to issue an
        // order this frame (see the note at the destination block). Everything above that
        // could abandon the walk has already returned, so an abandoned walk can no longer
        // leave a registered controller behind.
        var controller = EnsureController(avatar);
        if (controller == null) { StopAndCancel(avatar, Stop.Unavailable); return false; }

        // The game's own walk driver, 3-arg overload (vec3 vec, vec3 viewPos, bool mirror).
        if (!Order(controller, targetX, targetY, targetZ, playerX, playerY, playerZ))
        {
            RetireController(avatar);
            Stop_(Stop.Unavailable);
            return false;
        }

        _ordersSince++;
        _active = true;
        return true;
    }

    /// <summary>Advance to the next semantics candidate. False when the list is spent.</summary>
    private static bool NextSemantics()
    {
        if (_semanticsIndex + 1 >= SemanticsOrder.Length) return false;
        _semanticsIndex++;
        _semantics = SemanticsOrder[_semanticsIndex];
        _loggedThisTry = false;
        _windowOpen = false;
        return true;
    }

    // ---------- the straight-line route (fallback) ----------

    /// <summary><b>Walk to a point with no pathfinding at all.</b> The fallback used only
    /// after <c>SetChaseWalk</c> has measurably failed for every reading: it pushes a
    /// world-space direction into <c>AvatarPlayerControll.SetSlaveMoveVec</c>, the
    /// avatar's own movement component, which moves it in a straight line and stops for
    /// nothing.
    ///
    /// <para>Its weaknesses are exactly why it is second: no pathfinding, so it walks
    /// into walls and cannot take a ladder or open a door — the two things the owner
    /// asked for. It also has to be turned off explicitly (<c>SetSlaveMoveVec(0,0,0)</c>)
    /// when the walk ends, because it is a persistent movement state and not a per-frame
    /// order. But it needs no controller and no semantics: the argument is a direction
    /// in the same world space the player position is read in, which is the one
    /// coordinate space this mod has already proven it can read.</para>
    ///
    /// <para>Turns the avatar to face the direction first is NOT done: the component's
    /// own move vector is the only actuator here, and the direction alone is enough for
    /// it — a second write to the angle rate would be a guess about a third coordinate
    /// convention.</para></summary>
    private static bool DriveStraightLine(ManagedObject avatar, float legX, float legY, float legZ,
                                          float aimX, float aimY, float aimZ,
                                          float playerX, float playerY, float playerZ)
    {
        if (!_resolved) Resolve(avatar);
        if (_setSlaveMoveVec == null || _vec3Type == null) return false;

        float aimFlat = FlatDistance(playerX, playerZ, aimX, aimZ);
        bool useAim = aimFlat <= StopM + AimSlackM;
        float tx = useAim ? aimX : legX, ty = useAim ? aimY : legY, tz = useAim ? aimZ : legZ;
        float flat = FlatDistance(playerX, playerZ, tx, tz);
        if (flat <= StopM) { StopStraightLine(avatar); Stop_(Stop.Arrived); return false; }

        float dx = tx - playerX, dz = tz - playerZ;
        float len = (float)Math.Sqrt(dx * dx + dz * dz);
        if (len < 1e-4f) { StopStraightLine(avatar); Stop_(Stop.Arrived); return false; }
        // Unit horizontal direction. The vertical component is dropped on purpose: this
        // route cannot climb anything, and a downward component would push the avatar
        // into the floor while it is still supposed to be walking.
        float ux = dx / len, uz = dz / len;

        ManagedObject controll;
        try
        {
            controll = FieldProbeService.Member(avatar, "Components") is ManagedObject comps
                ? FieldProbeService.Member(comps, "Controll") as ManagedObject : null;
        }
        catch { controll = null; }
        if (controll == null) { Retire(avatar); Stop_(Stop.Unavailable); return false; }

        _straightVec ??= FieldOutBuffer.Acquire(_vec3Type);
        if (_straightVec == null) return false;
        if (!_straightVec.SetComponent("x", ux) || !_straightVec.SetComponent("y", 0f) ||
            !_straightVec.SetComponent("z", uz)) return false;

        try
        {
            _setSlaveMoveVec.InvokeBoxed(typeof(void), controll, new object[] { _straightVec.View });
            // The rate is the SPEED, and on 2026-09-29 12:47 the pair (unit vector, 1.0) was
            // accepted and produced 0.00 m/s for 21 s, so 1.0 is not "walk speed" in this
            // component's units. The unit is unverified, so the value is stepped through
            // candidates and the one that moves the avatar is kept — the same
            // measure-don't-guess rule as the chase-walk semantics, and for the same
            // reason: the alternative is another round trip to the player.
            float rate = StraightRate;
            _setSlaveMoveRate?.InvokeBoxed(typeof(void), controll, new object[] { rate });
            _straightWrote = true;
        }
        catch (Exception ex)
        {
            API.LogError($"[SF6Access] Auto-walk: SetSlaveMoveVec failed ({ex.GetType().Name}): {ex.Message}");
            RetireController(avatar);
            Stop_(Stop.Unavailable);
            return false;
        }
        _active = true;

        if (!_straightLogged)
        {
            _straightLogged = true;
            API.LogInfo($"[SF6Access] Auto walk: STRAIGHT-LINE route in use; dir=({ux:F2}, 0.00, {uz:F2}) " +
                        $"from=({playerX:F2}, {playerY:F2}, {playerZ:F2}) to=({tx:F2}, {ty:F2}, {tz:F2}) " +
                        $"dist={flat:F2} m rate={StraightRate:F2}");
        }

        // --- did it actually work? -----------------------------------------------------
        // The straight-line write can be ACCEPTED and have NO EFFECT, which is exactly what
        // the 2026-09-29 12:47 log shows (21 s of orders, avatar speed 0.00 m/s, and the
        // collision cue went quiet too — so nothing was moving). An actuator whose effect
        // is never measured produces the one state this mod must never be in: it thinks it
        // is driving, so it keeps rewriting the component and keeps holding the walk, while
        // the player sees nothing happen and cannot tell whether they still own the stick.
        // So the same rule as everywhere else in this file: measure the avatar's own world
        // position and judge on it.
        long nowMs = Environment.TickCount64;
        if (_ok) return true;                       // already proven this walk
        if (_okArmedAt == 0)
        {
            _okArmedAt = nowMs;
            _okDistWhenArmedM = flat;
            return true;                            // first order: nothing to judge yet
        }
        if (nowMs - _okArmedAt >= STRAIGHT_VERIFY_MS)
        {
            float closedM = _okDistWhenArmedM - flat;
            if (closedM > 0.5f)
            {
                _ok = true;
                API.LogInfo($"[SF6Access] Auto walk: straight-line route CONFIRMED - closed {closedM:F1} m " +
                            $"({_okDistWhenArmedM:F1} -> {flat:F1} m) over {nowMs - _okArmedAt} ms.");
            }
            else
            {
                // A rate that does nothing is not a reason to hand the walk back yet: the
                // unit is unverified, so the next rate candidate gets its own window. Only
                // when the list is spent is the route honestly declared dead.
                API.LogWarning($"[SF6Access] Auto walk: straight-line rate {StraightRate:F2} did not move the " +
                               $"avatar ({_okDistWhenArmedM:F1} -> {flat:F1} m over {nowMs - _okArmedAt} ms, " +
                               $"closed {closedM:F1} m).");
                if (_straightRateIndex + 1 < StraightRates.Length)
                {
                    _straightRateIndex++;
                    API.LogWarning($"[SF6Access] Auto walk: trying straight-line rate {StraightRate:F2} next " +
                                   $"({_straightRateIndex + 1} of {StraightRates.Length}).");
                    _okArmedAt = 0;
                    _okDistWhenArmedM = 0f;
                    return true;
                }
                // Give up honestly rather than keep pulling. The component is cleared, so
                // the avatar is not left with a direction pushed into it, and the walk ends
                // with the player told they have the controls back.
                //
                // The verdict has to be TERMINAL for this walk, not re-reached each frame:
                // the first version left _useStraightLine set and re-ran this branch on
                // every frame afterwards, which is where the 22 identical "no straight-line
                // rate moved the avatar" lines in the 2026-09-29 13:04 log came from. A
                // diagnosis repeated 22 times is not a diagnosis, it is noise that buries
                // the one line that mattered.
                if (!_straightDeadLogged)
                {
                    _straightDeadLogged = true;
                    API.LogWarning($"[SF6Access] Auto walk: no straight-line rate moved the avatar " +
                                   $"({string.Join(", ", StraightRates)} tried). SetSlaveMoveVec accepted the " +
                                   $"writes and had no effect, so auto-walk is not available here. Stopping and " +
                                   $"handing the controls back.");
                }
                _useStraightLine = false;
                StopStraightLine(avatar);
                Stop_(Stop.Unavailable);
                Speak(AutoWalkText.Unavailable());
                return false;
            }
        }
        return true;
    }

    /// <summary>Cancel the straight-line movement. Required, not cosmetic: the component
    /// holds the direction as STATE, so simply stopping to issue it would leave the
    /// avatar walking forever.</summary>
    private static void StopStraightLine(ManagedObject avatar)
    {
        // The guard is the MEMORY of having written a direction, not the current-route flag:
        // BeginWalk() clears _useStraightLine when the player picks a new target, and a stop
        // arriving after that must still clear a direction that is still sitting in the
        // component. Clearing is a no-op when there is nothing to clear, so it is always
        // safe to do and never safe to skip.
        if (_setSlaveMoveVec == null || !_straightWrote) return;
        try
        {
            var controll = FieldProbeService.Member(avatar, "Components") is ManagedObject comps
                ? FieldProbeService.Member(comps, "Controll") as ManagedObject : null;
            if (controll != null && _straightVec != null)
            {
                if (_straightVec.SetComponent("x", 0f) && _straightVec.SetComponent("y", 0f) &&
                    _straightVec.SetComponent("z", 0f))
                {
                    _setSlaveMoveVec.InvokeBoxed(typeof(void), controll, new object[] { _straightVec.View });
                    _setSlaveMoveRate?.InvokeBoxed(typeof(void), controll, new object[] { 0f });
                    _straightWrote = false;
                }
            }
        }
        catch { }
    }

    private static string Name(Semantics s) => s switch
    {
        Semantics.AbsolutePoint => "ABSOLUTE point",
        Semantics.RelativeVector => "RELATIVE vector",
        Semantics.PointThenFace => "POINT then face",
        _ => "?",
    };

    private static IEnumerable<string> AllNames()
    {
        foreach (var s in SemanticsOrder) yield return Name(s);
    }

    /// <summary><b>Give the controller back.</b> Destroying it here and not in
    /// <see cref="Reset"/> is the point: the game's controller registry is keyed only by
    /// priority, so "create at FieldEvent, destroy at FieldEvent" must be a matched pair
    /// within the walk. A controller created and then abandoned is the state that leaves
    /// the player unable to move — the game keeps awarding the input slot to a controller
    /// nobody is driving.</summary>
    private static void ReleaseController(ManagedObject avatar)
    {
        if (_controller == null) return;
        RetireController(avatar);
    }

    /// <summary>Stop driving and release the controller. Called on arrival, takeover,
    /// or when the avatar/target goes away.</summary>
    internal static void Release()
    {
        if (_controller != null)
        {
            try { _removeChaseWalk?.InvokeBoxed(typeof(void), _controller, null); } catch { }
        }
        _active = false;
    }

    /// <summary>Tear the controller down for good. Called when a walk order faulted or
    /// the calibration says the order means nothing: a controller that keeps the
    /// player's input slot while doing nothing is the 2026-09-29 symptom ("only the
    /// camera turns"), so it is destroyed through the game's own
    /// <c>AvatarBase.DestroyInputController(PriorityType)</c> rather than merely
    /// dropped. Best-effort — the destroy itself is allowed to be unavailable.</summary>
    private static void Retire(ManagedObject avatar)
    {
        RetireController(avatar);
        _retired = true;
    }

    /// <summary><b>Destroy the controller, but do NOT conclude the session is over.</b>
    /// The distinction is the fix for the 2026-09-29 12:47 bug, and it is worth stating
    /// because the two were the same call before: <see cref="_retired"/> is a verdict on
    /// the ENGINE ("this VM is broken, never touch it again this session"), while failing
    /// to drive the avatar to ONE target is a verdict on that target. Rolling the two into
    /// one meant the straight-line hand-off — which retires the chase controller on
    /// purpose, because that controller is the thing that failed — also poisoned the
    /// session, so the NEXT target could not walk either.</summary>
    private static void RetireController(ManagedObject avatar)
    {
        Release();
        try
        {
            if (_destroyController != null && avatar != null && _priorityFieldEvent >= 0)
                _destroyController.InvokeBoxed(typeof(void), avatar, new object[] { _priorityFieldEvent });
        }
        catch { }
        _controller = null;
    }

    /// <summary><b>A NEW walk was requested — the player pressed A again.</b> This clears
    /// every session-scoped verdict, and it is the one entry point allowed to, because a
    /// new press is a new question asked with the player's own hands: a fresh target, and
    /// usually a fresh situation (the first order may have gone out before the city had
    /// finished loading in, which is not information about the method's semantics). The
    /// verdicts that survive are only the ones that are about the ENGINE and not about the
    /// call: nothing here. Called from the beacon when its target actually changes.
    ///
    /// <para>Note this is NOT the same as <see cref="Reset"/> (which fires on a field
    /// change / mod reload): this keeps the walk alive and only discards the verdicts.</para></summary>
    internal static void BeginWalk()
    {
        _retired = false;
        _calibrated = false;
        _semanticsIndex = 0;
        _semantics = SemanticsOrder[0];
        _loggedThisTry = false;
        _windowOpen = false;
        _armedAt = 0;
        _ordersSince = 0;
        _distWhenArmedM = 0f;
        _useStraightLine = false;
        _straightLogged = false;
        _ok = false;
        _okArmedAt = 0;
        _okDistWhenArmedM = 0f;
        _straightRateIndex = 0;
        _straightDeadLogged = false;
        LastStop = Stop.None;
    }

    internal static void Reset()
    {
        Release();
        _controller = null;
        _resolved = false;
        _loggedUnavailable = false;
        _retired = false;
        _loggedThisTry = false;
        LastStop = Stop.None;
        _calibrated = false;
        _ordersSince = 0;
        _armedAt = 0;
        _useStraightLine = false;
        _straightLogged = false;
        _distWhenArmedM = 0f;
        _windowOpen = false;
        _ok = false;
        _okArmedAt = 0;
        _okDistWhenArmedM = 0f;
        _straightRateIndex = 0;
        _straightDeadLogged = false;
        _straightWrote = false;
    }

    // ---------- the controller ----------

    private static void Stop_(Stop reason)
    {
        if (_active) Release();
        LastStop = reason;
    }

    /// <summary><see cref="Stop_"/> plus the movement cancellation the straight-line route
    /// needs. Used on every stop path that runs while the avatar still exists, because the
    /// straight-line direction is persistent STATE on the movement component: releasing the
    /// controller alone would leave the avatar walking away forever.</summary>
    private static void StopAndCancel(ManagedObject avatar, Stop reason)
    {
        StopStraightLine(avatar);
        Stop_(reason);
        // Every stop path also gives the controller back. It is safe to do unconditionally
        // — the destroy is a no-op when there is none — and it is the only way to be sure
        // the registry does not keep an entry for a walk that is over. The 2026-09-29 12:47
        // loss of control was exactly this: a walk stopped on one path and the controller
        // stayed registered on another.
        ReleaseController(avatar);
    }

    private static ManagedObject EnsureController(ManagedObject avatar)
    {
        if (_controller != null)
        {
            // The wrapper must be re-validated: a city load destroys it, and calling
            // through a dead wrapper is the documented crash.
            try { if (FlowHelper.Call(_controller, "get_Destroyed") is bool dead && dead) { _controller = null; } }
            catch { _controller = null; }
            if (_controller != null) return _controller;
        }
        if (!_resolved) Resolve(avatar);
        if (_createController == null || _priorityFieldEvent < 0) return null;

        try
        {
            _createArgs[0] = _priorityFieldEvent;
            _controller = _createController.InvokeBoxed(typeof(object), avatar, _createArgs) as ManagedObject;
            if (_controller != null)
            {
                API.LogInfo("[SF6Access] Auto-walk: input controller created at FieldEvent priority");
                // A request controller that is not flagged to survive its request would
                // be destroyed the moment the walk order completes — we want it to stay
                // until we release it, so both flags are false.
                try { _controller.Call("set_DestroyOnRequest", false); } catch { }
                try { _controller.Call("set_DestroyOnRequestSuccess", false); } catch { }
            }
        }
        catch (Exception ex)
        {
            API.LogError($"[SF6Access] Auto-walk: CreateInputController failed: {ex.Message}");
        }
        return _controller;
    }

    private static void Resolve(ManagedObject avatarForResolve)
    {
        _resolved = true;
        var baseTd = TDB.Get().FindType(BASE_TYPE);
        var controllerTd = TDB.Get().FindType(CONTROLLER_TYPE);
        var managerTd = TDB.Get().FindType(MANAGER_TYPE);
        _createController = FieldProbeService.FindByShape(baseTd, "CreateInputController", 1, "PriorityType");
        _destroyController = FieldProbeService.FindByShape(baseTd, "DestroyInputController", 1, "PriorityType");
        // The vec3/viewPos overload is picked by SHAPE — both overloads have three
        // parameters and a trailing bool, so asking for the count alone would as often
        // as not bind the GameObject one, whose argument list is completely different.
        // The probe's dump names the two exactly:
        //   SetChaseWalk(via.vec3 vec, via.GameObject viewObj, System.Boolean mirror)
        //   SetChaseWalk(via.vec3 vec, via.vec3 viewPos,      System.Boolean mirror)
        _setChaseWalk = FindChaseWalk(controllerTd);
        _removeChaseWalk = FieldProbeService.FindByShape(controllerTd, "RemoveChaseWalk", 0, "");
        if (_removeChaseWalk == null) _removeChaseWalk = FindByName(controllerTd, "RemoveChaseWalk");
        _priorityFieldEvent = FieldProbeService.EnumValue(PRIORITY_ENUM, "FieldEvent");
        _vec3Type = TDB.Get().FindType("via.vec3");
        _createArgs = new object[] { _priorityFieldEvent };

        // The straight-line fallback, resolved up front so it is available the moment a
        // walk needs it (resolving it lazily inside the frame that needs it would put a
        // TDB walk on the frame budget exactly when the walk has just failed). It is NOT
        // used unless SetChaseWalk has measurably failed: SetSlaveMoveVec has no
        // pathfinding at all, so it walks into walls — SetChaseWalk is the route that can
        // actually open a door or take a ladder.
        //   AvatarPlayerControll.SetSlaveMoveVec(via.vec3), SetSlaveMoveRate(System.Single)
        // (probe 2026-09-29 10:49, Components.Controll = app.worldtour.avatar.AvatarPlayerControll,
        //  IsDontUseAIMap = False, both found).
        try
        {
            var controll = FieldProbeService.Member(avatarForResolve, "Components") is ManagedObject comps
                ? FieldProbeService.Member(comps, "Controll") as ManagedObject : null;
            var ctd = controll?.GetTypeDefinition();
            if (ctd != null)
            {
                _setSlaveMoveVec = FieldProbeService.FindByShape(ctd, "SetSlaveMoveVec", 1, "vec3");
                _setSlaveMoveRate = FieldProbeService.FindByShape(ctd, "SetSlaveMoveRate", 1, "Single");
            }
        }
        catch { }

        bool ok = _createController != null && _setChaseWalk != null && _priorityFieldEvent >= 0 && _vec3Type != null;
        if (!ok && !_loggedUnavailable)
        {
            _loggedUnavailable = true;
            API.LogWarning($"[SF6Access] Auto-walk unavailable: CreateInputController=" +
                           $"{(_createController == null ? "no" : "yes")}, SetChaseWalk(vec3,vec3,bool)=" +
                           $"{(_setChaseWalk == null ? "no" : "yes")}, FieldEvent={_priorityFieldEvent}, " +
                           $"fallback SetSlaveMoveVec={(_setSlaveMoveVec == null ? "no" : "yes")}");
        }
        else if (ok)
        {
            API.LogInfo("[SF6Access] Auto-walk bound: CreateInputController + SetChaseWalk(vec3, vec3, bool) at " +
                        $"FieldEvent; DestroyInputController={(_destroyController == null ? "no" : "yes")}, " +
                        $"RemoveChaseWalk={(_removeChaseWalk == null ? "no" : "yes")}, " +
                        $"fallback SetSlaveMoveVec={(_setSlaveMoveVec == null ? "no" : "yes")}");
        }
    }

    /// <summary>The <c>SetChaseWalk(vec3 vec, vec3 viewPos, bool mirror)</c> overload,
    /// matched on the SECOND parameter being a value type (the twin takes a
    /// <c>GameObject</c> there — see <see cref="Order"/> for what was tried against
    /// each). Walks the hierarchy, since the concrete controller may only inherit it.</summary>
    private static Method FindChaseWalk(TypeDefinition td)
    {
        for (var t = td; t != null; t = t.ParentType)
        {
            List<Method> methods;
            try { methods = t.GetMethods(); } catch { continue; }
            if (methods == null) continue;
            foreach (var m in methods)
            {
                if (m.Name != "SetChaseWalk") continue;
                try
                {
                    var ps = m.GetParameters();
                    if (ps != null && ps.Count == 3 && ps[1].Type?.IsValueType() == true) return m;
                    if (ps != null && ps.Count == 3) API.LogInfo(
                        $"[SF6Access] Auto-walk: skipping SetChaseWalk with parameter 2 = {ps[1].Type?.FullName} " +
                        "(the vec3/viewPos overload is the one this mod drives)");
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>An overload by name alone, for the methods that publish exactly one.</summary>
    private static Method FindByName(TypeDefinition td, string name)
    {
        for (var t = td; t != null; t = t.ParentType)
        {
            List<Method> methods;
            try { methods = t.GetMethods(); } catch { continue; }
            if (methods == null) continue;
            foreach (var m in methods) if (m.Name == name) return m;
        }
        return null;
    }

    // ---------- the order ----------

    /// <summary>Issue one walk order through the game's own driver.
    ///
    /// <summary>Issue one walk order through the game's own driver, laid out according to
    /// the reading currently under test (<see cref="_semantics"/>).
    ///
    /// <para><b>WHY THE LAYOUT IS A CANDIDATE LIST RATHER THAN A CONSTANT.</b>
    /// <c>SetChaseWalk(vec3, vec3, bool)</c> is not documented anywhere this mod can
    /// read, and the TDB gives types, not semantics. On 2026-09-29 two guesses were
    /// tried and neither moved the avatar. The next honest step is not a third guess in
    /// source — it is to try each reading IN GAME and keep the one that measurably
    /// works, which is what <see cref="Drive"/>'s calibration does with the candidates in
    /// <see cref="SemanticsOrder"/>. This method only has to lay the arguments out
    /// correctly for whichever one is on trial.</para>
    ///
    /// <para><b>The buffers are CACHED and never disposed per call.</b> This matches
    /// <see cref="FieldNavPath"/> and <see cref="FieldRayCaster"/>, which have always
    /// passed their vec3 arguments from two buffers held for the life of the process.
    /// Freeing unmanaged memory the engine may still hold a reference to is a crash
    /// surface with nothing to gain.</para></summary>
    private static bool Order(ManagedObject controller, float tx, float ty, float tz,
                              float px, float py, float pz)
    {
        if (_setChaseWalk == null) return false;
        _target ??= FieldOutBuffer.Acquire(_vec3Type);
        _view ??= FieldOutBuffer.Acquire(_vec3Type);
        if (_target == null || _view == null) return false;

        float vx, vy, vz, wx, wy, wz;
        switch (_semantics)
        {
            case Semantics.RelativeVector:
                // "go this far in this direction, from here"
                vx = tx - px; vy = ty - py; vz = tz - pz;
                wx = px; wy = py; wz = pz;
                break;
            case Semantics.PointThenFace:
                // "go to this world point, and arrive facing this way". The facing
                // direction is the horizontal heading from the player to the point, which
                // is the only heading this code can justify: it is the one a walker
                // arrives with. Height is dropped because a facing direction is planar.
                vx = tx; vy = ty; vz = tz;
                wx = tx - px; wy = 0f; wz = tz - pz;
                break;
            default:
                // AbsolutePoint: "go to this world point"; the viewpoint the order is
                // measured from is the player's own position, which is the only
                // observation point this mod legitimately has.
                vx = tx; vy = ty; vz = tz;
                wx = px; wy = py; wz = pz;
                break;
        }

        if (!_target.SetComponent("x", vx) || !_target.SetComponent("y", vy) || !_target.SetComponent("z", vz)) return false;
        if (!_view.SetComponent("x", wx) || !_view.SetComponent("y", wy) || !_view.SetComponent("z", wz)) return false;

        try
        {
            _setChaseWalk.InvokeBoxed(typeof(void), controller, new object[] { _target.View, _view.View, false });
            return true;
        }
        catch (Exception ex)
        {
            API.LogError($"[SF6Access] Auto-walk: SetChaseWalk failed ({ex.GetType().Name}): {ex.Message}");
            return false;
        }
    }

    /// <summary><b>Print the exact numbers a candidate is about to be judged on, once per
    /// candidate.</b> The 2026-09-29 log could say "did not move" but not what had been
    /// asked for, which left the reading genuinely undecidable from outside; these numbers
    /// are what makes the next log decisive. They also expose the one case the distance
    /// test cannot see: a reading that DOES move the avatar but sends it somewhere useless
    /// while the distance happens to stay flat.</summary>
    private static void LogSemanticsOnce(float tx, float ty, float tz, float px, float py, float pz)
    {
        if (_loggedThisTry) return;
        _loggedThisTry = true;
        API.LogInfo($"[SF6Access] Auto walk: trying semantics {Name(_semantics)} " +
                    $"({_semanticsIndex + 1}/{SemanticsOrder.Length}); destination=({tx:F2}, {ty:F2}, {tz:F2}) " +
                    $"player=({px:F2}, {py:F2}, {pz:F2}) dist={FlatDistance(px, pz, tx, tz):F2} m");
    }

    private static float FlatDistance(float ax, float az, float bx, float bz)
    {
        float dx = bx - ax, dz = bz - az;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static void Speak(string text)
    {
        if (!string.IsNullOrEmpty(text)) ScreenReaderService.Speak(text, interrupt: false);
    }
}
