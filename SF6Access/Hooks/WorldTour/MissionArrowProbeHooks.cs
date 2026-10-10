using System;
using System.Runtime.InteropServices;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;
using SF6Access.Services;
using SF6Access.Services.WorldTour;

namespace SF6Access.Hooks.WorldTour;

/// <summary>
/// <b>F5 — one-shot mission arrow / guide probe (RESEARCH TOOL, not a feature).</b>
///
/// <para>Presses dump <see cref="MissionArrowProbe"/> to
/// <c>reframework/data/sf6access_arrowprobe_*.txt</c>: what the game's own on-screen
/// mission arrow is, what it knows, and whether anything in the game exposes a ROUTE to a
/// target rather than merely a direction to one.</para>
///
/// <para>The reason it is worth a keypress at all: when the engine's pathfinding calls a
/// mission target unreachable, the mod falls back to searching on its own. If the game's own
/// guide knows about a lift, a switch or a staircase that the navmesh does not model, reading
/// it beats every guess the mod can make on its own.</para>
///
/// <para>Unlike the F10 field probe this does NOT require the field: the guide type's declared
/// shape (blocks B, C, F) is read from the type database and answers "what can this object
/// tell us" even while no arrow is on screen — which is the state the arrow reader has been
/// reporting. It therefore runs anywhere, and the blocks that do need live objects simply
/// record that nothing was found.</para>
/// </summary>
public class MissionArrowProbeHooks
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_F5 = 0x74;
    private static bool _lastKeyState;
    private static bool _running;

    [PluginEntryPoint]
    public static void Initialize()
    {
        API.LogInfo("[SF6Access] MissionArrowProbeHooks initialized (F5 = mission arrow / guide probe)");
    }

    [Callback(typeof(LateUpdateBehavior), CallbackType.Post)]
    public static void Tick()
    {
        bool down = (GetAsyncKeyState(VK_F5) & 0x8000) != 0;
        bool edge = down && !_lastKeyState;
        _lastKeyState = down;
        if (!edge || _running) return;

        _running = true;
        try
        {
            string path = MissionArrowProbe.Run();
            API.LogInfo($"[SF6Access] Mission arrow probe written to {path}");
            ScreenReaderService.Speak("Arrow probe complete");
        }
        catch (Exception ex)
        {
            API.LogError($"[SF6Access] Mission arrow probe failed: {ex.Message}");
            ScreenReaderService.Speak("Arrow probe failed");
        }
        finally { _running = false; }
    }
}
