using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// The city's named districts as choosable destinations — the places the banner announces
/// when the player walks into one ("节拍广场区域", "东湾大街", "SiRN大楼 · 建筑工地　1层").
/// The player asked for a district by name (建筑工地, 2026-10-02) and it was in none of the
/// picker's sources: a district is not a fast-travel point, not a shop, not an avatar.
///
/// <para><b>Where they come from.</b> <c>app.worldtour.CitySectionManager.SectionInfoList</c>
/// — a <c>List&lt;app.worldtour.CitySectionInfo&gt;</c> holding every section in the city,
/// confirmed in game 2026-10-02 by dumping that manager's own surface. Each entry is matched
/// to its localized name through <see cref="ZoneNameService"/> by section id, which is the
/// same table the area banner already reads from. Two earlier guesses were wrong and are
/// recorded here so they are not repeated: a <c>SectionMapSetting…UserDataHolder</c> type
/// (only <c>app.SectionMapSettingCatalogUserData</c> really exists, and it exposes no
/// per-section getter), and probing the lookup with section id 0.</para>
///
/// <para><b>Geometry is discovered, not assumed.</b> Which field on
/// <c>CitySectionInfo</c> holds the position was never confirmed, so an AABB
/// (<c>MinPos</c>/<c>MaxPos</c>) is looked for first, then a single point, then the object's
/// own <c>GameObject</c> transform; the field's real name is written to the dump.</para>
///
/// <para><b>Nothing is offered until the geometry proves itself.</b> A map-space box would
/// put every district within a metre of the player and fill the list with nonsense, so the
/// section the player is standing in must contain them (or sit within
/// <see cref="PointConfirmM"/> of them) before any district is listed; otherwise the
/// category stays empty and the reason is logged. The whole table is written to
/// <c>reframework/data/sf6access_sections.txt</c> once per city.</para>
/// </summary>
public static class SectionCatalog
{
    /// <summary>How far away a district may still be worth listing. The list sorts
    /// nearest-first, so this only decides what is reachable at all in one walk.</summary>
    private const float RangeM = 1000f;

    // A district is metres across, not millimetres and not a province.
    private const float MinSpanM = 2f;
    private const float MaxSpanM = 3000f;

    /// <summary>Slack on the "am I inside it" test: the banner fires on entering, so the
    /// player can legitimately stand a little outside the box that was just announced.</summary>
    private const float InsideMarginM = 40f;

    /// <summary>How near the player's own district's point must be for a point-only geometry
    /// to be believed. Generous on purpose: a corner of a large district, not its centre,
    /// is still tens of metres away — but map-space coordinates would land within a metre or
    /// two of the origin and fail this by a wide margin.</summary>
    private const float PointConfirmM = 120f;

    private const long RetryMs = 5000;

    /// <summary>Field names to try for the section id, most likely first.</summary>
    private static readonly string[] IdFields = { "Id", "SectionId", "id", "mId", "_Id" };

    /// <summary>Field-name pairs to try for the section's world-space AABB.</summary>
    private static readonly (string Min, string Max)[] BoxFields =
    {
        ("MinPos", "MaxPos"),
        ("Min", "Max"),
        ("MinPosition", "MaxPosition"),
        ("MinWorldPos", "MaxWorldPos"),
    };

    /// <summary>Field names to try when a section only exposes a single point.</summary>
    private static readonly string[] PointFields =
    {
        "Pos", "Position", "WorldPos", "WorldPosition", "CenterPos", "Center", "CenterPosition",
    };

    private static readonly List<Entry> _all = new();
    private static List<Destination> _near = new();
    private static uint _city;
    private static long _builtAt;
    private static bool _ready;
    private static bool _gaveUp;
    private static bool _loggedGiveUp;
    private static bool _dumped;
    private static string _geometry;
    private static int _infoCount;

    private readonly record struct Entry(uint Id, string Name, float MinX, float MinZ,
                                         float MaxX, float MaxZ, bool Point);

    /// <summary>The named districts within walking distance, nearest-first. Empty when the
    /// geometry could not be confirmed — never a guess.</summary>
    public static IReadOnlyList<Destination> Near(float px, float py, float pz)
    {
        uint city = WorldTourStateService.CityId;
        long now = Environment.TickCount64;
        if (_ready && city == _city) return _near;
        if (_gaveUp && city == _city) return _near;
        if (city == _city && now - _builtAt < RetryMs) return _near;

        _city = city;
        _builtAt = now;
        _near = new List<Destination>();
        _all.Clear();
        _ready = false;
        _gaveUp = false;

        ReadAll();
        if (_all.Count == 0)
        {
            // The geometry hunt has now failed on every type the game declares (CitySectionInfo
            // carries only an id, SectionMapSetting only delivery costs, ScenarioZoneGroup is a
            // script-contact table). Districts reach the list through DistrictMemory instead,
            // which records where the player actually stood. So this probe stops after one
            // failure per city: it costs reflection work and a log line every retry otherwise.
            _gaveUp = true;
            if (!_loggedGiveUp)
            {
                _loggedGiveUp = true;
                API.LogInfo("[SF6Access] Districts: no section with a readable position " +
                            "(districts come from DistrictMemory instead)");
            }
            Dump(float.NaN, float.NaN, 0, false, "no section geometry read");
            return _near;
        }

        // The gate: the district the player stands in must hold the player.
        uint current = WorldTourStateService.CurrentSectionId;
        var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        string why = "player position unreadable";
        bool confirmed = me.ok && HoldsPlayer(current, me.x, me.z, out why);
        Dump(me.ok ? me.x : float.NaN, me.ok ? me.z : float.NaN, current, confirmed, why);

        if (!confirmed)
        {
            API.LogInfo($"[SF6Access] Districts: section positions not confirmed as world " +
                        $"coordinates ({why}) - districts are not offered");
            return _near;
        }

        foreach (var e in _all)
        {
            float cx = (e.MinX + e.MaxX) * 0.5f, cz = (e.MinZ + e.MaxZ) * 0.5f;
            float dx = cx - px, dz = cz - pz;
            if (dx * dx + dz * dz > RangeM * RangeM) continue;
            // A district's height is a box, not a floor; guiding "20 metres up" to a
            // district would be wrong, so it is offered at the player's own level.
            _near.Add(new Destination(e.Name, null, DestinationCategory.Sections, cx, py, cz));
        }
        _near.Sort((a, b) => a.FlatDistanceTo(px, pz).CompareTo(b.FlatDistanceTo(px, pz)));
        _ready = true;
        API.LogInfo($"[SF6Access] Districts: {_near.Count} of {_all.Count} named sections " +
                    $"within {RangeM} m (geometry {_geometry ?? "-"})");
        return _near;
    }

    /// <summary>The name the game gives to one section id, or null when that id is not one the
    /// name table knows. Added for the lift: a lift that has carried the player somewhere is
    /// recorded by the SECTION it delivered them to, and "it took me to SiRN 1F" is only worth
    /// saying if the id can be turned back into the name the player has already heard.</summary>
    public static string NameOf(uint id)
    {
        if (id == 0) return null;
        try
        {
            var names = ZoneNameService.AllSectionNames();
            if (names != null && names.TryGetValue(id, out string name) && !string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch { }
        return null;
    }

    // ---------------------------------------------------------------- reading

    private static void ReadAll()
    {
        var names = ZoneNameService.AllSectionNames();
        var mgr = WorldTourStateService.GetSectionManager();
        var list = FlowHelper.GetObjectField(mgr, "SectionInfoList");
        int n = FlowHelper.GetListCount(list);
        _infoCount = n;
        for (int i = 0; i < n && _all.Count < 512; i++)
        {
            var info = FlowHelper.GetListItem(list, i);
            uint id = info == null ? 0 : ReadId(info);
            if (id == 0 || !names.TryGetValue(id, out string name)) continue;
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (!TryGeometry(info, out float minX, out float minZ,
                             out float maxX, out float maxZ, out bool point)) continue;

            float span = Math.Max(maxX - minX, maxZ - minZ);
            if (!point && (span < MinSpanM || span > MaxSpanM)) continue;
            _all.Add(new Entry(id, name, minX, minZ, maxX, maxZ, point));
        }
    }

    private static uint ReadId(ManagedObject info)
    {
        foreach (string field in IdFields)
        {
            object v = FieldProbeService.Member(info, field);
            if (v == null) continue;
            try { uint id = Convert.ToUInt32(v); if (id != 0) return id; } catch { }
        }
        return 0;
    }

    /// <summary>An AABB is preferred; failing that a point is taken as a zero-size box, and
    /// failing that the section's own GameObject. Which one answered is remembered so the
    /// dump and the log name the source instead of implying it was known.</summary>
    private static bool TryGeometry(ManagedObject info, out float minX, out float minZ,
                                    out float maxX, out float maxZ, out bool point)
    {
        minX = minZ = maxX = maxZ = float.NaN;
        point = false;

        foreach (var (minName, maxName) in BoxFields)
        {
            object min = FieldProbeService.Member(info, minName);
            object max = FieldProbeService.Member(info, maxName);
            if (min == null || max == null) continue;
            float ax = FlowHelper.ReadVecComponent(min, "x"), az = FlowHelper.ReadVecComponent(min, "z");
            float bx = FlowHelper.ReadVecComponent(max, "x"), bz = FlowHelper.ReadVecComponent(max, "z");
            if (!Finite(ax, az, bx, bz)) continue;
            minX = Math.Min(ax, bx); maxX = Math.Max(ax, bx);
            minZ = Math.Min(az, bz); maxZ = Math.Max(az, bz);
            _geometry = $"{minName}/{maxName}";
            return true;
        }

        foreach (string field in PointFields)
        {
            object p = FieldProbeService.Member(info, field);
            if (p == null) continue;
            float x = FlowHelper.ReadVecComponent(p, "x"), z = FlowHelper.ReadVecComponent(p, "z");
            if (!Finite(x, z)) continue;
            minX = maxX = x; minZ = maxZ = z;
            point = true;
            _geometry = field;
            return true;
        }

        var (wx, wy, wz, ok) = AvatarFieldReader.ReadWorldPos(info);
        if (!ok || !Finite(wx, wz)) return false;
        minX = maxX = wx; minZ = maxZ = wz;
        point = true;
        _geometry = "GameObject transform";
        return true;

        static bool Finite(params float[] v)
        {
            foreach (float f in v) if (!float.IsFinite(f)) return false;
            return true;
        }
    }

    // ---------------------------------------------------------------- the gate

    private static bool HoldsPlayer(uint current, float px, float pz, out string why)
    {
        why = "no current section";
        if (current == 0) return false;

        foreach (var e in _all)
        {
            if (e.Id != current) continue;
            if (e.Point)
            {
                float dx = e.MinX - px, dz = e.MinZ - pz;
                float d = (float)Math.Sqrt(dx * dx + dz * dz);
                why = $"section {current} '{e.Name}' point ({e.MinX:F0},{e.MinZ:F0}) player " +
                      $"({px:F0},{pz:F0}) {d:F0} m away";
                return d <= PointConfirmM;
            }

            bool inside = px >= e.MinX - InsideMarginM && px <= e.MaxX + InsideMarginM &&
                          pz >= e.MinZ - InsideMarginM && pz <= e.MaxZ + InsideMarginM;
            why = $"section {current} '{e.Name}' box ({e.MinX:F0},{e.MinZ:F0})-({e.MaxX:F0},{e.MaxZ:F0}) " +
                  $"player ({px:F0},{pz:F0})";
            return inside;
        }

        why = $"section {current} has no geometry";
        return false;
    }

    // ---------------------------------------------------------------- probe

    /// <summary>Types that could own a section's geometry. <c>CitySectionInfo</c> turned out
    /// to hold nothing but an id and a priority (2026-10-02), so the search moves to the
    /// catalog data types that appeared in the type scan.</summary>
    private static readonly string[] ProbeTypes =
    {
        "app.worldtour.SectionMapSetting",
        "app.SectionMapSettingCatalogUserData",
        "app.CatalogUserDataBase`1<app.worldtour.SectionMapSetting>",
        "app.CatalogUserDataBase`1",
        "app.CatalogUserDataHolderBase`1<app.worldtour.SectionMapSetting>",
        "app.CatalogUserDataHolderBase`1",
        "app.CityMapSettingCatalogUserDataHolder",
        "app.CityMapSetting",
    };

    /// <summary>Writes each candidate type's declared shape (no instance needed) and, where
    /// a singleton can be reached, the answer every getter gives for a real section id.
    /// Only <c>Get…</c>/<c>get_…</c> members are invoked: constructive or mutating calls are
    /// not worth the risk of disturbing the running game.</summary>
    private static void ProbeCatalogs(StringBuilder sb, uint currentSection)
    {
        uint sample = currentSection != 0 ? currentSection : SampleSectionId();
        sb.AppendLine($"# catalog probe (sample section {sample}):");
        DumpRecordFields(sb, sample);
        try
        {
            var tdb = TDB.Get();
            if (tdb == null) { sb.AppendLine("#   no TDB"); return; }
            foreach (string name in ProbeTypes)
            {
                var td = tdb.FindType(name);
                sb.AppendLine($"#   [{name}] -> {(td == null ? "no such type" : td.FullName ?? "?")}");
                if (td == null) continue;
                try { DumpShape(sb, td, "#     "); } catch { }

                object instance = null;
                try { instance = API.GetManagedSingleton(td.FullName ?? name); } catch { }
                sb.AppendLine($"#     singleton: {(instance == null ? "none" : "ok")}");
                if (instance == null) continue;
                ProbeGetters(sb, td, instance, sample, "#     ");
            }
        }
        catch (Exception ex) { sb.AppendLine("#   probe failed: " + ex.Message); }
    }

    /// <summary>The section's data record, in full. It is the one place a section id is
    /// guaranteed to be meaningful, so if a position (or a link to one) exists anywhere, it
    /// is likeliest here.</summary>
    private static void DumpRecordFields(StringBuilder sb, uint sectionId)
    {
        try
        {
            // The dictionary's KEY is not the section id — that is why probing with an id
            // found nothing. Walk the keys instead and say which record each one holds.
            for (uint key = 1; key <= 3; key++)
            {
                var record = FlowHelper.GetTableRecord("CitySectionDataUserDataDict",
                                                       "CitySectionDataUserDataRecord", key);
                sb.AppendLine($"#   record key {key}: {(record == null ? "null" : "id " + FlowHelper.ReadUIntField(record, "id"))}");
                if (record == null) continue;
                ObjectDumper.DumpFieldsWithValues(sb, record.GetTypeDefinition(), record, "#     ");
            }
            var byId = FlowHelper.GetTableRecord("CitySectionDataUserDataDict",
                                                 "CitySectionDataUserDataRecord", sectionId);
            sb.AppendLine($"#   record key {sectionId}: {(byId == null ? "null" : "id " + FlowHelper.ReadUIntField(byId, "id"))}");
        }
        catch (Exception ex) { sb.AppendLine("#   record dump failed: " + ex.Message); }
    }

    /// <summary>Declared field names and types — answerable without an instance, and the
    /// only way to see a type's shape when no instance can be reached.</summary>
    private static void DumpShape(StringBuilder sb, TypeDefinition td, string indent)
    {
        var fields = td.GetFields();
        if (fields == null) return;
        foreach (var f in fields)
        {
            string n = f.Name ?? "?";
            string t = f.Type?.FullName ?? "?";
            sb.AppendLine($"{indent}{(f.IsStatic() ? "[static] " : "")}{t} {n}");
        }
    }

    private static void ProbeGetters(StringBuilder sb, TypeDefinition td, object instance, uint sample, string indent)
    {
        var methods = td.GetMethods();
        if (methods == null) return;
        int tried = 0;
        foreach (var m in methods)
        {
            if (tried++ > 40) break;
            string name = m.Name ?? "";
            if (!(name.StartsWith("Get") || name.StartsWith("get_"))) continue;

            var ps = m.GetParameters();
            object[] args;
            if (ps == null || ps.Count == 0) args = Array.Empty<object>();
            else if (ps.Count == 1)
            {
                string pt = ps[0].Type?.FullName ?? "";
                if (pt.Contains("UInt32")) args = new object[] { sample };
                else if (pt.Contains("Int32") || pt.Contains("Int")) args = new object[] { (int)sample };
                else continue;
            }
            else continue;

            object result;
            try { result = m.InvokeBoxed(typeof(object), instance, args); }
            catch { continue; }
            Describe(sb, indent, name, result);
        }
    }

    /// <summary>What a getter returned: a value, an object's fields, or a list's count and
    /// first item. A list of section settings is the thing being hunted, so its first item
    /// is spelled out in full.</summary>
    private static void Describe(StringBuilder sb, string indent, string name, object result)
    {
        if (result == null) { sb.AppendLine($"{indent}{name}() -> null"); return; }

        int count = FlowHelper.GetListCount(result as ManagedObject);
        if (count > 0)
        {
            sb.AppendLine($"{indent}{name}() -> list of {count}");
            var item = FlowHelper.GetListItem(result as ManagedObject, 0);
            if (item == null) return;
            sb.AppendLine($"{indent}  item type {(item.GetTypeDefinition()?.FullName ?? "?")}");
            ObjectDumper.DumpFieldsWithValues(sb, item.GetTypeDefinition(), item, indent + "  ");
            return;
        }

        if (result is ManagedObject mo)
        {
            sb.AppendLine($"{indent}{name}() -> {(mo.GetTypeDefinition()?.FullName ?? "?")}");
            ObjectDumper.DumpFieldsWithValues(sb, mo.GetTypeDefinition(), mo, indent + "  ");
            return;
        }

        string text = result.ToString() ?? "?";
        sb.AppendLine($"{indent}{name}() -> {(text.Length > 120 ? text.Substring(0, 120) + "…" : text)}");
    }

    /// <summary>A section's geometry is not on <c>CitySectionInfo</c> and not on
    /// <c>SectionMapSetting</c> (both confirmed field-by-field, 2026-10-02), so the search
    /// goes one level deeper: into the objects those types point at. Two leads are followed
    /// — the scenario zone groups the access manager exposes, and the section catalog's own
    /// data list via the holder's static instance — and every list is opened, because a
    /// section's position may only exist on a child record.</summary>
    private static void DeepProbe(StringBuilder sb)
    {
        int budget = 400;
        try
        {
            sb.AppendLine("# deep probe:");
            var access = WorldTourStateService.GetAccessData();
            var groups = FlowHelper.Call(access, "GetScenarioZoneData") as ManagedObject;
            int n = FlowHelper.GetListCount(groups);
            sb.AppendLine($"#   scenario zone groups: {n}");
            for (int i = 0; i < Math.Min(n, 2); i++)
            {
                var g = FlowHelper.GetListItem(groups, i);
                sb.AppendLine($"#   group[{i}] {g?.GetTypeDefinition()?.FullName ?? "?"}");
                Walk(sb, g, 3, "#     ", ref budget);
            }

            var tdb = TDB.Get();
            var holder = tdb?.FindType("app.CatalogUserDataHolderBase`1<app.worldtour.SectionMapSetting>");
            var instanceField = holder?.GetField("Instance");
            object instance = null;
            try { instance = instanceField?.GetDataBoxed(typeof(object), 0, true); } catch { }
            sb.AppendLine($"#   section map setting holder: " +
                          $"{((instance as ManagedObject)?.GetTypeDefinition()?.FullName ?? "unreachable")}");
            var data = instance == null ? null : FieldProbeService.Member(instance, "Data");
            var dataList = data == null ? null : FieldProbeService.Member(data, "DataList");
            int m = FlowHelper.GetListCount(dataList as ManagedObject);
            sb.AppendLine($"#   section map settings: {m}");
            for (int i = 0; i < Math.Min(m, 2); i++)
            {
                var s = FlowHelper.GetListItem(dataList as ManagedObject, i);
                sb.AppendLine($"#   setting[{i}] {s?.GetTypeDefinition()?.FullName ?? "?"} " +
                              $"id={ReadId(s)}");
                Walk(sb, s, 2, "#     ", ref budget);
            }
        }
        catch (Exception ex) { sb.AppendLine("#   deep probe failed: " + ex.Message); }
    }

    /// <summary>An object's fields, opening any list and descending into any object it
    /// holds. Bounded by depth and a line budget: this runs inside the live game, and an
    /// unbounded walk would hang the picker.</summary>
    private static void Walk(StringBuilder sb, ManagedObject obj, int depth, string indent, ref int budget)
    {
        if (depth <= 0 || obj == null || budget <= 0) return;
        var td = obj.GetTypeDefinition();
        var fields = td?.GetFields();
        if (fields == null) return;

        foreach (var f in fields)
        {
            if (budget-- <= 0) return;
            string name = f.Name ?? "?";
            object value = null;
            try { value = f.GetDataBoxed(typeof(object), obj.GetAddress(), f.IsStatic()); } catch { }

            if (value is ManagedObject mo)
            {
                int n = FlowHelper.GetListCount(mo);
                if (n > 0)
                {
                    sb.AppendLine($"{indent}{name}: list[{n}]");
                    for (int i = 0; i < Math.Min(n, 2); i++)
                    {
                        var item = FlowHelper.GetListItem(mo, i);
                        sb.AppendLine($"{indent}  [{i}] {item?.GetTypeDefinition()?.FullName ?? "?"}");
                        Walk(sb, item, depth - 1, indent + "    ", ref budget);
                    }
                    continue;
                }
                sb.AppendLine($"{indent}{name}: {mo.GetTypeDefinition()?.FullName ?? "?"}");
                Walk(sb, mo, depth - 1, indent + "  ", ref budget);
                continue;
            }

            string text = value?.ToString() ?? "null";
            sb.AppendLine($"{indent}{name} = {(text.Length > 80 ? text.Substring(0, 80) + "…" : text)}");
        }
    }

    /// <summary>An id that certainly exists, so a getter that answers nothing for id 0 is
    /// not mistaken for a wrong type.</summary>
    private static uint SampleSectionId()
    {
        foreach (var (id, _) in ZoneNameService.AllSectionNames()) if (id != 0) return id;
        return 401;
    }

    // ---------------------------------------------------------------- dump

    /// <summary>The whole table, with the player's own position and whether the section they
    /// are standing in holds them — everything needed to decide whether this geometry is
    /// world-space, written once per city.</summary>
    private static void Dump(float px, float pz, uint current, bool confirmed, string why)
    {
        if (_dumped) return;
        _dumped = true;
        try
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine($"# city {_city}\tgeometry {_geometry ?? "-"}\t" +
                          $"section info objects {_infoCount}\t" +
                          $"player ({px.ToString("F1", inv)}, {pz.ToString("F1", inv)})" +
                          $"\tcurrent section {current}\tconfirmed {confirmed}\t{why}");

            // Names first: this is the list that answers "is the place I want a district?".
            sb.AppendLine("# id\tname\tcenX\tcenZ\tminX\tminZ\tmaxX\tmaxZ");
            var boxes = new Dictionary<uint, Entry>();
            foreach (var e in _all) boxes[e.Id] = e;
            foreach (var (id, name) in ZoneNameService.AllSectionNames())
            {
                if (boxes.TryGetValue(id, out var e))
                    sb.AppendLine(string.Join("\t", id.ToString(inv), name,
                        ((e.MinX + e.MaxX) * 0.5f).ToString("F1", inv),
                        ((e.MinZ + e.MaxZ) * 0.5f).ToString("F1", inv),
                        e.MinX.ToString("F1", inv), e.MinZ.ToString("F1", inv),
                        e.MaxX.ToString("F1", inv), e.MaxZ.ToString("F1", inv)));
                else
                    sb.AppendLine(string.Join("\t", id.ToString(inv), name, "-", "-", "-", "-", "-", "-"));
            }

            if (_all.Count == 0)
            {
                // Nothing readable: write one object's whole surface, then sweep the catalog
                // types that plausibly own the geometry, so the next attempt starts from what
                // the game really declares instead of from another guess.
                sb.AppendLine("# no geometry read - first CitySectionInfo surface:");
                var list = FlowHelper.GetObjectField(WorldTourStateService.GetSectionManager(), "SectionInfoList");
                var first = FlowHelper.GetListCount(list) > 0 ? FlowHelper.GetListItem(list, 0) : null;
                sb.AppendLine($"#   first: {(first == null ? "null" : "ok")}");
                if (first != null)
                {
                    var td = first.GetTypeDefinition();
                    sb.AppendLine("#   type " + (td?.FullName ?? "?"));
                    ObjectDumper.DumpMethodSignatures(sb, td, "#   ");
                    ObjectDumper.DumpFieldsWithValues(sb, td, first, "#   ");
                }
                ProbeCatalogs(sb, current);
                DeepProbe(sb);
            }

            string path = Path.Combine(ObjectDumper.DumpDir, "sf6access_sections.txt");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            API.LogInfo($"[SF6Access] Districts: wrote {ZoneNameService.AllSectionNames().Count} sections to {path}");
        }
        catch { }
    }
}
