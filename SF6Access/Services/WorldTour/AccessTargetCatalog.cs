using System.Collections.Generic;
using REFrameworkNET;
using SF6Access.Services;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// Live named people and facilities from the game's own map registry,
/// <c>app.GuiManager.AccessData</c> (<c>GUIAccessDataManager</c>) — the data the phone
/// map builds its NPC, master and facility icons from — so the picker needs no open map.
/// Every facility KIND the registry declares is read (<c>GetWTCityOMSetterData_OM</c>),
/// not just shops: rest stations, gyms and homes are registered the same way.
/// </summary>
public static class AccessTargetCatalog
{
    private const string OM_TYPE_ENUM = "app.GUIAccessDataManager.OMType";

    /// <summary>Safety bound on entries read per list, only so a corrupt count cannot stall a
    /// press; the picker sorts what is read, so this must stay well above a city's real count.</summary>
    private const int MAX_READ = 512;

    /// <summary>Highest OMType value probed. The enum has a handful of members (Shop,
    /// START, Else, NUM …), so this range covers it with room to spare; an out-of-range
    /// value just answers with an empty list.</summary>
    private const int MAX_OM_TYPE = 15;

    /// <summary>Upper bound on facilities listed per city. Each one costs a message lookup,
    /// and the build runs on the frame that opens the picker, so the read is capped rather
    /// than trusted to stay small.</summary>
    private const int MAX_FACILITIES = 200;

    private const long EMPTY_RETRY_MS = 5000;

    // Facilities do not move and their names cost a message lookup each: read once per city.
    private static List<Destination> _facilities;
    private static uint _facilitiesCity;
    private static long _facilitiesAt;

    private static bool _logged;

    /// <summary>Every kind of city facility the registry knows, read from the enum
    /// once per session. Asking for <c>OMType.Shop</c> alone was the whole reason a
    /// 休息站 (rest station), a gym or a home never reached the picker: they are
    /// registered exactly like a shop, just under their own kind.
    ///
    /// <para>When the enum cannot be read the <c>Shop</c> kind alone is asked for, so a
    /// failed enumeration degrades to the old list instead of emptying the category.</para></summary>
    private static List<(string name, int value)> FacilityKinds()
    {
        if (_kinds != null) return _kinds;
        // Byte width first: the int-width read reports every member as the value of the
        // member before it (city 400 log: Shop[0], START[0], Else[1] — START cannot be 0
        // when Shop is), so a kind list read that way queries the wrong categories.
        _kinds = FieldProbeService.ReadEnum(OM_TYPE_ENUM, byteWidth: true);
        if (_kinds.Count == 0) _kinds = FieldProbeService.ReadEnum(OM_TYPE_ENUM, byteWidth: false);
        if (_kinds.Count == 0)
        {
            int shop = FieldProbeService.EnumValue(OM_TYPE_ENUM, "Shop");
            if (shop >= 0) _kinds.Add(("Shop", shop));
            API.LogWarning($"[SF6Access] Facility kinds: {OM_TYPE_ENUM} could not be enumerated, " +
                           $"falling back to Shop only ({shop})");
        }
        return _kinds;
    }
    private static List<(string, int)> _kinds;

    public static void AddTo(List<Destination> into)
    {
        var data = WorldTourStateService.GetAccessData();
        if (data == null)
        {
            LogOnce("app.GuiManager.AccessData not readable");
            return;
        }
        LogMethodsOnce(data);

        int masters = AddTargets(data, AvatarFieldReader.CONTACT_LEGENDARY, into);
        int npcs = AddTargets(data, AvatarFieldReader.CONTACT_NPC, into);
        // ContactUIType.OM (2) — the scene's objects/gimmicks. A rest station is an
        // anonymous scene object, not a named city OM, which is why the city-OM
        // walk above only ever finds the two named shops.
        int oms = AddTargets(data, AvatarFieldReader.CONTACT_OM, into);
        var facilities = Facilities(data);
        into.AddRange(facilities);
        LogOnce($"{masters} masters, {npcs} NPCs, {oms} OM contacts, {facilities.Count} facilities");
    }

    private static int AddTargets(ManagedObject data, int contactType, List<Destination> into)
    {
        var list = FlowHelper.Call(data, "GetAccessTargetData", contactType) as ManagedObject;
        int n = System.Math.Min(FlowHelper.GetListCount(list), MAX_READ), added = 0, unnamed = 0;
        int namedOm = 0;   // of `added`, how many carried a name rather than a number
        for (int i = 0; i < n; i++)
        {
            var target = FlowHelper.GetListItem(list, i);
            string name = FlowHelper.CleanTags(FlowHelper.Call(target, "GetDispName") as string)?.Trim();

            // A scene object is frequently registered with no display name at all — a
            // rest station, a vending machine, the player's own home. It is still drawn
            // on the map, so it still belongs in the list: name it from what it does
            // expose (its DispName field, the facility icon, its own flags) before
            // deciding it cannot be spoken.
            if (string.IsNullOrEmpty(name) && contactType == AvatarFieldReader.CONTACT_OM)
                name = NameOmContact(target);

            var (x, y, z, ok) = AvatarFieldReader.ReadWorldPos(target);
            if (string.IsNullOrEmpty(name) || !ok || x == 0f && y == 0f && z == 0f)
            {
                if (contactType == AvatarFieldReader.CONTACT_OM)
                {
                    unnamed++;
                    LogOmSample(target, x, y, z, ok);

                    // An OM contact the game registered but never named, standing somewhere
                    // readable: it is a real interactable and the owner's rule is that every one
                    // of them is listed. It answers to its permanent number instead of being
                    // dropped — the same number the scene sweep gives the same object, so the
                    // two sources agree rather than listing it twice (see InteractableIds).
                    //
                    // This is the SECOND channel to those objects. Until now the scene sweep was
                    // the only one, so a sweep that had not yet come round to their family left
                    // them out of the list entirely — which is what "I walked here and it is not
                    // in my list" was.
                    if (ok && !(x == 0f && y == 0f && z == 0f))
                    {
                        string numbered = ExplorationText.InteractableNumbered(
                            InteractableIds.Number(WorldTourStateService.CityId, x, y, z));
                        into.Add(new Destination(numbered, null, DestinationCategory.Access, x, y, z));
                        added++;
                        continue;
                    }
                }
                continue;
            }

            // A rest station the registry named is worth keeping: it is a fixed place, and this is
            // the channel that can name one the player is not standing next to yet.
            if (contactType == AvatarFieldReader.CONTACT_OM && RestCatalog.IsRestName(name))
                RestCatalog.Learn(WorldTourStateService.CityId, name, x, y, z);

            var category = CategoryOf(target, contactType);
            // A rest station is not a shop: it goes to the category the player looks for it in,
            // where the remembered station already stands (the picker merges the two).
            if (category == DestinationCategory.Shops && RestCatalog.IsRestName(name))
                category = DestinationCategory.Rest;

            into.Add(new Destination(name, null, category, x, y, z));
            if (contactType == AvatarFieldReader.CONTACT_OM) namedOm++;
            added++;
        }
        if (contactType == AvatarFieldReader.CONTACT_OM)
            API.LogInfo($"[SF6Access] OM contacts: {added} listed ({namedOm} named, {added - namedOm} numbered), {unnamed} unnamed");
        return added;
    }

    /// <summary>Which picker category a registry contact belongs to.</summary>
    private static DestinationCategory CategoryOf(ManagedObject target, int contactType)
    {
        if (contactType == AvatarFieldReader.CONTACT_LEGENDARY) return DestinationCategory.Masters;
        if (contactType == AvatarFieldReader.CONTACT_OM) return DestinationCategory.Shops;

        // A master is registered as an ordinary NPC: ContactUIType.Legendary answered with
        // ZERO entries all through the 2026-10-02 session, so the picker had no masters at
        // all while the map was drawing them. WTNpcAccessTarget.IsLegendaryNPC is the flag
        // the HUD itself uses to draw the master icon.
        if (IsMasterNpc(target)) return DestinationCategory.Masters;

        return FlowHelper.GetObjectField(target, "ShopComponent") != null
            ? DestinationCategory.Shops : DestinationCategory.People;
    }

    private static bool IsMasterNpc(ManagedObject target)
    {
        if (FlowHelper.Call(target, "IsLegendaryNPC") is bool legendary && legendary) return true;
        if (FlowHelper.Call(target, "IsRelationToMaster") is bool related && related) return true;
        return false;
    }

    /// <summary>A name for a scene object the registry leaves unnamed. Tried in this order:
    /// the <c>DispName</c> field (these carry a message Guid even when
    /// <c>GetDispName()</c> answers empty), the facility icon the map draws for it, then
    /// the type's own flags (home / body shop / fast-travel point). Null when nothing
    /// names it — that one is still logged as a sample, never invented.</summary>
    private static string NameOmContact(ManagedObject target)
    {
        string name = ResolveDispNameField(target);
        if (!string.IsNullOrEmpty(name)) return name;

        int iconType = FlowHelper.CallInt(target, "GetFacilityIconType");
        if (iconType >= 0)
        {
            string word = DeviceMapText.IconType(iconType);
            if (!string.IsNullOrEmpty(word)) return word;
        }
        // A rest station: the game's own flag on the object, spoken with the word the phone map
        // itself uses for these icons rather than a word of ours.
        if (IsTrue(target, "IsHome")) return RestCatalog.Word;
        if (IsTrue(target, "IsBodyShop")) return DestinationText.OmBodyShop();
        if (IsTrue(target, "IsFastTravelOpenOM")) return DestinationText.OmFastTravel();
        return null;
    }

    private static string ResolveDispNameField(ManagedObject target)
    {
        try
        {
            if (FieldProbeService.Member(target, "DispName") is REFrameworkNET.ValueType guid)
                return PinnedValue.Use(guid, _ => FlowHelper.ResolveGuid(guid))?.Trim();
        }
        catch { }
        return null;
    }

    private static bool IsTrue(ManagedObject target, string method) =>
        FlowHelper.Call(target, method) is bool b && b;

    // Up to a few unnamed OM contacts per CITY, with what can be read from them, so the next
    // naming pass knows exactly which type holds the rest stations. Per city, not per session:
    // one session's cap was spent before the player ever left their first city, and the city
    // that was missing a station was the one never sampled.
    private static int _omSamples;
    private static uint _omSamplesCity;

    private static void LogOmSample(ManagedObject target, float x, float y, float z, bool ok)
    {
        uint city = WorldTourStateService.CityId;
        if (city != _omSamplesCity) { _omSamplesCity = city; _omSamples = 0; }
        if (_omSamples >= 8) return;
        _omSamples++;
        string type;
        try { type = target.GetTypeDefinition()?.FullName ?? "?"; } catch { type = "?"; }
        API.LogInfo($"[SF6Access] Unnamed OM contact (city {city}): {type} at ({x:F1}, {y:F1}, {z:F1}) posOk={ok} " +
                    $"shopComp={FlowHelper.GetObjectField(target, "ShopComponent") != null} " +
                    $"dispName={ResolveDispNameField(target) ?? "-"} " +
                    $"icon={FlowHelper.CallInt(target, "GetFacilityIconType")} " +
                    $"home={IsTrue(target, "IsHome")} bodyShop={IsTrue(target, "IsBodyShop")} " +
                    $"travel={IsTrue(target, "IsFastTravelOpenOM")}");
    }

    // The manager's own getter vocabulary, once per session: if a facility category stays
    // missing, the next fix is a method on this list we never asked, and this log names it.
    private static bool _methodsLogged;

    private static void LogMethodsOnce(ManagedObject data)
    {
        if (_methodsLogged) return;
        _methodsLogged = true;
        try
        {
            var names = new List<string>();
            foreach (var m in data.GetTypeDefinition().GetMethods())
            {
                try { names.Add(m.Name); } catch { }
            }
            API.LogInfo($"[SF6Access] AccessData methods ({names.Count}): {string.Join(", ", names)}");
        }
        catch { }
    }

    private static List<Destination> Facilities(ManagedObject data)
    {
        uint city = WorldTourStateService.CityId;
        long now = System.Environment.TickCount64;
        if (_facilities != null && city == _facilitiesCity &&
            (_facilities.Count > 0 || now - _facilitiesAt < EMPTY_RETRY_MS))
            return _facilities;

        _facilitiesCity = city;
        _facilitiesAt = now;
        _facilities = new List<Destination>();
        _seenPlaces.Clear();

        var kinds = FacilityOmTypes();
        var counts = new List<string>(kinds.Length);
        foreach (var (value, name) in kinds)
        {
            int before = _facilities.Count;
            ReadSetters(data, value, _facilities);
            // The value is logged with the count so a mis-read enum width shows up as
            // values that all found nothing, instead of as a silently short list.
            counts.Add($"{name}[{value}]={_facilities.Count - before}");
        }
        LogKinds(city, counts);
        return _facilities;
    }

    /// <summary>Every OMType value worth asking for, paired with the enum's own name for it
    /// when one is known (for the log only).
    ///
    /// <para>Asking the enum alone is what kept this category EMPTY: city 400 logged
    /// <c>Shop[0], START[0], Else[1], NUM[2]</c> — START given the same value as Shop,
    /// which is the documented off-by-one of an int-width read, and every one of those
    /// queries returned nothing. The enum is a small contiguous range, so every value in
    /// it is asked outright; a value with no registered setters answers with an empty list
    /// and costs one call, while a value we failed to name costs us a whole kind of
    /// facility — shops, rest stations, gyms and homes are registered this way.</para></summary>
    private static (int value, string name)[] FacilityOmTypes()
    {
        if (_omTypes != null) return _omTypes;

        var named = new Dictionary<int, string>();
        foreach (var (name, value) in FacilityKinds())
            if (value >= 0 && !named.ContainsKey(value)) named[value] = name;

        var list = new List<(int, string)>(MAX_OM_TYPE + 1);
        for (int v = 0; v <= MAX_OM_TYPE; v++)
            list.Add((v, named.TryGetValue(v, out string n) ? n : v.ToString()));
        _omTypes = list.ToArray();
        return _omTypes;
    }
    private static (int, string)[] _omTypes;

    // Setters repeat across OMType values, so a place already listed is not listed twice.
    // Keyed on the place alone: the same bench must not be named twice.
    private static readonly HashSet<string> _seenPlaces = new();

    /// <summary>One OMType value's registered setters as choosable places. A value whose
    /// list is empty or whose members cannot be named is skipped, never guessed at.</summary>
    private static void ReadSetters(ManagedObject data, int omType, List<Destination> into)
    {
        var list = FlowHelper.Call(data, "GetWTCityOMSetterData_OM", omType) as ManagedObject;
        int n = System.Math.Min(FlowHelper.GetListCount(list), MAX_READ);
        for (int i = 0; i < n; i++)
        {
            if (into.Count >= MAX_FACILITIES) return;
            var setter = FlowHelper.GetListItem(list, i);
            var (x, y, z, ok) = AvatarFieldReader.ReadWorldPos(setter);
            if (!ok || x == 0f && y == 0f && z == 0f) continue;

            // De-duplicated on the place BEFORE its name is resolved: several OMType
            // values answer with the same setters, and a message lookup costs a round
            // trip, so the second asking for the same bench must not pay for it again.
            if (!_seenPlaces.Add($"{(int)x}|{(int)y}|{(int)z}")) continue;

            // GetDispName returns the message Guid, a struct: resolve it with its bytes pinned.
            string name = FlowHelper.Call(setter, "GetDispName") is REFrameworkNET.ValueType guid
                ? PinnedValue.Use(guid, _ => FlowHelper.ResolveGuid(guid))?.Trim() : null;
            if (string.IsNullOrEmpty(name)) continue;

            string kind = DeviceMapText.IconType(FlowHelper.CallInt(setter, "GetFacilityIconType"));
            into.Add(new Destination(name, kind, DestinationCategory.Shops, x, y, z));
        }
    }

    // Per-kind counts, and the names read, so a facility missing from the picker can be
    // traced to the kind that did not list it (or to the kind that is not in the enum).
    private static uint _kindsCity;
    private static string _kindsSummary;

    private static void LogKinds(uint city, List<string> counts)
    {
        string summary = string.Join(", ", counts);
        if (city == _kindsCity && summary == _kindsSummary) return;
        _kindsCity = city;
        _kindsSummary = summary;
        API.LogInfo($"[SF6Access] Facility kinds in city {city}: {summary}");
        API.LogInfo($"[SF6Access] Facility names: {NameList()}");
    }

    private static string NameList()
    {
        const int MAX_NAMES = 60;
        var names = new List<string>(MAX_NAMES);
        foreach (var d in _facilities)
        {
            if (names.Count >= MAX_NAMES) { names.Add("…"); break; }
            names.Add(d.Name);
        }
        return string.Join(" / ", names);
    }

    private static void LogOnce(string what)
    {
        if (_logged) return;
        _logged = true;
        API.LogInfo($"[SF6Access] Destinations from the game's map registry: {what}");
    }
}
