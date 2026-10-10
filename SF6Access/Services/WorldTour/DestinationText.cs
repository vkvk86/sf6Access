using SF6Access.Services;

namespace SF6Access.Services.WorldTour;

/// <summary>Everything the destination picker and the "where am I" readout say.</summary>
public static class DestinationText
{
    private static float HeightBandM => Hooks.WorldTour.MissionBeaconHooks.ARRIVED_M;

    public static string CategoryName(DestinationCategory category) => category switch
    {
        DestinationCategory.Mission => LangFile.Get("wt.dest.cat_mission", "Story objective"),
        DestinationCategory.MissionHistory => LangFile.Get("wt.dest.cat_mission_history", "Earlier objectives"),
        DestinationCategory.Places => LangFile.Get("wt.dest.cat_places", "Places"),
        DestinationCategory.Rest => LangFile.Get("wt.dest.cat_rest", "Rest stations"),
        DestinationCategory.Sections => LangFile.Get("wt.dest.cat_sections", "Districts"),
        DestinationCategory.Lift => LangFile.Get("wt.dest.cat_lift", "Lifts"),
        DestinationCategory.Access => LangFile.Get("wt.dest.cat_access", "Ladders and doors nearby"),
        DestinationCategory.Shops => LangFile.Get("wt.dest.cat_shops", "Shops and services"),
        DestinationCategory.Masters => LangFile.Get("wt.dest.cat_masters", "Masters"),
        DestinationCategory.Missions => LangFile.Get("wt.dest.cat_missions", "Missions"),
        DestinationCategory.Jobs => LangFile.Get("wt.dest.cat_jobs", "Jobs"),
        DestinationCategory.People => LangFile.Get("wt.dest.cat_people", "People nearby"),
        DestinationCategory.Pins => LangFile.Get("wt.dest.cat_pins", "Map markers"),
        _ => LangFile.Get("wt.dest.cat_saved", "My places"),
    };

    /// <summary>"Shops and services, 12" — said when a category is entered.</summary>
    public static string CategoryLine(DestinationCategory category, int count) =>
        string.Format(LangFile.Get("wt.dest.category", "{0}, {1}"), CategoryName(category), count);

    /// <summary>"at 2 o'clock, 120 meters, 12 meters up" from the player to a point.</summary>
    public static string Where(float px, float py, float pz, float tx, float ty, float tz)
    {
        float dx = tx - px, dz = tz - pz, dy = ty - py;
        int meters = (int)System.Math.Round(System.Math.Sqrt(dx * dx + dz * dz));
        int hour = FieldDirectionService.ClockHour(FieldDirectionService.GetCameraForward(), dx, dz);
        string where = hour > 0
            ? LocalizedText.ClockShort(hour, meters)
            : string.Format(LangFile.Get("wt.dest.meters", "{0} meters"), meters);

        int height = (int)System.Math.Round(System.Math.Abs(dy));
        if (System.Math.Abs(dy) < HeightBandM) return where;
        return where + ", " + string.Format(dy > 0
            ? LangFile.Get("wt.dest.above", "{0} meters up")
            : LangFile.Get("wt.dest.below", "{0} meters down"), height);
    }

    /// <summary>"Beat Square, at 2 o'clock, 120 meters, 3 of 8".</summary>
    public static string Item(Destination d, string where, int index, int count) =>
        string.IsNullOrEmpty(where)
            ? string.Format(LangFile.Get("wt.dest.item_action", "{0}, {1} of {2}"), d.Label, index + 1, count)
            : string.Format(LangFile.Get("wt.dest.item", "{0}, {1}, {2} of {3}"), d.Label, where, index + 1, count);

    public static string Opened() => LangFile.Get("wt.dest.open", "Destinations");

    public static string Empty() => LangFile.Get("wt.dest.empty",
        "No destinations known yet. Open the phone map once so its places are learned");

    /// <summary>Said once per session while the list has only the basic places.</summary>
    public static string LearnHint() => LangFile.Get("wt.dest.learn_hint",
        "To add shops, masters and missions, open the phone map once");

    public static string Unavailable() => LangFile.Get("wt.dest.unavailable", "Destinations unavailable here");

    public static string Cancelled() => LangFile.Get("wt.dest.cancelled", "Destinations closed");

    /// <summary>"Destination: Beat Square, at 2 o'clock, 120 meters".</summary>
    public static string Chosen(string label, string where) =>
        string.Format(LangFile.Get("wt.dest.chosen", "Destination: {0}, {1}"), label, where);

    public static string FollowMission() =>
        LangFile.Get("wt.dest.follow_mission", "Following the story objective again");

    public static string MissionEntry() => LangFile.Get("wt.dest.mission_entry", "Story objective");

    /// <summary>One entry of the mission history: the mission's own title and its chapter — the
    /// story's own numbering, not a count of list rows, so an entry keeps the same number after
    /// another one is added ahead of it.
    ///
    /// <para>Every fallback still names something the player can tell apart. A mission whose title
    /// the game never published (a zone objective is a place, and a place has no name) used to come
    /// out as "剧情任务目标，第0章" — the same sentence for every one of them, and a chapter number
    /// that is really "unknown" spoken as if it were chapter zero. With an id it says the id, so
    /// two unnamed objectives are two different lines.</para></summary>
    public static string MissionHistoryEntry(string name, int chapter, uint id)
    {
        if (!string.IsNullOrWhiteSpace(name))
            // A chapter that was never read is 0, and "Beat Square, chapter 0" claims a chapter
            // that does not exist. The name alone is the honest sentence.
            return chapter > 0
                ? string.Format(LangFile.Get("wt.dest.mission_history_named", "{0}, chapter {1}"), name.Trim(), chapter)
                : name.Trim();
        if (chapter > 0)
            return string.Format(LangFile.Get("wt.dest.mission_history_unnamed", "Story objective, chapter {0}"), chapter);
        if (id != 0)
            return string.Format(LangFile.Get("wt.dest.mission_history_id", "Story objective {0}"), id);
        return LangFile.Get("wt.dest.mission_history_noname", "Story objective");
    }

    /// <summary>Chosen, but the game no longer has the objective's position: the entry is there to
    /// tell the player what the story has been through, not to be walked to.</summary>
    public static string HistoryNoPlace() => LangFile.Get("wt.dest.history_no_place",
        "That objective's place is no longer known - the game keeps no position for a finished mission");

    /// <summary>Chosen, and its position IS on file — but in the other city. The history is shown
    /// wherever the player is (a list that empties itself when they travel is worse than one that
    /// lists a place they must travel to), so this says why it cannot be walked to from here.</summary>
    public static string HistoryOtherCity() => LangFile.Get("wt.dest.history_other_city",
        "That objective is in another city - travel there and it can be guided to");

    public static string SaveHereEntry() => LangFile.Get("wt.dest.save_here", "Save this spot");

    public static string Saved(string name) =>
        string.Format(LangFile.Get("wt.dest.saved", "Saved: {0}"), name);

    public static string NotSaved() => LangFile.Get("wt.dest.not_saved", "This spot could not be saved");

    public static string Deleted(string name) =>
        string.Format(LangFile.Get("wt.dest.deleted", "Deleted: {0}"), name);

    /// <summary>What a scene object the registry leaves unnamed is called, taken from the
    /// flags its type exposes — so a rest bench, the player's own front door and a
    /// fast-travel point still reach the list under a name.</summary>
    public static string OmHome() => LangFile.Get("wt.dest.om_home", "home");

    public static string OmBodyShop() => LangFile.Get("wt.dest.om_bodyshop", "body shop");

    public static string OmFastTravel() => LangFile.Get("wt.dest.om_fast_travel", "fast travel point");

    /// <summary>What a rest station is called when the game has not named one for us in this
    /// language yet. The phone map's own word for these icons is learned the first time one is
    /// seen (<see cref="RestCatalog.Word"/>); this is the answer until then.</summary>
    public static string RestStation() => LangFile.Get("wt.dest.rest", "rest station");

    /// <summary>The kind word on a remembered station that stands in another city: "in Metro
    /// City", or "in another city" when the city's own name cannot be read.</summary>
    public static string RestOtherCity(string city) =>
        string.IsNullOrEmpty(city)
            ? LangFile.Get("wt.dest.rest_other_city", "in another city")
            : string.Format(LangFile.Get("wt.dest.rest_in_city", "in {0}"), city);

    /// <summary>Said when a remembered station in another city is chosen: it is not lost, it is
    /// simply in the city the player has to travel back to.</summary>
    public static string RestOtherCityChosen() => LangFile.Get("wt.dest.rest_other_city_chosen",
        "That rest station is in another city - travel there and it can be guided to");

    /// <summary>"Beat Square, spot 3" — the name a saved spot gets.</summary>
    public static string SavedName(string zoneName, int number) =>
        string.Format(LangFile.Get("wt.dest.saved_name", "{0}, spot {1}"),
                      string.IsNullOrEmpty(zoneName) ? LangFile.Get("wt.dest.spot", "Spot") : zoneName, number);
}
