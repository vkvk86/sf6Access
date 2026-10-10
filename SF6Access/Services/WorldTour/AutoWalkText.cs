namespace SF6Access.Services.WorldTour;

/// <summary>Spoken lines for auto-walk. Keys live under <c>wt.walk.*</c> so they sit
/// beside <c>wt.route.*</c> (the spoken route states) without colliding with them.</summary>
internal static class AutoWalkText
{
    /// <summary>Said the moment the player's own stick input cancels the walk. The
    /// player must never wonder why the avatar stopped following the route.</summary>
    internal static string PlayerTookOver() =>
        Get("player_took_over", "Auto walk off. You have the controls");

    /// <summary>Said when auto-walk could not start (the game object was not there).</summary>
    internal static string Unavailable() =>
        Get("unavailable", "Auto walk is not available here");

    /// <summary>Said once when auto-walk is driving, so the player knows the mod has
    /// the avatar and can take it back at any time.</summary>
    internal static string Started() =>
        Get("started", "Auto walk started. Press any direction to take back control");

    /// <summary>Said when the player presses the walk key again while it is already
    /// walking: the second press is a request to stop, not to stack a second walk.</summary>
    internal static string Stopped() =>
        Get("stopped", "Auto walk stopped");

    /// <summary>
    /// <b>Said when navigation is live but the world is stopping the avatar, and there is a
    /// breakable prop in front of it.</b> The clock hour and the distance are given because a
    /// blind player cannot see the crate: knowing it is at 2 o'clock and 2.3 metres away is
    /// the difference between acting and guessing. Wording follows the radar and the npc
    /// beacon, so it is the same language the player already knows.
    /// </summary>
    internal static string ObstacleBreakable(int hour, float metres) =>
        Get("obstacle_breakable",
            $"Something is blocking the way. A breakable object is at {hour} o'clock, {metres:F1} meters. " +
            "Break it and the walk continues by itself");

    /// <summary>
    /// <b>Said when navigation is stuck and no breakable is near enough to blame.</b> The
    /// mod does not invent a culprit: naming the wrong object would send the player to smash
    /// something that is not in the way, and a blind player has no way to notice.
    /// </summary>
    internal static string ObstacleUnnamed() =>
        Get("obstacle_unnamed",
            "Something is blocking the way and it does not look breakable. " +
            "Find another route, or press the walk key again later");

    /// <summary>
    /// <b>Said instead of starting a walk when the game itself says the destination has no
    /// walkable way to it.</b> The engine's verdict is <c>PathNotFound</c>: the point is on the
    /// navmesh, and no walk connects it to where the player is standing — an upper floor, a roof,
    /// a room behind a door or a scripted transition. This is not an obstacle to break and not a
    /// route to steer around, so the walk must not start; the player needs the way IN.
    ///
    /// <para><paramref name="riseM"/> is how much HIGHER the destination is. When it is positive
    /// and worth mentioning, the player is told, because "up about twelve meters" is the fact that
    /// makes the situation legible — it says look for stairs or a ladder rather than keep walking.</para>
    /// </summary>
    internal static string DestinationUnreachable(float riseM) =>
        riseM >= 2f
            ? Get("unreachable_above",
                  $"The destination has no walkable way from here — it is about {riseM:F0} meters above you. " +
                  "Look for stairs or a ladder")
            : Get("unreachable",
                  "The destination has no walkable way from here. Look for another route");

    /// <summary>
    /// <b>Said when the destination point itself is not on the navmesh</b> (<c>DestNodeNotFound</c>) —
    /// a rooftop, or a point inside geometry. Distinct from
    /// <see cref="DestinationUnreachable"/> because the two are different facts and the honest
    /// wording differs: one has no route, the other is not a place you can stand at all.
    /// </summary>
    internal static string DestinationOffMesh() =>
        Get("off_mesh",
            "The destination is not somewhere the game can walk to at all. Try selecting a nearby place instead");

    /// <summary>
    /// <b>Said when the PLAYER is not on the navmesh</b> (<c>StartNodeNotFound</c>) — mid-jump, on a
    /// ladder, or mid-transition. Nothing can be routed until they are back on solid ground, and
    /// saying so is better than a walk that silently does nothing.
    /// </summary>
    internal static string PlayerOffMesh() =>
        Get("player_off_mesh",
            "Cannot plan a walk while you are off the walkable ground. Stand still for a moment and try again");

    /// <summary>
    /// <b>The single refusal the walk makes, and the reason it is one method with branches rather
    /// than four messages.</b> Every refusal comes from <c>BeaconDestination.TryStep</c> saying
    /// there is no point worth walking to, and the branches here are ordered by what the player
    /// should DO about it — because a blind player cannot see which of these happened:
    ///
    /// <list type="number">
    /// <item><b>A ladder is the way, but only part of it works.</b> The ladder planner found the
    /// climb but its top is unreachable, or it found no usable ladder at all. The player is told
    /// the objective is above and that a ladder is involved, which is the actionable fact.</item>
    /// <item><b>The objective is simply higher.</b> No ladder plan; the point is up an upper floor
    /// or a roof. Same advice as the fifteenth finding's wording.</item>
    /// <item><b>Plain no route.</b> Nothing distinguishes it, so nothing is invented.</item>
    /// </list>
    ///
    /// <para><b>Why "still searching" is a refusal and not a start.</b> While the ladder search
    /// runs there is no aim yet, and starting a straight-line walk at a ladder foot the planner has
    /// not blessed is exactly the 15:20 failure — the avatar walked to a dead-end ladder and
    /// pressed into it. Saying "still looking" is honest and the player can press again.</para>
    /// </summary>
    internal static string CannotWalkThere(BeaconDestination dest, float riseM)
    {
        if (dest.Searching)
            return Get("still_searching",
                "Still working out a route — the game is searching for a ladder. Try again in a moment");

        if (dest.Ladder || dest.Partial || dest.Blocked)
            return riseM >= 2f
                ? Get("ladder_partial_above",
                      $"A ladder is the way up, but the route above it does not reach the destination. " +
                      $"The objective is about {riseM:F0} meters above you. Climb what you can and look for the next way up")
                : Get("ladder_partial",
                      "A ladder is the way, but the route above it does not reach the destination. " +
                      "Climb what you can and look for the next way up");

        return DestinationUnreachable(riseM);
    }

    /// <summary>
    /// <b>The two halves of the trial's one real question: is the engine's own pathfinder moving
    /// the avatar, or is it accepting the call and doing nothing?</b> This is not decoration — every
    /// other actuator in this feature's history (SetChaseWalk, SetSlaveMoveVec) was accepted and
    /// ignored, so "the call returned" has never been evidence of anything. These are said once,
    /// about two and a half seconds after the request, and they are the difference between a
    /// feature that works and a feature that only looks like it started.
    /// </summary>
    internal static string EngineWalked(float metres) =>
        Get("engine_walked",
            $"Engine navigation is moving you. About {metres:F0} meters so far");

    /// <summary>The other half — and the one worth knowing early, because it sends the next reader
    /// to the request's contents rather than to the avatar's behaviour.</summary>
    internal static string EngineIgnored() =>
        Get("engine_ignored",
            "The game accepted the navigation request but the character did not move. Report this — the request contents are wrong");

    /// <summary>
    /// <b>Said when the avatar walked, but walked the requested line backwards.</b> Three outcomes
    /// are now distinguishable and none of them may share wording with another: no movement at all
    /// (<see cref="EngineIgnored"/>), movement in the wrong direction (this), and movement toward
    /// the target (<see cref="EngineWalked"/>). A reverse walk is not a lesser failure than no
    /// walk — it is the signature of a point being read as a vector, and saying so is what tells
    /// the next attempt which of the two encodings to use.
    /// </summary>
    internal static string WalkedBackwards() =>
        Get("walked_backwards",
            "The character walked the right route but in the opposite direction. Trying the other coordinate encoding — press the walk key again");

    /// <summary>
    /// <b>Said when F12 is pressed after the StartNavi trial was retired.</b> The trial crashed
    /// the game 30 ms after a clean store on 2026-09-29 21:19 (the twenty-seventh finding), and
    /// the feature was stopped the same hour in favour of the read-only spoken guidance. The
    /// player must never be met with silence: the key answers, says what it no longer does, and
    /// points at what replaced it.
    /// </summary>
    internal static string TrialRetired() =>
        Get("trial_retired",
            "Auto navigation is switched off. Use the radar guidance: follow the sound and the spoken directions");

    private static string Get(string key, string fallback) => LangFile.Get("wt.walk." + key, fallback);
}
