using System;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>The way ROUND a wall: how far the obstruction is, and which side of it is open.</b>
///
/// <para><b>The hole this fills.</b> The objective can be two metres away in a straight line and
/// unreachable, with a hoarding between that no map models and no query reports — the engine
/// answers <c>Exist=True</c> because at two metres both ends snap onto the same corner of the
/// mesh, so the guide says "near the objective" and the player pushes a wall (2026-10-03,
/// 13:34–13:35, the SiRN building site: 任务目标 2 米, 交互未确认, and a front beam blocked at
/// 0.35 m, for a minute and a half). "Follow the wall round" is true advice and still leaves a
/// blind player guessing which way the wall runs and whether either end is open.</para>
///
/// <para><b>What this measures.</b> Five rays out from the player, fanned around the straight
/// line to the objective: the line itself, then 45° and 90° to each side of it. The straight
/// ray meeting geometry well short of the objective is the fact "a wall stands between" — with
/// the distance, which is the part that stops a player pushing. The side rays are the way round:
/// the one with open ground in front of it, nearest the objective's bearing first because that is
/// the way round that costs the least walking.</para>
///
/// <para><b>Silence when there is no way round.</b> Both sides blocked is answered with "no side
/// found" and the caller says what it said before. Claiming an opening that is not there is
/// worse than admitting the wall runs further than the rays can see.</para>
///
/// <para><b>Only ever asked when the objective is already near and the game has refused the
/// interaction.</b> Nothing here changes a bearing, a cue or a route: it adds a sentence.</para>
/// </summary>
internal static class WayRound
{
    /// <summary>The rays: the line to the objective, then 45° and 90° to each side. Positive is
    /// to the RIGHT of that line, the handedness this mod confirmed in game.</summary>
    private static readonly float[] AngleDeg = { 0f, 45f, -45f, 90f, -90f };

    /// <summary>How much nearer than the objective the straight ray must be blocked for it to
    /// count as a wall standing between. Two metres to the objective and a contact at 1.8 m is
    /// the objective's own plinth; 0.35 m is a hoarding.</summary>
    private const float WallMarginM = 0.8f;

    /// <summary>A side only counts as the way round when it is genuinely more open than the
    /// line being blocked — a few centimetres is the same wall seen at a slant.</summary>
    private const float MinGainM = 1.5f;

    /// <summary>Re-probe at most this often: the rays are cheap but not free, and a wall does
    /// not move while the player walks along it.</summary>
    private const long IntervalMs = 400;

    private static readonly FieldDirectionService.FlatDir[] Dirs = new FieldDirectionService.FlatDir[AngleDeg.Length];
    private static readonly RayHit[] Hits = new RayHit[AngleDeg.Length * FieldRayCaster.BODY_HEIGHTS];
    private static readonly float[] Clear = new float[AngleDeg.Length];

    private static long _nextAt;
    private static bool _ok, _wall;
    private static float _frontM, _sideM;
    private static int _side;

    /// <summary>A measured wall stands between the player and the objective.</summary>
    internal static bool HasWall => _ok && _wall;

    /// <summary>Metres from the player to whatever blocks the straight line.</summary>
    internal static float FrontM => _frontM;

    /// <summary>Metres of open ground along the way round that was found.</summary>
    internal static float SideM => _sideM;

    /// <summary>The way round lies to the RIGHT of the line to the objective, not the left.</summary>
    internal static bool SideIsRight => _side > 0;

    /// <summary>How far round from the line to the objective the way round is, in degrees.</summary>
    internal static float SideDegrees => Math.Abs(_side > 0 ? AngleDeg[_side] : AngleDeg[-_side]);

    /// <summary><b>Measure the fan once, then answer from the measurement.</b> Throttled, and it
    /// never throws: a failed cast is "not measured", which no caller may read as "clear".</summary>
    internal static void Probe(float px, float pz, float objX, float objZ)
    {
        long now = Environment.TickCount64;
        if (now < _nextAt) return;
        _nextAt = now + IntervalMs;

        float bx = objX - px, bz = objZ - pz;
        float dist = MathF.Sqrt(bx * bx + bz * bz);
        _ok = false;
        _wall = false;
        if (!(dist > 0.5f)) return;

        float ux = bx / dist, uz = bz / dist;
        float rx = -uz, rz = ux; // rightward = forward x up, confirmed in game
        for (int i = 0; i < AngleDeg.Length; i++)
        {
            double rad = AngleDeg[i] * Math.PI / 180.0;
            float c = (float)Math.Cos(rad), s = (float)Math.Sin(rad);
            Dirs[i] = new FieldDirectionService.FlatDir(ux * c + rx * s, uz * c + rz * s, true);
        }

        if (!FieldRayCaster.TryCastBodyStack(Dirs, FieldRadarTuning.ReachM, Hits)) return;

        for (int i = 0; i < AngleDeg.Length; i++)
        {
            // The WAIST ray is the one the radar trusts for a wall: the knee and shoulder rays
            // exist to tell a kerb from a railing, and a low sill is not what blocks a walk.
            ref readonly RayHit waist = ref Hits[i * FieldRayCaster.BODY_HEIGHTS + 1];
            Clear[i] = waist.Hit && waist.Distance > 0f ? waist.Distance : FieldRadarTuning.ReachM;
        }

        _ok = true;
        _frontM = Clear[0];
        _wall = _frontM < dist - WallMarginM;
        _side = 0;
        _sideM = 0f;
        if (!_wall) return;

        // The MOST OPEN side, nearest the objective's bearing first: 45° before 90°, because the
        // way round that costs the least walking is the one worth naming.
        for (int i = 1; i < AngleDeg.Length; i++)
        {
            if (Clear[i] < _frontM + MinGainM) continue;
            if (_side != 0 && Clear[i] <= _sideM) continue;
            _side = AngleDeg[i] > 0 ? i : -i;
            _sideM = Clear[i];
        }
    }

    /// <summary>Forget the last measurement: a new objective, or another field.</summary>
    internal static void Reset()
    {
        _ok = _wall = false;
        _frontM = _sideM = 0f;
        _side = 0;
        _nextAt = 0;
    }
}
