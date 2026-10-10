using System;
using System.IO;
using System.Text;
using REFrameworkNET;
using SF6Access.Services;
using SF6Access.Services.Ui;

namespace SF6Access.Hooks.WorldTour;

/// <summary>
/// Reading a message thread — the screen the Messages app opens into
/// (<c>app.worldtour.UIFlowIMContentScreen.IMContentFlowParam</c>).
///
/// <para><b>The body text is read from the GUI, not from the param.</b> The
/// param's <c>IMDataList</c> holds <c>WTIMData</c> records whose message content
/// is asset/script-backed — there is no plain string on them to read. The
/// rendered text, however, is right there in the <c>IMContentScreen</c> GUI as
/// <c>e_text_name</c> (sender) and <c>e_text_message</c> (body), which is the
/// same route the mod already uses for the in-world message window.</para>
///
/// <para>Announced on change rather than once on open: a thread advances message
/// by message as the player presses on, and each new one must be read.</para>
/// </summary>
public sealed class IMContentHooks : SingleParamScreenAdapter
{
    private const string GUI_OWNER = "IMContentScreen";

    protected override string ParamType => "app.worldtour.UIFlowIMContentScreen.IMContentFlowParam";

    /// <summary>Polls to wait before deciding the thread really has nothing to
    /// read (~2 s at ReadInterval 5), so a slow-loading thread is not written off
    /// while it is still drawing.</summary>
    private const int EMPTY_DUMP_POLLS = 24;

    private string _lastSpoken;
    private int _polls;
    private bool _dumped;

    protected override void OnBind()
    {
        _lastSpoken = null;
        _polls = 0;
        _dumped = false;
        API.LogInfo("[SF6Access] Message content active");
    }

    protected override void OnExit() => _lastSpoken = null;

    protected override void Poll()
    {
        _polls++;
        var texts = GuiTextReader.ReadTextsByOwner(GUI_OWNER);
        if (texts == null || texts.Count == 0)
        {
            // An opened thread that says nothing is the worst failure there is —
            // the player cannot tell an empty thread from a broken reader. Capture
            // the scene once so the next pass can see what the thread is made of
            // (a media attachment, a different GUI owner, nothing at all).
            if (!_dumped && _polls >= EMPTY_DUMP_POLLS)
            {
                _dumped = true;
                DumpSilentScreen();
            }
            return;
        }
        _dumped = true;   // it spoke — nothing to diagnose

        // A thread shows SEVERAL bubbles at once, not one. Walk the GUI in tree
        // order and pair each sender with the message that follows it; taking
        // only the first (or only the last) of each loses the rest of the
        // conversation, which is what the screen is actually showing.
        var lines = new System.Collections.Generic.List<string>();
        string pendingName = null;
        foreach (var t in texts)
        {
            string field = t.Name;
            string value = Clean(t.Text);
            if (string.IsNullOrEmpty(field) || string.IsNullOrEmpty(value)) continue;

            if (field.Contains("name")) { pendingName = value; continue; }
            if (!field.Contains("message")) continue;

            string line = pendingName == null ? value : $"{pendingName}: {value}";
            pendingName = null;
            // The same bubble can be rendered twice; a repeat of the line just
            // before it is the duplicate, not a real second message.
            if (lines.Count > 0 && lines[lines.Count - 1] == line) continue;
            lines.Add(line);
        }
        if (lines.Count == 0) return;

        // NEWEST FIRST in the GUI, oldest first when read aloud. The widget lists
        // the thread the way a chat app draws it — most recent at the top — which
        // read out loud reverses the conversation and makes replies precede what
        // they answer. Same newest-first convention as `_Handles` elsewhere in
        // this game. Reversing here also keeps the "speak only what is new" test
        // below honest: with oldest first, a newly arrived message extends the
        // string at the END, which is what the prefix comparison expects.
        lines.Reverse();

        string whole = string.Join(". ", lines);
        if (whole == _lastSpoken) return;

        // Speak only what is NEW. A thread grows a bubble at a time as the player
        // presses on, and re-reading the whole conversation at every step would
        // be unusable.
        string toSpeak = whole;
        if (_lastSpoken != null && whole.StartsWith(_lastSpoken, System.StringComparison.Ordinal))
            toSpeak = whole.Substring(_lastSpoken.Length).TrimStart(' ', '.');

        _lastSpoken = whole;
        if (string.IsNullOrEmpty(toSpeak)) return;
        API.LogInfo($"[SF6Access] Message ({lines.Count} shown): {toSpeak}");
        Speak(toSpeak);
    }

    private static string Clean(string raw) => FlowHelper.CleanTags(raw)?.Trim();

    /// <summary>What is on screen when the thread reads as empty: every text with
    /// its owner, so a differently-named GUI or a media-only message is visible
    /// instead of guessed at.</summary>
    private void DumpSilentScreen()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== IM CONTENT (silent thread) - {DateTime.Now:HH:mm:ss} ===");
            try
            {
                foreach (var t in GuiTextReader.ReadSceneTexts(visibleOnly: false))
                    sb.AppendLine($"  [{t.Owner}] {t.Name}{(t.Visible ? "" : " [hidden]")} = {t.Text}");
            }
            catch (Exception ex) { sb.AppendLine($"  [texts error: {ex.Message}]"); }
            sb.AppendLine();

            string path = Path.Combine(ObjectDumper.DumpDir, $"sf6access_imcontent_{DateTime.Now:HHmmss}.txt");
            File.AppendAllText(path, sb.ToString());
            API.LogInfo($"[SF6Access] Message content silent, dump written: {path}");
        }
        catch (Exception ex)
        {
            API.LogError($"[SF6Access] Message content dump failed: {ex.Message}");
        }
    }
}
