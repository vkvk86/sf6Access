using System;
using System.IO;
using System.Text;
using REFrameworkNET;
using SF6Access.Services;
using SF6Access.Services.Ui;

namespace SF6Access.Hooks;

/// <summary>
/// Reads the fighter-flag / region picker: <c>app.UICFNSelectHomeDialog.FlowParam</c>
/// — Options → "Change region" (变更所在地区), the screen where the flag shown
/// next to the fighter name is chosen.
///
/// Why it needs its own reader: the flag cells are IMAGES. The generic focus
/// reader found no text on them, and the dialog is not on any watch list, so the
/// whole screen was completely silent (verified: flow opened 20:34:51, closed
/// 20:35:09, zero announcements in between).
///
/// The cells live on <c>_NationalFlagScrollGrid</c> (UIPartsScrollGrid) and are
/// backed by <c>CurrentHomeList</c> (List&lt;app.AppDefine.CfnHome&gt;); the
/// category tabs (All / Asia / Europe...) live on <c>_CategoryTabSimpleList</c>
/// and DO carry text, so they are announced by name.
///
/// The home record's shape was not in any dump yet, so the name is resolved
/// adaptively: a string field, then a localized message Guid field, then a
/// getter — and the first focused record is written to
/// <c>reframework/data/sf6access_home_probe.txt</c> so the exact member can be
/// hard-coded later. When no name can be resolved the position ("3 of 20") is
/// spoken instead of staying silent.
/// </summary>
public sealed class HomeSelectHooks : ScreenAdapter
{
    private const string HOME_TYPE = "app.UICFNSelectHomeDialog.FlowParam";

    public override string[] OwnedTypes => new[] { HOME_TYPE };

    private const string GRID_FIELD = "_NationalFlagScrollGrid";
    private const string TAB_FIELD = "_CategoryTabSimpleList";
    private const string LIST_FIELD = "CurrentHomeList";
    private const string HOME_STRUCT = "app.AppDefine.CfnHome";

    public HomeSelectHooks()
    {
        SearchInterval = 20;
        ReadInterval = 5;
    }

    private ManagedObject _param;
    private int _lastIndex = -2;
    private string _lastName;
    private int _lastTab = -2;
    private string _lastTabText;
    private bool _probed;

    protected override bool Locate()
    {
        var param = FlowHelper.FindFlowParam(HOME_TYPE);
        if (param == null)
        {
            _param = null;
            return false;
        }

        // Re-bind when the dialog is reopened (the Param is recreated)
        if (FlowHelper.AddressOf(param) != FlowHelper.AddressOf(_param))
        {
            _param = param;
            _lastIndex = -2;
            _lastName = null;
            _lastTab = -2;
            _lastTabText = null;
        }
        return true;
    }

    protected override void OnDeactivate()
    {
        // The full picture, written on the way out: one visit is enough to see
        // what every source says about every region
        WriteTable();
        _param = null;
        _lastIndex = -2;
        _lastName = null;
        _lastTab = -2;
        _lastTabText = null;
    }

    protected override void OnPoll()
    {
        try
        {
            var grid = FlowHelper.GetObjectField(_param, GRID_FIELD);
            int idx = grid == null ? -1 : FlowHelper.CallInt(grid, "get_SelectedIndex");
            if (idx < 0) return;

            var list = FlowHelper.GetObjectField(_param, LIST_FIELD);
            int count = FlowHelper.GetListCount(list);

            // Category tab: its cells carry text, so read it before the grid —
            // switching category changes which flags are listed
            var tab = FlowHelper.GetObjectField(_param, TAB_FIELD);
            int tabIdx = tab == null ? -1 : FlowHelper.CallInt(tab, "get_SelectedIndex");
            string tabText = Sanitize(tab == null ? null : FlowHelper.ReadSelectedItemText(tab));

            bool tabChanged = tabIdx >= 0 && tabIdx != _lastTab
                              && !string.IsNullOrEmpty(tabText) && tabText != _lastTabText;
            _lastTab = tabIdx;
            if (!string.IsNullOrEmpty(tabText)) _lastTabText = tabText;

            // The game hands us the whole tab in display order — capture it once
            // per tab, so every id can be pinned to the flag drawn at that spot
            if (tabChanged) DumpTabIds(tabIdx, tabText, list, count);

            string name = ResolveName(grid, list, idx);

            // Shape capture AFTER resolving, so the file records whether the
            // message-row lookup worked and where it stopped
            Probe(list, idx, name);

            // Fall back to the position rather than saying nothing at all — and
            // put the continent in front of it, which the row does know for sure
            if (string.IsNullOrEmpty(name) && count > 0)
            {
                name = string.Format(LangFile.Get("n_of_m", "{0} of {1}"), idx + 1, count);
                string cat = LastEnumValue > 0 ? ResolveCategory(LastEnumValue) : null;
                if (!string.IsNullOrEmpty(cat)) name = cat + ". " + name;
            }
            if (string.IsNullOrEmpty(name)) return;

            if (idx == _lastIndex && name == _lastName && !tabChanged) return;
            _lastIndex = idx;
            _lastName = name;

            string announcement = tabChanged && !string.IsNullOrEmpty(tabText)
                ? tabText + ". " + name
                : name;

            API.LogInfo($"[SF6Access] Home select [tab {tabIdx} '{tabText ?? "-"}'][{idx}/{count}]"
                        + $" home={LastEnumValue}: {name}");
            Speak(announcement);
        }
        catch (Exception ex)
        {
            API.LogError($"[SF6Access] Home select error: {ex.Message}");
        }
    }

    /// <summary>Flag name for the focused cell: cell text first, then the home record.</summary>
    private string ResolveName(ManagedObject grid, ManagedObject list, int idx)
    {
        // Flags render as images, so this is usually empty — but if a cell ever
        // carries text it is already localized and wins
        string cellText = Sanitize(FlowHelper.ReadSelectedItemText(grid));
        if (!string.IsNullOrEmpty(cellText)) return cellText;

        if (list == null) return null;

        // app.AppDefine.CfnHome is a VALUE TYPE: FlowHelper.GetListItem hands
        // back null for struct elements (they are stored inline), so the record
        // has to be unboxed through get_Item and read by address.
        object raw = GetItemBoxed(list, idx);

        if (raw is ManagedObject mo)
        {
            string byString = ReadStringMember(mo);
            if (!string.IsNullOrEmpty(byString)) return byString;
            return ReadGuidMember(mo);
        }

        if (raw is REFrameworkNET.ValueType vt2)
            return ReadValueTypeMember(vt2);

        // app.AppDefine.CfnHome is an ENUM: boxing it through get_Item yields a
        // plain System.Int32 holding the id, and that id is the key of the
        // CfnHome user-data table (same shape as ResolveMasterMessage).
        int enumValue = -1;
        if (raw is int i32) enumValue = i32;
        else if (raw is uint u32) enumValue = (int)u32;
        else if (raw is short i16) enumValue = i16;
        if (enumValue > 0)
        {
            LastEnumValue = enumValue;

            // The whole list is mapped once, from whichever table covers most
            // of it. Coverage is what the last three rounds were missing: every
            // lookup "worked" on some rows, so nothing ever said it was wrong.
            BuildSources(list);
            if (MapUsable && _cellNames.TryGetValue(enumValue, out string mapped))
                return mapped;

            // Nothing covers the list, so pick up whatever the focused cell
            // carries — recorded, not spoken, until it has been checked
            HarvestCell(grid, idx, enumValue);

            string fromTable = ResolveFromUserData(enumValue);
            if (!string.IsNullOrEmpty(fromTable)) return fromTable;
        }

        return null;
    }

    /// <summary>The CfnHome id behind the cell read last — logged so the numbers
    /// in the log can be matched to the rows in the probe file.</summary>
    internal static int LastEnumValue;

    /// <summary>List element as REFramework boxed it — a ManagedObject for a
    /// reference element, a ValueType for a struct one.</summary>
    private static object GetItemBoxed(ManagedObject list, int idx)
    {
        try
        {
            var getItem = list?.GetTypeDefinition()?.GetMethod("get_Item(System.Int32)");
            if (getItem == null) return null;
            return getItem.InvokeBoxed(typeof(object), list, new object[] { idx });
        }
        catch { return null; }
    }

    private static readonly System.Collections.Generic.HashSet<int> _idDumpedTabs =
        new System.Collections.Generic.HashSet<int>();
    private static bool _idFileFresh;

    /// <summary>One line per tab: every region id of that tab, in DISPLAY order.
    /// The screenshots pin a country to each display position; this file pins an
    /// id to each display position. Together they are the complete, ground-truth
    /// id -> country map — no alphabetical model involved.</summary>
    private static void DumpTabIds(int tabIdx, string tabText, ManagedObject list, int count)
    {
        if (tabIdx < 0 || list == null || count <= 0 || _idDumpedTabs.Contains(tabIdx)) return;
        try
        {
            var getItem = list.GetTypeDefinition()?.GetMethod("get_Item(System.Int32)");
            if (getItem == null) return;

            var ids = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                object raw = getItem.InvokeBoxed(typeof(object), list, new object[] { i });
                ids.Append(raw is int n ? n.ToString() : "?");
                if (i < count - 1) ids.Append(',');
            }

            string path = Path.Combine(ObjectDumper.DumpDir, "sf6access_home_ids.txt");
            if (!_idFileFresh) { try { File.Delete(path); } catch { } }
            File.AppendAllText(path,
                $"tab {tabIdx} '{tabText}' count={count}\nids: {ids}\n\n");
            _idDumpedTabs.Add(tabIdx);
            _idFileFresh = true;
            API.LogInfo($"[SF6Access] Home id list dumped: tab {tabIdx} ({count} entries)");
        }
        catch (Exception ex)
        {
            API.LogError($"[SF6Access] Home id dump failed: {ex.Message}");
        }
    }

    /// <summary>The localized name inside a STRUCT home record, read by address.</summary>
    private static string ReadValueTypeMember(REFrameworkNET.ValueType vt)
    {
        var td = HomeType();
        ulong addr = vt?.GetAddress() ?? 0;
        if (td == null || addr == 0) return null;

        foreach (var f in td.GetFields())
        {
            try
            {
                if (f.IsStatic()) continue;
                string fn = CleanFieldName(f.Name);
                string ft = f.Type?.FullName ?? "";
                if (!LooksLikeName(fn)) continue;

                if (ft.Contains("String"))
                {
                    string v = f.GetDataBoxed(typeof(string), addr, false) as string;
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
                else if (ft.Contains("Guid"))
                {
                    if (f.GetDataBoxed(typeof(Guid), addr, false) is REFrameworkNET.ValueType gvt)
                    {
                        string resolved = FlowHelper.ResolveGuid(gvt);
                        if (!string.IsNullOrWhiteSpace(resolved)) return Sanitize(resolved);
                    }
                }
            }
            catch { }
        }
        return null;
    }

    private static TypeDefinition _homeTd;
    private static bool _homeTdLooked;

    private static TypeDefinition HomeType()
    {
        if (!_homeTdLooked)
        {
            _homeTdLooked = true;
            try { _homeTd = TDB.Get().FindType(HOME_STRUCT); } catch { }
        }
        return _homeTd;
    }

    // -------- every name source, run and scored at once --------
    //
    // Eight rounds of "guess one table, ask about one row" produced eight
    // plausible-looking lies. This replaces that: every candidate source is run
    // over a sample of the whole list and SCORED, so one session answers which
    // source (if any) really covers the 194 regions.

    private static readonly System.Collections.Generic.Dictionary<int, string> _cellNames =
        new System.Collections.Generic.Dictionary<int, string>();
    private static readonly System.Collections.Generic.List<int> _allIds =
        new System.Collections.Generic.List<int>();
    private static bool _mapBuilt;
    internal static bool MapUsable;
    internal static string MapInfo = "(map not built)";
    internal static readonly System.Collections.Generic.List<string> Scoreboard =
        new System.Collections.Generic.List<string>();
    internal static bool Quiet;

    private sealed class Source
    {
        public string Name;
        public System.Func<int, string> Get;
        public int Covered, Distinct;
        public string Sample = "";
        public readonly System.Collections.Generic.Dictionary<int, string> Map =
            new System.Collections.Generic.Dictionary<int, string>();
    }

    private static readonly System.Collections.Generic.List<Source> _sources =
        new System.Collections.Generic.List<Source>();

    /// <summary>Run every source over a sample of the list and keep the winner.
    /// A source only wins if it names at least 80% of the sample AND never gives
    /// two ids the same name — 194 regions cannot share one, so a repeat means the
    /// key column is wrong and the whole source is thrown out.</summary>
    private static void BuildSources(ManagedObject list)
    {
        if (_mapBuilt) return;
        _mapBuilt = true;
        Quiet = true;
        try
        {
            int total = FlowHelper.GetListCount(list);
            if (total <= 0) { MapInfo = "list empty"; return; }

            for (int i = 0; i < total; i++)
                if (GetItemBoxed(list, i) is int id && id > 0 && !_allIds.Contains(id))
                    _allIds.Add(id);
            if (_allIds.Count == 0) { MapInfo = "no readable ids"; return; }

            var sample = new System.Collections.Generic.List<int>();
            int stride = Math.Max(1, _allIds.Count / 24);
            for (int i = 0; i < _allIds.Count; i += stride) sample.Add(_allIds[i]);

            _sources.Clear();
            _sources.Add(new Source { Name = "manual (sf6access_home_names.txt)", Get = id => Learned(id, "manual") });
            _sources.Add(new Source { Name = "shipped table (UN alphabetical)", Get = ShippedName });
            _sources.Add(new Source { Name = "home.messageId", Get = StepA });
            _sources.Add(new Source { Name = COUNTRY_DICT, Get = id => TryCountryName((uint)id) });
            foreach (var t in CandidateTables())
            {
                if (string.IsNullOrEmpty(t[1]) || t[0] == COUNTRY_DICT) continue;
                string dict = t[0], rec = t[1];
                _sources.Add(new Source { Name = dict, Get = id => TableRowName(dict, rec, id) });
            }
            _sources.Add(new Source { Name = "TDB helper", Get = id => TryHelper((uint)id) });
            _sources.Add(new Source { Name = "harvested (cell/sprite)", Get = id => Harvested(id) });

            Source best = null;
            foreach (var s in _sources)
            {
                foreach (int id in sample)
                {
                    string n = null;
                    try { n = Sanitize(s.Get(id)); } catch { }
                    if (string.IsNullOrEmpty(n)) continue;
                    s.Covered++;
                    s.Map[id] = n;
                    if (s.Sample.Length < 70)
                        s.Sample += (s.Sample.Length > 0 ? " " : "") + id + "=" + n;
                }
                var uniq = new System.Collections.Generic.HashSet<string>(s.Map.Values);
                s.Distinct = uniq.Count;
                bool dup = s.Covered > s.Distinct;

                Scoreboard.Add($"  {s.Name,-34} {s.Covered,3}/{sample.Count}"
                               + $"  distinct={s.Distinct,3}{(dup ? "  DUPLICATES" : "")}"
                               + "   " + s.Sample);
                if (dup) continue;
                if (s.Covered < (int)(sample.Count * 0.8m)) continue;
                if (best == null || s.Covered > best.Covered) best = s;
            }

            _cellNames.Clear();
            int named = 0, dropped = 0;
            if (best != null)
            {
                foreach (int id in _allIds)
                {
                    string n = null;
                    try { n = Sanitize(best.Get(id)); } catch { }
                    if (string.IsNullOrEmpty(n)) continue;
                    best.Map[id] = n;
                    named++;
                }
                var counts = new System.Collections.Generic.Dictionary<string, int>();
                foreach (string v in best.Map.Values)
                    counts[v] = counts.TryGetValue(v, out int c) ? c + 1 : 1;
                foreach (var kv in best.Map)
                {
                    if (counts[kv.Value] > 1) { dropped++; continue; }
                    _cellNames[kv.Key] = kv.Value;
                }
            }

            MapUsable = _cellNames.Count >= (int)(_allIds.Count * 0.8m);
            MapInfo = $"ids={_allIds.Count} source={best?.Name ?? "(none)"} sample={sample.Count}"
                      + $" named={named} droppedDuplicates={dropped}"
                      + $" usable={MapUsable} ({_cellNames.Count}/{_allIds.Count})";
            Trace("map: " + MapInfo);
        }
        catch (Exception e) { MapInfo = "map error: " + e.Message; }
        finally { Quiet = false; }
    }

    private static string TableRowName(string dict, string rec, int id)
    {
        try
        {
            var row = FlowHelper.GetTableRecord(dict, rec, (uint)id);
            return row == null ? null : RowName(row);
        }
        catch { return null; }
    }

    /// <summary>The record's own name-ish members — without the category, which
    /// is the continent and used to be announced as if it were the country.</summary>
    private static string StepA(int enumValue)
    {
        try
        {
            var record = FlowHelper.GetTableRecord(_dictField ?? HOME_DICT, RECORD_TYPE, (uint)enumValue);
            var rd = record?.GetTypeDefinition();
            if (rd == null) return null;

            foreach (var f in rd.GetFields())
            {
                if (f.IsStatic()) continue;
                string n = CleanFieldName(f.Name);
                string t = f.Type?.FullName ?? "";
                if (!IsNameish(n) && n.IndexOf("Message", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (n.IndexOf("Category", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                string s = ReadMemberAsText(record, n, t);
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        catch { }
        return null;
    }

    // ---------------- names remembered between sessions ----------------

    private static readonly System.Collections.Generic.Dictionary<int, (string name, string src)> _learned =
        new System.Collections.Generic.Dictionary<int, (string, string)>();
    private static bool _learnedLoaded;

    private static string NamesFile => Path.Combine(ObjectDumper.DumpDir, "sf6access_home_names.txt");

    private static long _learnedStamp = -1;

    /// <summary>Re-read whenever the file changes, so a corrected order takes
    /// effect the next time the screen is opened — no rebuild, no restart.</summary>
    private static void LoadLearned()
    {
        try
        {
            if (!File.Exists(NamesFile)) return;
            long stamp = new FileInfo(NamesFile).LastWriteTimeUtc.Ticks;
            if (_learnedLoaded && stamp == _learnedStamp) return;
            _learnedStamp = stamp;
            _learnedLoaded = true;
            _learned.Clear();
            foreach (var line in File.ReadAllLines(NamesFile))
            {
                var p = line.Split('\t');
                if (p.Length < 3) continue;
                if (!int.TryParse(p[0], out int id)) continue;
                _learned[id] = (p[1], p[2]);
            }
        }
        catch { }
    }

    /// <summary>A remembered name, optionally only from one trusted source.
    /// "manual" entries are the ones verified by a human and are always used.</summary>
    private static string Learned(int id, string onlySource)
    {
        LoadLearned();
        if (_learned.TryGetValue(id, out var v) && (onlySource == null || v.src == onlySource))
            return v.name;
        return null;
    }

    // ---------------- harvesting the focused cell ----------------

    private static readonly System.Collections.Generic.Dictionary<int, string> _harvest =
        new System.Collections.Generic.Dictionary<int, string>();

    private static string Harvested(int id) => _harvest.TryGetValue(id, out string v) ? v : null;

    /// <summary>
    /// The flags are images, so if any word is attached to a cell it is on the
    /// cell object or its sprite. Every string found on the focused cell is
    /// recorded (once per id) — harvested text is NOT spoken until it has been
    /// checked, but it is written to the table file so one look settles it.
    /// </summary>
    private static void HarvestCell(ManagedObject grid, int idx, int enumValue)
    {
        if (grid == null || enumValue <= 0 || _harvest.ContainsKey(enumValue)) return;
        var found = new System.Collections.Generic.List<string>();
        try
        {
            var gtd = grid.GetTypeDefinition();
            if (gtd == null) return;

            foreach (var m in gtd.GetMethods())
            {
                string mn = m.Name ?? "";
                string rt = m.ReturnType?.FullName ?? "";
                if (rt == "System.Void" || rt.StartsWith("System.Int") || rt.StartsWith("System.UInt")
                    || rt == "System.Boolean" || rt == "System.Single" || rt == "System.String") continue;

                bool wanted = mn.IndexOf("Item", StringComparison.OrdinalIgnoreCase) >= 0
                              || mn.IndexOf("Cell", StringComparison.OrdinalIgnoreCase) >= 0
                              || mn.IndexOf("Select", StringComparison.OrdinalIgnoreCase) >= 0
                              || mn.IndexOf("Current", StringComparison.OrdinalIgnoreCase) >= 0
                              || mn.IndexOf("Part", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!wanted) continue;

                var ps = m.GetParameters();
                if (ps != null && ps.Count > 1) continue;
                object[] a = (ps == null || ps.Count == 0) ? new object[0] : new object[] { idx };

                object cellObj;
                try { cellObj = m.InvokeBoxed(typeof(object), grid, a); } catch { continue; }
                var cell = cellObj as ManagedObject;
                if (cell == null) continue;

                var ctd = cell.GetTypeDefinition();
                if (ctd != null)
                {
                    foreach (var f in ctd.GetFields())
                    {
                        if (f.IsStatic()) continue;
                        string ft = f.Type?.FullName ?? "";
                        if (!ft.Contains("String")) continue;
                        string fn = CleanFieldName(f.Name);
                        string v = null;
                        try { v = FlowHelper.ReadStringField(cell, fn); } catch { }
                        if (!string.IsNullOrWhiteSpace(v)) found.Add($"{mn}.{fn}={Sanitize(v)}");
                    }
                }
                try
                {
                    foreach (var t in GuiTextReader.ReadControlTexts(cell, false))
                        if (!string.IsNullOrEmpty(t.Text)) found.Add($"{mn}.text[{t.Name}]={t.Text}");
                }
                catch { }

                // A name can also sit behind a getter rather than in a field
                if (ctd != null)
                {
                    foreach (var g in ctd.GetMethods())
                    {
                        string gn = g.Name ?? "";
                        if (!gn.StartsWith("get_")) continue;
                        var gp = g.GetParameters();
                        if (gp != null && gp.Count > 0) continue;
                        string rt2 = g.ReturnType?.FullName ?? "";
                        if (!rt2.Contains("String") && !rt2.Contains("Guid")) continue;
                        try
                        {
                            object gv = g.InvokeBoxed(typeof(object), cell, null);
                            if (gv == null) continue;
                            string s = gv as string;
                            if (!string.IsNullOrWhiteSpace(s)) found.Add($"{mn}.{gn}()={Sanitize(s)}");
                        }
                        catch { }
                    }
                }

                // The flag is a sprite, so the country code is far more likely to
                // be in a control/sprite NAME than in any text
                try
                {
                    var tree = new StringBuilder();
                    GuiTextReader.DumpControlTree(cell, tree, 0);
                    int k = 0;
                    foreach (var l in tree.ToString().Split('\n'))
                    {
                        if (string.IsNullOrWhiteSpace(l)) continue;
                        if (++k > 25) break;
                        found.Add($"{mn}.tree {l.Trim()}");
                    }
                }
                catch { }
            }
        }
        catch (Exception e) { found.Add("<" + e.Message + ">"); }

        string joined = found.Count == 0 ? "(nothing)" : string.Join(" | ", found);
        _harvest[enumValue] = joined;
        Trace($"harvest home={enumValue}: {joined}");
    }

    /// <summary>Everything known about every region, in one file. Written when
    /// the screen closes so a single visit captures what the run harvested.</summary>
    private void WriteTable()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("home_id\tgame_continent\tspoken_name\ttable_continent\tharvested");
            foreach (int id in _allIds)
            {
                string best = _cellNames.TryGetValue(id, out string b) ? b : "";
                string hv = Harvested(id) ?? "";
                string cat = ResolveCategory(id) ?? "";
                string mine = RegionRow(id)?[3] ?? "";
                sb.AppendLine($"{id}\t{cat}\t{best}\t{mine}\t{hv}");
            }
            File.WriteAllText(Path.Combine(ObjectDumper.DumpDir, "sf6access_home_table.txt"), sb.ToString());
            API.LogInfo($"[SF6Access] Home table written ({_allIds.Count} rows)");
            WriteContinents();
        }
        catch (Exception e) { API.LogError($"[SF6Access] Home table failed: {e.Message}"); }
    }

    // ---------------- the region names, shipped with the mod ----------------
    //
    // This screen draws 194 FLAGS and no text whatsoever: every message GUID in
    // app.CfnHomeUserDataRecord is all zeroes, no dictionary in app.TableDataManager
    // covers the 194 ids with a name, and the scene holds no text for them. There
    // is nothing left to read, so the names come with the mod.
    //
    // The mapping below is not a model any more — it was taken from the game:
    //   * DumpTabIds captured each continent tab's full id list (the sets are the
    //     game's own);
    //   * screenshots of all six tabs (193 flags) proved the display order inside
    //     every tab is English-alphabetical, so the n-th alphabetical country of a
    //     continent IS the n-th id of that continent's ascending id list;
    //   * two legacy names shift positions and were proved on screen:
    //         Swaziland (not Eswatini)  -> between Sudan and Tanzania (id 165)
    //         The former Yugoslav Republic of Macedonia -> between Switzerland
    //         and Ukraine (id 172)
    // Every (tab index -> id) pair ever observed in the logs matches this table.
    // Id 169 is unused by the game; id 999 is the "no region" cell.
    //
    // Belt and braces: every name is still checked at runtime against the
    // continent the game itself stores for that id (ShippedName). If they ever
    // disagree the name is NOT spoken — the cell falls back to
    // "continent, N of 194" rather than lying.

    private static readonly string[] RegionTable =
    {
        // id | name (zh) | name (en) | continent, keyed by CfnHome id.
        "1|阿富汗|Afghanistan|Asia",
        "2|阿尔巴尼亚|Albania|Europe",
        "3|阿尔及利亚|Algeria|Africa",
        "4|安道尔|Andorra|Europe",
        "5|安哥拉|Angola|Africa",
        "6|安提瓜和巴布达|Antigua and Barbuda|NA",
        "7|阿根廷|Argentina|SA",
        "8|亚美尼亚|Armenia|Asia",
        "9|澳大利亚|Australia|Oceania",
        "10|奥地利|Austria|Europe",
        "11|阿塞拜疆|Azerbaijan|Asia",
        "12|巴哈马|Bahamas|NA",
        "13|巴林|Bahrain|Asia",
        "14|孟加拉国|Bangladesh|Asia",
        "15|巴巴多斯|Barbados|NA",
        "16|白俄罗斯|Belarus|Europe",
        "17|比利时|Belgium|Europe",
        "18|伯利兹|Belize|NA",
        "19|贝宁|Benin|Africa",
        "20|不丹|Bhutan|Asia",
        "21|玻利维亚|Bolivia|SA",
        "22|波斯尼亚和黑塞哥维那|Bosnia and Herzegovina|Europe",
        "23|博茨瓦纳|Botswana|Africa",
        "24|巴西|Brazil|SA",
        "25|文莱|Brunei|Asia",
        "26|保加利亚|Bulgaria|Europe",
        "27|布基纳法索|Burkina Faso|Africa",
        "28|布隆迪|Burundi|Africa",
        "29|佛得角|Cabo Verde|Africa",
        "30|柬埔寨|Cambodia|Asia",
        "31|喀麦隆|Cameroon|Africa",
        "32|加拿大|Canada|NA",
        "33|中非|Central African Republic|Africa",
        "34|乍得|Chad|Africa",
        "35|智利|Chile|SA",
        "36|中国|China|Asia",
        "37|哥伦比亚|Colombia|SA",
        "38|科摩罗|Comoros|Africa",
        "39|刚果（布）|Congo|Africa",
        "40|哥斯达黎加|Costa Rica|NA",
        "41|科特迪瓦|Cote d'Ivoire|Africa",
        "42|克罗地亚|Croatia|Europe",
        "43|古巴|Cuba|NA",
        "44|塞浦路斯|Cyprus|Asia",
        "45|捷克|Czechia|Europe",
        "46|朝鲜|North Korea|Asia",
        "47|刚果（金）|Democratic Republic of the Congo|Africa",
        "48|丹麦|Denmark|Europe",
        "49|吉布提|Djibouti|Africa",
        "50|多米尼克|Dominica|NA",
        "51|多米尼加|Dominican Republic|NA",
        "52|厄瓜多尔|Ecuador|SA",
        "53|埃及|Egypt|Africa",
        "54|萨尔瓦多|El Salvador|NA",
        "55|赤道几内亚|Equatorial Guinea|Africa",
        "56|厄立特里亚|Eritrea|Africa",
        "57|爱沙尼亚|Estonia|Europe",
        "58|埃塞俄比亚|Ethiopia|Africa",
        "59|斐济|Fiji|Oceania",
        "60|芬兰|Finland|Europe",
        "61|法国|France|Europe",
        "62|加蓬|Gabon|Africa",
        "63|冈比亚|Gambia|Africa",
        "64|格鲁吉亚|Georgia|Asia",
        "65|德国|Germany|Europe",
        "66|加纳|Ghana|Africa",
        "67|希腊|Greece|Europe",
        "68|格林纳达|Grenada|NA",
        "69|危地马拉|Guatemala|NA",
        "70|几内亚|Guinea|Africa",
        "71|几内亚比绍|Guinea-Bissau|Africa",
        "72|圭亚那|Guyana|SA",
        "73|海地|Haiti|NA",
        "74|洪都拉斯|Honduras|NA",
        "75|匈牙利|Hungary|Europe",
        "76|冰岛|Iceland|Europe",
        "77|印度|India|Asia",
        "78|印度尼西亚|Indonesia|Asia",
        "79|伊朗|Iran|Asia",
        "80|伊拉克|Iraq|Asia",
        "81|爱尔兰|Ireland|Europe",
        "82|以色列|Israel|Asia",
        "83|意大利|Italy|Europe",
        "84|牙买加|Jamaica|NA",
        "85|日本|Japan|Asia",
        "86|约旦|Jordan|Asia",
        "87|哈萨克斯坦|Kazakhstan|Asia",
        "88|肯尼亚|Kenya|Africa",
        "89|基里巴斯|Kiribati|Oceania",
        "90|科威特|Kuwait|Asia",
        "91|吉尔吉斯斯坦|Kyrgyzstan|Asia",
        "92|老挝|Laos|Asia",
        "93|拉脱维亚|Latvia|Europe",
        "94|黎巴嫩|Lebanon|Asia",
        "95|莱索托|Lesotho|Africa",
        "96|利比里亚|Liberia|Africa",
        "97|利比亚|Libya|Africa",
        "98|列支敦士登|Liechtenstein|Europe",
        "99|立陶宛|Lithuania|Europe",
        "100|卢森堡|Luxembourg|Europe",
        "101|马达加斯加|Madagascar|Africa",
        "102|马拉维|Malawi|Africa",
        "103|马来西亚|Malaysia|Asia",
        "104|马尔代夫|Maldives|Asia",
        "105|马里|Mali|Africa",
        "106|马耳他|Malta|Europe",
        "107|马绍尔群岛|Marshall Islands|Oceania",
        "108|毛里塔尼亚|Mauritania|Africa",
        "109|毛里求斯|Mauritius|Africa",
        "110|墨西哥|Mexico|NA",
        "111|密克罗尼西亚|Micronesia|Oceania",
        "112|摩尔多瓦|Moldova|Europe",
        "113|蒙古|Mongolia|Asia",
        "114|摩纳哥|Monaco|Europe",
        "115|摩洛哥|Morocco|Africa",
        "116|莫桑比克|Mozambique|Africa",
        "117|缅甸|Myanmar|Asia",
        "118|纳米比亚|Namibia|Africa",
        "119|瑙鲁|Nauru|Oceania",
        "120|尼泊尔|Nepal|Asia",
        "121|黑山|Montenegro|Europe",
        "122|新西兰|New Zealand|Oceania",
        "123|尼加拉瓜|Nicaragua|NA",
        "124|尼日尔|Niger|Africa",
        "125|尼日利亚|Nigeria|Africa",
        "126|荷兰|Netherlands|Europe",
        "127|阿曼|Oman|Asia",
        "128|巴基斯坦|Pakistan|Asia",
        "129|帕劳|Palau|Oceania",
        "130|巴拿马|Panama|NA",
        "131|巴布亚新几内亚|Papua New Guinea|Oceania",
        "132|巴拉圭|Paraguay|SA",
        "133|秘鲁|Peru|SA",
        "134|菲律宾|Philippines|Asia",
        "135|挪威|Norway|Europe",
        "136|波兰|Poland|Europe",
        "137|卡塔尔|Qatar|Asia",
        "138|韩国|Republic of Korea|Asia",
        "139|葡萄牙|Portugal|Europe",
        "140|罗马尼亚|Romania|Europe",
        "141|俄罗斯|Russia|Europe",
        "142|卢旺达|Rwanda|Africa",
        "143|圣基茨和尼维斯|Saint Kitts and Nevis|NA",
        "144|圣卢西亚|Saint Lucia|NA",
        "145|圣文森特和格林纳丁斯|Saint Vincent and the Grenadines|NA",
        "146|萨摩亚|Samoa|Oceania",
        "147|圣马力诺|San Marino|Europe",
        "148|圣多美和普林西比|Sao Tome and Principe|Africa",
        "149|沙特阿拉伯|Saudi Arabia|Asia",
        "150|塞内加尔|Senegal|Africa",
        "151|塞尔维亚|Serbia|Europe",
        "152|塞舌尔|Seychelles|Africa",
        "153|塞拉利昂|Sierra Leone|Africa",
        "154|新加坡|Singapore|Asia",
        "155|斯洛伐克|Slovakia|Europe",
        "156|斯洛文尼亚|Slovenia|Europe",
        "157|所罗门群岛|Solomon Islands|Oceania",
        "158|索马里|Somalia|Africa",
        "159|南非|South Africa|Africa",
        "160|南苏丹|South Sudan|Africa",
        "161|西班牙|Spain|Europe",
        "162|斯里兰卡|Sri Lanka|Asia",
        "163|苏丹|Sudan|Africa",
        "164|苏里南|Suriname|SA",
        "165|斯威士兰|Eswatini|Africa",
        "166|瑞典|Sweden|Europe",
        "167|瑞士|Switzerland|Europe",
        "168|叙利亚|Syria|Asia",
        "170|塔吉克斯坦|Tajikistan|Asia",
        "171|泰国|Thailand|Asia",
        "172|北马其顿|North Macedonia|Europe",
        "173|东帝汶|Timor-Leste|Asia",
        "174|多哥|Togo|Africa",
        "175|汤加|Tonga|Oceania",
        "176|特立尼达和多巴哥|Trinidad and Tobago|NA",
        "177|突尼斯|Tunisia|Africa",
        "178|土耳其|Turkey|Asia",
        "179|土库曼斯坦|Turkmenistan|Asia",
        "180|图瓦卢|Tuvalu|Oceania",
        "181|乌干达|Uganda|Africa",
        "182|乌克兰|Ukraine|Europe",
        "183|阿拉伯联合酋长国|United Arab Emirates|Asia",
        "184|英国|United Kingdom|Europe",
        "185|坦桑尼亚|Tanzania|Africa",
        "186|美国|United States|NA",
        "187|乌拉圭|Uruguay|SA",
        "188|乌兹别克斯坦|Uzbekistan|Asia",
        "189|瓦努阿图|Vanuatu|Oceania",
        "190|委内瑞拉|Venezuela|SA",
        "191|越南|Vietnam|Asia",
        "192|也门|Yemen|Asia",
        "193|赞比亚|Zambia|Africa",
        "194|津巴布韦|Zimbabwe|Africa",    };


    private static readonly System.Collections.Generic.Dictionary<int, string[]> _regionRows =
        new System.Collections.Generic.Dictionary<int, string[]>();

    private static string[] RegionRow(int id)
    {
        if (_regionRows.TryGetValue(id, out var cached)) return cached;
        string[] row = null;
        foreach (var line in RegionTable)
        {
            var p = line.Split('|');
            if (p.Length < 4) continue;
            if (int.TryParse(p[0], out int n) && n == id) { row = p; break; }
        }
        _regionRows[id] = row;
        return row;
    }

    private static readonly System.Collections.Generic.HashSet<int> _continentRejects =
        new System.Collections.Generic.HashSet<int>();

    /// <summary>The shipped name for a region, in the game language. A lang-file
    /// entry (key <c>home.japan</c>) wins, so a name can be corrected or added in
    /// another language without rebuilding the mod.</summary>
    private static string ShippedName(int id)
    {
        // CfnHome 999 is the "no region" cell that leads the 'all' tab
        if (id == 999)
        {
            var lang0 = FlowHelper.GetDisplayLang();
            return lang0 == FlowHelper.UiLang.ZhHans || lang0 == FlowHelper.UiLang.ZhHant
                ? "未设置" : "Not set";
        }

        var row = RegionRow(id);
        if (row == null) return null;

        // The continent is the one thing the game really knows about a region. If
        // our ordering ever drifted this catches it, and the name stays silent.
        string theirs = ContinentCode(ResolveCategory(id));
        if (!string.IsNullOrEmpty(theirs) && !string.Equals(row[3], theirs, StringComparison.Ordinal))
        {
            if (_continentRejects.Add(id))
                API.LogInfo($"[SF6Access] Home name mismatch: id {id} table={row[3]} game={theirs}");
            return null;
        }

        string viaLangFile = LangFile.GetByText("home", row[2]);
        if (!string.IsNullOrEmpty(viaLangFile) && viaLangFile != row[2]) return viaLangFile;

        var lang = FlowHelper.GetDisplayLang();
        return lang == FlowHelper.UiLang.ZhHans || lang == FlowHelper.UiLang.ZhHant ? row[1] : row[2];
    }

    /// <summary>The game continent word mapped to the code used in the table;
    /// null when the word is not recognised (the check is then skipped).</summary>
    private static string ContinentCode(string gameWord)
    {
        if (string.IsNullOrEmpty(gameWord)) return null;
        switch (gameWord)
        {
            case "非洲": case "Africa": case "アフリカ": return "Africa";
            case "亚洲": case "Asia": case "アジア": return "Asia";
            case "欧洲": case "欧州": case "Europe": case "ヨーロッパ": return "Europe";
            case "南美洲": case "South America": case "南アメリカ": return "SA";
            case "北美洲": case "North America": case "北アメリカ": return "NA";
            case "大洋洲": case "Oceania": case "オセアニア": return "Oceania";
        }
        return null;
    }

    // ---------------- id -> localized name (user-data table) ----------------

    private const string RECORD_TYPE = "CfnHomeUserDataRecord";
    // The home record carries no text itself: messageId holds a message object
    // whose GUID resolves through via.gui.message.get, and the numeric id inside
    // it may also point at the second table (CountryMessageUserDataRecord).
    private const string HOME_DICT = "CfnHomeUserDataDict";
    private const string COUNTRY_DICT = "CountryMessageUserDataDict";
    private const string COUNTRY_RECORD = "CountryMessageUserDataRecord";

    private static string _dictField;
    private static bool _dictLooked;
    /// <summary>Set once, for the probe: what actually resolved the name.</summary>
    internal static string LastResolvePath;

    /// <summary>Every reading attempt of the first lookup, in order. Written to
    /// the probe file so the next round diagnose instead of guess.</summary>
    internal static readonly System.Collections.Generic.List<string> AttemptLog =
        new System.Collections.Generic.List<string>();

    internal static void Trace(string line)
    {
        // Bulk scoring runs every source over dozens of ids — those traces would
        // bury the ones that matter
        if (Quiet) return;
        try { if (AttemptLog.Count < 300) AttemptLog.Add(line); } catch { }
    }

    /// <summary>Localized region name for a CfnHome enum id.
    ///
    /// The record carries no text of its own: <c>messageId</c> is a message row
    /// object whose GUID resolves through <c>via.gui.message.get</c> — the same
    /// shape <see cref="FlowHelper.ResolveMasterMessage"/> uses for WT masters.
    /// Everything else (the numeric ids inside that row, the second country
    /// table, and any id→name helper in the type database) is tried in order,
    /// and EVERY attempt is traced for the probe file.</summary>
    private static string ResolveFromUserData(int enumValue)
    {
        try
        {
            var tdm0 = API.GetManagedSingleton("app.TableDataManager");
            if (tdm0 == null) { Trace("TableDataManager missing"); return null; }

            if (!_dictLooked)
            {
                _dictLooked = true;
                // Kept adaptive so a renamed field shows up in the probe instead
                // of silently disabling the whole readout
                _dictField = FindDictField(tdm0, RECORD_TYPE) ?? HOME_DICT;
                Trace($"dict field = {_dictField ?? "(none)"}");
            }
            if (_dictField == null) return null;

            var record = FlowHelper.GetTableRecord(_dictField, RECORD_TYPE, (uint)enumValue);
            if (record == null)
            {
                Trace($"record MISSING: {_dictField}[{enumValue}]");
                LastResolvePath = $"no record: {_dictField}[{enumValue}]";
                return null;
            }
            Trace($"record found: {record.GetTypeDefinition()?.GetFullName()} via {_dictField}[{enumValue}]");

            // A. Anything on the record itself that could carry the name
            var rd = record.GetTypeDefinition();
            if (rd != null)
            {
                foreach (var f in rd.GetFields())
                {
                    if (f.IsStatic()) continue;
                    string n = CleanFieldName(f.Name);
                    string t = f.Type?.FullName ?? "";
                    if (!IsNameish(n) && n.IndexOf("Message", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    // homeCategoryId is the CONTINENT, not the region. It used to
                    // win here and every cell in Asia was announced as "Asia" —
                    // which is how the readout ended up both wrong and partial.
                    if (n.IndexOf("Category", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    string s = ReadMemberAsText(record, n, t);
                    Trace($"  record.{n} ({t}) -> {Show(s)}");
                    if (!string.IsNullOrEmpty(s)) return Win($"{_dictField}[{enumValue}].{n}", s, enumValue);
                }
            }

            // B. The country table keyed by the same id — DIAGNOSTIC ONLY.
            //    It looked right (key 2 -> Japan, matching InitialHomeId=2), but
            //    coverage and the player both say it isn't: the table holds 16
            //    rows (Nashal, Japan, China, ... the in-world nations) against
            //    193 home rows, and the names that DO come out don't match the
            //    flags. Never spoken.
            Trace($"  [diag] {COUNTRY_DICT}#{enumValue} -> {Show(TryCountryName((uint)enumValue))}");

            // C. Diagnostic only: what the OTHER numeric ids on the row map to.
            //    Never spoken — same-key pairing above is the lookup.
            foreach (uint id in CandidateIds(record))
                Trace($"  [diag] {COUNTRY_DICT}#{id} -> {Show(TryCountryName(id))}");

            // D. The country table only covers the ~16 in-world nations (Nashal,
            //    Japan, China, ...): country rows=16 against 193 home rows. The
            //    full 194-region list has to sit in ANOTHER user-data table
            //    keyed by the same id, so try every candidate once and keep the
            //    one that answers.
            string viaOther = TryOtherTable((uint)enumValue);
            if (!string.IsNullOrEmpty(viaOther))
                return Win(WinningTable + "#" + enumValue, viaOther, enumValue);

            LastResolvePath = $"{_dictField}[{enumValue}] nothing resolved";
            return null;
        }
        catch (Exception e) { Trace("EXCEPTION: " + e.Message); return null; }
    }

    private static readonly System.Collections.Generic.Dictionary<string, int> _nameOwner =
        new System.Collections.Generic.Dictionary<string, int>();
    private static readonly System.Collections.Generic.HashSet<string> _rejected =
        new System.Collections.Generic.HashSet<string>();

    private static string Win(string path, string name, int homeId)
    {
        LastResolvePath = path + " -> " + name;

        // 194 different regions cannot share one name. A name that comes back
        // for two different ids is a lookup keyed by the wrong column — that is
        // exactly how one readout collapsed all 194 flags onto six countries —
        // so it is refused and never spoken again: better the position than a lie.
        if (string.IsNullOrEmpty(name)) return null;
        if (_rejected.Contains(name)) { Trace($"  REJECTED (known duplicate) '{name}'"); return null; }
        if (_nameOwner.TryGetValue(name, out int other))
        {
            if (other != homeId)
            {
                _rejected.Add(name);
                Trace($"  REJECTED '{name}': already spoken for home {other}, not {homeId}");
                return null;
            }
        }
        else
        {
            _nameOwner[name] = homeId;
        }
        return name;
    }

    private static readonly System.Collections.Generic.Dictionary<int, string> _categories =
        new System.Collections.Generic.Dictionary<int, string>();

    /// <summary>
    /// The continent a region belongs to. This is the one name on the home row
    /// that IS trustworthy: the category tabs filter by the very same value, so
    /// it comes from the row itself rather than from a guessed join. It is spoken
    /// when the region name cannot be found — "Asia, 3 of 194" beats a wrong
    /// country, and it is something the player can check by switching tabs.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<int, int> _catIds =
        new System.Collections.Generic.Dictionary<int, int>();

    private static string ResolveCategory(int enumValue)
    {
        if (_categories.TryGetValue(enumValue, out string cached)) return cached;
        string result = null;
        int cat = 0;
        try
        {
            var record = FlowHelper.GetTableRecord(_dictField ?? HOME_DICT, RECORD_TYPE, (uint)enumValue);
            if (record != null)
            {
                cat = ReadIntMember(record, "homeCategoryId");
                if (cat > 0) result = CategoryName(cat);
            }
        }
        catch { }
        _catIds[enumValue] = cat;
        _categories[enumValue] = result;
        return result;
    }

    /// <summary>The continent the game files every region under, one file. This is
    /// the only fact about a region the game actually knows, so it is the only
    /// thing the shipped name order can be checked against — and it is written
    /// separately so a wrong guess shows up as a number, not as silence.</summary>
    private void WriteContinents()
    {
        try
        {
            var sb = new StringBuilder();
            int resolved = 0, matched = 0, total = 0, differ = 0;
            var rows = new System.Collections.Generic.List<string>();
            foreach (int id in _allIds)
            {
                if (id <= 0) continue;
                total++;
                string cat = ResolveCategory(id) ?? "";
                if (!string.IsNullOrEmpty(cat)) resolved++;
                string mine = RegionRow(id)?[3] ?? "";
                string code = ContinentCode(cat);
                string verdict = code == null ? "?" : (code == mine ? "ok" : "DIFF");
                if (verdict == "DIFF") differ++;
                else if (verdict == "ok") matched++;
                string catId = _catIds.TryGetValue(id, out int c) ? c.ToString() : "";
                rows.Add($"{id}\t{catId}\t{cat}\t{mine}\t{verdict}");
            }
            sb.AppendLine($"# resolved {resolved}/{total}  same continent as the shipped table {matched}  DIFF {differ}");
            sb.AppendLine("home_id\tcategory_id\tgame_continent\ttable_continent\tverdict");
            foreach (string r in rows) sb.AppendLine(r);
            File.WriteAllText(
                Path.Combine(ObjectDumper.DumpDir, "sf6access_home_continents.txt"), sb.ToString());
            API.LogInfo($"[SF6Access] Home continents written: resolved {resolved}/{total}, "
                        + $"same continent {matched}, DIFF {differ}");
        }
        catch (Exception e) { API.LogError($"[SF6Access] Home continents failed: {e.Message}"); }
    }

    private static Method _catName;
    private static bool _catNameLooked;

    /// <summary>The continent word for a category id. The category rows carry no
    /// readable name of their own — the screen goes through this utility.</summary>
    private static string CategoryName(int catId)
    {
        try
        {
            if (!_catNameLooked)
            {
                _catNameLooked = true;
                _catName = MemberAccess.FindMethod(
                    TDB.Get().FindType("app.UICFNHomeCategoryUtility"),
                    "GetHomeCategoryName(System.Int32)", logMiss: false);
            }
            if (_catName == null) return null;
            return Sanitize(_catName.InvokeBoxed(typeof(string), null, new object[] { catId }) as string);
        }
        catch { return null; }
    }

    private static string Show(string s) => string.IsNullOrEmpty(s) ? "(none)" : "'" + s + "'";

    /// <summary>A field name worth trying for text (never the numeric bookkeeping).</summary>
    private static bool IsNameish(string name)
    {
        foreach (var w in NameWords)
            if (name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>One member read as speakable text: a string, an inline Guid, a
    /// message object, or a struct holding any of those.</summary>
    private static string ReadMemberAsText(ManagedObject owner, string memberName, string typeName)
    {
        try
        {
            if (typeName.Contains("String"))
                return Sanitize(FlowHelper.ReadStringField(owner, memberName));

            if (typeName.Contains("Guid"))
                return ReadGuidText(owner, memberName);

            var child = FlowHelper.GetObjectField(owner, memberName)
                        ?? FlowHelper.ReadMember(owner, memberName) as ManagedObject;
            if (child != null) return ReadObjectAsText(child);

            if (FlowHelper.ReadMember(owner, memberName) is REFrameworkNET.ValueType vtx)
                return ReadStructAsText(vtx);
        }
        catch (Exception e) { Trace("      ! " + memberName + ": " + e.Message); }
        return null;
    }

    /// <summary>Every plausible name inside a child object: Guid fields first
    /// (the MasterProfile message shape), then strings, then string getters.</summary>
    private static string ReadObjectAsText(ManagedObject obj)
    {
        var td = obj?.GetTypeDefinition();
        if (td == null) return null;
        string type = td.GetFullName() ?? "?";

        try
        {
            foreach (var f in td.GetFields())
            {
                if (f.IsStatic()) continue;
                if (!(f.Type?.FullName ?? "").Contains("Guid")) continue;

                string s = ReadGuidText(obj, CleanFieldName(f.Name));
                if (!string.IsNullOrEmpty(s)) { Trace($"      {type}.{CleanFieldName(f.Name)} -> {Show(s)}"); return s; }
            }

            foreach (var f in td.GetFields())
            {
                if (f.IsStatic()) continue;
                if (!(f.Type?.FullName ?? "").Contains("String")) continue;

                string v = Sanitize(FlowHelper.ReadStringField(obj, CleanFieldName(f.Name)));
                if (!string.IsNullOrEmpty(v)) { Trace($"      {type}.{CleanFieldName(f.Name)} (string) -> {Show(v)}"); return v; }
            }

            // A message row can carry its message as a NUMERIC id instead —
            // only tried when the engine actually has that overload
            foreach (var f in td.GetFields())
            {
                if (f.IsStatic()) continue;
                string fn = CleanFieldName(f.Name);
                if (fn.IndexOf("id", StringComparison.OrdinalIgnoreCase) < 0) continue;

                int v = ReadIntMember(obj, fn);
                if (v <= 0) continue;

                string sv = MsgGetString((uint)v);
                if (!string.IsNullOrEmpty(sv)) { Trace($"      {type}.{fn}={v} as message id -> {Show(sv)}"); return sv; }
            }

            foreach (var m in td.GetMethods())
            {
                string mn = m.Name ?? "";
                var ps = m.GetParameters();
                if (ps != null && ps.Count > 0) continue;
                string rt = m.ReturnType?.FullName ?? "";

                if (rt.Contains("Guid"))
                {
                    string gv = MsgGetString(InvokeSafe(m, typeof(Guid), obj));
                    if (!string.IsNullOrEmpty(gv)) { Trace($"      {type}.{mn}() (guid) -> {Show(gv)}"); return gv; }
                }
                else if (rt.Contains("String") && IsNameish(mn))
                {
                    string sv = Sanitize(FlowHelper.CleanTags(InvokeSafe(m, typeof(string), obj) as string));
                    if (!string.IsNullOrEmpty(sv)) { Trace($"      {type}.{mn}() -> {Show(sv)}"); return sv; }
                }
            }
        }
        catch (Exception e) { Trace("      ! " + type + ": " + e.Message); }
        return null;
    }

    /// <summary>A Guid member, read every way REFramework will hand it over.</summary>
    private static string ReadGuidText(ManagedObject owner, string memberName)
    {
        // The established path (via.gui.message.get on the boxed field value)
        string direct = Sanitize(FlowHelper.ResolveGuidField(owner, memberName));
        if (!string.IsNullOrEmpty(direct)) return direct;

        try
        {
            ulong addr = FlowHelper.AddressOf(owner);
            foreach (var f in owner.GetTypeDefinition().GetFields())
            {
                if (CleanFieldName(f.Name) != memberName) continue;

                    string hex = FlowHelper.ReadGuidKey(owner, memberName);
                    Trace($"      {memberName} guid key = {hex ?? "(engine box read failed)"}");

                    // Read the 16 bytes straight out of the field's address —
                    // independent of how REFramework chooses to box them — and
                    // hand them to via.gui.message.get in a box we build
                    // ourselves (see ResolveBytes).
                    var raw = ReadGuidBytes(owner, f);
                    Trace($"      {memberName} raw bytes = {Hex(raw)}");
                    string s = ResolveBytes(raw);
                    if (!string.IsNullOrEmpty(s)) { Trace($"      {memberName} raw -> {Show(s)}"); return s; }

                    foreach (var want in new[] { typeof(Guid), typeof(object) })
                    {
                        s = MsgGetString(f.GetDataBoxed(want, addr, false));
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }

            // The property getter, which is what interface-declared members need
            var getter = owner.GetTypeDefinition().GetMethod("get_" + memberName);
            if (getter != null)
            {
                string s = MsgGetString(InvokeSafe(getter, typeof(Guid), owner))
                           ?? MsgGetString(InvokeSafe(getter, typeof(object), owner));
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        catch { }
        return null;
    }

    /// <summary>A struct member read by address (its fields have no object header).</summary>
    private static string ReadStructAsText(REFrameworkNET.ValueType vt)
    {
        try
        {
            var td = vt.GetTypeDefinition();
            ulong addr = vt.GetAddress();
            foreach (var f in td.GetFields())
            {
                if (f.IsStatic()) continue;
                string ft = f.Type?.FullName ?? "";
                if (ft.Contains("Guid"))
                {
                    string s = MsgGetString(f.GetDataBoxed(typeof(Guid), addr, true))
                               ?? MsgGetString(f.GetDataBoxed(typeof(object), addr, true));
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                else if (ft.Contains("String"))
                {
                    string v = Sanitize(f.GetDataBoxed(typeof(string), addr, true) as string);
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
        }
        catch { }
        return null;
    }

    private static object InvokeSafe(Method m, Type want, ManagedObject owner)
    {
        try { return m.InvokeBoxed(want, owner, null); } catch { return null; }
    }

    private static Method _msgGet;
    private static bool _msgGetLooked;
    private static Method _msgGetU32;
    private static bool _msgGetU32Looked;

    // ---- reading a Guid nobody has been able to box so far ----

    private static TypeDefinition _guidTd;
    private static bool _guidTdLooked;
    private static System.Reflection.FieldInfo _boxData;
    private static bool _boxDataLooked;

    private static TypeDefinition GuidType()
    {
        if (!_guidTdLooked)
        {
            _guidTdLooked = true;
            try { _guidTd = TDB.Get().FindType("System.Guid"); } catch { }
        }
        return _guidTd;
    }

    /// <summary>The private byte store inside REFramework's value wrapper
    /// (the same one <c>PinnedValue</c> reads).</summary>
    private static System.Reflection.FieldInfo BoxDataField()
    {
        if (!_boxDataLooked)
        {
            _boxDataLooked = true;
            try
            {
                _boxData = typeof(REFrameworkNET.ValueType).GetField(
                    "m_data", System.Reflection.BindingFlags.Instance
                              | System.Reflection.BindingFlags.NonPublic);
            }
            catch { }
        }
        return _boxData;
    }

    /// <summary>The 16 bytes of a Guid field, read from its raw address.</summary>
    private static byte[] ReadGuidBytes(ManagedObject owner, Field f)
    {
        try
        {
            ulong p = f.GetDataRaw(FlowHelper.AddressOf(owner), false);
            if (p == 0) return null;
            var b = new byte[16];
            System.Runtime.InteropServices.Marshal.Copy((IntPtr)(long)p, b, 0, 16);
            return b;
        }
        catch { return null; }
    }

    /// <summary>Localized text for Guid bytes we read ourselves. REFramework has
    /// no public way to build a value wrapper, so an empty one is constructed
    /// through reflection and its byte store is swapped for ours — after that it
    /// is indistinguishable from a boxed field read.</summary>
    private static string ResolveBytes(byte[] data)
    {
        if (data == null) return null;
        bool allZero = true;
        foreach (byte b in data) if (b != 0) { allZero = false; break; }
        if (allZero) return null;

        try
        {
            var td = GuidType();
            var dataField = BoxDataField();
            if (td == null || dataField == null) return null;

            object box = Activator.CreateInstance(
                typeof(REFrameworkNET.ValueType),
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic,
                null, new object[] { td }, null);
            if (box == null) return null;

            dataField.SetValue(box, data);
            return FlowHelper.ResolveGuid((REFrameworkNET.ValueType)box);
        }
        catch (Exception e) { Trace("      box resolve failed: " + e.Message); return null; }
    }

    private static string Hex(byte[] data)
    {
        if (data == null) return "(no read)";
        return BitConverter.ToString(data);
    }

    /// <summary>Some message tables are keyed by a numeric id rather than a
    /// Guid — only worth trying when such an overload exists.</summary>
    private static string MsgGetString(uint id)
    {
        if (id == 0) return null;
        try
        {
            if (!_msgGetU32Looked)
            {
                _msgGetU32Looked = true;
                _msgGetU32 = TDB.Get().FindType("via.gui.message")?.GetMethod("get(System.UInt32)");
                Trace($"      message.get(System.UInt32) overload: {(_msgGetU32 == null ? "absent" : "present")}");
            }
            if (_msgGetU32 == null) return null;
            return Sanitize(FlowHelper.CleanTags(
                _msgGetU32.InvokeBoxed(typeof(string), null, new object[] { id }) as string));
        }
        catch { return null; }
    }

    /// <summary>Localized text for a Guid, whichever shape the box came back in
    /// (REFramework value box or a plain CLR System.Guid).</summary>
    private static string MsgGetString(object guidValue)
    {
        if (guidValue == null) return null;
        if (guidValue is REFrameworkNET.ValueType vt) return Sanitize(FlowHelper.ResolveGuid(vt));
        if (guidValue is Guid g)
        {
            try
            {
                if (!_msgGetLooked)
                {
                    _msgGetLooked = true;
                    _msgGet = TDB.Get().FindType("via.gui.message")?.GetMethod("get(System.Guid)");
                }
                if (_msgGet == null) return null;
                string s = _msgGet.InvokeBoxed(typeof(string), null, new object[] { g }) as string;
                return Sanitize(FlowHelper.CleanTags(s));
            }
            catch { return null; }
        }
        return null;
    }

    /// <summary>Numeric ids on the record and inside each of its child rows —
    /// one of them is probably the key of the table that holds the name.</summary>
    private static System.Collections.Generic.List<uint> CandidateIds(ManagedObject record)
    {
        var found = new System.Collections.Generic.List<uint>();
        try
        {
            var objects = new System.Collections.Generic.List<ManagedObject> { record };
            foreach (var f in record.GetTypeDefinition().GetFields())
            {
                if (f.IsStatic()) continue;
                var child = FlowHelper.GetObjectField(record, CleanFieldName(f.Name));
                if (child != null) objects.Add(child);
            }

            foreach (var o in objects)
            {
                var otd = o.GetTypeDefinition();
                foreach (var f in otd.GetFields())
                {
                    if (f.IsStatic()) continue;
                    string ft = f.Type?.FullName ?? "";
                    if (!(ft.Contains("UInt32") || ft.Contains("Int32") || ft.Contains("Byte")
                          || ft.Contains("Home") || ft.Contains("Category") || ft.Contains("Country")))
                        continue;

                    int v = ReadIntMember(o, CleanFieldName(f.Name));
                    if (v > 0 && !found.Contains((uint)v)) found.Add((uint)v);
                }
            }
        }
        catch { }
        return found;
    }

    /// <summary>The localized name out of the second table.</summary>
    private static string TryCountryName(uint id)
    {
        try
        {
            var rec = FlowHelper.GetTableRecord(COUNTRY_DICT, COUNTRY_RECORD, id);
            if (rec == null) return null;

            foreach (var f in rec.GetTypeDefinition().GetFields())
            {
                if (f.IsStatic()) continue;
                string n = CleanFieldName(f.Name);
                string t = f.Type?.FullName ?? "";
                string s = ReadMemberAsText(rec, n, t);
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        catch { }
        return null;
    }

    /// <summary>First field of a row that carries speakable text.</summary>
    private static string RowName(ManagedObject rec)
    {
        var td = rec?.GetTypeDefinition();
        if (td == null) return null;
        try
        {
            foreach (var f in td.GetFields())
            {
                if (f.IsStatic()) continue;
                string n = CleanFieldName(f.Name);
                string t = f.Type?.FullName ?? "";
                string v = ReadMemberAsText(rec, n, t);
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        catch { }
        return null;
    }

    // ---------------- id -> name helpers in the type database ----------------

    private sealed class Helper
    {
        public Method Method;
        public ManagedObject Owner;      // null for a static method
        public string Label;
        public string ParamKind;         // u32 | i32 | enum
        public bool ReturnsString;
    }

    private static System.Collections.Generic.List<Helper> _helpers;
    private static bool _helpersScanned;
    /// <summary>Per candidate: what it answered for a first id, so a helper that
    /// returns the SAME text for two different ids can be rejected as junk.</summary>
    private static readonly System.Collections.Generic.Dictionary<int, (uint, string)> _helperFirst =
        new System.Collections.Generic.Dictionary<int, (uint, string)>();

    /// <summary>Every method in the type database that takes a numeric id (or a
    /// CfnHome) and hands back text or a message Guid — the lookup the UI uses to
    /// turn the flag image into a name has to be one of them.</summary>
    private static void ScanHelpers()
    {
        if (_helpersScanned) return;
        _helpersScanned = true;
        _helpers = new System.Collections.Generic.List<Helper>();
        try
        {
            var tdb = TDB.Get();
            uint numTypes = tdb.GetNumTypes();
            for (uint i = 0; i < numTypes; i++)
            {
                TypeDefinition t;
                try { t = tdb.GetType(i); } catch { continue; }
                string tn;
                try { tn = t?.GetFullName(); } catch { continue; }
                if (string.IsNullOrEmpty(tn) || !tn.StartsWith("app.")) continue;

                bool interesting = tn.IndexOf("CfnHome", StringComparison.OrdinalIgnoreCase) >= 0
                                || tn.IndexOf("Country", StringComparison.OrdinalIgnoreCase) >= 0
                                || tn.IndexOf("Nation", StringComparison.OrdinalIgnoreCase) >= 0
                                || tn.IndexOf("Region", StringComparison.OrdinalIgnoreCase) >= 0
                                || tn.IndexOf("Flag", StringComparison.OrdinalIgnoreCase) >= 0
                                || tn.IndexOf("TableDataManager", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!interesting) continue;

                ManagedObject singleton = null;
                foreach (var m in t.GetMethods())
                {
                    if (_helpers.Count >= 60) break;
                    string mn = m.Name ?? "";
                    if (mn.StartsWith("get_") || mn.StartsWith("set_")) continue;

                    string rt = m.ReturnType?.FullName ?? "";
                    // Reference returns are off limits (a mismatched conversion
                    // there is how this game crashes): only text or a Guid value.
                    bool retStr = rt.Contains("String");
                    bool retGuid = rt.Contains("Guid");
                    if (!retStr && !retGuid) continue;

                    // Only lookups this could plausibly be — anything else risks
                    // speaking an unrelated string as the region name
                    if (mn.IndexOf("Home", StringComparison.OrdinalIgnoreCase) < 0
                        && mn.IndexOf("Country", StringComparison.OrdinalIgnoreCase) < 0
                        && mn.IndexOf("Nation", StringComparison.OrdinalIgnoreCase) < 0
                        && mn.IndexOf("Region", StringComparison.OrdinalIgnoreCase) < 0
                        && mn.IndexOf("Flag", StringComparison.OrdinalIgnoreCase) < 0
                        && mn.IndexOf("Name", StringComparison.OrdinalIgnoreCase) < 0) continue;


                    var ps = m.GetParameters();
                    if (ps == null || ps.Count != 1) continue;
                    string pt = ps[0].Type?.FullName ?? "";
                    string kind = null;
                    if (pt == "System.UInt32") kind = "u32";
                    else if (pt == "System.Int32") kind = "i32";
                    else if (pt.IndexOf("CfnHome", StringComparison.OrdinalIgnoreCase) >= 0) kind = "enum";
                    if (kind == null) continue;

                    bool isStatic;
                    try { isStatic = m.IsStatic(); } catch { continue; }

                    ManagedObject owner = null;
                    if (!isStatic)
                    {
                        singleton ??= API.GetManagedSingleton(tn);
                        owner = singleton;
                        if (owner == null) continue;
                    }

                    _helpers.Add(new Helper
                    {
                        Method = m,
                        Owner = owner,
                        Label = tn + "." + mn + "(" + pt + ") : " + rt,
                        ParamKind = kind,
                        ReturnsString = retStr,
                    });
                }
            }
        }
        catch (Exception e) { Trace("helper scan failed: " + e.Message); }

        Trace($"helper candidates: {_helpers.Count}");
        foreach (var h in _helpers) Trace("  " + h.Label);
    }

    private static string TryHelper(uint id)
    {
        ScanHelpers();
        for (int i = 0; i < _helpers.Count; i++)
        {
            string s = InvokeHelper(_helpers[i], id);
            if (string.IsNullOrEmpty(s)) continue;

            if (_helperFirst.TryGetValue(i, out var prev))
            {
                // Constant answer across different ids — not a per-region lookup
                if (prev.Item2 == s && prev.Item1 != id) continue;
            }
            else
            {
                _helperFirst[i] = (id, s);
            }
            return s;
        }
        return null;
    }

    private static string InvokeHelper(Helper h, uint id)
    {
        try
        {
            object arg = h.ParamKind == "i32" ? (object)Convert.ToInt32(id) : (object)id;
            if (h.ReturnsString)
                return Sanitize(FlowHelper.CleanTags(
                    h.Method.InvokeBoxed(typeof(string), h.Owner, new object[] { arg }) as string));

            string viaGuid = MsgGetString(h.Method.InvokeBoxed(typeof(Guid), h.Owner, new object[] { arg }));
            if (!string.IsNullOrEmpty(viaGuid)) return viaGuid;
            return MsgGetString(h.Method.InvokeBoxed(typeof(object), h.Owner, new object[] { arg }));
        }
        catch { return null; }
    }

    /// <summary>An integer-valued member (enum ids come back boxed as int).</summary>
    private static int ReadIntMember(ManagedObject obj, string memberName)
    {
        var td = obj?.GetTypeDefinition();
        if (td == null) return -1;
        try
        {
            foreach (var f in td.GetFields())
            {
                if (f.IsStatic()) continue;
                if (CleanFieldName(f.Name) != memberName) continue;

                object boxed = f.GetDataBoxed(typeof(object), FlowHelper.AddressOf(obj), false);
                if (boxed is ManagedObject mo)
                {
                    // A boxed wrapper: try its own numeric field (value__)
                    int inner = ReadIntMember(mo, "value__");
                    if (inner >= 0) return inner;
                    continue;
                }
                if (boxed is REFrameworkNET.ValueType vt)
                {
                    int inner = ReadIntMemberFromAddress(vt);
                    if (inner >= 0) return inner;
                    continue;
                }
                if (boxed is int i) return i;
                if (boxed is uint u) return (int)u;
                if (boxed is short s) return s;
                if (boxed is byte b) return b;
            }
        }
        catch { }
        return -1;
    }

    private static int ReadIntMemberFromAddress(REFrameworkNET.ValueType vt)
    {
        try
        {
            var td = vt.GetTypeDefinition();
            ulong addr = vt.GetAddress();
            foreach (var f in td.GetFields())
            {
                if (CleanFieldName(f.Name) != "value__") continue;
                object b = f.GetDataBoxed(typeof(int), addr, false);
                if (b is int i) return i;
                if (b is uint u) return (int)u;
            }
        }
        catch { }
        return -1;
    }

    /// <summary>The TableDataManager field that holds our record type.</summary>
    private static string FindDictField(ManagedObject tdm, string recordTypeName)
    {
        var td = tdm?.GetTypeDefinition();
        if (td == null) return null;
        try
        {
            foreach (var f in td.GetFields())
            {
                if (f.IsStatic()) continue;
                string t = f.Type?.FullName ?? "";
                if (t.IndexOf(recordTypeName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return CleanFieldName(f.Name);
            }
        }
        catch { }
        return null;
    }

    // ---------------- adaptive record reading ----------------

    private static readonly string[] NameWords =
        { "Name", "Country", "Region", "Home", "Disp", "Title", "Text" };

    private static string CleanFieldName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        return raw.Replace("<", "").Replace(">k__BackingField", "");
    }

    private static bool LooksLikeName(string fieldName)
    {
        foreach (var w in NameWords)
            if (fieldName.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>A string field whose name looks like a label (name/country/region).</summary>
    private static string ReadStringMember(ManagedObject item)
    {
        var td = item?.GetTypeDefinition();
        if (td == null) return null;

        try
        {
            foreach (var f in td.GetFields())
            {
                string fn = CleanFieldName(f.Name);
                string ft = f.Type?.FullName ?? "";
                if (!ft.Contains("String") || !LooksLikeName(fn)) continue;

                string v = FlowHelper.ReadStringField(item, fn);
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
        }
        catch { }

        // A getter exposing the localized name (the world map's country label
        // comes from get_Country(), not from a field)
        try
        {
            foreach (var m in td.GetMethods())
            {
                string mn = m.Name ?? "";
                if (!mn.StartsWith("get_") || !LooksLikeName(mn)) continue;
                var ps = m.GetParameters();
                if (ps != null && ps.Count > 0) continue;
                if (!(m.ReturnType?.FullName ?? "").Contains("String")) continue;

                var result = FlowHelper.Call(item, mn) as string;
                if (!string.IsNullOrWhiteSpace(result)) return result.Trim();
            }
        }
        catch { }

        return null;
    }

    /// <summary>A localized message Guid field (via.gui.message.get).</summary>
    private static string ReadGuidMember(ManagedObject item)
    {
        var td = item?.GetTypeDefinition();
        if (td == null) return null;

        try
        {
            foreach (var f in td.GetFields())
            {
                string fn = CleanFieldName(f.Name);
                string ft = f.Type?.FullName ?? "";
                if (!ft.Contains("Guid") || !LooksLikeName(fn)) continue;

                string v = FlowHelper.ResolveGuidField(item, fn);
                if (!string.IsNullOrWhiteSpace(v)) return Sanitize(v);
            }
        }
        catch { }
        return null;
    }

    /// <summary>A dozen indices spread over the list, so one probe run shows
    /// whether EVERY row carries its own name or they collapse onto a few.</summary>
    private static int[] Samples(int total)
    {
        if (total <= 0) return new[] { 0 };
        if (total <= 12)
        {
            var all = new int[total];
            for (int i = 0; i < total; i++) all[i] = i;
            return all;
        }
        int step = total / 11;
        var picks = new System.Collections.Generic.List<int> { 0, 1, 2, 3, 4, total - 1 };
        for (int i = 6; i < 12; i++)
        {
            int v = i * step;
            if (v < total && !picks.Contains(v)) picks.Add(v);
        }
        return picks.ToArray();
    }

    /// <summary>Drop tag leftovers (an icon-only cell reads as &lt;ICON ...&gt;).</summary>
    private static string Sanitize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.Trim();
        if (t.IndexOf('<') >= 0) return null;
        return t;
    }

    // -------- which user-data table actually holds the 194 names --------

    /// <summary>Two strings per candidate table: the field name and the record
    /// type inside its RecordHolder.</summary>
    private static System.Collections.Generic.List<string[]> _tables;
    private static int _preferredTable = -1;
    private static string _winningTable;
    internal static string WinningTable => _winningTable ?? "(none)";

    private static ManagedObject _tdm;
    private static bool _tdmLooked;

    private static ManagedObject Tdm()
    {
        if (!_tdmLooked) { _tdmLooked = true; try { _tdm = API.GetManagedSingleton("app.TableDataManager"); } catch { } }
        return _tdm;
    }

    /// <summary>app.TableDataManager.RecordHolder`1&lt;app.X&gt; -> app.X</summary>
    private static string RecordTypeOf(string type)
    {
        try
        {
            int i = type.LastIndexOf('<');
            if (i < 0) return null;
            return type.Substring(i + 1).Trim().TrimEnd('>').Trim();
        }
        catch { return null; }
    }

    private static System.Collections.Generic.List<string[]> CandidateTables()
    {
        if (_tables != null) return _tables;
        var list = new System.Collections.Generic.List<string[]>();
        try
        {
            var td = Tdm()?.GetTypeDefinition();
            if (td != null)
            {
                foreach (var f in td.GetFields())
                {
                    if (f.IsStatic()) continue;
                    string field = CleanFieldName(f.Name);
                    string type = f.Type?.FullName ?? "";
                    if (type.IndexOf("Dictionary", StringComparison.Ordinal) < 0) continue;

                    string rec = RecordTypeOf(type);
                    // CountryMessageUserDataDict holds the 16 IN-WORLD nations
                    // (Nashal, Japan, China...), not the 194 regions. It answers
                    // for a handful of ids, which is how several flags ended up
                    // announced as the same — or the wrong — country.
                    if (field == COUNTRY_DICT) continue;

                    bool wanted = field.IndexOf("Home", StringComparison.OrdinalIgnoreCase) >= 0
                        || field.IndexOf("Country", StringComparison.OrdinalIgnoreCase) >= 0
                        || field.IndexOf("Nation", StringComparison.OrdinalIgnoreCase) >= 0
                        || field.IndexOf("Region", StringComparison.OrdinalIgnoreCase) >= 0
                        || field.IndexOf("Area", StringComparison.OrdinalIgnoreCase) >= 0
                        || field.IndexOf("Flag", StringComparison.OrdinalIgnoreCase) >= 0
                        || (rec != null && (rec.IndexOf("Home", StringComparison.OrdinalIgnoreCase) >= 0
                                            || rec.IndexOf("Country", StringComparison.OrdinalIgnoreCase) >= 0));
                    if (wanted) list.Add(new[] { field, rec ?? "", type });
                }
            }
            // Home tables first — that is where a region list would live
            list.Sort((a, b) => Rank(a[0]).CompareTo(Rank(b[0])));
        }
        catch { }
        _tables = list;
        return _tables;
    }

    private static int Rank(string field)
    {
        if (field.IndexOf("Home", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
        if (field.IndexOf("Country", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
        return 2;
    }

    /// <summary>A row count straight off the dictionary — one call, no unwrap.</summary>
    private static int DictCount(string dictField)
    {
        try
        {
            var dict = FlowHelper.GetObjectField(Tdm(), dictField);
            if (dict == null) return -1;
            return FlowHelper.CallInt(dict, "get_Count");
        }
        catch { return -1; }
    }

    /// <summary>Probe only: never call something that mutates the manager.</summary>
    private static bool HasSideEffects(string name)
    {
        foreach (var w in new[] { "Set", "Add", "Remove", "Clear", "Delete", "Load",
                                  "Update", "Write", "Reset", "Create", "Init", "Regist" })
            if (name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>A row straight out of a dictionary by key — no unwrapping, so it
    /// is cheap enough for a coverage sweep.</summary>
    private static ManagedObject DictRow(string dictField, uint key)
    {
        try
        {
            var dict = FlowHelper.GetObjectField(Tdm(), dictField);
            if (dict == null) return null;
            return FlowHelper.Call(dict, "get_Item", key) as ManagedObject;
        }
        catch { return null; }
    }

    /// <summary>The localized name out of whichever table really holds a row for
    /// this id. The winner is remembered, so later cells cost one lookup.</summary>
    private static string TryOtherTable(uint id)
    {
        var tables = CandidateTables();
        if (tables == null || tables.Count == 0) return null;

        var order = new System.Collections.Generic.List<int>();
        if (_preferredTable >= 0 && _preferredTable < tables.Count) order.Add(_preferredTable);
        for (int i = 0; i < tables.Count; i++)
            if (i != _preferredTable) order.Add(i);

        foreach (int i in order)
        {
            var t = tables[i];
            if (string.IsNullOrEmpty(t[1])) continue;
            try
            {
                var row = FlowHelper.GetTableRecord(t[0], t[1], id);
                if (row == null) continue;

                string s = RowName(row);
                Trace($"  table {t[0]}#{id} -> {Show(s)}");
                if (!string.IsNullOrEmpty(s))
                {
                    _preferredTable = i;
                    _winningTable = t[0];
                    return s;
                }
            }
            catch { }
        }
        return null;
    }

    // ---------------- one-off shape capture ----------------

    /// <summary>
    /// One-off shape capture: the struct's members (from the TDB, so it works
    /// even though no instance can be unboxed), how the list element comes back,
    /// and every TDB type whose name mentions Home/Country — the id→name helper
    /// has to live in one of those. The whole point of this file is to end the
    /// guessing: it is written once per game session.
    /// </summary>
    /// <summary>
    /// One-off shape capture. Everything so far has failed for the same reason:
    /// the home row itself has NO name (every message GUID in it is all zeroes),
    /// and no dictionary in TableDataManager covers the 194 ids with text. So this
    /// pass stops guessing at tables and instead asks the type database directly
    /// who can turn a CfnHome into a word — then dumps the dialog, the grid and
    /// one focused cell so the next change is made from real member names.
    /// </summary>
    private void Probe(ManagedObject list, int idx, string resolvedName)
    {
        if (_probed) return;
        _probed = true;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== HOME PROBE {DateTime.Now:yyyy-MM-dd HH:mm:ss} idx={idx} ===");
            sb.AppendLine($"resolved this frame: '{resolvedName ?? "(none)"}'");
            sb.AppendLine($"resolve path: {LastResolvePath ?? "(none)"}");
            sb.AppendLine($"map: {MapInfo}");
            sb.AppendLine();

            // 1. Who can turn a home into a word? Anything that ACCEPTS the enum
            //    is a candidate, and whatever the screen draws has to call one.
            sb.AppendLine("--- TDB methods taking app.AppDefine.CfnHome ---");
            try
            {
                var tdb = TDB.Get();
                uint num = tdb.GetNumTypes();
                for (uint i = 0; i < num; i++)
                {
                    TypeDefinition t;
                    try { t = tdb.GetType(i); } catch { continue; }
                    string tn = t?.FullName ?? "";
                    if (!tn.StartsWith("app.")) continue;

                    System.Collections.Generic.IEnumerable<Method> ms;
                    try { ms = t.GetMethods(); } catch { continue; }
                    if (ms == null) continue;

                    foreach (var m in ms)
                    {
                        var ps = m.GetParameters();
                        if (ps == null || ps.Count == 0) continue;
                        bool takes = false;
                        for (int k = 0; k < ps.Count; k++)
                        {
                            string pt = ps[k].Type?.FullName ?? "";
                            if (pt == "app.AppDefine.CfnHome") { takes = true; break; }
                        }
                        if (!takes) continue;
                        sb.AppendLine($"  {tn} :: {Sig(m)}");
                        TryCallInto(sb, m, tn, 2);
                        TryCallInto(sb, m, tn, 10);
                        TryCallInto(sb, m, tn, 50);
                    }
                }
            }
            catch (Exception e) { sb.AppendLine($"  [scan error: {e.Message}]"); }
            sb.AppendLine();

            // 2. Anything named after the concept that hands back text
            sb.AppendLine("--- TDB methods named Home/Country/Nation/Region/Flag returning text ---");
            try
            {
                var tdb = TDB.Get();
                uint num = tdb.GetNumTypes();
                int shown = 0;
                for (uint i = 0; i < num && shown < 400; i++)
                {
                    TypeDefinition t;
                    try { t = tdb.GetType(i); } catch { continue; }
                    string tn = t?.FullName ?? "";
                    if (!tn.StartsWith("app.")) continue;

                    System.Collections.Generic.IEnumerable<Method> ms;
                    try { ms = t.GetMethods(); } catch { continue; }
                    if (ms == null) continue;

                    foreach (var m in ms)
                    {
                        string mn = m.Name ?? "";
                        bool themed = mn.IndexOf("Home", StringComparison.OrdinalIgnoreCase) >= 0
                                      || mn.IndexOf("Country", StringComparison.OrdinalIgnoreCase) >= 0
                                      || mn.IndexOf("Nation", StringComparison.OrdinalIgnoreCase) >= 0
                                      || mn.IndexOf("Region", StringComparison.OrdinalIgnoreCase) >= 0
                                      || mn.IndexOf("Flag", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!themed) continue;
                        string rt = m.ReturnType?.FullName ?? "";
                        if (!(rt.Contains("String") || rt.Contains("Guid") || rt.Contains("Message"))) continue;
                        var ps = m.GetParameters();
                        if (ps != null && ps.Count > 1) continue;

                        shown++;
                        sb.AppendLine($"  {tn} :: {Sig(m)}");
                        TryCallInto(sb, m, tn, 2);
                        TryCallInto(sb, m, tn, 10);
                        TryCallInto(sb, m, tn, 50);
                    }
                }
            }
            catch (Exception e) { sb.AppendLine($"  [scan error: {e.Message}]"); }
            sb.AppendLine();

            // 3. The types most likely to hold what we need
            sb.AppendLine("--- candidate holder types ---");
            foreach (var tn in new[]
            {
                "app.UICFNSelectHomeDialog",
                "app.UICFNSelectHomeDialog.FlowParam",
                "app.CfnHomeUserData",
                "app.CfnHomeCategoryUserData",
                "app.AppDefine.CfnHomeCategory",
                "app.CfnHomeUserDataRecord",
                "app.CfnHomeUserDataRecord.CfnHomeMessage",
                "app.CountryMessageUserDataRecord.CountryNameMessage",
            }) DumpTdbType(sb, tn);
            sb.AppendLine();

            // 4. Categories — the one name lookup that does work (Asia, Europe...)
            sb.AppendLine("--- CfnHomeCategory rows ---");
            try
            {
                for (uint k = 0; k <= 10; k++)
                {
                    var r = DictRow("CfnHomeCategoryUserDataDict", k);
                    if (r == null) continue;
                    sb.AppendLine($"  {k} -> {Show(RowName(r))}");
                }
            }
            catch (Exception e) { sb.AppendLine($"  [category error: {e.Message}]"); }
            sb.AppendLine();

            // 5. The grid and one focused cell — if the flag's own sprite/resource
            //    is reachable, its name almost certainly carries the country code
            sb.AppendLine("--- flag grid ---");
            try
            {
                var grid = FlowHelper.GetObjectField(_param, GRID_FIELD);
                sb.AppendLine($"  grid = {grid?.GetTypeDefinition()?.FullName ?? "(null)"}");
                if (grid != null)
                {
                    var gtd = grid.GetTypeDefinition();
                    int n = 0;
                    foreach (var m in gtd.GetMethods())
                    {
                        if (++n > 70) { sb.AppendLine("  ..."); break; }
                        sb.AppendLine($"  method {Sig(m)}");
                    }

                    foreach (var m in gtd.GetMethods())
                    {
                        string mn = m.Name ?? "";
                        string rt = m.ReturnType?.FullName ?? "";
                        if (rt == "System.Void" || rt.StartsWith("System.Int")
                            || rt.StartsWith("System.UInt") || rt == "System.Boolean"
                            || rt == "System.Single" || rt == "System.String") continue;
                        var ps = m.GetParameters();
                        if (ps != null && ps.Count > 1) continue;

                        object[] a = (ps == null || ps.Count == 0) ? new object[0] : new object[] { idx };
                        object cell;
                        try { cell = m.InvokeBoxed(typeof(object), grid, a); }
                        catch (Exception e) { sb.AppendLine($"  {mn} -> <{e.Message}>"); continue; }

                        var cm = cell as ManagedObject;
                        if (cm == null) { sb.AppendLine($"  {mn} -> {cell?.GetType().Name ?? "(null)"}"); continue; }
                        sb.AppendLine($"  {mn} -> {cm.GetTypeDefinition()?.FullName}");

                        if (mn.IndexOf("Item", StringComparison.OrdinalIgnoreCase) >= 0
                            || mn.IndexOf("Cell", StringComparison.OrdinalIgnoreCase) >= 0
                            || mn.IndexOf("Select", StringComparison.OrdinalIgnoreCase) >= 0
                            || mn.IndexOf("Current", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            sb.AppendLine("     --- cell fields ---");
                            ObjectDumper.DumpFieldsWithValues(sb, cm.GetTypeDefinition(), cm, "     ");
                            sb.AppendLine("     --- cell texts ---");
                            foreach (var t in GuiTextReader.ReadControlTexts(cm, false))
                                sb.AppendLine($"     {t.Name} = '{t.Text}'");
                        }
                    }
                }
            }
            catch (Exception e) { sb.AppendLine($"  [grid error: {e.Message}]"); }
            sb.AppendLine();

            // 6. Everything the screen actually renders
            sb.AppendLine("--- SelectHome GUI texts ---");
            try
            {
                foreach (var t in GuiTextReader.ReadTextsByOwner("SelectHome", false))
                    sb.AppendLine($"  {t.Owner}/{t.Name} = '{t.Text}'");
            }
            catch (Exception e) { sb.AppendLine($"  [gui error: {e.Message}]"); }
            sb.AppendLine();

            // 7. If the region name is on screen anywhere, it is in here
            sb.AppendLine("--- ALL scene GUI texts (dialog open) ---");
            try
            {
                int n = 0;
                foreach (var t in GuiTextReader.ReadSceneTexts(false))
                {
                    if (string.IsNullOrEmpty(t.Text)) continue;
                    if (++n > 300) break;
                    sb.AppendLine($"  {t.Owner}/{t.Name} = '{t.Text}'");
                }
            }
            catch (Exception e) { sb.AppendLine($"  [scene gui error: {e.Message}]"); }
            sb.AppendLine();

            // 8. Every source, scored against the same sample of the list
            sb.AppendLine("--- source scoreboard ---");
            try
            {
                foreach (var l in Scoreboard) sb.AppendLine(l);
                sb.AppendLine($"  winner: {MapInfo}");
            }
            catch (Exception e) { sb.AppendLine($"  [scoreboard error: {e.Message}]"); }
            sb.AppendLine();

            sb.AppendLine("--- harvested cells ---");
            try
            {
                int n = 0;
                foreach (var kv in _harvest)
                {
                    if (++n > 60) break;
                    sb.AppendLine($"  home={kv.Key}: {kv.Value}");
                }
            }
            catch (Exception e) { sb.AppendLine($"  [harvest error: {e.Message}]"); }
            sb.AppendLine();

            sb.AppendLine("--- attempts (first lookup, in order) ---");
            foreach (var line in AttemptLog) sb.AppendLine(line);
            sb.AppendLine();
            sb.AppendLine("--- mapped cells (first 40 named) ---");
            try
            {
                int shown = 0;
                foreach (var kv in _cellNames)
                {
                    if (++shown > 40) break;
                    sb.AppendLine($"  {kv.Key} -> {kv.Value}");
                }
            }
            catch (Exception e) { sb.AppendLine($"  [map dump error: {e.Message}]"); }

            File.WriteAllText(
                Path.Combine(ObjectDumper.DumpDir, "sf6access_home_probe.txt"),
                sb.ToString());
            API.LogInfo("[SF6Access] Home probe written to sf6access_home_probe.txt");
        }
        catch (Exception ex)
        {
            API.LogError($"[SF6Access] Home probe failed: {ex.Message}");
        }
    }

    private static string Sig(Method m)
    {
        var ps = m.GetParameters();
        string args = "";
        if (ps != null)
            for (int i = 0; i < ps.Count; i++)
                args += (i > 0 ? ", " : "") + (ps[i].Type?.FullName ?? "?");
        return $"{m.ReturnType?.FullName} {m.Name}({args})";
    }

    /// <summary>Call a text-returning method with one id, if it looks callable.</summary>
    private static void TryCallInto(StringBuilder sb, Method m, string typeName, uint id)
    {
        string rt = m.ReturnType?.FullName ?? "";
        if (!(rt.Contains("String") || rt.Contains("Guid") || rt.Contains("Message"))) return;

        var ps = m.GetParameters();
        if (ps == null || ps.Count != 1) return;
        string pt = ps[0].Type?.FullName ?? "";
        if (pt != "System.UInt32" && pt != "System.Int32" && pt != "app.AppDefine.CfnHome") return;
        if (HasSideEffects(m.Name ?? "")) return;

        try
        {
            object target = typeName.IndexOf("TableDataManager", StringComparison.OrdinalIgnoreCase) >= 0
                ? Tdm() : null;
            object boxed = m.InvokeBoxed(typeof(object), target, new object[] { id });
            string res = boxed == null ? "(null)"
                : rt.Contains("String") ? Show(Sanitize(FlowHelper.CleanTags(boxed as string)))
                : Show(MsgGetString(boxed));
            sb.AppendLine($"      {m.Name}({id}) -> {res}");
        }
        catch (Exception e) { sb.AppendLine($"      {m.Name}({id}) -> <{e.Message}>"); }
    }

    private static void DumpTdbType(StringBuilder sb, string typeName)
    {
        sb.AppendLine($"  == {typeName}");
        try
        {
            var t = TDB.Get().FindType(typeName);
            if (t == null) { sb.AppendLine("     (not found)"); return; }
            try
            {
                foreach (var f in t.GetFields())
                    sb.AppendLine($"     field  {f.Type?.FullName} {f.Name}{(f.IsStatic() ? " [static]" : "")}");
            }
            catch (Exception e) { sb.AppendLine($"     [fields error: {e.Message}]"); }
            int n = 0;
            try
            {
                foreach (var m in t.GetMethods())
                {
                    if (++n > 80) { sb.AppendLine("     ..."); break; }
                    sb.AppendLine($"     method {Sig(m)}");
                }
            }
            catch (Exception e) { sb.AppendLine($"     [methods error: {e.Message}]"); }
        }
        catch (Exception e) { sb.AppendLine($"     [error: {e.Message}]"); }
    }
}
