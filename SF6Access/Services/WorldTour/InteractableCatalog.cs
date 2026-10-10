using System;
using System.Collections.Generic;
using System.Globalization;
using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>Everything the game itself says can be interacted with, as choosable destinations.</b>
///
/// <para><b>The source is the avatar manager's <c>CurrentAccessInfoList</c></b> — the same list
/// <c>AtMissionContact</c> asks "is the objective interactable YET", so it is the game's own
/// answer to "what can I use from where I am standing", not a guess of ours. Every entry carries
/// an <c>AvatarAccessTargetBase</c>, and that component is the thing with a world position and a
/// display name.</para>
///
/// <para><b>Why it is a destination list and not just a narration.</b> 2026-10-03, the SiRN site:
/// the mission marker sat three metres away behind hoarding, the engine called it reachable and
/// then unreachable by turns, and the only thing the guide could say about the last metres was
/// where the marker was. The owner's own read of that — "我怀疑这个目标只是箭头，真正的交互点不是
/// 这里" — is testable, and listing what IS interactable is how the player tests it: walk there,
/// open the picker, and see what the world actually offers at arm's length.</para>
///
/// <para><b>It accumulates while walking.</b> <see cref="Pump"/> runs from the beacon's own tick
/// and remembers what has been in range recently, so opening the picker shows what they passed as
/// well as what they are touching. These are static objects — a vending machine does not follow
/// you — so a remembered position is still true. Nothing here is invented to fill a gap; an entry
/// whose name cannot be read answers to the honest word "可交互物" plus its own permanent number
/// (<see cref="InteractableIds"/>), rather than being guessed at, because a wrong name in a list
/// you navigate by is worse than a dull one — and a number this object keeps tomorrow is what
/// lets the player tell whether this one has been done already.</para>
/// </summary>
public static class InteractableCatalog
{
    /// <summary>How often the live list is read while walking. Twice a second: the game rebuilds
    /// it for its own purposes far faster than that, and an interactable does not move.</summary>
    private const long PumpMs = 500;

    /// <summary>How long a remembered entry stays listed, in milliseconds. Long enough to cover
    /// walking past a door and turning back to it; short enough that leaving the field and
    /// coming back does not offer last field's objects.</summary>
    private const long LifetimeMs = 60000;

    /// <summary>The farthest remembered object still offered as a destination. Past a street's
    /// length what is offered is clutter, and the picker sorts by distance anyway.</summary>
    private const float OfferRangeM = 120f;

    /// <summary>Memory bound, so a long walk through a crowded market cannot grow this without
    /// end. Well above anything a neighbourhood actually offers.</summary>
    private const int MaxEntries = 96;

    private const long LOG_MS = 10000;

    private readonly record struct Entry(string Name, float X, float Y, float Z, long SeenAt, bool Named);

    private static readonly Dictionary<ulong, Entry> _entries = new();
    private static readonly List<ulong> _stale = new();
    private static long _nextPump, _nextLog, _logAt;
    private static int _lastCount;

    /// <summary>Read the game's live list and remember it. Called from the beacon's tick so the
    /// picker has something to offer the moment it opens — a list built only on that open would
    /// hold exactly one frame's arm's-length neighbours and nothing walked past.</summary>
    public static void Pump()
    {
        long now = Environment.TickCount64;
        if (now < _nextPump) return;
        _nextPump = now + PumpMs;
        Sample(now);
    }

    /// <summary>Read it now, regardless of the clock: the picker is opening and an answer it can
    /// hold is worth more than one it can schedule.</summary>
    private static void Refresh() => Sample(Environment.TickCount64);

    private static void Sample(long now)
    {
        try
        {
            var mgr = WorldTourStateService.GetAvatarManager();
            var list = AvatarFieldReader.GetAccessInfoList(mgr);
            int count = FlowHelper.GetListCount(list);
            Expire(now);
            // Diagnostics, not decoration: this reported "0 remembered" for a whole session
            // while the field plainly had things to interact with, and the count alone could
            // not say whether the list was empty or whether every entry was being dropped.
            int noTarget = 0, noPosition = 0;
            for (int i = 0; i < count; i++)
            {
                if (_entries.Count >= MaxEntries) break;
                var access = FlowHelper.GetListItem(list, i);
                if (access == null) continue;
                var info = AvatarFieldReader.GetProp(access, "TargetInfo");
                var target = AvatarFieldReader.GetProp(info, "Target") as ManagedObject;
                if (target == null) { noTarget++; continue; }

                var (x, y, z, ok) = AvatarFieldReader.ReadWorldPos(target);
                // An exact origin is a failed read, not an object at the world's centre.
                if (!ok || x == 0f && y == 0f && z == 0f) { noPosition++; continue; }

                var named = NameOf(target);
                _entries[target.GetAddress()] = new Entry(named.Name, x, y, z, now, named.Named);
            }
            Log(now, count, noTarget, noPosition);
        }
        catch { }
    }

    /// <summary>The game's own name for what this is, falling back to the crowdsourced leave-me-
    /// anonymous word. Three readings rather than one because <c>GetDispName</c> lives on
    /// differing concrete subtypes and answers empty for a great many of them.</summary>
    private static (string Name, bool Named) NameOf(ManagedObject target)
    {
        string name = FlowHelper.CleanTags(FlowHelper.Call(target, "GetDispName") as string)?.Trim();
        if (!string.IsNullOrEmpty(name)) return (name, true);

        // The component-walk <see cref="AvatarFieldReader.Classify"/> already makes: the target
        // component's own GameObject frequently carries a second one that does answer.
        var (fallback, _) = AvatarFieldReader.Classify(target);
        // Stored UNNUMBERED: the number depends on where everything stands when the list is
        // built, and is put on in AddTo. The bare word is what this object IS.
        return string.IsNullOrEmpty(fallback)
            ? (ExplorationText.Get("interactable", "Interactable object"), false)
            : (fallback, true);
    }

    private static void Expire(long now)
    {
        _stale.Clear();
        foreach (var pair in _entries)
            if (now - pair.Value.SeenAt > LifetimeMs) _stale.Add(pair.Key);
        foreach (ulong key in _stale) _entries.Remove(key);
    }

    /// <summary>Everything remembered near enough to be worth walking to.</summary>
    public static void AddTo(List<Destination> into, float px, float pz)
    {
        Refresh();
        float rangeSq = OfferRangeM * OfferRangeM;
        var near = new List<Entry>(_entries.Count);
        foreach (var pair in _entries)
        {
            var e = pair.Value;
            float dx = e.X - px, dz = e.Z - pz;
            if (dx * dx + dz * dz > rangeSq) continue;
            near.Add(e);
        }
        // Nearest first, and the anonymous ones under the number that belongs to the OBJECT
        // rather than to this moment — number 7 is number 7 tomorrow as well, which is what
        // lets the player know whether this one has been done already.
        near.Sort((a, b) => FlatSq(a, px, pz).CompareTo(FlatSq(b, px, pz)));
        foreach (var e in near)
        {
            string name = e.Named ? e.Name
                : ExplorationText.InteractableNumbered(InteractableIds.Number(WorldTourStateService.CityId, e.X, e.Y, e.Z));
            into.Add(new Destination(name, null, DestinationCategory.Access, e.X, e.Y, e.Z));
        }
    }

    private static float FlatSq(Entry e, float px, float pz)
    {
        float dx = e.X - px, dz = e.Z - pz;
        return dx * dx + dz * dz;
    }

    /// <summary>What is being remembered, rate-limited: the tester cannot see a screen, so if the
    /// picker offers two things where a sighted player would see twenty, this is the line that
    /// says whether nothing was found or nothing survived the naming.</summary>
    private static void Log(long now, int inRange = 0, int noTarget = 0, int noPosition = 0)
    {
        if (now < _nextLog) return;
        _nextLog = now + LOG_MS;
        if (_entries.Count == _lastCount && now - _logAt < 4 * LOG_MS) return;
        _lastCount = _entries.Count;
        _logAt = now;

        var names = new List<string>(Math.Min(_entries.Count, 12));
        foreach (var pair in _entries)
        {
            if (names.Count >= 12) break;
            names.Add(pair.Value.Name);
        }
        API.LogInfo(string.Format(CultureInfo.InvariantCulture,
            "[SF6Access] Interactables: {0} remembered - {1} (in the game's list right now {2}, " +
            "of which {3} carried no target and {4} no readable position)",
            _entries.Count, string.Join(" / ", names), inRange, noTarget, noPosition));
    }

    /// <summary>Another field, another city, another save: none of these objects are here any
    /// more, whatever their positions were.</summary>
    public static void Reset()
    {
        _entries.Clear();
        _stale.Clear();
        _nextPump = 0;
        _lastCount = 0;
    }
}
