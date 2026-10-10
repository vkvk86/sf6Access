using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using REFrameworkNET;
using SF6Access.Services;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>THE SWITCH AND THE PLATFORM IT MOVES — read out of the level, not guessed.</b>
///
/// <para><b>Why this exists (2026-10-06, SiRN building site).</b> The owner pressed a wall button
/// and a platform travelled across to them, and the question was which thing moves. Three earlier
/// attempts answered it by guessing which object family a lift belongs to, and each guess was
/// either wrong or unprovable. This answers it the way the level itself states it.</para>
///
/// <para><b>The link really is written down.</b> <c>app.worldtour.om.Om024000</c> is the wall
/// switch (<c>IsWallSwitch</c>, states <c>Disable_Action</c>/<c>Able_Action</c>/<c>InOperate</c>).
/// It owns a <c>WTSwitchOMSupporter</c>, and that supporter carries <c>TargetObjArray</c> — the
/// objects this switch drives — plus <c>RequestExecDataList</c>, whose entries hold the very same
/// objects as <c>SwitchSupported</c>. Both names were read out of the engine's own type database,
/// so "this switch moves that platform" is the level author's answer, not ours.</para>
///
/// <para><b>What that buys.</b> The platform can be named before it is ever summoned: right after
/// the switch has been found, not only while it happens to be travelling. And the switch itself is
/// offered too, so the whole errand is walkable in order — first to the button, then onto what it
/// brings.</para>
///
/// <para><b>Direction is still measured.</b> Which platform moves and where the switch is come
/// from the level; whether it is rising or falling, and the span it covers, are read from the
/// platform's own change in height between two observations.</para>
/// </summary>
internal static class SwitchLink
{
    /// <summary>The wall switch. Read from the type database, where it is the only family carrying
    /// both <c>IsWallSwitch</c> and a switch supporter.</summary>
    internal const string SWITCH_TYPE = "app.worldtour.om.Om024000";

    /// <summary>Where a target must stand, relative to the player, to be worth naming. The chain
    /// from a switch can reach across the whole district; most of it is no use to somebody
    /// standing here.</summary>
    private const float RangeM = 90f;

    /// <summary>Already named under a better description, so it is not said twice.</summary>
    private const float SameM = 2f;

    /// <summary>Change between two observations that counts as movement.</summary>
    private const float MovedM = 0.25f;

    private const long HotMs = 2500;

    /// <summary>How often the switch and its targets are re-read. Fast enough to say where a
    /// travelling platform is at the moment the list is opened.</summary>
    private const long RefreshMs = 400;

    private const int MaxSwitches = 12;
    private const int MaxTargets = 12;
    private const int MaxOffered = 10;
    private const long ForgetMs = 15000;

    /// <summary>How long a platform the level once named stays offered after it stops answering.
    /// Longer than a switch's own expiry on purpose: the target array empties again the moment the
    /// ride is over, and something that was called once is still where the switch brings it.</summary>
    private const long TargetMemoryMs = 120000;

    private const string FILE = "sf6access_switch_link.txt";

    private sealed class Target
    {
        public ulong Addr;
        public string Type;
        public string Go;
        public float X, Y, Z;
        public float LowY = float.NaN, HighY = float.NaN;
        public float Rise;
        public long MovedAt;
        public long SeenAt;
    }

    private sealed class Switch
    {
        public ulong Addr;
        public string Go;
        public float X, Y, Z;
        public int State = -1;      // the byte enum: Disable_Action / Able_Action / InOperate
        public int InitState = -1;
        public long SeenAt;
        public long PressedAt;      // when it was last seen leave its initial state
        public bool ResolveFailed;  // tried for the supporter and got nothing — stop asking
        public readonly List<Target> Targets = new();
    }

    private static readonly Dictionary<ulong, Switch> Switches = new();
    private static readonly List<ulong> Dead = new();
    private static long _nextRead;
    private static long _nextDump;
    private static string _lastLogged;

    internal static void Pump()
    {
        long now = Environment.TickCount64;
        if (now < _nextRead) return;
        _nextRead = now + RefreshMs;
        try
        {
            Read(now);
            Expire(now);
            Dump(now);
            Log(now);
        }
        catch { }
    }

    /// <summary>A read now, whatever the clock says — the picker is opening, and the owner's rule
    /// is that opening the list shows what is really there.</summary>
    internal static void Sweep() => Read(Environment.TickCount64);

    private static void Read(long now)
    {
        ManagedObject list = null;
        try { list = FieldProbeService.SceneComponents(SWITCH_TYPE); }
        catch { list = null; }
        int n = Math.Min(FlowHelper.GetListCount(list), MaxSwitches);

        for (int i = 0; i < n; i++)
        {
            ManagedObject om = null;
            try { om = FlowHelper.GetListItem(list, i); }
            catch { }
            if (om == null) continue;

            var (x, y, z, ok) = AvatarFieldReader.ReadWorldPos(om);
            if (!ok || (x == 0f && y == 0f && z == 0f)) continue;

            ulong addr = om.GetAddress();
            if (!Switches.TryGetValue(addr, out var sw))
            {
                if (Switches.Count >= MaxSwitches) continue;
                sw = new Switch { Addr = addr, Go = ObjectName(om) };
                Switches[addr] = sw;
            }
            bool wasPressed = Pressed(sw);
            sw.X = x; sw.Y = y; sw.Z = z;
            sw.SeenAt = now;
            sw.State = FlowHelper.ReadByteField(om, "CurrentState", sw.State);
            sw.InitState = FlowHelper.ReadByteField(om, "InitState", sw.InitState);

            ReadTargets(sw, om, now);

            if (!wasPressed && Pressed(sw))
            {
                sw.PressedAt = now;
                // The one moment that answers "which thing did that?": the level has just filled
                // the supporter's target list, so what it names NOW is what this press moves.
                API.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "[SF6Access] Switch PRESSED {0} at {1:F1},{2:F1},{3:F1} - now names {4} object(s){5}",
                    sw.Go ?? "-", sw.X, sw.Y, sw.Z, sw.Targets.Count,
                    Describe(sw)));
            }
        }
    }

    private static bool Pressed(Switch sw) => sw.State >= 0 && sw.InitState >= 0 && sw.State != sw.InitState;

    private static string Describe(Switch sw)
    {
        var sb = new StringBuilder();
        foreach (var t in sw.Targets)
            sb.Append(string.Format(CultureInfo.InvariantCulture, "; {0} ({1}) at {2:F1},{3:F1},{4:F1}",
                t.Go ?? "-", Short(t.Type), t.X, t.Y, t.Z));
        return sb.ToString();
    }

    /// <summary>Every object this switch drives, straight out of its supporter. Two routes are
    /// tried because the engine exposes the same objects both ways and either table can be empty
    /// in a given level: the supporter's target array first, then its request list.</summary>
    private static void ReadTargets(Switch sw, ManagedObject om, long now)
    {
        if (sw.ResolveFailed) return;

        ManagedObject supporter = null;
        try { supporter = FlowHelper.GetObjectField(om, "SwitchSupporter"); }
        catch { }
        if (supporter == null) { sw.ResolveFailed = true; return; }

        bool any = Collect(sw, FlowHelper.GetObjectField(supporter, "TargetObjArray"), now);

        ManagedObject reqs = null;
        try { reqs = FlowHelper.GetObjectField(supporter, "RequestExecDataList"); }
        catch { }
        int reqCount = FlowHelper.GetListCount(reqs);
        for (int i = 0; i < Math.Min(reqCount, MaxTargets) && !any; i++)
        {
            ManagedObject data = null;
            try { data = FlowHelper.GetListItem(reqs, i); }
            catch { }
            if (data == null) continue;

            ManagedObject supported = null;
            try { supported = FlowHelper.GetObjectField(data, "SwitchSupported"); }
            catch { }
            NoteTarget(sw, supported, now);
        }
    }

    /// <summary>Reads every entry of the target array. False when nothing usable came out of it,
    /// which is what sends the reader to the second route.</summary>
    private static bool Collect(Switch sw, ManagedObject array, long now)
    {
        if (array == null) return false;
        int count = FlowHelper.GetListCount(array);
        bool any = false;
        for (int i = 0; i < Math.Min(count, MaxTargets); i++)
        {
            ManagedObject item = null;
            try { item = FlowHelper.GetListItem(array, i); }
            catch { }
            if (NoteTarget(sw, item, now)) any = true;
        }
        return any;
    }

    private static bool NoteTarget(Switch sw, ManagedObject obj, long now)
    {
        if (obj == null) return false;
        var (x, y, z, ok) = AvatarFieldReader.ReadWorldPos(obj);
        if (!ok || (x == 0f && y == 0f && z == 0f)) return false;

        ulong addr = obj.GetAddress();
        Target t = null;
        foreach (var cand in sw.Targets) if (cand.Addr == addr) { t = cand; break; }

        if (t == null)
        {
            if (sw.Targets.Count >= MaxTargets) return true;
            t = new Target
            {
                Addr = addr, Type = TypeOf(obj), Go = ObjectName(obj),
                X = x, Y = y, Z = z, LowY = y, HighY = y,
            };
            sw.Targets.Add(t);
        }
        else
        {
            float dx = x - t.X, dy = y - t.Y, dz = z - t.Z;
            if (dx * dx + dy * dy + dz * dz >= MovedM * MovedM)
            {
                t.Rise = dy;
                t.MovedAt = now;
            }
            t.X = x; t.Y = y; t.Z = z;
            t.LowY = float.IsNaN(t.LowY) ? y : Math.Min(t.LowY, y);
            t.HighY = float.IsNaN(t.HighY) ? y : Math.Max(t.HighY, y);
        }
        t.SeenAt = now;
        return true;
    }

    private static void Expire(long now)
    {
        Dead.Clear();
        foreach (var pair in Switches)
            if (now - pair.Value.SeenAt > ForgetMs) Dead.Add(pair.Key);
        foreach (ulong a in Dead) Switches.Remove(a);
    }

    private static string TypeOf(ManagedObject om)
    {
        try { return om.GetTypeDefinition()?.FullName ?? "?"; }
        catch { return "?"; }
    }

    private static string ObjectName(ManagedObject om)
    {
        try
        {
            if (FlowHelper.Call(om, "get_GameObject") is ManagedObject go &&
                FlowHelper.Call(go, "get_Name") is string s) return s;
        }
        catch { }
        return null;
    }

    // ---------- what the picker is given ----------

    /// <summary>The switches within walking distance and what each of them drives. Nearest first,
    /// because the errand has an order: reach the button, then what the button brings.</summary>
    internal static void AddTo(List<Destination> into, float px, float pz)
    {
        Pump();
        Sweep();
        long now = Environment.TickCount64;

        var picks = new List<(float dist, string name, string kind, float x, float y, float z)>();

        foreach (var sw in Switches.Values)
        {
            float flatS = FlatSq(sw.X, sw.Z, px, pz);
            if (flatS > RangeM * RangeM) continue;

            if (!Merge(into, sw.X, sw.Z, SwitchKind(sw), false))
                picks.Add((flatS, SwitchName(), SwitchKind(sw), sw.X, sw.Y, sw.Z));

            foreach (var t in sw.Targets)
            {
                if (now - t.SeenAt > TargetMemoryMs) continue;
                float tSq = FlatSq(t.X, t.Z, px, pz);
                if (tSq > RangeM * RangeM) continue;

                string kind = PlatformKind(t, now);
                // The platform this switch drives is usually ALREADY in the list as a lift — the
                // lift sweep found it first, under a name of its own. Dropping it here is exactly
                // what made it invisible to the owner who pressed the switch: the one thing they
                // were looking for was the one thing refused a second entry. So instead of being
                // skipped it is IMPROVED in place — the same object now says who moves it, and
                // says "coming down" the moment it does.
                if (Merge(into, t.X, t.Z, kind, Moving(t, now))) continue;
                picks.Add((tSq, PlatformName(), kind, t.X, t.Y, t.Z));
            }
        }

        picks.Sort((a, b) => a.dist.CompareTo(b.dist));
        int n = Math.Min(picks.Count, MaxOffered);
        for (int i = 0; i < n; i++)
        {
            var p = picks[i];
            into.Add(new Destination(p.name, p.kind, DestinationCategory.Lift, p.x, p.y, p.z));
        }
    }

    /// <summary>True when a switch, or something a switch drives, stands here. Used to keep the
    /// general scene sweep from naming the same object again under a number.</summary>
    internal static bool HasAnyNear(float x, float z, float m)
    {
        long now = Environment.TickCount64;
        float mSq = m * m;
        foreach (var sw in Switches.Values)
        {
            if (FlatSq(sw.X, sw.Z, x, z) <= mSq) return true;
            foreach (var t in sw.Targets)
            {
                if (now - t.SeenAt > TargetMemoryMs) continue;
                if (FlatSq(t.X, t.Z, x, z) <= mSq) return true;
            }
        }
        return false;
    }

    /// <summary>Rewrite an entry already standing here instead of adding a second one.</summary>
    /// <para>Why improve rather than add: on the building site the object the switch drives IS one
    /// of the two lifts, which <see cref="LiftCatalog"/> had already listed under its own name.
    /// Skipping it as a duplicate meant that pressing the switch changed nothing the owner could
    /// hear — the one thing they were looking for was the one thing always suppressed.</para>
    /// <param name="moving">Something in motion outranks what the entry already said; anything
    /// else only fills in an entry that had nothing to say.</param>
    private static bool Merge(List<Destination> into, float x, float z, string kind, bool moving)
    {
        if (string.IsNullOrEmpty(kind)) return false;
        float sameSq = SameM * SameM;
        for (int i = 0; i < into.Count; i++)
        {
            var d = into[i];
            if (d.Category != DestinationCategory.Lift || !d.HasPosition) continue;
            float dx = d.X - x, dz = d.Z - z;
            if (dx * dx + dz * dz > sameSq) continue;

            if ((moving || string.IsNullOrEmpty(d.Kind)) && d.Kind != kind)
                into[i] = d with { Kind = kind };
            return true;
        }
        return false;
    }

    private static string SwitchName() => LangFile.Get("wt.dest.switch", "Platform switch");

    private static string PlatformName() => LangFile.Get("wt.dest.switch_platform", "Platform moved by a switch");

    /// <summary>Pressed or not is the level's own comparison: every switch carries the state it
    /// started in next to the state it is in. Saying "already pressed" only when those two differ
    /// is the difference between a fact and a guess.</summary>
    private static string SwitchKind(Switch sw)
    {
        if (sw.State >= 0 && sw.InitState >= 0 && sw.State != sw.InitState)
            return LangFile.Get("wt.dest.switch_pressed", "already pressed");
        return LangFile.Get("wt.dest.switch_ready", "not pressed yet");
    }

    private static bool Moving(Target t, long now) => t.MovedAt != 0 && now - t.MovedAt <= HotMs;

    private static string PlatformKind(Target t, long now)
    {
        if (Moving(t, now))
        {
            if (Math.Abs(t.Rise) < 0.05f) return LangFile.Get("wt.dest.moving_now", "moving now");
            return t.Rise > 0
                ? LangFile.Get("wt.dest.moving_up", "coming up")
                : LangFile.Get("wt.dest.moving_down", "coming down");
        }
        if (float.IsFinite(t.LowY) && float.IsFinite(t.HighY) && t.HighY - t.LowY >= 1f)
            return string.Format(LangFile.Get("wt.dest.moving_span", "travels between {0} and {1} metres"),
                                 t.LowY.ToString("F0", CultureInfo.InvariantCulture),
                                 t.HighY.ToString("F0", CultureInfo.InvariantCulture));
        // Nothing observed moving yet: still worth saying WHO moves it, because that is the one
        // thing the owner cannot find out any other way.
        return LangFile.Get("wt.dest.switch_driven", "moved by the switch beside it");
    }

    private static float FlatSq(float x, float z, float px, float pz)
    {
        float dx = x - px, dz = z - pz;
        return dx * dx + dz * dz;
    }

    // ---------- evidence ----------

    private static void Dump(long now)
    {
        if (now < _nextDump) return;
        _nextDump = now + 2000;

        var lines = new List<string> { "# every wall switch in this scene and WHAT IT MOVES" };
        foreach (var sw in Switches.Values)
        {
            lines.Add(string.Format(CultureInfo.InvariantCulture,
                "switch {0} at {1:F1},{2:F1},{3:F1} state={4} init={5} -> {6} target(s){7}",
                sw.Go ?? "-", sw.X, sw.Y, sw.Z, sw.State, sw.InitState, sw.Targets.Count,
                sw.ResolveFailed ? " (supporter unreadable)" : ""));
            foreach (var t in sw.Targets)
                lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "    target {0} ({1}) at {2:F1},{3:F1},{4:F1} span {5:F1}..{6:F1} lastMoveMsAgo={7}",
                    t.Go ?? "-", Short(t.Type), t.X, t.Y, t.Z, t.LowY, t.HighY, now - t.MovedAt));
        }
        try { File.WriteAllLines(Path.Combine(ObjectDumper.DumpDir, FILE), lines, Encoding.UTF8); }
        catch { }
    }

    private static void Log(long now)
    {
        string line = string.Format(CultureInfo.InvariantCulture,
            "[SF6Access] Switch links: {0} wall switch(es), {1} driven object(s)",
            Switches.Count, CountTargets());
        if (line == _lastLogged) return;
        _lastLogged = line;
        API.LogInfo(line);

        foreach (var sw in Switches.Values)
        {
            API.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[SF6Access] Switch {0} at {1:F1},{2:F1},{3:F1} state {4}/{5} drives {6}{7}",
                sw.Go ?? "-", sw.X, sw.Y, sw.Z, sw.State, sw.InitState, sw.Targets.Count,
                sw.ResolveFailed ? " (supporter unreadable)" : ""));
            foreach (var t in sw.Targets)
                API.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "[SF6Access]   drives {0} ({1}) at {2:F1},{3:F1},{4:F1} - span {5:F1}..{6:F1}, last moved {7} ms ago",
                    t.Go ?? "-", Short(t.Type), t.X, t.Y, t.Z, t.LowY, t.HighY, now - t.MovedAt));
        }
    }

    private static int CountTargets()
    {
        int n = 0;
        foreach (var sw in Switches.Values) n += sw.Targets.Count;
        return n;
    }

    private static string Short(string type)
    {
        int dot = type.LastIndexOf('.');
        return dot >= 0 ? type.Substring(dot + 1) : type;
    }

    internal static void Reset()
    {
        Switches.Clear();
        Dead.Clear();
        _nextRead = 0;
        _nextDump = 0;
        _lastLogged = null;
    }
}
