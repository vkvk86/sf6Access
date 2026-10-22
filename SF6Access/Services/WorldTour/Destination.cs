#nullable enable
namespace SF6Access.Services.WorldTour;

/// <summary>The destination picker's categories, in the order it cycles them.
/// <see cref="MissionHistory"/> sits right after <see cref="Mission"/> because it is the same kind
/// of thing — the story's objective — with the difference that the entry above it is the one being
/// followed NOW and this one holds the ones that came before.
///
/// <para><see cref="KnownPlaces"/> holds the places the player can pick by NAME — the sonar list —
/// and comes from upstream's "places by name" work (<see cref="CityPlaceDirectory"/>).</para>
///
/// <para><see cref="Travel"/> is the fast-travel points (upstream), <see cref="Lift"/> the lifts
/// (ours): both are "the way somewhere else", listed before <see cref="Access"/> — the way up —
/// which is the one entry the player asked to be able to reach without hunting through two
/// hundred numbered objects.</para>
///
/// <para><see cref="Rest"/> holds the rest stations (休息站). They are facilities — the phone map
/// files them with the shops — but they are the one facility asked for by name, and they are
/// remembered across cities, so they get a category of their own rather than a place in a list of
/// eight shops. See <see cref="RestCatalog"/>.</para>
///
/// <para><see cref="Sections"/> holds the named districts the banner announces — neither landmarks
/// nor shops nor people (<see cref="SectionCatalog"/>).</para></summary>
public enum DestinationCategory { Mission, MissionHistory, KnownPlaces, Places, Rest, Travel, Sections, Lift, Access, Shops, Masters, Missions, Jobs, People, Pins, Saved }

/// <summary>What choosing an entry does: guide to it, hand guidance back to the story, save the
/// spot, explore freely or listen to the city (upstream) — or, for a history entry the player
/// cannot be walked to from here, say which of the two reasons it is: the game keeps no position
/// for it, or it belongs to another city.</summary>
public enum DestinationAction { Go, FollowMission, SaveHere, Explore, ListenCity, NowhereKnown, OtherCity }

public enum DestinationActorSource { None, Avatar, Registry }

/// <summary>A live actor's exact instance identity, copied without retaining native wrappers.</summary>
public readonly record struct DestinationActor(DestinationActorSource Source, ulong Address,
    ulong Owner, uint City, string Name, int Kind)
{
    public bool IsValid => Source != DestinationActorSource.None && Address != 0 && Owner != 0
        && City != 0 && !string.IsNullOrWhiteSpace(Name);
}

/// <summary>A named world point the player can choose to be guided to.</summary>
public readonly record struct Destination(
    string Name, string? Kind, DestinationCategory Category, float X, float Y, float Z,
    DestinationAction Action = DestinationAction.Go,
    MapSelectionMemory.SelectionKind AccessKind = MapSelectionMemory.SelectionKind.Point,
    DestinationActor Actor = default)
{
    /// <summary>"Beat Square, fast travel" — name plus the kind word when there is one.</summary>
    public string Label => string.IsNullOrEmpty(Kind) ? Name : Name + ", " + Kind;

    /// <summary>Whether the entry carries a world position; an exact origin means "none".
    /// An action that does not guide anywhere (save, explore, listen, or the two history
    /// verdicts) never has one, and a non-finite coordinate is a failed read, not a place.</summary>
    public bool HasPosition => Action is not (DestinationAction.SaveHere or DestinationAction.Explore
        or DestinationAction.ListenCity or DestinationAction.NowhereKnown or DestinationAction.OtherCity)
        && float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z)
        && (X != 0f || Y != 0f || Z != 0f);

    public float FlatDistanceTo(float x, float z)
    {
        float dx = X - x, dz = Z - z;
        return (float)System.Math.Sqrt(dx * dx + dz * dz);
    }
}
