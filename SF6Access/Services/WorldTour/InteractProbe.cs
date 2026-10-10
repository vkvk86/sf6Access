using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>The listener that will answer "did they actually interact with it" — with evidence, not
/// with a guess.</b>
///
/// <para><b>Why it exists.</b> The owner asked for marks only on REAL interactions: 只认真正的
/// 按键交互. Nothing known so far can be trusted to say one happened, and an earlier
/// investigation in this very field proved the opposite is common — the tutorial panels keep no
/// record of being stepped on ("that state lives in the mission system",
/// <see cref="FieldPadService"/>) — so guessing at a "used" field would produce exactly the kind
/// of lie this mod must not speak. This therefore WATCHES and writes down what it sees, and
/// nothing else.</para>
///
/// <para><b>What it records.</b> Whenever something is within reach: what is offered, every key
/// and pad button that goes down, and — around each press — a full dump of that object's fields.
/// If interacting changes anything readable, a later analysis sees it; if nothing changes, that
/// is the answer too, and then the mark has to come from the press itself.</para>
///
/// <para><b>What it costs.</b> A poll ten times a second that does nothing at all unless
/// something is in reach, and at most one object dump every few seconds. Files, not chatter: the
/// dump goes to the mod's own data folder, the log gets one line per event.</para>
/// </summary>
internal static class InteractProbe
{
    private const string FILE = "sf6access_interactprobe.txt";
    private const long POLL_MS = 100;
    private const long SETTLE_MS = 500;      // how long after the press the "after" snapshot waits
    private const long DUMP_MS = 4000;       // at most one object dump this often
    private const float REACH_M = 4f;        // closer than this counts as standing at it
    private const int MAX_NOTES = 400;

    private readonly record struct Offered(ManagedObject Object, float Distance);

    private static readonly Dictionary<ulong, Offered> _offered = new();
    private static readonly HashSet<int> _heldKeys = new();
    private static long _nextPoll, _nextDump, _dumpAt;
    private static ManagedObject _dumpObject;
    private static uint _heldPad;
    private static int _notes;
    private static int[] _keys;

    /// <summary>Called from the beacon's own tick. Costs nothing outside the World Tour field,
    /// and nothing at all when the player is not standing next to anything.</summary>
    internal static void Pump()
    {
        long now = Environment.TickCount64;
        if (now < _nextPoll) return;
        _nextPoll = now + POLL_MS;
        try { Sample(now); }
        catch { }
    }

    private static void Sample(long now)
    {
        if (!ReadOffered()) { Clear(); return; }
        LogEdges();
        MaybeSnapshot(now);
    }

    /// <summary>The game's own list of what can be touched right now, keeping only what is
    /// within arm's reach — anything further away is not what a press acts on.</summary>
    private static bool ReadOffered()
    {
        _offered.Clear();
        var list = AvatarFieldReader.GetAccessInfoList(WorldTourStateService.GetAvatarManager());
        int count = FlowHelper.GetListCount(list);
        for (int i = 0; i < count; i++)
        {
            var info = MemberAccess.ReadFieldsFirst(FlowHelper.GetListItem(list, i), "TargetInfo");
            if (info == null) continue;
            float distance = FieldProbeService.ToFloat(MemberAccess.ReadFieldsFirst(info, "Distance", typeof(float)));
            if (distance > REACH_M) continue;
            var target = MemberAccess.ReadFieldsFirst(info, "Target") as ManagedObject;
            if (target == null) continue;
            _offered[target.GetAddress()] = new Offered(target, distance);
        }
        return _offered.Count > 0;
    }

    /// <summary>Every key or button that goes DOWN while something is in reach. This is the line
    /// the tester's log has to show: whatever here matches their own interact binding is the one
    /// a real detector would watch.</summary>
    private static void LogEdges()
    {
        foreach (int vk in Keys())
        {
            bool down = ReadoutShortcut.KeyDown(vk);
            if (down == _heldKeys.Contains(vk)) continue;
            if (!down) { _heldKeys.Remove(vk); continue; }
            _heldKeys.Add(vk);
            if (!ReadoutShortcut.IsGameForeground()) continue;
            Note("key down: " + NameKey(vk));
            Arm(Environment.TickCount64);
        }

        uint pad = ReadoutShortcut.ReadPadButtons();
        if (pad != _heldPad)
        {
            uint went = pad & ~_heldPad;
            _heldPad = pad;
            if (went != 0 && ReadoutShortcut.IsGameForeground())
            {
                Note("pad down: " + InputNameResolver.GamePadButtonName(went));
                Arm(Environment.TickCount64);
            }
        }
    }

    /// <summary>Remember what was under the hand at the moment of the press, and queue the later
    /// snapshot of it. Nothing is read yet: the object may be busy being used.</summary>
    private static void Arm(long now)
    {
        _dumpObject = Nearest();
        if (_dumpObject != null) _dumpAt = now + SETTLE_MS;
    }

    private static void MaybeSnapshot(long now)
    {
        if (_dumpObject != null && _dumpAt != 0 && now >= _dumpAt)
        {
            Snapshot("after");
            _dumpAt = 0;
            _dumpObject = null;
            return;
        }
        if (now < _nextDump) return;
        _nextDump = now + DUMP_MS;
        _dumpObject = Nearest();
        Snapshot("before");
    }

    private static ManagedObject Nearest()
    {
        ManagedObject best = null;
        float bestD = float.MaxValue;
        foreach (var pair in _offered)
            if (pair.Value.Distance < bestD) { bestD = pair.Value.Distance; best = pair.Value.Object; }
        return best;
    }

    private static void Snapshot(string when)
    {
        var obj = _dumpObject;
        if (obj == null) return;
        try
        {
            // A wrapper kept from an earlier poll can point at something the scene has dropped.
            if (!FieldAccessCatalog.IsLive(obj)) return;
        }
        catch { return; }

        var td = obj.GetTypeDefinition();
        var sb = new StringBuilder();
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "=== {0} interact watch: {1} ({2}) ===",
                                    when, Label(obj), td?.GetFullName() ?? "?"));
        ObjectDumper.DumpFieldsWithValues(sb, td, obj, "  ");
        sb.AppendLine();
        try { File.AppendAllText(Path.Combine(ObjectDumper.DumpDir, FILE), sb.ToString(), Encoding.UTF8); }
        catch { }
    }

    /// <summary>How the mod itself would name this object, so the dump can be matched against
    /// what the list says.</summary>
    private static string Label(ManagedObject obj)
    {
        try
        {
            var pos = AvatarFieldReader.ReadWorldPos(obj);
            if (!pos.ok) return "(no position)";
            string name = FlowHelper.CleanTags(FlowHelper.Call(obj, "GetDispName") as string)?.Trim();
            return string.IsNullOrEmpty(name)
                ? ExplorationText.InteractableNumbered(InteractableIds.Number(WorldTourStateService.CityId, pos.x, pos.y, pos.z))
                : name;
        }
        catch { return "(unreadable)"; }
    }

    private static string NameKey(int vk)
    {
        try
        {
            string name = InputNameResolver.KeyboardKeyName(vk);
            return name == "Unassigned" ? "vk 0x" + vk.ToString("X", CultureInfo.InvariantCulture) : name;
        }
        catch { return "vk 0x" + vk.ToString("X", CultureInfo.InvariantCulture); }
    }

    private static int[] Keys()
    {
        if (_keys != null) return _keys;
        var keys = new List<int>(48);
        for (int c = 'A'; c <= 'Z'; c++) keys.Add(c);
        for (int c = '0'; c <= '9'; c++) keys.Add(c);
        keys.Add(0x20);   // Space
        keys.Add(0x0D);   // Enter
        keys.Add(0x1B);   // Escape
        keys.Add(0x11);   // Control
        keys.Add(0x10);   // Shift
        keys.Add(0x12);   // Alt
        return _keys = keys.ToArray();
    }

    private static void Note(string what)
    {
        if (_notes++ >= MAX_NOTES) return;
        API.LogInfo(string.Format(CultureInfo.InvariantCulture,
            "[SF6Access] Interact probe: {0} - {1} offered within reach, dumped to {2}",
            what, _offered.Count, FILE));
    }

    private static void Clear()
    {
        _offered.Clear();
        _heldKeys.Clear();
        _heldPad = 0;
        _dumpObject = null;
        _dumpAt = 0;
    }

    internal static void Reset() => Clear();
}
