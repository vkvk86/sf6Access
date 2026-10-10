#nullable enable
namespace SF6Access.Services.WorldTour;

/// <summary>The destination picker's categories, in the order it cycles them.
/// <see cref="MissionHistory"/> sits right after <see cref="Mission"/> because it is the same kind
/// of thing — the story's objective — with the difference that the entry above it is the one being
/// followed NOW and this one holds the ones that came before.
///
/// <para><see cref="Lift"/> sits immediately before <see cref="Access"/> because it is the same
/// kind of thing too — the way up — and it is the one entry the player asked to be able to reach
/// without hunting through two hundred numbered objects.</para>
///
/// <para><see cref="Rest"/> holds the rest stations (休息站). They are facilities — the phone map
/// files them with the shops — but they are the one facility asked for by name, and they are
/// remembered across cities, so they get a category of their own rather than a place in a list of
/// eight shops. See <see cref="RestCatalog"/>.</para></summary>
public enum DestinationCategory { Mission, MissionHistory, Places, Rest, Sections, Lift, Access, Shops, Masters, Missions, Jobs, People, Pins, Saved }

/// <summary>What choosing an entry does: guide to it, hand guidance back to the story, save the
/// spot, or — for a history entry the player cannot be walked to from here — say which of the two
/// reasons it is: the game keeps no position for it, or it belongs to another city.</summary>
public enum DestinationAction { Go, FollowMission, SaveHere, NowhereKnown, OtherCity }

/// <summary>A named world point the player can choose to be guided to.</summary>
public readonly record struct Destination(
    string Name, string? Kind, DestinationCategory Category, float X, float Y, float Z,
    DestinationAction Action = DestinationAction.Go, MapSelectionMemory.SelectionKind AccessKind = MapSelectionMemory.SelectionKind.Point)
{
    /// <summary>"Beat Square, fast travel" — name plus the kind word when there is one.</summary>
    public string Label => string.IsNullOrEmpty(Kind) ? Name : Name + ", " + Kind;

    /// <summary>Whether the entry carries a world position; an exact origin means "none".</summary>
    public bool HasPosition => Action == DestinationAction.Go && (X != 0f || Y != 0f || Z != 0f);

    public float FlatDistanceTo(float x, float z)
    {
        float dx = X - x, dz = Z - z;
        return (float)System.Math.Sqrt(dx * dx + dz * dz);
    }
}
