using System;
using System.Collections.Generic;
using System.Globalization;
using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

public static partial class FieldNavPath
{
    // ---------- reading the answer ----------

    /// <summary><c>DistanceType.Raw</c> / <c>.Path</c> straight out of the TDB, once —
    /// never the literals 0 and 1.</summary>
    private static void EnsureDistanceIds()
    {
        if (_distanceIdsTried) return;
        _distanceIdsTried = true;
        _rawId = FieldProbeService.EnumValue(DISTANCE_ENUM, "Raw");
        _pathId = FieldProbeService.EnumValue(DISTANCE_ENUM, "Path");
    }

    /// <summary>One <c>PathInfo.calcDistance</c> reading in metres.
    /// <paramref name="orUntyped"/> allows the no-argument overload (<c>PathInfo.cs:289</c>)
    /// as a fallback — right for the walked length, wrong for the straight-line one, which
    /// returns 0 rather than a number that is not what it claims to be.</summary>
    private static float Distance(ManagedObject info, int distanceId, bool orUntyped)
    {
        var td = info.GetTypeDefinition();
        _calcDistanceTyped ??= Find(td, "calcDistance",
            m => ParamCount(m) == 1 && m.GetParameters()[0].Type?.IsValueType() == true);
        _calcDistance ??= Find(td, "calcDistance", m => ParamCount(m) == 0);

        if (distanceId >= 0 && _calcDistanceTyped != null)
            return FieldProbeService.ToFloat(
                _calcDistanceTyped.InvokeBoxed(typeof(float), info, new object[] { distanceId }));
        if (!orUntyped || _calcDistance == null) return 0f;
        return FieldProbeService.ToFloat(_calcDistance.InvokeBoxed(typeof(float), info, null));
    }

    /// <summary>Read the first straight run after string pulling; preserve close bends.</summary>
    private static bool AimPoint(ManagedObject info, int points, float px, float py, float pz,
                                 float minAwayM, out float x, out float y, out float z, out int turn)
    {
        x = y = z = 0f;
        turn = 0;
        if (points < 1) return false;
        _getPathPointInfo ??= Find(info.GetTypeDefinition(), "getPathPointInfo",
            m => ParamCount(m) == 1 && m.GetParameters()[0].Type?.IsValueType() == true);
        if (_getPathPointInfo == null) return false;
        var aim = new RouteLookAhead(px, pz, minAwayM);
        // A malformed or very dense path must not cause an unbounded wrapper walk.
        const int MaxPointsRead = 64;
        for (uint i = 0; i < (uint)Math.Min(points, MaxPointsRead); i++)
        {
            var point = _getPathPointInfo.InvokeBoxed(typeof(object), info, new object[] { i }) as ManagedObject;
            if (point == null) break;
            if (FieldProbeService.Member(point, "IsSkiped", typeof(bool)) is true) continue;
            var pos = FieldProbeService.Member(point, "PortalPos");
            if (pos == null) break;
            if (!aim.Add(FlowHelper.ReadVecComponent(pos, "x"), FlowHelper.ReadVecComponent(pos, "y"),
                         FlowHelper.ReadVecComponent(pos, "z"))) break;
        }
        x = aim.X; y = aim.Y; z = aim.Z;
        turn = aim.Turn;
        return aim.HasPoint;
    }

    /// <summary>A whole route, as the engine's own pathfinder laid it out.
    /// <see cref="Points"/> is the number of portal points the corridor had,
    /// <see cref="Corners"/> the ones that survived string pulling and are therefore worth
    /// steering through, and <see cref="Ok"/> says whether the list is trustworthy at all.</summary>
    public readonly struct RouteShape
    {
        public readonly bool Ok;
        public readonly int Points;
        public readonly (float X, float Y, float Z)[] Corners;
        public readonly (float X, float Y, float Z) Start, End;
        /// <summary>What the engine said about this destination. <c>Unknown</c> when no answer
        /// was obtained — which is NOT the same as walkable and NOT the same as blocked.
        /// This is the field a walk must consult before it starts: a <c>Position</c> request
        /// aimed at an <c>Unreachable</c> point makes the avatar press into whatever is between
        /// it and the destination, because the engine has no route to follow and was not asked
        /// for one.</summary>
        public readonly Reachability Reach;
        internal RouteShape(bool ok, int points, (float, float, float)[] corners,
                            (float, float, float) start, (float, float, float) end,
                            Reachability reach = Reachability.Unknown)
        { Ok = ok; Points = points; Corners = corners; Start = start; End = end; Reach = reach; }
    }

    /// <summary><b>Read EVERY portal point of a route, in order — the data a hand-built
    /// <c>NaviRequestData.RoutePos</c> array needs.</b>
    ///
    /// <para><see cref="AimPoint"/> walks the same list but stops at the first real bend and
    /// returns a single point, because one point is what a homing cue needs. Handing a route to
    /// the engine is the opposite problem: it needs the bends themselves, in order, or it walks
    /// straight at the destination and pushes against whatever is in the way. The two readers
    /// share <c>getPathPointInfo</c> and the <c>IsSkiped</c> filter, so they cannot disagree
    /// about which points exist.</para>
    ///
    /// <para><b>Nothing here moves the avatar or the camera.</b> It reads the <c>PathInfo</c>
    /// the query already produced; the caller writes its own log and decides what to do with the
    /// list. That split exists so "is the data right?" can be answered before "does the engine
    /// accept it?" is asked — the mistake this project has paid for three times.</para>
    ///
    /// <para><c>Ok == false</c> means the list must not be used; a caller must never treat an
    /// empty corner list as "a straight route".</para></summary>
    internal static RouteShape ReadRoute(ManagedObject info, float px, float py, float pz)
    {
        var empty = new (float, float, float)[0];
        var start = (px, py, pz);
        if (info == null) return new RouteShape(false, 0, empty, start, start);
        try
        {
            _getPathPointInfo ??= Find(info.GetTypeDefinition(), "getPathPointInfo",
                m => ParamCount(m) == 1 && m.GetParameters()[0].Type?.IsValueType() == true);
            if (_getPathPointInfo == null) return new RouteShape(false, 0, empty, start, start);

            var nObj = FieldProbeService.Member(info, "PathPointCount", typeof(uint));
            int points = nObj == null ? 0 : (int)Convert.ToUInt32(nObj);
            if (points <= 0) return new RouteShape(true, 0, empty, start, start);

            // Same 64-point bound AimPoint uses: a dense or malformed corridor must not walk
            // an unbounded number of engine wrappers.
            const int MaxPointsRead = 64;
            var list = new List<(float, float, float)>(Math.Min(points, MaxPointsRead));
            for (uint i = 0; i < (uint)Math.Min(points, MaxPointsRead); i++)
            {
                var point = _getPathPointInfo.InvokeBoxed(typeof(object), info, new object[] { i }) as ManagedObject;
                if (point == null) break;
                if (FieldProbeService.Member(point, "IsSkiped", typeof(bool)) is true) continue;
                var pos = FieldProbeService.Member(point, "PortalPos");
                if (pos == null) break;
                float x = FlowHelper.ReadVecComponent(pos, "x");
                float y = FlowHelper.ReadVecComponent(pos, "y");
                float z = FlowHelper.ReadVecComponent(pos, "z");
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) break;
                list.Add((x, y, z));
            }
            var corners = list.ToArray();
            var end = corners.Length > 0 ? corners[corners.Length - 1] : start;
            return new RouteShape(true, points, corners, start, end);
        }
        catch (Exception ex)
        {
            Fail($"ReadRoute: {ex.GetType().Name}: {ex.Message}");
            return new RouteShape(false, 0, empty, start, start);
        }
    }


    /// <summary>Pull the route's string: turn the corridor of portals the query
    /// produced into an optimised line. Bound by SHAPE (zero parameters) and matched
    /// exactly by name; <c>void</c> return, nothing by reference, nothing constructed.
    /// A failure to bind is not fatal — the aim then reads raw portal positions, which
    /// is what it did before and is merely worse, not dangerous.</summary>
    private static void TraceLine(ManagedObject info)
    {
        try
        {
            _createTraceLine ??= Find(info.GetTypeDefinition(), "createTraceLine", m => ParamCount(m) == 0);
            if (_createTraceLine == null)
            {
                Step(5, "createTraceLine() not found; reading RAW portal positions instead");
                return;
            }
            Step(5, "calling PathInfo.createTraceLine() - string-pulling the corridor into a route");
            _createTraceLine.InvokeBoxed(typeof(object), info, null);
        }
        catch (Exception ex) { Fail($"createTraceLine: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>The radius of the navmesh agent the query ran on
    /// (<c>NavigationSurface.AgentRad</c>) — the engine's own answer to "how much room
    /// does this body need". Read once and kept: the agent is a borrowed NPC and so may
    /// differ between queries, but it is used only as the "is this corner just my own feet"
    /// threshold, where any human-sized body gives the same answer. Zero when unreadable, which makes the
    /// corner test fall back to "any point that is not exactly the player".</summary>
    private static float AgentRadius(ManagedObject surface)
    {
        if (_agentRadius >= 0f) return _agentRadius;
        _agentRadius = FieldProbeService.ToFloat(FieldProbeService.Member(surface, "AgentRad", typeof(float)));
        if (!float.IsFinite(_agentRadius) || _agentRadius < 0f) _agentRadius = 0f;
        return _agentRadius;
    }

    private static bool Write(FieldOutBuffer buf, float x, float y, float z)
    {
        if (!buf.SetComponent("x", x) || !buf.SetComponent("y", y) || !buf.SetComponent("z", z)) return false;
        // Read back through the same field metadata the engine uses: evidence the value
        // landed where native code looks for it, not a half-filled struct.
        return buf.Component("x") == x && buf.Component("y") == y && buf.Component("z") == z;
    }

}
