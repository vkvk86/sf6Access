namespace SF6Access.Services.WorldTour;

/// <summary>Mod descriptions for geometry and availability. Native prompt text wins when present.</summary>
internal static class ExplorationText
{
    internal static string Action(string name) => name switch
    {
        "Ladder_Up" or "Ladder_Up_Air" => Get("climb_up", "Climb up"),
        "Ladder_Down" => Get("climb_down", "Climb down"),
        "Ladder_UpDown" => Get("climb_both", "Climb up or down"),
        "Ladder_Slide" => Get("slide", "Slide down"),
        "Ladder_Catch" => Get("catch", "Grab ladder"),
        "Climb" => Get("climb", "Climb"),
        "GetOver" => Get("get_over", "Climb over"),
        "Open" => Get("open", "Open"),
        "Close" => Get("close", "Close"),
        "EnterShop" => Get("enter_shop", "Enter shop"),
        "GetOutCity" => Get("leave_city", "Leave city"),
        "Open_TreasureChest" => Get("chest", "Open chest"),
        "FindOut" => Get("examine", "Examine"),
        "StartUp" => Get("activate", "Activate"),
        "GetItem" => Get("get_item", "Pick up item"),
        _ => null,
    };
    internal static string Get(string key, string fallback) => LangFile.Get("wt.explore." + key, fallback);

    /// <summary>The name to speak for an object the game itself will not name.
    ///
    /// <para>The owner's rule, 2026-10-04: everything on the map goes into the list, and what has
    /// no name answers to a NUMBER instead. The number is given once per object and never moves
    /// (<see cref="InteractableIds"/>), so "可交互物 7" is the same machine tomorrow and can be
    /// recognised as already done — an earlier attempt numbered by list position instead, and
    /// that number reshuffled every time the player walked three metres. Unlike the level
    /// author's own object name ("gm 025000"), a number can also never be mistaken for a name
    /// the game gave us, and it still lets two machines two metres apart be told apart.</para></summary>
    internal static string InteractableNumbered(int n) =>
        string.Format(Get("interactable_no", "Interactable object {0}"), n);
    internal static string Nearby(string word) => string.Format(Get("nearby", "Near {0}. Interaction not confirmed"), word);
    internal static string Available(string word) => string.Format(Get("available", "Available: {0}"), word);
    internal static string RouteLost() => Get("route_lost", "Route unavailable. The sound points directly toward the destination");
    internal static string RouteRestored() => Get("route_restored", "Following the walking route");
    internal static string Approach() => Get("approach", "Nearby point only. An entrance to the objective has not been found");
}
