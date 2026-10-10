#nullable enable
using System.Collections.Generic;

namespace SF6Access.Services.WorldTour;

/// <summary>The destination picker's cursor: categories of places, each sorted nearest-first.</summary>
public sealed class DestinationPicker
{
    public readonly record struct Group(DestinationCategory Category, IReadOnlyList<Destination> Items);

    private List<Group> _groups = new();
    private int _group, _item;

    public bool IsEmpty => _groups.Count == 0;

    public bool Has(DestinationCategory category) => _groups.Exists(g => g.Category == category);
    public DestinationCategory Category => _groups[_group].Category;
    public Destination Current => _groups[_group].Items[_item];
    public int Index => _item;
    public int Count => _groups[_group].Items.Count;

    /// <summary>Groups the entries by category (in enum order), drops entries that repeat
    /// one already kept within <paramref name="mergeM"/>, and sorts each group
    /// nearest-first with its position-less actions on top. Earlier entries win a merge,
    /// so callers list live sources before remembered ones.</summary>
    public static List<Group> Build(IEnumerable<Destination> entries, float px, float pz, float mergeM)
    {
        var byCategory = new SortedDictionary<DestinationCategory, List<Destination>>();
        foreach (var e in entries)
        {
            // A place whose position failed to read (exact origin) cannot be guided to — with the
            // one exception of an entry that never claimed a position at all (a history objective
            // the game no longer has a live object for, or the "save this spot" action): those are
            // listed and spoken, and choosing them explains itself. See DestinationAction.
            if (string.IsNullOrWhiteSpace(e.Name)) continue;
            if (e.Action == DestinationAction.Go && !e.HasPosition) continue;
            if (!byCategory.TryGetValue(e.Category, out var list))
                byCategory[e.Category] = list = new List<Destination>();
            if (!IsRepeat(list, e, mergeM)) list.Add(e);
        }

        var groups = new List<Group>();
        foreach (var (category, list) in byCategory)
        {
            // THE STORY'S OWN ORDER SURVIVES. Every other category is sorted nearest-first,
            // because for a list of shops that is the useful order. The mission history is the
            // one list whose sequence carries information — entry N was the objective before
            // entry N+1 — and re-sorting it by distance would shuffle the story into noise.
            if (category != DestinationCategory.MissionHistory)
                list.Sort((a, b) => SortKey(a, px, pz).CompareTo(SortKey(b, px, pz)));
            groups.Add(new Group(category, list));
        }
        return groups;
    }

    private static bool IsRepeat(List<Destination> kept, Destination e, float mergeM)
    {
        foreach (var k in kept)
            if (k.Name == e.Name && k.Action == e.Action &&
                (!e.HasPosition || k.FlatDistanceTo(e.X, e.Z) <= mergeM))
                return true;
        return false;
    }

    private static float SortKey(Destination d, float px, float pz) =>
        d.HasPosition ? d.FlatDistanceTo(px, pz) : -1f;

    /// <summary>Starts on <paramref name="preferred"/> when it has entries, else on the first group.</summary>
    public void Load(List<Group> groups, DestinationCategory preferred)
    {
        _groups = groups ?? new List<Group>();
        _group = System.Math.Max(0, _groups.FindIndex(g => g.Category == preferred));
        _item = 0;
    }

    public void MoveItem(int step)
    {
        if (IsEmpty) return;
        _item = Wrap(_item + step, Count);
    }

    public void MoveCategory(int step)
    {
        if (IsEmpty) return;
        _group = Wrap(_group + step, _groups.Count);
        _item = 0;
    }

    private static int Wrap(int value, int count) => ((value % count) + count) % count;
}
