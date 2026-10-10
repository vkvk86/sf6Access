using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using REFrameworkNET;
using SF6Access.Services;
using SF6Access.Services.Ui;

namespace SF6Access.Hooks.WorldTour;

/// <summary>
/// The passcode screen the Messages app opens when you press Y to add a contact
/// (<c>app.worldtour.UIFlowIMPasscodeScreen.IMPasscodeFlowParam</c>).
///
/// <para><b>Why it was silent.</b> Nothing in the mod owned this flow: no
/// dedicated adapter, and <c>app.worldtour.UIFlowIMPasscodeScreen</c> was not one
/// of the prefixes <see cref="Hooks.GroupFocusHooks"/> watches. The only thing
/// ever spoken on it was the generic guide line ("输入密码即可添加联系人。") — the
/// digits on screen were never read, so entering a code was impossible without
/// sight.</para>
///
/// <para><b>Read the rendered digits, not the param.</b> The param is dumped on
/// the first appearance of the screen (see <see cref="Dump"/>), and until that
/// capture says otherwise there is no known field holding the entered code — so
/// this reader takes the code from the GUI: the on-screen text elements whose
/// names mark them as the code's cells (num / code / pass / digit / input). The
/// keypad's own ten buttons are excluded by recognising them as a full 0-9 set
/// rather than a row of cells.</para>
///
/// <para><b>Speak the change, not the whole code.</b> The player enters one
/// digit at a time; reading all four again at every step would be unusable, so
/// only the digit that changed is spoken (and "删除" when one is removed).</para>
/// </summary>
public sealed class IMPasscodeHooks : SingleParamScreenAdapter
{
    protected override string ParamType =>
        "app.worldtour.UIFlowIMPasscodeScreen.IMPasscodeFlowParam";

    /// <summary>Polls to wait before dumping the screen, so it has finished
    /// building (~1 s at ReadInterval 5).</summary>
    private const int DUMP_DELAY_POLLS = 12;

    /// <summary>The GUI that renders this screen: the keypad (e_text = 0-9,
    /// 确定, 删除) and the code display (e_txt_passCode, hidden).</summary>
    private const string GUI_OWNER = "IMPasscodeScreen";

    private static IMPasscodeHooks _instance;

    /// <summary>Whether this screen is the one on top — lets the generic focus
    /// reader stand down instead of announcing raw control names over it.</summary>
    public static bool IsActive => _instance != null && _instance.Active;

    private int _polls;
    private bool _dumped;
    private bool _ownersLogged;
    private string _lastCode;
    private string _dumpPath;

    public IMPasscodeHooks()
    {
        _instance = this;
        SearchInterval = 15;
        ReadInterval = 5;
    }

    protected override void OnBind()
    {
        _polls = 0;
        _dumped = false;
        _lastCode = null;
        API.LogInfo("[SF6Access] Passcode screen active");
    }

    protected override void OnExit()
    {
        _lastCode = null;
        API.LogInfo("[SF6Access] Passcode screen ended");
    }

    protected override void Poll()
    {
        if (!_dumped && ++_polls >= DUMP_DELAY_POLLS)
        {
            _dumped = true;
            Dump();
        }
        if (!_ownersLogged)
        {
            _ownersLogged = true;
            LogScreenTexts();
        }
        ReadCode();
    }

    // ------------------------------------------------------------------- code

    private void ReadCode()
    {
        // The code display is e_txt_passCode under IMPasscodeScreen, and the
        // capture shows it HIDDEN — reading only visible texts (as the first
        // pass did) never sees it at all, which is why the screen announced
        // digits that were really the keypad's own labels.
        string code = PasscodeText();
        if (string.IsNullOrEmpty(code)) { _lastCode = null; return; }

        string previous = _lastCode;
        _lastCode = code;
        // First sight is the baseline ("000000" — one placeholder per digit), not
        // something the player typed.
        if (previous == null || previous.Length != code.Length) return;

        int at = -1;
        for (int i = 0; i < code.Length; i++)
            if (code[i] != previous[i]) { at = i; break; }
        if (at < 0) return;

        char typed = code[at];
        // An empty cell reads as 0, so a cell going back to 0 is a deletion.
        // (A real 0 in the code cannot be told apart from that yet — the next
        // capture will say whether the placeholder is really '0'.)
        string spoken;
        if (typed == '0')
            spoken = previous[at] == '0'
                ? null
                : LangFile.Get("wt.im.passcode.deleted", "删除");
        else
            spoken = typed.ToString();

        if (string.IsNullOrEmpty(spoken)) return;
        API.LogInfo($"[SF6Access] Passcode '{code}' (cell {at}) -> {spoken}");
        Speak(spoken);
    }

    /// <summary>The code as the screen is holding it: the text element named for
    /// the passcode, hidden or not.</summary>
    private static string PasscodeText()
    {
        try
        {
            foreach (var t in GuiTextReader.ReadTextsByOwner(GUI_OWNER, visibleOnly: false))
            {
                if (t.Name == null) continue;
                if (t.Name.IndexOf("passcode", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string v = (t.Text ?? string.Empty).Trim();
                if (v.Length > 0) return v;
            }
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------------- dump

    /// <summary>One-shot capture of the screen: the param's fields and methods
    /// plus every on-screen text with its owner, so a later pass can read the
    /// code from the param — or at least name the cell elements exactly.</summary>
    private void Dump()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== IM PASSCODE DUMP - {DateTime.Now:HH:mm:ss} ===");

            sb.AppendLine("--- param fields ---");
            try { ObjectDumper.DumpFieldsWithValues(sb, Param.GetTypeDefinition(), Param, "  "); }
            catch (Exception ex) { sb.AppendLine($"  [fields error: {ex.Message}]"); }

            sb.AppendLine("--- param methods ---");
            try { ObjectDumper.DumpMethodSignatures(sb, Param.GetTypeDefinition(), "  "); }
            catch (Exception ex) { sb.AppendLine($"  [methods error: {ex.Message}]"); }

            // The keypad and the code display are this widget's children — it is
            // where a later pass finds which key the cursor is on.
            var parts = FlowHelper.GetObjectField(Param, "PasscodeScreenParts");
            if (parts != null)
            {
                var ptd = parts.GetTypeDefinition();
                sb.AppendLine($"--- PasscodeScreenParts ({ptd?.FullName ?? "?"}) ---");
                try { ObjectDumper.DumpFieldsWithValues(sb, ptd, parts, "  "); }
                catch (Exception ex) { sb.AppendLine($"  [fields error: {ex.Message}]"); }
                try { ObjectDumper.DumpMethodSignatures(sb, ptd, "  "); }
                catch (Exception ex) { sb.AppendLine($"  [methods error: {ex.Message}]"); }
            }

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

            _dumpPath ??= Path.Combine(ObjectDumper.DumpDir, $"sf6access_impasscode_{DateTime.Now:HHmmss}.txt");
            File.AppendAllText(_dumpPath, sb.ToString());
            API.LogInfo($"[SF6Access] Passcode dump written: {_dumpPath}");
        }
        catch (Exception ex)
        {
            API.LogError($"[SF6Access] Passcode dump failed: {ex.Message}");
        }
    }

    /// <summary>The same texts in the log, so the cell element names are visible
    /// without opening the dump file.</summary>
    private void LogScreenTexts()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var t in GuiTextReader.ReadSceneTexts(visibleOnly: false))
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append($"[{t.Owner}]{t.Name}={t.Text}");
                if (sb.Length > 1500) break;
            }
            API.LogInfo($"[SF6Access] Passcode screen texts: {sb}");
        }
        catch (Exception ex)
        {
            API.LogError($"[SF6Access] Passcode screen texts failed: {ex.Message}");
        }
    }
}
