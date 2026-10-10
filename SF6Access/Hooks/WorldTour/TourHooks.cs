using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;
using SF6Access.Services;
using SF6Access.Services.WorldTour;

namespace SF6Access.Hooks.WorldTour;

/// <summary>
/// T = the grand tour on and off; Shift+T = the next stop now (the owner's choices,
/// 2026-10-04: every arrival waits for the key, and the tour does not yield to mission
/// targets). Keyboard-only on purpose — <see cref="ReadoutShortcut.PAD_NONE"/> — because
/// every pad button the testers tried in the World Tour field turned out to be a game
/// action. T completes the letter family the field features already read on: V
/// destinations, N people, B radar, Z where-am-I.
/// </summary>
public class TourHooks
{
    private const int VK_T = 0x54;

    private static readonly ReadoutShortcut NextKey = new(VK_T, ReadoutShortcut.PAD_NONE, shift: true);
    private static readonly ReadoutShortcut ToggleKey = new(VK_T, ReadoutShortcut.PAD_NONE);

    [PluginEntryPoint]
    public static void Initialize() =>
        API.LogInfo("[SF6Access] TourHooks initialized (T = grand tour, Shift+T = next stop)");

    [Callback(typeof(LateUpdateBehavior), CallbackType.Post)]
    public static void Tick()
    {
        // Shift+T is polled first: the two shortcuts can never fire on the same press
        // (ReadoutShortcut splits them on the Shift state), and this order is what makes
        // certain of it.
        bool next = NextKey.Pressed();
        bool toggle = ToggleKey.Pressed();
        if (!next && !toggle && !TourService.On) return;

        FieldPresenceService.Refresh();
        if (next)
        {
            if (FieldOk()) TourService.Skip();
            else API.LogInfo("[SF6Access] Tour: Shift+T outside the speakable field - ignored");
            return;
        }
        if (toggle)
        {
            if (!FieldPresenceService.CanSpeak)
            {
                ScreenReaderService.Speak(TourService.Text("notfield",
                    "The grand tour works in the World Tour field only"), interrupt: true);
                return;
            }
            if (!FieldOk())
            {
                // In a dialogue or the pad tutorial: speaking here would talk over the
                // game's own words. The press is not lost — it is simply not acted on.
                API.LogInfo("[SF6Access] Tour: T during dialogue - ignored");
                return;
            }
            TourService.Toggle();
            return;
        }
        if (FieldOk()) TourService.Pump();
    }

    /// <summary>The field state the tour may speak and pick in: the field itself, and
    /// neither of the two moments the game owns the microphone.</summary>
    private static bool FieldOk() =>
        FieldPresenceService.CanSpeak && !SpTalkNovelHooks.DialogueActive && !PadGuideHooks.Active;
}
