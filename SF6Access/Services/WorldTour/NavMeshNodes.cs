using System;
using System.Collections.Generic;
using REFrameworkNET;
using SF6Access.Services;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// Every ENGINE call the navmesh reader makes, one per method, each printing what it
/// is about to do BEFORE it does it. <see cref="NavMeshOpenings"/> owns the geometry,
/// the cache and the spoken answer; this file owns the calls that can kill the
/// process.
///
/// <para><b>Why the numbered trace exists.</b> The first in-game press (2026-09-08)
/// took SF6 down inside this route and left a CLEAN log: the last mod line was the
/// radar sample that runs immediately before it, and no navmesh line was ever
/// written. A native fault - an access violation inside the engine, or one raised on
/// the GC finalizer thread by a bogus managed wrapper, the failure this mod has
/// already met once (see <c>docs/sf6-architecture.md</c>, "Never CreateInstance an
/// interface") - is NOT a .NET exception and walks straight through every
/// <c>try/catch</c> in this codebase. The only way to learn WHICH call died is to
/// have said so in the log first. Each step below logs, then calls; the last
/// <c>NavMesh step</c> line in <c>re2_framework_log.txt</c> names the call that never
/// returned.</para>
///
/// <para><b>What is proven and what is not.</b> Proven by the crashed session's own
/// log: the singleton/field route down to <c>CityAIMap</c>, <c>findMapHandle()</c>
/// (2026-09-04), and <see cref="FieldOutBuffer"/> + its <c>NativeObject</c> view as an
/// engine call argument - the radar ran nine <c>GetCastRayPosition(CastRayTypes, ref
/// vec3, ref vec3)</c> casts in the same second the game died. NOT proven: anything
/// from <c>queryClosestNode(vec3)</c> onwards; nothing in this mod had ever queried a
/// navmesh node. Every failure path BEFORE that call writes a log line, and none was
/// written, so <see cref="ClosestNode"/> is the first call that can have killed it and
/// the prime suspect.</para>
///
/// <para><b>Never</b> call <c>MapHandleBase.queryNode()</c> with no argument: it is an
/// unbounded city-wide query and stalls the game (<c>docs/sf6-screens.md</c> § 6).
/// <see cref="ClosestNode"/> binds its method BY SHAPE - the name plus exactly one
/// parameter whose type is <c>via.vec3</c> - so it can never fall onto that overload
/// nor onto <c>queryClosestNode(vec3, NodeQueryInfo)</c>.</para>
/// </summary>
public static class NavMeshNodes
{
    // ---- the confirmed route, named rather than spelled out at each use ----
    private const string WT_COMMON = "app.global.WTCommon";
    private const string QUERY_CLOSEST_NODE = "queryClosestNode";

    /// <summary>Picks the SAFE one-argument <c>queryClosestNode(vec3)</c> apart from
    /// the <c>(vec3, NodeQueryInfo)</c> overload by shape, the same way the radar picks
    /// its cast overloads - a by-name call could bind either.</summary>
    private const string VEC3_SUFFIX = "vec3";

    /// <summary>Sanity caps on how much of one node is walked per query, in the spirit
    /// of <c>AvatarFieldReader</c>'s avatar cap: a navmesh polygon is a triangle or a
    /// small convex polygon and a node has a handful of links, so anything past these
    /// is a bad read, and must not become an unbounded loop in a method polled several
    /// times a second.</summary>
    public const int MAX_LINKS = 16;
    private const int MAX_VERTICES = 16;

    /// <summary>What bound and what did not, in one line, for the log and for a
    /// diagnostic readout.</summary>
    public static string Route { get; private set; } = "not probed yet";

    // ---- the only survivors between calls: TDB metadata and unmanaged memory ----
    private static readonly Dictionary<string, Method> QueryByHandleType = new();
    private static FieldOutBuffer _posArg;
    private static bool _posArgTried;
    private static bool _tracing = true;
    private static bool _failLogged;

    /// <summary>Drop everything derived from the current city; the unmanaged argument
    /// buffer stays, because unmanaged memory does not move and there is nothing about
    /// it that a city load can invalidate.</summary>
    public static void Reset()
    {
        QueryByHandleType.Clear();
        Route = "not probed yet";
        _failLogged = false;
        _tracing = true;
    }

    /// <summary>Say what is about to be called, BEFORE calling it. Tracing stops once
    /// one query has run all the way to its summary (<see cref="Complete"/>): a crash
    /// hunt needs the first run spelled out, and a navmesh that works must not write
    /// six lines per press into a log the player reads after a session.</summary>
    public static void Step(int number, string what)
    {
        if (!_tracing) return;
        API.LogInfo($"[SF6Access] NavMesh step {number}: {what}");
    }

    /// <summary>A query reached its summary line, so every step in it returned: stop
    /// tracing until the next <see cref="Reset"/>.</summary>
    public static void Complete() => _tracing = false;

    /// <summary>Record a failure and say it ONCE. A navmesh that is not there must not
    /// write a line per query into a log the player reads after a session.</summary>
    public static void Fail(string reason)
    {
        Route = "unavailable: " + reason;
        if (_failLogged) return;
        _failLogged = true;
        API.LogWarning("[SF6Access] NavMesh openings " + Route);
    }

    // ---------- step 1: the handle ----------

    /// <summary>The live <c>via.navigation.MapHandle</c>, re-resolved every rebuild.
    /// Each link of the chain is named in the failure text, so a null says WHICH link
    /// broke rather than just "navmesh unavailable". Confirmed in game 2026-09-04.</summary>
    public static ManagedObject FindHandle()
    {
        Step(1, $"resolving the map handle ({WT_COMMON}.CityResource.CityAIMap.findMapHandle())");
        var common = API.GetManagedSingleton(WT_COMMON) as ManagedObject;
        if (common == null) { Fail($"{WT_COMMON} singleton missing"); return null; }
        var res = FieldProbeService.Member(common, "CityResource") as ManagedObject;
        if (res == null) { Fail("WTCommon.CityResource null (not in a city?)"); return null; }
        var aiMap = FieldProbeService.Member(res, "CityAIMap") as ManagedObject;
        if (aiMap == null) { Fail("WTCityResources.CityAIMap null"); return null; }
        var handle = FlowHelper.Call(aiMap, "findMapHandle") as ManagedObject;
        if (handle == null) { Fail("AIMap.findMapHandle() returned null"); return null; }
        return handle;
    }

    // ---------- step 2: the node under the player ----------

    /// <summary>The walkable polygon the player is standing on. The position is handed
    /// over in an unmanaged, over-reserved buffer shaped by the method's OWN parameter
    /// type: the engine's own generated bindings pass a <c>via.vec3</c> BY VALUE as an
    /// object whose <c>Ptr()</c> is the argument (see <c>app.CollisionSystem.castRayAll
    /// (vec3, vec3, ...)</c> in the decompiled source), which is exactly what a
    /// <see cref="FieldOutBuffer"/> view is, and what the radar's <c>ref vec3</c> casts
    /// already prove in game.
    ///
    /// <para>The return type handed to <c>InvokeBoxed</c> is <c>typeof(object)</c>, not
    /// null: <c>NodeInfo</c> is a REFERENCE type (confirmed in the decompiled
    /// <c>via.navigation.map.NodeInfo</c>, which is not marked <c>ValueType</c>), and
    /// every non-void call in this codebase that is proven in game names its return
    /// type. Null is only ever passed here for methods that return <c>void</c>.</para></summary>
    public static ManagedObject ClosestNode(ManagedObject handle, float px, float py, float pz)
    {
        if (handle == null) return null;
        var td = handle.GetTypeDefinition();
        string typeName = td?.GetFullName();
        if (typeName == null) { Fail("map handle has no type definition"); return null; }

        if (!QueryByHandleType.TryGetValue(typeName, out var query))
        {
            query = FieldProbeService.FindByShape(td, QUERY_CLOSEST_NODE, 1, VEC3_SUFFIX);
            QueryByHandleType[typeName] = query;
        }
        if (query == null) { Fail($"{QUERY_CLOSEST_NODE}(vec3) not found on {typeName}"); return null; }

        var posType = query.GetParameters()?[0].Type;
        if (!EnsurePosArg(posType)) return null;
        if (!Write(_posArg, px, py, pz)) { Fail("could not write the query position"); return null; }

        // The coordinates go through Invariant on their own — concatenating an
        // interpolated string first would produce a plain string, and a comma decimal
        // separator in a log line is not a coordinate anyone can compare.
        string at = FormattableString.Invariant($"({px:F2}, {py:F2}, {pz:F2})");
        Step(2, $"handle {typeName} ok; calling {QUERY_CLOSEST_NODE}(vec3) at {at} " +
                "- the first call in this route that has never run in game");
        var node = query.InvokeBoxed(typeof(object), handle, new object[] { _posArg.View }) as ManagedObject;
        if (node == null) { Fail($"{QUERY_CLOSEST_NODE} returned no node at the player"); return null; }
        return node;
    }

    /// <summary>The one argument buffer, once for the process. Unmanaged memory does
    /// not move, so there is nothing to re-acquire; a refusal disables the class rather
    /// than being worked around with a managed array.</summary>
    private static bool EnsurePosArg(TypeDefinition posType)
    {
        if (_posArg != null) return true;
        if (_posArgTried) return false;
        _posArgTried = true;
        _posArg = FieldOutBuffer.Acquire(posType);
        if (_posArg != null) return true;
        Fail(FieldOutBuffer.Refusal(posType));
        return false;
    }

    private static bool Write(FieldOutBuffer buf, float x, float y, float z)
    {
        if (!buf.SetComponent("x", x) || !buf.SetComponent("y", y) || !buf.SetComponent("z", z))
            return false;
        // Read back through the same field metadata: the read side is proven correct in
        // game, so a matching read-back is the evidence the engine will find the value
        // where we put it rather than being handed a half-filled struct.
        return buf.Component("x") == x && buf.Component("y") == y && buf.Component("z") == z;
    }

    // ---------- a node's own geometry ----------

    /// <summary>How many corners the polygon has, with none of them read. Split out of
    /// <see cref="GlobalVertices"/> because "the count came back" and "a corner came back"
    /// are two different calls and two different ways to die: a probe must be able to tell
    /// which one killed it.</summary>
    public static int VertexCount(ManagedObject node) => ToInt(FlowHelper.Call(node, "getGlobalVertexCount"));

    /// <summary>A node's corners in WORLD space. <c>getVertex</c> is deliberately not
    /// used as a fallback: it is node-local, and mixing the two spaces would compare
    /// coordinates that are not in the same frame. <paramref name="step"/> and
    /// <paramref name="which"/> only shape the trace lines.</summary>
    public static List<(float x, float y, float z)> GlobalVertices(ManagedObject node, int step, string which)
    {
        var list = new List<(float, float, float)>();
        if (node == null) return list;

        Step(step, $"calling getGlobalVertexCount() on {which}");
        int n = ToInt(FlowHelper.Call(node, "getGlobalVertexCount"));

        Step(step, $"{which} reports {n} vertices; calling getGlobalVertex(uint) on it");
        for (int i = 0; i < n && i < MAX_VERTICES; i++)
        {
            var v = FlowHelper.Call(node, "getGlobalVertex", (uint)i);
            if (v == null) continue;
            float x = FlowHelper.ReadVecComponent(v, "x");
            float y = FlowHelper.ReadVecComponent(v, "y");
            float z = FlowHelper.ReadVecComponent(v, "z");
            if (float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z)) list.Add((x, y, z));
        }
        return list;
    }

    /// <summary>The polygons this one is linked to - the ways out, before any geometry
    /// is worked out. <paramref name="reported"/> is what the engine SAID it had, so a
    /// summary can tell "three links" from "three links we could read".</summary>
    public static List<ManagedObject> LinkedNodes(ManagedObject node, out int reported)
    {
        reported = 0;
        var list = new List<ManagedObject>();
        if (node == null) return list;

        Step(5, "player node read; calling NodeInfo.queryLinkToNodes() and getNodeCount()");
        var links = FlowHelper.Call(node, "queryLinkToNodes") as ManagedObject;
        if (links == null) { Fail("NodeInfo.queryLinkToNodes() returned nothing"); return list; }
        reported = ToInt(FlowHelper.Call(links, "getNodeCount"));

        for (int i = 0; i < reported && i < MAX_LINKS; i++)
        {
            Step(6, $"calling getNode({i}) of {reported} linked nodes");
            var other = FlowHelper.Call(links, "getNode", i) as ManagedObject;
            if (other != null) list.Add(other);
        }
        return list;
    }

    /// <summary>A navmesh WALL node is not somewhere to walk, however wide the edge
    /// shared with it is.</summary>
    public static bool IsWall(ManagedObject node) =>
        FieldProbeService.Member(node, "Wall", typeof(bool)) as bool? == true;

    /// <summary>The node's own published position, for the summary line - printed next
    /// to the player position this mod already reads correctly, because a zeroed vec3
    /// out of a new read path is a marshalling bug far more often than a real value
    /// (<c>docs/sf6-screens.md</c>, "Value-type reads: a known trap").</summary>
    public static (float x, float y, float z) Pos(ManagedObject node)
    {
        var pos = FieldProbeService.Member(node, "Pos");
        return (FlowHelper.ReadVecComponent(pos, "x"),
                FlowHelper.ReadVecComponent(pos, "y"),
                FlowHelper.ReadVecComponent(pos, "z"));
    }

    private static int ToInt(object boxed)
    {
        try { return boxed == null ? 0 : Convert.ToInt32(boxed); }
        catch { return 0; }
    }
}
