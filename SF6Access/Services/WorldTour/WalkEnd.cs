namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>Is the walk over, or is there still a stand to walk to? — the one precedence question the
/// beacon's sentence depends on.</b> Pulled out of <see cref="RouteNotice"/> so it can be proved
/// without a running game, because getting it wrong is not a cosmetic fault: it makes the guide
/// announce a failure that is not a failure.
///
/// <para><b>Why this exists (owner, 2026-10-05).</b> The objective was a mission NPC on a platform
/// 6.07 m in front of the player — no storey change, no ladder needed, a straight walk. The ladder
/// search correctly found nothing and the beacon said <i>"没有找到能到达目的地的梯子"</i> over the
/// bearing. Five seconds later the measured approach point arrived and the guide said the true
/// thing: <i>"任务目标的脚下，在12点钟方向，6米外"</i>. The player hears the alarm first and the
/// answer second, which reads as a failure that had to be patched up afterwards.</para>
///
/// <para><b>The rule.</b> "No route" is only honest when there is nothing at all to walk to — no
/// aim point, no foot, no nearest reachable stand. The moment any of those exists, a stand the
/// player CAN walk to is known, the walk is not over, and no sentence may call it over. This is an
/// ordering rule and nothing else: it adds no measurement and suppresses no sound. The bearing and
/// the homing cue carry on exactly as before, as they must — <i>"没有雷达让我怎么找"</i>.</para>
/// </summary>
internal static class WalkEnd
{
    /// <summary>True only when the walk has genuinely run out of things to walk to. Any known
    /// stand — an approach point, the foot of the objective, the nearest reachable place —
    /// outranks "no route", because a stand that can be reached IS a route.</summary>
    internal static bool WalkIsOver(bool noRoute, bool isApproach, bool atFoot, bool closest)
        => noRoute && !isApproach && !atFoot && !closest;

    /// <summary>The sentence key this decision yields. "no-route" is the alarm that says no ladder
    /// reaches the destination; "approach" and "closest" are the ordinary guidance. Kept beside the
    /// rule so the two can never drift apart in the tests.</summary>
    internal static string Key(bool noRoute, bool isApproach, bool atFoot, bool closest)
        => WalkIsOver(noRoute, isApproach, atFoot, closest) ? "no-route"
         : closest ? "closest"
         : isApproach ? "approach"
         : "route";
}
