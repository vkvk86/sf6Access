using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using REFrameworkNET;
using SF6Access.Services;
using SF6Access.Services.Ui;

namespace SF6Access.Hooks.WorldTour;

/// <summary>
/// The phone's Messages app — contacts down one side, that contact's
/// conversations down the other
/// (<c>app.worldtour.UIFlowWTDeviceIM.DeviceIMParam</c>).
///
/// <para><b>Read the item arrays, not the scroll list.</b> <c>HolderListParts</c>
/// and <c>SubjectListParts</c> are <c>UIPartsScrollList</c>, which carries no
/// <c>_Children</c> to walk. The rows are reachable instead as
/// <c>HolderPartsArray</c> / <c>SubjectPartsArray</c>, whose <c>ItemText</c> is a
/// plain field-backed <c>via.gui.Text</c> — read with <c>get_Message</c>, not an
/// interface getter.</para>
///
/// <para>Which pane has focus is not announced separately: the selected contact
/// and the selected conversation each announce themselves when they change, and
/// moving between panes changes one of them.</para>
///
/// <para><b>The shoulder buttons change the category, and the category is a field
/// on the param.</b> L1/R1 switch between the message categories, which reloads
/// both lists without moving the selection — so nothing the old reader watched
/// changed and the switch was silent. The field is discovered by name rather
/// than hard-coded (it is dumped on first open), and a change re-announces the
/// contact and conversation that the new category brought up.</para>
///
/// <para><b>The row's own "is selected" getter is not <c>get_IsSelect</c>.</b>
/// The log recorded <c>via.gui.SelectItem.get_IsSelect not found</c>, which left
/// every row looking selected and the FIRST contact in the list being announced
/// whatever was highlighted — the contacts were effectively unreadable. Several
/// spellings are tried (quietly), and when none is declared the selected id is
/// used as the row position instead.</para>
/// </summary>
public sealed class DeviceIMHooks : SingleParamScreenAdapter
{
    protected override string ParamType => "app.worldtour.UIFlowWTDeviceIM.DeviceIMParam";

    // "No contact selected". The game's own not-set value for these id fields is
    // uint.MaxValue, not 0 — the same sentinel that caught us out in the sound
    // system's language-id fields.
    private const uint NO_HOLDER = uint.MaxValue;

    /// <summary>Polls to wait before dumping the screen, so it has finished
    /// building (~1 s at ReadInterval 5).</summary>
    private const int DUMP_DELAY_POLLS = 12;

    /// <summary>Spellings of the row's selection getter. The real one is named in
    /// the dump; tried quietly so a miss costs no log line.</summary>
    private static readonly string[] SelectMethods =
        { "get_IsSelect", "get_IsSelected", "get_Selected", "get_IsSelecting" };

    /// <summary>The GUI that renders this screen — the source of the contact
    /// name, which the row array cannot be trusted for (see
    /// <see cref="ContactName"/>).</summary>
    private const string GUI_OWNER = "DeviceIM";

    /// <summary>How many polls a reading must repeat before it is spoken
    /// (~0.17 s at ReadInterval 5).</summary>
    private const int STABLE_POLLS = 2;

    private static DeviceIMHooks _instance;

    /// <summary>Whether the Messages app is the screen on top — lets the generic
    /// focus reader stand down instead of announcing raw control names
    /// ("p_FaceIconItem_0") over it.</summary>
    public static bool IsActive => _instance != null && _instance.Active;

    private uint _lastHolder = NO_HOLDER;
    private int _lastSubject = int.MinValue;
    private string _lastSpoken;

    /// <summary>Reading waiting to be confirmed, and how many polls it has
    /// repeated for — the guard against a virtual list mid-rebind.</summary>
    private string _pending;
    private int _stable;

    private int _polls;
    private bool _dumped;
    private string _dumpPath;
    private uint _lastTab = uint.MaxValue;

    public DeviceIMHooks()
    {
        _instance = this;
        SearchInterval = 15;
        ReadInterval = 5;
    }

    protected override void OnBind()
    {
        _lastHolder = NO_HOLDER;
        _lastSubject = int.MinValue;
        _lastSpoken = null;
        _pending = null;
        _stable = 0;
        _polls = 0;
        _dumped = false;
        _lastTab = uint.MaxValue;
        API.LogInfo("[SF6Access] Messages app active");
    }

    protected override void OnExit()
    {
        _lastSpoken = null;
        API.LogInfo("[SF6Access] Messages app ended");
    }

    protected override void Poll()
    {
        if (!_dumped && ++_polls >= DUMP_DELAY_POLLS)
        {
            _dumped = true;
            Dump();
        }

        ReadCategory();

        // Contact first: changing contact re-lays the conversation list, so
        // announcing the new contact before its threads keeps the order sane.
        // The contact id is a uint and stays one: narrowing it to int is how the
        // "not set" sentinel (uint.MaxValue) turns into a wrong number.
        uint holder = FlowHelper.ReadUIntField(Param, "SelectedHolderID", NO_HOLDER);
        int subject = FlowHelper.ReadIntField(Param, "SelectedSubjectIndex", int.MinValue);

        // Both lists are virtual: while the player scrolls, the row controls are
        // rebound and a reading taken mid-move still carries the previous row's
        // text. Left unguarded that flips the announcement between two names
        // several times a second (captured: "换一个新地方" / "辛仁，第一个粉丝"
        // alternating every poll), so a reading has to repeat before it counts.
        string reading = Reading(subject);
        if (reading == null) { _pending = null; _stable = 0; return; }
        if (reading == _pending) _stable++;
        else { _pending = reading; _stable = 1; }
        if (_stable < STABLE_POLLS) return;

        // What the player actually moved is the contact or the conversation. A
        // row whose text flickers underneath an unmoved selection must not
        // re-speak — keying on the reading alone was the source of the chatter.
        if (holder == _lastHolder && subject == _lastSubject) return;
        _lastHolder = holder;
        _lastSubject = subject;

        Say(reading);
    }

    /// <summary>What the app is showing: the contact and, when one is selected,
    /// the conversation under it.</summary>
    private string Reading(int subject)
    {
        string contact = ContactName();
        string thread = TextAt("SubjectPartsArray", subject);
        if (contact == null) return thread;
        if (thread == null || contact == thread) return contact;
        return $"{contact}，{thread}";
    }

    private void Say(string text)
    {
        if (text == _lastSpoken) return;
        _lastSpoken = text;
        API.LogInfo($"[SF6Access] Messages: {text}");
        Speak(text);
    }

    // -------------------------------------------------------------- category

    /// <summary>The category switched by the shoulder buttons. Its field is found
    /// by name on first open rather than hard-coded, because the field list is
    /// only known from the dump. A change re-announces what the new category
    /// shows; without this the switch is completely silent.</summary>
    private void ReadCategory()
    {
        // The category the shoulder buttons switch —
        // <SelectedHolderTab>k__BackingField / get_SelectedHolderTab(), confirmed
        // by the capture. The first pass looked for a field named like a tab and
        // found HolderTabParts, which is the tab WIDGET, not the index: reading
        // it as a number gave nothing, and the switch stayed silent.
        uint tab = FlowHelper.ReadUIntField(Param, "SelectedHolderTab", uint.MaxValue);
        if (tab == _lastTab) return;
        bool first = _lastTab == uint.MaxValue;
        _lastTab = tab;
        if (first) return;

        // Both lists now belong to the new category: forget the old selection so
        // what it shows gets spoken again.
        API.LogInfo($"[SF6Access] Messages category -> {tab}");
        _lastHolder = NO_HOLDER;
        _lastSubject = int.MinValue;
        _pending = null;
        _stable = 0;

        string name = TabName();
        if (!string.IsNullOrEmpty(name)) Say(name);
    }

    /// <summary>Name of the category now selected, read from the tab widget
    /// (app.UIPartsSimpleList) rather than from an index — otherwise the
    /// announcement would be a number.</summary>
    private string TabName()
    {
        try
        {
            var tabs = FlowHelper.GetObjectField(Param, "HolderTabParts");
            return FlowHelper.CleanTags(FlowHelper.ReadSelectedItemText(tabs))?.Trim();
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ rows

    /// <summary>The contact the app has selected: the row that reports itself
    /// selected, or — when the row declares no such getter — the row at the
    /// selected id, which is the usual layout.</summary>
    private string ContactName()
    {
        // What the screen is actually showing. HolderPartsArray is a VIRTUAL
        // list: the capture showed only two row controls for it, rebound as the
        // list scrolls, so the control carrying a name is not necessarily the one
        // carrying the selection — reading the array is what made the
        // announcement flicker between two contacts. The GUI's own contact label
        // is redrawn as a whole and does not.
        string shown = GuiContactName();
        if (shown != null) return shown;

        var array = FlowHelper.GetObjectField(Param, "HolderPartsArray");
        int n = FlowHelper.GetListCount(array);

        for (int i = 0; i < n; i++)
        {
            var item = FlowHelper.GetListItem(array, i);
            if (item == null || !IsRowSelected(item)) continue;
            string text = ItemText(item);
            if (!string.IsNullOrEmpty(text)) return text;
        }

        if (_lastHolder != NO_HOLDER && _lastHolder < (uint)n)
        {
            string text = ItemText(FlowHelper.GetListItem(array, (int)_lastHolder));
            if (!string.IsNullOrEmpty(text)) return text;
        }
        return null;
    }

    /// <summary>The contact label on screen — <c>e_text_name</c> under the
    /// <c>DeviceIM</c> GUI. Case matters: <c>e_text_Name</c> on the same screen
    /// is the player's own name.</summary>
    private static string GuiContactName()
    {
        try
        {
            foreach (var t in GuiTextReader.ReadTextsByOwner(GUI_OWNER, visibleOnly: true))
            {
                if (!string.Equals(t.Name, "e_text_name", StringComparison.Ordinal)) continue;
                string v = FlowHelper.CleanTags(t.Text)?.Trim();
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        catch { }
        return null;
    }

    private static bool IsRowSelected(ManagedObject item)
    {
        var ctrl = FlowHelper.GetObjectField(item, "ItemCtrl");
        if (ctrl == null) return false;
        var td = ctrl.GetTypeDefinition();
        if (td == null) return false;

        foreach (string method in SelectMethods)
        {
            // logMiss:false — these are guesses, and a wrong one is not news.
            if (MemberAccess.FindMethod(td, method, logMiss: false) == null) continue;
            if (FlowHelper.Call(ctrl, method) is bool selected && selected) return true;
        }
        return false;
    }

    private string TextAt(string arrayField, int index)
    {
        if (index < 0) return null;
        var array = FlowHelper.GetObjectField(Param, arrayField);
        if (index >= FlowHelper.GetListCount(array)) return null;
        return ItemText(FlowHelper.GetListItem(array, index));
    }

    private static string ItemText(ManagedObject item)
    {
        var text = FlowHelper.GetObjectField(item, "ItemText");
        return FlowHelper.CleanTags(FlowHelper.Call(text, "get_Message") as string)?.Trim();
    }

    // ------------------------------------------------------------------ dump

    /// <summary>One-shot capture of the screen: the param, one row of each list
    /// and every on-screen text, so the category, the row's selection getter and
    /// the real field names are all on record instead of guessed at again.</summary>
    private void Dump()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== DEVICE IM DUMP - {DateTime.Now:HH:mm:ss} ===");

            sb.AppendLine("--- param fields ---");
            try { ObjectDumper.DumpFieldsWithValues(sb, Param.GetTypeDefinition(), Param, "  "); }
            catch (Exception ex) { sb.AppendLine($"  [fields error: {ex.Message}]"); }

            sb.AppendLine("--- param methods ---");
            try { ObjectDumper.DumpMethodSignatures(sb, Param.GetTypeDefinition(), "  "); }
            catch (Exception ex) { sb.AppendLine($"  [methods error: {ex.Message}]"); }

            DumpRow(sb, "HolderPartsArray");
            DumpRow(sb, "SubjectPartsArray");

            // The data behind the lists — the row controls are virtual and cannot
            // be trusted for the selection, so the next pass reads these instead.
            DumpSubObject(sb, "HolderTabParts");
            DumpSubObject(sb, "SubjectListParts");
            DumpSubObject(sb, "HolderListParts");
            DumpArrayItem(sb, "SubjectArray");
            DumpArrayItem(sb, "SelectedHolderArray");

            sb.AppendLine("--- on-screen texts (whole scene) ---");
            try
            {
                foreach (var t in GuiTextReader.ReadSceneTexts(visibleOnly: false))
                {
                    string shown = string.IsNullOrEmpty(t.Text) && !string.IsNullOrEmpty(t.Raw)
                        ? $"[raw] {t.Raw}" : t.Text;
                    sb.AppendLine($"  [{t.Owner}] {t.Name}{(t.Visible ? "" : " [hidden]")} = {shown}");
                }
            }
            catch (Exception ex) { sb.AppendLine($"  [texts error: {ex.Message}]"); }
            sb.AppendLine();

            _dumpPath ??= Path.Combine(ObjectDumper.DumpDir, $"sf6access_deviceim_{DateTime.Now:HHmmss}.txt");
            File.AppendAllText(_dumpPath, sb.ToString());
            API.LogInfo($"[SF6Access] Messages dump written: {_dumpPath}");
        }
        catch (Exception ex)
        {
            API.LogError($"[SF6Access] Messages dump failed: {ex.Message}");
        }
    }

    private void DumpSubObject(StringBuilder sb, string field)
    {
        try
        {
            var obj = FlowHelper.GetObjectField(Param, field);
            if (obj == null) return;
            var td = obj.GetTypeDefinition();
            sb.AppendLine($"--- {field} ({td?.FullName ?? "?"}) ---");
            ObjectDumper.DumpFieldsWithValues(sb, td, obj, "  ");
            ObjectDumper.DumpMethodSignatures(sb, td, "  ");
        }
        catch (Exception ex) { sb.AppendLine($"  [{field} error: {ex.Message}]"); }
    }

    /// <summary>First records of a data array — the conversations and contacts
    /// the screen is built from, which is what a data read needs.</summary>
    private void DumpArrayItem(StringBuilder sb, string field)
    {
        try
        {
            var array = FlowHelper.GetObjectField(Param, field);
            int n = FlowHelper.GetListCount(array);
            sb.AppendLine($"--- {field} ({n} items) ---");
            for (int i = 0; i < n && i < 3; i++)
            {
                var item = FlowHelper.GetListItem(array, i);
                if (item == null) continue;
                var td = item.GetTypeDefinition();
                sb.AppendLine($"  {i} ({td?.FullName ?? "?"})");
                ObjectDumper.DumpFieldsWithValues(sb, td, item, "    ");
                ObjectDumper.DumpMethodSignatures(sb, td, "    ");
            }
        }
        catch (Exception ex) { sb.AppendLine($"  [{field} error: {ex.Message}]"); }
    }

    /// <summary>One row of a list: its type, fields and methods — that is where
    /// the selection getter and the row's text live.</summary>
    private void DumpRow(StringBuilder sb, string arrayField)
    {
        try
        {
            var array = FlowHelper.GetObjectField(Param, arrayField);
            int n = FlowHelper.GetListCount(array);
            sb.AppendLine($"--- {arrayField} ({n} rows) ---");
            for (int i = 0; i < n && i < 3; i++)
            {
                var item = FlowHelper.GetListItem(array, i);
                if (item == null) continue;
                var td = item.GetTypeDefinition();
                sb.AppendLine($"  row {i} ({td?.FullName ?? "?"}) text={ItemText(item)}");
                ObjectDumper.DumpFieldsWithValues(sb, td, item, "    ");
                ObjectDumper.DumpMethodSignatures(sb, td, "    ");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  [{arrayField} error: {ex.Message}]");
        }
    }
}
