using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>The grand tour (T): every place the phone map has ever shown, one stop at a time.</b>
///
/// <para><b>Why this exists (owner, 2026-10-04).</b> Every authoritative route source has
/// been tried and none is complete: the mission arrow renders screen icons, the phone map
/// holds no routes, the navmesh refuses cross-level, the NPC route network warps between
/// levels. The owner's answer was the simple one: 让导航带我走过这张地图的所有地方，带我去
/// 交互这张地图里的所有交互物，这样就一定能找到目标. A graph the player has actually walked
/// cannot lie the way read data can — the principle <see cref="DistrictMemory"/> already
/// stands on, promoted from a by-product to the strategy.</para>
///
/// <para><b>What a stop is.</b> The checklist is <see cref="MapIconSnapshot"/> — every icon
/// the phone map has shown (fast travel, shops, jobs, missions, masters, pins), already
/// persisted per city on disk — PLUS every interactable the scene sweep has found, nearest
/// first, whether the game named it or not: the anonymous ones under the number
/// <see cref="InteractableIds"/> gives each object for good. The owner's call, 2026-10-04
/// ("全部加入"): the tour promised to interact with 这张地图里的所有交互物, and offering only
/// the handful of objects the game publishes a name for would not have been that. Places and
/// interactables are ONE pool now, so "the nearest thing not yet done" is simply the next
/// stop.</para>
///
/// <para><b>How it drives.</b> It never walks anyone anywhere itself. It writes the next
/// stop into <see cref="MapSelectionMemory"/> with the same call the destination picker's
/// Choose makes, and the whole existing guidance pipeline — ladder search, relay, measured
/// round, drop edge, step-by-step words, the homing cue — takes over unchanged. Arrivals
/// and "this stop cannot be reached" verdicts are observed from the resolver the beacon
/// already runs (<see cref="Observe"/>): one observer, no second route planner, no new
/// engine calls.</para>
///
/// <para><b>What it promises.</b> A stop the walk cannot reach is deferred and the tour
/// moves on — every failure is a discovered hole, nothing blocks, nothing goes quiet.
/// Progress is persisted to <c>reframework/data/sf6access_tour.txt</c>, so the tour
/// resumes across sessions. When only deferred places remain they are retried once per
/// tour, from wherever the player stands then; what still fails is named in the
/// completion line and kept for the next session.</para>
///
/// <para><b>The owner's choices (2026-10-04).</b> T toggles; Shift+T goes to the next stop
/// now; every arrival WAITS for that key, because a stop is where interacting happens;
/// and the tour does NOT yield to mission targets — missions are icons in the checklist
/// too, and the selection the tour holds is exactly what keeps the mission beacon from
/// taking the voice mid-tour.</para>
/// </summary>
internal static class TourService
{
    private const string FILE = "sf6access_tour.txt";

    /// <summary>Arrival is the mod's own definition of it — the same band the beacon and
    /// the walk steps use — so "the tour says you are there" never disagrees with "the
    /// guide says you are there". An icon's GlobalPos is the icon, not the door; if
    /// testing shows stops never triggering, THIS is the number to loosen, here only.</summary>
    private static float ArriveM => Hooks.WorldTour.MissionBeaconHooks.ARRIVED_M;

    /// <summary>Level tolerance of an arrival: a storey is not a visit.</summary>
    private const float ARRIVE_DY_M = 2f;

    /// <summary>How long the walk must be OVER — <see cref="RouteNotice.AtEndOfWalk"/>,
    /// after every planner has said its piece — before the stop is deferred. Long enough
    /// that a ladder search (up to half a minute) or a relay sweep never counts as stuck;
    /// short enough that standing at the end of a walk is not a dead end either.</summary>
    private const long STUCK_MS = 25000;

    private const byte DONE = 1, DEFERRED = 2;

    private sealed class Stop
    {
        public byte Status;
        public string Name;
        public float X, Z;
    }

    // city -> stop key -> what happened there. A key is stable across sessions: a place's
    // key is its icon group, its position to 4 metres and its name — the same identity the
    // map snapshot itself deduplicates on.
    private static readonly Dictionary<uint, Dictionary<string, Stop>> _byCity = new();
    private static bool _loaded;
    private static long _savedAt;

    private static bool _on, _waiting, _retryRound;
    private static uint _city;
    private static string _key, _name, _banKey;
    private static float _x, _y, _z;
    private static long _stuckSince;
    private static readonly HashSet<string> _retried = new();

    internal static bool On => _on;

    /// <summary>One line of tour wording, for the hook that owns the key.</summary>
    internal static string Text(string key, string fallback) => Get(key, fallback);

    /// <summary>T pressed: start the tour, or pause it. Progress is never lost — the
    /// status file is the memory, the toggle is only a switch.</summary>
    internal static void Toggle()
    {
        if (_on)
        {
            _on = false;
            // The selection the tour set stays: it is a real place, following it harms
            // nobody, and "do not yield to missions" means the tour never clears it for
            // the mission beacon either.
            Speak(Get("off", "The grand tour is paused, progress saved. Press T to continue"));
            return;
        }

        var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        if (!me.ok)
        {
            Speak(Get("nopos", "Cannot read where you are standing - press T again in a moment"));
            return;
        }
        uint city = WorldTourStateService.CityId;
        if (city != 0) _city = city;
        Load();
        var candidates = new List<SceneTargetCatalog.Named>();
        try { SceneTargetCatalog.CollectAll(candidates, me.x, me.z, SceneTargetCatalog.RangeM, 0f); }
        catch { }
        if (MapIconSnapshot.For(_city).Count == 0 && candidates.Count == 0)
        {
            // Nothing to tour YET: the places grow by opening the phone map, the interactables
            // by walking the field, and saying so beats touring an empty list in silence.
            Speak(Get("empty", "The grand tour has no places yet: open the phone map once, then press T again"));
            return;
        }

        _on = true;
        _waiting = false; _stuckSince = 0; _banKey = null;
        _retryRound = false; _retried.Clear(); _key = null;
        string prefix = string.Format(Get("on",
            "The grand tour is on: {0} places on the list, {1} already visited"),
            MapIconSnapshot.For(_city).Count + candidates.Count, DoneCount());
        string line = PickLine(me.x, me.y, me.z, prefix);
        if (_key == null) _on = false;   // everything already visited: the line says so
        Speak(line);
    }

    /// <summary>Shift+T pressed: the next stop now. A stop left before arrival comes back
    /// later (deferred) — it is never counted visited on the owner's say-so alone.</summary>
    internal static void Skip()
    {
        if (!_on) return;
        string prefix = null;
        if (_key != null && !_waiting)
        {
            SetStatus(_key, _name, _x, _z, DEFERRED);
            // Banned from the very next pick only: skipping a place and being sent
            // straight back to it is the one way this key could feel like a no-op.
            _banKey = _key;
            prefix = string.Format(Get("skipped", "Skipping {0} for now"), _name);
        }
        _key = null; _waiting = false; _stuckSince = 0;
        var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        if (!me.ok)
        {
            Speak(prefix ?? Get("nopos", "Cannot read where you are standing - press T again in a moment"));
            return;
        }
        string line = PickLine(me.x, me.y, me.z, prefix);
        if (_key == null) _on = false;
        Speak(line);
    }

    /// <summary>The tour's heartbeat between stops: city changes and the pick that follows
    /// them. Called every field frame; costs nothing while a stop is held.</summary>
    internal static void Pump()
    {
        if (!_on) return;
        uint city = WorldTourStateService.CityId;
        if (city == 0) return;
        if (city != _city)
        {
            // Fast travel, another city: the old stop's coordinates mean nothing here.
            // The status file is keyed by city, so nothing is lost — only this leg ends.
            _city = city;
            _key = null; _waiting = false; _stuckSince = 0; _banKey = null;
            _retryRound = false; _retried.Clear();
        }
        if (_key != null) return;
        var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        if (!me.ok) return;
        string line = PickLine(me.x, me.y, me.z, null);
        if (_key == null) _on = false;
        Speak(line);
    }

    /// <summary>Arrivals and stuck verdicts, decided from the destination the beacon's
    /// resolver already holds. True when the tour spoke, so the resolver can yield the
    /// tick exactly the way its own speech sites do.</summary>
    internal static bool Observe(in BeaconDestination dest, float px, float py, float pz, long now)
    {
        if (!_on || _key == null || _waiting) return false;

        // The player picked somewhere of their own in the destination picker: the tour
        // stands down until they come back to it (Shift+T re-anchors). Fighting their
        // hands is the one thing this codebase must never do — the auto-walk rule.
        var sel = MapSelectionMemory.Current;
        if (!sel.Ok || sel.Label != _name || sel.X != _x || sel.Z != _z)
        {
            _stuckSince = 0;
            return false;
        }

        // ARRIVED — the mod's own band, so the tour and the guide never disagree.
        float dx = _x - px, dz = _z - pz;
        if (MathF.Sqrt(dx * dx + dz * dz) <= ArriveM && MathF.Abs(_y - py) <= ARRIVE_DY_M)
        {
            SetStatus(_key, _name, _x, _z, DONE);
            _waiting = true;
            _stuckSince = 0;
            return Speak(string.Format(Get("arrived",
                "Arrived at {0}. That is visit {1}, {2} places left. Press Shift+T for the next one"),
                _name, DoneCount(), LeftCount(px, pz)));
        }

        // STUCK — the walk is over (no route, no ladder, no relay, no measured way round)
        // and has stayed over. The beacon has been saying the end-of-walk hint all this
        // time; the tour's job is to make that failure cost nothing: defer, speak, move on.
        if (RouteNotice.AtEndOfWalk(dest))
        {
            if (_stuckSince == 0) _stuckSince = now;
            if (now - _stuckSince >= STUCK_MS)
            {
                SetStatus(_key, _name, _x, _z, DEFERRED);
                _banKey = _key;
                string prefix = string.Format(Get("deferred",
                    "{0} cannot be reached right now - skipped, to be tried again later"), _name);
                _key = null; _waiting = false; _stuckSince = 0;
                return Speak(PickLine(px, py, pz, prefix));
            }
        }
        else _stuckSince = 0;
        return false;
    }

    // ------------------------------------------------------------------ the pick

    /// <summary>Chooses the next stop and speaks nothing itself — the line comes back for
    /// the caller to say, so a verdict and the stop that follows it are ONE utterance
    /// rather than two lines cutting each other off.</summary>
    private static string PickLine(float px, float py, float pz, string prefix)
    {
        Load();
        var places = MapIconSnapshot.For(_city);

        // 1. Every interactable in the field, nearest first — named ones under their own name
        //    and the rest under the number each keeps for good, because the tour's promise is
        //    to touch them ALL (owner, 2026-10-04: 全部加入), not merely the ones the game
        //    bothered to name. It is one pool with the places, so nothing "jumps the queue"
        //    any more: whatever is nearest and not yet done is next.
        var near = new List<SceneTargetCatalog.Named>();
        try { SceneTargetCatalog.CollectAll(near, px, pz, SceneTargetCatalog.RangeM, ArriveM); }
        catch { }
        foreach (var n in near)
        {
            if (string.IsNullOrWhiteSpace(n.Name)) continue;
            string k = TargetKey(n.Name, n.X, n.Z);
            if (!Eligible(k, n.X, n.Z, false)) continue;
            return Go(k, n.Name, n.X, n.Y, n.Z, px, pz, prefix, true);
        }

        // 2. The nearest map place not yet visited.
        PlaceFile.Entry best = default;
        string bestKey = null;
        float bestSq = float.MaxValue;
        foreach (var e in places)
        {
            if (string.IsNullOrWhiteSpace(e.Name)) continue;
            string k = PlaceKey(e);
            if (!Eligible(k, e.X, e.Z, true)) continue;
            float dx = e.X - px, dz = e.Z - pz, sq = dx * dx + dz * dz;
            if (sq >= bestSq) continue;
            best = e; bestKey = k; bestSq = sq;
        }
        if (bestKey != null)
            return Go(bestKey, best.Name, best.X, best.Y, best.Z, px, pz, prefix, false);

        // 3. Nothing fresh left, and deferred places remain: one more round, from wherever
        // the player stands NOW. What fails twice is left for the next session — a tour
        // that ping-pongs between two unreachable stops is not a tour.
        if (!_retryRound && AnyDeferred())
        {
            _retryRound = true;
            _retried.Clear();
            string head = string.Format(Get("retry_round",
                "The {0} remaining places all failed before: trying them again from somewhere else"),
                DeferredCount());
            string line = PickLine(px, py, pz, null);
            return _key != null ? head + " " + line : head;
        }

        // 4. The tour is over.
        _key = null; _waiting = false; _banKey = null;
        int done = DoneCount(), deferred = DeferredCount();
        return deferred > 0
            ? string.Format(Get("done_partial",
                "The grand tour is complete: {0} visited, {1} would not open. They are on the list for next time"),
                done, deferred)
            : string.Format(Get("done", "The grand tour is complete: all {0} places visited"),
                places.Count);
    }

    /// <summary>Eligibility of one candidate: not visited, not the one just skipped or
    /// failed, and not deferred — unless this is the retry round, and then not already
    /// retried either.
    ///
    /// <para><paramref name="positional"/> is about DUPLICATES: two map icons three metres
    /// apart are usually one doorway, so arriving at either is arriving at it. Two interactables
    /// three metres apart are two machines, so for them the key alone decides — otherwise
    /// touching one would silently mark its neighbour as done, which is exactly what the tour
    /// must never do.</para></summary>
    private static bool Eligible(string key, float x, float z, bool positional)
    {
        if (Visited(key, x, z, positional)) return false;
        if (key == _banKey) return false;
        if (_retryRound && _retried.Contains(key)) return false;
        if (!_retryRound && Deferred(key)) return false;
        return true;
    }

    private static string Go(string key, string name, float x, float y, float z, float px, float pz,
                             string prefix, bool onTheWay)
    {
        _key = key; _name = name; _x = x; _y = y; _z = z;
        _waiting = false; _stuckSince = 0; _banKey = null;
        if (_retryRound) _retried.Add(key);

        // The same call the destination picker's Choose makes. From here the beacon, the
        // ladder search, the relay, the measured round and the step-by-step words own the
        // walk; this class never moves anyone anywhere itself.
        MapSelectionMemory.Remember(name, x, y, z);

        float dx = x - px, dz = z - pz;
        int meters = Math.Max(1, (int)MathF.Round(MathF.Sqrt(dx * dx + dz * dz)));
        string line = string.Format(Get(onTheWay ? "next_ia" : "next",
            onTheWay ? "Stopping at {0} on the way, {1} meters"
                     : "Going to {0}, {1} meters. {2} places left"),
            name, meters, LeftCount(px, pz));
        API.LogInfo(string.Format(CultureInfo.InvariantCulture,
            "[SF6Access] Tour: next stop '{0}' at ({1:0.0}, {2:0.0}, {3:0.0}), {4} places left",
            name, x, y, z, LeftCount(px, pz)));
        return prefix != null ? prefix + " " + line : line;
    }

    // ------------------------------------------------------------------ status

    private static string PlaceKey(in PlaceFile.Entry e) =>
        string.Format(CultureInfo.InvariantCulture, "p{0}|{1}|{2}|{3}",
                      e.Group, (int)(e.X / 4), (int)(e.Z / 4), e.Name);

    private static string TargetKey(string name, float x, float z) =>
        string.Format(CultureInfo.InvariantCulture, "i{0}|{1}|{2}",
                      (int)(x / 4), (int)(z / 4), name);

    private static Dictionary<string, Stop> Stops()
    {
        Load();
        return _byCity.TryGetValue(_city, out var map) ? map : null;
    }

    private static bool Visited(string key, float x, float z, bool positional)
    {
        var map = Stops();
        if (map == null) return false;
        if (map.TryGetValue(key, out var s) && s.Status == DONE) return true;
        if (!positional) return false;
        // Two icons for one doorway (the same shop read a few metres apart) are one
        // place: arriving at either is arriving at it. Without this, the duplicate the
        // map kept becomes a stop that is "arrived" the moment it is picked.
        foreach (var v in map.Values)
        {
            if (v.Status != DONE) continue;
            float dx = v.X - x, dz = v.Z - z;
            if (dx * dx + dz * dz <= ArriveM * ArriveM) return true;
        }
        return false;
    }

    private static bool Deferred(string key)
    {
        var map = Stops();
        return map != null && map.TryGetValue(key, out var s) && s.Status == DEFERRED;
    }

    private static void SetStatus(string key, string name, float x, float z, byte status)
    {
        Load();
        if (!_byCity.TryGetValue(_city, out var map)) _byCity[_city] = map = new Dictionary<string, Stop>();
        if (map.TryGetValue(key, out var s)) { s.Status = status; s.Name = name; s.X = x; s.Z = z; }
        else map[key] = new Stop { Status = status, Name = name, X = x, Z = z };
        Save();
    }

    /// <summary>Stops actually ticked off, of every kind: "visit 12" has to count the machines
    /// too, not only the places the phone map drew.</summary>
    private static int DoneCount()
    {
        int n = 0;
        var map = Stops();
        if (map != null)
            foreach (var s in map.Values)
                if (s.Status == DONE) n++;
        return n;
    }

    /// <summary>Still to do from where the player stands: the map's places PLUS every
    /// interactable the field has shown so far, less what is already ticked off. The
    /// interactable half grows as they walk, so this is honestly "not done among what we
    /// know" — never a promise about objects nobody has seen yet.</summary>
    private static int LeftCount(float px, float pz)
    {
        int left = 0;
        foreach (var e in MapIconSnapshot.For(_city))
            if (!Visited(PlaceKey(e), e.X, e.Z, true)) left++;
        var stops = new List<SceneTargetCatalog.Named>();
        try { SceneTargetCatalog.CollectAll(stops, px, pz, SceneTargetCatalog.RangeM, 0f); }
        catch { }
        foreach (var s in stops)
        {
            if (string.IsNullOrWhiteSpace(s.Name)) continue;
            if (!Visited(TargetKey(s.Name, s.X, s.Z), s.X, s.Z, false)) left++;
        }
        return left;
    }

    private static int DeferredCount()
    {
        int n = 0;
        var map = Stops();
        if (map != null)
            foreach (var s in map.Values)
                if (s.Status == DEFERRED) n++;
        return n;
    }

    private static bool AnyDeferred() => DeferredCount() > 0;

    // ------------------------------------------------------------------ the file

    private static string FilePath => Path.Combine(ObjectDumper.DumpDir, FILE);

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var inv = CultureInfo.InvariantCulture;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] f = line.Split('\t');
                if (f.Length < 4) continue;
                if (!uint.TryParse(f[0], NumberStyles.Integer, inv, out uint city)) continue;
                if (!byte.TryParse(f[1], NumberStyles.Integer, inv, out byte status)) continue;
                if (status != DONE && status != DEFERRED) continue;
                if (f[2].Length == 0) continue;
                float.TryParse(f.Length > 4 ? f[4] : "0", NumberStyles.Float, inv, out float x);
                float.TryParse(f.Length > 5 ? f[5] : "0", NumberStyles.Float, inv, out float z);
                if (!_byCity.TryGetValue(city, out var map)) _byCity[city] = map = new Dictionary<string, Stop>();
                map[f[2]] = new Stop { Status = status, Name = f[3], X = x, Z = z };
            }
            int stops = 0;
            foreach (var m in _byCity.Values) stops += m.Count;
            API.LogInfo($"[SF6Access] Tour: {stops} stop statuses loaded from {FILE}");
        }
        catch { }
    }

    /// <summary>Written only when a stop's status changes — a handful of times per
    /// session, never per frame. The same discipline DistrictMemory keeps.</summary>
    private static void Save()
    {
        long now = Environment.TickCount64;
        if (now - _savedAt < 1000) return;
        _savedAt = now;
        try
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("# city\tstatus (1 visited, 2 deferred)\tkey\tname\tx\tz");
            foreach (var (city, map) in _byCity)
                foreach (var (key, s) in map)
                    sb.AppendLine(string.Join("\t", city.ToString(inv), s.Status.ToString(inv),
                        key, s.Name ?? "-", s.X.ToString("F2", inv), s.Z.ToString("F2", inv)));
            File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    private static bool Speak(string line)
    {
        ScreenReaderService.Speak(line, interrupt: true);
        API.LogInfo("[SF6Access] Tour: " + line);
        return true;
    }

    private static string Get(string key, string fallback) => LangFile.Get("wt.tour." + key, fallback);
}
