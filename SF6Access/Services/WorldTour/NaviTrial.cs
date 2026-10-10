using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>One-shot trial of the game's OWN pathfinder, on its own key, touching nothing else.</b>
///
/// <para><b>Why it is separate from <see cref="AutoWalk"/>.</b> Five in-game tests established
/// that every vector actuator this mod could find (<c>SetChaseWalk</c>, <c>SetSlaveMoveVec</c>)
/// is accepted and then ignored, while the player's own stick moves the avatar at 4.37 m/s. The
/// probe then found the reason: those methods are not the game's entry point for moving an
/// avatar, they are helpers that write into a <c>NaviRequestData</c> — and no such request was
/// ever running, so the fields were written into something nobody read. The real entry point is
/// <c>AvatarBase.StartNavi(PriorityType, NaviRequestData)</c>, and it is strictly better for
/// this feature because it is PATHFINDING: it can round a corner and take a ladder, which is
/// exactly what the owner asked for and what a raw vector can never do.
///
/// <para><b>Why a trial and not the feature.</b> Filling that request is the first thing in this
/// feature that has never been done in this codebase, so its failure modes are unknown: it may
/// do nothing, walk somewhere unexpected, or fault. Running it on its own key means a wrong
/// guess costs <i>that key</i> and nothing else — the player's working takeover, their radar,
/// and their auto-walk selection are all untouched.</para>
///
/// <para><b>How the request is built — and what it deliberately does NOT do.</b> The obvious
/// reading of probe block 9 was "use <c>TypeDefinition.CreateValueType()</c>", since it is
/// confirmed present and <c>NaviRequestData</c> looks like a value type. <b>That would have been
/// the crash this codebase already paid for.</b> <see cref="FieldOutBuffer"/> exists precisely to
/// replace it, and its own remarks say why: <c>CreateValueType</c> allocates a managed
/// <c>byte[]</c> on the GC heap and <c>ValueType.Ptr()</c> hands back an address taken inside a
/// <c>fixed</c> block that has already been released — so a collection during the native call
/// relocates the array underneath the engine, which goes on writing into memory that is no
/// longer ours. It is also exactly <c>ValueTypeSize</c> bytes with no slack, so a whole-register
/// store of a <c>via.vec3</c> runs off the end. Every symptom of that crash is "correct data
/// returned, game dead seconds later, nothing in the log".</para>
///
/// <para>So the request goes in a <see cref="FieldOutBuffer"/>: unmanaged, never moves, rounded
/// up and aligned to the engine's own vector width, zeroed before use. The engine reads and
/// writes it by the game's own field metadata. The nested route value does the same thing one
/// level down — its own buffer, and only its ADDRESS is copied into the request, so nothing here
/// is memory the GC could move.</para>
///
/// <para><b>What is written, and why so little.</b> <c>TargetType=Position</c> (so
/// <c>RouteObject</c> is documented as unread), <c>MoveType=Walk</c>,
/// <c>FindingActionType=GoFront</c>, <c>LoopType=None</c>, <c>EndAction=None</c>, and a route
/// holding exactly one point. Everything else stays zero, because the request struct is the
/// game's own contract and inventing values for fields nothing has asked about is how a guess
/// becomes a wrong walk. <b>One change at a time</b>: if this does not move the avatar, the next
/// trial adds exactly one field, so the log says which one mattered.</para>
///
/// <para>All offsets and enum members come from probe blocks 8 and 9 (in game, 2026-09-29 13:15
/// and 13:30), never from memory.</para>
/// </summary>
internal static class NaviTrial
{
    private const string BASE_TYPE = "app.worldtour.avatar.AvatarBase";
    private const string PRIORITY_ENUM = "app.worldtour.avatar.AvatarInputController.PriorityType";
    private const string NAVI_TYPE = "app.worldtour.avatar.AvatarControllBase.NaviRequestData";
    private const string VEC3_TYPE = "via.vec3";

    // Enum members, by the names the TDB publishes (read via FieldProbeService.EnumValue, so
    // nothing below hardcodes a number the dump could contradict).
    private const string TARGET_ENUM = NAVI_TYPE + ".RouteTargetType";
    private const string FINDING_ENUM = NAVI_TYPE + ".PathFindingActionType";
    private const string LOOP_ENUM = NAVI_TYPE + ".RouteLoopType";
    private const string MOVE_ENUM = NAVI_TYPE + ".MoveActionType";
    private const string END_ENUM = NAVI_TYPE + ".EndActionType";

    // NOTE — the constant "via.vec3[]" and the offset 0x10 that used to live here are GONE, and
    // must not come back. The string lookup for "via.vec3[]" resolved to the jagged type
    // via.vec3[][] on this engine, so the array it created had an OBJECT POINTER as its element
    // (16 bytes, not 12), and writing three floats at object+0x10 overwrote a live managed
    // object's header and crashed the game (2026-09-29 16:04:59). The route type now comes from
    // the request's own RoutePos field metadata and the elements are written at the native data
    // pointer the array itself publishes — see WriteArrayElements.

    private static bool _resolved, _loggedUnavailable;
    private static Method _startNavi, _forceEndNavi;
    private static TypeDefinition _naviTd, _vec3Td;
    /// <summary><b>The priority the request is made at.</b> A low-to-high ladder
    /// (<c>_Invalid 0 &lt; Default 1 &lt; __DebugDefault 2 &lt; FieldEvent 3 &lt; MenuUI 4 &lt;
    /// Flow 5 &lt; BattleIn 6 &lt; __DebugForce 7</c>), currently <c>MenuUI</c> — one rung ABOVE
    /// the <c>FieldEvent</c> the field itself runs at, because a request at or below the current
    /// priority is not a handover and an input manager is entitled to ignore it.</summary>
    private static int _naviPriority = -1;

    /// <summary>Is a trial walk currently believed to be running?</summary>
    internal static bool Active { get; private set; }

    /// <summary>The request, its route array and the single element, held for the walk.
    ///
    /// <para><b>Why they are held at all.</b> <c>StartNavi</c> hands the request to the engine,
    /// and <c>RoutePos</c> inside it is an array the engine reads while walking — so it must
    /// outlive the call. These are UNMANAGED (<see cref="FieldOutBuffer"/> or raw
    /// <c>AllocHGlobal</c>), which is exactly what makes holding them safe: they never move, so
    /// the engine keeps reading valid bytes for as long as the walk runs.</para></summary>
    /// <summary>The live request object. Created once and globalized; the same instance is
    /// refilled for every trial, which is both cheaper and safer than making a new engine object
    /// per press (each one is a local object the engine reclaims — see
    /// <see cref="FieldProbeService.SharedInstance"/>).</summary>
    private static ManagedObject _request;
    private static ManagedObject _sharedRequest;

    /// <summary>The route array object handed to the current request — one of the per-length
    /// arrays in <see cref="_routeArrays"/>, chosen by the route's point count.</summary>
    private static ManagedObject _routeObject;

    /// <summary>The globalized route arrays, one per element count used so far, kept for the
    /// process for the same reason as <see cref="_sharedRequest"/>: the engine reads the array
    /// while walking, so it must outlive the call, and releasing a rooted object is the
    /// dangerous direction. The route's point count varies with the city block and a managed
    /// array's length is fixed at creation, hence one per length.</summary>
    private static readonly Dictionary<int, ManagedObject> _routeArrays = new Dictionary<int, ManagedObject>();

    /// <summary>The route array's declared type (from the request's own <c>RoutePos</c> field
    /// metadata — never a string, the twenty-second finding) and its element stride
    /// (<c>via.vec3</c>'s <c>ValueTypeSize</c>, 16). Zero until <c>BindRouteArrayType</c> has
    /// run; a failed bind leaves them zero so every press retries the bind rather than caching
    /// a refusal.</summary>
    private static TypeDefinition _routeArrayTd;
    private static int _routeElemStride;

    /// <summary>The route array object's own address, for the log — the address the request's
    /// <c>RoutePos</c> field is given.</summary>
    private static ulong _routeBlockAddr;

    /// <summary>Stops the trial walk (the player took over, or the walk ended). Safe to call
    /// when nothing is running.</summary>
    internal static void End(ManagedObject avatar, string reason)
    {
        // Release the avatar BEFORE anything else, and unconditionally. This flag is what tells
        // AutoWalk to stand down; if it survived the end of the walk, the mod would refuse to
        // drive the avatar at all afterwards and the player would be left with no auto-walk and
        // no explanation. See AutoWalk.Drive's eighteenth-finding remarks for why the flag exists.
        AutoWalk.EngineIsDriving = false;
        if (!Active && _request == null) return;
        Active = false;
        try
        {
            if (_forceEndNavi != null && avatar != null)
                _forceEndNavi.InvokeBoxed(typeof(void), avatar, null);
        }
        catch (Exception ex) { API.LogWarning($"[SF6Access] Navi trial ForceEndNavi failed: {ex.Message}"); }
        ReleaseBuffers();
        API.LogInfo($"[SF6Access] Navi trial ended ({reason}).");
    }

    /// <summary>
    /// <b>Has the player taken the controller back while a trial walk is running?</b>
    ///
    /// <para><b>Why this and not the avatar's speed.</b> The speed test that guards the rest of
    /// the mod cannot work here: a walk driven by <c>StartNavi</c> moves the avatar at 2–4 m/s,
    /// which is the same reading as a player pushing the stick, so the first working walk
    /// cancelled itself 54 ms after it began. Speed is a consequence; this asks the cause.
    /// While the engine is walking for us the input slot carries the priority WE asked for
    /// (<c>MenuUI</c>); the game hands the slot back to the player's own <c>Default</c>
    /// controller the moment the player claims it, which is the same mechanism that makes a
    /// leftover controller able to hold the input slot hostage — documented in
    /// <see cref="AutoWalk"/>'s remarks and confirmed from both sides here.</para>
    ///
    /// <para><b>This is a by-value read.</b> <c>GetCurrentInputControllerPriority()</c> returns
    /// an enum, and the codebase has called it safely since probe block 4. The rule
    /// <see cref="AutoWalk"/>'s remarks set down — never reintroduce an engine call that is
    /// not a plain by-value read or a by-value actuator call — is kept: this is the former.</para>
    ///
    /// <para><b>If the priority cannot be read, this says no.</b> Failing towards "the player
    /// still has control" would cancel every walk the instant the reading faltered; failing
    /// towards "we still have it" leaves the player one keypress from stopping the walk
    /// themselves. The second is the safer side of this particular trade, and the log records
    /// the choice once so it is not silent.</para>
    /// </summary>
    internal static bool PlayerHasTheController(ManagedObject avatarManager)
    {
        try
        {
            var avatar = PlayerAvatar();
            if (avatar == null) return false;

            object pri = FlowHelper.Call(avatar, "GetCurrentInputControllerPriority");
            if (pri == null) return false;
            int now = Convert.ToInt32(pri);

            // Below the priority we asked for means somebody with a better claim holds the
            // slot — the player's Default, or a menu. At or above it, it is still ours.
            bool playerHasIt = now < _naviPriority;
            if (playerHasIt)
                API.LogInfo($"[SF6Access] Navi trial: the input slot dropped to priority {now} " +
                            $"(we asked for {_naviPriority}) — the player has taken the controller");
            return playerHasIt;
        }
        catch (Exception ex)
        {
            if (!_loggedPriorityUnavailable)
            {
                _loggedPriorityUnavailable = true;
                API.LogInfo($"[SF6Access] Navi trial: GetCurrentInputControllerPriority is not readable " +
                            $"({ex.GetType().Name}) — takeover will rely on the walk key instead");
            }
            return false;
        }
    }

    private static bool _loggedPriorityUnavailable;

    private static ManagedObject PlayerAvatar()
    {
        try { return FieldRayCaster.PlayerAvatar(); } catch { return null; }
    }

    /// <summary>The player moved the stick: hand the avatar straight back and say so once.</summary>
    internal static void TakeOver(ManagedObject avatar)
    {
        if (!Active) return;
        End(avatar, "player took over");
        ScreenReaderService.Speak(AutoWalkText.PlayerTookOver(), interrupt: false);
    }

    /// <summary>
    /// <b>Pause navigation because the world is in the way — without throwing away where we
    /// were going.</b>
    ///
    /// <para><b>Why this is not <see cref="End"/>.</b> <c>End</c> calls <c>ForceEndNavi</c>,
    /// which tells the engine to forget the navigation entirely. That is right when the player
    /// takes over or when the walk is finished, and wrong here: the whole point of an obstacle
    /// hold is that the same journey continues afterwards, so the engine must keep the goal it
    /// was given. This stops the avatar moving while leaving the request in place.</para>
    ///
    /// <para><b>What actually stops the avatar.</b> Clearing <c>Active</c> is enough for the
    /// mod's own bookkeeping, but the mod is not what moves the avatar here — the engine is.
    /// The honest statement of what this does is therefore: it stops <i>asking</i>, and the
    /// caller stops re-issuing. Whether the engine walks on regardless is exactly what the
    /// trial is measuring, and the log says so rather than pretending the outcome is known.</para>
    /// </summary>
    internal static void SuspendForObstacle()
    {
        if (!Active) return;
        Active = false;
        API.LogInfo("[SF6Access] Navi trial: suspended — the world is in the way and something " +
                    "has to be broken before this journey can continue");
    }

    /// <summary>
    /// <b>Ask for the same destination again once the obstacle is gone.</b> A full fresh
    /// <see cref="TryWalk"/>, deliberately: the engine's own pathfinder is the thing that can
    /// route around whatever is left, so re-issuing a whole request is both simpler and more
    /// likely to succeed than trying to unpick a half-finished one.
    /// </summary>
    internal static void ResumeAfterObstacle(ManagedObject avatar, float x, float y, float z)
    {
        API.LogInfo($"[SF6Access] Navi trial: resuming after the obstacle — re-issuing StartNavi " +
                    $"for ({x:F2}, {y:F2}, {z:F2})");
        // No cached verdict here, and deliberately so: the obstacle has just been destroyed, which
        // is precisely a change in what is walkable. The whole point of resuming is that the old
        // answer no longer applies, so this is the one path that must ask the engine afresh — and
        // being called from a key- or event-driven moment rather than a frame, it can afford the
        // rate-limit wait.
        var fresh = BeaconDestination.Resolve();
        TryWalk(avatar, x, y, z, fresh);
    }

    /// <summary>Field change / mod reload: drop everything, including any live request.</summary>
    internal static void Reset()
    {
        Active = false;
        ReleaseBuffers();
        _resolved = false;
        _loggedUnavailable = false;
        _startNavi = _forceEndNavi = null;
        _naviTd = _vec3Td = null;
        _routeArrayTd = null;
        _routeElemStride = 0;
        // The arrays themselves stay globalized (releasing a rooted object is the dangerous
        // direction), but they belong to the old field's type metadata — forget them so the
        // next press binds and builds fresh ones.
        _routeArrays.Clear();
        _naviPriority = -1;
    }

    /// <summary>Drops the per-frame references. The engine objects
    /// (<see cref="_sharedRequest"/> / the per-length arrays in <see cref="_routeArrays"/>) are
    /// deliberately NOT released: they are process-lifetime by design — globalized — and
    /// releasing a rooted object the engine may still be reading is what would be dangerous,
    /// not holding it.</summary>
    private static void ReleaseBuffers()
    {
        _request = null;
        _routeObject = null;
        _routeBlockAddr = 0;
    }

    /// <summary><b>Log the route the engine is about to be handed, bend by bend.</b> Decides
    /// nothing — the gate above already decided from the beacon's answer. This log is the
    /// evidence for WHAT was handed over: a multi-point route means the engine can walk round
    /// a wall; a single point means it can only walk AT the destination and press into
    /// whatever stands between (the twenty-sixth finding).</summary>
    private static void LogRouteBends((float X, float Y, float Z)[] route,
                                      (float x, float y, float z, bool ok) before)
    {
        float px = before.ok ? before.x : 0f;
        float py = before.ok ? before.y : 0f;
        float pz = before.ok ? before.z : 0f;
        var forward = FieldDirectionService.GetCameraForward();
        var (ex, ey, ez) = route[route.Length - 1];
        API.LogInfo($"[SF6Access] Navi trial: route — {route.Length} point(s), Start=({px:F2}, {py:F2}, {pz:F2}) " +
                    $"Destination=({ex:F2}, {ey:F2}, {ez:F2})");

        for (int i = 0; i < route.Length; i++)
        {
            var (cx, cy, cz) = route[i];
            float dx = cx - px, dz = cz - pz;
            float flat = MathF.Sqrt(dx * dx + dz * dz);
            int hour = FieldDirectionService.ClockHour(forward, dx, dz);

            // Bearing change against the previous bend is what says "this is a turn".
            string turn = "";
            if (i > 0)
            {
                var (ox, _, oz) = route[i - 1];
                double b1 = Math.Atan2(ox - px, oz - pz) * 180.0 / Math.PI;
                double b2 = Math.Atan2(cx - px, cz - pz) * 180.0 / Math.PI;
                double d = Math.IEEERemainder(b2 - b1, 360.0);
                turn = $" turn={d:+0;-0;0}deg";
            }

            API.LogInfo($"[SF6Access] Navi trial: route — bend {i + 1}/{route.Length}: " +
                        $"({cx:F2}, {cy:F2}, {cz:F2}) from player {flat:F2} m flat, {hour} o'clock{turn}");
        }
    }

    /// <summary><b>The recalled bend list as the route, plus the gate's walk point appended
    /// ONLY when no bend already stands near it.</b> The bends are the engine's own
    /// string-pulled line to the leg end, so for the common legs the walk point IS one of
    /// them (the first, via the beacon's aim; the last, for a direct leg) and nothing is
    /// appended. The append exists for the exotic case of the gate approving a point that
    /// is on no bend — the walk then ends there, as the gate intended. Capped at 64 points,
    /// the bound <see cref="FieldNavPath"/>'s own readers use.</summary>
    private static (float X, float Y, float Z)[] AssembleRoute(
        (float X, float Y, float Z)[] bends, float sx, float sy, float sz)
    {
        const int MaxRoutePoints = 64;
        bool walkPointOnRoute = false;
        foreach (var b in bends)
        {
            float dx = b.X - sx, dy = b.Y - sy, dz = b.Z - sz;
            if (dx * dx + dy * dy + dz * dz <= 1.5f * 1.5f) { walkPointOnRoute = true; break; }
        }
        int n = Math.Min(bends.Length, MaxRoutePoints);
        int total = walkPointOnRoute || n >= MaxRoutePoints ? n : n + 1;
        var route = new (float X, float Y, float Z)[total];
        for (int i = 0; i < n; i++) route[i] = bends[i];
        if (total > n)
        {
            route[n] = (sx, sy, sz);
            API.LogInfo($"[SF6Access] Navi trial: the gate's walk point ({sx:F2}, {sy:F2}, {sz:F2}) " +
                        "is on no bend of the recalled route — appended as the final element");
        }
        return route;
    }

    /// <summary><b>Build and send the walk request — but only if the destination can be walked to.</b>
    ///
    /// <para><paramref name="dest"/> is the ALREADY-RESOLVED destination, and it is what the
    /// reachability gate reads. That is not a convenience: <c>BeaconDestination.Resolve</c> ran an
    /// engine path query moments ago for this very target, and <see cref="FieldNavPath"/> allows
    /// one query per 250 ms because <c>queryPathSync</c> is a blocking city-wide A*. Asking again
    /// here would collide with that limiter — the trial run of 15:08 lost the race on all four key
    /// presses and the gate silently let every walk through. The answer already exists; the gate
    /// must use it rather than race for it.</para></summary>
    internal static void TryWalk(ManagedObject avatar, float x, float y, float z,
                                 in BeaconDestination dest)
    {
        if (avatar == null) { API.LogWarning("[SF6Access] Navi trial: no avatar"); return; }

        if (!_resolved) Resolve();
        if (_startNavi == null || _naviTd == null || _vec3Td == null || _naviPriority < 0)
        {
            Complain($"Navi trial unavailable: StartNavi={(_startNavi == null ? "no" : "yes")}, " +
                     $"NaviRequestData={(_naviTd == null ? "no" : "yes")}, vec3={(_vec3Td == null ? "no" : "yes")}, " +
                     $"priority={_naviPriority}");
            return;
        }

        // A second press while one is running is a request to stop, not to stack requests.
        if (Active)
        {
            End(avatar, "second press");
            ScreenReaderService.Speak(AutoWalkText.Stopped(), interrupt: false);
            return;
        }

        int targetType = FieldProbeService.EnumValue(TARGET_ENUM, "Position");
        int finding = FieldProbeService.EnumValue(FINDING_ENUM, "GoFront");
        int loop = FieldProbeService.EnumValue(LOOP_ENUM, "None");
        int move = FieldProbeService.EnumValue(MOVE_ENUM, "Walk");
        int end = FieldProbeService.EnumValue(END_ENUM, "None");
        if (targetType < 0 || move < 0)
        {
            Complain($"Navi trial: enums not readable (TargetType={targetType}, FindingActionType={finding}, " +
                     $"LoopType={loop}, MoveType={move}, EndAction={end})");
            return;
        }

        var before = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());

        // ---------------------------------------------------------------------------------
        // THE GATE. Decide the point to walk to, or refuse — and never refuse silently.
        //
        // History, because this gate has now been wrong twice and both times the log said so:
        //   (1) It asked its OWN path query, milliseconds after BeaconDestination.Resolve had run
        //       the identical query. FieldNavPath allows one per 250 ms, so it lost the race on
        //       every press, read "no answer", and — because no-answer deliberately proceeds — let
        //       every wall-bound walk through. Fixed by reading dest, not by asking again.
        //   (2) It then keyed ONLY on dest.NoRoute. The 15:20 trial showed why that is not enough:
        //       a LADDER leg reports NoRoute=false (there IS a route to the ladder's foot) while
        //       the ladder's top is the thing that cannot be reached — so the gate stayed open and
        //       the walk was sent at a ladder foot the planner had already rejected. dest.TryStep
        //       now answers the question the gate actually needs: is there a point worth walking
        //       to, and where is it?
        //
        // The asymmetry that governs every branch below: a wrong "keep going" is recoverable (the
        // player stops it with the same key, and the engine has stuck-detection); a wrong "stop" is
        // the avatar standing still with no explanation and no way to tell why. So every refusal
        // here SPEAKS, and the only silent outcomes are the ones that genuinely proceed.
        // ---------------------------------------------------------------------------------
        if (!dest.TryStep(out float sx, out float sy, out float sz, out string refuseWhy))
        {
            float rise = dest.TargetY - before.y;
            API.LogInfo($"[SF6Access] Navi trial: NOT walking — {refuseWhy}. " +
                        $"(key={dest.Key}, word={dest.Word}, ladder={dest.Ladder}, partial={dest.Partial}, " +
                        $"blocked={dest.Blocked}, atFoot={dest.AtFoot}, closest={dest.Closest}, " +
                        $"noRoute={dest.NoRoute}, holding={dest.Holding}, searching={dest.Searching}, " +
                        $"aim=({dest.X:F2}, {dest.Y:F2}, {dest.Z:F2}), " +
                        $"legEnd=({dest.TargetX:F2}, {dest.TargetY:F2}, {dest.TargetZ:F2}), " +
                        $"rise={rise:F2} m)");
            ScreenReaderService.Speak(AutoWalkText.CannotWalkThere(dest, rise), interrupt: false);
            return;
        }

        // Did the walk point move away from the homing cue? Then the beacon was pointing somewhere
        // a walk cannot go, and the trial would otherwise be silently walking somewhere else than
        // the player is being told to expect — the reported "the walk goes the opposite way to the
        // radar". Worth a warning, because it means the aimed point and the walkable point differ.
        float aimGap = MathF.Sqrt((sx - dest.X) * (sx - dest.X) + (sz - dest.Z) * (sz - dest.Z));
        if (dest.ViaRoute && aimGap > 1f)
            API.LogWarning($"[SF6Access] Navi trial: the walk point is {aimGap:F2} m from the aim point " +
                           $"the beacon is homing on (aim=({dest.X:F2}, {dest.Y:F2}, {dest.Z:F2}) " +
                           $"walk=({sx:F2}, {sy:F2}, {sz:F2})) — the player is being pointed one way and " +
                           $"walked another");
        if (sx != x || sy != y || sz != z)
            API.LogInfo($"[SF6Access] Navi trial: walk point resolved to ({sx:F2}, {sy:F2}, {sz:F2}) " +
                        $"rather than the leg end ({x:F2}, {y:F2}, {z:F2})");

        // ---------------------------------------------------------------------------------
        // THE TWENTY-SIXTH FINDING — ONE point presses into walls; the route must be the
        // engine's OWN bend list, recalled from the beacon's just-run query.
        //
        // The 20:15 trial proved the mechanics end-to-end — the element stored at the
        // array's native data pointer, read back, and the engine FOLLOWED it (moved 4.29 m,
        // cos=0.757 TOWARD, encoding Point) — and exposed the remaining defect in the same
        // breath: walk 1 wedged the avatar against a wall 3.16 m short of the target
        // (travelled (-1.07, -4.15) against the requested bearing (+2.08, -4.21)), and
        // walk 2, issued from the wedged position, moved 0.00 m. A one-point RoutePos
        // hands the engine a destination and NO route, so it walks AT the point and pushes
        // against whatever stands between — exactly the reported "开始走了，但是开始顶墙".
        //
        // The bends the walk needs already exist: the beacon's own Resolve ran this very
        // path query milliseconds ago, tracing the line and reading every portal point.
        // They are RECALLED from FieldNavPath's cache, never re-queried — a fresh query
        // here loses the rate-limit race to that resolve on every press, which is why the
        // old ProbeRoute (log-only, always throttled) is gone.
        // ---------------------------------------------------------------------------------
        (float X, float Y, float Z)[] routePoints;
        if (FieldNavPath.TryRecallRoute(dest.TargetX, dest.TargetY, dest.TargetZ, out var bends)
            && bends.Length > 0)
        {
            routePoints = AssembleRoute(bends, sx, sy, sz);
            LogRouteBends(routePoints, before);
        }
        else
        {
            API.LogInfo("[SF6Access] Navi trial: no bend list within recall (the beacon's query " +
                        "is older than the freshness window, or was for another goal) — falling " +
                        "back to the single walk point, which is the wall-pressing behaviour");
            var (rx, ry, rz) = RouteValue(sx, sy, sz, before);
            routePoints = new[] { (rx, ry, rz) };
        }
        if (!BuildRouteArray(routePoints))
        {
            Complain("Navi trial: could not build the route array; not calling StartNavi");
            ReleaseBuffers();
            return;
        }

        // The request itself. It is a REFERENCE type (block 8: IsValueType=False, parent
        // System.Object), which the FIRST trial run proved the hard way: FieldOutBuffer refused
        // it — correctly — because a buffer is only right for a value the engine reads or writes
        // in place, whereas a reference type is a live engine object. So it is obtained the way
        // this codebase already obtains every live engine object it needs to keep: NewInstance
        // (the game's own Activator) plus Globalize (AddRef), which is what makes it survive
        // past the frame that created it. See SharedInstance's remarks for why both steps are
        // mandatory and neither is optional.
        var req = _sharedRequest ??= FieldProbeService.SharedInstance(_naviTd);
        if (req == null)
        {
            Complain("Navi trial: could not create a NaviRequestData instance " +
                     "(CreateInstance or Globalize refused it)");
            ReleaseBuffers();
            return;
        }
        _request = req;

        // Written by NAME through the game's own field metadata, which is both safer and more
        // honest than raw offsets: the metadata and the write land in the same place by
        // construction, and a name the TDB does not publish reports itself instead of silently
        // scribbling on a neighbour.
        bool wroteRoute = TrySetField(req, "RoutePos", _routeObject);
        bool wroteEnums =
            SetEnumField(req, "TargetType", targetType) &
            SetEnumField(req, "FindingActionType", finding) &
            SetEnumField(req, "LoopType", loop) &
            SetEnumField(req, "MoveType", move) &
            SetEnumField(req, "EndAction", end);

        API.LogInfo($"[SF6Access] Navi trial: request filled — TargetType=Position({targetType}), " +
                    $"FindingActionType=GoFront({finding}), LoopType=None({loop}), MoveType=Walk({move}), " +
                    $"EndAction=None({end}), RoutePos={(wroteRoute ? "set" : "NOT SET")}, " +
                    $"enums={(wroteEnums ? "all landed" : "SOME DID NOT LAND")} " +
                    $"(object @0x{req.GetAddress():X}, route array @0x{_routeBlockAddr:X}, " +
                    $"declared size {_naviTd.Size})");

        // A half-filled request is worse than no request: TargetType defaults to _Invalid(0), which
        // is a perfectly legal "no destination" the engine accepts and then ignores — the exact
        // silent no-op that produced "it says it started, but the avatar does not move". So both
        // halves are hard preconditions for the call.
        if (!wroteRoute || !wroteEnums)
        {
            Complain($"Navi trial: request incomplete (route={wroteRoute}, enums={wroteEnums}); " +
                     "not calling StartNavi with a request the engine would silently ignore");
            ReleaseBuffers();
            return;
        }

        // The call. NaviRequestData is a REFERENCE type, so the live object goes in as itself —
        // the shape this codebase uses for every engine method that takes an object.
        //
        // The yield is published HERE, in the same frame as the call, and not left to a later
        // callback. Both this hook and AutoWalk are LateUpdate.Post, and the order between two
        // plugins is not guaranteed — so a flag set in one frame and read in another is read one
        // frame late, and that frame is a straight-line order written into a controller while the
        // engine's own pathfinder is already walking the avatar. Two drivers, one avatar, which is
        // the eighteenth finding.
        Active = true;
        AutoWalk.EngineIsDriving = true;
        // Arm the displacement verdict in the same breath as the call. The numbers are plain floats
        // held on this class (never an engine object across frames — the rule this mod has paid for
        // twice), and they are what turns "the call returned" into "the avatar moved".
        _issuedFrom = (before.x, before.y, before.z);
        var lastPoint = routePoints[routePoints.Length - 1];
        _issuedTo = (lastPoint.X, lastPoint.Y, lastPoint.Z);
        _issuedAt = 0;
        _verdictAt = 0;
        _startNavi.InvokeBoxed(typeof(void), avatar, new object[] { _naviPriority, req });

        // The priority is printed as the value that was ACTUALLY passed, not as a name written
        // into the string. This line used to say "FieldEvent" unconditionally while the call
        // carried MenuUI(4) — a log that disagrees with the code sends the next reader looking in
        // the wrong place, which is the same class of mistake as a success line for a failed write.
        API.LogInfo($"[SF6Access] Navi trial: StartNavi(priority={_naviPriority}{PriorityName(_naviPriority)}, " +
                    $"req) issued; player before=({before.x:F2}, {before.y:F2}, {before.z:F2}) ok={before.ok}. " +
                    "Press F12 again to stop.");
        ScreenReaderService.Speak(AutoWalkText.Started(), interrupt: false);
    }

    /// <summary>
    /// <b>What actually goes into <c>RoutePos[0]</c> — the point, or the direction to it.</b>
    ///
    /// <para><b>Why this is a switch and not a decision.</b> The evidence says the engine did not
    /// walk to the point it was given, but to that point REFLECTED through the player: intended
    /// direction (4.57, 2.56) produced actual (-4.32, -2.22), 177.9° apart, and the avatar's end
    /// position sat 0.42 m from the mirror of the walk point. Two encodings can produce that
    /// signature — the array holding an absolute point the engine treats as an offset, or the
    /// array holding an offset the engine treats as a point — and the code cannot tell them apart
    /// by reading, because both write cleanly and both read back as what was written. So the trial
    /// tries one, measures the displacement, and the measurement chooses.</para>
    ///
    /// <para><b>What is written for each mode.</b> <see cref="RouteEncoding.Point"/> writes the
    /// world co-ordinate (<c>x, y, z</c>); <see cref="RouteEncoding.Offset"/> writes the delta from
    /// the player to it (<c>x - px, y - py, z - pz</c>). The deltas are also logged in full, so the
    /// next attempt can be reasoned about on paper rather than guessed at in game.</para>
    /// </summary>
    private static (float x, float y, float z) RouteValue(float x, float y, float z,
                                                          (float x, float y, float z, bool ok) before)
    {
        float px = before.ok ? before.x : 0f;
        float py = before.ok ? before.y : 0f;
        float pz = before.ok ? before.z : 0f;
        float dx = x - px, dy = y - py, dz = z - pz;

        if (_routeEncoding == RouteEncoding.Offset)
        {
            API.LogInfo($"[SF6Access] Navi trial: RoutePos encoding=OFFSET — writing the delta from the " +
                        $"player ({px:F2}, {py:F2}, {pz:F2}) to the walk point ({x:F2}, {y:F2}, {z:F2}) " +
                        $"= ({dx:F2}, {dy:F2}, {dz:F2})");
            return (dx, dy, dz);
        }

        API.LogInfo($"[SF6Access] Navi trial: RoutePos encoding=POINT — writing the world co-ordinate " +
                    $"({x:F2}, {y:F2}, {z:F2}); delta from the player would be ({dx:F2}, {dy:F2}, {dz:F2})");
        return (x, y, z);
    }

    /// <summary>Which of the two candidate readings <c>RoutePos[0]</c> is written as. Starts at
    /// <see cref="RouteEncoding.Point"/> because that is what the engine's own type name —
    /// <c>RoutePos</c>, a position — says it should be, and the field name is the only evidence
    /// available that is not the avatar's behaviour. The displacement verdict is what is allowed to
    /// overrule it.
    ///
    /// <para><b>THE TWENTY-FIFTH FINDING resets this to Point.</b> Every previous verdict —
    /// including the 15:43 run that flipped this to <c>Offset</c> — was measured on a CORRUPTED
    /// array (the old addressed write overwrote the elements pointer and count with float bits,
    /// so the engine walked toward wild-pointer garbage, never toward any point this code chose).
    /// Those verdicts say nothing about the encoding; the first honest trial starts from
    /// <c>Point</c> again.</para></summary>
    internal enum RouteEncoding { Point = 0, Offset = 1 }

    private static RouteEncoding _routeEncoding = RouteEncoding.Point;

    /// <summary>The enum name for a priority value, for logs — read from the TDB, so it stays
    /// right if the ladder is ever extended. Empty string when the number matches no member,
    /// which is itself worth seeing.</summary>
    private static string PriorityName(int value)
    {
        if (value < 0) return "";
        foreach (string name in PriorityNames)
        {
            int v = FieldProbeService.EnumValue(PRIORITY_ENUM, name);
            if (v == value) return $" ({name})";
        }
        return "";
    }

    /// <summary>The ladder as published, low to high. Order is documentary; only the values
    /// matter, and they are read from the engine rather than assumed.</summary>
    private static readonly string[] PriorityNames =
    {
        "_Invalid", "Default", "__DebugDefault", "FieldEvent", "MenuUI", "Flow", "BattleIn", "__DebugForce",
    };

    /// <summary>
    /// <b>Where the avatar was when the request went out, and how far it has actually moved since.</b>
    /// Safe to call every frame; cheap and does nothing until a request has been issued.
    ///
    /// <para><b>Why the trial needs this.</b> Until now the only evidence a trial walk "worked" was
    /// that <c>StartNavi</c> returned without throwing — and the whole history of this feature is a
    /// list of calls that were accepted and ignored. The displacement is the one verdict that is
    /// measured from outside the mod, and it is the same standard the straight-line route was held
    /// to. It also settles the eighteenth finding's question directly: with <c>AutoWalk</c> standing
    /// down, any movement at all is the engine's own pathfinder moving the avatar, which is what the
    /// trial exists to prove.</para>
    ///
    /// <para><b>Reported once, at a fixed elapsed time, so the number is comparable between runs.</b>
    /// A running commentary would flood a screen reader; one sentence at <see cref="VERDICT_MS"/>
    /// answers the only question worth asking, and the log carries the raw metres either way.</para>
    /// </summary>
    internal static void MeasureWalk()
    {
        if (!Active) return;
        long now = Environment.TickCount64;
        if (_issuedAt == 0) { _issuedAt = now; return; }
        if (_verdictAt != 0) return;

        var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        if (!me.ok) return;

        if (_verdictAt == 0 && now - _issuedAt >= VERDICT_MS)
        {
            _verdictAt = now;
            float dx = me.x - _issuedFrom.x, dz = me.z - _issuedFrom.z;
            float moved = MathF.Sqrt(dx * dx + dz * dz);
            float left = MathF.Sqrt((_issuedTo.x - me.x) * (_issuedTo.x - me.x) +
                                    (_issuedTo.z - me.z) * (_issuedTo.z - me.z));

            // Which way DID it go, against which way it was asked to go? A walk that moves is not
            // automatically a walk that worked: the 15:43 run covered 4.86 m at 177.9° from the
            // requested bearing, i.e. briskly in the opposite direction. So the verdict is signed,
            // and the sign is what picks the encoding for the next attempt.
            float ix = _issuedTo.x - _issuedFrom.x, iz = _issuedTo.z - _issuedFrom.z;
            float inLen = MathF.Sqrt(ix * ix + iz * iz);
            float cos = (moved > 0.01f && inLen > 0.01f) ? (dx * ix + dz * iz) / (moved * inLen) : 0f;
            bool toward = cos > 0.5f;      // within ~60° of the requested bearing
            bool away = cos < -0.5f;       // more than 120° off: it went the other way
            bool walked = moved >= 1.5f;

            API.LogInfo($"[SF6Access] Navi trial: VERDICT after {VERDICT_MS} ms — moved {moved:F2} m " +
                        $"from ({_issuedFrom.x:F2}, {_issuedFrom.z:F2}) to ({me.x:F2}, {me.z:F2}); " +
                        $"{left:F2} m left to the walk point ({_issuedTo.x:F2}, {_issuedTo.z:F2}); " +
                        $"requested bearing ({ix:F2}, {iz:F2}), travelled ({dx:F2}, {dz:F2}), " +
                        $"cos={cos:F3} ({(toward ? "TOWARD the target" : away ? "AWAY from the target" : "sideways")}); " +
                        $"encoding was {_routeEncoding}");

            if (away)
            {
                // The signature of a point/offset mix-up, and now with a direction attached: walking
                // the correct line in the opposite sign. Switch the encoding and say so, so one more
                // press answers the question instead of the player having to guess.
                _routeEncoding = _routeEncoding == RouteEncoding.Point
                    ? RouteEncoding.Offset : RouteEncoding.Point;
                API.LogWarning($"[SF6Access] Navi trial: the avatar walked the requested line in the " +
                               $"WRONG SIGN (cos={cos:F3}), which is what a point/offset mix-up looks " +
                               $"like. Switching RoutePos encoding to {_routeEncoding} for the next press.");
                ScreenReaderService.Speak(AutoWalkText.WalkedBackwards(), interrupt: false);
            }
            else if (walked && toward)
            {
                ScreenReaderService.Speak(AutoWalkText.EngineWalked(moved), interrupt: false);
            }
            else if (!walked)
            {
                // No movement at all is a different failure from wrong-direction movement, and the
                // two need different fixes, so they are never merged into one sentence.
                ScreenReaderService.Speak(AutoWalkText.EngineIgnored(), interrupt: false);
            }
        }
    }

    /// <summary>How long after the request the displacement verdict is taken. Long enough for a
    /// walk to cover several metres at the game's 2–4 m/s, short enough that the player is not
    /// left wondering; the same order of magnitude as the straight-line route's settling window.</summary>
    private const int VERDICT_MS = 2500;

    private static long _issuedAt;
    private static long _verdictAt;
    private static (float x, float y, float z) _issuedFrom;
    private static (float x, float y, float z) _issuedTo;

    /// <summary>
    /// <b>An N-element <c>via.vec3[]</c>, as a real, globalized engine array.</b>
    ///
    /// <para><b>Why the type still comes from the request's own metadata — the twenty-second
    /// finding.</b> The TDB lookup for the STRING "via.vec3[]" returns a JAGGED array —
    /// <c>via.vec3[][]</c> — which the engine's own exception proved by naming it
    /// (<c>REMethodDefinition::invoke for via.vec3[][].set_Item</c>). Building that type is
    /// what crashed the game at 16:04. So the array type is read from the request's OWN
    /// <c>RoutePos</c> field metadata, and the element type is checked to actually be
    /// <c>via.vec3</c> — whatever the engine declares is what gets built, never a guess.</para>
    ///
    /// <para><b>Why one array per length.</b> The route's point count varies with the city
    /// block, and a managed array's length is fixed at creation. Each length used is created
    /// once, globalized, and kept for the process: the engine reads the array while walking,
    /// so it must outlive the call, and releasing a rooted object is the dangerous direction,
    /// not holding it (see <see cref="FieldProbeService.SharedInstance"/>).</para>
    ///
    /// <para><b>Why N points and not one — the twenty-sixth finding.</b> A one-point
    /// <c>RoutePos</c> hands the engine a destination and no route, so it walks AT the point
    /// and presses into whatever stands between (the 20:15 trial: store verified, engine
    /// followed the point, cos=0.757 TOWARD — and the avatar wedged against a wall 3.16 m
    /// short; the next walk, from the wedged position, moved 0.00 m). The points here are
    /// the engine's OWN string-pulled bend list, recalled from the query the beacon just
    /// ran, so the walk can go round the wall instead of through it.</para></summary>
    private static bool BuildRouteArray((float X, float Y, float Z)[] points)
    {
        try
        {
            if (_routeElemStride <= 0 && !BindRouteArrayType()) return false;

            if (!_routeArrays.TryGetValue(points.Length, out var array))
            {
                array = _routeArrayTd.CreateManagedArray((uint)points.Length);
                if (array == null)
                {
                    API.LogWarning($"[SF6Access] Navi trial: CreateManagedArray({points.Length}) on " +
                                   $"{_routeArrayTd.FullName} returned null");
                    return false;
                }
                // The same two steps as every kept engine object: Globalize is what makes it
                // survive the frame. A local object released while the walk is still reading it
                // is the crash documented in docs/sf6-architecture.md.
                try { array.Globalize(); }
                catch (Exception ex)
                {
                    API.LogWarning($"[SF6Access] Navi trial: could not globalize the route array " +
                                   $"({ex.GetType().Name}) — refusing it");
                    return false;
                }
                int refs = 0;
                try { refs = array.GetReferenceCount(); } catch { }
                if (refs <= 0)
                {
                    API.LogWarning($"[SF6Access] Navi trial: Globalize left the route array reference " +
                                   $"count at {refs} — refusing it");
                    return false;
                }
                _routeArrays[points.Length] = array;
                API.LogInfo($"[SF6Access] Navi trial: route array created and globalized " +
                            $"(@0x{array.GetAddress():X}, refs={refs}); element count={points.Length}, " +
                            $"element type={VEC3_TYPE} (stride {_routeElemStride} bytes)");
            }

            _routeObject = array;
            _routeBlockAddr = array.GetAddress();

            // The elements themselves: every bend, written into the array's native data area
            // and read back — see WriteArrayElements for why that area and nowhere else.
            if (!WriteArrayElements(array, points, _routeElemStride)) return false;
            return true;
        }
        catch (Exception ex)
        {
            API.LogWarning($"[SF6Access] Navi trial: route array build failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Bind and validate the route array's TYPE, once, from the request's own
    /// <c>RoutePos</c> field metadata. Refuses — never guesses — if the element type is not
    /// <c>via.vec3</c>, because the string-based lookup already produced <c>via.vec3[][]</c>
    /// once and that crashed the game.</summary>
    private static bool BindRouteArrayType()
    {
        var routeField = _naviTd.FindField("RoutePos");
        var arrayTd = routeField?.Type;
        if (arrayTd == null)
        {
            API.LogWarning("[SF6Access] Navi trial: the request publishes no RoutePos field, " +
                           "so a route array cannot be created from its declared type");
            return false;
        }

        // The element type, asked of the array type itself — never assumed. A jagged array
        // would report an element that is itself an array, which is the exact mistake the
        // twenty-second finding records, so the shape is named in the log before anything
        // is allocated.
        var elemTd = arrayTd.GetElementType();
        string elemName = elemTd?.GetFullName() ?? "<not published>";
        uint elemSize = 0;
        try { elemSize = elemTd?.ValueTypeSize ?? 0; } catch { }

        // What the element type actually IS. The 16:39 dump settled the layout question:
        //
        //   fields = [x@16, y@20, z@24, Zero@16, One@32, NegativeOne@48, AxisX@64,
        //             AxisY@80, AxisZ@96, Min@112, Max@128]
        //
        // via.vec3 IS a value type (IsValueType=True). x/y/z are the INSTANCE fields —
        // their offsets are relative to a BOXED base (payload at +16, so payload-relative
        // they are 0/4/8, which is what the direct store writes). Zero/One/NegativeOne/
        // AxisX/AxisY/AxisZ/Min/Max are STATIC members whose offsets point into the statics
        // area — they are NOT instance fields and must never be read as "the second field
        // of a vec3".
        try
        {
            var fields = elemTd?.GetFields();
            var names = new List<string>();
            if (fields != null)
                foreach (var f in fields)
                {
                    string off = "?";
                    try { off = f.OffsetFromBase.ToString(); } catch { }
                    names.Add($"{f.Name}@{off}:{f.Type?.GetFullName() ?? "?"}");
                }
            API.LogInfo($"[SF6Access] Navi trial: element type {elemName} — " +
                        $"ValueTypeSize={elemSize}, IsValueType={elemTd?.IsValueType()} " +
                        $"(type: {arrayTd.GetFullName()}, element: {elemName}); " +
                        $"fields (instance + STATIC — Zero/One/Axis* are statics, only " +
                        $"x/y/z are instance) = [{string.Join(", ", names)}]");
        }
        catch (Exception ex)
        {
            API.LogInfo($"[SF6Access] Navi trial: element type {elemName} " +
                        $"(ValueTypeSize={elemSize}); its fields could not be listed " +
                        $"({ex.GetType().Name})");
        }
        API.LogInfo($"[SF6Access] Navi trial: RoutePos is declared as {arrayTd.GetFullName()}; " +
                    $"element type = {elemName} ({elemSize} bytes)");
        if (elemTd == null || elemName != VEC3_TYPE || elemSize <= 0)
        {
            API.LogWarning($"[SF6Access] Navi trial: RoutePos's element type is " +
                           $"{elemName}, not {VEC3_TYPE} — refusing to build a route array " +
                           $"from a type this code does not understand (a string-based lookup " +
                           $"already produced via.vec3[][] once, and that crashed the game)");
            return false;
        }

        _routeArrayTd = arrayTd;
        _routeElemStride = (int)elemSize;
        return true;
    }

    /// <summary>Store every route point into the array, verified by reading the bytes back
    /// from the array's own native data area.
    ///
    /// <para><b>The layout, settled by the 17:37 and 20:15 dumps (twenty-fifth finding).</b>
    /// This is RE Engine's <c>via.Array</c>: the elements live in NATIVE memory BEHIND the
    /// pointer at <c>+0x10</c> in the array object, not inline. That pointer is READ, never
    /// written — the 15:43 wrong-sign walk and the 16:04 crash were both floats written over
    /// the pointer and its neighbour. What <c>+0x18</c> actually holds is NOT settled: a
    /// 13-element array read 1 there (20:51), so the authoritative element count is taken
    /// from the engine's own <see cref="_System.Array"/> proxy, and the header fields are
    /// logged for the record.</para>
    ///
    /// <para><b>Two store paths, bytes as the judge.</b> Path 1 is the engine's own typed
    /// store — <c>_System.Array.SetValue</c> with a real <c>via.vec3</c> value built from its
    /// TypeDefinition — the documented, shape-correct way to write an array element. Path 2
    /// is the direct store proven by the 20:15 walk: three floats at
    /// <c>dataPtr + i * stride</c>, payload-relative (+0/+4/+8), aimed by the game's OWN
    /// pointer. Path 1 runs first when the proxy is available; whether it landed is decided
    /// by scanning the data area for the triples, never by the call returning. (The raw
    /// <c>set_Item</c> through InvokeBoxed was a proven no-op three times and stays
    /// gone.)</para></summary>
    private static bool WriteArrayElements(ManagedObject array,
                                           (float X, float Y, float Z)[] points, int stride)
    {
        try
        {
            ulong arrAddr = array.GetAddress();

            // The engine's own answer to "how many elements does this array hold".
            _System.Array proxy = null;
            int proxyLen = -1;
            try
            {
                proxy = array.As<_System.Array>();
                proxyLen = proxy?.Length ?? -1;
            }
            catch (Exception ex)
            {
                API.LogInfo($"[SF6Access] Navi trial: the _System.Array proxy refused the cast " +
                            $"({ex.GetType().Name}: {ex.Message}) — the header fields are the only " +
                            $"length evidence");
            }

            ulong dataPtr = 0;
            int count18 = 0;
            long len08 = -1;
            try
            {
                dataPtr = ReadQwordAt(arrAddr + DATA_POINTER_OFFSET);
                count18 = Marshal.ReadInt32((IntPtr)(long)(arrAddr + COUNT_OFFSET));
                len08 = Marshal.ReadInt64((IntPtr)(long)(arrAddr + 0x08));
            }
            catch (Exception ex)
            {
                API.LogWarning($"[SF6Access] Navi trial: the route array's header could not be " +
                               $"read ({ex.GetType().Name}: {ex.Message}) — refusing; a store that " +
                               $"cannot be aimed cannot be verified either");
                return false;
            }
            API.LogInfo($"[SF6Access] Navi trial: route array @0x{arrAddr:X} — proxy Length={proxyLen}, " +
                        $"header +0x08={len08}, +0x10=0x{dataPtr:X} (elements), +0x18={count18}, " +
                        $"needed={points.Length}");
            if (dataPtr == 0)
            {
                API.LogWarning("[SF6Access] Navi trial: the route array's data pointer is null — " +
                               "there is no element storage to fill; refusing");
                return false;
            }

            // The authoritative capacity is the proxy's; the header field is the fallback.
            // (20:51: a 13-element array read 1 at +0x18, so +0x18 alone is not trusted.)
            int capacity = proxyLen >= 0 ? proxyLen : count18;
            if (capacity < points.Length)
            {
                API.LogWarning($"[SF6Access] Navi trial: the route array holds {capacity} element(s) " +
                               $"(proxy Length={proxyLen}, +0x18={count18}) but the route needs " +
                               $"{points.Length} — refusing to write past the engine's own count");
                return false;
            }

            // The first element's region BEFORE the store, so the log shows the history.
            string dataBefore = DumpBytes(dataPtr, DATA_DUMP_BYTES);

            // Path 1: the engine's own typed store, if the proxy is in hand. The elements
            // are kept in a list until after the verification scan, so nothing the array
            // might reference is collected mid-frame.
            if (proxy != null && TryProxyStore(proxy, points, out var kept))
            {
                if (AllElementsReadBack(dataPtr, points, stride))
                {
                    API.LogInfo($"[SF6Access] Navi trial: {points.Length} route element(s) stored by " +
                                $"the engine's own _System.Array.SetValue — all triples are in the " +
                                $"native data area at 0x{dataPtr:X} (stride {stride}). " +
                                $"Before [{dataBefore}] After [{DumpBytes(dataPtr, DATA_DUMP_BYTES)}]");
                    GC.KeepAlive(kept);
                    return true;
                }
                GC.KeepAlive(kept);
                API.LogInfo("[SF6Access] Navi trial: _System.Array.SetValue completed without " +
                            "throwing but the triples are not all in the data area — falling " +
                            "back to the direct store");
            }

            // Path 2: the direct store, per element: three floats at dataPtr + i*stride,
            // payload-relative — the convention FieldOutBuffer's ray-caster writes have
            // proven for weeks, aimed by the game's OWN pointer. The 4 padding bytes of
            // each 16-byte element are never touched.
            for (int i = 0; i < points.Length; i++)
            {
                ulong slot = dataPtr + (ulong)(i * stride);
                var (x, y, z) = points[i];
                try
                {
                    Marshal.WriteInt32((IntPtr)(long)slot, BitConverter.SingleToInt32Bits(x));
                    Marshal.WriteInt32((IntPtr)(long)(slot + 4), BitConverter.SingleToInt32Bits(y));
                    Marshal.WriteInt32((IntPtr)(long)(slot + 8), BitConverter.SingleToInt32Bits(z));
                }
                catch (Exception ex)
                {
                    API.LogWarning($"[SF6Access] Navi trial: the direct store of element {i} at " +
                                   $"0x{slot:X} threw {ex.GetType().Name}: {ex.Message} — refusing");
                    return false;
                }
            }

            if (!AllElementsReadBack(dataPtr, points, stride)) return false;

            var (fx, fy, fz) = points[0];
            var (lx, ly, lz) = points[points.Length - 1];
            API.LogInfo($"[SF6Access] Navi trial: {points.Length} route element(s) stored directly " +
                        $"at the array's own data pointer 0x{dataPtr:X} (stride {stride}); " +
                        $"first=({fx:F2}, {fy:F2}, {fz:F2}) last=({lx:F2}, {ly:F2}, {lz:F2}); " +
                        $"all read back from the same addresses. Before [{dataBefore}] " +
                        $"After [{DumpBytes(dataPtr, DATA_DUMP_BYTES)}] — the array's pointer " +
                        $"and count were never touched.");
            return true;
        }
        catch (Exception ex)
        {
            API.LogWarning($"[SF6Access] Navi trial: route element write failed: " +
                           $"{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Path 1: build a real <c>via.vec3</c> per point from its TypeDefinition and
    /// hand each to <c>_System.Array.SetValue</c>. Returns false on any throw — the caller
    /// then falls back to the direct store. The built values ride back in
    /// <paramref name="kept"/> so nothing the array might reference is collected before the
    /// verification scan has run.</summary>
    private static bool TryProxyStore(_System.Array proxy, (float X, float Y, float Z)[] points,
                                      out List<object> kept)
    {
        kept = new List<object>(points.Length);
        try
        {
            for (int i = 0; i < points.Length; i++)
            {
                var (x, y, z) = points[i];
                var v = _vec3Td.CreateValueType();
                if (v == null) return false;
                var io = v as IObject;
                if (io == null) return false;
                io.Call("set_x", x);
                io.Call("set_y", y);
                io.Call("set_z", z);
                kept.Add(v);
                proxy.SetValue(v, i);
            }
            return true;
        }
        catch (Exception ex)
        {
            API.LogInfo($"[SF6Access] Navi trial: _System.Array.SetValue path threw " +
                        $"{ex.GetType().Name}: {ex.Message} — falling back to the direct store");
            return false;
        }
    }

    /// <summary>Read back every element from the native data area, from the same addresses
    /// the engine will read. Any mismatch is a refusal with the evidence in the log.</summary>
    private static bool AllElementsReadBack(ulong dataPtr, (float X, float Y, float Z)[] points,
                                            int stride)
    {
        for (int i = 0; i < points.Length; i++)
        {
            ulong slot = dataPtr + (ulong)(i * stride);
            var (x, y, z) = points[i];
            float gx = ReadFloatAt(slot);
            float gy = ReadFloatAt(slot + 4);
            float gz = ReadFloatAt(slot + 8);
            if (gx != x || gy != y || gz != z)
            {
                API.LogWarning($"[SF6Access] Navi trial: element {i} did not land — 0x{slot:X} " +
                               $"reads ({gx:F3}, {gy:F3}, {gz:F3}), expected ({x:F3}, {y:F3}, " +
                               $"{z:F3}). Area now [{DumpBytes(dataPtr, DATA_DUMP_BYTES)}]");
                return false;
            }
        }
        return true;
    }

    /// <summary>Where <c>via.Array</c> keeps its pointer to the native element storage, read from
    /// the 17:37 dump: <c>+0x10</c> held <c>0x14D5935C0</c> while the count sat at
    /// <c>+0x18</c>. This offset is READ, never written — the 15:43 wrong-sign walk and the
    /// 16:04 crash were both floats written over this pointer and the count.</summary>
    private const int DATA_POINTER_OFFSET = 0x10;

    /// <summary>Where the element count lives, same dump: <c>+0x18</c>.</summary>
    private const int COUNT_OFFSET = 0x18;

    /// <summary>How many bytes of the native data area to dump around the store. One element is
    /// 16 bytes (via.vec3's ValueTypeSize); 32 covers it with slack, and stays inside the heap
    /// allocation's usual rounding so the dump cannot fault.</summary>
    private const int DATA_DUMP_BYTES = 32;

    /// <summary>Scan <paramref name="count"/> bytes at <paramref name="baseAddr"/> for the
    /// requested x/y/z triple, on 4-byte alignment. Returns the offset where it starts, or
    /// −1.</summary>
    private static int FindTriple(ulong baseAddr, int count, float x, float y, float z)
    {
        try
        {
            for (int p = 0; p + 12 <= count; p += 4)
            {
                if (ReadFloatAt(baseAddr + (ulong)p) == x &&
                    ReadFloatAt(baseAddr + (ulong)(p + 4)) == y &&
                    ReadFloatAt(baseAddr + (ulong)(p + 8)) == z)
                    return p;
            }
        }
        catch { }
        return -1;
    }

    /// <summary>The bytes at an address, hex, for the log. Read-only; an unreadable region
    /// reports itself rather than failing the write that is being diagnosed.</summary>
    private static string DumpBytes(ulong baseAddr, int count)
    {
        try
        {
            var b = new List<string>(count);
            for (int i = 0; i < count; i++)
                b.Add(Marshal.ReadByte((IntPtr)(long)(baseAddr + (ulong)i)).ToString("x2"));
            return string.Join(" ", b);
        }
        catch (Exception ex) { return $"<unreadable: {ex.GetType().Name}>"; }
    }

    private static float ReadFloatAt(ulong addr) =>
        BitConverter.Int32BitsToSingle(Marshal.ReadInt32((IntPtr)(long)addr));

    private static ulong ReadQwordAt(ulong addr) =>
        (ulong)Marshal.ReadInt64((IntPtr)(long)addr);

    /// <summary>Set a reference-typed field (an object) on the live request by NAME, through the
    /// game's own field metadata — the same mechanism the rest of the codebase reads fields with,
    /// run in reverse.
    ///
    /// <para><c>isValueType: false</c> because the request is a reference type: <c>obj</c>'s own
    /// address is the object, not the address of a value payload. Getting this flag wrong is the
    /// distinction <c>Field.GetDataRaw</c>'s remarks turn on, and it fails silently.</para>
    ///
    /// <para><b>The write is verified by re-reading the field by NAME through the containing
    /// object</b> — the same path any other reader in this mod would take, not the wrapper that
    /// was just written through. A read-back through the writing wrapper proves nothing.</para></summary>
    private static bool TrySetField(ManagedObject obj, string name, object value)
    {
        try
        {
            var f = _naviTd.FindField(name) ?? obj.GetTypeDefinition()?.FindField(name);
            if (f == null)
            {
                API.LogWarning($"[SF6Access] Navi trial: field {name} is not published on {_naviTd.FullName}");
                return false;
            }
            f.SetDataBoxed(obj.GetAddress(), value, false);
            var back = obj.GetField(name) as ManagedObject;
            bool landed = ReferenceEquals(back, value) || (back != null && value is ManagedObject v
                                                           && back.GetAddress() == v.GetAddress());
            if (!landed)
                API.LogWarning($"[SF6Access] Navi trial: {name} did not read back as the object that " +
                               $"was written (got {(back == null ? "null" : "0x" + back.GetAddress().ToString("X"))})");
            return landed;
        }
        catch (Exception ex)
        {
            API.LogWarning($"[SF6Access] Navi trial: {name} write failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// <b>The boxed type each enum field must be written as.</b>
    ///
    /// <para><b>How these were obtained, and why not by asking the type system.</b>
    /// <c>Field.Type.GetUnderlyingType()</c> was the obvious source and it lied: it reported
    /// <c>System.Int64</c> for every one of these five enums, while <c>SetDataBoxed</c> rejected
    /// every <c>Int64</c> box with a cast error naming the type it actually wanted —
    /// <c>"Unable to cast object of type 'System.Int64' to type 'System.Byte'"</c>. REFrameworkNET
    /// publishes no enum-aware boxing helper at all (<c>TypeDefinition.IsEnum()</c> is the only
    /// enum member on the whole surface), so the engine, not the reflection layer, owns the
    /// conversion, and the only reliable statement of what it accepts is the engine's own refusal.
    /// These five values are read off those five messages verbatim; the field offsets corroborate
    /// them (see the layout table in docs/wt-autowalk.md: <c>LoopType</c> and <c>MoveType</c> are
    /// one byte apart, so neither can be four bytes wide).</para>
    ///
    /// <para><b>The general lesson.</b> For an engine-side conversion, an exception that names both
    /// the type given and the type required is a complete specification. Reading it as one is
    /// cheaper and more correct than deriving the same fact from metadata that may be a
    /// placeholder — which is what <c>GetUnderlyingType</c> turned out to be here.</para></summary>
    private static readonly (string field, Type box)[] EnumWritePlan =
    {
        ("TargetType",        typeof(byte)),   // RouteTargetType:          _Invalid/Position/Object
        ("FindingActionType", typeof(int)),    // PathFindingActionType:    GoFront/Stand
        ("LoopType",          typeof(byte)),   // RouteLoopType:            None/Loop/TurnBack
        ("MoveType",          typeof(byte)),   // MoveActionType:           Walk/Run/ChaseRun/...
        ("EndAction",         typeof(byte)),   // EndActionType:            None/TurnToSettedDir
    };

    /// <summary>
    /// Set an enum-typed field with the boxed type the engine's own conversion expects, then read
    /// it back through the containing object so a silent no-op cannot be mistaken for a write.
    /// Returns whether the value landed.</summary>
    private static bool SetEnumField(ManagedObject obj, string name, int value)
    {
        try
        {
            var f = _naviTd.FindField(name) ?? obj.GetTypeDefinition()?.FindField(name);
            if (f == null) { API.LogWarning($"[SF6Access] Navi trial: enum field {name} not published"); return false; }

            Type box = typeof(byte);
            foreach (var (n, b) in EnumWritePlan) if (n == name) { box = b; break; }

            object boxed = box == typeof(int) ? (object)value : (object)(byte)value;
            f.SetDataBoxed(obj.GetAddress(), boxed, false);

            // Read back through the object's own field lookup, not through the Field written
            // through: a read-back through the writing wrapper proves nothing.
            var raw = f.GetDataBoxed(obj.GetAddress(), false);
            int back = raw == null ? int.MinValue : Convert.ToInt32(raw);
            if (back != value)
            {
                API.LogWarning($"[SF6Access] Navi trial: {name} wrote {value} as {box.Name} but reads back " +
                               $"{back} as {raw?.GetType().Name ?? "null"}");
                return false;
            }
            API.LogInfo($"[SF6Access] Navi trial: {name} = {value} written as {box.Name}, read back OK");
            return true;
        }
        catch (Exception ex)
        {
            API.LogWarning($"[SF6Access] Navi trial: {name} write failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void Complain(string message)
    {
        if (!_loggedUnavailable) { _loggedUnavailable = true; API.LogWarning($"[SF6Access] {message}"); }
        else API.LogInfo($"[SF6Access] {message}");
        ScreenReaderService.Speak(AutoWalkText.Unavailable(), interrupt: false);
    }

    private static void Resolve()
    {
        _resolved = true;
        var baseTd = TDB.Get().FindType(BASE_TYPE);
        _naviTd = TDB.Get().FindType(NAVI_TYPE);
        _vec3Td = TDB.Get().FindType(VEC3_TYPE);

        // PriorityType is a LOW-TO-HIGH ladder:
        //   _Invalid 0 < Default 1 < __DebugDefault 2 < FieldEvent 3 < MenuUI 4 < Flow 5 < BattleIn 6 < __DebugForce 7
        // Block 4 read GetCurrentInputControllerPriority() = 3 while the player was simply standing
        // in the field — i.e. the controller occupying the avatar is ALREADY FieldEvent. Asking for
        // FieldEvent therefore asks at exactly the current level, and a request at or below the
        // current priority is the classic reason an input-manager API accepts a call and does
        // nothing: nothing outranks anything, so there is no handover to perform. The trial asks
        // one rung HIGHER than the field's own level so a handover is actually required.
        _naviPriority = FieldProbeService.EnumValue(PRIORITY_ENUM, "MenuUI");
        if (_naviPriority < 0) _naviPriority = FieldProbeService.EnumValue(PRIORITY_ENUM, "FieldEvent");

        // StartNavi(priority, NaviRequestData) and ForceEndNavi() — both confirmed by the
        // AvatarBase method dump (2026-09-29 13:15, block 2).
        _startNavi = FindByShapeAndSecond(baseTd, "StartNavi", "NaviRequestData");
        _forceEndNavi = FindByName(baseTd, "ForceEndNavi");

        bool ok = _startNavi != null && _forceEndNavi != null && _naviTd != null && _vec3Td != null;
        if (ok)
            API.LogInfo("[SF6Access] Navi trial bound: StartNavi(PriorityType, NaviRequestData) + ForceEndNavi() at " +
                        $"priority={_naviPriority} (MenuUI; FieldEvent would be " +
                        $"{FieldProbeService.EnumValue(PRIORITY_ENUM, "FieldEvent")}); " +
                        $"NaviRequestData size={_naviTd.Size} " +
                        $"ValueTypeSize={_naviTd.ValueTypeSize}, vec3 ValueTypeSize={_vec3Td.ValueTypeSize}, " +
                        $"RoutePos declared as " +
                        $"{_naviTd.FindField("RoutePos")?.Type?.GetFullName() ?? "<no RoutePos field>"}");
        else
            API.LogWarning($"[SF6Access] Navi trial could not bind: StartNavi={(_startNavi == null ? "no" : "yes")}, " +
                           $"ForceEndNavi={(_forceEndNavi == null ? "no" : "yes")}, " +
                           $"NaviRequestData={(_naviTd == null ? "no" : "yes")}, vec3={(_vec3Td == null ? "no" : "yes")}, " +
                           $"priority={_naviPriority}");
    }

    /// <summary>Find a method by NAME and by the shape of its parameter list.
    ///
    /// <para><b>Why this now states which overload it picked.</b> The thirteenth finding was a
    /// signature match that reached a method belonging to a different type entirely, and it did so
    /// silently. This helper cannot check ownership the same way — it is deliberately searching up
    /// the parent chain (<c>AvatarBase</c> may inherit <c>StartNavi</c>), so a required
    /// <c>DeclaringType</c> would defeat its purpose — but it CAN refuse to be quiet when more
    /// than one candidate matches. When that happens the chosen one is logged with its declaring
    /// type, so a wrong pick is visible in the log instead of only in the avatar's behaviour.</para>
    /// </summary>
    private static Method FindByShapeAndSecond(TypeDefinition td, string name, string secondParamSuffix)
    {
        Method found = null;
        int matches = 0;
        for (var t = td; t != null; t = t.ParentType)
        {
            List<Method> methods;
            try { methods = t.GetMethods(); } catch { continue; }
            if (methods == null) continue;
            foreach (var m in methods)
            {
                if (m.Name != name) continue;
                try
                {
                    var ps = m.GetParameters();
                    if (ps != null && ps.Count == 2 && ps[1].Type?.FullName?.EndsWith(secondParamSuffix) == true)
                    {
                        matches++;
                        if (found == null) found = m;
                    }
                }
                catch { }
            }
        }
        if (matches > 1)
            API.LogInfo($"[SF6Access] Navi trial: {name} has {matches} overloads matching " +
                        $"(?, {secondParamSuffix}); using the one declared on " +
                        $"{found?.DeclaringType?.FullName ?? "-unknown-"}");
        return found;
    }

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
}
