using System;
using System.Runtime.InteropServices;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;
using SF6Access.Services;
using SF6Access.Services.WorldTour;

namespace SF6Access.Hooks.WorldTour;

/// <summary>
/// <b>F11 — fire the navmesh ignition once. The route is RETIRED: this key now only says so.</b>
///
/// <para>The question was <i>which engine call kills the game</i>, and this key asked it, once,
/// on purpose, with the answer written to disk before the call was made — see
/// <see cref="NavMeshIgnition"/> for why a file and not a log line. It was asked twice on
/// 2026-10-03 and answered twice: generation 1 died on the node's fields, generation 2 on the
/// node's type metadata. Two different operations, so the object itself is unusable, and the
/// route is closed rather than tried a third time. The city map will come from the pathfinder
/// instead.</para>
///
/// <para><b>Nothing else hangs off this key.</b> It does not steer, it does not speak a route,
/// and it does not run by itself: a probe that crashed the game once must never be able to
/// crash it while the player is simply walking. It answers with one sentence and stops.</para>
///
/// <para>F11 is already the avatar creator's research dump; that hook only lives inside the
/// creator, and this one only answers while a World Tour avatar exists, so the two never
/// meet.</para>
/// </summary>
public class NavMeshIgnitionHooks
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_F11 = 0x7A;
    private static bool _last;

    [PluginEntryPoint]
    public static void Initialize()
    {
        NavMeshIgnition.Load();
        API.LogInfo("[SF6Access] NavMeshIgnitionHooks initialized (F11 = fire the navmesh probe once)");
    }

    [Callback(typeof(LateUpdateBehavior), CallbackType.Post)]
    public static void Tick()
    {
        bool down = (GetAsyncKeyState(VK_F11) & 0x8000) != 0;
        bool edge = down && !_last;
        _last = down;
        if (!edge) return;

        // Only in the World Tour field: F11 is the avatar creator's dump key everywhere else.
        if (WorldTourStateService.GetAvatarManager() == null) return;
        if (PadGuideHooks.Active || SpTalkNovelHooks.DialogueActive) return;

        var (verdict, line) = NavMeshIgnition.Run();
        string spoken = verdict switch
        {
            NavMeshIgnition.Verdict.Ok => string.Format(Text("ok",
                "Navmesh: it answered. The spot you are standing on has {0} ways out of it"),
                NavMeshIgnition.LastLinks),
            NavMeshIgnition.Verdict.Tripped => string.Format(Text("tripped",
                "Navmesh switched off: the game crashed at step {0} last time"), NavMeshIgnition.DiedAtStep),
            NavMeshIgnition.Verdict.NoPlayer => Text("noplayer",
                "Navmesh: no avatar to stand on right now"),
            NavMeshIgnition.Verdict.Retired => Text("retired",
                "Navmesh node reading is switched off for good"),
            _ => string.Format(Text("fail", "Navmesh: nothing to read, it stopped at step {0}"), line),
        };
        ScreenReaderService.Speak(spoken, interrupt: true);
        API.LogInfo("[SF6Access] Navmesh ignition said: " + spoken);
    }

    private static string Text(string key, string fallback) => LangFile.Get("wt.mesh." + key, fallback);
}
