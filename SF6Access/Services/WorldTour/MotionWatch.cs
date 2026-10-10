using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using REFrameworkNET;
using SF6Access.Services;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>WHAT IS MOVING — and where it is now.</b>
///
/// <para><b>Why this exists (2026-10-06, SiRN building site).</b> The owner pressed a button and
/// a platform travelled across to them: "我按下按钮，升降机的平台会移动过来，你能抓住是哪个东西
/// 在移动吗，如果能，就把这个平台加到导航列表里". Answering that needs no theory about which
/// object family a lift belongs to — movement is itself the proof. Anything whose world position
/// changes between two observations HAS moved, and whatever moved towards the player is the thing
/// they pressed for.</para>
///
/// <para><b>How it is caught.</b> Nothing new is asked of the engine during the wide sweep:
/// <see cref="SceneTargetCatalog"/> already walks all forty <c>Om</c> families and already knows
/// where each object stood, so this merely compares where it stands NOW against where the same
/// object stood last — keyed on the object's address, which is what that sweep already uses as its
/// identity. Discovery is therefore free.</para>
///
/// <para><b>Then it is tracked fast.</b> The wide sweep comes back to a family only every sixteen
/// seconds, which is right for finding the thing and useless for following it. So once a family
/// turns out to hold something that moves, this watches THAT family alone every few hundred
/// milliseconds, and the position offered to the picker is where the platform is at the moment the
/// list is opened — not where it was when the ride began.</para>
///
/// <para><b>Anything can be counted, so the bar is movement, not membership.</b> A platform that
/// belongs to no lift family is offered exactly like one that does, because the test never asks
/// what the object IS. Nothing here infers "this must be the lift" from a name or a type.</para>
/// </summary>
internal static class MotionWatch
{
    /// <summary>Change between two observations that counts as "it moved". Large enough to
    /// shrug off float noise in a standing object, small enough to catch a platform that has
    /// only just begun to travel.</summary>
    private const float MovedM = 0.25f;

    /// <summary>The object's components are still there, but it has not been re-read for this
    /// long: drop it, rather than guiding the player towards something the scene has let go.</summary>
    private const long ForgetMs = 12000;

    /// <summary>How recently it must have moved still to be called "moving" — not "has moved".</summary>
    private const long HotMs = 2500;

    /// <summary>How long after its last movement it is still offered at all. A crate nudged once
    /// an hour ago is not a way to another floor, and a list is not improved by everything that
    /// ever twitched.</summary>
    private const long OfferMs = 300000;

    /// <summary>The fast re-read of the families something in them is moving.</summary>
    private const long FastMs = 300;

    /// <summary>Only what is within walking distance is tracked — this city is hundreds of
    /// metres across and everything in it is somebody's scenery.</summary>
    private const float RangeM = 120f;

    /// <summary>Only movement this close to the player earns a family the fast watch.</summary>
    private const float HotRangeM = 80f;

    /// <summary>How many families may be watched at once. Each one costs a read of everything in
    /// it every third of a second, and four hot families plus the hunt is already plenty.</summary>
    private const int MaxHotFamilies = 4;

    /// <summary>How long a family keeps the fast watch after the last thing in it moved. Released
    /// so that whatever twitched once and went quiet does not hold the pass for the rest of the
    /// session.</summary>
    private const long KeepHotMs = 20000;

    private const int MaxBodies = 192;

    private const string FILE = "sf6access_motion.txt";

    private sealed class Body
    {
        public ulong Addr;
        public string Type;              // full type name, so it can be scanned again
        public string Go;                // the level author's object name — survives restarts
        public float X, Y, Z;            // where it is now
        public long SeenAt;              // last observation of any kind
        public long MovedAt;             // last observation where it had actually moved
        public long PrevSeenAt;          // the observation before that one, for the speed
        public float PrevX, PrevY, PrevZ;
        public float LowY = float.NaN;   // the span it has actually travelled
        public float HighY = float.NaN;
        public float Rise;               // signed change over the last step; + is up
        public bool Missing;             // not answered on the last fast pass
    }

    // Keyed by ADDRESS: stable for as long as the object lives, and the same identity the wide
    // sweep uses, so "this is the same object" is never a guess made from a position.
    private static readonly Dictionary<ulong, Body> Bodies = new();
    private static readonly List<ulong> Dead = new();
    private static readonly HashSet<string> HotTypes = new(StringComparer.Ordinal);
    private static long _nextFast;
    private static long _nextDump;
    private static string _lastLogged;
    private static int _discoStep;
    private static int _discoTick;
    private static string[] _disco;

    /// <summary>The families walked while hunting. Same numbering the scene sweep already proves
    /// real, plus the access targets, which is what anything interactable usually is.</summary>
    private static string[] DiscoveryTypes()
    {
        if (_disco != null) return _disco;
        var list = new List<string>(48) { "app.worldtour.om.WTOmAccessTarget" };
        for (int family = 1; family <= 40; family++)
            list.Add(string.Format(CultureInfo.InvariantCulture, "app.worldtour.om.Om{0:000}000", family));
        return _disco = list.ToArray();
    }

    /// <summary>Called for every object the wide sweep reads. Cheap by construction: the position
    /// was read already; this only compares it to what the same object read last time.</summary>
    internal static void Note(ulong addr, string type, string go, float x, float y, float z, long now)
    {
        if (addr == 0) return;
        try
        {
            // Far away and not yet known: nothing here is worth a slot. Known movers stay
            // tracked even if they travelled out of range, so the range test is on discovery.
            bool known = Bodies.TryGetValue(addr, out var b);
            if (!known)
            {
                // One street holds more objects than this table has room for, so the table can
                // fill up with things that have never moved — and the platform that matters would
                // then be the one refused a slot. What gets pushed out is always the least
                // recently seen thing that has NEVER moved; anything that has moved once is
                // kept, because a thing known to travel is the entire point of watching.
                if (Bodies.Count >= MaxBodies)
                {
                    if (!Evict()) return;
                }
                if (!WithinRange(x, z, RangeM)) return;
                b = new Body
                {
                    Addr = addr, Type = type, Go = go,
                    X = x, Y = y, Z = z,
                    LowY = y, HighY = y,
                    SeenAt = now, PrevSeenAt = now,
                    PrevX = x, PrevY = y, PrevZ = z,
                };
                Bodies[addr] = b;
                return;
            }

            float dx = x - b.X, dy = y - b.Y, dz = z - b.Z;
            bool moved = dx * dx + dy * dy + dz * dz >= MovedM * MovedM;
            if (moved)
            {
                b.PrevX = b.X; b.PrevY = b.Y; b.PrevZ = b.Z; b.PrevSeenAt = b.SeenAt;
                b.Rise = dy;
                b.MovedAt = now;
                // A hundred metres away something always twitches — a bit of scenery in another
                // street twitched once on the building site and took the only fast watch there
                // was. Only what moved NEARBY earns it; the rest is still compared every pass.
                if (WithinRange(x, z, HotRangeM)) HotTypes.Add(type);
            }
            b.X = x; b.Y = y; b.Z = z;
            b.SeenAt = now;
            b.Missing = false;
            b.LowY = float.IsNaN(b.LowY) ? y : Math.Min(b.LowY, y);
            b.HighY = float.IsNaN(b.HighY) ? y : Math.Max(b.HighY, y);
        }
        catch { }
    }

    /// <summary>The fast re-read: only the families that have already proved they hold something
    /// that moves. Everything else keeps being discovered by the wide sweep for free.</summary>
    internal static void Pump()
    {
        long now = Environment.TickCount64;
        if (now < _nextFast) return;
        _nextFast = now + FastMs;
        try
        {
            Fast(now);
            Expire(now);
            Dump(now);
            Log();
        }
        catch { }
    }

    private static void Fast(long now)
    {
        // Both passes run always. They did not at first: the hunting stopped the moment any family
        // turned out to hold something that moves — and on the building site the thing that moved
        // was a hundred metres away and belonged to nobody, so IT got the fast watch and the
        // platform being summoned beside the player was left to the sixteen-second sweep. Nothing
        // is trusted with the only fast pair of eyes from now on. The hunt costs one family per
        // pass; that is cheaper than missing the answer.
        if (HotTypes.Count > 0)
        {
            int n = 0;
            foreach (string type in HotTypes)
            {
                if (n++ >= MaxHotFamilies) break;
                Read(type, now, allowNew: false);
            }
        }
        if (((_discoTick++) & 1) == 0) return;   // hunting walks at half speed once there is anything to follow
        string disco = DiscoveryTypes()[_discoStep];
        _discoStep = (_discoStep + 1) % DiscoveryTypes().Length;
        Read(disco, now, allowNew: true);
    }

    private static void Read(string type, long now, bool allowNew)
    {
        ManagedObject list = null;
        try { list = FieldProbeService.SceneComponents(type); }
        catch { list = null; }
        int n = FlowHelper.GetListCount(list);
        for (int i = 0; i < Math.Min(n, MaxBodies); i++)
        {
            ManagedObject om = null;
            try { om = FlowHelper.GetListItem(list, i); }
            catch { }
            if (om == null) continue;

            ulong addr = om.GetAddress();
            // While still hunting, every object of the family is worth REMEMBERING (its first
            // sighting is the baseline it will later be compared against). Once something moves,
            // only that thing is followed: walking the rest again would cost readings to learn
            // nothing the wide sweep is not already giving us.
            bool known = Bodies.TryGetValue(addr, out var prev);
            if (!known && !allowNew) continue;

            var (x, y, z, ok) = AvatarFieldReader.ReadWorldPos(om);
            if (!ok) { if (known) prev.Missing = true; continue; }
            Note(addr, type, known ? prev.Go : ObjectName(om), x, y, z, now);
        }
    }

    private static string ObjectName(ManagedObject om)
    {
        try { return FlowHelper.Call(om, "get_GameObject") is ManagedObject go ? FlowHelper.Call(go, "get_Name") as string : null; }
        catch { return null; }
    }

    private static void Expire(long now)
    {
        Dead.Clear();
        var stillHot = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in Bodies)
        {
            if (now - pair.Value.SeenAt > ForgetMs) { Dead.Add(pair.Key); continue; }
            if (now - pair.Value.MovedAt <= KeepHotMs) stillHot.Add(pair.Value.Type);
        }
        foreach (ulong a in Dead) Bodies.Remove(a);

        // Whatever holds something that moved lately keeps the fast pass; the rest of the list
        // shrinks back, so the watch belongs to what is actually travelling.
        if (HotTypes.Count > 0)
        {
            HotTypes.Clear();
            foreach (string t in stillHot) HotTypes.Add(t);
        }
    }

    /// <summary>Make room by dropping the object least recently seen that has never once moved,
    /// or false when every slot is held by something that has — those are worth keeping.</summary>
    private static bool Evict()
    {
        ulong victim = 0;
        long oldest = long.MaxValue;
        foreach (var pair in Bodies)
        {
            if (pair.Value.MovedAt != 0) continue;
            if (pair.Value.SeenAt >= oldest) continue;
            oldest = pair.Value.SeenAt;
            victim = pair.Key;
        }
        if (victim == 0) return false;
        Bodies.Remove(victim);
        return true;
    }

    /// <summary>Whether the object being first noticed is near enough to care about. Range is
    /// tested on DISCOVERY only — something already known keeps being followed even if it has
    /// travelled away, because the whole point is to be able to say where it went.</summary>
    private static bool WithinRange(float x, float z, float m)
    {
        RefreshMe();
        if (!_haveMe) return true;
        float dx = x - _meX, dz = z - _meZ;
        return dx * dx + dz * dz <= m * m;
    }

    // The player is asked once in a while, not once per object: a discovery pass can notice
    // dozens of things in one family, and their combined answer to "am I near enough" is one
    // question, not dozens.
    private static float _meX, _meZ;
    private static bool _haveMe;
    private static long _meAt;

    private static void RefreshMe()
    {
        long now = Environment.TickCount64;
        if (_haveMe && now - _meAt < 500) return;
        _meAt = now;
        try
        {
            var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
            if (!me.ok) return;
            _meX = me.x; _meZ = me.z; _haveMe = true;
        }
        catch { }
    }

    // ---------- what the picker is given ----------

    /// <summary>Everything that has actually moved lately, nearest first, offered WHERE IT IS
    /// NOW — which for something travelling towards the player is the one thing worth aiming
    /// at. <see cref="SceneTargetCatalog.AddTo"/> drops entries standing on top of a lift so the
    /// player does not hear the same platform twice.</summary>
    internal static void AddTo(List<Destination> into, float px, float pz)
    {
        Pump();
        long now = Environment.TickCount64;

        var live = new List<Body>(Bodies.Count);
        foreach (var b in Bodies.Values)
        {
            if (b.Missing) continue;
            if (now - b.MovedAt > OfferMs) continue;      // has not moved for five minutes: not a way anywhere
            if (AlreadyListed(into, b.X, b.Z)) continue;
            live.Add(b);
        }
        live.Sort((a, b) => FlatSq(a, px, pz).CompareTo(FlatSq(b, px, pz)));

        foreach (var b in live)
            into.Add(new Destination(Name(), Kind(b, now), DestinationCategory.Lift, b.X, b.Y, b.Z));
    }

    /// <summary>True when anything that has moved lately stands here. Used to keep the general
    /// scene sweep from listing the same travelling object a second time under a number.</summary>
    internal static bool HasMoverNear(float x, float z, float m)
    {
        long now = Environment.TickCount64;
        float mSq = m * m;
        foreach (var b in Bodies.Values)
        {
            if (b.Missing) continue;
            if (now - b.MovedAt > OfferMs) continue;
            float dx = b.X - x, dz = b.Z - z;
            if (dx * dx + dz * dz <= mSq) return true;
        }
        return false;
    }

    private static bool AlreadyListed(List<Destination> into, float x, float z)
    {
        // Only the LIFT entries: something the lift catalog already named is better described
        // there (it can say where the ride ends), and hearing the same platform twice under two
        // names is noise. Every other category keeps its own entries — a platform standing next
        // to a shop does not make the shop disappear.
        const float sameSq = 2f * 2f;
        foreach (var d in into)
        {
            if (d.Category != DestinationCategory.Lift || !d.HasPosition) continue;
            float dx = d.X - x, dz = d.Z - z;
            if (dx * dx + dz * dz <= sameSq) return true;
        }
        return false;
    }

    private static string Name() => LangFile.Get("wt.dest.moving", "Moving platform");

    /// <summary>Direction is measured, not assumed: the sign of the last change in height, or —
    /// when it has stopped moving but has travelled somewhere worth saying — the span it covers.
    /// Both are things the player can check by pressing the button again.</summary>
    private static string Kind(Body b, long now)
    {
        if (now - b.MovedAt <= HotMs)
        {
            return b.Rise > 0
                ? LangFile.Get("wt.dest.moving_up", "coming up")
                : LangFile.Get("wt.dest.moving_down", "going down");
        }
        if (float.IsFinite(b.LowY) && float.IsFinite(b.HighY) && b.HighY - b.LowY >= 1f)
            return string.Format(LangFile.Get("wt.dest.moving_span", "travels {0} to {1} metres"),
                                 b.LowY.ToString("F0", CultureInfo.InvariantCulture),
                                 b.HighY.ToString("F0", CultureInfo.InvariantCulture));
        return null;
    }

    private static float FlatSq(Body b, float px, float pz)
    {
        float dx = b.X - px, dz = b.Z - pz;
        return dx * dx + dz * dz;
    }

    // ---------- evidence ----------

    private static void Dump(long now)
    {
        if (now < _nextDump) return;
        _nextDump = now + 3000;

        var lines = new List<string>(Bodies.Count + 2);
        lines.Add("# everything that has MOVED, and how far it travels");
        lines.Add("# type\tgameObject\tnow(x,y,z)\ttravelSpan\tlastMoveMsAgo\triseLastStep");
        foreach (var b in Bodies.Values)
        {
            lines.Add(string.Format(CultureInfo.InvariantCulture, "{0}\t{1}\t{2:F1},{3:F1},{4:F1}\t{5:F1}..{6:F1}\t{7}\t{8:F2}",
                Short(b.Type), b.Go ?? "-", b.X, b.Y, b.Z, b.LowY, b.HighY,
                now - b.MovedAt, b.Rise));
        }
        try
        {
            File.WriteAllLines(Path.Combine(ObjectDumper.DumpDir, FILE), lines, Encoding.UTF8);
        }
        catch { }
    }

    private static void Log()
    {
        long now = Environment.TickCount64;
        int moved = 0, hot = 0;
        foreach (var b in Bodies.Values)
        {
            if (now - b.MovedAt > OfferMs) continue;
            moved++;
            if (now - b.MovedAt <= HotMs) hot++;
        }
        string line = string.Format(CultureInfo.InvariantCulture,
            "[SF6Access] Motion: {0} object(s) have moved, {1} moving now, {2} families watched fast",
            moved, hot, HotTypes.Count);
        if (line == _lastLogged) return;
        _lastLogged = line;
        API.LogInfo(line);

        foreach (var b in Bodies.Values)
        {
            if (now - b.MovedAt > OfferMs) continue;
            API.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[SF6Access] Moving: {0} ({1}) at {2:F1},{3:F1},{4:F1} - span {5:F1}..{6:F1}, last moved {7} ms ago, last step {8:F2} m",
                Short(b.Type), b.Go ?? "-", b.X, b.Y, b.Z, b.LowY, b.HighY, now - b.MovedAt, b.Rise));
        }
    }

    private static string Short(string type)
    {
        int dot = type.LastIndexOf('.');
        return dot >= 0 ? type.Substring(dot + 1) : type;
    }

    /// <summary>Another field: none of these objects exist any more, and a family that moves in
    /// one place is not assumed to move in the next.</summary>
    internal static void Reset()
    {
        Bodies.Clear();
        HotTypes.Clear();
        _nextFast = 0;
        _lastLogged = null;
        _discoStep = 0;
        _haveMe = false;
    }
}
