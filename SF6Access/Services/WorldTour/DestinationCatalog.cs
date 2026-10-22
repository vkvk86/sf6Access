using System.Collections.Generic;
using SF6Access.Hooks.WorldTour;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// Gathers every place the player can be guided to, live sources first (story objective,
/// fast-travel points, avatars around the player, the game's map registry), then what the
/// phone map showed last and the player's saved spots.
/// </summary>
public static class DestinationCatalog
{
    /// <summary>Two sources naming the same thing this close together are one place.</summary>
    internal static float MergeM => 2f * MissionBeaconHooks.ARRIVED_M;

    /// <param name="people">False leaves out the crowd (avatars around the player and the
    /// registry's plain NPCs): the places-only view the look sweep and "passing by" use.</param>
    public static List<DestinationPicker.Group> Build(float px, float pz, bool people = true)
    {
        var all = new List<Destination>();

        var mission = MissionTargetService.Find();
        all.Add(new Destination(DestinationText.MissionEntry(), null, DestinationCategory.Mission,
            mission.Ok ? mission.X : 0f, mission.Ok ? mission.Y : 0f, mission.Ok ? mission.Z : 0f,
            DestinationAction.FollowMission));
        all.Add(new Destination(DestinationText.ExploreEntry(), null, DestinationCategory.Mission,
            0f, 0f, 0f, DestinationAction.Explore));
        all.Add(new Destination(DestinationText.ListenCityEntry(), null, DestinationCategory.Mission,
            0f, 0f, 0f, DestinationAction.ListenCity));

        // The objectives the story handed over BEFORE this one — the list the player asked for so
        // that turning a mission in does not erase where they were walking. The story's own mission
        // list is folded in first (see MissionHistory.Backfill), which is what makes the list go
        // all the way back rather than starting from the day this feature shipped.
        MissionHistory.Backfill();
        uint homeCity = WorldTourStateService.CityId;
        // VISIBLE, not merely "mine": when this city holds none of the history, the whole record is
        // shown instead of an empty list. The owner's requirement is that the earlier objectives
        // are there whenever they open the game, and a list that empties itself because the city id
        // changed is indistinguishable from the history having been lost.
        foreach (var h in MissionHistory.Visible(homeCity))
        {
            // The id goes into the label when the mission has no title, so two unnamed objectives
            // are two lines a player can tell apart instead of the same sentence twice.
            string label = DestinationText.MissionHistoryEntry(h.Name, h.Chapter, MissionHistory.MissionIdOf(h.Key));
            // NOTHING IS DROPPED. A mission whose target the game no longer has a live object for
            // has no coordinates to offer — the engine keeps none for a mission it has finished —
            // but it is still part of where the story has been, and a list that silently omits
            // half its own history is not the list the player asked for. Such an entry keeps a
            // position-less action, which is how the picker already carries the "everything you
            // cannot walk to" entries (the Save-this-spot one): it is listed and spoken, and
            // choosing it says so instead of aiming at the world origin.
            //
            // THE OBJECTIVE BEING FOLLOWED IS *NOT* SKIPPED, unlike the first version of this,
            // which did skip it and thereby emptied the category: with one objective known, the
            // one being followed, "history" held nothing at all and the category vanished from the
            // picker. The duplicate is worth having — the Mission entry above says where the story
            // is now, this one is the record of it — and it is the entry a player most wants to
            // find again after wandering off.
            bool elsewhere = h.City != homeCity;
            all.Add(h.HasPlace && !elsewhere
                ? new Destination(label, null, DestinationCategory.MissionHistory, h.X, h.Y, h.Z)
                : new Destination(label, null, DestinationCategory.MissionHistory, 0f, 0f, 0f,
                                  elsewhere ? DestinationAction.OtherCity : DestinationAction.NowhereKnown));
        }

        foreach (var p in FastTravelCatalog.Points())
            all.Add(new Destination(p.Name, p.IsMyRoom ? DestinationText.RestKind() : DestinationText.FastTravelKind(),
                DestinationCategoryRules.FastTravel(p.IsMyRoom), p.X, p.Y, p.Z));

        // The named districts the banner announces — 建筑工地 and friends. They are neither
        // landmarks nor shops nor people, which is why they were longest missing from the list.
        var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        foreach (var s in SectionCatalog.Near(px, me.ok ? me.y : 0f, pz))
            all.Add(s);

        // Districts the player has actually stood in — real coordinates, learned on
        // arrival, for exactly the places the game will not give a position for.
        all.AddRange(DistrictMemory.Places(WorldTourStateService.CityId));

        if (people) AddNearbyAvatars(all);
        AddAccessPoints(all, px, pz);
        AddAccesses(all);
        // The lifts — 升降机 — before the general ladders-and-doors group: the same kind of
        // thing (the way up to another floor), and named for what they are rather than numbered
        // like every other object in the field. See LiftCatalog.
        LiftCatalog.AddTo(all, px, me.ok ? me.y : 0f, pz);
        // Whatever is actually MOVING, found without asking what it is first (2026-10-06): the
        // owner pressed a switch and a platform came to them, and no family list would have had
        // that object's name in it. Right after the lifts because it is usually one of them.
        MotionWatch.AddTo(all, px, pz);
        // The switch and the platform the level says it drives: read out of the
        // switch's own supporter rather than guessed from a type name, so the platform is
        // named even before anybody presses anything. See SwitchLink.
        SwitchLink.AddTo(all, px, pz);
        AccessTargetCatalog.AddTo(all);
        // What the world says can be USED, not merely visited: everything that has been within
        // interaction range lately. Added before SceneTargetCatalog so its entries never shadow
        // named shop entries standing at the same spot — see <see cref="InteractableCatalog"/>.
        InteractableCatalog.AddTo(all, px, pz);
        // Every interactable in the FIELD, not merely the ones in arm's reach right now — the
        // generator, the switch, the thing the objective turns out to be. Last of all, and every
        // one of them whether the game named it or not: what has no name answers to a number.
        SceneTargetCatalog.AddTo(all, px, pz);

        uint city = WorldTourStateService.CityId;
        foreach (var e in MapIconSnapshot.For(city))
        {
            var category = MapIconSnapshot.CategoryOf(e.Group, e.Type);
            if (category == null) continue;
            // A rest station is a facility the map files with the shops, and it used to be listed
            // there — which is why it disappeared the moment the player travelled: the map places
            // are read for the city they are standing in. It goes to the rest-station category
            // instead, where it is both findable and remembered after they leave. Upstream's
            // RepeatsKnownRest then drops an icon that only repeats what the fast-travel list
            // already holds.
            if (category == DestinationCategory.Shops && RestCatalog.IsHomeIcon(e.Type))
            {
                RestCatalog.Learn(e.City, e.Name, e.X, e.Y, e.Z);
                continue;
            }
            string kind = category == DestinationCategory.Travel ? DestinationText.FastTravelKind()
                : category == DestinationCategory.Shops ? DeviceMapText.IconType(e.Type) : null;
            var remembered = new Destination(e.Name, kind, category.Value, e.X, e.Y, e.Z);
            if (!DestinationCategoryRules.RepeatsKnownRest(remembered, all, MergeM)) all.Add(remembered);
        }
        // Known wherever the player is: a station in another city is named with that city and says
        // it cannot be walked to from here, rather than vanishing from the list.
        RestCatalog.AddTo(all, city);

        all.Add(new Destination(DestinationText.SaveHereEntry(), null, DestinationCategory.Saved,
                                0f, 0f, 0f, DestinationAction.SaveHere));
        foreach (var s in SavedPlaces.For(city))
            all.Add(new Destination(s.Name, null, DestinationCategory.Saved, s.X, s.Y, s.Z));

        var groups = DestinationPicker.Build(all, px, pz, MergeM);
        // Upstream's "places by name" group: the sonar list, inserted second so the picker's
        // category order matches the enum (Mission, KnownPlaces, ...). Only when the crowd is
        // being listed at all.
        if (people)
        {
            var named = CityPlaceDirectory.ByName(groups);
            if (named.Count > 0) groups.Insert(1, new(DestinationCategory.KnownPlaces, named));
        }
        LogCounts(groups);
        return groups;
    }

    /// <summary>What the picker ended up holding, category by category. The player reports
    /// "the map shows things this list does not", and this is the one line that lets the
    /// two be compared without asking them to read the whole list aloud.</summary>
    private static void LogCounts(List<DestinationPicker.Group> groups)
    {
        var counts = new System.Collections.Generic.List<string>(groups.Count);
        foreach (var g in groups) counts.Add($"{g.Category}={g.Items.Count}");
        REFrameworkNET.API.LogInfo($"[SF6Access] Destinations: {string.Join(", ", counts)}");
    }

    /// <summary>Ladders and doors around the player from upstream's live access catalog —
    /// merged with our AccessPointCatalog entries above rather than replacing them.</summary>
    private static void AddAccesses(List<Destination> into)
    {
        var me = FieldPresenceService.PlayerPos;
        foreach (var a in FieldAccessCatalog.Nearby(me.X, me.Y, me.Z, System.Environment.TickCount64))
            into.Add(new Destination(a.Text, null, DestinationCategory.Access, a.X, a.Y, a.Z));
    }

    /// <summary>The named avatars the N key reads: masters and people near the player.</summary>
    private static void AddNearbyAvatars(List<Destination> into)
    {
        foreach (var other in AvatarFieldReader.ReadOthers(WorldTourStateService.GetAvatarManager()))
        {
            var (name, kind) = AvatarFieldReader.Classify(other.Avatar);
            if (string.IsNullOrEmpty(name)) continue;
            var (x, y, z, ok) = AvatarFieldReader.ReadWorldPos(other.Avatar);
            if (!ok) continue;
            var category = kind == AvatarFieldReader.CONTACT_LEGENDARY
                ? DestinationCategory.Masters : DestinationCategory.People;
            into.Add(new Destination(name, null, category, x, y, z, Actor: LiveDestinationReader.ForAvatar(other)));
        }
    }

    /// <summary>Ladders and doors within reach, so a destination on another level can be
    /// reached by naming the way up instead of walking into the wall under it.</summary>
    private static void AddAccessPoints(List<Destination> into, float px, float pz)
    {
        var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        float py = me.ok ? me.y : 0f;
        long now = System.Environment.TickCount64;
        // The catalog remembers its last answer while the player stands still; a walk invalidates it.
        foreach (var p in AccessPointCatalog.Near(px, py, pz, now))
            into.Add(new Destination(p.Text, null, DestinationCategory.Access, p.X, p.Y, p.Z,
                                     DestinationAction.Go, p.Kind));
    }
}
