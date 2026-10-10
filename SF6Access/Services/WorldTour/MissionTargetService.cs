using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// Where the current World Tour mission wants you to go.
///
/// <para><b>The HUD marker answers first.</b> The mission the PLAYER selected
/// (primary or secondary) is the one the game's own on-screen arrow follows, and
/// that arrow reads its target out of <c>app.UICityHud_MissionGuide</c> — see
/// <see cref="MissionGuideReader"/>. Whenever that yields a target we can take a
/// position from, it wins, because it is the player's choice rather than the
/// story's.</para>
///
/// <para>The mission system below stays as the fallback for everything the HUD
/// cannot answer: the marker not built yet, hidden, or following nothing.</para>
///
/// <para>The fallback tracks this in <c>app.worldtour.WTMissionSystem</c>:
/// <c>FindProgressMissionId()</c> gives the mission the HUD is following, and
/// <c>GetList{Npc,Om,Zone}MissionTargetInfo(id)</c> return that mission's target
/// records. Each record carries <c>ListHolderObj</c> — a list of LIVE scene
/// <c>GameObject</c>s — so the objective is not an abstract coordinate but a real
/// object we can read a transform from, and point a sound at.</para>
///
/// <para>All three target kinds are asked in turn because a mission objective is
/// sometimes a person, sometimes a thing, sometimes a place, and the game keeps
/// them in separate lists.</para>
///
/// <para><b>Empty is normal, not an error:</b> the holder list is empty whenever
/// the target has not streamed into the loaded scene — a different district, or
/// simply not spawned yet. That is a "no beacon right now", never a failure.</para>
/// </summary>
public static class MissionTargetService
{
    private const string MISSION_SYSTEM = "app.worldtour.WTMissionSystem";
    private const string FIND_PROGRESS_ID = "FindProgressMissionId";

    // The three target kinds, in the order they are asked. NPC first: a mission
    // objective is a person far more often than not — which is also the order of
    // the game's own app.UICityHud_MissionGuide.eTargetType (NPC, OM, ZONE), so
    // the index doubles as that enum's value when naming the kind in the log.
    private static readonly string[] TargetListGetters =
    {
        "GetListNpcMissionTargetInfo",
        "GetListOmMissionTargetInfo",
        "GetListZoneMissionTargetInfo",
    };

    /// <summary>The objective's GameObject and its position, or ok=false when
    /// there is nothing to point at right now.</summary>
    public readonly struct Target
    {
        public readonly ManagedObject Go;
        public readonly float X, Y, Z;
        public readonly bool Ok;
        /// <summary>Which objective this is, independent of the object holding it: the game swaps a far NPC
        /// for its map icon (<c>IconNpc_*</c>, 1.8 m higher) and back as the player moves (2026-09-26).</summary>
        public readonly string Slot;
        /// <summary>Whatever the game will publish as this objective's own name — a person's display
        /// name, a prop's component name — or empty. Read for the history list, which has to say
        /// <i>which</i> objective an entry is; the guidance itself never needs it. Empty is normal:
        /// a zone objective is a place, and a place has no name of its own.</summary>
        public readonly string Name;
        public Target(ManagedObject go, float x, float y, float z, string slot, string name = "")
        {
            Go = go; X = x; Y = y; Z = z; Ok = go != null; Slot = slot; Name = name ?? "";
        }
    }

    // What the last logged fix was. The log line is worth having on every CHANGE
    // of objective and worthless once a second for the same one.
    private static string _lastLogKey;

    /// <summary>Locate the current mission objective. Re-resolved every call —
    /// never cached, because the mission, the target and the object behind it all
    /// change underneath us.</summary>
    public static Target Find()
    {
        var hud = FindFromGuide();
        if (hud.Ok) return hud;
        var fallback = FindFromMissionSystem();
        if (fallback.Ok) return fallback;
        LogMiss();
        return default;
    }

    /// <summary>Why there is no objective right now, in one line. A mission the player has
    /// accepted but cannot be guided to looks identical to having no mission at all from
    /// the outside — the player asked to be taken to a mission's construction site and the
    /// picker offered nothing, with nothing logged to explain it (2026-10-02).</summary>
    private static void LogMiss()
    {
        long now = System.Environment.TickCount64;
        if (now - _missAt < MISS_LOG_MS) return;
        _missAt = now;

        var sb = new System.Text.StringBuilder("[SF6Access] Mission guide: no target - ");
        // Time of day matters: a mission whose target only exists by day (or by night)
        // has no target object at the wrong hour, which looks exactly like a bug from
        // the outside. WTDefine.TimeType: Invalid=0, Day=2, Night=4, Midnight=5.
        sb.Append($"time={WorldTourStateService.TimeType}; ");
        sb.Append($"hud={MissionGuideReader.LastMiss ?? "read ok but no position"}");

        var sys = API.GetManagedSingleton(MISSION_SYSTEM) as ManagedObject;
        if (sys == null) { sb.Append("; mission system unreachable"); API.LogInfo(sb.ToString()); return; }

        object idBoxed = null;
        try { idBoxed = FlowHelper.Call(sys, FIND_PROGRESS_ID); } catch { }
        sb.Append($"; {FIND_PROGRESS_ID}={idBoxed ?? "null"}");
        if (idBoxed == null) { API.LogInfo(sb.ToString()); return; }

        uint missionId;
        try { missionId = System.Convert.ToUInt32(idBoxed); } catch { API.LogInfo(sb.ToString()); return; }

        for (int kind = 0; kind < TargetListGetters.Length; kind++)
        {
            var list = FlowHelper.Call(sys, TargetListGetters[kind], missionId) as ManagedObject;
            int n = FlowHelper.GetListCount(list);
            sb.Append($"; {MissionGuideReader.TargetTypeName(kind)}={n}");
            for (int i = 0; i < n; i++)
            {
                var info = FlowHelper.GetListItem(list, i);
                if (info == null) continue;
                bool have = FlowHelper.ReadBoolField(info, "HaveMissionTarget");
                var holders = FlowHelper.Call(info, "get_ListHolderObj") as ManagedObject
                              ?? AvatarFieldReader.GetProp(info, "ListHolderObj");
                int h = FlowHelper.GetListCount(holders);
                sb.Append($" [{i}]have={have} holders={h}");
                for (int j = 0; j < h; j++)
                {
                    var go = FlowHelper.GetListItem(holders, j);
                    var p = PositionOf(go);
                    sb.Append($" {FlowHelper.Call(go, "get_Name") as string ?? "?"}" +
                              $"@({p.x:0.0},{p.y:0.0},{p.z:0.0})ok={p.ok}");
                }
            }
        }
        API.LogInfo(sb.ToString());
        DumpShape(sys, missionId);
    }

    /// <summary>Written once per mission that cannot be located. The player reported a story
    /// objective that used to appear and now does not; the target record exists but carries
    /// no holder objects, so the next attempt has to start from what the record actually
    /// declares — a zone id, a position, an unread member — not from another guess.</summary>
    private static void DumpShape(ManagedObject sys, uint missionId)
    {
        if (_dumpedMission == missionId) return;
        _dumpedMission = missionId;
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# mission {missionId}\thud miss: {MissionGuideReader.LastMiss ?? "-"}\t" +
                          $"guide components: {MissionGuideReader.LastComponentCount}");
            var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
            sb.AppendLine($"# player ({me.x:0.0}, {me.y:0.0}, {me.z:0.0}) ok={me.ok}");

            // What else the mission system can be asked — a getter that answers "where" for
            // the whole mission is worth more than any of the three target lists.
            var sysTd = sys.GetTypeDefinition();
            sb.AppendLine("# WTMissionSystem fields:");
            ObjectDumper.DumpFieldsWithValues(sb, sysTd, sys, "#   ");
            sb.AppendLine("# WTMissionSystem getters answering Target/Guide/Pos/Zone/Npc/Om/Icon:");
            ObjectDumper.DumpGetters(sb, sysTd, sys, "#   ", new[] { "Target", "Guide", "Pos", "Zone", "Npc", "Om", "Icon" }, sample: missionId);

            // Where the game files the OBJECTS a mission scatters around the city — the one
            // place left that could know where a not-yet-spawned objective belongs.
            var dist = API.GetManagedSingleton("app.worldtour.WTMissionDataManager") as ManagedObject;
            sb.AppendLine("# WTMissionDataManager:");
            if (dist == null) sb.AppendLine("#   no singleton");
            else
            {
                var dtd = dist.GetTypeDefinition();
                ObjectDumper.DumpFieldsWithValues(sb, dtd, dist, "#   ");
                sb.AppendLine("#   getters:");
                ObjectDumper.DumpGetters(sb, dtd, dist, "#   ", sample: missionId);
            }

            for (int kind = 0; kind < TargetListGetters.Length; kind++)
            {
                var list = FlowHelper.Call(sys, TargetListGetters[kind], missionId) as ManagedObject;
                int n = FlowHelper.GetListCount(list);
                for (int i = 0; i < n; i++)
                {
                    var info = FlowHelper.GetListItem(list, i);
                    if (info == null) continue;
                    var td = info.GetTypeDefinition();
                    sb.AppendLine($"# {MissionGuideReader.TargetTypeName(kind)}[{i}] {td?.FullName ?? "?"}");
                    ObjectDumper.DumpFieldsWithValues(sb, td, info, "#   ");
                    sb.AppendLine("#   getters:");
                    ObjectDumper.DumpGetters(sb, td, info, "#     ", sample: missionId);
                }
            }

            // Any catalog the game keeps about missions is a candidate source for a
            // destination; their names are the only way to find one.
            var names = new System.Collections.Generic.List<string>();
            try
            {
                foreach (var t in TDBTypes())
                {
                    string f = t.FullName ?? "";
                    if (f.Contains("Mission") && (f.StartsWith("app."))) names.Add(f);
                    if (names.Count > 120) break;
                }
            }
            catch { }
            sb.AppendLine($"# app types named *Mission* ({names.Count}):");
            foreach (string n in names) sb.AppendLine("#   " + n);

            string path = System.IO.Path.Combine(ObjectDumper.DumpDir, "sf6access_missiontarget.txt");
            System.IO.File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
            API.LogInfo($"[SF6Access] Mission guide: wrote target shape to {path}");
        }
        catch { }
    }

    private static uint _dumpedMission;

    // Every fifteen seconds at most: this runs once a second while no objective is found.
    private const long MISS_LOG_MS = 15000;
    private static long _missAt;

    /// <summary>The objective the game's own HUD marker is following — the
    /// player's selected mission. Not-ok whenever the marker has nothing, or its
    /// target has no readable position yet.</summary>
    private static Target FindFromGuide()
    {
        var guide = MissionGuideReader.Read();
        if (!guide.Ok) return default;

        var p = PositionOf(guide.TargetObject);
        if (!p.ok) return default;

        float x = p.x, y = p.y, z = p.z;

        // WHERE THE PLAYER MUST STAND beats where the object happens to be. For a
        // street NPC the two are nearly the same point; for one on an upper floor
        // the object's own origin is somewhere the player can never be, and the
        // navmesh says so. Refused answers fall straight back to the transform, so
        // this can only improve the destination.
        if (StandingPoint(guide.TargetObject, guide.AccessTarget, p.x, p.y, p.z,
                          out float sx, out float sy, out float sz))
        { x = sx; y = sy; z = sz; }
        LogStanding("hud", guide.MissionId, p.x, p.y, p.z, x, y, z);

        LogFix("hud", guide.MissionId, MissionGuideReader.TargetTypeName(guide.TargetType),
               guide.TargetObject, x, y, z);
        return new Target(guide.TargetObject, x, y, z, $"hud:{guide.MissionId}:{guide.TargetType}",
                          NameOf(guide.TargetObject));
    }

    /// <summary><b>The place the player must stand to interact, when the game publishes one.</b>
    /// False leaves the caller where it was — the target's own origin — never somewhere worse.
    ///
    /// <para><paramref name="accessTarget"/> is handed over ready-made by the HUD guide's
    /// <c>ProgressMisionInfo</c>. The mission system's own target records do not carry one, so
    /// when it is missing the target object's components are walked for it (see
    /// <see cref="AccessTargetOf"/>), which is what makes the fallback path equal to the HUD one
    /// rather than a permanently coarser answer.</para></summary>
    private static bool StandingPoint(ManagedObject targetGo, ManagedObject accessTarget,
                                      float x, float y, float z, out float sx, out float sy, out float sz)
    {
        sx = x; sy = y; sz = z;
        if (targetGo == null) return false;
        accessTarget ??= AccessTargetOf(targetGo);
        if (accessTarget == null) return false;
        var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        if (!me.ok) return false;
        return AccessPointReader.TryRead(accessTarget, targetGo, me.x, me.y, me.z, x, y, z,
                                         out sx, out sy, out sz);
    }

    /// <summary>The interaction component a target object carries, if any. Anything a player can
    /// be prompted about publishes one; asking costs one component walk per read and answers
    /// null for a bare prop, which is the honest answer for one.
    ///
    /// <para>Searcher components share the <c>"AccessTarget"</c> substring and carry no check
    /// shapes, so they are refused rather than filtered — trying one costs a read, choosing the
    /// wrong one costs a route.</para></summary>
    private static ManagedObject AccessTargetOf(ManagedObject go)
    {
        try
        {
            var comps = FlowHelper.Call(go, "get_Components") as ManagedObject;
            int n = FlowHelper.GetListCount(comps);
            for (int i = 0; i < n; i++)
            {
                var c = FlowHelper.GetListItem(comps, i);
                string type = c?.GetTypeDefinition()?.GetFullName() ?? "";
                if (type.Contains("AccessTarget")) return c;
            }
        }
        catch { }
        return null;
    }

    private static string _lastStandingKey;

    /// <summary>Said once per standing point that MOVED: the distance between the marker and the
    /// place the game expects the walker to be is the whole answer to "the objective is only a
    /// marker, the interaction is somewhere else" — with nothing here it can only be suspected.
    /// A metre or less is one object and not worth a line.</summary>
    private static void LogStanding(string source, uint missionId, float ox, float oy, float oz,
                                    float x, float y, float z)
    {
        float dx = x - ox, dy = y - oy, dz = z - oz;
        if (dx * dx + dy * dy + dz * dz < 1f) return;
        string key = $"{source}|{missionId}|{(int)x}|{(int)y}|{(int)z}";
        if (key == _lastStandingKey) return;
        _lastStandingKey = key;
        API.LogInfo($"[SF6Access] Mission guide: to stand and interact is {(float)System.Math.Sqrt(dx * dx + dy * dy + dz * dz):0.0} m " +
                    $"from the marker itself; guiding to ({x:0.0}, {y:0.0}, {z:0.0})");
    }

    private static Target FindFromMissionSystem()
    {
        var sys = API.GetManagedSingleton(MISSION_SYSTEM) as ManagedObject;
        if (sys == null) return default;

        object idBoxed;
        try { idBoxed = FlowHelper.Call(sys, FIND_PROGRESS_ID); }
        catch { return default; }
        if (idBoxed == null) return default;

        uint missionId;
        try { missionId = System.Convert.ToUInt32(idBoxed); }
        catch { return default; }

        for (int kind = 0; kind < TargetListGetters.Length; kind++)
        {
            string getter = TargetListGetters[kind];
            var list = FlowHelper.Call(sys, getter, missionId) as ManagedObject;
            int n = FlowHelper.GetListCount(list);
            for (int i = 0; i < n; i++)
            {
                var info = FlowHelper.GetListItem(list, i);
                if (info == null) continue;
                // The game's own "this record actually has a target" flag.
                if (!FlowHelper.ReadBoolField(info, "HaveMissionTarget")
                    && FlowHelper.Call(info, "get_HaveMissionTarget") is bool have && !have) continue;

                // Getter first: ListHolderObj is a property with no backing field,
                // and asking for the field logs a "Member not found" line on every
                // pass — this runs once a second, all session.
                var holders = FlowHelper.Call(info, "get_ListHolderObj") as ManagedObject
                              ?? AvatarFieldReader.GetProp(info, "ListHolderObj");
                int h = FlowHelper.GetListCount(holders);
                for (int j = 0; j < h; j++)
                {
                    var go = FlowHelper.GetListItem(holders, j);
                    var p = PositionOf(go);
                    if (!p.ok) continue;

                    // The mission system hands over a naked GameObject: nothing here says where
                    // the player has to STAND to use the thing. For three days that omission sent
                    // every route to om_setter's own origin, two metres short of a wall.
                    float x = p.x, y = p.y, z = p.z;
                    if (StandingPoint(go, null, p.x, p.y, p.z, out float sx, out float sy, out float sz))
                    { x = sx; y = sy; z = sz; }
                    LogStanding("system", missionId, p.x, p.y, p.z, x, y, z);

                    if (LogFix("system", missionId, MissionGuideReader.TargetTypeName(kind), go, x, y, z))
                        LogHolders(sys, missionId);
                    return new Target(go, x, y, z, $"system:{missionId}:{kind}:{i}", NameOf(go));
                }
            }
        }
        return default;
    }

    /// <summary>One log line per CHANGE of objective — a new mission, a new
    /// target kind, a different object, or a switch between the HUD marker and
    /// the mission system. The position is a snapshot at that moment, not a
    /// tracked value: an objective that walks does not deserve a line a second.
    /// </summary>
    private static bool LogFix(string source, uint missionId, string type,
                               ManagedObject go, float x, float y, float z)
    {
        string key = $"{source}|{missionId}|{type}|{FlowHelper.AddressOf(go):X}";
        if (key == _lastLogKey) return false;
        _lastLogKey = key;
        API.LogInfo($"[SF6Access] Mission guide: id={missionId} type={type} source={source} " +
                    $"name={FlowHelper.Call(go, "get_Name") as string ?? "?"} pos=({x:0.0}, {y:0.0}, {z:0.0})");
        return true;
    }

    /// <summary>Every live target object of the mission, by name and position, and why the HUD
    /// marker was not used: the first holder is not necessarily the one the player must reach.</summary>
    private static void LogHolders(ManagedObject sys, uint missionId)
    {
        var line = new System.Text.StringBuilder($"[SF6Access] Mission guide: HUD unused ({MissionGuideReader.LastMiss ?? "hit"})");
        for (int kind = 0; kind < TargetListGetters.Length; kind++)
        {
            var list = FlowHelper.Call(sys, TargetListGetters[kind], missionId) as ManagedObject;
            int n = FlowHelper.GetListCount(list);
            for (int i = 0; i < n; i++)
            {
                var info = FlowHelper.GetListItem(list, i);
                var holders = FlowHelper.Call(info, "get_ListHolderObj") as ManagedObject
                              ?? AvatarFieldReader.GetProp(info, "ListHolderObj");
                int h = FlowHelper.GetListCount(holders);
                line.Append($"; {MissionGuideReader.TargetTypeName(kind)}[{i}] holders={h}");
                for (int j = 0; j < h; j++)
                {
                    var go = FlowHelper.GetListItem(holders, j);
                    var p = PositionOf(go);
                    line.Append($" {FlowHelper.Call(go, "get_Name") as string ?? "?"}@({p.x:0.0}, {p.y:0.0}, {p.z:0.0})");
                }
            }
        }
        API.LogInfo(line.ToString());
    }

    /// <summary>Every type the game declares, for finding catalogs by name.</summary>
    private static System.Collections.Generic.IEnumerable<TypeDefinition> TDBTypes()
    {
        var tdb = TDB.Get();
        var m = tdb?.GetType().GetMethod("get_Types");
        if (m == null) yield break;
        var all = m.Invoke(tdb, null) as System.Collections.IEnumerable;
        if (all == null) yield break;
        foreach (var t in all)
            if (t is TypeDefinition td) yield return td;
    }

    /// <summary>
    /// <b>What the game calls this objective, or empty.</b> Only the history list needs it —
    /// "objective, 240 meters" repeated eight times tells a player nothing about which of the
    /// eight they want.
    ///
    /// <para>In order, all of them answers the GAME gave: the access target's own display name
    /// (what an NPC is called in the contact HUD), then the same on the target object itself,
    /// then the <c>WTNpcContext</c> fallback crowd NPCs carry. A zone objective publishes none
    /// of these, and a zone is a place rather than a thing — empty is the honest answer, and the
    /// picker speaks its own word for one.</para>
    ///
    /// <para>Reads only, and every one of them guarded: this rides on the once-a-second
    /// resolution and must never be the reason the bearing stops.</para>
    /// </summary>
    public static string NameOf(ManagedObject go)
    {
        if (go == null) return "";
        try
        {
            string name = Clean(FlowHelper.Call(go, "GetDispName") as string);
            if (name.Length > 0) return name;

            var access = AccessTargetOf(go);
            name = Clean(FlowHelper.Call(access, "GetDispName") as string);
            if (name.Length > 0) return name;

            // Crowd NPCs carry no access target but do publish their name on the context
            // component — the same fallback AvatarFieldReader uses for the same reason.
            var comps = FlowHelper.Call(go, "get_Components") as ManagedObject;
            int n = FlowHelper.GetListCount(comps);
            for (int i = 0; i < n; i++)
            {
                var c = FlowHelper.GetListItem(comps, i);
                if (c?.GetTypeDefinition()?.GetFullName()?.EndsWith("WTNpcContext") != true) continue;
                name = Clean(FlowHelper.Call(c, "get_NpcName") as string);
                if (name.Length > 0) return name;
            }
        }
        catch { }
        return "";
    }

    /// <summary>
    /// The MISSION's title, when the caller has the mission id and not just the object.
    ///
    /// <para>This is the name that actually means something to a player: "SenarioZoneGroup" is the
    /// level author's name for a place and reads as nothing at all, where the mission it belongs to
    /// is called "Metro City's skyscraper". The mission system keeps a device record per mission
    /// (<c>WTMissionDeviceInfo</c>) whose <c>GetTitleMessage()</c> answers exactly this, and the
    /// phone's own mission list reads it the same way (see <c>MissionListHooks</c>) — so this is
    /// the game's own answer, not a guess.</para>
    /// </summary>
    public static string MissionTitle(uint missionId)
    {
        if (missionId == 0) return "";
        try
        {
            var sys = API.GetManagedSingleton(MISSION_SYSTEM) as ManagedObject;
            if (sys == null) return "";
            var list = AvatarFieldReader.GetProp(sys, "mListMissionDevice") as ManagedObject
                       ?? FlowHelper.Call(sys, "get_mListMissionDevice") as ManagedObject;
            int n = FlowHelper.GetListCount(list);
            for (int i = 0; i < n; i++)
            {
                var info = FlowHelper.GetListItem(list, i);
                if (info == null) continue;
                object idBoxed = FlowHelper.Call(info, "GetMissionId");
                uint id = 0;
                try { if (idBoxed != null) id = System.Convert.ToUInt32(idBoxed); } catch { }
                if (id != missionId) continue;
                string title = Clean(FlowHelper.Call(info, "GetTitleMessage") as string);
                if (title.Length > 0) return title;
                break;
            }
        }
        catch { }
        return "";
    }

    /// <summary>Trim a display string and refuse one that is really a message Guid: an unresolved
    /// Guid reads as its own hex and would be spoken as the objective's name.</summary>
    private static string Clean(string text)
    {
        string s = FlowHelper.CleanTags(text)?.Trim();
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length == 36 || s.Length == 32)
        {
            bool hex = true;
            foreach (char c in s)
                if (c != '-' && !char.IsAsciiHexDigit(c)) { hex = false; break; }
            if (hex) return "";
        }
        return s;
    }

    private static (float x, float y, float z, bool ok) PositionOf(ManagedObject go)
    {        try
        {
            var tr = FlowHelper.Call(go, "get_Transform") as ManagedObject;
            var p = FlowHelper.Call(tr, "get_Position");
            if (p == null) return (0f, 0f, 0f, false);
            float x = FlowHelper.ReadVecComponent(p, "x");
            float y = FlowHelper.ReadVecComponent(p, "y");
            float z = FlowHelper.ReadVecComponent(p, "z");
            // An exact origin means the read failed, not an objective at (0,0,0).
            return (x, y, z, float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z)
                             && (x != 0f || y != 0f || z != 0f));
        }
        catch { return (0f, 0f, 0f, false); }
    }
}
