using System.Collections.Generic;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;
using SF6Access.Services;

namespace SF6Access.Hooks;

/// <summary>
/// <b>L3 on the pad (or H on the keyboard): "my status" — health, Zenny, level,
/// experience, skill points, the five combat stats, both gauges and the story
/// chapter, spoken in one sentence.</b>
///
/// <para>The World Tour field shows health as a bar with no number and the money
/// only inside a shop, so a blind player has no way to know either. This is the
/// key that says them out loud, on demand — not on a timer, because numbers
/// spoken unasked are noise.</para>
///
/// <para><b>Why the press does not read immediately, and why it reads so many
/// times.</b> L3 is a game action in its own right, and the wallet does not fail
/// by saying "unavailable" — it hands back an EMPTY WALLET, so the money, the
/// Miles and even the save-data version all read 0 together, at random, about
/// six frames out of ten, while the real amounts never move. Measured presses
/// on 2026-10-06: <c>125252, 0, 0, 125252, 0</c>, then <c>0, 0, 0, 0, 125252</c>,
/// then <c>0, 0, 0, 0, 0</c> — and that last one is what made the mod speak
/// "0 Zenny". So the press waits <see cref="SETTLE_FRAMES"/> frames for the
/// game to finish its own handling, then polls both wallet numbers once per
/// frame for <see cref="SAMPLE_FRAMES"/> frames and keeps the value the polls
/// agree on — with the last real amount remembered as a fallback, because a
/// purse does not empty between two presses.</para>
///
/// <para>Shift+H reads immediately and additionally probes every numeric getter
/// (keyboard only: it is a research tool, and a pad button has no shift chord
/// to make it deliberate). Every press writes
/// <c>sf6access_playerstatus.txt</c> under <c>reframework/data</c>.</para>
/// </summary>
public class PlayerStatusHooks
{
    private const int VK_H = 0x48;          // H
    private const uint PAD_L3 = 0x1000;     // via.hid.GamePadButton L3

    // Frames to let the game finish handling the pad action, then the frames
    // both wallet numbers are polled over. 12 polls because a single read is a
    // coin toss: at the observed ~40% hit rate, five polls missed entirely once
    // in four presses, twelve miss once in eighty.
    private const int SETTLE_FRAMES = 12;   // ~0.2 s
    private const int SAMPLE_FRAMES = 12;   // ~0.2 s

    private static readonly ReadoutShortcut Key = new(VK_H, PAD_L3);
    private static readonly ReadoutShortcut DeepKey = new(VK_H, ReadoutShortcut.PAD_NONE, shift: true);

    private static int _settle;
    private static int _samplesLeft;
    private static readonly List<int> _zenny = new();
    private static readonly List<int> _miles = new();

    // The last amounts that were really read. A purse does not empty between
    // two presses, so when every poll comes back empty the remembered amount is
    // a better answer than a 0 that is only a failed read.
    private static int _lastZenny = int.MinValue;
    private static int _lastMiles = int.MinValue;

    [PluginEntryPoint]
    public static void Initialize() =>
        API.LogInfo("[SF6Access] PlayerStatusHooks initialized (L3 / H = my status: health, Zenny, level and more)");

    [Callback(typeof(LateUpdateBehavior), CallbackType.Post)]
    public static void Tick()
    {
        // Polled every frame: ReadoutShortcut edge-detects, and a slower poll
        // misses a short press entirely.
        if (DeepKey.Pressed())
        {
            PlayerStatusReader.Announce(int.MinValue, int.MinValue, deep: true);
            return;
        }

        if (Key.Pressed())
        {
            _settle = SETTLE_FRAMES;
            _samplesLeft = 0;
            _zenny.Clear();
            _miles.Clear();
            return;
        }

        if (_settle > 0)
        {
            if (--_settle == 0) _samplesLeft = SAMPLE_FRAMES;
            return;
        }

        if (_samplesLeft > 0)
        {
            _zenny.Add(PlayerStatusReader.ReadZennyStable());
            _miles.Add(PlayerStatusReader.ReadMileFast());
            if (--_samplesLeft == 0) Speak();
        }
    }

    /// <summary>Speak the status with the numbers the polls agreed on. The raw
    /// polls are logged rather than silently collapsed: which values the wallet
    /// flickers between IS the diagnosis.</summary>
    private static void Speak()
    {
        int zenny = Agreed(_zenny, ref _lastZenny);
        int miles = Agreed(_miles, ref _lastMiles);
        API.LogInfo($"[SF6Access] Zenny samples: {string.Join(", ", _zenny)} -> {zenny}");
        API.LogInfo($"[SF6Access] Miles samples: {string.Join(", ", _miles)} -> {miles}");

        PlayerStatusReader.Announce(zenny, miles);
    }

    /// <summary>The value the polls agree on: the majority one when the reads
    /// flickered, and the last real amount when this press never got one.
    ///
    /// <para>A wallet read that fails does NOT come back as "unavailable" — it
    /// comes back as 0, so a 0 among real amounts is a read that failed, not an
    /// empty purse; only a press where every single poll says zero, and no real
    /// amount has ever been read, is believed to mean zero.</para></summary>
    private static int Agreed(List<int> samples, ref int last)
    {
        // A failed wallet read comes back as 0, so 0 is NOT a vote — counting
        // it is what made twelve polls with one real 125252 among them speak
        // "0 Zenny". Only real amounts vote, and they take the majority among
        // themselves; the remembered amount is used only when this press got no
        // real amount at all.
        int best = int.MinValue, bestCount = 0;
        foreach (int v in samples)
        {
            if (v <= 0) continue;                       // unreadable, or a failed read
            int count = 0;
            foreach (int w in samples) if (w == v) count++;
            if (count > bestCount) { bestCount = count; best = v; }
        }

        int result = best != int.MinValue ? best : (last != int.MinValue ? last : int.MinValue);
        if (result != int.MinValue) last = result;
        return result;
    }
}
