#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using REFrameworkNET;
using SF6Access.Services;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>The rest stations (休息站) — the places the game offers to rest at — remembered once and
/// offered wherever the player stands.</b>
///
/// <para><b>Why this is its own catalog.</b> A rest station is registered like a shop and drawn on
/// the phone map like one, but it is NOT a shop: it is the place the player goes to recover and to
/// turn day into night, and it is the one facility a blind player asks for by name. Until now it
/// reached the list only through the phone map of the city it stands in — so travelling to another
/// city made it vanish, which is exactly the report "我在纳夏尔，但是休息站没有出现在列表里". A
/// station is a fixed object: once the game has shown where one is, that answer stays true, and
/// this keeps it.</para>
///
/// <para><b>How one is recognised — never by guessing at a name.</b> Three answers the GAME gives:
/// the phone map's own icon type (<c>app.UIMapWindowBase.ICON_TYPE.HOME</c>, read back from the
/// TDB so a renumbered enum cannot make a shop a rest station), a scene object's own
/// <c>IsHome()</c> flag, and the display name the game publishes for it. The word spoken for a
/// station the game has not named in this language is the one the phone map itself used for these
/// icons, learned the first time one is seen; before that it comes from the lang file.</para>
///
/// <para><b>A station in another city is listed, not dropped.</b> It is spoken with the city it
/// stands in and, when chosen, says it cannot be walked to from here — the same contract the
/// mission history follows. A list that silently empties itself because the player crossed a
/// border is indistinguishable from the place having been forgotten.</para>
/// </summary>
public static class RestCatalog
{
    private const string FILE = "sf6access_rest_stations.txt";
    private const string ICON_ENUM = "app.UIMapWindowBase.ICON_TYPE";
    private const string HOME_MEMBER = "HOME";

    /// <summary>Two sightings this close together are one station wearing a slightly different
    /// coordinate: the map re-reads an icon with a different height every time it is opened.</summary>
    private const float SameM = 3f;

    private const long LOG_MS = 15000;

    private static readonly List<PlaceFile.Entry> _stations = new();
    private static bool _loaded;
    private static string? _word;
    private static int _homeType = int.MinValue;
    private static long _nextLog;
    private static string? _lastLine;

    /// <summary>The word used for a station the game has not named for us yet: the one the phone
    /// map itself gave the first time one was seen, else the lang file's.</summary>
    public static string Word => string.IsNullOrEmpty(_word) ? DestinationText.RestStation() : _word!;

    /// <summary>Whether the phone map's icon type is the one it draws for a rest station. A value
    /// the enum does not name (or an enum that cannot be read) is never treated as one.</summary>
    public static bool IsHomeIcon(int iconType)
    {
        if (_homeType == int.MinValue)
            _homeType = FieldProbeService.EnumValue(ICON_ENUM, HOME_MEMBER);
        return _homeType >= 0 && iconType == _homeType;
    }

    /// <summary>Whether a name the game published is the name it uses for a rest station.</summary>
    public static bool IsRestName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string n = name!.Trim();
        if (string.Equals(n, Word, StringComparison.Ordinal)) return true;
        return !string.IsNullOrEmpty(_word) && string.Equals(n, _word, StringComparison.Ordinal);
    }

    /// <summary>How many stations are known, and how many of them are in this city — the line that
    /// answers "is there one here at all" without asking the player to read the list aloud.</summary>
    public static int CountIn(uint city)
    {
        Load();
        int n = 0;
        foreach (var s in _stations) if (s.City == city) n++;
        return n;
    }

    /// <summary>Remember a station. Called from every channel that can recognise one: the phone
    /// map, the city registry's OM contacts, and the scene sweep. Duplicates are silent; a new one
    /// is written to disk straight away, because a station learned this session is the only record
    /// of it there will ever be.</summary>
    public static void Learn(uint city, string? name, float x, float y, float z)
    {
        Load();
        // An exact origin is a failed read, not a place at the world's centre.
        if (x == 0f && y == 0f && z == 0f) return;
        if (!string.IsNullOrWhiteSpace(name)) NoteWord(name!.Trim());

        foreach (var s in _stations)
            if (s.City == city && Apart(s.X, s.Z, x, z) <= SameM)
                return;

        var entry = new PlaceFile.Entry(city, 0, 0,
            string.IsNullOrWhiteSpace(name) ? Word : name!.Trim(), x, y, z);
        _stations.Add(entry);
        Save();
        API.LogInfo(string.Format(CultureInfo.InvariantCulture,
            "[SF6Access] Rest station learned: '{0}' in city {1} at ({2:F1}, {3:F1}, {4:F1}) - {5} known",
            entry.Name, city, x, y, z, _stations.Count));
    }

    /// <summary>Every station known, as choosable destinations: the ones in this city by distance,
    /// the ones elsewhere named with the city they stand in and an action that says why they
    /// cannot be walked to from here.</summary>
    public static void AddTo(List<Destination> into, uint city)
    {
        Load();
        int here = 0, elsewhere = 0;
        foreach (var s in _stations)
        {
            if (s.City == city)
            {
                into.Add(new Destination(s.Name, null, DestinationCategory.Rest, s.X, s.Y, s.Z));
                here++;
            }
            else
            {
                string? where = ZoneNameService.CityNameOf(s.City);
                into.Add(new Destination(s.Name, DestinationText.RestOtherCity(where ?? string.Empty),
                                         DestinationCategory.Rest, 0f, 0f, 0f,
                                         DestinationAction.OtherCity));
                elsewhere++;
            }
        }
        Log(here, elsewhere, city);
    }

    /// <summary>Another field, another city: the stations themselves do not move, but the objects
    /// that name them do. Nothing is dropped here on purpose — see <see cref="Learn"/>.</summary>
    public static void Reset()
    {
        _nextLog = 0;
        _lastLine = null;
    }

    private static void NoteWord(string name)
    {
        if (string.IsNullOrEmpty(_word)) _word = name;
    }

    private static float Apart(float ax, float az, float bx, float bz)
    {
        float dx = ax - bx, dz = az - bz;
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }

    private static void Log(int here, int elsewhere, uint city)
    {
        long now = Environment.TickCount64;
        if (now < _nextLog) return;
        _nextLog = now + LOG_MS;
        string line = string.Format(CultureInfo.InvariantCulture,
            "{0} rest station(s) known, {1} in city {2}, {3} elsewhere (word '{4}')",
            _stations.Count, here, city, elsewhere, Word);
        if (line == _lastLine) return;
        _lastLine = line;
        API.LogInfo("[SF6Access] Rest stations: " + line);
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;

        foreach (var e in PlaceFile.Load(FilePath))
        {
            if (e.X == 0f && e.Y == 0f && e.Z == 0f) continue;
            _stations.Add(e);
            NoteWord(e.Name);
        }

        // The map of a city the player has since LEFT is the only record of that city's stations,
        // and it is already on disk: seeding from it is what makes this useful on the first run
        // after the feature ships, rather than after the player happens to walk past one again.
        try
        {
            foreach (var e in MapIconSnapshot.AllPlaces())
                if (IsHomeIcon(e.Type)) Learn(e.City, e.Name, e.X, e.Y, e.Z);
        }
        catch { }

        if (_stations.Count > 0)
            API.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[SF6Access] Rest stations: {0} known from disk and from the phone map's record",
                _stations.Count));
    }

    private static void Save()
    {
        try { PlaceFile.Save(FilePath, _stations); } catch { }
    }

    private static string FilePath => Path.Combine(ObjectDumper.DumpDir, FILE);
}
