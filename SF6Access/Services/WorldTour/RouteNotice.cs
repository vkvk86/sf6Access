using System;

namespace SF6Access.Services.WorldTour;

/// <summary>Route states for the destination beacon and the sentence spoken when the state changes.</summary>
internal static class RouteNotice
{
    /// <summary>A walk worth calling a detour: the path is at least this many times the straight
    /// line to the same place. Two is a route that doubles back or rounds a whole block.</summary>
    private const float MinDetourRatio = 2f;

    /// <summary>...and this many metres longer in absolute terms. A 6 m path where 3 m would do
    /// is a corner, not a detour, and naming it would make the word mean nothing.</summary>
    private const float MinDetourExtraM = 20f;

    /// <summary><b>Is the walk over, or is there still a stand to walk to?</b> The one precedence
    /// question in <see cref="State"/>. The rule itself lives in <see cref="WalkEnd"/>, where it is
    /// proved by the tests; see that class for why an approach point outranks "no route".</summary>
    internal static bool WalkIsOver(bool noRoute, bool isApproach, bool atFoot, bool closest)
        => WalkEnd.WalkIsOver(noRoute, isApproach, atFoot, closest);

    internal static string State(in BeaconDestination d, bool near) =>
        d.Searching ? "searching"
        : d.AccessKind != MapSelectionMemory.SelectionKind.Point
            ? (near ? "access-near:" : "access:") + d.AccessKind
        : d.Stair ? (near ? "stair-near:" : "stair:") + d.Leg
        : d.Descent ? (near ? "descent-near:" : "descent:") + d.Leg
        : d.Lift ? (near ? "lift-near:" : "lift:") + d.Leg
        : d.Jump ? (near ? "jump-near:" : "jump:") + d.Leg
        : d.Ladder ? (near ? "ladder-near:" : d.Blocked && !d.ViaRoute ? "ladder-blocked:" : "ladder:") + d.Leg
        : d.Drop ? (near ? "drop-near" : "drop")
        : d.Round ? (near ? "round-near" : "round")
        : d.RelayHop ? (near ? "relay-near" : "relay")
        : d.Closest ? ((near && d.IsApproach) || d.AtFoot ? "closest-near" : "closest")
        : d.IsApproach ? (near || d.AtFoot ? "approach-near" : "approach")
        : WalkIsOver(d.NoRoute, d.IsApproach, d.AtFoot, d.Closest) ? "no-route"
        : !d.ViaRoute ? "unavailable"
        : near ? "near" : "route";

    internal static bool IsNear(string state) => state is "near" or "approach-near" or "closest-near" or "relay-near"
        || state?.StartsWith("ladder-near:") == true || state?.StartsWith("access-near:") == true
        || state?.StartsWith("jump-near:") == true || state?.StartsWith("lift-near:") == true
        || state?.StartsWith("descent-near:") == true
        || state == "drop-near" || state == "round-near";

    /// <summary><b>True when the walk is over and the remaining metres belong to the player.</b>
    /// Nothing walkable connects the stand they are on to the objective: no route, no ladder that
    /// reaches, no relay hop found.
    ///
    /// <para>This does NOT mute the bearing or the cue. It did, for one build, and the owner's
    /// answer was "没有雷达让我怎么找": a blind player with the sound switched off has nothing
    /// left, and a bearing that ends at a wall is still the answer to "which way is it". All this
    /// flag does is add the missing fact to the sentence — see <see cref="EndHint"/>.</para></summary>
    internal static bool AtEndOfWalk(in BeaconDestination d)
        => (d.AtFoot || d.Closest) && !d.RelayHop;

    /// <summary><b>The handful of words that ride ALONGSIDE the bearing when the walk is over.</b>
    /// Short on purpose — it is a suffix to "the objective is at 10 o'clock, 3 meters", not a
    /// replacement for it — and it names what to look for, because "you cannot get there" with no
    /// follow-up is the sentence that leaves someone circling a hoarding.</summary>
    internal static string EndHint(in BeaconDestination d)
    {
        float rise = d.AboveM;
        if (rise <= -LevelRoute.LevelM)
            return string.Format(Get("hint_down", "no walk reaches it: it is {0} meters below you, look for an edge to drop from"),
                                 (int)MathF.Round(-rise));
        if (rise >= LevelRoute.LevelM)
            return string.Format(Get("hint_up", "no walk reaches it: it is {0} meters above you, look for a ledge or a ladder"),
                                 (int)MathF.Round(rise));
        return Get("hint_blocked",
            "no walk reaches it: something a map cannot show stands between, look for an entrance or something a master action can break");
    }

    /// <summary><b>The ride-along line for the near-but-not-interactable limbo.</b> The objective
    /// is inside the arrival band, the game's own interactable list does not hold it, and the walk
    /// is over as far as any route can tell — but unlike <see cref="AtEndOfWalk"/> the engine here
    /// may still CLAIM a path (a snap-artifact route that the wall sonar contradicts), so the
    /// end-of-walk hint never fired and the player was left with a bare bearing through a wall
    /// (2026-10-02 22:38, the SiRN building site: two metres, 交互未确认, and nothing else, for
    /// over a minute). This says what to DO about it, not "you cannot": the wall sonar is already
    /// reporting the wall itself, and the bearing keeps pointing at the objective.</summary>
    internal static string ContactHint()
    {
        // A wall MEASURED between the player and the objective (WayRound) beats "if a wall stands
        // between you": the distance stops them pushing it, and the open side tells them where the
        // way round starts. Without a measurement the old sentence is still the honest one.
        if (WayRound.HasWall)
        {
            string wall = string.Format(Get("wall_between",
                "A wall stands between you, {0} meters on"),
                // Never "zero meters": rounding a contact half a metre away to nothing would tell
                // the player the wall is not there, which is the one thing it must not say.
                Math.Max(1, (int)MathF.Round(WayRound.FrontM)));
            if (WayRound.SideM <= 0f) return wall;
            return wall + string.Format(Get("wall_round",
                ". Go round to the {0}: {1} meters of open ground that way"),
                WayRound.SideIsRight ? Get("side_right", "right") : Get("side_left", "left"),
                (int)MathF.Round(WayRound.SideM));
        }
        return Get("contact_hint",
            "The interaction has not confirmed. If a wall stands between you, follow it round and look for a door or an entrance on another side");
    }

    /// <summary><b>The leg is there, but no walk reaches it.</b>
    ///
    /// <para>2026-10-07, 17:22. A flight of stairs stood 42 metres away and the words said
    /// <c>Found a way down: 42 meters on, there is a flight of stairs</c>. The player walked and met
    /// a wall. The navmesh had already said why: <c>PathNotFound — the destination IS a valid
    /// navmesh node, but no walkable link reaches it from the player's node: it is on a
    /// DISCONNECTED piece of mesh</c>. The stairs were real, the "way down" was not: the 42 metres
    /// was the straight line, because there was no path length to quote.</para>
    ///
    /// <para>So the sentence changes shape rather than going quiet. It still names what stands
    /// there and how far it goes — the player asked for that proof and it is not withdrawn — but it
    /// says the walk to it does not exist, and it stops calling it a way down.</para></summary>
    private static string Unreachable(in BeaconDestination d, float meters)
        => string.Format(Get("unreachable",
               "No walk reaches it: straight-line {0} meters away there is {1}{2}, but nothing you can walk gets to it from here"),
               meters, LegWord(d), LegDepth(d));

    /// <summary>What kind of connection this leg is, in words the player hears.</summary>
    private static string LegWord(in BeaconDestination d)
        => d.Lift ? LiftWord() : d.Descent ? DropWord() : d.Stair ? StairWord()
         : d.Jump ? JumpWord() : LadderWord();

    /// <summary>How far that connection goes, when it was measured: the whole point of naming it.</summary>
    private static string LegDepth(in BeaconDestination d)
        => d.Stair ? string.Format(Get("unreachable_of", " going down {0} meters"), Round(d.StairM))
         : d.Descent ? string.Format(Get("unreachable_of", " going down {0} meters"), Round(d.DescentM))
         : string.Empty;

    /// <summary><b>A plan the planner itself marked as not arriving is not "a way down".</b>
    ///
    /// <para>2026-10-07, 17:13, Metro City training centre. The planner wrote "partial plan" and
    /// "no full route after 23 questions" to its own log — link #3's lower end reached the
    /// objective and nothing reached link #3 — and what the player heard was
    /// <c>Found a way down: 11 meters on there is a flight of stairs going down 5 meters</c>.
    /// They walked it. Four metres of height were still missing at the bottom and a wall stood
    /// across the way on.</para>
    ///
    /// <para>The sentence was honest about the stairs and a lie about the route: "a way down"
    /// promises an arrival, and the planner had already said there wasn't one. So a leg on a
    /// partial plan gets its own sentence, one that says what it DOES do — gets them nearer —
    /// and what it does not. Only the partial case is touched; a plan that actually reaches
    /// still says "found a way down", because there it is true.</para></summary>
    private static string NotAWay(in BeaconDestination d, float meters)
        => string.Format(Get("partial_lead",
               "This does not reach the objective: {0} meters on there is {1}{2}, which gets you nearer but leaves {3} meters of height still to close. The sound points at it, because it is the nearest way down there is"),
               meters, LegWord(d), LegDepth(d), Round(d.ShortM));

    /// <summary>What to say on entering <paramref name="state"/>, or null to stay quiet.</summary>
    internal static string Text(string state, string previous, in BeaconDestination d, float distance, float playerY)
    {
        int meters = Math.Max(1, (int)MathF.Round(d.RouteM > 0 ? d.RouteM : distance));
        int previousLeg = LegOf(previous);
        // NO WALK REACHES THIS LEG. Before anything says "found a way down", and only while the
        // player is still walking toward it — once they are ON it ("-near") the walk evidently
        // happened and the ordinary arrival words are the true ones. Once per leg, like the rest:
        // a sentence repeated every tick is noise, not guidance.
        if (d.AimUnreachable && !IsNear(state))
            return previous == state ? null : Unreachable(d, meters);
        // A LEG ON A PLAN THAT DOES NOT ARRIVE. Before it is called a way down — see NotAWay.
        // Only while the player is still walking toward it: once they are ON it ("-near") the
        // walk plainly happened and the ordinary arrival words are the true ones.
        if (d.Partial && !IsNear(state)
            && (state.StartsWith("stair:") || state.StartsWith("descent:")
                || state.StartsWith("lift:") || state.StartsWith("jump:")))
            return previous == state ? null : NotAWay(d, meters);
        // The destination is the ladder or door itself: on arrival say how to use it, so the
        // player is not left circling it. Reuses the words the ladder-leg arrival already has.
        if (state.StartsWith("access-near:"))
        {
            var kind = (MapSelectionMemory.SelectionKind)int.Parse(state.AsSpan("access-near:".Length));
            return kind switch
            {
                MapSelectionMemory.SelectionKind.LadderUp => Get("at_ladder_up", "Ladder here. Face it and press the action button to climb up"),
                MapSelectionMemory.SelectionKind.LadderDown => Get("at_ladder_down", "Ladder here. Face it and press the action button to climb down"),
                MapSelectionMemory.SelectionKind.Door => Get("at_door", "Door here. Face it and press the action button to open it"),
                _ => null,
            };
        }
        // --- a flight of stairs: the leg the player simply walks up (or, since 2026-10-05,
        // down — the same connection read the other way) ---
        // The height is spoken for the same reason the descent's is: a flight that drops one and
        // a half metres and a flight that drops nine are not the same instruction, and until
        // 2026-10-07 the guide used one sentence for both. Asked to prove there were stairs here,
        // the proof is not a yes — it is the number.
        if (state.StartsWith("stair-near:"))
            return d.StairDown
                ? string.Format(Get("stair_near_down", "Here: this is the stairs. It goes down {0} meters, walk down them carefully"), Round(d.StairM))
                : string.Format(Get("stair_near", "Here: this is the stairs. It goes up {0} meters, walk up it"), Round(d.StairM));
        if (state.StartsWith("stair:"))
        {
            if (previous == state) return null;
            return d.StairDown
                ? Short(string.Format(Get("stair_step_down",
                      "Found a way down: {0} meters on, there is a flight of stairs going down {1} meters. Walk down it and carry on"), meters, Round(d.StairM)), d)
                : Short(string.Format(Get("stair_step",
                      "Found a way up: {0} meters on, there is a flight of stairs going up {1} meters. Walk up it and carry on"), meters, Round(d.StairM)), d);
        }
        // --- a descent: the ground simply falls away, and stepping off it IS the connection.
        // Added 2026-10-07. The height is spoken because it is measured, and because "a long way"
        // tells someone who cannot see it nothing at all about what they are stepping into.
        if (state.StartsWith("descent-near:"))
            return string.Format(Get("descent_near",
                "Here: the ground falls away {0} meters. Step off and land below"), Round(d.DescentM));
        if (state.StartsWith("descent:"))
        {
            if (previous == state) return null;
            return string.Format(Get("descent_step",
                "Found a way down: {0} meters on, the ground falls away {1} meters. Step off it, land below, then carry on"),
                meters, Round(d.DescentM));
        }
        // --- a lift: stand still and let the platform carry you (2026-10-07) ---
        if (state.StartsWith("lift-near:"))
            return d.LiftDown ? Get("lift_near_down", "Here: this is the lift. Stand on it and it will take you down")
                              : Get("lift_near_up", "Here: this is the lift. Stand on it and it will take you up");
        if (state.StartsWith("lift:"))
        {
            if (previous == state) return null;
            return d.LiftDown
                ? string.Format(Get("lift_step_down", "Found a way down: {0} meters on, there is a lift. Stand on it and it will take you down, then carry on"), meters)
                : string.Format(Get("lift_step_up", "Found a way up: {0} meters on, there is a lift. Stand on it and it will take you up, then carry on"), meters);
        }
        // --- a crossing: the one leg where the player's hands finish the route ---
        if (state.StartsWith("jump-near:"))
            return d.JumpUp ? Get("jump_near_up", "Here: this is the step. Get up onto it")
                            : Get("jump_near_gap", "Here: this is the gap. Jump across it");
        if (state.StartsWith("jump:"))
        {
            if (previous == state) return null;
            return d.JumpUp
                ? string.Format(Get("jump_step", "Found a way over: {0} meters on, there is a step to get up onto, then carry on"), meters)
                : string.Format(Get("jump_gap", "Found a way over: {0} meters on, there is a gap to jump across, then carry on"), meters);
        }
        if (state.StartsWith("ladder-near:"))
        {
            int rung = (int)MathF.Round(d.BottomRungM);
            if (rung >= 1) return string.Format(Get("at_ladder_high", "Ladder here. It starts {0} meters above the ground: jump toward it to grab it"), rung);
            return d.LadderUp ? Get("at_ladder_up", "Ladder here. Face it and press the action button to climb up")
                              : Get("at_ladder_down", "Ladder here. Face it and press the action button to climb down");
        }
        if (state.StartsWith("ladder-blocked:"))
            return previousLeg == d.Leg ? null
                : string.Format(Get("blocked_ladder", "The next ladder is {0} meters away, but there is no clear way to it: look for something to break with a master action, or a jump. The sound points straight at it"), meters);
        if (state.StartsWith("ladder:"))
        {
            string detour = Detour(d);
            if (previous?.StartsWith("ladder-blocked:") == true && previousLeg == d.Leg)
                return string.Format(Get("path_open", "The way to the ladder is open, {0} meters"), meters) + detour;
            if (previousLeg == d.Leg) return null;
            if (previousLeg > d.Leg) return string.Format(Get("back_to_ladder", "Back to the ladder, {0} meters away"), meters) + detour;
            if (previousLeg >= 0) return string.Format(Get("next_ladder", "Ladder done. Next ladder {0} meters away"), meters) + detour;
            if (d.Partial)
                return (d.LaddersLeft > 1
                    ? string.Format(Get("partial_many", "No ladder reaches the destination, but {0} ladders take you closer. The first is {1} meters away"), d.LaddersLeft, meters)
                    : string.Format(Get("partial_one", "No ladder reaches the destination, but one takes you closer. It is {0} meters away"), meters)) + detour;
            return (d.LaddersLeft > 1
                ? string.Format(Get("found_many", "Route found through {0} ladders. The first is {1} meters away"), d.LaddersLeft, meters)
                : string.Format(Get("found_one", "Route found through a ladder, {0} meters away"), meters)) + detour;
        }
        if (previousLeg >= 0 && (state == "route" || state == "near"))
            return string.Format(Get("leg_done", "Ladder done. Continue to {0}, {1} meters"), d.Word, meters) + Detour(d);
        bool afterSearch = previous == "searching";
        return state switch
        {
            "searching" => Searching(d.TargetY - playerY),
            "no-route" when afterSearch => Get("search_failed", "No ladder found that reaches the destination"),
            "no-route" => LocalizedText.AimNoRoute(d.Word, meters),
            "unavailable" => ExplorationText.RouteLost(),
            "approach" => ExplorationText.Approach(),
            "approach-near" when d.AboveM >= LevelRoute.LevelM => AtFoot(d),
            "approach-near" => ExplorationText.Get("approach_near", "Near the approach point. The objective still needs an entrance"),
            // A DROP: the walk reaches the edge of the floor the objective stands above, and the
            // fall is the part no map can route. Named with its bearing, its distance and its
            // depth, because "look for an edge" with no numbers is not an instruction.
            "drop" => string.Format(Get("drop",
                "Found a way down: {0} meters on, the floor ends. Drop about {1} meters and you are on the level it stands on"),
                meters, (int)MathF.Round(d.DropM)),
            "drop-near" => string.Format(Get("drop_near",
                "Here: the edge. Drop about {0} meters"), (int)MathF.Round(d.DropM)),
            // A relay: the nearest stand is reached, but somewhere else can be walked to and is
            // nearer. Say so in those words — "no way in" would be a lie, and silence would lose
            // the player at the exact moment the route changes shape.
            // A WAY ROUND: not advice about a wall, but a ROW OF PLACES TO WALK, every hop of them
            // measured with the avatar's own collision filter so nothing stands across any of it.
            // "Follow the wall round" asked a blind player to guess where the wall ends; this says
            // how many steps it took to get past it and that there is a way past it at all.
            "round" when previous == "round" || previous == "round-near" => null,
            "round" => RoundPlanner.ReachesGoal
                ? string.Format(Get("round_found",
                    "Found the way round: {0} meters of open ground, measured clear. Follow it"), meters)
                : string.Format(Get("round_partial",
                    "Found open ground round it: {0} meters on, nearer than anywhere else. Follow that"), meters),
            "round-near" => null,
            "relay" => string.Format(Get("relay_hop",
                "No walk reaches the destination from here: going {0} meters further on, to ground that gets you nearer"), meters),
            "relay-near" => Get("relay_hop_near", "Here: another step from this spot"),
            "near" => ExplorationText.Nearby(d.Word),
            "closest" when previous == "closest-near" => null,
            "closest" => Closest(d.AboveM),
            "closest-near" when d.AboveM >= LevelRoute.LevelM => AtFoot(d),
            "closest-near" => ExplorationText.Get("approach_near", "Near the approach point. The objective still needs an entrance"),
            "route" when previous is "no-route" or "unavailable" or "approach" => ExplorationText.RouteRestored(),
            _ => null,
        };
    }

    internal static string LadderWord() => Get("ladder", "ladder");

    /// <summary>What the beacon is walking the player to when the leg ends in a crossing.</summary>
    internal static string JumpWord() => Get("jump", "the crossing");

    /// <summary>What the beacon is walking the player to when the leg ends at a flight of
    /// narrow stairs: they walk up it, with no action button, which is exactly why it is not
    /// called a ladder.</summary>
    internal static string StairWord() => Get("stair", "the stairs");

    /// <summary>What the beacon is walking the player to when the leg ends on a lift platform
    /// (2026-10-07): they stand still and ride, which is neither a climb nor a walk.</summary>
    internal static string LiftWord() => Get("lift", "the lift");

    /// <summary>What the beacon is walking the player to when the leg ends at a place where the
    /// ground falls away (2026-10-07): a drop measured by rays, because it is not an object and
    /// so has no name of its own in the world.</summary>
    internal static string DropWord() => Get("descent", "a drop");

    internal static string LadderOffered() => Get("ladder_offered", "You can grab the ladder now");

    private static int Round(float m) => (int)Math.Round(m, MidpointRounding.AwayFromZero);

    /// <summary><b>A leg that does not arrive must say so.</b> 2026-10-07: the planner wrote
    /// "partial plan" and "no full route" to its own log and the player heard "Found a way down".
    /// The words were not wrong about the stairs — they were wrong about the ROUTE. So the shortfall
    /// is spoken, as a number, appended to whatever the leg would otherwise have said: the guide
    /// keeps talking, and it tells the truth about how far this way actually goes.</summary>
    private static string Short(string text, in BeaconDestination d)
    {
        if (d.ShortM < LevelRoute.LevelM) return text;
        return text + string.Format(Get("partial_short",
            " But this only goes part of the way — {0} meters of height are still missing when it ends, so it does not reach the objective"),
            Round(d.ShortM));
    }

    /// <summary>What the beacon is heading for: an approach point is the foot of the destination, not the
    /// destination.</summary>
    internal static string Heading(in BeaconDestination d)
        => d.IsApproach && !d.AtFoot ? string.Format(Get("foot_of", "foot of {0}"), d.Word) : d.Word;

    private static string AtFoot(in BeaconDestination d)
        => string.Format(Get("at_foot", "At the foot of {0}. It is {1} meters above you. The sound now points at it: walk toward it looking for a way up"),
                         d.Word, (int)MathF.Round(d.AboveM));

    /// <summary><b>When the walk to the next aim point goes a long way round, SAY so.</b> A blind
    /// player told "the next ladder is 90 meters away" about a destination 25 m off in a straight
    /// line is being sent round something the navmesh cannot model — a jump, a ledge, a breakable
    /// — and the straight-line number is the one thing that lets them decide for themselves
    /// whether to follow the walk or find the short way. Quiet when the walk is honest: an
    /// unqualified "90 meters" is then exactly right, and a warning that always fires teaches
    /// the player to stop listening to it.</summary>
    private static string Detour(in BeaconDestination d)
    {
        if (d.DetourRatio < MinDetourRatio) return "";
        int path = Math.Max(1, (int)MathF.Round(d.RouteM));
        int straight = Math.Max(1, (int)MathF.Round(d.StraightM));
        if (path - straight < MinDetourExtraM) return "";
        return " " + string.Format(Get("detour",
            "but it is only {0} meters away in a straight line: the walk goes a long way round"), straight);
    }

    internal static string SearchingNow() => Get("searching_now", "Searching for a route through ladders, one moment");

    /// <summary>While the search runs on, so a long search is not silence: how long it has been going.</summary>
    internal static string SearchingStill(int seconds) =>
        string.Format(Get("searching_still", "Still searching for a way up: {0} seconds"), seconds);

    /// <summary>When the search stops on its own budget: the player should know it gave up, not that it is stuck.</summary>
    internal static string SearchGaveUp() =>
        Get("searching_gave_up", "The search stopped without a full route. Leading you as far as it can");

    /// <summary>A partial route is climbed: how far the destination still is in height, and what may close the gap.</summary>
    private static string Closest(float rise)
    {
        if (MathF.Abs(rise) < LevelRoute.LevelM)
            return Get("closest_level", "The ladders go no nearer. Something blocks the way: look for something to break with a master action, or a jump");
        return string.Format(rise > 0 ? Get("closest_up", "The ladders go no higher. The destination is {0} meters above: look for a ledge to climb, a jump, or something to break with a master action")
                                      : Get("closest_down", "The ladders go no lower. The destination is {0} meters below: look for a place to drop down"),
                             (int)MathF.Round(MathF.Abs(rise)));
    }

    private static string Searching(float rise)
    {
        int meters = (int)MathF.Round(MathF.Abs(rise));
        if (MathF.Abs(rise) < LevelRoute.LevelM) return Get("searching_level", "No direct walk to the destination. Searching for a ladder");
        return string.Format(rise > 0 ? Get("searching_up", "The destination is {0} meters above you. Searching for a ladder")
                                      : Get("searching_down", "The destination is {0} meters below you. Searching for a ladder"), meters);
    }

    private static int LegOf(string state)
    {
        if (state == null || !state.StartsWith("ladder")) return -1;
        return int.TryParse(state.AsSpan(state.IndexOf(':') + 1), out int leg) ? leg : -1;
    }

    private static string Get(string key, string fallback) => LangFile.Get("wt.route." + key, fallback);
}
