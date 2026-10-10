using System;
using System.Globalization;
using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

public static partial class FieldNavPath
{
    // ---------- trace and log ----------

    /// <summary><b>Why did the engine say no?</b> Pure diagnosis: every call here is a
    /// by-value read on a report we already own, so it adds no risk to a path that has
    /// crashed this project twice today.
    ///
    /// <para>It exists because "no route" was being treated as one thing when it is
    /// several. <c>PathQueryReport.FailReport</c> (<c>PathQueryReport.cs:24</c>) carries
    /// <c>isFailed(FailAttribute)</c> (<c>FailReport.cs:157</c>) over
    /// <c>{DestPosNotSpecified, StartNodeNotFound, DestNodeNotFound, PathNotFound,
    /// PathfindInterrupt}</c> (<c>:114</c>), and the distinction that matters is
    /// <c>DestNodeNotFound</c> — "the destination point is not ON the navmesh", which is
    /// what a mission objective twelve metres up a building looks like — versus
    /// <c>PathNotFound</c>, "it is on the mesh and genuinely cannot be reached". The
    /// first is our problem to solve; the second is the game's answer and must be
    /// respected. Guessing between them is how a beacon ends up marching a blind player
    /// into a wall.</para>
    ///
    /// <para>Every attribute is asked by NAME out of the TDB, never by ordinal, and the
    /// verdict is logged once per distinct reason so a long walk past an unreachable
    /// objective does not flood the log.</para></summary>
    /// <summary><b>What the engine said about a point, in a form a caller can act on.</b>
    ///
    /// <para>The three cases are not the same kind of fact, and the difference is the whole
    /// reason a walk must ask before it starts:</para>
    /// <list type="bullet">
    /// <item><c>Reachable</c> — the engine found a walkable route. Safe to walk.</item>
    /// <item><c>Unreachable</c> — <c>PathNotFound</c>. The destination is ON the mesh and no
    /// walkable link reaches it: an upper floor, a roof, a room behind a scripted transition.
    /// <b>This is the game's answer and must be respected.</b> Starting a walk at a point the
    /// engine has already said is unreachable is exactly how a request ends up pushing a blind
    /// player into a wall.</item>
    /// <item><c>OffMesh</c> — the destination never resolved to a node at all (a roof, inside
    /// geometry). Different problem, possibly fixable by aiming somewhere else, but not
    /// walkable as asked.</item>
    /// <item><c>Unknown</c> — no answer was obtained: rate-limited, the player is off the mesh
    /// themselves, or the query faulted. <b>Must not be treated as either of the above.</b></item>
    /// </list>
    ///
    /// <para>Derived from <see cref="Diagnose"/>, which reads <c>FailReport</c> by name, so the
    /// two cannot drift apart: this is a classification of the same verdict the log already
    /// prints in full.</para></summary>
    public enum Reachability
    {
        /// <summary>No answer — the caller must not read this as walkable OR as blocked.</summary>
        Unknown = 0,
        /// <summary>The engine found a route.</summary>
        Reachable = 1,
        /// <summary>On the mesh, and provably no walk reaches it (upper floor / behind a door).</summary>
        Unreachable = 2,
        /// <summary>The destination point itself is not on the navmesh.</summary>
        OffMesh = 3,
        /// <summary>The PLAYER is not on the navmesh; nothing can be routed until they are.</summary>
        PlayerOffMesh = 4,
    }

    /// <summary>Classify a verdict string from <see cref="Diagnose"/>.</summary>
    private static Reachability Classify(string verdict)
    {
        if (string.IsNullOrEmpty(verdict)) return Reachability.Unknown;
        // Order matters: a verdict naming both the player's node and the destination's must be
        // read as the player's problem first, because that is the one the caller cannot route
        // around by choosing a different destination.
        if (verdict.Contains("StartNodeNotFound")) return Reachability.PlayerOffMesh;
        if (verdict.Contains("DestNodeNotFound")) return Reachability.OffMesh;
        if (verdict.Contains("PathNotFound")) return Reachability.Unreachable;
        return Reachability.Unknown;
    }

    private static string Diagnose(ManagedObject report, bool log)
    {
        try
        {
            var fail = FieldProbeService.Member(report, "FailReport") as ManagedObject;
            if (fail == null) return null;

            _isFailed ??= FieldProbeService.FindByShape(fail.GetTypeDefinition(), "isFailed", 1, "FailAttribute");
            if (_isFailed == null) return null;

            var reasons = FieldProbeService.ReadEnum(FAIL_ENUM, false);
            if (reasons.Count == 0) reasons = FieldProbeService.ReadEnum(FAIL_ENUM, true);

            string verdict = null;
            foreach (var (name, value) in reasons)
            {
                if (!(_isFailed.InvokeBoxed(typeof(bool), fail, new object[] { value }) is bool hit) || !hit)
                    continue;
                verdict = verdict == null ? name : verdict + ", " + name;
            }
            if (verdict == null) verdict = "no attribute set";

            // Node ids say the same thing a second way: a destination that never
            // resolved to a node is a point off the mesh, not an unreachable place.
            object startNode = FieldProbeService.Member(fail, "StartNodeID");
            object destNode = FieldProbeService.Member(fail, "DestNodeID");

            string line = $"{verdict} (startNode={startNode ?? "?"}, destNode={destNode ?? "?"})";
            if (!log || line == _lastFailVerdict) return verdict;
            _lastFailVerdict = line;

            // The explanation must follow the VERDICT, not be printed regardless of it.
            // The first version always appended the DestNodeNotFound reading and so
            // said "the destination is off the navmesh" underneath a PathNotFound,
            // which is the opposite of what happened.
            string meaning =
                verdict.Contains("DestNodeNotFound")
                    ? "the destination point is NOT ON the navmesh (a roof, a walkway, inside " +
                      "geometry) - snapping it to the mesh would be the fix"
                : verdict.Contains("StartNodeNotFound")
                    ? "the PLAYER is not on the navmesh - nothing can be routed until they are"
                : verdict.Contains("PathNotFound")
                    ? "the destination IS a valid navmesh node, but no walkable link reaches it from " +
                      "the player's node: it is on a DISCONNECTED piece of mesh - an upper floor or " +
                      "roof entered through a door, lift or scripted transition the navmesh does not " +
                      "model. Snapping or widening the node search cannot help; the player needs the " +
                      "way IN, not a route to the point"
                : "see via.navigation.FailReport.FailAttribute";

            API.LogInfo($"[SF6Access] NavPath no route because: {line} - {meaning}");
            return verdict;
        }
        catch { return null; }
    }

    /// <summary>Say what is about to be called, BEFORE calling it. Tracing stops after the
    /// first query that reaches its summary: a crash hunt needs the first run spelled out, a
    /// working pathfinder must not write nine lines a query.</summary>
    private static void Step(int number, string what)
    {
        if (_tracing) API.LogInfo($"[SF6Access] NavPath step {number}: {what}");
    }

    /// <summary>Record why the route could not answer. <b>Tracing stops here too,
    /// not only on success</b> (<c>_tracing</c>): a route that fails every time
    /// fails once a second, and leaving the numbered steps on wrote four log lines
    /// a second for as long as the player kept walking — 500 lines in the
    /// 2026-09-09 session, the same log-flood pathology this mod has already been
    /// bitten by twice. The FIRST failure is fully traced, which is all a
    /// diagnosis needs; the reason stays readable in
    /// <see cref="RouteDescription"/> afterwards.</summary>
    private static void Fail(string reason)
    {
        RouteDescription = "unavailable: " + reason;
        _tracing = false;
        if (_failLogged) return;
        _failLogged = true;
        API.LogWarning("[SF6Access] NavPath " + RouteDescription);
    }

    /// <summary>One line, once: the agent the query actually ran on, so a session in play
    /// can say whether the answers belong to the player's own body size.</summary>
    private static void LogRouteOnce(ManagedObject surface)
    {
        if (_summaryLogged) return;
        _summaryLogged = true;
        RouteDescription = string.Format(CultureInfo.InvariantCulture,
            "{0}.{1}(vec3, vec3, {2}) on the player avatar's own NaviSurface; map='{3}' agent radius={4:F3} m " +
            "height={5:F3} m; at most one query per {6} ms",
            SURFACE_TYPE, QUERY_PATH_SYNC, _reportType?.FullName ?? "?",
            FieldProbeService.Member(surface, "MapName") as string ?? "?",
            FieldProbeService.ToFloat(FieldProbeService.Member(surface, "AgentRad", typeof(float))),
            FieldProbeService.ToFloat(FieldProbeService.Member(surface, "AgentHeight", typeof(float))),
            MinQueryIntervalMs);
        API.LogInfo("[SF6Access] NavPath route: " + RouteDescription);
    }
}
