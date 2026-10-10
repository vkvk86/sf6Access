using System;
using System.Runtime.InteropServices;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;
using SF6Access.Services;
using SF6Access.Services.WorldTour;

namespace SF6Access.Hooks.WorldTour;

/// <summary>
/// <b>Shift+F5 — 下行探测：“从这里到底怎么下去？”</b>
///
/// <para>按一次，<see cref="WayDownProbe"/> 把周围所有“下去的手段”全部测量一遍，念一句结论，
/// 并写一份 <c>sf6access_waydown.txt</c> 供慢慢核对。</para>
///
/// <para>为什么要单独一个键：当导航同时说出“六百多米”和“十八米”时，玩家没有手段判断哪个
/// 才是对的。这个键给出的不是又一个数字，而是一句能自己核实的判断——包括“什么都不用，
/// 直接走就能到”这种可能，那正是导航自己已经知道却没能说清楚的答案。</para>
///
/// <para>放在 F5 探针家族里（F5 是箭头探针，Shift+F5 是下行探测），因为它回答的是同一类
/// 问题——“这条路到底存不存在”——只是问的是脚下而不是游戏自己的箭头。</para>
/// </summary>
public class WayDownProbeHooks
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_SHIFT = 0x10;
    private const int VK_F5 = 0x74;
    private const int VK_F2 = 0x71;

    private static bool _lastF5;
    private static bool _lastF2;
    private static bool _busy;

    [PluginEntryPoint]
    public static void Initialize()
    {
        API.LogInfo("[SF6Access] WayDownProbeHooks initialized (Shift+F5 = probe the way down)");
    }

    [Callback(typeof(LateUpdateBehavior), CallbackType.Post)]
    public static void Tick()
    {
        // The measurement itself: one navmesh question a frame at most, so it never starves the
        // navigation the player is listening to. Runs whether or not a probe is in flight.
        WayDownProbe.Pump();

        bool f5 = (GetAsyncKeyState(VK_F5) & 0x8000) != 0;
        bool f2 = (GetAsyncKeyState(VK_F2) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

        bool edge5 = f5 && !_lastF5;
        bool edge2 = f2 && !_lastF2;
        _lastF5 = f5;
        _lastF2 = f2;

        // Every press is recorded, even the ones that do not fire the probe. A blind player cannot
        // see whether a keypress reached the mod, and a held SHIFT read one frame late is enough to
        // turn Shift+F5 into a plain F5 — which is exactly what happened on the first try (2026-10-07):
        // the player pressed it, the arrow probe answered, and nothing anywhere said the way-down
        // probe had never been asked. Silence is the one failure this mod is not allowed.
        if (edge5 || edge2)
            API.LogInfo($"[SF6Access] Way down: key seen (F2={edge2} F5={edge5} shift={shift})" +
                        (edge2 || (edge5 && shift) ? " - starting" : " - ignored, no modifier"));

        if (!(edge2 || (edge5 && shift)) || _busy) return;

        _busy = true;
        try
        {
            WayDownProbe.Start();
        }
        catch (Exception ex)
        {
            API.LogError($"[SF6Access] Way down probe failed: {ex.Message}");
            ScreenReaderService.Speak("Probe failed", interrupt: true);
        }
        finally { _busy = false; }
    }
}
