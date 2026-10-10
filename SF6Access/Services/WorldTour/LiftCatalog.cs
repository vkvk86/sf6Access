using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using REFrameworkNET;
using SF6Access.Services;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>The lifts — 升降机 — the platforms that carry the player between floors.</b>
///
/// <para><b>Why this exists (2026-10-06, SiRN building).</b> The owner asked "我要找升降机，我走
/// 上去以后他会带我到研究大楼一层，但我找不到他，你让他出现在导航列表里". Every lift-shaped thing
/// in the field was already reachable by the scene sweep — <see cref="SceneTargetCatalog"/> walks
/// all forty <c>Om0NN000</c> families — but it came out of that sweep as one more unnumbered
/// object among two hundred, which for a player navigating by ear is not a thing they can find.
/// The sweep is right to list everything; what was missing was knowing WHICH of those objects
/// is the lift, and saying so.</para>
///
/// <para><b>The types are not guesses — there are two of them, and both are read out of the
/// type database.</b></para>
///
/// <para><c>app.worldtour.om.Om006000</c> is the plain moving platform: <c>UpHeight</c> /
/// <c>UnderHeight</c>, <c>StopHeightDataList</c> (one <c>Height</c> per floor it stops at),
/// <c>DescentSpeed</c>, <c>IsMotionMove</c>, and a state enum whose members are literally
/// <c>Up</c>, <c>OnDescent</c>, <c>Stop</c>, <c>Under</c>.</para>
///
/// <para><c>app.worldtour.om.Om029000</c> is the one that carries the player to ANOTHER SCENE —
/// its state enum is <c>Up</c> / <c>Middle</c> / <c>Down</c> plus the six in-between moving
/// states, and it holds a <c>SetSceneId</c>. That is exactly the thing the owner described —
/// "我走上去以后他会带我到研究大楼一层" — a platform you stand on which then puts you somewhere
/// else. It is swept as a lift for the same reason and listed under the same name.</para>
///
/// <para>Nothing here infers "this looks like a lift" from a position; the engine says what
/// the object is.</para>
///
/// <para><b>A lift moves, so what is remembered is the SHAFT, not the car.</b> The component's
/// position is the platform's, and the platform travels. The shaft — where it stands on the
/// map (<c>x</c>, <c>z</c>) and which heights it stops at — is what does not change, and it is
/// what gets written down. The entry offered to the picker is placed at the stop NEAREST THE
/// PLAYER'S OWN HEIGHT, which is the landing they would actually walk onto, rather than at
/// wherever the car happens to be.</para>
///
/// <para><b>Remembered across sessions, per city.</b> A lift is often in a section the game has
/// not streamed in while the player stands somewhere else, and "the lift is not in my list"
/// and "the lift is not loaded right now" are indistinguishable from the outside. So every
/// lift seen is written to <c>sf6access_lifts.txt</c> and keeps being offered for that city —
/// the same contract <see cref="DistrictMemory"/> keeps for districts. The file is also the
/// evidence: it says what the game itself reports about each lift.</para>
/// </summary>
internal static class LiftCatalog
{
    /// <summary>The game's moving-platform lift component, confirmed against the type database.</summary>
    internal const string LIFT_TYPE = "app.worldtour.om.Om006000";

    /// <summary>The lift that carries the player into another scene — the construction hoist at
    /// SiRN, which is what the owner was looking for. Confirmed against the type database.</summary>
    internal const string SCENE_LIFT_TYPE = "app.worldtour.om.Om029000";

    private static readonly string[] Types = { LIFT_TYPE, SCENE_LIFT_TYPE };

    /// <summary>The other object families that can move the player somewhere else, dumped in the
    /// evidence file while they are within reach: <c>Om016000</c>/<c>Om017000</c> are doors (they
    /// carry a <c>MoveType</c>), <c>Om024000</c> is the wall switch that calls a lift down, and
    /// <c>Om021000</c> is the one family whose members did not load in an offline reader at all,
    /// which is a reason to look at it live rather than a reason to ignore it.</summary>
    private static readonly string[] OtherCarriers = { "Om016000", "Om017000", "Om021000", "Om024000" };

    /// <summary>How close a door or switch must be to be worth printing.</summary>
    private const float NearM = 30f;

    private const string FILE = "sf6access_lifts.txt";

    /// <summary>How often the scene is asked again. One family, a handful of objects — but a
    /// lift only exists in the scene while its section is streamed in, so this is also how a
   /// lift that arrives a few seconds after the player does still gets noticed.</summary>
    private const long RescanMs = 2000;

    /// <summary>Circuit breaker on how many lifts one city can hold.</summary>
    private const int MaxShafts = 32;

    /// <summary>Circuit breaker on how many stop heights are read off one lift.</summary>
    private const int MaxStops = 24;

    /// <summary>Two sightings this close together on the map are the same shaft.</summary>
    private const float SameShaftM = 4f;

    private const long DumpMs = 15000;

    private sealed class Shaft
    {
        public uint City;
        public float X, Y, Z;          // Y = where the car was last seen; the shaft's own span is below
        public float LowY = float.NaN; // UnderHeight
        public float HighY = float.NaN;// UpHeight
        public string State;
        public uint SceneId;        // Om029000 only: the scene the lift puts the player in
        public int Type;            // 0 = Om006000 platform, 1 = Om029000 scene lift

        /// <summary>The section the player was standing in the moment AFTER they were last seen
        /// on this shaft and the section changed — i.e. where this lift actually delivered them.
        /// This is the answer to "which one takes me to the lab", written down by the ride
        /// itself rather than inferred from a number the engine never explains.</summary>
        public uint ToSection;

        public readonly List<float> Stops = new();
        public long SeenAt;
        public bool Live;
    }

    // Keyed on where the shaft stands, so a lift seen twice is one lift. The key is coarse on
    // purpose: a lift's reported position is the car's, and the car moves within its shaft.
    private static readonly Dictionary<string, Shaft> Found = new(StringComparer.Ordinal);
    private static readonly List<Shaft> Ordered = new();
    private static long _nextScan;
    private static long _nextDump;
    private static bool _loaded;
    private static int _inScene = -1;
    private static string _lastLogged;

    /// <summary>How many lift components the scene reported on the last sweep. -1 until the
    /// first sweep. This is the one number that answers "is the lift here at all".</summary>
    internal static int InScene => _inScene;

    // ---------- the sweep ----------

    /// <summary>One sweep, at most every <see cref="RescanMs"/>. Called from the beacon's tick
    /// so a lift is already known before the picker is opened.</summary>
    internal static void Pump()
    {
        long now = Environment.TickCount64;
        if (now < _nextScan) return;
        _nextScan = now + RescanMs;
        Scan(now);
    }

    /// <summary>A sweep now, whatever the clock says: the picker is being opened and the owner's
    /// rule is that opening the list shows what is really there.</summary>
    internal static void Sweep() => Scan(Environment.TickCount64);

    private static void Scan(long now)
    {
        Load();
        uint city = WorldTourStateService.CityId;

        foreach (var shaft in Ordered) shaft.Live = false;

        int seen = 0;
        foreach (string type in Types)
        {
            ManagedObject list = null;
            try { list = FieldProbeService.SceneComponents(type); }
            catch { list = null; }
            int count = FlowHelper.GetListCount(list);
            seen += count;

            int n = Math.Min(count, MaxShafts);
            for (int i = 0; i < n; i++)
            {
                ManagedObject om = null;
                try { om = FlowHelper.GetListItem(list, i); }
                catch { }
                if (om == null) continue;

                var (x, y, z, ok) = AvatarFieldReader.ReadWorldPos(om);
                if (!ok || (x == 0f && y == 0f && z == 0f)) continue;

                var shaft = Take(city, x, z);
                shaft.City = city;
                shaft.X = x; shaft.Y = y; shaft.Z = z;
                shaft.SeenAt = now;
                shaft.Live = true;
                shaft.Type = type == SCENE_LIFT_TYPE ? 1 : 0;

                if (type == SCENE_LIFT_TYPE)
                {
                    shaft.State = FlowHelper.ResolveEnumName(SCENE_LIFT_TYPE + ".State",
                                      FlowHelper.ReadIntField(om, "CurrentState", -1)) ?? shaft.State;
                    uint scene = FlowHelper.ReadUIntField(om, "SetSceneId", 0);
                    if (scene != 0) shaft.SceneId = scene;
                }
                else
                {
                    shaft.LowY = Float(om, "UnderHeight", shaft.LowY);
                    shaft.HighY = Float(om, "UpHeight", shaft.HighY);
                    shaft.State = FlowHelper.ResolveEnumName(LIFT_TYPE + ".State",
                                      FlowHelper.ReadByteField(om, "CurrentState", -1)) ?? shaft.State;
                    ReadStops(om, shaft);
                }
            }
        }
        _inScene = seen;

        // Nothing is dropped here, on purpose. A lift is a fixed part of a building, not a
        // street object that expires when the sweep looks away; and a section unload must not
        // make the list change under the player's feet. Only the LIVE flag was cleared above.
        Reorder();
        NoteRide(now);
        Log();
        Dump(now);
        Probe(now);
    }

    // ---------- which lift goes where ----------

    /// <summary>How near the player must stand to a shaft for "they were on it" to be true.
    /// Generous enough to cover a platform a couple of metres across.</summary>
    private const float BoardedM = 3f;

    /// <summary>How long a "the player was on that shaft" memory stays good. A section can
    /// change while walking too, so without this the lift would be credited with a ride the
    /// player made on foot.</summary>
    private const long BoardedMs = 45000;

    private static uint _lastSection;
    private static Shaft _boarded;
    private static long _boardedAt;

    /// <summary>Watch what the lift actually does to the player. This is the honest answer to
    /// "which of these takes me to the lab": the engine hands out a <c>SetSceneId</c> number it
    /// never explains, but it cannot lie about the player being on the platform and then being
    /// somewhere else. So the ride is recorded, once, and said out loud from then on.</summary>
    private static void NoteRide(long now)
    {
        try
        {
            var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
            if (!me.ok) return;
            uint city = WorldTourStateService.CityId;
            uint section = WorldTourStateService.CurrentSectionId;

            if (_boarded != null && section != 0 && _lastSection != 0 && section != _lastSection
                && now - _boardedAt <= BoardedMs)
            {
                _boarded.ToSection = section;
                API.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "[SF6Access] Ride: the lift at {0:F1},{1:F1} took the player from section {2} " +
                    "to section {3} ({4})", _boarded.X, _boarded.Z, _lastSection, section,
                    SectionCatalog.NameOf(section) ?? "-"));
                _boarded = null;
                Dump(now + DumpMs);   // written down at once: this is the one fact that is hard to get back
            }
            if (section != 0) _lastSection = section;

            // Which shaft the player is standing on now, if any.
            Shaft on = null;
            float best = BoardedM * BoardedM;
            foreach (var s in Ordered)
            {
                if (s.City != city) continue;
                float dx = s.X - me.x, dz = s.Z - me.z;
                float d = dx * dx + dz * dz;
                if (d <= best) { best = d; on = s; }
            }
            if (on != null) { _boarded = on; _boardedAt = now; }
            _playerX = me.x; _playerY = me.y; _playerZ = me.z; _havePlayer = true;
        }
        catch { }
    }

    private static float _playerX, _playerY, _playerZ;
    private static bool _havePlayer;

    /// <summary>The shaft at this spot, new or remembered. A lift seen on a later visit keeps
    /// the floors it was known to serve rather than starting from nothing.</summary>
    private static Shaft Take(uint city, float x, float z)
    {
        string key = Key(city, x, z);
        if (Found.TryGetValue(key, out var shaft)) return shaft;
        shaft = new Shaft { City = city, X = x, Z = z };
        Found[key] = shaft;
        Ordered.Add(shaft);
        return shaft;
    }

    private static string Key(uint city, float x, float z) =>
        string.Format(CultureInfo.InvariantCulture, "{0}|{1:0}|{2:0}", city, x, z);

    private static void Reorder()
    {
        // Stable: newest first would be meaningless, and the picker sorts by distance anyway.
        // This only exists so the dump file reads in a fixed order.
        Ordered.Sort((a, b) => string.CompareOrdinal(Key(a.City, a.X, a.Z), Key(b.City, b.X, b.Z)));
    }

    // ---------- reading the component ----------

    /// <summary>A float member: the property getter first (these are auto-properties), then the
    /// backing field and the plain field. NaN when the engine will not say.</summary>
    private static float Float(ManagedObject om, string name, float fallback)
    {
        if (om == null) return fallback;
        try
        {
            var v = FlowHelper.Call(om, "get_" + name);
            if (v != null)
            {
                float f = Convert.ToSingle(v);
                if (float.IsFinite(f)) return f;
            }
        }
        catch { }
        return FlowHelper.ReadFloatField(om, name, fallback);
    }

    /// <summary>The floors this lift stops at. Read ONLY when the list's elements are reference
    /// objects: a list of STRUCTS would have to be boxed through <c>get_Item</c>, and that is the
    /// call shape (a member handed back by value) that has taken this game down before — see
    /// docs/sf6-architecture.md. A struct list is skipped rather than risked, and the lift is
    /// still offered from its <c>UnderHeight</c>/<c>UpHeight</c> span.</summary>
    private static void ReadStops(ManagedObject om, Shaft shaft)
    {
        try
        {
            var list = FlowHelper.GetObjectField(om, "StopHeightDataList");
            if (list == null) return;

            var element = list.GetTypeDefinition()?.ElementType;
            if (element == null || element.IsValueType()) return;

            int n = FlowHelper.GetListCount(list);
            if (n <= 0 || n > MaxStops) return;

            shaft.Stops.Clear();
            for (int i = 0; i < n; i++)
            {
                var item = FlowHelper.GetListItem(list, i);
                if (item == null) continue;
                float h = Float(item, "Height", float.NaN);
                if (float.IsFinite(h)) shaft.Stops.Add(h);
            }
            shaft.Stops.Sort();
        }
        catch { }
    }

    // ---------- what the picker is given ----------

    /// <summary>Every lift known in this city, nearest first, as choosable destinations.</summary>
    internal static void AddTo(List<Destination> into, float px, float py, float pz)
    {
        Sweep();
        uint city = WorldTourStateService.CityId;
        var near = new List<Shaft>(Ordered.Count);
        foreach (var s in Ordered)
        {
            if (s.City != city) continue;
            near.Add(s);
        }
        near.Sort((a, b) => FlatSq(a, px, pz).CompareTo(FlatSq(b, px, pz)));

        foreach (var s in near)
            into.Add(new Destination(Name(), Kind(s), DestinationCategory.Lift,
                                     s.X, BoardingY(s, py), s.Z));
    }

    /// <summary><b>The lift's stops as connections a route may pass through — 2026-10-07.</b>
    ///
    /// <para>A lift links its own stops to each other, and nothing else about it matters to a
    /// plan: the player walks onto the landing, stands still, and rides. Each shaft contributes
    /// one link per adjacent pair of stops, exactly as a ladder contributes its two ends. Going
    /// DOWN, this is the connection that was missing: every other way down had a place in the
    /// plan — ladders, stairs, crossings, even a leap off an edge — but the platform the game
    /// actually puts in these buildings did not, so destinations reachable by riding were
    /// reported as unreachable ones.</para>
    ///
    /// <para>Height semantics are checked rather than assumed. <c>StopHeightDataList</c>'s
    /// <c>Height</c> is only used for the picker today, so a stop farther from the shaft than any
    /// real building could be is dropped instead of being turned into a connection — wrong floors
    /// would be worse than none, and the whole lift is skipped rather than half-trusted.</para></summary>
    internal static void AddLinksTo(List<LevelRoutePlanner.Link> into, uint city)
    {
        Sweep();
        foreach (var s in Ordered)
        {
            if (s.City != city) continue;
            // A shaft with any coordinate the world did not give us would put NaN into the
            // planner's spot list, and every distance the planner sorts on would then compare
            // as "equal" — the whole ordering that decides which question is asked next would
            // quietly become arbitrary. Refuse to offer a link rather than let that in.
            if (!float.IsFinite(s.X) || !float.IsFinite(s.Z)) continue;
            var stops = LevelHeights(s);
            if (stops.Count < 2) continue;
            for (int i = 0; i + 1 < stops.Count; i++)
                into.Add(new LevelRoutePlanner.Link(
                    new LevelRoutePlanner.Spot(s.X, stops[i], s.Z),
                    new LevelRoutePlanner.Spot(s.X, stops[i + 1], s.Z),
                    Lift: true));
        }
    }

    /// <summary>The heights this shaft serves, lowest first, as plausible WORLD heights. The stop
    /// list is trusted first, then the declared <c>UnderHeight</c>/<c>UpHeight</c> span; both are
    /// filtered against where the shaft itself was seen, which is real engine geometry.</summary>
    private static List<float> LevelHeights(Shaft s)
    {
        var list = new List<float>();
        if (float.IsFinite(s.Y))
        {
            foreach (float h in s.Stops)
            {
                if (!float.IsFinite(h)) continue;
                if (float.IsFinite(s.LowY) && h < s.LowY - SpanSlackM) continue;
                if (float.IsFinite(s.HighY) && h > s.HighY + SpanSlackM) continue;
                if (list.Count > 0 && h - list[list.Count - 1] < PersonM) continue;  // already have this floor
                list.Add(h);
            }
        }
        if (list.Count >= 2) return list;
        list.Clear();
        if (float.IsFinite(s.LowY)) list.Add(s.LowY);
        if (float.IsFinite(s.HighY)) list.Add(s.HighY);
        if (list.Count == 2 && list[1] < list[0]) list.Reverse();
        return list;
    }

    /// <summary>How far a stop may sit outside the declared span and still be believed.</summary>
    private const float SpanSlackM = 2f;

    /// <summary>Two stops closer than a person is tall are one floor.</summary>
    private const float PersonM = 2f;

    /// <summary>True when a lift is known to stand within <paramref name="m"/> of this spot.
    /// Used so the general scene sweep does not also list the same lift a second time as an
    /// anonymous numbered object.</summary>
    internal static bool HasShaftNear(float x, float z, float m)
    {
        uint city = WorldTourStateService.CityId;
        float mSq = m * m;
        foreach (var s in Ordered)
        {
            if (s.City != city) continue;
            float dx = s.X - x, dz = s.Z - z;
            if (dx * dx + dz * dz <= mSq) return true;
        }
        return false;
    }

    /// <summary>The height to aim at: the floor the lift stops at that is nearest the player's
    /// own height, which is the landing they would walk onto. Failing that, the player's height
    /// inside the shaft's span; failing that, wherever the car was last seen.</summary>
    private static float BoardingY(Shaft s, float py)
    {
        if (s.Stops.Count > 0)
        {
            float best = s.Stops[0], bestD = float.MaxValue;
            foreach (float h in s.Stops)
            {
                float d = Math.Abs(h - py);
                if (d < bestD) { bestD = d; best = h; }
            }
            return best;
        }
        if (float.IsFinite(s.LowY) && float.IsFinite(s.HighY) && s.HighY > s.LowY)
            return Math.Min(s.HighY, Math.Max(s.LowY, py));
        return s.Y;
    }

    private static string Name() => LangFile.Get("wt.dest.lift", "Lift");

    /// <summary>What the game says about where this lift is — never a guess. "3 floors" when the
    /// engine declared its stops; for the scene lift, which floor its car is resting on, or that
    /// it is on the move. Both are things the player can check against what they already
    /// know about the building; a guess is not worth putting in a list someone navigates by.</summary>
    private static string Kind(Shaft s)
    {
        // Where this lift has actually delivered the player beats anything else: it is the
        // reason the player is looking at the list at all, and it is measured, not inferred.
        if (s.ToSection != 0)
        {
            string name = SectionCatalog.NameOf(s.ToSection);
            if (!string.IsNullOrEmpty(name))
                return string.Format(LangFile.Get("wt.dest.lift_to", "took you to {0}"), name);
        }
        if (s.Stops.Count >= 2)
            return string.Format(LangFile.Get("wt.dest.lift_stops", "{0} floors"), s.Stops.Count);
        return s.Type == 1 ? CarWhere(s.State) : null;
    }

    /// <summary>The Om029000 state word, in the player's language. Its enum has nine members —
    /// three resting floors and six in-between moves — and what matters to a player walking
    /// towards it is simply which of those two.</summary>
    private static string CarWhere(string state)
    {
        if (string.IsNullOrEmpty(state)) return null;
        if (state.EndsWith("Move", StringComparison.Ordinal))
            return LangFile.Get("wt.dest.lift_moving", "on the move");
        if (state.IndexOf("Middle", StringComparison.Ordinal) >= 0)
            return LangFile.Get("wt.dest.lift_middle", "at the middle floor");
        if (state.IndexOf("Down", StringComparison.Ordinal) >= 0)
            return LangFile.Get("wt.dest.lift_down", "at the bottom floor");
        if (state.IndexOf("Up", StringComparison.Ordinal) >= 0)
            return LangFile.Get("wt.dest.lift_up", "at the top floor");
        return null;
    }

    private static float FlatSq(Shaft s, float px, float pz)
    {
        float dx = s.X - px, dz = s.Z - pz;
        return dx * dx + dz * dz;
    }

    // ---------- persistence ----------

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            string path = Path.Combine(ObjectDumper.DumpDir, FILE);
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split('\t');
                if (p.Length < 5) continue;
                if (!uint.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint city)) continue;
                if (!float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) continue;
                if (!float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) continue;

                var shaft = Take(city, x, z);
                shaft.City = city;
                shaft.X = x; shaft.Z = z;
                float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out shaft.LowY);
                float.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out shaft.HighY);
                if (p.Length > 5)
                {
                    shaft.Stops.Clear();
                    foreach (var piece in p[5].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        if (float.TryParse(piece, NumberStyles.Float, CultureInfo.InvariantCulture, out float h))
                            shaft.Stops.Add(h);
                    shaft.Stops.Sort();
                }
                if (p.Length > 6 && p[6].Length > 0) shaft.State = p[6];
                if (p.Length > 7) int.TryParse(p[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out shaft.Type);
                if (p.Length > 8) uint.TryParse(p[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out shaft.SceneId);
                if (p.Length > 9) uint.TryParse(p[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out shaft.ToSection);
            }
            Reorder();
        }
        catch { }
    }

    private static void Dump(long now)
    {
        if (now < _nextDump) return;
        _nextDump = now + DumpMs;

        var lines = new List<string>(Ordered.Count + 2);
        lines.Add("# city\tx\tz\tunderHeight\tupHeight\tstopHeights\tstate\ttype (0 platform, 1 scene lift)\tsceneId\ttookPlayerToSection");
        foreach (var s in Ordered)
        {
            var stops = new StringBuilder();
            foreach (float h in s.Stops)
            {
                if (stops.Length > 0) stops.Append(',');
                stops.Append(h.ToString("F1", CultureInfo.InvariantCulture));
            }
            lines.Add(string.Format(CultureInfo.InvariantCulture, "{0}\t{1:F1}\t{2:F1}\t{3:F1}\t{4:F1}\t{5}\t{6}\t{7}\t{8}\t{9}",
                s.City, s.X, s.Z, s.LowY, s.HighY, stops, s.State ?? "-", s.Type, s.SceneId, s.ToSection));
        }
        try
        {
            File.WriteAllLines(Path.Combine(ObjectDumper.DumpDir, FILE), lines, Encoding.UTF8);
        }
        catch { }
    }

    // ---------- the evidence file ----------

    private const string PROBE_FILE = "sf6access_lift_probe.txt";

    /// <summary>Everything the engine will say about each lift, written down on its own.
    ///
    /// <para>The type database only shows three members for <c>Om029000</c> — the rest of the
    /// type fails to load in an offline reader — so the only way to know what a lift really
    /// carries is to ask the live object and print every field it has. That is what this does.
    /// A Guid field is also resolved into the text it points at, which is how a name the game
    /// gives the lift becomes readable.</para>
    ///
    /// <para>Entirely diagnostic and never read back by the mod. It exists so that "which lift
    /// goes to the lab" can be answered from what the game said, on the player's own machine
    /// and in their own language, instead of from a guess about what <c>SetSceneId</c> means.</para>
    /// </summary>
    private static void Probe(long now)
    {
        if (now < _nextProbe) return;
        _nextProbe = now + ProbeMs;

        var sb = new StringBuilder();
        sb.Append("# what the game itself says about every lift in this scene\n");
        sb.Append(string.Format(CultureInfo.InvariantCulture,
            "# city {0}  section {1} ({2})  player {3}\n",
            WorldTourStateService.CityId, WorldTourStateService.CurrentSectionId,
            SectionCatalog.NameOf(WorldTourStateService.CurrentSectionId) ?? "-",
            _havePlayer ? string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F1},{2:F1}",
                                        _playerX, _playerY, _playerZ) : "unreadable"));

        foreach (string type in Types)
        {
            ManagedObject list = null;
            try { list = FieldProbeService.SceneComponents(type); }
            catch { list = null; }
            int n = FlowHelper.GetListCount(list);
            sb.Append(string.Format(CultureInfo.InvariantCulture, "\n=== {0}: {1} in scene ===\n", type, n));

            for (int i = 0; i < Math.Min(n, MaxShafts); i++)
            {
                ManagedObject om = null;
                try { om = FlowHelper.GetListItem(list, i); }
                catch { }
                if (om == null) continue;
                DumpMembers(sb, "  ", om);
            }
        }

        // The other things that can put the player somewhere else. The question was never
        // really "where is the lift" — it was "what takes me to the lab", and a door or a
        // wall switch can hold the same scene id a lift does. So they are asked too, in the
        // same file, while the player is standing next to them.
        foreach (string type in OtherCarriers)
        {
            ManagedObject list = null;
            try { list = FieldProbeService.SceneComponents("app.worldtour.om." + type); }
            catch { list = null; }
            int n = FlowHelper.GetListCount(list);
            sb.Append(string.Format(CultureInfo.InvariantCulture, "\n=== {0}: {1} in scene ===\n", type, n));

            for (int i = 0; i < Math.Min(n, MaxShafts); i++)
            {
                ManagedObject om = null;
                try { om = FlowHelper.GetListItem(list, i); }
                catch { }
                if (om == null) continue;

                var (x, y, z, ok) = AvatarFieldReader.ReadWorldPos(om);
                // Only what is within reach: a door on the far side of the city is noise here.
                if (ok && _havePlayer)
                {
                    float dx = x - _playerX, dz = z - _playerZ;
                    if (dx * dx + dz * dz > NearM * NearM) continue;
                }
                DumpMembers(sb, "  ", om);
            }
        }
        try
        {
            File.WriteAllText(Path.Combine(ObjectDumper.DumpDir, PROBE_FILE), sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    private static long _nextProbe;
    private const long ProbeMs = 20000;

    /// <summary>Every field of one object, one line each. Read-only and per-field guarded: a
    /// field that will not read is a line saying so, not a reason to lose the rest.</summary>
    private static void DumpMembers(StringBuilder sb, string pad, ManagedObject om)
    {
        var (x, y, z, ok) = AvatarFieldReader.ReadWorldPos(om);
        sb.Append(string.Format(CultureInfo.InvariantCulture,
            "{0}object at {1:F1},{2:F1},{3:F1}\n", pad, x, y, z,
            ok ? "" : " (position unreadable)"));
        try
        {
            var td = om.GetTypeDefinition();
            ulong addr = om.GetAddress();
            var fields = td?.GetFields();
            if (fields == null) { sb.Append(pad + "  (no fields readable)\n"); return; }
            foreach (var f in fields)
            {
                string fname = f.Name ?? "";
                try
                {
                    var val = f.GetDataBoxed(typeof(object), addr, false);
                    string text = val == null ? "null" : val.ToString();
                    // A Guid is a pointer at a line of game text; the whole reason this file
                    // exists is to turn pointers like that into words.
                    if (val is REFrameworkNET.ValueType vt)
                    {
                        string resolved = FlowHelper.ResolveGuid(vt);
                        if (!string.IsNullOrEmpty(resolved))
                            text = text + "  -> \"" + resolved + "\"";
                    }
                    sb.Append(pad + "  " + fname + " = " + text + "\n");
                }
                catch (Exception ex) { sb.Append(pad + "  " + fname + " ERROR: " + ex.Message + "\n"); }
            }
        }
        catch (Exception ex) { sb.Append(pad + "  fields ERROR: " + ex.Message + "\n"); }
    }

    /// <summary>One line whenever the answer changes: whether the lift is in this scene at all,
    /// and where the shafts known for this city are. "I cannot find the lift" has to be
    /// answerable from the log without asking the player to read the whole list aloud.</summary>
    private static void Log()
    {
        uint city = WorldTourStateService.CityId;
        int live = 0, here = 0;
        foreach (var s in Ordered)
        {
            if (s.City != city) continue;
            here++;
            if (s.Live) live++;
        }
        string line = string.Format(CultureInfo.InvariantCulture,
            "[SF6Access] Lifts: {0} in this scene, {1} known for city {2}", _inScene, live, city);
        if (line == _lastLogged) return;
        _lastLogged = line;
        API.LogInfo(line);

        foreach (var s in Ordered)
        {
            if (s.City != city) continue;
            API.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[SF6Access] Lift at {0:F1},{1:F1} height {2:F1} - {3}, {4} stops, span {5:F1}..{6:F1}, state {7}, scene {8}{9}",
                s.X, s.Z, s.Y, s.Type == 1 ? SCENE_LIFT_TYPE : LIFT_TYPE, s.Stops.Count,
                s.LowY, s.HighY, s.State ?? "-", s.SceneId,
                s.ToSection != 0
                    ? string.Format(CultureInfo.InvariantCulture, ", took player to section {0} ({1})",
                                    s.ToSection, SectionCatalog.NameOf(s.ToSection) ?? "-")
                    : ""));
        }
    }

    /// <summary>Another field: write down what we know before the in-memory set is cleared.</summary>
    internal static void Reset()
    {
        Dump(Environment.TickCount64 + DumpMs);
        Found.Clear();
        Ordered.Clear();
        _loaded = false;
        _inScene = -1;
        _lastLogged = null;
        _nextScan = 0;
        _nextProbe = 0;
        _lastSection = 0;
        _boarded = null;
        _boardedAt = 0;
        _havePlayer = false;
    }
}
