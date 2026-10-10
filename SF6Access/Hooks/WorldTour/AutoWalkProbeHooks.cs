using System;
using System.Runtime.InteropServices;
using System.Text;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;
using SF6Access.Services;
using SF6Access.Services.WorldTour;

namespace SF6Access.Hooks.WorldTour;

/// <summary>
/// <b>F9 — auto-walk probe (RESEARCH TOOL, not a feature). Read-only.</b>
///
/// <para>Writes <c>reframework/data/sf6access_autowalk_*.txt</c> describing what it
/// would take to move the avatar: the input-controller factory, the priority enum,
/// the controller's method surface, and — the measurement the feature turns on —
/// the avatar's move vector next to the camera bearing, so the vector's coordinate
/// space (world vs camera-relative) can be settled by pressing F9 twice while
/// turning the camera.</para>
///
/// <para><b>Nothing here moves the avatar.</b> Every call is a by-value read or a
/// by-name member lookup; no input controller is created and no writer is invoked.
/// This exists because the space of <c>SetMove(vec3)</c> is unknown, and a wrong
/// guess would walk a blind player somewhere arbitrary.</para>
///
/// <para>Separate from <see cref="FieldProbeHooks"/> (F10), which dumps the
/// sensing/collision side: this one is about MOVEMENT only, and is meant to be
/// pressed repeatedly in the same session, so it appends rather than replacing.
/// Design notes: <c>docs/wt-autowalk.md</c>.</para>
/// </summary>
public class AutoWalkProbeHooks
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_F9 = 0x78;
    private static bool _lastKeyState;
    private static bool _running;

    [PluginEntryPoint]
    public static void Initialize() =>
        API.LogInfo("[SF6Access] AutoWalkProbeHooks initialized (F9 = what can move the avatar; read-only)");

    [Callback(typeof(LateUpdateBehavior), CallbackType.Post)]
    public static void Tick()
    {
        bool down = (GetAsyncKeyState(VK_F9) & 0x8000) != 0;
        bool edge = down && !_lastKeyState;
        _lastKeyState = down;
        if (!edge || _running) return;

        // The probe reads World Tour field data exclusively.
        if (WorldTourStateService.GetAvatarManager() == null)
        {
            API.LogInfo("[SF6Access] Auto-walk probe: not in the World Tour field (AvatarManager null)");
            ScreenReaderService.Speak("Auto-walk probe needs World Tour");
            return;
        }

        _running = true;
        try
        {
            var sb = new StringBuilder();
            AutoWalkProbe.Dump(sb);
            string path = System.IO.Path.Combine(ObjectDumper.DumpDir,
                $"sf6access_autowalk_{DateTime.Now:HHmmss}.txt");
            System.IO.File.WriteAllText(path, sb.ToString());
            API.LogInfo($"[SF6Access] Auto-walk probe written to {path}");
            ScreenReaderService.Speak("Auto-walk probe written");
        }
        catch (Exception ex)
        {
            API.LogError($"[SF6Access] Auto-walk probe failed: {ex.Message}");
            ScreenReaderService.Speak("Auto-walk probe failed");
        }
        finally { _running = false; }
    }
}
