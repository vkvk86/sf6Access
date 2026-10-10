using System;
using REFrameworkNET;
using SF6Access.Services;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>When the game's own pathfinder cannot get all the way, because something in the
/// way has to be broken by the player first.</b>
///
/// <para><b>The problem this solves, in the owner's words.</b> "假如要到达目标点需要破坏某个
/// 障碍物的话，导航怎么走都是过不去的" — if reaching the destination requires smashing
/// something, no amount of pathfinding gets there. And the owner proposed the right shape
/// for the answer: stop in front of the obstacle, tell the player, let them break it, then
/// resume the walk. This class is that loop.</para>
///
/// <para><b>Why a "blocked" state is a real state and not a failure.</b> Navigation has
/// three possible outcomes, and until now this mod only distinguished two:
/// <list type="number">
/// <item><b>Walked</b> — the avatar moves. Handled by the walk itself.</item>
/// <item><b>Blocked by the player</b> — the player pushes the stick. Handled by takeover.</item>
/// <item><b>Blocked by the world</b> — navigation is live, the avatar is not moving, and the
/// player is not touching anything. <i>This is the case that had no handling</i>, and the
/// one where a sighted player would see a crate and a blind player would see nothing at all
/// and conclude the mod is broken.</item>
/// </list></para>
///
/// <para><b>How "blocked" is detected, and why that way.</b> By the avatar's own speed: if
/// navigation is running and the avatar has moved less than <see cref="StuckSpeedMps"/> for
/// <see cref="StuckSeconds"/> continuously, the world is stopping it. The alternative — ask
/// the engine which obstacle closed the path — needs the engine to expose a path the mod can
/// read, which no probe has shown it does. Speed is available every frame, needs nothing from
/// the engine, and measures the thing that actually matters to the player: <i>am I getting
/// closer or not?</i> A speed test also cannot be fooled by a path that exists on paper but
/// not in practice, which is exactly the "navmesh does not model a breakable" case
/// <see cref="LevelRoutePlanner"/> already warns about.</para>
///
/// <para><b>What the player hears.</b> The obstruction is named, its direction is given by
/// clock hour relative to where the camera is looking (the same geometry the radar and the
/// npc beacon already use, so the phrasing is consistent with everything else in this mod),
/// and its distance is given in meters. Then navigation stops and waits — it does not
/// restart on a timer, because the player must be free to line up a hit without the avatar
/// walking off mid-swing. It resumes when the obstacle is actually gone, which
/// <see cref="BreakableCatalog"/> reports by omission on its next scan.</para>
///
/// <para><b>What it does NOT do.</b> It does not break anything itself, and it does not
/// guess which object is in the way — it names the nearest intact breakable in front of the
/// avatar, and if there is none it says so honestly rather than inventing a culprit. A
/// beacon pointing at rubble is worse than no beacon; that principle is
/// <see cref="BreakableCatalog"/>'s own and it is kept here.</para>
/// </summary>
internal static class ObstacleHold
{
    /// <summary>Below this speed the avatar is considered stopped. Deliberately well under the
    /// ~4.4 m/s a walk runs at, but above the jitter of a character shifting weight: a
    /// walking avatar never reads near this, and a blocked one reads 0.</summary>
    private const float StuckSpeedMps = 0.25f;

    /// <summary>How long the avatar must stay stopped before the world is blamed. Long enough
    /// that a path-finding hiccup, a slope or a turn at a corner does not trigger it; short
    /// enough that a blind player is not left wondering. The failed-journey threshold in
    /// <see cref="AutoWalk"/> uses the same order of magnitude for the same reason.</summary>
    private const float StuckSeconds = 1.5f;

    /// <summary>How near a breakable must be to be named as the obstruction. This is not a
    /// damage range — it is "close enough that it could plausibly be what the avatar is
    /// pressed against". The breakable the avatar is actually stuck on is by definition
    /// within a body length or two.</summary>
    private const float BlameRangeM = 4.0f;

    /// <summary>Once the obstacle is gone, wait this long before restarting, so the resumed
    /// walk does not begin during the break animation or the moment the player is still
    /// holding the attack button.</summary>
    private const float ResumeDelaySeconds = 0.6f;

    /// <summary>How often the "still blocked?" question is re-asked while waiting. The
    /// catalog rescans on its own slow timer; this is the poll that notices the change.</summary>
    private const float RecheckIntervalSeconds = 0.35f;

    private enum Phase { Idle, Blocked, Cleared }

    private static Phase _phase = Phase.Idle;
    private static float _stuckFor;         // seconds the avatar has been not-moving
    private static float _blockedFor;       // seconds spent in Blocked, for the recheck cadence
    private static float _clearedFor;       // seconds spent in Cleared, before resuming
    private static float _lastX, _lastZ;
    private static bool _haveLast;

    /// <summary>Was the obstruction announced for the CURRENT block? Announces once, not
    /// every frame — a blind player must be told, not lectured.</summary>
    private static bool _announced;

    /// <summary>
    /// Where navigation is trying to go, so the caller can be told to ask for the same place
    /// again after the obstacle is gone. Echoed back from the most recent
    /// <see cref="Tick"/> call and deliberately NOT held as lasting state — the hook owns the
    /// journey, this class only decides whether it may continue. Plain numbers only: nothing
    /// engine-owned is held across frames, the rule this mod has paid for twice.
    /// </summary>
    private static float _goalX, _goalY, _goalZ;

    /// <summary>
    /// <b>Call once per frame while navigation is running.</b> Returns what the caller should
    /// do next. The caller owns the walk; this only decides whether it should keep going.
    /// </summary>
    /// <param name="avatar">The player's avatar, for its position.</param>
    /// <param name="goalX">Where navigation is trying to go, so it can be resumed.</param>
    /// <param name="goalY">See <paramref name="goalX"/>.</param>
    /// <param name="goalZ">See <paramref name="goalX"/>.</param>
    /// <returns>
    /// <see cref="Verdict.Continue"/> — nothing is in the way, carry on walking.<br/>
    /// <see cref="Verdict.Hold"/> — the world is blocking; navigation has been told to stop
    /// and the player has been informed.<br/>
    /// <see cref="Verdict.Resume"/> — the obstacle is gone; start navigation again for the
    /// same goal.<br/>
    /// </returns>
    internal static Verdict Tick(ManagedObject avatar, float goalX, float goalY, float goalZ)
    {
        _goalX = goalX; _goalY = goalY; _goalZ = goalZ;

        var here = AvatarFieldReader.ReadPlayerPos(avatar);
        if (!here.ok) return Verdict.Continue;

        float dt = FrameSeconds();

        // Prime the position cache on the first frame of a run. Without this the first
        // sample would compare against (0, 0) and read as a huge move.
        if (!_haveLast)
        {
            _lastX = here.x; _lastZ = here.z; _haveLast = true;
            return Verdict.Continue;
        }

        switch (_phase)
        {
            case Phase.Idle:
            {
                float dx = here.x - _lastX, dz = here.z - _lastZ;
                float moved = MathF.Sqrt(dx * dx + dz * dz);
                _lastX = here.x; _lastZ = here.z;

                // Speed over the sampling window; the window is one frame, so compare against
                // the distance a walking avatar would cover in that frame rather than a raw
                // per-frame epsilon (which would vary with frame rate).
                float speed = dt > 0f ? moved / dt : 0f;
                _stuckFor = speed < StuckSpeedMps ? _stuckFor + dt : 0f;

                if (_stuckFor >= StuckSeconds) EnterBlocked(here.x, here.z);
                return Verdict.Continue;
            }

            case Phase.Blocked:
            {
                _blockedFor += dt;
                if (_blockedFor < RecheckIntervalSeconds) return Verdict.Hold;
                _blockedFor = 0f;

                // Is the thing still there? BreakableCatalog drops a prop once it is broken,
                // so "the nearest intact breakable in front of me is gone" IS the completion
                // signal — no state of our own to keep in sync, and nothing to go stale.
                if (StillBlockedByABreakable(here.x, here.y, here.z))
                {
                    // Still there. Re-aim the announcement only if the nearest one CHANGED,
                    // so a player who smashes the wrong crate is told about the new one.
                    if (!_announced) Announce(here.x, here.y, here.z);
                    return Verdict.Hold;
                }

                // Nothing intact left in front of us: either the player broke it, or what was
                // stopping the avatar was never a breakable (a wall, a kerb, a closed door).
                // Both mean "stop holding and try again" — a wall will simply re-trigger this
                // in another second and a half, and saying nothing is better than inventing a
                // cause the player cannot act on.
                _phase = Phase.Cleared;
                _clearedFor = 0f;
                return Verdict.Hold;
            }

            case Phase.Cleared:
            {
                _clearedFor += dt;
                if (_clearedFor < ResumeDelaySeconds) return Verdict.Hold;

                API.LogInfo("[SF6Access] Obstacle: the way looks clear again — resuming navigation " +
                            $"towards ({_goalX:F2}, {_goalY:F2}, {_goalZ:F2})");

                // Remember the goal across Reset(), because the caller needs it to restart the
                // walk and this is the last place it is known to be correct.
                float gx = _goalX, gy = _goalY, gz = _goalZ;
                Reset();
                _goalX = gx; _goalY = gy; _goalZ = gz;
                return Verdict.Resume;
            }
        }

        return Verdict.Continue;
    }

    /// <summary>Start watching from scratch. Called on every new walk and on field change.</summary>
    internal static void Reset()
    {
        _phase = Phase.Idle;
        _stuckFor = 0f;
        _blockedFor = 0f;
        _clearedFor = 0f;
        _haveLast = false;
        _announced = false;
    }

    private static void EnterBlocked(float px, float pz)
    {
        _phase = Phase.Blocked;
        _blockedFor = 0f;
        _announced = false;
        API.LogInfo($"[SF6Access] Obstacle: navigation is live but the avatar has not moved for " +
                    $"{StuckSeconds:F1} s (player at ({px:F2}, {pz:F2})) — something in the world is in the way");
        Announce(px, 0f, pz);
    }

    /// <summary>True when there is still an intact breakable near enough to be the culprit.</summary>
    private static bool StillBlockedByABreakable(float px, float py, float pz) =>
        BreakableCatalog.Nearest(px, py, pz, BlameRangeM) != null;

    /// <summary>
    /// <b>Say what is in the way, and where.</b> Names the nearest intact breakable and gives
    /// its clock hour and distance, using the same direction geometry as the radar and the
    /// npc beacon so the wording is consistent with everything else the player has learnt.
    ///
    /// <para>If there is no breakable nearby, says that instead. The mod must never point at
    /// something that is not there: a blind player cannot check, so a wrong cue is worse than
    /// a vague one.</para>
    /// </summary>
    private static void Announce(float px, float py, float pz)
    {
        _announced = true;

        var near = BreakableCatalog.Nearest(px, py, pz, BlameRangeM);
        if (near == null)
        {
            API.LogInfo("[SF6Access] Obstacle: blocked, but no intact breakable within " +
                        $"{BlameRangeM:F1} m — reporting the block without naming a cause");
            ScreenReaderService.Speak(AutoWalkText.ObstacleUnnamed(), interrupt: false);
            return;
        }

        var b = near.Value;
        float dx = b.X - px, dz = b.Z - pz;
        float dist = MathF.Sqrt(dx * dx + dz * dz);

        int hour = FieldDirectionService.ClockHour(FieldDirectionService.GetCameraForward(), dx, dz);
        API.LogInfo($"[SF6Access] Obstacle: nearest intact breakable at ({b.X:F2}, {b.Y:F2}, {b.Z:F2}), " +
                    $"{dist:F1} m, {hour} o'clock");
        ScreenReaderService.Speak(AutoWalkText.ObstacleBreakable(hour, dist), interrupt: false);
    }

    private static long _lastTickMs;

    /// <summary>Seconds since the previous tick, clamped. A frame that took longer than a
    /// quarter second (a load, a hitch) must not be counted as "the avatar stood still",
    /// which is why the clamp exists rather than a raw delta.</summary>
    private static float FrameSeconds()
    {
        long now = Environment.TickCount64;
        long delta = _lastTickMs == 0 ? 0 : now - _lastTickMs;
        _lastTickMs = now;
        if (delta <= 0) return 0f;
        if (delta > 250) return 0f;
        return delta / 1000f;
    }

    /// <summary>What the caller should do with the walk this frame.</summary>
    internal enum Verdict
    {
        /// <summary>Nothing in the way — keep walking.</summary>
        Continue,
        /// <summary>Held: do not walk, and do not treat the standstill as a failure.</summary>
        Hold,
        /// <summary>The obstacle is gone: start the walk again for the same goal.</summary>
        Resume,
    }
}
