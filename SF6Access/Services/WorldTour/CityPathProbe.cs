using System;
using System.Collections.Generic;
using System.Text;
using REFrameworkNET;
using SF6Access.Services;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// Blocks appended to the F5 probe: the city's OWN path and route system.
///
/// <para><b>Why this exists.</b> The HUD mission arrow turned out to be a screen-space icon
/// renderer — it projects a target position onto the screen and draws an arrow; it holds no
/// path, no waypoints, nothing about lifts or switches. That closed the "read the game's
/// arrow" idea.</para>
///
/// <para>The route-name census from the same probe, however, turned up a navigation system the
/// mod has never looked at: <c>app.worldtour.WTPathDefine</c>,
/// <c>app.worldtour.WTNpcRouteManager</c> (with <c>WayPoint</c> nested types and a live
/// <c>WTCityRoute</c>), <c>AvatarNaviControllBase</c> and
/// <c>AvatarControllBase.NaviRequestData</c> with a <c>NaviMapType</c>.</para>
///
/// <para><b>Why that matters more than any of the mod's own searching.</b> NPCs walk the city
/// on routes the game authored. An authored route is walkable BY CONSTRUCTION — no query can
/// answer "unreachable" for it — and NPCs move between levels, so those routes plausibly carry
/// the very cross-level connections the navmesh does not model. Reading them costs nothing per
/// query, unlike <c>FieldNavPath</c>, which answers one question at a time and throttles.</para>
///
/// <para>So the question this asks is not "does the arrow know a route" but <b>"does the city
/// publish the network its own characters walk on"</b>. If it does, the mod should walk that
/// network instead of raycasting at walls.</para>
///
/// <para><b>Safety.</b> Only field reads and <c>Get*</c>/<c>get_*</c> members taking no
/// argument are used. The recursive explorer is depth- and width-bounded so a route structure
/// with thousands of points writes a useful sample instead of a megabyte. Every engine object is
/// resolved inside the call and dropped.</para>
/// </summary>
public static class CityPathProbe
{
    private const string CITY_ROUTE = "app.worldtour.WTCityRoute";
    private const string ROUTE_MANAGER = "app.worldtour.WTNpcRouteManager";
    private const string PATH_DEFINE = "app.worldtour.WTPathDefine";
    private const string NAVI_REQUEST = "app.worldtour.avatar.AvatarControllBase.NaviRequestData";
    private const string NAVI_MAP_TYPE = NAVI_REQUEST + ".NaviMapType";
    private const string NAVI_CONTROLL = "app.worldtour.avatar.AvatarNaviControllBase";
    private const string SCENE_MANAGER = "via.SceneManager";
    private const string SCENE_TYPE = "via.Scene";
    private const string FIND_COMPONENTS = "findComponents(System.Type)";

    // A route network is a big structure: sample it, do not transcribe it. Three levels deep
    // and three items wide is enough to learn the SHAPE, which is the whole point of a probe.
    private const int MAX_DEPTH = 3;
    private const int MAX_ITEMS = 3;
    private const int MAX_TYPES = 120;

    /// <summary>Appends the city-path blocks to a probe already in progress.</summary>
    public static void Append(StringBuilder sb)
    {
        Block(sb, "G. WTPathDefine — THE CITY'S PATH DEFINITIONS", () => DumpType(sb, PATH_DEFINE, recurse: true));
        Block(sb, "H. WTNpcRouteManager — DECLARED SHAPE", () => DumpType(sb, ROUTE_MANAGER, recurse: false));
        Block(sb, "I. LIVE ROUTE MANAGER AND ITS CITY ROUTE", () => DumpLiveRoutes(sb));
        Block(sb, "J. AVATAR NAVIGATION", () => DumpAvatarNavi(sb));
        Block(sb, "K. PATH / ROUTE / WAYPOINT TYPES IN app.worldtour", () => DumpTypeCensus(sb));
        Block(sb, "L. ROUTE GEOMETRY — DO ROUTES CARRY POSITIONS?", () => DumpRouteGeometry(sb));
        Block(sb, "M. WAYPOINT POSITIONS — THE ACTUAL GEOMETRY", () => DumpWayPointPositions(sb));
        Block(sb, "N. via.Sphere LAYOUT — HOW TO READ A WAYPOINT", () => DumpSphereLayout(sb));
        Block(sb, "O. HOW DO NPCS CHANGE LEVEL? — COMMANDS AND HEIGHTS", () => DumpCommandsAndHeights(sb));
    }

    // ---------- O. the cross-level question ----------

    /// <summary>Block M settled the geometry and block N settled how to read it: 400 routes,
    /// 1935 waypoints, real coordinates — and <b>zero</b> routes that change level by more than
    /// 2 m. Every NPC patrol stays on its own floor.
    ///
    /// <para>Yet the network spans Y -1.32..15.69, so routes DO exist on upper levels. An NPC
    /// up there got up there somehow, and the way it did is exactly the cross-level connection
    /// the navmesh does not model. Two things here look for it:</para>
    /// <list type="bullet">
    /// <item>The <c>WTNpcRouteManager.Command</c> enum — what an NPC is told to DO at a
    /// waypoint. A ladder, a lift or a door would appear here as a command.</item>
    /// <item>A height histogram, to see how many routes live on each level and whether the
    /// levels are separated in a way that suggests distinct floors.</item>
    /// </list>
    ///
    /// <para>Reads fields and the type database only; no engine method returning a reference
    /// type is invoked.</para></summary>
    private static void DumpCommandsAndHeights(StringBuilder sb)
    {
        var cmdTd = TDB.Get()?.FindType("app.worldtour.WTNpcRouteManager.Command");
        sb.AppendLine("  WTNpcRouteManager.Command members:");
        if (cmdTd == null) sb.AppendLine("    enum not found");
        else DumpEnumMembers(sb, cmdTd);

        var mgr = FindComponentByTypeName(ROUTE_MANAGER);
        var city = mgr == null ? null : FlowHelper.GetObjectField(mgr, "CityRoute");
        var wrapper = city == null ? null : FlowHelper.GetObjectField(city, "RouteInfos");
        var arr = wrapper == null ? null : FlowHelper.GetObjectField(wrapper, "RouteInfos");
        if (arr == null) { sb.AppendLine("  no routes to sample"); return; }
        int routes = FlowHelper.GetListCount(arr);

        // Which floors the network knows about, and how busy each is.
        var bands = new SortedDictionary<int, int>();
        var heights = new List<float>();
        for (int i = 0; i < routes; i++)
        {
            var info = FlowHelper.GetListItem(arr, i);
            var wp = info == null ? null : FlowHelper.GetObjectField(info, "WayPointIndex");
            var pts = wp == null ? null : FlowHelper.GetObjectField(wp, "Point");
            int pc = FlowHelper.GetListCount(pts);
            if (pc <= 0) continue;
            float sum = 0f; int got = 0;
            for (int k = 0; k < pc; k++)
            {
                object s = FlowHelper.Call(pts, "Get", k);
                // Named, not discarded: this project declares a namespace called "_", and the
                // discard token collides with it (CS0118).
                if (!TrySpherePos(s, out float sx, out float sy, out float sz)) continue;
                sum += sy; got++;
            }
            if (got == 0) continue;
            float avg = sum / got;
            heights.Add(avg);
            int band = (int)Math.Floor(avg / 5.0);
            bands.TryGetValue(band, out int n);
            bands[band] = n + 1;
        }

        sb.AppendLine("  routes by average height (5 m bands):");
        foreach (var kv in bands)
            sb.AppendLine($"    Y {kv.Key * 5}..{kv.Key * 5 + 5} m : {kv.Value} routes");

        // The commands carried at waypoints, on a few routes: a CommandClass that is not the
        // plain WayPointOnly marker is an NPC doing something at that spot.
        sb.AppendLine("  waypoint command classes (sample routes):");
        int shown = 0;
        for (int i = 0; i < routes && shown < 6; i++)
        {
            var info = FlowHelper.GetListItem(arr, i);
            var wp = info == null ? null : FlowHelper.GetObjectField(info, "WayPointIndex");
            if (wp == null) continue;
            var custom = FlowHelper.GetObjectField(wp, "CustomWay");
            int cc = FlowHelper.GetListCount(custom);
            var kinds = new List<string>();
            for (int k = 0; k < cc; k++)
            {
                var cw = FlowHelper.GetListItem(custom, k);
                var cmd = cw == null ? null : FlowHelper.GetObjectField(cw, "CommandClass");
                kinds.Add(cmd?.GetTypeDefinition()?.GetName() ?? "null");
            }
            if (kinds.Count == 0) continue;
            shown++;
            sb.AppendLine($"    route[{i}] {string.Join(", ", kinds)}");
        }
    }

    // ---------- N. stop guessing the sphere layout ----------

    /// <summary>Block M read every waypoint as the same nonsense value
    /// (<c>193576704, 0, 0</c>), which is what reading at the wrong offset looks like.
    /// <c>via.Sphere</c> need not expose <c>x</c>/<c>y</c>/<c>z</c> at all — it may wrap a
    /// nested vector under another name, or order its members differently — and
    /// <see cref="FlowHelper.ReadVecComponent"/> on a struct with no such field returns
    /// whatever sits at that offset rather than failing.
    ///
    /// <para>So this dumps the type's declared fields and then asks for each one by name on a
    /// real element. Guessing cost one probe already; reading the declaration costs nothing.
    /// Still field-only: no engine method returning a reference type is invoked.</para></summary>
    private static void DumpSphereLayout(StringBuilder sb)
    {
        var td = TDB.Get()?.FindType("via.Sphere");
        if (td == null) { sb.AppendLine("  via.Sphere not found in the type database"); return; }

        sb.AppendLine("  declared fields of via.Sphere:");
        var names = new List<string>();
        try
        {
            var fields = td.GetFields();
            if (fields == null || fields.Count == 0) sb.AppendLine("    (none)");
            else foreach (var f in fields)
            {
                string n = f.Name ?? "?";
                if (n != "value__") names.Add(n);
                sb.AppendLine($"    {f.Type?.FullName ?? "?"} {n}");
            }
        }
        catch (Exception ex) { sb.AppendLine($"    [fields threw: {ex.Message}]"); }

        ObjectDumper.DumpMethodSignatures(sb, td, "  ");

        // A real element to interrogate.
        var mgr = FindComponentByTypeName(ROUTE_MANAGER);
        var city = mgr == null ? null : FlowHelper.GetObjectField(mgr, "CityRoute");
        var wrapper = city == null ? null : FlowHelper.GetObjectField(city, "RouteInfos");
        var arr = wrapper == null ? null : FlowHelper.GetObjectField(wrapper, "RouteInfos");
        var info = arr == null ? null : FlowHelper.GetListItem(arr, 0);
        var wp = info == null ? null : FlowHelper.GetObjectField(info, "WayPointIndex");
        var pts = wp == null ? null : FlowHelper.GetObjectField(wp, "Point");
        if (pts == null) { sb.AppendLine("  no Point array to sample"); return; }

        object e0 = FlowHelper.Call(pts, "Get", 0);
        sb.AppendLine($"  element[0] runtime type = {e0?.GetType().Name ?? "null"}");
        if (e0 == null) { sb.AppendLine("  (boxing a value-type array element returned null)"); return; }

        sb.AppendLine("  direct component reads on the boxed element:");
        foreach (string c in new[] { "x", "y", "z", "w", "r" })
            sb.AppendLine(FormattableString.Invariant($"    {c} = {FlowHelper.ReadVecComponent(e0, c)}"));

        sb.AppendLine("  each declared field, asked by name:");
        foreach (string n in names)
        {
            object v = FieldProbeService.Member(e0, n);
            string kind = v?.GetType().Name ?? "null";
            string desc = kind;
            if (v != null)
            {
                float fx = FlowHelper.ReadVecComponent(v, "x");
                float fy = FlowHelper.ReadVecComponent(v, "y");
                float fz = FlowHelper.ReadVecComponent(v, "z");
                if (float.IsFinite(fx) && float.IsFinite(fy) && float.IsFinite(fz))
                    desc = FormattableString.Invariant($"{kind} ({fx:F2}, {fy:F2}, {fz:F2})");
            }
            sb.AppendLine($"    {n} = {desc}");
        }

        // Three elements side by side: distinct, plausible coordinates are the only evidence
        // that a layout is right. One element can look plausible by accident.
        sb.AppendLine("  first three elements, direct read:");
        for (int k = 0; k < 3; k++)
        {
            object e = FlowHelper.Call(pts, "Get", k);
            float fx = FlowHelper.ReadVecComponent(e, "x");
            float fy = FlowHelper.ReadVecComponent(e, "y");
            float fz = FlowHelper.ReadVecComponent(e, "z");
            sb.AppendLine(FormattableString.Invariant($"    [{k}] ({fx:F2}, {fy:F2}, {fz:F2})"));
        }
    }

    // ---------- M. the coordinates themselves ----------

    /// <summary>Reads the waypoint coordinates out of <c>WayPoint.Point</c>
    /// (<c>via.Sphere[]</c>), route by route.
    ///
    /// <para><b>Deliberately field-only.</b> <c>WayPoint.GetWayPoints()</c> would hand back a
    /// ready <c>via.vec3[]</c>, but calling an engine method that returns a reference type is
    /// exactly what crashed the game before (<c>AvatarBase.GetMoveVec()</c>), so this reads the
    /// <c>Point</c> field instead: a field read plus the array element access every other part
    /// of this mod already does. If the field path ever stops yielding coordinates, the method
    /// call is the next thing to try — but that is a decision to make with the player, not
    /// silently inside a probe.</para>
    ///
    /// <para><c>via.Sphere</c> is a value type, so its elements arrive boxed and are read
    /// through <see cref="FlowHelper.ReadVecComponent"/>, which takes <c>object</c> and handles
    /// boxed engine vectors. Direct components are tried first, then a nested centre member,
    /// because the sphere's layout is not assumed.</para></summary>
    private static void DumpWayPointPositions(StringBuilder sb)
    {
        var mgr = FindComponentByTypeName(ROUTE_MANAGER);
        var city = mgr == null ? null : FlowHelper.GetObjectField(mgr, "CityRoute");
        if (city == null) { sb.AppendLine("  no live CityRoute"); return; }

        var wrapper = FlowHelper.GetObjectField(city, "RouteInfos");
        var arr = wrapper == null ? null : FlowHelper.GetObjectField(wrapper, "RouteInfos");
        int routes = FlowHelper.GetListCount(arr);
        sb.AppendLine($"  routes in the city = {routes}");
        if (routes == 0) { sb.AppendLine("  (RouteInfo[] empty)"); return; }

        var counts = new List<int>();
        int detailed = 0;
        // Whether the network climbs is the whole reason for wanting it: a route whose
        // waypoints change height is a route that goes UP or DOWN, and that is precisely the
        // connection the navmesh does not model. Counted per route, not just globally.
        int verticalRoutes = 0;
        float worldMinY = float.MaxValue, worldMaxY = float.MinValue;

        for (int i = 0; i < routes; i++)
        {
            var info = FlowHelper.GetListItem(arr, i);
            if (info == null) { counts.Add(-1); continue; }
            var wp = FlowHelper.GetObjectField(info, "WayPointIndex");
            var pts = wp == null ? null : FlowHelper.GetObjectField(wp, "Point");
            int pc = FlowHelper.GetListCount(pts);
            counts.Add(pc);
            if (pc <= 0) continue;

            float lo = float.MaxValue, hi = float.MinValue;
            for (int k = 0; k < pc; k++)
            {
                object sphere = FlowHelper.Call(pts, "Get", k);
                if (!TrySpherePos(sphere, out float x, out float y, out float z)) continue;
                if (y < lo) lo = y;
                if (y > hi) hi = y;
                if (detailed < 3 && k < 6)
                    sb.AppendLine(FormattableString.Invariant($"    route[{i}] [{k}] ({x:F2}, {y:F2}, {z:F2})"));
            }
            if (detailed < 3 && lo != float.MaxValue)
            {
                detailed++;
                sb.AppendLine(FormattableString.Invariant(
                    $"  --- route[{i}] {pc} waypoints, Y {lo:F2}..{hi:F2} (span {hi - lo:F2} m) ---"));
            }
            if (lo == float.MaxValue) continue;
            if (hi - lo > LEVEL_M) verticalRoutes++;
            if (lo < worldMinY) worldMinY = lo;
            if (hi > worldMaxY) worldMaxY = hi;
        }

        int usable = 0;
        foreach (int c in counts) if (c > 0) usable++;
        int min = int.MaxValue, max = 0, total = 0;
        foreach (int c in counts) if (c > 0) { if (c < min) min = c; if (c > max) max = c; total += c; }
        int shownMin = min == int.MaxValue ? 0 : min;
        sb.AppendLine($"  routes with waypoints = {usable}/{routes}; " +
                      $"points per route min={shownMin} max={max} total={total}");
        if (worldMinY != float.MaxValue)
            // One interpolated string, not two concatenated ones: `$a + $b` collapses to a
            // plain string and FormattableString.Invariant would not accept it.
            sb.AppendLine(FormattableString.Invariant(
                $"  height: network spans Y {worldMinY:F2}..{worldMaxY:F2}; routes changing level by >{LEVEL_M:F1} m = {verticalRoutes}/{usable}"));
    }

    /// <summary>The centre of a boxed <c>via.Sphere</c>. Direct x/y/z first (a sphere may
    /// inline its centre), then the usual nested member names — the layout is not assumed
    /// because getting it wrong would look exactly like "there are no coordinates".</summary>
    private static bool TrySpherePos(object sphere, out float x, out float y, out float z)
    {
        x = y = z = 0f;
        if (sphere == null) return false;

        // A named member FIRST. Reading x/y/z straight off the struct is what produced
        // 193576704 for every waypoint in the 10:56 probe: via.Sphere has no such fields, and
        // ReadVecComponent returns whatever sits at the offset rather than failing, so a
        // non-zero garbage value sailed through the old "is it non-zero?" check.
        // "pos" is the field's actual spelling — confirmed from the type database on
        // 2026-10-04: via.Sphere is { via.Float3 pos; System.Single r; }. Member lookup is an
        // exact match, so the capitalised guesses above must be accompanied by the real name.
        foreach (string name in new[] { "pos", "Pos", "center", "Center", "Position", "Origin", "C", "P", "V", "Vec" })
        {
            object v = FieldProbeService.Member(sphere, name);
            if (v == null) continue;
            float ax = FlowHelper.ReadVecComponent(v, "x");
            float ay = FlowHelper.ReadVecComponent(v, "y");
            float az = FlowHelper.ReadVecComponent(v, "z");
            if (Plausible(ax, ay, az)) { x = ax; y = ay; z = az; return true; }
        }

        float dx = FlowHelper.ReadVecComponent(sphere, "x");
        float dy = FlowHelper.ReadVecComponent(sphere, "y");
        float dz = FlowHelper.ReadVecComponent(sphere, "z");
        if (Plausible(dx, dy, dz)) { x = dx; y = dy; z = dz; return true; }
        return false;
    }

    /// <summary>A plausible world coordinate for this game: finite, not the origin, and within
    /// a sane magnitude. The bound is what catches a bad offset — the 10:56 probe read
    /// 193576704 from a struct that has no x/y/z, and "non-zero" was not enough to reject it.
    /// Real field positions seen so far are of the order of tens of metres.</summary>
    private const float MAX_COORD = 100000f;

    /// <summary>How much a route's height must change before it counts as one that goes
    /// between levels — the same threshold the rest of the mod uses to call two places
    /// "different floors".</summary>
    private const float LEVEL_M = 2f;

    private static bool Plausible(float x, float y, float z) =>
        float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z)
        && (x != 0f || y != 0f || z != 0f)
        && Math.Abs(x) < MAX_COORD && Math.Abs(y) < MAX_COORD && Math.Abs(z) < MAX_COORD;

    private static void Block(StringBuilder sb, string title, Action body)
    {
        sb.AppendLine($"========== {title} ==========");
        try { body(); }
        catch (Exception ex) { sb.AppendLine($"[BLOCK FAILED: {ex.GetType().Name}: {ex.Message}]"); }
        sb.AppendLine();
    }

    // ---------- G / H. declared shape ----------

    /// <summary>Fields, method signatures and — when asked — a bounded walk into whatever the
    /// type's static/instance fields actually hold. Declaration comes first because it survives
    /// "the object is not in the scene", which is exactly what hid the arrow's answer.</summary>
    private static void DumpType(StringBuilder sb, string typeName, bool recurse)
    {
        var td = TDB.Get()?.FindType(typeName);
        if (td == null) { sb.AppendLine($"  type {typeName} NOT FOUND"); return; }

        sb.AppendLine($"  {td.FullName} fields:");
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

        if (!recurse) return;
        // Statics are read without an instance: a path table is usually static data.
        sb.AppendLine($"  static field VALUES of {td.FullName}:");
        try
        {
            var fields = td.GetFields();
            if (fields != null)
                foreach (var f in fields)
                {
                    bool isStatic = false;
                    try { isStatic = f.IsStatic(); } catch { }
                    if (!isStatic) continue;
                    string v = "(read error)";
                    try
                    {
                        v = f.GetDataBoxed(typeof(object), 0, true)?.ToString() ?? "null";
                        if (v.Length > 160) v = v.Substring(0, 160) + "...";
                    }
                    catch { }
                    sb.AppendLine($"    {f.Name} = {v}");
                }
        }
        catch (Exception ex) { sb.AppendLine($"    [statics threw: {ex.Message}]"); }
    }

    // ---------- I. the live route network ----------

    /// <summary>The route manager was ALIVE in the scene (one instance) in the probe that found
    /// it, so its <c>CityRoute</c> can be opened here. This is the block that decides whether
    /// the city publishes a walkable network: if <c>WTCityRoute</c> holds routes with
    /// waypoints carrying POSITIONS, the mod has a map it never had.</summary>
    private static void DumpLiveRoutes(StringBuilder sb)
    {
        var mgr = FindComponentByTypeName(ROUTE_MANAGER);
        if (mgr == null) { sb.AppendLine($"  no live {ROUTE_MANAGER} in the scene"); }

        if (mgr != null)
        {
            sb.AppendLine($"  live {ROUTE_MANAGER}:");
            ObjectDumper.DumpFieldsWithValues(sb, mgr.GetTypeDefinition(), mgr, "    ");
            ObjectDumper.DumpGetters(sb, mgr.GetTypeDefinition(), mgr, "    ", maxTries: 120);

            var city = FlowHelper.GetObjectField(mgr, "CityRoute");
            sb.AppendLine($"  CityRoute = {(city == null ? "null" : city.GetTypeDefinition()?.FullName)}");
            if (city != null)
            {
                sb.AppendLine($"  --- contents of {CITY_ROUTE} ---");
                ObjectDumper.DumpFieldsWithValues(sb, city.GetTypeDefinition(), city, "    ");
                ObjectDumper.DumpGetters(sb, city.GetTypeDefinition(), city, "    ", maxTries: 200);
                sb.AppendLine("  --- recursive walk of CityRoute ---");
                Explore(sb, "CityRoute", city, 0);
            }
        }

        sb.AppendLine($"  --- declared shape of {CITY_ROUTE} ---");
        DumpType(sb, CITY_ROUTE, recurse: false);
    }

    // ---------- L. do the routes carry geometry? ----------

    /// <summary>The one question block I could not answer: <c>WTCityRoute.RouteInfos</c> is a
    /// <c>udWTNpcRoute</c> wrapping a <c>RouteInfo[]</c>, and the first probe stopped one level
    /// short of opening it. Everything else here is moot if a <c>RouteInfo</c> has no
    /// coordinates — and everything else is solved if it does.</summary>
    private static void DumpRouteGeometry(StringBuilder sb)
    {
        var mgr = FindComponentByTypeName(ROUTE_MANAGER);
        var city = mgr == null ? null : FlowHelper.GetObjectField(mgr, "CityRoute");
        if (city == null) { sb.AppendLine("  no live CityRoute to walk"); }

        if (city != null)
        {
            var wrapper = FlowHelper.GetObjectField(city, "RouteInfos");
            sb.AppendLine($"  CityRoute.RouteInfos wrapper = {wrapper?.GetTypeDefinition()?.FullName ?? "null"}");
            var arr = wrapper == null ? null : FlowHelper.GetObjectField(wrapper, "RouteInfos");
            int n = FlowHelper.GetListCount(arr);
            sb.AppendLine($"  RouteInfo count = {n}");
            if (n > 0)
            {
                for (int i = 0; i < Math.Min(n, MAX_ITEMS); i++)
                {
                    var info = FlowHelper.GetListItem(arr, i);
                    if (info == null) continue;
                    var td = info.GetTypeDefinition();
                    sb.AppendLine($"  --- RouteInfo[{i}] {td?.FullName ?? "?"} ---");
                    ObjectDumper.DumpFieldsWithValues(sb, td, info, "    ");
                    ObjectDumper.DumpGetters(sb, td, info, "    ", maxTries: 120);
                    Explore(sb, $"RouteInfo[{i}]", info, 0);
                }
            }
            else sb.AppendLine("  (RouteInfo[] empty or not readable as a list)");
        }

        // Shapes are read regardless of whether an instance was found: they say what a route IS.
        foreach (string t in new[]
        {
            "app.worldtour.udWTNpcRoute",
            "app.worldtour.udWTNpcRoute.RouteInfo",
            "app.worldtour.WTNpcRouteManager.WayPoint",
            "app.worldtour.WTNpcRouteManager.WayPoint.CustomWayPoint",
            "app.worldtour.udWTOverrideNpcRoute",
        })
        {
            sb.AppendLine($"  --- declared shape of {t} ---");
            DumpType(sb, t, recurse: false);
        }
    }

    // ---------- J. how characters navigate ----------

    /// <summary>The avatar's own navigation request structure and controller. Even if it can
    /// never be used to move the player (autowalk is off by the player's decision), the SHAPE
    /// tells us what a navigation request looks like to the game — and a <c>NaviMapType</c>
    /// implies the game keeps more than one navigation map, which is worth knowing about.</summary>
    private static void DumpAvatarNavi(StringBuilder sb)
    {
        DumpType(sb, NAVI_REQUEST, recurse: false);
        sb.AppendLine($"  --- {NAVI_CONTROLL} ---");
        DumpType(sb, NAVI_CONTROLL, recurse: false);

        sb.AppendLine($"  --- {NAVI_MAP_TYPE} members ---");
        var etd = TDB.Get()?.FindType(NAVI_MAP_TYPE);
        if (etd == null) sb.AppendLine("    enum not found");
        else DumpEnumMembers(sb, etd);

        // SectionType on WTPathDefine is the same kind of hint: what kinds of path the city
        // recognises at all.
        var st = TDB.Get()?.FindType(PATH_DEFINE + ".SectionType");
        sb.AppendLine($"  --- {PATH_DEFINE}.SectionType members ---");
        if (st == null) sb.AppendLine("    enum not found");
        else DumpEnumMembers(sb, st);
    }

    private static void DumpEnumMembers(StringBuilder sb, TypeDefinition td)
    {
        try
        {
            var fields = td.GetFields();
            if (fields == null || fields.Count == 0) { sb.AppendLine("    (none)"); return; }
            foreach (var f in fields)
            {
                string v = "?";
                try { v = f.GetDataBoxed(typeof(object), 0, true)?.ToString() ?? "null"; } catch { }
                sb.AppendLine($"    {f.Name ?? "?"} = {v}");
            }
        }
        catch (Exception ex) { sb.AppendLine($"    [enum threw: {ex.Message}]"); }
    }

    // ---------- K. census ----------

    /// <summary>Every <c>app.worldtour.*</c> type naming a path, route, waypoint, navigation or
    /// link — the inventory to pick from once one of them turns out to hold real geometry.</summary>
    private static void DumpTypeCensus(StringBuilder sb)
    {
        string[] needles = { "Path", "Route", "WayPoint", "Navi", "Link", "Node", "Move", "Walk" };
        var hits = new List<string>();
        foreach (var td in TdbTypes())
        {
            string full = td?.FullName ?? "";
            if (!full.StartsWith("app.worldtour.")) continue;
            if (!ContainsAny(full, needles)) continue;
            if (hits.Count >= MAX_TYPES) break;
            hits.Add(full);
        }
        hits.Sort(StringComparer.Ordinal);
        sb.AppendLine($"  {hits.Count} app.worldtour.* types name a path / route / waypoint / link:");
        foreach (string h in hits) sb.AppendLine("    " + h);
    }

    // ---------- bounded recursive explorer ----------

    /// <summary>Walks an object's fields, and into the first few items of any list it finds,
    /// down to <see cref="MAX_DEPTH"/>. Written for route structures: the point is to learn what
    /// a route IS made of and whether waypoints carry coordinates.</summary>
    private static void Explore(StringBuilder sb, string label, object value, int depth)
    {
        if (depth > MAX_DEPTH || value == null) return;
        string pad = new string(' ', 4 + depth * 2);

        if (value is ManagedObject mo)
        {
            int count = FlowHelper.GetListCount(mo);
            if (count > 0)
            {
                sb.AppendLine($"{pad}{label}: list of {count}");
                for (int i = 0; i < Math.Min(count, MAX_ITEMS); i++)
                {
                    var item = FlowHelper.GetListItem(mo, i);
                    if (item == null) continue;
                    var itd = item.GetTypeDefinition();
                    sb.AppendLine($"{pad}  [{i}] {itd?.FullName ?? "?"}");
                    ObjectDumper.DumpFieldsWithValues(sb, itd, item, pad + "    ");
                    Explore(sb, $"[{i}] deeper", item, depth + 1);
                }
                return;
            }

            var td = mo.GetTypeDefinition();
            sb.AppendLine($"{pad}{label}: {td?.FullName ?? "?"}");
            try
            {
                var fields = td?.GetFields();
                if (fields != null)
                    foreach (var f in fields)
                    {
                        object fv = null;
                        try { fv = f.GetDataBoxed(typeof(object), mo.GetAddress(), false); } catch { }
                        // Positions are the prize: print them even when they are nested.
                        if (fv is ManagedObject fmo)
                        {
                            int fc = FlowHelper.GetListCount(fmo);
                            if (fc > 0) sb.AppendLine($"{pad}  {f.Name}: list of {fc}");
                            else sb.AppendLine($"{pad}  {f.Name}: {fmo.GetTypeDefinition()?.FullName ?? "?"}");
                            // Recurse into OBJECTS too, not only lists. The first probe missed
                            // the route network entirely because RouteInfos is a wrapper object
                            // holding the array one level down, and stopping at non-lists left
                            // the geometry unopened.
                            Explore(sb, f.Name ?? "?", fmo, depth + 1);
                        }
                        else sb.AppendLine($"{pad}  {f.Name} = {fv?.ToString() ?? "null"}");
                    }
            }
            catch (Exception ex) { sb.AppendLine($"{pad}  [explore threw: {ex.Message}]"); }
        }
        else sb.AppendLine($"{pad}{label} = {value}");
    }

    // ---------- plumbing ----------

    private static ManagedObject FindComponentByTypeName(string typeName)
    {
        var mgr = API.GetNativeSingleton(SCENE_MANAGER);
        var scene = (mgr as IObject)?.Call("get_CurrentScene") as IObject;
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
