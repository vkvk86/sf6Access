using System.Collections.Generic;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;
using SF6Access.Services;
using SF6Access.Services.WorldTour;

namespace SF6Access.Hooks.WorldTour;

/// <summary>
/// World Tour field-awareness reader (WT-1). An always-on monitor (no menu focus
/// to hook): while the World Tour avatar field is loaded it
/// <list type="bullet">
/// <item>announces the CURRENT interaction target when it changes — the nearest
///   thing the player can talk to / examine, by name and kind; and</item>
/// <item>on an on-demand key, lists the nearby interactables — in-range targets
///   from the game's own access list, else every avatar in the field by name,
///   camera-relative clock hour and metric distance ("Luke, master at 12
///   o'clock, 5 meters" — fully calibrated in game 2026-07-20).</item>
/// </list>
/// Upstream v0.6.0 moved the announcement lane onto <see cref="FieldFeedback"/>'s
/// speech policy (interruption-safe, muted in dialogue and fights); the arrival
/// readout and the scene-surroundings sweep below stay ours.
/// </summary>
public class FieldAwarenessHooks
{
    private const int TargetStablePolls = 3;
    private const int MaxNearbySpoken = 8;
    private const long PollMs = 250;
    private const int VkN = 0x4E;
    private static readonly ReadoutShortcut NearbyKey = new(VkN, ReadoutShortcut.PAD_START);
    private static string _pendingTarget, _announcedTarget;
    private static int _pendingPolls;
    private static long _nextPoll;
    private readonly record struct Interactable(string Name, int ContactType, float Distance, bool IncludesKind);

    private const string SECTION_KEY = "wt_field_section";

    // Set when the player arrives somewhere new, cleared once the surroundings
    // have actually been read out. It is a flag rather than an immediate call
    // because arrival is exactly when the game is most likely to be talking.
    private static bool _arrivalPending;

    // Hold after the reader speaks, so the arrival readout does not cut off an
    // announcement that is still being spoken.
    private const long READER_HOLD_MS = 1200;

    // Floor between automatic arrival announcements, independent of what the
    // section id does.
    private const long ARRIVAL_MIN_GAP_MS = 20000;
    private static long _lastArrivalTick;

    // How far out the on-demand readout names the SCENE, and how close in it stays quiet
    // because the game's own access list already covers that (arm's reach).
    private const float SCENE_NEAR_M = 25f;
    private const float ARM_REACH_M = 2.5f;

    [PluginEntryPoint]
    public static void Initialize() => API.LogInfo("[SF6Access] FieldAwarenessHooks initialized (interaction changes and N / Start readout)");

    [Callback(typeof(LateUpdateBehavior), CallbackType.Post)]
    public static void Tick()
    {
        // The on-demand key must be sampled every frame (short presses).
        bool wantsNearby = NearbyKey.Pressed();
        FieldPresenceService.Refresh();
        if (!FieldFeedback.CanOutput)
        {
            _pendingTarget = _announcedTarget = null;
            _pendingPolls = 0;
            _arrivalPending = false;
            GameStateTracker.Remove(SECTION_KEY);
            FieldFeedback.Forget("contact");
            FieldFeedback.Forget("contact-left");
            return;
        }

        var mgr = WorldTourStateService.GetAvatarManager();

        // Arriving somewhere new reads out the surroundings by itself (user rule
        // 2026-08-14). The section id is the game's own notion of "where am I",
        // so it changes exactly on arrival and not while walking around inside a
        // place. The readout is DEFERRED rather than fired here: arrival often
        // coincides with a dialogue or a prompt, and consuming the change while
        // muted would lose the announcement entirely.
        // Section id 0 means "unavailable", not "section zero" — treating it as a
        // real value makes the id flap between 0 and the true one and fires an
        // arrival on every flap.
        uint section = WorldTourStateService.CurrentSectionId;
        if (section != 0 && GameStateTracker.HasChanged(SECTION_KEY, section.ToString()))
        {
            _arrivalPending = true;

            // Where this district is, learned by standing in it: the game has no
            // position for a district anywhere it can be read, so the player's own
            // feet are the only honest source. (DistrictMemory, 2026-10-02.)
            var pp = AvatarFieldReader.ReadPlayerPos(mgr);
            if (pp.ok) DistrictMemory.Sample(WorldTourStateService.CityId, section, pp.x, pp.y, pp.z);
        }

        if (wantsNearby)
        {
            _arrivalPending = false;   // asked for it manually; no need to repeat
            AnnounceNearby();
        }
        else if (_arrivalPending && ArrivalReadoutAllowed(mgr))
        {
            _arrivalPending = false;
            AnnounceArrival(mgr);
        }

        long now = System.Environment.TickCount64;
        if (now < _nextPoll) return;
        _nextPoll = now + PollMs;
        AnnounceCurrentTargetChange();
    }

    /// <summary>What arriving somewhere says: HOW MANY people are around, and
    /// nothing else.
    ///
    /// <para>It used to read the whole list, and in a busy district that is
    /// eighteen names with bearings and distances that nobody asked for and
    /// nobody can hold in their head. A count tells the player what kind of place
    /// they have walked into, which is the entire point of an arrival
    /// announcement; the detail is one keypress away on the on-demand radar,
    /// where it was requested.</para></summary>
    private static void AnnounceArrival(ManagedObject mgr)
    {
        int count = ReadInteractables().Count;
        if (count == 0) count = AvatarFieldReader.ReadOthers(mgr).Count;
        // Nothing around is not worth saying out of the blue — only in answer to
        // the key.
        if (count == 0) return;

        _lastArrivalTick = System.Environment.TickCount64;
        // Queued: the same crossing makes ZoneHooks name the new zone, which must not be cut off.
        ScreenReaderService.Speak(LocalizedText.NearbyCount(count), interrupt: false);
    }

    /// <summary>Whether the automatic arrival readout may speak right now. The
    /// on-demand key deliberately bypasses all of this: an explicit press is a
    /// request, and a request must always be answered.</summary>
    private static bool ArrivalReadoutAllowed(ManagedObject mgr)
    {
        if (!FieldPresenceService.CanSpeak) return false;
        // A floor on how often arrival can speak at all, whatever the section id
        // does. Belt and braces against the flapping that produced the spam.
        if (System.Environment.TickCount64 - _lastArrivalTick < ARRIVAL_MIN_GAP_MS) return false;
        if (SpTalkNovelHooks.DialogueActive) return false;
        if (System.Environment.TickCount64 - ScreenReaderService.LastInterruptTick < READER_HOLD_MS) return false;
        // Standing on top of something: the target-change reader below is already
        // announcing it, and a full list would just repeat it with extras.
        return AvatarFieldReader.GetAccessInfoCount(mgr) == 0;
    }

    private static void AnnounceCurrentTargetChange()
    {
        string description = NearestDescription();
        if (string.IsNullOrEmpty(description))
        {
            FieldFeedback.Forget("contact");
            _pendingTarget = null;
            _pendingPolls = 0;
            if (_announcedTarget != null && FieldFeedback.CanExplore)
                FieldFeedback.Say("contact-left", LocalizedText.LeavingTarget(_announcedTarget),
                    FieldSpeechPriority.Ambient, stillValid: () => _pendingTarget == null);
            _announcedTarget = null;
            return;
        }
        FieldFeedback.Forget("contact-left");
        string spoken = ExplorationText.Interact(description);
        if (description != _pendingTarget)
        {
            FieldFeedback.Forget("contact");
            _pendingTarget = description;
            _pendingPolls = 1;
            return;
        }
        if (_pendingPolls < TargetStablePolls) { _pendingPolls++; return; }
        if (description == _announcedTarget) return;
        FieldFeedback.Say("contact", spoken, FieldSpeechPriority.Interaction,
            onSpoken: () => _announcedTarget = description, stillValid: () => _pendingTarget == description);
    }

    /// <summary>The nearest actual contact for the Z readout; proximity to a landmark never counts.</summary>
    internal static string CurrentInteraction()
    {
        if (!FieldFeedback.CanOutput) return null;
        string description = NearestDescription();
        return string.IsNullOrEmpty(description) ? null : ExplorationText.Interact(description);
    }

    private static string NearestDescription()
    {
        var list = ReadInteractables();
        if (list.Count == 0) return null;
        var nearest = list[0];
        foreach (var item in list)
            if (item.Distance < nearest.Distance) nearest = item;
        return Describe(nearest);
    }

    /// <summary>On-demand: list the nearby interactables, nearest first. The
    /// access list only covers arm's-length targets, so when it's empty fall
    /// back to the field's avatar list with real distances — hot/cold guidance
    /// toward a DISTANT NPC (the "walk to Luke" case).</summary>
    private static void AnnounceNearby()
    {
        var list = ReadInteractables();
        list.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        var parts = new List<string>(list.Count);
        foreach (var item in list)
        {
            string d = Describe(item);
            if (!string.IsNullOrEmpty(d)) parts.Add(d);
        }
        AddNamedSurroundings(parts);

        if (parts.Count == 0)
        {
            if (!AnnounceNearbyFromAvatarList()) ScreenReaderService.Speak(LocalizedText.NothingNearby());
            return;
        }

        string header = LocalizedText.NearbyCount(parts.Count);
        ScreenReaderService.Speak($"{header}: {Join(parts)}");
    }

    /// <summary>What the field AROUND the player is called, past arm's reach: every real name
    /// the scene sweep has found, each with a clock bearing and a distance.
    ///
    /// <para>The picker answers "where can I go"; this answers "what is around me" — which is
    /// the whole of the 2026-10-03 request "多了很多交互物，但我都不知道是什么呀". Only names
    /// the game itself publishes are spoken: an editor's object name ("gm 025000") read aloud
    /// tells a blind player nothing, which is exactly why they were filtered out of the
    /// picker on the same day.</para>
    /// </summary>
    private static void AddNamedSurroundings(List<string> parts)
    {
        var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        if (!me.ok) return;

        var named = new List<SceneTargetCatalog.Named>();
        SceneTargetCatalog.CollectNear(named, me.x, me.z, SCENE_NEAR_M, ARM_REACH_M);
        if (named.Count == 0) return;

        var camFwd = FieldDirectionService.GetCameraForward();
        foreach (var n in named)
        {
            float dx = n.X - me.x, dz = n.Z - me.z;
            int meters = (int)System.Math.Round(System.MathF.Sqrt(dx * dx + dz * dz));
            int hour = FieldDirectionService.ClockHour(camFwd, dx, dz);
            parts.Add(hour > 0 ? LocalizedText.AtClockMeters(n.Name, hour, meters)
                               : LocalizedText.AtMeters(n.Name, meters));
        }
    }

    private static List<Interactable> ReadInteractables()
    {
        var result = new List<Interactable>();
        var mgr = WorldTourStateService.GetAvatarManager();
        if (mgr == null) return result;
        var list = AvatarFieldReader.GetAccessInfoList(mgr);
        int count = FlowHelper.GetListCount(list);
        for (int i = 0; i < count; i++)
        {
            var access = FlowHelper.GetListItem(list, i);
            var info = AvatarFieldReader.GetProp(access, "TargetInfo");
            var target = AvatarFieldReader.GetProp(info, "Target");
            string name = FlowHelper.CleanTags(FlowHelper.Call(target, "GetDispName") as string)?.Trim();
            // Upstream v0.6.0: rest stations and fast-travel points get their own kind word
            // rather than the generic contact-type word; anonymous access targets are named
            // from the access catalog's own text.
            bool includesKind = RestContact.TryDescribe(target, name, out string contactLabel)
                || FastTravelContact.TryDescribe(target, name, out contactLabel);
            if (includesKind) name = contactLabel;
            else if (string.IsNullOrEmpty(name) && target != null)
            {
                var player = FieldPresenceService.PlayerPos;
                foreach (var entry in FieldAccessCatalog.Nearby(player.X, player.Y, player.Z, System.Environment.TickCount64))
                    if (entry.Target == target.GetAddress()) { name = entry.Text; break; }
            }
            if (string.IsNullOrEmpty(name)) continue;
            int contactType = target != null ? AvatarFieldReader.ReadContactType(target) : -1;
            float distance = FlowHelper.ReadFloatField(info, "Distance", 0f);
            result.Add(new(name, contactType, distance, includesKind));
        }
        return result;
    }

    /// <summary>Fallback radar for the on-demand key: every OTHER avatar in the
    /// field by name, camera-relative clock hour and metric distance, nearest
    /// first. Returns false when nothing could be read, so the caller falls
    /// back to "nothing nearby".</summary>
    private static bool AnnounceNearbyFromAvatarList()
    {
        var mgr = WorldTourStateService.GetAvatarManager();
        var others = AvatarFieldReader.ReadOthers(mgr);
        if (others.Count == 0) return false;

        // Clock frame: camera forward (stick-relative "12").
        var camFwd = FieldDirectionService.GetCameraForward();

        var parts = new List<string>(others.Count);
        foreach (var other in others)
        {
            int meters = (int)System.Math.Round(other.Dist);
            int hour = FieldDirectionService.ClockHour(camFwd, other.Dx, other.Dz);
            string what = AvatarFieldReader.DescribeAvatar(other.Avatar) ?? LocalizedText.ContactPerson();
            parts.Add(hour > 0
                ? LocalizedText.AtClockMeters(what, hour, meters)
                : LocalizedText.AtMeters(what, meters));
        }

        ScreenReaderService.Speak($"{LocalizedText.NearbyCount(parts.Count)}: {Join(parts)}");
        return true;
    }

    /// <summary>The nearest few, then a count of the rest. A crowded hub can put
    /// dozens of people in range, and a readout that names all of them is a wall
    /// of speech nobody can hold in their head — but silently dropping the tail
    /// would misreport the place as emptier than it is, so the remainder is
    /// spoken as a number.</summary>
    private static string Join(List<string> parts)
    {
        if (parts.Count <= MaxNearbySpoken) return string.Join(", ", parts);
        return $"{string.Join(", ", parts.GetRange(0, MaxNearbySpoken))}, {LocalizedText.AndMore(parts.Count - MaxNearbySpoken)}";
    }

    /// <summary>"Ryu, person" — the interactable's name plus its kind word, unless the
    /// name already carries its kind (rest stations, fast travel).</summary>
    private static string Describe(Interactable item)
    {
        string kind = item.IncludesKind ? null : AvatarFieldReader.KindWord(item.ContactType);
        return string.IsNullOrEmpty(kind) ? item.Name : $"{item.Name}, {kind}";
    }
}
