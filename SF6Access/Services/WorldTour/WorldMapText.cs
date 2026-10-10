using SF6Access.Services;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// The world map's own vocabulary: how a city row and a mission row are spoken.
/// Everything the screen <i>names</i> (cities, countries, missions) arrives
/// already localized from the game, so the only strings supplied here are the
/// sentence shapes.
///
/// <para>Lives beside the map reader rather than in <see cref="LocalizedText"/>
/// for the same reason <see cref="DeviceMapText"/> does: it is the phrasing for
/// one screen, while the translations live in <c>SF6Access.lang\*.txt</c>.</para>
/// </summary>
public static class WorldMapText
{
    /// <summary>The list row's state enum, so a locked city says so instead of
    /// reading like any other stop.</summary>
    public const string ItemStateEnum = "app.UIFlowWorldMap.ItemState";

    /// <summary>The row's cursor enum — which row the selection is on.</summary>
    public const string CursorEnum = "app.UIPartsWorldMapSpotList.Cursor";

    /// <summary>"Metro City, 3 of 15, United States" — the city, its place in
    /// the list, and the country when the detail panel has caught up with the
    /// selection. Any part the game does not supply is left out.</summary>
    public static string Spot(string name, int index, int count, string country, string state)
    {
        var parts = new System.Collections.Generic.List<string>();

        string head = count > 1 && index >= 0 && index < count && !string.IsNullOrEmpty(name)
            ? string.Format(LangFile.Get("wt.worldmap.spot", "{0}, {1} of {2}"), name, index + 1, count)
            : name;
        if (!string.IsNullOrEmpty(head)) parts.Add(head);

        // The country comes from the detail panel, which the game updates a
        // moment AFTER the selection moves (SpotListSelectionChanged takes a
        // delay flag) — so it is only said once the panel names the same city.
        if (!string.IsNullOrEmpty(country)) parts.Add(country);
        if (!string.IsNullOrEmpty(state)) parts.Add(state);

        return parts.Count > 0 ? string.Join("，", parts) : null;
    }

    /// <summary>The word for a row's state — "locked", "new" — or null for an
    /// ordinary unlocked city, which needs no extra word.
    ///
    /// <para>Matched on the enum member NAME, never on its number, so a patch
    /// that renumbers the enum cannot make the map call an open city locked.
    /// LOCK is tested before UNLOCK because a name can carry both.</para></summary>
    public static string State(string enumName)
    {
        if (string.IsNullOrEmpty(enumName)) return null;
        string upper = enumName.ToUpperInvariant();
        if (upper.Contains("LOCK") && !upper.Contains("UNLOCK"))
            return LangFile.Get("wt.worldmap.locked", "locked");
        if (upper.Contains("NEW"))
            return LangFile.Get("wt.worldmap.new", "new");
        return null;
    }

    /// <summary>True for a row the game has not opened yet. A locked row's name
    /// is never spoken: the game leaves the label empty on those rows, and while
    /// the list scrolls a row control keeps the previous city's name for a poll
    /// or two — which is how a locked spot used to be announced as an unlocked
    /// one hundreds of miles away.</summary>
    public static bool IsLocked(string enumName)
    {
        if (string.IsNullOrEmpty(enumName)) return false;
        string upper = enumName.ToUpperInvariant();
        return upper.Contains("LOCK") && !upper.Contains("UNLOCK");
    }

    /// <summary>"5 tickets" — held tickets, said once on arrival. Reading it
    /// with every city made it sound like that city's price.</summary>
    public static string TicketsHeld(int tickets) =>
        tickets < 0 ? null
                    : string.Format(LangFile.Get("wt.worldmap.tickets", "{0} tickets"), tickets);

    /// <summary>A mission available at the selected city. Spoken on its own —
    /// it is a different list, reached with a different button.</summary>
    public static string Mission(string name) => name;
}
