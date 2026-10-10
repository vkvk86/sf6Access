using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;
using SF6Access.Services;
using SF6Access.Services.WorldTour;

namespace SF6Access.Hooks.WorldTour;

/// <summary>
/// <b>F4 — wall following: put your right hand on the wall, or on the edge of the floor when there
/// is no wall, and be walked round it one step at a time.</b>
///
/// <para>The owner's own description (2026-10-04): 按下 F4 打开，打开后他带着我贴着墙走，直到一整圈
/// 或再次按 F4 结束，每次按都从当前所在地开始贴墙走；没有墙就贴着悬崖边走，高度大于一点六米才算
/// 悬崖，否则当作平地. The work behind the key is <see cref="WallFollow"/>; this file only owns the
/// key and the frame, as <see cref="TourHooks"/> owns T.</para>
///
/// <para>Keyboard only on purpose — <see cref="ReadoutShortcut.PAD_NONE"/> — because every pad
/// button the testers tried in the World Tour field turned out to be a game action. F4 is free:
/// F5 is the arrow probe, F6 the stats diagnostic, F7 the screenshot, F8 and F9 the object dump,
/// F11 the create dump, F12 spoken way-finding.</para>
///
/// <para>Nothing here moves the avatar. The walk is a sentence per step and the player walks it,
/// which is the choice the owner made for every part of this mod.</para>
/// </summary>
public class WallFollowHooks
{
    private const int VK_F4 = 0x73;

    private static readonly ReadoutShortcut ToggleKey = new(VK_F4, ReadoutShortcut.PAD_NONE);

    [PluginEntryPoint]
    public static void Initialize() =>
        API.LogInfo("[SF6Access] WallFollowHooks initialized (F4 = follow the wall on your right, " +
                    "or the edge of the floor when there is no wall)");

    [Callback(typeof(LateUpdateBehavior), CallbackType.Post)]
    public static void Tick()
    {
        bool toggle = ToggleKey.Pressed();
        if (!toggle && !WallFollow.On) return;

        FieldPresenceService.Refresh();
        if (toggle)
        {
            if (!FieldPresenceService.CanSpeak)
            {
                ScreenReaderService.Speak(WallFollow.Text("notfield",
                    "Wall following works in the World Tour field only"), interrupt: true);
                return;
            }
            // In a dialogue or the pad tutorial the game owns the microphone; speaking here would
            // talk over its own words. The press is simply not acted on.
            if (!FieldOk()) return;
            WallFollow.Toggle();
            return;
        }
        if (FieldOk()) WallFollow.Pump();
    }

    /// <summary>The field state the walk may measure and speak in: the field itself, and neither of
    /// the two moments the game owns the microphone.</summary>
    private static bool FieldOk() =>
        FieldPresenceService.CanSpeak && !SpTalkNovelHooks.DialogueActive && !PadGuideHooks.Active;
}
