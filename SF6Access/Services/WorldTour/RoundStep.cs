using System;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>The pure half of the way ROUND: how far one hop of that way can be.</b>
///
/// <para><b>The problem.</b> The whole reason <see cref="RoundPlanner"/> exists is that the
/// navmesh and the world disagree at a hoarding: the engine answers "reachable" about a target the
/// player cannot walk to, or "no route" about one three metres away round a corner. So the plan is
/// made out of the world instead — with rays — and every hop of it is a question a ray has already
/// answered.</para>
///
/// <para><b>What one hop is.</b> A ray leaves a point the player can stand on, low enough to be a
/// body and aimed roughly at the objective. How far it gets before something stops it is how far a
/// body can go that way. The hop then stops SHORT of that by <c>clearM</c>, because a body is not a
/// line: arriving with its centre on the contact means arriving inside the wall.</para>
///
/// <para><b>Three ways to refuse, all of them "0".</b> Something closer than
/// <c>clearM + minM</c> ahead is not a hop at all — it is the wall the player is already
/// touching. A ray that never ran says NOTHING here either; this function is handed a distance
/// only when the sensor reported one, and <see cref="RoundPlanner"/> is the place that enforces
/// "not measured" never becoming "open".</para>
///
/// <para><b>Kept apart from <see cref="RoundPlanner"/> so it can be tested</b> without a game, an
/// avatar or a single engine type — the reason this file exists at all.</para>
/// </summary>
internal static class RoundStep
{
    /// <summary>How far one hop may ever be. Two metres keeps every hop short enough that the ray
    /// measuring it is still asking about the ground it is standing on, not about what is round
    /// three corners — and it is the length of one stride-and-a-half, which is what a blind player
    /// can be asked to walk without a turn.</summary>
    internal const float HopM = 2f;

    /// <summary>How much space past the END of a hop the ray must still have found open. The avatar
    /// is a capsule about this wide; stopping its CENTRE one clearance from a contact leaves its
    /// flank brushing nothing.</summary>
    internal const float ClearM = 0.45f;

    /// <summary>Below this, a hop is not worth walking: they add up to standing still and turning
    /// every half metre, which is not a route anyone can follow.</summary>
    internal const float MinHopM = 0.8f;

    /// <summary>The squeezed pair, used only when the ordinary one has refused — see
    /// <see cref="SqueezeClearM"/>. Both live here rather than in the planner so that the shape of
    /// "how short a step still counts" can be tested without a game.</summary>
    internal const float SqueezeClearM = 0.25f;

    /// <summary>The shortest hop worth walking when nothing ordinary is available.</summary>
    internal const float SqueezeMinM = 0.35f;

    /// <summary>The farthest a hop's measuring ray is ever sent: the longest hop plus the clearance
    /// past it. This is what makes one cast answer the whole question.</summary>
    internal const float ReachM = HopM + ClearM;

    /// <summary><b>How long a hop along this direction is, or 0 when there is none.</b></summary>
    /// <param name="blocked">Whether the ray met anything at all within <paramref name="reach"/>.</param>
    /// <param name="hitDistance">Where it met the nearest thing, in metres from the origin.</param>
    /// <param name="reach">How far it was sent.</param>
    /// <param name="hopM">The longest hop wanted.</param>
    /// <param name="clearM">Clearance kept past the far end.</param>
    /// <param name="minM">Below this it is not a hop.</param>
    internal static float LengthM(bool blocked, float hitDistance, float reach,
                                  float hopM, float clearM, float minM)
    {
        if (!(reach > 0f)) return 0f;
        // An unreadable contact distance is not an absence of contact. This is the rule every
        // sensor in this mod keeps: "not measured" must never reach a caller as "the way is open".
        if (blocked && !float.IsFinite(hitDistance)) return 0f;
        // A ray that met nothing travelled the whole way it was sent. Anything shorter than the
        // ray's own reach must be obeyed: it is nearer than what we asked for, so it is real.
        float open = blocked && hitDistance > 0f ? MathF.Min(hitDistance, reach) : reach;
        float len = open - clearM;
        if (!(len >= minM)) return 0f;
        return MathF.Min(len, hopM);
    }

    /// <summary>Whether a hop of <paramref name="len"/> metres reaches the objective's immediate
    /// neighbourhood: the point a route hands over to the player's own hands.</summary>
    internal static bool IsArrival(float len, float gapM) => len > 0f && len <= gapM;

    /// <summary>Whether a ray of <paramref name="toTarget"/> metres towards the objective got
    /// close enough to call the route finished. <paramref name="blocked"/> with anything less is a
    /// wall still standing between; unblocked is open ground all the way to it.</summary>
    internal static bool Reaches(bool blocked, float hitDistance, float toTarget, float gapM)
    {
        if (!(toTarget > gapM)) return true;       // already on top of it
        if (!blocked || hitDistance <= 0f) return true;
        return hitDistance >= toTarget - gapM;
    }
}
