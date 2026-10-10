using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>Every interactable object standing in the field, not just the ones within arm's reach.</b>
///
/// <para><b>Why this exists (2026-10-03, SiRN building site).</b> The objective was a marker
/// three metres behind hoarding, the walkable route to it did not exist, and the owner's read
/// was "这个目标只是箭头，真正的交互点不是这里" — correct, and the follow-up question was
/// "我要找发电机，你能让他出现在列表里吗". Nothing answered that, because everything the
/// picker drew on was either IN RANGE right now (<see cref="InteractableCatalog"/>, a few
/// metres) or REGISTERED with the city (<see cref="AccessTargetCatalog"/>, 3 named contacts
/// and 26 with no name at all). A generator neither of those knows about had nowhere to come
/// from, so this walks the SCENE instead.</para>
///
/// <para><b>Everything on this map is listed, named or not</b> — the owner's rule, 2026-10-04:
/// "把这张地图上的所有可交互物加到列表里，按距离排序，如果没有名字就用序号". So entries are
/// sorted NEAREST FIRST and every object the game would not name answers to a NUMBER OF ITS
/// OWN instead of being dropped.</para>
///
/// <para><b>That number never changes</b> — the owner's follow-up was "我要他编号永远不变，我才
/// 知道我有没有交互过". A number that reshuffles because you took three steps cannot be
/// recognised again, let alone remembered as done, so numbers are NOT list positions: they are
/// given once per object, stored in <see cref="InteractableIds"/> keyed by where the object
/// stands, and they survive restarts.</para>
///
/// <para><b>How it names things.</b> In order: the component's <c>GetDispName()</c>, then the
/// same on the access target it owns, then the <c>DispName</c> message Guid resolved, then the
/// GameObject's own name tidied up. The last is not a guess — it is the name the level authors
/// gave the object — but it is NOT a name the game publishes, so since 2026-10-04 it is written
/// to the dump file and never spoken; the object answers to its number instead. "gm 025000" is
/// what buried the list under 150 unrecognisable entries on 2026-10-03, and a number cannot be
/// mistaken for anything — yet still lets two machines two metres apart be told apart.</para>
///
/// <para><b>Cost.</b> One scene walk per tick, spread over every type family in turn, so the
/// full sweep costs nothing measurable on any single frame; a hundred metres of scene cost the
/// same as two.</para>
/// </summary>
internal static class SceneTargetCatalog
{
    /// <summary>How far a discovered object may be and still be offered. The picker sorts
    /// nearest-first, so the honest thing is to offer rather than to silently decide for the
    /// player what is "too far" — that decision costs them the one object they walked here
    /// for.</summary>
    internal const float RangeM = 120f;

    /// <summary>The most this category ever offers. The owner asked for EVERY interactable on
    /// the map, so this is a circuit-breaker rather than a filter: well beyond anything a
    /// street actually holds, and the one number to change if the list ever feels too long.
    /// Everything is sorted nearest-first, so what is cut is always the farthest.</summary>
    private const int MaxOffered = 256;

    /// <summary>An unnamed object this close to something already listed is the same place
    /// wearing another name: half the beacon's arrival band, so arriving at one IS arriving at
    /// the other, and listing both is a duplicate rather than information. A REAL name is
    /// never dropped by this, whatever it stands next to.</summary>
    private const float SameSpotM = 2f;

    private const long PumpMs = 400;
    private const int BatchSize = 48;
    private const int MaxEntries = 512;
    private const long EntryTtlMs = 12000;
    private const long DumpMs = 15000;

    private const string FILE = "sf6access_scenetargets.txt";

    /// <summary><see cref="Real"/> is the difference between a name the GAME gave us and a
    /// name the level author typed into the editor. Only the first is worth speaking.</summary>
    private readonly record struct Entry(string Type, string GameObject, string Name,
                                         float X, float Y, float Z, long SeenAt, bool Real);

    private static readonly Dictionary<ulong, Entry> Found = new();
    private static readonly List<ulong> Stale = new();
    private static readonly Dictionary<string, int> TypeCounts = new(StringComparer.Ordinal);
    private static string[] _steps;
    private static int _step;
    private static int _cursor;
    private static long _nextPump, _nextDump, _lastDumpCount;

    /// <summary>The types walked, in order: the OM access targets first (that is what a
    /// generator IS, if it is interactable at all), then every assumed OM object family in the
    /// <c>OmNNN000</c> numbering the game uses for scenery — <see cref="BreakableCatalog"/>
    /// proves that numbering is real and that useful objects hide in families nobody expects.
    /// The sweep is built once and re-used; the families are a DISCOVERY net, so the cost of a
    /// wrong guess is one empty scene walk in every ~16 s cycle.</summary>
    private static string[] Steps()
    {
        if (_steps != null) return _steps;
        var steps = new List<string>(64);
        steps.Add("app.worldtour.om.WTOmAccessTarget");
        for (int family = 1; family <= 40; family++)
            steps.Add(string.Format(CultureInfo.InvariantCulture, "app.worldtour.om.Om{0:000}000", family));
        return _steps = steps.ToArray();
    }

    /// <summary><b>The whole scene, right now, before the list is spoken.</b>
    ///
    /// <para>The 400 ms rotation takes a full 16 s cycle to come back round to a family, and
    /// the owner's rule (2026-10-05) is "我每次打开列表都能看到所有交互物，无论是移动了还是换层了，
    /// 都要一个不落下的看见". Waiting for the rotation cannot promise that: walk into a new street,
    /// open the list inside the same second, and the family holding that street's generator may
    /// not be walked again for another fifteen. So opening the list walks EVERY family once,
    /// here and now, and the answer is as fresh as the press.</para>
    ///
    /// <para>Cost is one scene walk per family, paid once on the frame that opens a list a
    /// sighted player reads with their eyes — the same order of work the rotation already does
    /// every 400 ms, just paid in full instead of spread out. Nothing here is new work the
    /// engine was not already being asked for.</para></summary>
    internal static void SweepAll()
    {
        var steps = Steps();
        long now = Environment.TickCount64;
        for (int i = 0; i < steps.Length; i++)
            Walk(steps[i], now, int.MaxValue);   // whole families, not 48 at a time
        _step = 0;
        _cursor = 0;
        Expire(now);
        Dump(now);
        _nextPump = Environment.TickCount64 + PumpMs;
    }

    /// <summary>One family's worth of the scene, each tick. Called both by the picker and by
    /// the beacon's own tick, so walking for a few seconds fills this before the list is ever
    /// opened.</summary>
    internal static void Pump()
    {
        long now = Environment.TickCount64;
        if (now < _nextPump) return;
        _nextPump = now + PumpMs;
        Scan(now);
    }

    private static void Scan(long now)
    {
        var steps = Steps();
        string type = steps[_step];

        // Batched: a family with 212 members in it is read 48 at a time across the rotation,
        // which is right for a sweep nobody is waiting on.
        Walk(type, now, BatchSize);

        _step = (_step + 1) % steps.Length;
        Expire(now);
        Dump(now);
    }

    /// <summary>Every member of one family, all of it. <paramref name="limit"/> caps how many
    /// are read this call; the cursor only advances when the family was read WHOLE, so a capped
    /// read resumes where it stopped instead of skipping the tail.</summary>
    private static void Walk(string type, long now, int limit)
    {
        ManagedObject list;
        try { list = FieldProbeService.SceneComponents(type); }
        catch { list = null; }
        int count = FlowHelper.GetListCount(list);

        // Only a real answer is recorded: a type that does not exist answers 0 like a type that
        // exists and is empty, and FamiliesPresent counts what is really here.
        if (list == null) return;
        TypeCounts[type] = count;
        if (count == 0) return;

        int amount = Math.Min(count, limit);
        int start = _cursor % count;
        for (int j = 0; j < amount; j++)
        {
            ManagedObject om = null;
            try { om = FlowHelper.GetListItem(list, (start + j) % count); }
            catch { }
            if (om == null) continue;
            ulong addr = om.GetAddress();

            // Gone from the scene since the last observation: the wrapper would name a
            // thing that is no longer there. Proof of absence beats a stale position.
            if (!IsLive(om)) { Found.Remove(addr); continue; }

            var (x, y, z, ok) = AvatarFieldReader.ReadWorldPos(om);
            if (!ok || x == 0f && y == 0f && z == 0f) continue;

            // Remembered even with no name: the dump is HOW the next naming pass is made,
            // and an unnamed object we can at least show at its own coordinates.
            var named = NameOf(om);
            Found[addr] = new Entry(type, ObjectNameOf(om), named.Name, x, y, z, now, named.Real);
            // A rest station is worth keeping even after it leaves the sweep's memory: it is a
            // fixed place, and remembering it here is what lets the picker offer it tomorrow from
            // the other side of the city.
            if (named.Real && RestCatalog.IsRestName(named.Name))
                RestCatalog.Learn(WorldTourStateService.CityId, named.Name, x, y, z);
            // Whether THIS object has moved since it was last read. The position was taken
            // anyway; comparing it costs nothing and is the only honest answer to "which thing
            // is moving" — see MotionWatch.
            try { MotionWatch.Note(addr, type, ObjectNameOf(om), x, y, z, now); } catch { }
            if (Found.Count > MaxEntries) return;
        }
        _cursor = (start + amount) % count;
    }

    private static bool IsLive(ManagedObject om)
    {
        try { return FieldAccessCatalog.IsLive(om); }
        catch { return true; }
    }

    private static void Expire(long now)
    {
        Stale.Clear();
        foreach (var pair in Found)
            if (now - pair.Value.SeenAt > EntryTtlMs) Stale.Add(pair.Key);
        foreach (ulong key in Stale) Found.Remove(key);
    }

    /// <summary>The name to speak, or null when nothing honest can be said. Every step is an
    /// answer the GAME gave; the GameObject name is last because it is the raw authoring name
    /// ("om_generator_01") and needs tidying, but it is far better than nothing when the game
    /// has no display string — which is exactly the case for every one of those 26 unnamed
    /// contacts the city registry logs.</summary>
    private static (string Name, bool Real) NameOf(ManagedObject om)
    {
        string name = Clean(FlowHelper.Call(om, "GetDispName") as string)
                      ?? Clean(FlowHelper.Call(om, "get_DispName") as string);
        if (!string.IsNullOrEmpty(name)) return (name, true);

        ManagedObject access = null;
        try { access = MemberAccess.ReadFieldsFirst(om, "AccessTarget") as ManagedObject; }
        catch { }
        if (access != null)
        {
            name = Clean(FlowHelper.Call(access, "GetDispName") as string);
            if (!string.IsNullOrEmpty(name)) return (name, true);
        }

        name = GuidName(om) ?? (access != null ? GuidName(access) : null);
        if (!string.IsNullOrEmpty(name)) return (name, true);

        // A rest station is registered like a shop but publishes no display name until the player
        // stands next to it, which is why the sweep walked past one for a whole session without
        // naming it. IsHome() is the game's own answer about the object, and the word spoken is
        // the one the phone map itself puts on these icons — see RestCatalog.
        if (IsHome(om)) return (RestCatalog.Word, true);

        // Reached only when the game publishes no display string at all: what is left is the
        // level author's own object name ("gm_025000"). It is recorded — it is how the next
        // naming pass gets made — but it is NOT a real name and must never be spoken as one.
        string go = ObjectNameOf(om);
        return Usable(go) ? (Tidy(go), false) : (null, false);
    }

    /// <summary>Whether the object itself says it is a rest station. Read from the object rather
    /// than from a type name: the same family holds shops, machines and doors.</summary>
    private static bool IsHome(ManagedObject om)
    {
        try { return FlowHelper.Call(om, "IsHome") is bool b && b; }
        catch { return false; }
    }

    private static string Clean(string text)
    {
        string s = FlowHelper.CleanTags(text)?.Trim();
        return string.IsNullOrEmpty(s) || IsGuid(s) ? null : s;
    }

    /// <summary>A component's <c>DispName</c> is often a message Guid STRUCT rather than a
    /// string, which reads empty as one and must be resolved with its bytes pinned.</summary>
    private static string GuidName(ManagedObject target)
    {
        try
        {
            if (FieldProbeService.Member(target, "DispName") is REFrameworkNET.ValueType guid)
                return Clean(PinnedValue.Use(guid, _ => FlowHelper.ResolveGuid(guid)));
        }
        catch { }
        return null;
    }

    private static string ObjectNameOf(ManagedObject om)
    {
        try
        {
            var go = FlowHelper.Call(om, "get_GameObject") as ManagedObject;
            string n = go == null ? null : FlowHelper.Call(go, "get_Name") as string;
            return string.IsNullOrEmpty(n) ? null : n;
        }
        catch { return null; }
    }

    /// <summary>Whether a raw authoring name is worth speaking. Generated names
    /// (<c>GUID_7f3c…</c>), pure digits and the engine's own placeholders are noise: saying
    /// them would put an unpronounceable entry in a list someone navigates by ear.</summary>
    private static bool Usable(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length < 2) return false;
        if (IsGuid(name)) return false;
        foreach (char c in name)
            if (char.IsLetter(c)) return true;
        return false;
    }

    private static bool IsGuid(string s)
    {
        if (s.Length != 36) return false;
        return s[8] == '-' && s[13] == '-' && s[18] == '-' && s[23] == '-';
    }

    /// <summary>"om_generator_01" -&gt; "generator 01". Strip the type prefix and the
    /// underscores; keep whatever the level author actually wrote. Nothing is translated and
    /// nothing is invented, so an authoring name in another language stays that way rather
    /// than becomingEnglishwords.</summary>
    private static string Tidy(string name)
    {
        string s = name.Trim();
        int slash = s.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0) s = s.Substring(slash + 1);
        if (s.StartsWith("om_", StringComparison.OrdinalIgnoreCase)) s = s.Substring(3);
        s = s.Replace('_', ' ').Replace('.', ' ');
        while (s.IndexOf("  ", StringComparison.Ordinal) >= 0) s = s.Replace("  ", " ");
        return s.Trim();
    }

    /// <summary>Every object discovered near enough to walk to, as choosable destinations:
    /// nearest to farthest, named ones under their own name, everything else under the number
    /// it holds in that order.</summary>
    internal static void AddTo(List<Destination> into, float px, float pz)
    {
        // The whole field, read on the press that opens the list. Waiting for the rotation to
        // come round is what made "I walked here and the thing is not in my list" possible.
        SweepAll();
        float rangeSq = RangeM * RangeM, sameSq = SameSpotM * SameSpotM;
        var near = new List<Entry>(Found.Count);
        foreach (var pair in Found)
        {
            var e = pair.Value;
            float dx = e.X - px, dz = e.Z - pz;
            if (dx * dx + dz * dz > rangeSq) continue;
            near.Add(e);
        }
        // Nearest first, because that is the order worth hearing. The NUMBER each anonymous
        // object answers to is not this order though: it is the object's own, given once and
        // kept for good, so it can be recognised the next time the player comes past.
        near.Sort((a, b) => FlatSq(a, px, pz).CompareTo(FlatSq(b, px, pz)));

        int offered = 0, unnamed = 0;
        for (int i = 0; i < near.Count && offered < MaxOffered; i++)
        {
            var e = near[i];
            bool named = !string.IsNullOrEmpty(e.Name) && e.Real;
            // A lift is already in the list under its own name and its own category. Listing the
            // same object again here as an anonymous number would put the thing the player is
            // looking for in the list twice, once findable and once not.
            if (!named && LiftCatalog.HasShaftNear(e.X, e.Z, SameSpotM)) continue;
            // So is anything that has been seen to MOVE: a platform on its way over is already
            // offered under its own name, and finding it twice — once speakable, once as a
            // number — is worse than finding it once. See MotionWatch.
            if (!named && MotionWatch.HasMoverNear(e.X, e.Z, SameSpotM)) continue;
            // And so is a wall switch, or the platform that switch names as its own target:
            // both are already listed by SwitchLink with a description worth hearing.
            if (!named && SwitchLink.HasAnyNear(e.X, e.Z, SameSpotM)) continue;
            if (!named && AlreadyListed(into, e.X, e.Z, sameSq)) continue;
            string name = named ? e.Name : NumberLabel(e.X, e.Y, e.Z);
            if (!named) unnamed++;
            into.Add(new Destination(name, null, DestinationCategory.Access, e.X, e.Y, e.Z));
            offered++;
        }
        LogOffered(offered, unnamed);
    }

    /// <summary>Another destination of the same kind already stands this close: within the
    /// band that counts as "arrived", naming this one again would be a duplicate.</summary>
    private static bool AlreadyListed(List<Destination> into, float x, float z, float sameSq)
    {
        foreach (var d in into)
        {
            if (d.Category != DestinationCategory.Access || !d.HasPosition) continue;
            float dx = d.X - x, dz = d.Z - z;
            if (dx * dx + dz * dz <= sameSq) return true;
        }
        return false;
    }

    /// <summary>What the list ended up holding, in one line. The tester cannot see the
    /// picker, so "the generator is not in my list" has to be answerable from the log alone:
    /// this says whether nothing was found, nothing survived the dedupe, or the cap cut it.</summary>
    private static void LogOffered(int offered, int unnamed)
    {
        long now = Environment.TickCount64;
        if (now < _nextOfferLog) return;
        _nextOfferLog = now + DumpMs;
        string line = string.Format(CultureInfo.InvariantCulture,
            "{0} offered ({1} by name, {2} numbered), {3} seen in this field, {4} numbered so far",
            offered, offered - unnamed, unnamed, Found.Count, InteractableIds.Count);
        if (line == _lastOfferLine) return;
        _lastOfferLine = line;
        API.LogInfo("[SF6Access] Scene targets in the destination list: " + line);
    }

    private static long _nextOfferLog;
    private static string _lastOfferLine;

    /// <summary>The name an object the game will not name answers to: 可交互物 plus the number
    /// that belongs to the object itself, not to the moment — see <see cref="InteractableIds"/>.
    /// A number this thing keeps next session too, which is the whole point.</summary>
    private static string NumberLabel(float x, float y, float z) =>
        ExplorationText.InteractableNumbered(InteractableIds.Number(WorldTourStateService.CityId, x, y, z));

    private static float FlatSq(Entry e, float px, float pz)
    {
        float dx = e.X - px, dz = e.Z - pz;
        return dx * dx + dz * dz;
    }

    /// <summary>Every object in the field that can be VISITED, nearest first: the named ones
    /// under their own name, everything else under the number it keeps for good. This is the
    /// honest answer to "all the interactables on this map", and it is what the grand tour walks
    /// — asking only for real names here would leave out most of the world, and that is exactly
    /// what the owner asked it not to do.</summary>
    internal static void CollectAll(List<Named> into, float px, float pz, float rangeM, float skipM)
    {
        Pump();
        float rangeSq = rangeM * rangeM, skipSq = skipM * skipM;
        var near = new List<Entry>(Found.Count);
        foreach (var pair in Found)
        {
            var e = pair.Value;
            float dx = e.X - px, dz = e.Z - pz;
            float sq = dx * dx + dz * dz;
            if (sq > rangeSq || sq < skipSq) continue;
            near.Add(e);
        }
        near.Sort((a, b) => FlatSq(a, px, pz).CompareTo(FlatSq(b, px, pz)));
        foreach (var e in near)
        {
            bool named = !string.IsNullOrEmpty(e.Name) && e.Real;
            into.Add(new Named(named ? e.Name : NumberLabel(e.X, e.Y, e.Z), e.X, e.Y, e.Z));
        }
    }

    /// <summary>An object the player can be told about by name: what the game calls it, and
    /// where it stands.</summary>
    internal readonly record struct Named(string Name, float X, float Y, float Z);

    /// <summary>Everything within arm's hearing that carries a REAL name, nearest first.
    ///
    /// <para>This is the "just read the name" channel: the picker answers "where can I go",
    /// this answers "what is around me". The scene sweep is what makes it possible at all —
    /// the game's own in-range list only covers what can be touched.</para>
    ///
    /// <para>Objects closer than <paramref name="skipM"/> are left out: at that distance the
    /// game's own access list already describes them, and naming the same thing twice under
    /// two names is worse than naming it once.</para>
    /// </summary>
    internal static void CollectNear(List<Named> into, float px, float pz, float rangeM, float skipM)
    {
        Pump();
        float rangeSq = rangeM * rangeM, skipSq = skipM * skipM;
        var near = new List<Entry>(Found.Count);
        foreach (var pair in Found)
        {
            var e = pair.Value;
            if (string.IsNullOrEmpty(e.Name) || !e.Real) continue;
            float dx = e.X - px, dz = e.Z - pz;
            float sq = dx * dx + dz * dz;
            if (sq > rangeSq || sq < skipSq) continue;
            near.Add(e);
        }
        near.Sort((a, b) => FlatSq(a, px, pz).CompareTo(FlatSq(b, px, pz)));
        foreach (var e in near) into.Add(new Named(e.Name, e.X, e.Y, e.Z));
    }

    /// <summary>Everything seen, named or not, written out so the NEXT naming pass is made
    /// from evidence: if the generator is in the scene this file says what the game calls it.
    /// One file the mod owns, rewritten whole, the same way districts are persisted.</summary>
    private static void Dump(long now)
    {
        if (now < _nextDump) return;
        _nextDump = now + DumpMs;
        if (Found.Count == _lastDumpCount) return;
        _lastDumpCount = Found.Count;

        var lines = new List<string>(Found.Count + TypeCounts.Count + 2);
        foreach (var pair in TypeCounts)
            if (pair.Value > 0)
                lines.Add(string.Format(CultureInfo.InvariantCulture, "# {0} x{1}", pair.Key, pair.Value));
        foreach (var pair in Found)
        {
            var e = pair.Value;
            lines.Add(string.Format(CultureInfo.InvariantCulture,
                "{0}\t{1}\t{2}\t{3:F1},{4:F1},{5:F1}",
                string.IsNullOrEmpty(e.Name) ? "(no name)" : e.Real ? e.Name : e.Name + " (authoring)",
                e.GameObject ?? "-", e.Type, e.X, e.Y, e.Z));
        }

        try
        {
            string path = Path.Combine(ObjectDumper.DumpDir, FILE);
            File.WriteAllLines(path, lines, Encoding.UTF8);
            API.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[SF6Access] Scene targets: {0} objects in the field, {1} named by the game, {2} only by the level author - written to {3}",
                Found.Count, RealNames(), Found.Count - RealNames(), FILE));
        }
        catch { }

        LogSummary();
    }

    private static int RealNames()
    {
        int n = 0;
        foreach (var pair in Found)
            if (!string.IsNullOrEmpty(pair.Value.Name) && pair.Value.Real) n++;
        return n;
    }

    private static long _nextLog;
    private static string _lastSummary;

    /// <summary>How many object families answered with something, and the names found. Two
    /// lines every little while: the field's inventory, which is also the evidence that a
    /// missing object really is missing.</summary>
    private static void LogSummary()
    {
        long now = Environment.TickCount64;
        if (now < _nextLog) return;
        _nextLog = now + DumpMs;

        var kinds = new List<string>(TypeCounts.Count);
        foreach (var pair in TypeCounts)
            if (pair.Value > 0) kinds.Add(string.Format(CultureInfo.InvariantCulture, "{0}={1}", Short(pair.Key), pair.Value));
        if (kinds.Count == 0) return;

        string summary = string.Join(" ", kinds);
        if (summary == _lastSummary) return;
        _lastSummary = summary;

        var names = new List<string>(24);
        foreach (var pair in Found)
        {
            if (names.Count >= 24) break;
            if (!string.IsNullOrEmpty(pair.Value.Name) && pair.Value.Real) names.Add(pair.Value.Name);
        }
        API.LogInfo(string.Format(CultureInfo.InvariantCulture,
            "[SF6Access] Scene objects present: {0}", summary));
        API.LogInfo(string.Format(CultureInfo.InvariantCulture,
            "[SF6Access] Scene object names: {0}",
            names.Count == 0 ? "(none readable)" : string.Join(" / ", names)));
    }

    private static string Short(string type)
    {
        int dot = type.LastIndexOf('.');
        return dot >= 0 ? type.Substring(dot + 1) : type;
    }

    /// <summary>Another field, another city: none of these objects are here.</summary>
    internal static void Reset()
    {
        Found.Clear();
        TypeCounts.Clear();
        _cursor = 0;
        _step = 0;
        _nextPump = 0;
        _lastDumpCount = 0;
        _lastSummary = null;
        _lastOfferLine = null;
    }
}
