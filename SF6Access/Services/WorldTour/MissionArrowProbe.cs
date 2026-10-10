using System;
using System.Collections.Generic;
using System.Text;
using REFrameworkNET;
using SF6Access.Services;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>F5 — one-shot probe of the game's own mission arrow / HUD guide (RESEARCH TOOL, not a feature).</b>
///
/// <para><b>The question this exists to answer.</b> When the engine's pathfinding reports a
/// mission target as UNREACHABLE, the mod has to fall back to its own search. The hope behind
/// this probe is that the game itself knows better than its navmesh query: an on-screen arrow
/// a sighted player follows may be fed by a route ladder, a lift, a switch — something the
/// navmesh does not model. If that information is reachable, the mod should read it instead of
/// guessing.</para>
///
/// <para>So this does not assume the arrow holds a route, and it does not assume it holds only
/// a direction. It dumps the shape and lets the answer come out of the file.</para>
///
/// <para><b>Blocks.</b>
/// <list type="bullet">
/// <item>A — which guide/route-ish components are ALIVE in the current scene, and under what
///   real type names. The reader has been reporting "0 components in scene", so the first
///   thing to establish is whether the object exists at all and what it is actually
///   called.</item>
/// <item>B — the declared shape of <c>app.UICityHud_MissionGuide</c>: every field and every
///   method signature. Needs no live instance, so "nothing on screen" cannot hide it. A
///   route member (<c>Route</c>, <c>Path</c>, <c>Navi</c>, <c>Way</c>) would show up here
///   even when the arrow is not currently in the scene.</item>
/// <item>C — the same for <c>ProgressMisionInfo</c>, the record behind the arrow's target.</item>
/// <item>D — the live instance, when there is one: fields with values plus every getter it
///   answers.</item>
/// <item>E — the mission system's own GUIDE data (<c>WTMissionTutorialGuideData</c> and the
///   tutorial guide dictionary). This is the only place in the mission system literally named
///   "guide", so it is asked what it knows.</item>
/// <item>F — a census of every <c>app.*</c> type whose name suggests routing. Answers "does a
///   route API exist anywhere in the game" rather than "does this one object have one".</item>
/// </list></para>
///
/// <para><b>Safety.</b> Only <c>Get*</c>/<c>get_*</c> members taking no argument (or a single
/// integer) are ever invoked — <see cref="ObjectDumper.DumpGetters"/> is read-only by
/// construction. Nothing engine-owned is cached across the probe; every object is resolved
/// inside the call and dropped. Each block is independently guarded so a block that throws is
/// recorded in place and the rest of the dump still happens.</para>
/// </summary>
public static class MissionArrowProbe
{
    private const string SCENE_MANAGER = "via.SceneManager";
    private const string SCENE_TYPE = "via.Scene";
    private const string FIND_COMPONENTS = "findComponents(System.Type)";

    private const string GUIDE_TYPE = "app.UICityHud_MissionGuide";
    private const string INFO_TYPE = GUIDE_TYPE + ".ProgressMisionInfo";
    private const string MISSION_SYSTEM = "app.worldtour.WTMissionSystem";

    /// <summary>Substrings a scene component's type name must contain to be worth one
    /// <c>findComponents</c> call in block A.</summary>
    private static readonly string[] SCENE_PATTERNS =
    {
        "MissionGuide", "CityHud", "Navi", "Route", "Arrow", "Waypoint", "WayPoint", "Guide",
    };

    /// <summary>Substrings an <c>app.*</c> type name must contain to appear in the block F
    /// census. Deliberately wider than block A: this is a name survey, not a scene search.</summary>
    private static readonly string[] ROUTE_NAME_PATTERNS =
    {
        "Route", "Navi", "Arrow", "Guide", "Waypoint", "Path", "Transit", "Lift", "Elevator", "Stairs",
    };

    // findComponents is not free and block A is a fishing expedition, not a feature: a
    // bounded sweep keeps one keypress from stalling the frame for seconds.
    private const int MAX_SCENE_TYPES = 80;
    private const int MAX_CENSUS = 200;

    /// <summary>Where the dump went. Called from the F5 handler.</summary>
    public static string Run()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== SF6 MISSION ARROW / GUIDE PROBE - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

        var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        sb.AppendLine($"city={WorldTourStateService.CityId} section={WorldTourStateService.CurrentSectionId} " +
                      $"time={WorldTourStateService.TimeType}");
        sb.AppendLine(FormattableString.Invariant(
            $"player=({me.x:F1}, {me.y:F1}, {me.z:F1}) ok={me.ok} inField={WorldTourStateService.GetAvatarManager() != null}"));
        sb.AppendLine($"existing reader: components={MissionGuideReader.LastComponentCount} " +
                      $"miss={MissionGuideReader.LastMiss ?? "(hit)"}");
        sb.AppendLine();

        Block(sb, "A. GUIDE / ROUTE COMPONENTS ALIVE IN SCENE", () => DumpSceneInventory(sb));
        Block(sb, $"B. DECLARED SHAPE OF {GUIDE_TYPE}", () => DumpDeclaredShape(sb, GUIDE_TYPE));
        Block(sb, $"C. DECLARED SHAPE OF {INFO_TYPE}", () => DumpDeclaredShape(sb, INFO_TYPE));
        Block(sb, "D. LIVE GUIDE INSTANCE", () => DumpLiveGuide(sb));
        Block(sb, "E. MISSION SYSTEM GUIDE DATA", () => DumpGuideData(sb));
        Block(sb, "F. CENSUS OF ROUTE-ISH TYPE NAMES", () => DumpNameCensus(sb));

        // The arrow is a dead end for routing (see block B), so the same keypress goes on to
        // ask the city's own path system — the thing block F's census turned up.
        CityPathProbe.Append(sb);

        string path = System.IO.Path.Combine(ObjectDumper.DumpDir,
            $"sf6access_arrowprobe_{DateTime.Now:HHmmss}.txt");
        System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    /// <summary>Run one block under its own guard: a block that throws is reported in place
    /// and the rest of the dump still happens.</summary>
    private static void Block(StringBuilder sb, string title, Action body)
    {
        sb.AppendLine($"========== {title} ==========");
        try { body(); }
        catch (Exception ex) { sb.AppendLine($"[BLOCK FAILED: {ex.GetType().Name}: {ex.Message}]"); }
        sb.AppendLine();
    }

    // ---------- A. what is actually alive in the scene ----------

    /// <summary>For every type whose name looks like a guide or a route, ask the CURRENT SCENE
    /// whether it holds one. The arrow reader has been finding nothing, so the question is not
    /// "what does the guide know" but "is the guide there, and is it called what we think".
    ///
    /// <para><c>findComponents</c> takes a runtime <c>System.Type</c> and returns the matching
    /// components; it is the same call <see cref="MissionGuideReader"/> already makes, so it is
    /// known-safe. Each type is guarded separately — one unresolvable type must not cost the
    /// rest of the sweep.</para></summary>
    private static void DumpSceneInventory(StringBuilder sb)
    {
        var scene = CurrentScene();
        var find = TDB.Get()?.FindType(SCENE_TYPE)?.GetMethod(FIND_COMPONENTS);
        if (scene == null || find == null)
        {
            sb.AppendLine($"  scene search unavailable (scene={(scene == null ? "null" : "ok")}, " +
                          $"{SCENE_TYPE}.{FIND_COMPONENTS}={(find == null ? "missing" : "ok")})");
            return;
        }

        int swept = 0;
        var alive = new List<string>();
        foreach (var td in TdbTypes())
        {
            if (swept >= MAX_SCENE_TYPES) break;
            string full = td?.FullName ?? "";
            if (!full.StartsWith("app.")) continue;
            // Array types ("Foo[]") are not component types; findComponents on one answers with
            // a garbage length (every array type reported the same 61623 on 2026-10-04), which
            // drowned the one real hit in noise.
            if (full.EndsWith("[]") || full.Contains("[]]")) continue;
            if (!ContainsAny(full, SCENE_PATTERNS)) continue;
            swept++;

            int count = -1;
            ManagedObject first = null;
            try
            {
                var rt = td.GetRuntimeType();
                if (rt == null) continue;
                var list = find.InvokeBoxed(typeof(object), scene, new object[] { rt }) as ManagedObject;
                count = FlowHelper.GetListCount(list);
                if (count > 0) first = FlowHelper.GetListItem(list, 0);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  {full}: findComponents threw {ex.GetType().Name}");
                continue;
            }

            if (count > 0)
            {
                alive.Add($"{full} x{count}");
                sb.AppendLine($"ALIVE {full} x{count}");
                if (first != null)
                {
                    var ftd = first.GetTypeDefinition();
                    ObjectDumper.DumpFieldsWithValues(sb, ftd, first, "    ");
                }
            }
        }
        sb.AppendLine($"  swept {swept} types; alive: {(alive.Count == 0 ? "NONE" : string.Join(", ", alive))}");
    }

    // ---------- B / C. declared shape, no instance needed ----------

    /// <summary>Every field declaration and every method signature the type declares. Read from
    /// the type database, so it answers "what CAN this object tell us" even while the arrow is
    /// not in the scene — which is exactly the state the reader has been reporting.</summary>
    private static void DumpDeclaredShape(StringBuilder sb, string typeName)
    {
        var td = TDB.Get()?.FindType(typeName);
        if (td == null) { sb.AppendLine($"  type {typeName} NOT FOUND in the type database"); return; }

        sb.AppendLine($"  fields of {td.FullName}:");
        try
        {
            var fields = td.GetFields();
            if (fields == null || fields.Count == 0) sb.AppendLine("    (none)");
            else foreach (var f in fields)
            {
                string tag = "";
                try { if (f.IsStatic()) tag = " [static]"; } catch { }
                sb.AppendLine($"    {f.Type?.FullName ?? "?"} {f.Name ?? "?"}{tag}");
            }
        }
        catch (Exception ex) { sb.AppendLine($"    [fields threw: {ex.Message}]"); }

        ObjectDumper.DumpMethodSignatures(sb, td, "  ");
    }

    // ---------- D. the live instance ----------

    /// <summary>What the live arrow object is actually holding right now. Re-resolved through
    /// <see cref="MissionGuideReader"/> so the probe and the feature agree: if the probe can see
    /// a route here, that is the same object the feature would read.</summary>
    private static void DumpLiveGuide(StringBuilder sb)
    {
        var guide = MissionGuideReader.Read();
        if (!guide.Ok)
        {
            sb.AppendLine($"  no live guide: {MissionGuideReader.LastMiss ?? "(no reason recorded)"}");
            sb.AppendLine("  (nothing below can be read without an instance)");
            return;
        }

        sb.AppendLine($"  missionId={guide.MissionId} targetType={guide.TargetType} " +
                      $"({MissionGuideReader.TargetTypeName(guide.TargetType)})");

        var go = guide.TargetObject;
        sb.AppendLine($"  TargetObject={go?.GetTypeDefinition()?.FullName ?? "?"} " +
                      $"name={FlowHelper.Call(go, "get_Name") as string ?? "?"}");
        sb.AppendLine($"  AccessTarget={(guide.AccessTarget == null ? "null" : guide.AccessTarget.GetTypeDefinition()?.FullName)}");

        var td = go.GetTypeDefinition();
        sb.AppendLine("  TargetObject fields:");
        ObjectDumper.DumpFieldsWithValues(sb, td, go, "    ");
        sb.AppendLine("  TargetObject getters:");
        ObjectDumper.DumpGetters(sb, td, go, "    ", maxTries: 120);

        if (guide.AccessTarget != null)
        {
            var atd = guide.AccessTarget.GetTypeDefinition();
            sb.AppendLine($"  AccessTarget fields ({atd?.FullName ?? "?"}):");
            ObjectDumper.DumpFieldsWithValues(sb, atd, guide.AccessTarget, "    ");
            sb.AppendLine("  AccessTarget getters:");
            ObjectDumper.DumpGetters(sb, atd, guide.AccessTarget, "    ", maxTries: 120);
        }

        // The object BEHIND the arrow is the HUD component itself, and that is where a route
        // would live if the arrow carries one — not on the target. Ask the reader's own type for
        // a live instance and dump it whole.
        var hud = FindComponentByTypeName(GUIDE_TYPE);
        if (hud == null) { sb.AppendLine($"  no live {GUIDE_TYPE} component to dump"); return; }
        var htd = hud.GetTypeDefinition();
        sb.AppendLine($"  live {GUIDE_TYPE}:");
        ObjectDumper.DumpFieldsWithValues(sb, htd, hud, "    ");
        ObjectDumper.DumpGetters(sb, htd, hud, "    ", maxTries: 200);
    }

    // ---------- E. the mission system's guide data ----------

    /// <summary>The mission system keeps a tutorial guide structure and a dictionary of guide
    /// info records. It is the one place in the mission system named after GUIDANCE rather than
    /// after state, so it is asked what it knows about where to go.</summary>
    private static void DumpGuideData(StringBuilder sb)
    {
        var sys = API.GetManagedSingleton(MISSION_SYSTEM) as ManagedObject;
        if (sys == null) { sb.AppendLine($"  no {MISSION_SYSTEM} singleton"); return; }
        var sysTd = sys.GetTypeDefinition();

        sb.AppendLine("  WTMissionSystem guide-ish fields:");
        try
        {
            var fields = sysTd.GetFields();
            if (fields != null)
                foreach (var f in fields)
                {
                    string n = f.Name ?? "";
                    if (!ContainsAny(n, new[] { "Guide", "Navi", "Route", "Target", "Pos", "Zone" })) continue;
                    string v = "(read error)";
                    try
                    {
                        v = f.GetDataBoxed(typeof(object), sys.GetAddress(), f.IsStatic())?.ToString() ?? "null";
                        if (v.Length > 160) v = v.Substring(0, 160) + "...";
                    }
                    catch { }
                    sb.AppendLine($"    {f.Type?.FullName ?? "?"} {n} = {v}");
                }
        }
        catch (Exception ex) { sb.AppendLine($"    [fields threw: {ex.Message}]"); }

        sb.AppendLine("  WTMissionSystem getters answering Guide/Navi/Route/Path/Way:");
        ObjectDumper.DumpGetters(sb, sysTd, sys, "    ",
            new[] { "Guide", "Navi", "Route", "Path", "Way", "Progress" }, maxTries: 120);

        // The tutorial guide data object itself — a named structure is worth opening even when
        // its purpose turns out to be tutorial hints and not navigation.
        var tut = FlowHelper.GetObjectField(sys, "mTutorialGuideData");
        if (tut == null) { sb.AppendLine("  mTutorialGuideData = null"); return; }
        var ttd = tut.GetTypeDefinition();
        sb.AppendLine($"  mTutorialGuideData ({ttd?.FullName ?? "?"}):");
        ObjectDumper.DumpFieldsWithValues(sb, ttd, tut, "    ");
        ObjectDumper.DumpGetters(sb, ttd, tut, "    ", maxTries: 80);
    }

    // ---------- F. is there a route API anywhere ----------

    /// <summary>Every <c>app.*</c> type whose name suggests routing, navigation, guidance or
    /// vertical transport. This is the block that answers the real question: not "does the
    /// arrow have a route" but "does anything in this game expose one".</summary>
    private static void DumpNameCensus(StringBuilder sb)
    {
        var hits = new List<string>();
        foreach (var td in TdbTypes())
        {
            string full = td?.FullName ?? "";
            if (!full.StartsWith("app.")) continue;
            if (!ContainsAny(full, ROUTE_NAME_PATTERNS)) continue;
            if (hits.Count >= MAX_CENSUS) break;
            hits.Add(full);
        }
        hits.Sort(StringComparer.Ordinal);
        sb.AppendLine($"  {hits.Count} app.* types name a route / guide / vertical transport:");
        foreach (string h in hits) sb.AppendLine("    " + h);
    }

    // ---------- plumbing ----------

    /// <summary>The scene to search. Never cached — the HUD is rebuilt across loads.</summary>
    private static IObject CurrentScene()
    {
        var mgr = API.GetNativeSingleton(SCENE_MANAGER);
        return (mgr as IObject)?.Call("get_CurrentScene") as IObject;
    }

    /// <summary>One live component of an exactly named type, or null.</summary>
    private static ManagedObject FindComponentByTypeName(string typeName)
    {
        var scene = CurrentScene();
        var find = TDB.Get()?.FindType(SCENE_TYPE)?.GetMethod(FIND_COMPONENTS);
        var td = TDB.Get()?.FindType(typeName);
        if (scene == null || find == null || td == null) return null;
        try
        {
            var rt = td.GetRuntimeType();
            if (rt == null) return null;
            var list = find.InvokeBoxed(typeof(object), scene, new object[] { rt }) as ManagedObject;
            return FlowHelper.GetListCount(list) > 0 ? FlowHelper.GetListItem(list, 0) : null;
        }
        catch { return null; }
    }

    /// <summary>Every type the game declares. Reflection on <c>TDB.Types</c> because the
    /// managed accessor is not exposed directly — the same route
    /// <c>MissionTargetService</c> uses to hunt catalogs by name.</summary>
    private static IEnumerable<TypeDefinition> TdbTypes()
    {
        var tdb = TDB.Get();
        var m = tdb?.GetType().GetMethod("get_Types");
        if (m == null) yield break;
        var all = m.Invoke(tdb, null) as System.Collections.IEnumerable;
        if (all == null) yield break;
        foreach (var t in all)
            if (t is TypeDefinition td) yield return td;
    }

    private static bool ContainsAny(string text, string[] needles)
    {
        foreach (string n in needles)
            if (text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }
}
