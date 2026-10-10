using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using REFrameworkNET;
using SF6Access.Services;
using SF6Access.Services.Ui;
using SF6Access.Services.WorldTour;

namespace SF6Access.Hooks.WorldTour;

/// <summary>
/// The World Tour <b>world map</b> — the screen opened from the phone's
/// "World map" app that lists the cities you can fly to with a plane ticket
/// (<c>app.UIFlowWorldMap.*</c>).
///
/// <para><b>Why it was silent.</b> Nothing in the mod covered it: the generic
/// list reader (<see cref="Hooks.GroupFocusHooks"/>) only watches the prefixes
/// in its <c>WatchPrefixes</c> table and <c>app.UIFlowWorldMap</c> was not one of
/// them, and no dedicated screen adapter existed either — so the only thing ever
/// spoken on that screen came from the generic guide-text hook
/// ("Go to an unlocked country or region."). The three params the game creates
/// (<c>app.UIFlowWorldMap.Bg.Param</c>, <c>.Main.Param</c>,
/// <c>.SpotList.Param</c>) went unread entirely.</para>
///
/// <para><b>Read the row's data, not the rendered widget.</b> Three passes over
/// the widget tree and the detail panel failed before this one, and the
/// captures explain why (2026-10-01):
/// <list type="bullet">
/// <item><c>RootGroup</c> is only a container over the two lists, and
///   <c>SpotList</c>'s own control reports EVERY visible city at once
///   ("Colosseum. Metro City") — so moving through the list changed nothing
///   that could be heard.</item>
/// <item><c>Detail.get_Name()</c> / <c>get_Country()</c> are refreshed by
///   <c>SpotListSelectionChanged(bool delay)</c> a moment AFTER the selection
///   moves, and are not refreshed at all for a locked city — which is why
///   every row after the second one was announced as the same place.</item>
/// <item><c>SpotList.GetFocusIndex()</c> does not name the selected row: it
///   returned 0 while the row carrying the cursor was index 1, and the detail
///   panel agreed with the cursor. The selection is found by
///   <c>VisibleItem.get_Cursor()</c>.</item>
/// </list>
/// So the city is read from the row itself:
/// <c>_VisibleItems[i].get_Index() / get_Name() / get_State()</c> over nine
/// row controls drawn out of <c>_ItemCount</c> (15), plus
/// <c>Ticket.GetCount()</c> for the tickets held (without one the trip is
/// impossible) and <c>ScrollList</c> (<c>app.UIPartsScrollList</c>) for the
/// missions at the selected city.</para>
///
/// <para><b>A locked row has no name.</b> The game leaves the label empty on
/// rows it has not opened, and while the window scrolls the row controls are
/// rebound — so a locked row can carry the previous city's name for a poll or
/// two. A row marked Locked never contributes a name; it is announced by
/// position and state. The index also has to hold still for a poll or two
/// before anything is said, because a reading taken mid-move is the previous
/// row's.</para>
///
/// <para><b>Two lists, one voice.</b> Choosing a city reloads the mission list
/// under it, so a city announcement is followed by a short quiet window in
/// which the mission list may change without speaking — otherwise every move
/// down the city list would also read out that city's missions.</para>
///
/// <para><b>Safety.</b> Every engine touch is inside a try/catch: a screen
/// adapter's own exception handler silences it permanently after the first
/// fault, and that failure is invisible to the player. No method returning or
/// passing out a reference type is called here — see the note on
/// <c>AvatarBase.GetMoveVec</c> in the field-probe docs.</para>
/// </summary>
public sealed class WorldMapHooks : ScreenAdapter
{
    private const string TYPE_PREFIX = "app.UIFlowWorldMap";
    private static readonly string[] Types = { TYPE_PREFIX };

    public override string[] OwnedTypes => Types;

    private const string FIELD_SPOTS = "SpotList";
    private const string FIELD_MISSIONS = "ScrollList";
    private const string FIELD_DETAIL = "Detail";
    private const string FIELD_TICKET = "Ticket";

    /// <summary>The GUI that renders this screen. Only used as a last resort
    /// when the detail panel will not name the city.</summary>
    private const string GUI_OWNER = "ui59900";

    /// <summary>Polls after a city announcement during which the mission list
    /// may change silently (~0.5 s at ReadInterval 5). See the class remarks.</summary>
    private const int MISSION_QUIET_POLLS = 6;

    /// <summary>Polls the focused index must repeat before it is spoken
    /// (~0.17 s). The list animates and a virtual list rebinds its row
    /// controls, so the very first poll after a move still carries the
    /// previous row's name.</summary>
    private const int STABLE_POLLS = 2;

    /// <summary>Polls to keep waiting for the country to join the city
    /// (~0.4 s). The detail panel is refreshed a moment after the selection
    /// moves, and the two are worth saying in one breath.</summary>
    private const int COUNTRY_WAIT_POLLS = 5;

    /// <summary>Polls to wait before dumping a newly appeared param, so the
    /// screen has finished building (~1 s at ReadInterval 5).</summary>
    private const int DUMP_DELAY_POLLS = 12;

    /// <summary>Sub-objects dumped once per session, so a later pass can extend
    /// the reading without another round of guesswork.</summary>
    private static readonly string[] DumpSubObjects =
    {
        "Share", "Ticket", "Detail", "Right", "SpotList", "ScrollList",
        "RootGroup", "PageIndex", "DayMasterList", "NightMasterList", "mSituation",
    };

    private sealed class TrackedParam
    {
        public string TypeName;
        public ulong Address;
        public int Polls;
        public bool Dumped;
    }

    private readonly List<TrackedParam> _tracked = new();
    private string _dumpPath;
    private int _pollCounter;

    // City: the row the selection is resting on. The index has to hold still
    // for a couple of polls before anything is said — the list animates, and a
    // virtual list rebinds its row controls, so a reading taken mid-move is
    // either the wrong row or the previous row's leftover name.
    private int _pendingIndex = -2;
    private int _stablePolls;

    /// <summary>The row already spoken. Once a row has been announced it is
    /// never announced again while the selection stays on it — without this, an
    /// empty label (or one that flickers as rows are rebound) made every poll
    /// look like a new row and the list repeated itself endlessly.</summary>
    private int _announcedIndex = -2;
    private string _announcedName;
    private string _announcedCountry;

    /// <summary>Last diagnostic line logged, so the log records a move instead
    /// of twelve identical lines a second.</summary>
    private string _lastProbe;

    // Missions: same, plus the quiet window after a city announcement.
    private string _lastMission;
    private int _lastMissionIndex = -2;
    private int _missionQuietUntil;

    /// <summary>Tickets held are said once on arrival, not with every city —
    /// read per row they sounded like that city's price.</summary>
    private bool _ticketsSaid;

    public WorldMapHooks()
    {
        // The map is opened from the phone and closed again quickly; search
        // faster than the 60-frame default so the opening announcement is not
        // missed.
        SearchInterval = 15;
        ReadInterval = 5;
    }

    protected override bool Locate()
    {
        var matches = FlowHelper.FindFlowParamsByPrefix(TYPE_PREFIX);
        return matches.Count > 0;
    }

    protected override void OnActivate()
    {
        _tracked.Clear();
        _pendingIndex = -2;
        _stablePolls = 0;
        _announcedIndex = -2;
        _announcedName = null;
        _announcedCountry = null;
        _lastProbe = null;
        _lastMission = null;
        _lastMissionIndex = -2;
        _missionQuietUntil = 0;
        _ticketsSaid = false;
        _pollCounter = 0;
        API.LogInfo("[SF6Access] World map active");
        Speak(LangFile.Get("wt.worldmap.title", "World map"), interrupt: true);
    }

    protected override void OnDeactivate()
    {
        _tracked.Clear();
        API.LogInfo("[SF6Access] World map ended");
    }

    protected override void OnPoll()
    {
        var matches = FlowHelper.FindFlowParamsByPrefix(TYPE_PREFIX);
        if (matches.Count == 0) return;
        _pollCounter++;

        var live = new List<ulong>();
        foreach (var (typeName, param) in matches)
        {
            if (param == null) continue;
            ulong addr = FlowHelper.AddressOf(param);
            live.Add(addr);

            TrackedParam entry = null;
            foreach (var t in _tracked)
                if (t.Address == addr) { entry = t; break; }

            if (entry == null)
            {
                entry = new TrackedParam { TypeName = typeName, Address = addr };
                _tracked.Add(entry);
                API.LogInfo($"[SF6Access] World map param: {typeName}");
            }
            entry.Polls++;

            if (!entry.Dumped && entry.Polls >= DUMP_DELAY_POLLS)
            {
                entry.Dumped = true;
                Dump(typeName, param, entry);
            }

            ReadCity(param);
            ReadMissions(param);
        }

        for (int i = _tracked.Count - 1; i >= 0; i--)
            if (!live.Contains(_tracked[i].Address)) _tracked.RemoveAt(i);
    }

    // ------------------------------------------------------------------ city

    /// <summary>The selected city: its name, where it sits in the list, its
    /// state and — once the panel has caught up — its country.
    ///
    /// <para>Three things make this harder than reading a row of text, and each
    /// one cost a wrong announcement before it was understood:
    /// <list type="number">
    /// <item><c>GetFocusIndex()</c> does <b>not</b> name the selected row.
    ///   Captured side by side: focus index 0 while the row carrying the cursor
    ///   was index 1 — and the detail panel agreed with the cursor
    ///   (梅特隆市/美国). The selection is therefore found by the row's
    ///   <c>Cursor</c>, with the index getter only as a fallback.</item>
    /// <item>A locked row's label is empty, and while the window scrolls the
    ///   row controls are rebound — so a locked row can carry the previous
    ///   city's name for a poll or two. A row marked Locked therefore never
    ///   contributes a name; it is announced by position and state only.</item>
    /// <item>The row's own data is still the right source for an <i>open</i>
    ///   city. The detail panel is refreshed a moment later and is not
    ///   refreshed at all for locked cities, which is what made every row after
    ///   the second one announce the same place.</item>
    /// </list></para></summary>
    private void ReadCity(ManagedObject param)
    {
        try
        {
            var spots = FlowHelper.GetObjectField(param, FIELD_SPOTS);
            if (spots == null) return;
            var detail = FlowHelper.GetObjectField(param, FIELD_DETAIL);

            int count = FlowHelper.ReadIntField(spots, "_ItemCount");
            int focus = FocusIndex(spots, out var row);
            if (row == null) return;

            // Let the selection hold still: a reading taken mid-move is the
            // previous row's.
            if (focus != _pendingIndex)
            {
                _pendingIndex = focus;
                _stablePolls = 1;
                return;
            }
            _stablePolls++;

            string stateName = RowStateName(row);
            bool locked = WorldMapText.IsLocked(stateName);

            // Never trust a locked row's label — see the remarks above.
            string name = locked ? null : Clean(FlowHelper.Call(row, "get_Name") as string);
            string country = CountryFor(detail, name);
            string state = WorldMapText.State(stateName);

            Probe(focus, count, name, stateName, country);

            // A row the game has not filled in: no label AND no lock. The top
            // and bottom rows of the window are half-clipped and stay empty,
            // and a row is briefly empty while the list animates — announcing
            // those put a meaningless "1 of 15" in front of every visit to the
            // map, half a second before the city that is actually selected.
            if (string.IsNullOrEmpty(name) && !locked) return;

            bool announced = focus == _announcedIndex;
            if (announced)
            {
                // Already spoken: worth speaking again only if the row finally
                // produced a name it did not have a moment ago.
                if (string.IsNullOrEmpty(name) || name == _announcedName) return;
            }
            else
            {
                if (_stablePolls < STABLE_POLLS) return;
                // Wait for the country to join the city so the two are said in
                // one breath. The panel is refreshed a moment after the
                // selection moves; a locked row has no country to wait for.
                if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(country)
                    && _stablePolls < COUNTRY_WAIT_POLLS) return;
            }

            _announcedIndex = focus;
            _announcedName = name;
            _announcedCountry = country;

            string spoken = WorldMapText.Spot(name, focus, count, country, state)
                            ?? WorldMapText.Spot(PositionOnly(focus, count), -1, 0, null, state);
            API.LogInfo($"[SF6Access] World map city [{focus}/{count}]: name={name ?? "(none)"}" +
                        $", state={stateName ?? "(none)"}" +
                        (string.IsNullOrEmpty(country) ? "" : $", country={country}"));
            Announce(spoken, !announced);

            if (!_ticketsSaid)
            {
                _ticketsSaid = true;
                string held = WorldMapText.TicketsHeld(Tickets(param));
                if (!string.IsNullOrEmpty(held)) Speak(held, false);
            }

            // The mission list reloads under the new city — let it settle
            // silently so a city move is not followed by a mission reading.
            _missionQuietUntil = _pollCounter + MISSION_QUIET_POLLS;
        }
        catch { }
    }

    /// <summary>One log line per change while the map is being worked on: the
    /// two candidate focus signals side by side, so a disagreement between the
    /// cursor and the index getter is visible instead of being guessed at.</summary>
    private void Probe(int focus, int count, string name, string stateName, string country)
    {
        string line = $"{focus}/{count}|{name ?? "-"}|{stateName ?? "-"}|{country ?? "-"}";
        if (line == _lastProbe) return;
        _lastProbe = line;
        API.LogInfo($"[SF6Access] World map probe: {line}");
    }

    /// <summary>"3 of 15" for a row that carries no name — a locked city, whose
    /// label the game leaves empty.</summary>
    private static string PositionOnly(int index, int count) =>
        index >= 0 && count > 1
            ? string.Format(LangFile.Get("wt.worldmap.position", "{0} of {1}"), index + 1, count)
            : null;

    /// <summary>The index the selection is resting on, and the row object that
    /// goes with it. The row with a cursor wins over <c>GetFocusIndex()</c>:
    /// see the remarks on <see cref="ReadCity"/>.</summary>
    private static int FocusIndex(ManagedObject spots, out ManagedObject row)
    {
        row = null;

        int index = FlowHelper.CallInt(spots, "GetFocusIndex", -1);
        if (index < 0) index = FlowHelper.ReadIntField(spots, "_ListIndex", -1);

        var items = FlowHelper.GetObjectField(spots, "_VisibleItems");
        int rows = FlowHelper.GetListCount(items);
        if (rows <= 0) return index;

        ManagedObject byIndex = null;
        for (int i = 0; i < rows; i++)
        {
            var item = FlowHelper.GetListItem(items, i);
            if (item == null) continue;

            if (FlowHelper.CallInt(item, "get_Cursor", 0) != 0)
            {
                row = item;
                int cursorIndex = RowIndex(item);
                return cursorIndex >= 0 ? cursorIndex : index;
            }
            if (RowIndex(item) == index) byIndex = item;
        }

        row = byIndex;
        return index;
    }

    /// <summary>The row's own position in the list, independent of where it
    /// sits in the nine row controls currently drawn.</summary>
    private static int RowIndex(ManagedObject item)
    {
        int value = FlowHelper.CallInt(item, "get_Index", -1);
        if (value < 0) value = FlowHelper.ReadIntField(item, "<Index>k__BackingField", -1);
        return value;
    }

    /// <summary>The row's state as the enum's member NAME — matched on the name
    /// so a renumbered enum cannot call an open city locked.</summary>
    private static string RowStateName(ManagedObject item)
    {
        if (item == null) return null;
        int value = FlowHelper.CallInt(item, "get_State", -1);
        if (value < 0) value = FlowHelper.ReadIntField(item, "<State>k__BackingField", -1);
        return value < 0 ? null : FlowHelper.ResolveEnumName(WorldMapText.ItemStateEnum, value);
    }

    /// <summary>The country only once the panel is showing the city the list has
    /// focused — otherwise it still belongs to the previous row.</summary>
    private static string CountryFor(ManagedObject detail, string name)
    {
        if (detail == null || string.IsNullOrEmpty(name)) return null;
        string panelCity = Clean(FlowHelper.Call(detail, "get_Name") as string);
        if (panelCity == null || panelCity != name) return null;
        return Clean(FlowHelper.Call(detail, "get_Country") as string);
    }

    /// <summary>Tickets held, or -1 when the screen will not say.</summary>
    private static int Tickets(ManagedObject param)
    {
        var ticket = FlowHelper.GetObjectField(param, FIELD_TICKET);
        if (ticket == null) return -1;

        int count = FlowHelper.CallInt(ticket, "GetCount", -1);
        return count >= 0 ? count : FlowHelper.ReadIntField(ticket, "_Count", -1);
    }

    /// <summary>Last resort when the detail panel is empty: the city name the
    /// screen is actually drawing.</summary>
    private static string FirstCityText()
    {
        try
        {
            foreach (var t in GuiTextReader.ReadTextsByOwner(GUI_OWNER, visibleOnly: true))
            {
                if (t.Name != "e_text_city") continue;
                string value = Clean(t.Text);
                if (!string.IsNullOrEmpty(value)) return value;
            }
        }
        catch { }
        return null;
    }

    // -------------------------------------------------------------- missions

    /// <summary>The mission highlighted at the selected city.</summary>
    private void ReadMissions(ManagedObject param)
    {
        try
        {
            var scroll = FlowHelper.GetObjectField(param, FIELD_MISSIONS);
            if (scroll == null) return;

            int index = FlowHelper.CallInt(scroll, "get_SelectedIndex", -1);
            if (index < 0) index = SelectedByPlayState(scroll);

            string text = RowText(scroll, index) ?? MissionNameData(param, index);
            if (string.IsNullOrEmpty(text)) return;

            bool first = _lastMission == null;
            bool changed = text != _lastMission || (index >= 0 && index != _lastMissionIndex);
            _lastMission = text;
            _lastMissionIndex = index;
            if (!first && !changed) return;

            // A city announcement just happened and reloaded this list.
            if (_pollCounter < _missionQuietUntil) return;

            API.LogInfo($"[SF6Access] World map mission [{index}]: {text}");
            Announce(WorldMapText.Mission(text), false);
        }
        catch { }
    }

    /// <summary>Scroll lists that keep their selection as a PlayState rather
    /// than a SelectedIndex: find the row the GUI marks SELECT.</summary>
    private static int SelectedByPlayState(ManagedObject parts)
    {
        var control = FlowHelper.GetObjectField(parts, "Control")
                      ?? FlowHelper.Call(parts, "get_Control") as ManagedObject;
        return control == null ? -1 : GuiTextReader.FindSelectedItemIndex(control, "SELECT");
    }

    /// <summary>The mission list is also available as data
    /// (<c>List&lt;app.UIFlowWorldMap.Mission&gt;</c>: Id, Type, Name, OrderNo),
    /// which is what the row renders — used when the widget read comes back
    /// empty.</summary>
    private static string MissionNameData(ManagedObject param, int index)
    {
        if (index < 0) return null;
        var missions = FlowHelper.GetObjectField(param, "Missions");
        if (missions == null || index >= FlowHelper.GetListCount(missions)) return null;
        return Clean(FlowHelper.ReadStringField(FlowHelper.GetListItem(missions, index), "Name"));
    }

    private static string RowText(ManagedObject parts, int index)
    {
        var item = FlowHelper.Call(parts, "get_SelectedItem") as ManagedObject
                   ?? FlowHelper.Call(parts, "GetFocusChild") as ManagedObject;
        string text = item != null ? GuiTextReader.ReadControlTextJoined(item, visibleOnly: true) : null;
        if (!string.IsNullOrEmpty(text)) return Clean(text);

        if (index >= 0)
        {
            var children = FlowHelper.GetObjectField(parts, "_Children");
            var child = FlowHelper.GetListItem(children, index);
            var control = FlowHelper.GetObjectField(child, "Control")
                          ?? FlowHelper.Call(child, "get_Control") as ManagedObject;
            text = GuiTextReader.ReadControlTextJoined(control, visibleOnly: true);
            if (!string.IsNullOrEmpty(text)) return Clean(text);
        }

        return null;
    }

    // ------------------------------------------------------------------ say

    private void Announce(string spoken, bool interrupt)
    {
        if (string.IsNullOrEmpty(spoken)) return;
        Speak(spoken, interrupt);
    }

    /// <summary>Screen text arrives with layout tags and soft line breaks.</summary>
    private static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        string flat = System.Text.RegularExpressions.Regex.Replace(
            FlowHelper.CleanTags(text), @"\s+", " ").Trim();
        return flat.Length == 0 ? null : flat;
    }

    // ------------------------------------------------------------------ dump

    /// <summary>One-shot structural capture per param type: the param, every
    /// sub-object the screen is built from, and the data records behind the
    /// rows, so a later pass can extend this reader without guessing.</summary>
    private void Dump(string typeName, ManagedObject param, TrackedParam entry)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== WORLD MAP DUMP {typeName} - {DateTime.Now:HH:mm:ss} ===");
            sb.AppendLine($"address = 0x{entry.Address:x}");

            sb.AppendLine("--- param fields ---");
            try { ObjectDumper.DumpFieldsWithValues(sb, param.GetTypeDefinition(), param, "  "); }
            catch (Exception ex) { sb.AppendLine($"  [fields error: {ex.Message}]"); }

            sb.AppendLine("--- param methods ---");
            try { ObjectDumper.DumpMethodSignatures(sb, param.GetTypeDefinition(), "  "); }
            catch (Exception ex) { sb.AppendLine($"  [methods error: {ex.Message}]"); }

            foreach (string name in DumpSubObjects)
            {
                var sub = FlowHelper.GetObjectField(param, name);
                if (sub == null) continue;

                var td = sub.GetTypeDefinition();
                sb.AppendLine($"--- sub-object {name} ({td?.FullName ?? "?"}) ---");
                try { ObjectDumper.DumpFieldsWithValues(sb, td, sub, "  "); }
                catch (Exception ex) { sb.AppendLine($"  [fields error: {ex.Message}]"); }
                try { ObjectDumper.DumpMethodSignatures(sb, td, "  "); }
                catch (Exception ex) { sb.AppendLine($"  [methods error: {ex.Message}]"); }
            }

            // The full list of places lives in the shared input param, not in
            // the nine rows on screen — captured so a later pass can name a
            // locked row instead of falling back to its position.
            DumpDataRecord(sb, "Share.In", Nested(param, "Share", "In"));

            // The whole list of places as data — including the locked ones the
            // rows leave unnamed. Captured so a later pass can name a locked
            // city instead of falling back to its position.
            DumpDataRecord(sb, "Share.In.ItemParams[0]",
                FirstArrayItem(Nested(param, "Share", "In", "ItemParams")));
            DumpDataRecord(sb, "Share.Item", ShareItem(param));
            DumpDataRecord(sb, "SpotList._VisibleItems[0]",
                FirstArrayItem(FlowHelper.GetObjectField(
                    FlowHelper.GetObjectField(param, FIELD_SPOTS), "_VisibleItems")));
            DumpDataRecord(sb, "Missions[0]",
                FirstArrayItem(FlowHelper.GetObjectField(param, "Missions")));

            sb.AppendLine("--- visible rows (the virtual list) ---");
            try
            {
                var spots = FlowHelper.GetObjectField(param, FIELD_SPOTS);
                var items = FlowHelper.GetObjectField(spots, "_VisibleItems");
                int rows = FlowHelper.GetListCount(items);
                for (int i = 0; i < rows; i++)
                {
                    var item = FlowHelper.GetListItem(items, i);
                    if (item == null) continue;
                    int rowIndex = FlowHelper.CallInt(item, "get_Index", -1);
                    int state = FlowHelper.CallInt(item, "get_State", -1);
                    int cursor = FlowHelper.CallInt(item, "get_Cursor", -1);
                    string unlocked = FlowHelper.Call(item, "IsUnlocked") is bool b ? b.ToString() : "?";
                    sb.AppendLine($"  row {i}: index={rowIndex}, name={FlowHelper.Call(item, "get_Name") as string}, " +
                                  $"state={state}({FlowHelper.ResolveEnumName(WorldMapText.ItemStateEnum, state)}), " +
                                  $"cursor={cursor}({FlowHelper.ResolveEnumName(WorldMapText.CursorEnum, cursor)}), " +
                                  $"unlocked={unlocked}");
                }
            }
            catch (Exception ex) { sb.AppendLine($"  [rows error: {ex.Message}]"); }

            sb.AppendLine("--- missions (data) ---");
            try
            {
                var missions = FlowHelper.GetObjectField(param, "Missions");
                int n = FlowHelper.GetListCount(missions);
                for (int i = 0; i < n; i++)
                {
                    var m = FlowHelper.GetListItem(missions, i);
                    sb.AppendLine($"  {i}: {FlowHelper.ReadStringField(m, "Name")} " +
                                  $"(id={FlowHelper.ReadUIntField(m, "Id")}, order={FlowHelper.ReadIntField(m, "OrderNo")})");
                }
            }
            catch (Exception ex) { sb.AppendLine($"  [missions error: {ex.Message}]"); }

            sb.AppendLine("--- live readings ---");
            sb.AppendLine($"  city = {FlowHelper.Call(FlowHelper.GetObjectField(param, FIELD_DETAIL), "get_Name") as string}");
            sb.AppendLine($"  country = {FlowHelper.Call(FlowHelper.GetObjectField(param, FIELD_DETAIL), "get_Country") as string}");
            sb.AppendLine($"  focusIndex = {FlowHelper.CallInt(FlowHelper.GetObjectField(param, FIELD_SPOTS), "GetFocusIndex")}");
            sb.AppendLine($"  itemCount = {FlowHelper.ReadIntField(FlowHelper.GetObjectField(param, FIELD_SPOTS), "_ItemCount")}");
            sb.AppendLine($"  tickets = {Tickets(param)}");

            sb.AppendLine("--- on-screen texts (this screen only) ---");
            try
            {
                foreach (var t in GuiTextReader.ReadTextsByOwner(GUI_OWNER, visibleOnly: false))
                    sb.AppendLine($"  {t.Name}{(t.Visible ? "" : " [hidden]")} = {t.Text}");
            }
            catch (Exception ex) { sb.AppendLine($"  [texts error: {ex.Message}]"); }
            sb.AppendLine();

            _dumpPath ??= Path.Combine(ObjectDumper.DumpDir, $"sf6access_worldmap_{DateTime.Now:HHmmss}.txt");
            File.AppendAllText(_dumpPath, sb.ToString());
            API.LogInfo($"[SF6Access] World map dump appended: {typeName} -> {_dumpPath}");
        }
        catch (Exception ex)
        {
            API.LogError($"[SF6Access] World map dump failed: {ex.Message}");
        }
    }

    /// <summary>The record behind the selected row — whatever the screen has
    /// currently picked, which is exactly what a data read needs to name.</summary>
    private static ManagedObject ShareItem(ManagedObject param)
    {
        var share = FlowHelper.GetObjectField(param, "Share");
        return share == null ? null : FlowHelper.Call(share, "get_Item") as ManagedObject;
    }

    /// <summary>A field of a field, without letting a null link throw.</summary>
    private static ManagedObject Nested(ManagedObject obj, params string[] path)
    {
        try
        {
            foreach (string field in path)
            {
                obj = FlowHelper.GetObjectField(obj, field);
                if (obj == null) return null;
            }
            return obj;
        }
        catch { return null; }
    }

    private static ManagedObject FirstArrayItem(ManagedObject array)
    {
        if (array == null) return null;
        try
        {
            if (FlowHelper.GetListCount(array) <= 0) return null;
            return FlowHelper.GetListItem(array, 0);
        }
        catch { return null; }
    }

    private static void DumpDataRecord(StringBuilder sb, string label, ManagedObject obj)
    {
        if (obj == null) return;
        var td = obj.GetTypeDefinition();
        sb.AppendLine($"--- data record {label} ({td?.FullName ?? "?"}) ---");
        try { ObjectDumper.DumpFieldsWithValues(sb, td, obj, "  "); }
        catch (Exception ex) { sb.AppendLine($"  [fields error: {ex.Message}]"); }
        try { ObjectDumper.DumpMethodSignatures(sb, td, "  "); }
        catch (Exception ex) { sb.AppendLine($"  [methods error: {ex.Message}]"); }
    }
}
