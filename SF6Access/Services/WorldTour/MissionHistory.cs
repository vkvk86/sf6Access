#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>EVERY mission objective the player has ever been sent to — not just the ones this mod
/// happened to watch.</b>
///
/// <para><b>Why "not just the ones we watched" is the whole design.</b> A first version recorded
/// an objective the moment the beacon resolved it. That is worthless for the player's actual
/// request: the objectives they want back are the ones from BEFORE the feature existed, and a
/// recorder that only starts when the feature ships can never recover those. The story's own
/// record has to be the source, and it is — see <see cref="Backfill"/>.</para>
///
/// <para><b>What the game gives us, and what it does not.</b>
/// <c>WTMissionSystem.mListMissionDevice</c> is a plain <c>List&lt;WTMissionDeviceInfo&gt;</c>,
/// and each entry answers <c>GetTitleMessage()</c>, <c>GetChapterNo()</c>, <c>GetCityNo()</c>,
/// <c>GetProgressRate()</c>, <c>IsCleared()</c>, <c>IsAccepted()</c> — i.e. the mission's NAME and
/// its place in the story are available for every mission the save file knows about, in any city,
/// with no position needed.</para>
///
/// <para><b>The honest limit.</b> A mission's TARGET COORDINATES exist only while that mission's
/// object is streamed into the loaded scene — the mission system's own note says so. A mission
/// finished last week has no live object to read a position from, so the world position for most
/// of the history genuinely cannot be recovered; the game does not keep it. What CAN always be
/// recovered is the NAME, the chapter and the progress, and that is what this list stores for
/// them. Entries that DO carry a position (recorded live while being followed) keep it and can be
/// guided to; entries that do not still tell the player what the story has been through, in
/// order, which is what they asked for.</para>
/// </summary>
public static class MissionHistory
{
    private const string FILE = "sf6access_mission_history.txt";
    private const string MISSION_SYSTEM = "app.worldtour.WTMissionSystem";
    private const string MISSION_DEVICE_LIST = "mListMissionDevice";

    /// <summary>One recorded objective.
    ///
    /// <para><see cref="Key"/> is the identity: the mission system's own slot when we watched it
    /// being followed, or <c>mission:{id}</c> when the entry came from the story's own list.
    /// <see cref="HasPlace"/> is false for an entry whose coordinates the game no longer has —
    /// such an entry is still listed and spoken, but it cannot be chosen, because pointing a
    /// blind player at (0,0,0) is worse than saying the place is no longer known.</para></summary>
    public readonly record struct Item(uint City, string Key, string Name, int Chapter, int Progress,
                                       bool Cleared, bool HasPlace, float X, float Y, float Z, int Order);

    private static readonly List<Item> _all = new();
    private static bool _loaded;
    private static long _savedAt;
    private static long _backfilledAt;
    private static uint _backfilledCity;

    /// <summary>Every recorded objective for a city, in story order, oldest first.</summary>
    public static IEnumerable<Item> For(uint city) => Sorted(city);

    private static List<Item> Sorted(uint? city)
    {
        Load();
        // Stable sort by order: entries the story list refreshed in place keep the position they
        // were first given, and a tie falls back to the file's own order, so the list never
        // reshuffles between sessions.
        var mine = new List<Item>();
        foreach (var e in _all)
            if (city == null || e.City == city.Value) mine.Add(e);
        mine.Sort((a, b) => a.Order.CompareTo(b.Order));
        return mine;
    }

    /// <summary>
    /// <b>What the list shows: this city's history — or, when this city has none, the WHOLE
    /// history.</b>
    ///
    /// <para>The owner's standing requirement is that the earlier objectives are there whenever
    /// they open the game. Filtering strictly by city breaks that the moment the city id is not
    /// the one the entries were recorded under: travelling, or a save that loads somewhere else,
    /// and the list comes back empty — indistinguishable, to a player who cannot see the screen,
    /// from the history having been lost. A list that empties itself is the worse failure, so the
    /// filter is a preference and the whole record is the fallback. Entries from another city are
    /// listed but not walked to (<see cref="DestinationAction.OtherCity"/>).</para>
    /// </summary>
    public static IEnumerable<Item> Visible(uint city)
    {
        var mine = Sorted(city);
        return mine.Count > 0 ? mine : Sorted(null);
    }

    /// <summary>
    /// <b>Fold the story's own mission list into the history.</b> Runs at most once per city per
    /// minute, because it walks every mission the save knows and every one of them is a getter
    /// call.
    ///
    /// <para>Entries already recorded keep their position and are updated in place (a mission that
    /// has since been cleared says so); entries never seen are appended in story order, chapter
    /// first. That is what makes the list complete rather than merely current — the missions
    /// finished before this mod existed arrive here, from the game's own record, the first time
    /// the city loads.</para>
    /// </summary>
    public static void Backfill()
    {
        long now = System.Environment.TickCount64;
        uint city = WorldTourStateService.CityId;
        if (city == 0) return;
        if (city == _backfilledCity && now - _backfilledAt < 60000) return;
        _backfilledCity = city;
        _backfilledAt = now;

        Load();
        var sys = API.GetManagedSingleton(MISSION_SYSTEM) as ManagedObject;
        if (sys == null)
        {
            API.LogInfo("[SF6Access] Mission history: backfill found no WTMissionSystem");
            return;
        }

        // WHAT THE PLAYER ACTUALLY HAS, not what a device screen is showing — and, just as
        // importantly, in a form that can actually be indexed. Two false starts are recorded here
        // because the in-game probe (sf6access_missionhistory_probe.txt, 2026-10-06) is what
        // settled it:
        //
        //   1. `mListMissionDevice` resolves but is EMPTY in the city — it is the phone device
        //      screen's own list, filled only while that screen is up.
        //   2. `GetDicAcceptedMissionValues()` / `GetDicDeployedMissionValues()` report counts of 6
        //      and 26 but yield NOTHING when indexed: they return a Dictionary's ValueCollection,
        //      which has a Count and no indexer. The backfill logged "walked 0 mission records
        //      (accepted=6, deployed=26)" for exactly that reason.
        //
        // `GetDeployedMissionInfoList()` and `GetAcceptedMissionInfoListExcludeClear()` are real
        // lists — the probe successfully read an item out of both — so those are what is walked.
        var accepted = FlowHelper.Call(sys, "GetAcceptedMissionInfoListExcludeClear") as ManagedObject
                       ?? FlowHelper.Call(sys, "GetDicAcceptedMissionValues") as ManagedObject;
        var deployed = FlowHelper.Call(sys, "GetDeployedMissionInfoList") as ManagedObject
                       ?? FlowHelper.Call(sys, "GetDicDeployedMissionValues") as ManagedObject;
        int na = FlowHelper.GetListCount(accepted), nd = FlowHelper.GetListCount(deployed);
        if (na <= 0 && nd <= 0)
        {
            API.LogInfo("[SF6Access] Mission history: backfill found no accepted or deployed missions " +
                        $"(accepted={na}, deployed={nd}); nothing to add this run");
            DumpMissionSystemOnce(sys);
            return;
        }

        int added = 0, updated = 0, seen = 0;
        var samples = new System.Text.StringBuilder();
        foreach (var list in new[] { accepted, deployed })
        {
            int n = FlowHelper.GetListCount(list);
            for (int i = 0; i < n && i < MAX_MISSIONS_READ; i++)
            {
                try
                {
                    var proc = FlowHelper.GetListItem(list, i);
                    if (proc == null) continue;
                    seen++;
                    // A process record is a live mission the story has handed over. Its id and its
                    // name come off the player-data payload it carries (see MissionProcess).
                    var info = MissionProcess(proc, out uint id, out string name, out int chapter, out int progress);
                    // The first few say what was actually read, so "added 0" is never a bare
                    // number again: an id of 0 means the payload was not found, and an empty name
                    // with a good id means only the title lookup failed.
                    if (samples.Length < 600)
                        samples.Append($" [id={id} name='{Describe(name)}' ch={chapter} pr={progress}" +
                                       (info == null ? " no-payload]" : "]"));
                    if (id == 0) continue;

                    string key = $"mission:{id}";
                    int existing = _all.FindIndex(e => e.Key == key);
                    if (existing >= 0)
                    {
                        var old = _all[existing];
                        // Position and identity are kept; only the story's own state is refreshed.
                        // A field that reads as 0 this time is "not read", not "became zero" — the
                        // chapter an entry already has is better than the blank that replaced it.
                        _all[existing] = old with
                        {
                            Name = name.Length > 0 ? name : old.Name,
                            Chapter = chapter > 0 ? chapter : old.Chapter,
                            Progress = progress > 0 ? progress : old.Progress,
                        };
                        updated++;
                        continue;
                    }

                    // A mission's city is not published on the process record either, and the mission
                    // being walked is one the player is standing in — so the current city is the read,
                    // and a mission from another city is learned properly the first time it is followed
                    // there (see Record).
                    _all.Add(new Item(city, key, name, chapter, progress, false,
                                      HasPlace: false, 0f, 0f, 0f, NextOrder(city)));
                    added++;
                }
                catch { }
            }
        }
        Save(force: true);
        API.LogInfo($"[SF6Access] Mission history: backfill walked {seen} mission records " +
                    $"(accepted={na}, deployed={nd}), added {added}, refreshed {updated}, {_all.Count} stored" +
                    samples);
        // Walking the list and getting nothing out of it is the failure worth dumping: the records
        // are there, so what is wrong is the shape of what is inside them. The probe writes once
        // per session and only then, so it costs nothing on a working day.
        if (added + updated == 0) DumpMissionSystemOnce(sys);
    }

    /// <summary>
    /// <b>Pull a mission's identity and title off one live process record.</b>
    ///
    /// <para><b>Nothing here is read by a name anyone guessed.</b> That is the whole point. The
    /// first version asked for <c>mProcessData</c> with <c>AvatarFieldReader.GetProp</c>, which
    /// walks properties and getters — and <c>mProcessData</c> is a FIELD. Every record therefore
    /// came back with no payload, no id, and was skipped, which is why the backfill kept reporting
    /// "added 0" while sitting on a list it had indexed perfectly well. It then tried to read the
    /// id by CALLING a method called <c>mMissionId</c>, which is not a method either.</para>
    ///
    /// <para>So the payload is found by looking through the type's own field list, and the id,
    /// the chapter and the progress are found by SHAPE: an integer field whose name mentions what
    /// is being looked for. A game patch that renames a field stops costing the entire feature.
    /// The title is asked for the same way the phone's own mission list asks for it.</para>
    ///
    /// <para>Every step is guarded: this runs on a keypress, and one throwing call must not take
    /// the destination list down with it.</para>
    /// </summary>
    private static ManagedObject? MissionProcess(ManagedObject proc, out uint id, out string name, out int chapter, out int progress)
    {
        id = 0; name = ""; chapter = 0; progress = 0;
        if (proc == null) return null;

        var data = ObjField(proc, "mProcessData", "ProcessData", "mMissionData");

        id = FindUInt(data, "MissionId", "MissionNo", "MissionID", "MissionIndex");
        if (id == 0) id = FindUInt(proc, "MissionId", "MissionNo", "MissionID");
        if (id == 0) id = ReadUInt(proc, "GetMissionId");
        if (id == 0 && data != null) id = FindUInt(data, "Id");

        chapter = (int)FindUInt(data, "Chapter", "ChapterNo");
        progress = (int)FindUInt(data, "Progress", "ProgressRate");

        name = Clean(FindTitle(data) ?? "");
        if (name.Length == 0) name = Clean(FindTitle(proc) ?? "");
        // Last resort that still beats a blank row: the mission's own title, looked up by id
        // through the device list (which IS populated once the phone screen has been opened).
        if (name.Length == 0 && id != 0) name = Clean(MissionTargetService.MissionTitle(id));
        return data;
    }

    /// <summary>A nested object of <paramref name="obj"/>, found by name among its fields.
    /// <c>GetObjectField</c> is tried first (it understands backing fields and accessors); the
    /// field list is the fallback, because a plain field with no getter is exactly what
    /// <c>mProcessData</c> turned out to be.</summary>
    private static ManagedObject? ObjField(ManagedObject obj, params string[] names)
    {
        if (obj == null) return null;
        foreach (string n in names)
        {
            try
            {
                var v = FlowHelper.GetObjectField(obj, n);
                if (v != null) return v;
            }
            catch { }
        }
        try
        {
            var fields = obj.GetTypeDefinition()?.GetFields();
            if (fields == null) return null;
            foreach (var f in fields)
            {
                string fn = f.Name ?? "";
                bool hit = false;
                foreach (string n in names)
                    if (fn.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
                if (!hit) continue;
                try
                {
                    if (f.GetDataBoxed(typeof(object), obj.GetAddress(), f.IsStatic()) is ManagedObject mo)
                        return mo;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>An integer field whose name mentions one of <paramref name="hints"/>. Read through
    /// the accessor first (a property, or a <c>get_X</c>), then through the field list; a value of
    /// 0 means "not found", which is the same answer the game gives for "not set", and neither is
    /// worth an entry of its own.</summary>
    private static uint FindUInt(ManagedObject? obj, params string[] hints)
    {
        if (obj == null) return 0;
        foreach (string h in hints)
        {
            try
            {
                uint v = FlowHelper.ReadUIntField(obj, h);
                if (v != 0) return v;
            }
            catch { }
        }
        try
        {
            var fields = obj.GetTypeDefinition()?.GetFields();
            if (fields == null) return 0;
            foreach (var f in fields)
            {
                string fn = f.Name ?? "";
                string type = f.Type?.FullName ?? "";
                if (!type.Contains("UInt32") && !type.Contains("Int32")) continue;
                bool hit = false;
                foreach (string h in hints)
                    if (fn.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
                if (!hit) continue;
                try
                {
                    object? raw = f.GetDataBoxed(typeof(object), obj.GetAddress(), f.IsStatic());
                    if (raw == null) continue;
                    uint u = Convert.ToUInt32(raw);
                    // An id, not a bitmask, a handle or a tick count.
                    if (u != 0 && u < 10_000_000) return u;
                }
                catch { }
            }
        }
        catch { }
        return 0;
    }

    /// <summary>The mission's title: any zero-argument method returning a string whose name says
    /// "title" — <c>GetTitleMessage()</c> is the shape the phone's own mission list uses. Failing
    /// that, a nested object (the mission definition the payload points at) that answers the same
    /// question. A Guid is not a title: an object's own id string is what a field like this holds
    /// when the real text was never localised, and reading one aloud is worse than saying nothing.
    /// </summary>
    private static string? FindTitle(ManagedObject? obj)
    {
        if (obj == null) return null;
        string? direct = InvokeTitle(obj);
        if (direct != null) return direct;
        foreach (string member in new[] { "mMissionScript", "mMissionDefine", "mMissionUserData",
                                          "mMissionParam", "mMissionInfo" })
        {
            var nested = ObjField(obj, member);
            if (nested == null) continue;
            string? text = InvokeTitle(nested);
            if (text != null) return text;
        }
        return null;
    }

    private static string? InvokeTitle(ManagedObject obj)
    {
        try
        {
            var methods = obj.GetTypeDefinition()?.GetMethods();
            if (methods == null) return null;
            foreach (var m in methods)
            {
                string n = m.Name ?? "";
                string ret = m.ReturnType?.FullName ?? "";
                if (!ret.Contains("String")) continue;
                if (n.IndexOf("Title", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var ps = m.GetParameters();
                if (ps != null && ps.Count != 0) continue;
                try
                {
                    var r = m.InvokeBoxed(typeof(object), obj, Array.Empty<object>());
                    if (r is string s && s.Length > 0 && !LooksLikeGuid(s)) return s;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>A 32- or 36-character hex string is an id the engine generated, not text a player
    /// can hear. The same defence <see cref="MissionTargetService"/> makes for its own names.</summary>
    private static bool LooksLikeGuid(string s)
    {
        if (s.Length != 32 && s.Length != 36) return false;
        int hex = 0;
        foreach (char c in s)
        {
            if (c == '-') continue;
            if (!char.IsAsciiHexDigit(c)) return false;
            hex++;
        }
        return hex == 32;
    }

    private static bool _dumped;

    /// <summary>
    /// Written once, the first time the mission list cannot be read: everything the mission system
    /// actually declares, so the next attempt starts from the engine's own names instead of another
    /// guess. This is the same discipline <see cref="MissionTargetService"/> follows for a target
    /// it cannot locate — the log alone cannot say whether a name was wrong or a list was empty,
    /// and those need opposite fixes.
    /// </summary>
    private static void DumpMissionSystemOnce(ManagedObject sys)
    {
        if (_dumped) return;
        _dumped = true;
        try
        {
            var sb = new System.Text.StringBuilder("# WTMissionSystem, for the mission-history backfill\n");
            var td = sys.GetTypeDefinition();
            sb.AppendLine($"# type: {td?.FullName ?? "?"}");
            sb.AppendLine("# fields:");
            ObjectDumper.DumpFieldsWithValues(sb, td, sys, "#   ");
            sb.AppendLine("# getters mentioning Mission/Device/List/Info:");
            ObjectDumper.DumpGetters(sb, td, sys, "#   ",
                                     new[] { "Mission", "Device", "List", "Info", "Accept", "Clear" }, maxTries: 300);

            // THE SECOND LAYER, and the one that actually matters: mListMissionDevice turned out to
            // be EMPTY in game (it holds what the phone's device screen is showing, not the
            // player's missions), so the data has to come from the accepted/deployed process
            // records instead — and what THOSE can be asked for a mission's name and its target is
            // the thing no dump we already have answers. One live record is walked in full.
            foreach (string getter in new[] { "GetDicAcceptedMissionValues", "GetDicDeployedMissionValues" })
            {
                var values = FlowHelper.Call(sys, getter) as ManagedObject;
                int count = FlowHelper.GetListCount(values);
                sb.AppendLine($"# {getter}() -> {count} records");
                for (int i = 0; i < count && i < 3; i++)
                {
                    var proc = FlowHelper.GetListItem(values, i);
                    if (proc == null) continue;
                    var ptd = proc.GetTypeDefinition();
                    sb.AppendLine($"#   [{i}] {ptd?.FullName ?? "?"} fields:");
                    ObjectDumper.DumpFieldsWithValues(sb, ptd, proc, "#     ");
                    sb.AppendLine($"#   [{i}] getters:");
                    ObjectDumper.DumpGetters(sb, ptd, proc, "#     ", maxTries: 300);
                    // The payload most likely to name the mission and place it. It is a FIELD, so
                    // it is looked up among the fields (ObjField) rather than as a property — the
                    // reason the very first version of this dump printed nothing here.
                    foreach (string member in new[] { "mProcessData" })
                    {
                        var data = ObjField(proc, member);
                        if (data == null)
                        {
                            sb.AppendLine($"#   [{i}].{member} NOT REACHABLE (not a property, and no field matched)");
                            continue;
                        }
                        var dtd = data.GetTypeDefinition();
                        sb.AppendLine($"#   [{i}].{member} {dtd?.FullName ?? "?"} fields:");
                        ObjectDumper.DumpFieldsWithValues(sb, dtd, data, "#     ");
                        sb.AppendLine($"#   [{i}].{member} getters:");
                        ObjectDumper.DumpGetters(sb, dtd, data, "#     ", maxTries: 300);
                    }
                }
            }

            // THE MISSION SYSTEM IS NOT THE ONLY PLACE A MISSION IS REMEMBERED. The player's own
            // save data carries one too, and if the process records turn out to hold no readable
            // id, that is where the next attempt starts. Only the singletons whose name says so
            // are asked, and only their list-shaped getters, so this stays a bounded question
            // rather than a scan of the whole engine.
            sb.AppendLine("# other singletons naming Mission/PlayerData — list-shaped getters only:");
            try
            {
                foreach (var singleton in API.GetManagedSingletons())
                {
                    var obj = singleton.Instance;
                    if (obj == null) continue;
                    string typeName = obj.GetTypeDefinition()?.FullName ?? "";
                    if (typeName.Length == 0) continue;
                    if (typeName.IndexOf("Mission", StringComparison.OrdinalIgnoreCase) < 0
                        && typeName.IndexOf("PlayerData", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (typeName == MISSION_SYSTEM) continue;
                    sb.AppendLine($"#   {typeName}");
                    ObjectDumper.DumpGetters(sb, obj.GetTypeDefinition(), obj, "#     ",
                                             new[] { "List", "Values", "Data", "Clear", "History" }, maxTries: 200);
                }
            }
            catch { }

            string path = System.IO.Path.Combine(ObjectDumper.DumpDir, "sf6access_missionhistory_probe.txt");
            System.IO.File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
            API.LogInfo($"[SF6Access] Mission history: wrote mission-system shape to {path}");
        }
        catch { }
    }

    private static uint ReadUInt(ManagedObject obj, string getter)
    {
        var boxed = FlowHelper.Call(obj, getter);
        if (boxed == null) return 0;
        try { return System.Convert.ToUInt32(boxed); } catch { return 0; }
    }

    /// <summary>How many missions the list is walked for. The dump reports 180 in one city; the
    /// cap is the loose end of that so a future patch with more still fits.</summary>
    private const int MAX_MISSIONS_READ = 400;

    /// <summary>Whether this objective is already recorded under this key.</summary>
    public static bool Has(string key)
    {
        Load();
        if (string.IsNullOrEmpty(key)) return false;
        foreach (var e in _all)
            if (e.Key == key) return true;
        return false;
    }

    /// <summary>
    /// <b>Note that the player has STOOD at this objective, and where.</b> This is the only
    /// place a POSITION enters the history: the objective is being resolved right now, so its
    /// object is in the scene and its coordinates are real. An entry that already exists (put
    /// there by the backfill, with no place) gets the place filled in rather than a second line.
    ///
    /// <para>Called only on ARRIVAL, not on resolution — the owner's own rule (2026-10-06): an
    /// objective joins the history when they come within two metres of it, so that every entry
    /// with coordinates is a place they have actually stood.</para>
    /// </summary>
    public static void Record(string slot, string name, float x, float y, float z,
                              float flatM = -1f, float dyM = 0f)
    {
        if (string.IsNullOrEmpty(slot)) return;
        if (x == 0f && y == 0f && z == 0f) return;
        uint city = WorldTourStateService.CityId;
        if (city == 0) return;

        Load();
        string trimmed = (name ?? "").Trim();
        // The object's own name is a last resort and on a zone objective it is meaningless
        // ("SenarioZoneGroup"). The MISSION's title is what the player recognises, and the slot the
        // caller hands over carries the mission id, so ask for it when the object gave nothing.
        if (trimmed.Length == 0)
            trimmed = MissionTargetService.MissionTitle(MissionIdOf(slot));

        // ONE LINE PER OBJECTIVE, not one per slot — but never by throwing a recorded place away.
        // The backfill writes an entry keyed `mission:{id}` (all it knows is the story's own
        // record) while an arrival is keyed `hud:{id}:{type}` or `system:{id}:{kind}:{i}`.
        // Matching on the slot alone therefore produced TWO rows for one mission — the
        // backfill's with no coordinates and the arrival's with them — and the list read out as
        // the same objective twice, once reachable and once not. So the mission id (the second
        // field of every one of those keys) is used AS A FALLBACK, and only to fill in an entry
        // that has no place yet. A mission with several targets keeps one line per target: the
        // file already holds two places for mission 11041, and merging by id alone would have
        // overwritten one of them with the other's coordinates.
        uint id = MissionIdOf(slot);
        int existing = _all.FindIndex(e => e.Key == slot);
        if (existing < 0 && id != 0)
            existing = _all.FindIndex(e => !e.HasPlace && MissionIdOf(e.Key) == id);

        if (existing >= 0)
        {
            var old = _all[existing];
            bool moved = !old.HasPlace
                         || MathF.Abs(old.X - x) > 0.01f || MathF.Abs(old.Y - y) > 0.01f
                         || MathF.Abs(old.Z - z) > 0.01f;
            _all[existing] = old with
            {
                // The entry is ADOPTED under the slot it was actually stood at. Leaving the
                // backfill's `mission:{id}` key in place would mean the next arrival at this very
                // objective matched nothing (its slot is not that key) and opened a second line —
                // the duplicate this merge exists to prevent, arriving one step later.
                Key = slot,
                Name = trimmed.Length > 0 ? trimmed : old.Name,
                HasPlace = true, X = x, Y = y, Z = z,
                City = old.City != 0 ? old.City : city,
            };
            // Arrival is noticed twice a second while the player stands there, and the same
            // objective is re-read every time. Only a genuine change is worth a line in the log or
            // a write to disk; otherwise the log fills with one sentence per half second.
            if (moved)
            {
                Save();
                API.LogInfo($"[SF6Access] Mission history: reached '{Describe(_all[existing].Name)}' " +
                            $"at ({x:0.0}, {y:0.0}, {z:0.0}) slot={slot}" +
                            (flatM >= 0f ? $" — {flatM:0.0} m away horizontally, height difference {dyM:0.0} m" : ""));
            }
            return;
        }

        _all.Add(new Item(city, slot, trimmed, 0, 0, false, HasPlace: true, x, y, z, NextOrder(city)));
        Save();
        API.LogInfo($"[SF6Access] Mission history: reached '{Describe(trimmed)}' at " +
                    $"({x:0.0}, {y:0.0}, {z:0.0}) slot={slot}" +
                    (flatM >= 0f ? $" — {flatM:0.0} m away horizontally, height difference {dyM:0.0} m" : ""));
    }

    /// <summary>The mission id inside a slot string — <c>hud:{id}:{type}</c> or
    /// <c>system:{id}:{kind}:{i}</c>. The id is the second field either way, which is what makes
    /// the slot useful for looking the mission's own title up.</summary>
    public static uint MissionIdOf(string slot)
    {
        if (string.IsNullOrEmpty(slot)) return 0;
        string[] f = slot.Split(':');
        if (f.Length < 2) return 0;
        return uint.TryParse(f[1], out uint id) ? id : 0;
    }

    /// <summary>The next story-order number within a city. Kept as an explicit number rather than
    /// the list's own index so an entry updated in place never renumbers the ones after it.</summary>
    private static int NextOrder(uint city)
    {
        int max = 0;
        foreach (var e in _all)
            if (e.City == city && e.Order > max) max = e.Order;
        return max + 1;
    }

    private static string Describe(string name) => name.Length > 0 ? name : "(unnamed)";

    // ------------------------------------------------------------------ the file

    private static string FilePath => Path.Combine(ObjectDumper.DumpDir, FILE);

    /// <summary>File format, one objective per line:
    /// <c>city \t key \t name \t chapter \t progress \t cleared \t hasPlace \t x \t y \t z \t order</c>
    /// — ELEVEN fields.
    ///
    /// <para>The count is derived from the format above rather than written twice: a strict number
    /// that disagrees with the columns written is the exact bug this file shipped with once
    /// (2026-10-06 — the header said eleven, the check demanded twelve, every line was rejected and
    /// the category came back empty while the log said "0 objectives loaded" and the file plainly
    /// had one). A short line is now read as far as it goes instead of being dropped whole, so a
    /// field added later never costs the entries already on disk.</para></summary>
    private const int MIN_FIELDS = 7;   // city, key, name, chapter, progress, cleared, hasPlace

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            // What the file had, and what we managed to read out of it, are two different numbers
            // — and the difference between them is how yesterday's entries were lost. A build whose
            // format check disagreed with its own writer read 0 of 1, `Save()` then rewrote the file
            // wholesale, and the one objective on disk was gone before anyone could see it. So the
            // count on disk is remembered, and no save is allowed to shrink it (see CanSave).
            _linesOnDisk = 0;
            var inv = CultureInfo.InvariantCulture;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                _linesOnDisk++;
                string[] f = line.Split('\t');
                if (f.Length < MIN_FIELDS) continue;
                if (!uint.TryParse(f[0], NumberStyles.Integer, inv, out uint city)) continue;
                if (f[1].Length == 0) continue;
                int.TryParse(f[3], NumberStyles.Integer, inv, out int chapter);
                int.TryParse(f[4], NumberStyles.Integer, inv, out int progress);
                bool cleared = f[5] == "1";
                bool hasPlace = f[6] == "1";
                float.TryParse(At(f, 7), NumberStyles.Float, inv, out float x);
                float.TryParse(At(f, 8), NumberStyles.Float, inv, out float y);
                float.TryParse(At(f, 9), NumberStyles.Float, inv, out float z);
                if (!int.TryParse(At(f, 10), NumberStyles.Integer, inv, out int order)) order = 0;
                _all.Add(new Item(city, f[1], f[2] == "-" ? "" : f[2], chapter, progress, cleared,
                                  hasPlace, x, y, z, order));
            }
            API.LogInfo($"[SF6Access] Mission history: {_all.Count} objectives loaded from {FILE}" +
                        (_linesOnDisk > _all.Count ? $" — {_linesOnDisk} on disk, {_linesOnDisk - _all.Count} unreadable; the file is left alone" : ""));
        }
        catch { }
    }

    private static int _linesOnDisk;

    /// <summary>
    /// <b>The file is only ever written when we hold at least what it held.</b>
    ///
    /// <para>This is the guard that should have existed from the start. The history is the one
    /// thing here that cannot be recovered once lost — an objective's position exists only while
    /// its object is in the scene — and a wholesale rewrite is one bad read away from erasing it.
    /// On 2026-10-06 a format check that disagreed with its own writer read 0 of 1, saved
    /// immediately after, and the player's objective of the previous evening was gone.</para>
    ///
    /// <para>Refusing to save costs nothing recoverable: the next read gets another chance, and the
    /// log says plainly that the file was kept rather than being replaced by an empty one.</para>
    /// </summary>
    private static bool CanSave()
    {
        if (_all.Count >= _linesOnDisk) return true;
        API.LogInfo($"[SF6Access] Mission history: refusing to write {_all.Count} over {_linesOnDisk} " +
                    "stored objectives — the file is kept as it is");
        return false;
    }

    /// <summary>A field by index, or an empty string when the line stopped short of it — so a
    /// record written by an older build still loads its leading fields instead of being dropped.</summary>
    private static string At(string[] fields, int index) => index < fields.Length ? fields[index] : "";

    /// <summary>Throttled like <see cref="DistrictMemory"/>. <paramref name="force"/> is for the
    /// backfill, which has just changed a batch and should not wait for the throttle window.</summary>
    private static void Save(bool force = false)
    {
        long now = System.Environment.TickCount64;
        if (!force && now - _savedAt < 1000) return;
        _savedAt = now;
        if (!CanSave()) return;
        try
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# city\tkey\tname\tchapter\tprogress\tcleared\thasPlace\tx\ty\tz\torder");
            foreach (var e in _all)
                sb.AppendLine(string.Join('\t', e.City.ToString(inv), Clean(e.Key),
                                          e.Name.Length > 0 ? Clean(e.Name) : "-",
                                          e.Chapter.ToString(inv), e.Progress.ToString(inv),
                                          e.Cleared ? "1" : "0", e.HasPlace ? "1" : "0",
                                          e.X.ToString("R", inv), e.Y.ToString("R", inv), e.Z.ToString("R", inv),
                                          e.Order.ToString(inv)));
            File.WriteAllText(FilePath, sb.ToString(), new System.Text.UTF8Encoding(false));
            // What is on disk is now exactly what we hold, so a later save is judged against this
            // rather than against the count read at load time.
            _linesOnDisk = _all.Count;
        }
        catch { }
    }

    /// <summary>A tab or a newline in a name would split the record — the same defence
    /// <see cref="PlaceFile"/> makes for its own field.</summary>
    private static string Clean(string? text) =>
        (text ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
