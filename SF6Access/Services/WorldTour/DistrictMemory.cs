using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// Where a named district is, learned by standing in it.
///
/// <para>The city's hundred-odd district names are readable from the game's own table
/// (see <see cref="ZoneNameService"/>), and the player asked for one of them by name —
/// 建筑工地, a floor of the SiRN building. But nothing in the game hands out a district's
/// POSITION: <c>CitySectionInfo</c> carries only an id and a priority,
/// <c>SectionMapSetting</c> only a delivery-cost list, and the scenario "zone" groups are
/// script contacts, not places. Rather than invent coordinates — which for a player who
/// cannot see the screen means walking into a wall — this learns them.</para>
///
/// <para><b>Every entry is a spot the player really occupied.</b> The first time a district
/// is entered, the player's own position is remembered as that district's place; later
/// visits only add to the count. Nothing is averaged, extrapolated or snapped: the
/// coordinate is by construction somewhere a player has stood, so it is somewhere a player
/// can stand again.</para>
///
/// <para>The cost is honesty about coverage — a district never visited is never offered.
/// That is the deliberate trade: an incomplete list the player can trust beats a complete
/// one that guides them into geometry.</para>
///
/// <para>Persisted to <c>reframework/data/sf6access_districts.txt</c> as
/// <c>city, id, x, y, z, visits</c>; the name comes from the game's table at read time, so
/// it follows the player's language and never goes stale.</para>
/// </summary>
public static class DistrictMemory
{
    private const string FILE = "sf6access_districts.txt";

    private sealed class Spot
    {
        public float X, Y, Z;
        public int Visits;
    }

    // city -> section id -> where the player stood in it.
    private static readonly Dictionary<uint, Dictionary<uint, Spot>> _byCity = new();
    private static bool _loaded;
    private static long _savedAt;

    private static string FilePath => Path.Combine(ObjectDumper.DumpDir, FILE);

    /// <summary>Remember where the player is standing, as the place this district is.
    /// Called when the game reports the player has entered a district.</summary>
    public static void Sample(uint city, uint sectionId, float x, float y, float z)
    {
        if (city == 0 || sectionId == 0) return;
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return;
        // An exact origin is a failed read, not a place anyone stood.
        if (x == 0f && y == 0f && z == 0f) return;

        Load();
        if (!_byCity.TryGetValue(city, out var map)) _byCity[city] = map = new Dictionary<uint, Spot>();

        // The FIRST spot is kept: it is real and walkable, and averaging a large district
        // would happily place its centre inside a building.
        if (map.TryGetValue(sectionId, out var known)) { known.Visits++; return; }

        map[sectionId] = new Spot { X = x, Y = y, Z = z, Visits = 1 };
        Save();
        API.LogInfo($"[SF6Access] District learned: {ZoneNameService.SectionName(sectionId) ?? ("id " + sectionId)} " +
                    $"at ({x:0.0}, {y:0.0}, {z:0.0}) - {map.Count} districts known in city {city}");
    }

    /// <summary>The districts of this city the player has actually been to.</summary>
    public static List<Destination> Places(uint city)
    {
        Load();
        var list = new List<Destination>();
        if (city == 0 || !_byCity.TryGetValue(city, out var map)) return list;

        foreach (var (id, spot) in map)
        {
            string name = ZoneNameService.SectionName(id);
            // A district the table cannot name is not one the player can recognise.
            if (string.IsNullOrEmpty(name)) continue;
            list.Add(new Destination(name, null, DestinationCategory.Sections, spot.X, spot.Y, spot.Z));
        }
        return list;
    }

    /// <summary>How many districts of this city are remembered, for the log line that
    /// compares what the picker holds with what the map shows.</summary>
    public static int Count(uint city)
    {
        Load();
        return city != 0 && _byCity.TryGetValue(city, out var map) ? map.Count : 0;
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var inv = CultureInfo.InvariantCulture;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] f = line.Split('\t');
                if (f.Length < 5) continue;
                if (!uint.TryParse(f[0], NumberStyles.Integer, inv, out uint city)) continue;
                if (!uint.TryParse(f[1], NumberStyles.Integer, inv, out uint id)) continue;
                if (!float.TryParse(f[2], NumberStyles.Float, inv, out float x)) continue;
                if (!float.TryParse(f[3], NumberStyles.Float, inv, out float y)) continue;
                if (!float.TryParse(f[4], NumberStyles.Float, inv, out float z)) continue;

                if (!_byCity.TryGetValue(city, out var map)) _byCity[city] = map = new Dictionary<uint, Spot>();
                int visits = f.Length > 5 && int.TryParse(f[5], NumberStyles.Integer, inv, out int v) ? v : 1;
                map[id] = new Spot { X = x, Y = y, Z = z, Visits = visits };
            }
        }
        catch { }
    }

    /// <summary>Written only when a district is first learned — a handful of times per
    /// session, never per frame.</summary>
    private static void Save()
    {
        long now = Environment.TickCount64;
        if (now - _savedAt < 1000) return;
        _savedAt = now;
        try
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("# city\tsection id\tx\ty\tz\t(first spot the player stood in)");
            foreach (var (city, map) in _byCity)
                foreach (var (id, spot) in map)
                    sb.AppendLine(string.Join("\t", city.ToString(inv), id.ToString(inv),
                        spot.X.ToString("F2", inv), spot.Y.ToString("F2", inv), spot.Z.ToString("F2", inv),
                        spot.Visits.ToString(inv)));
            File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }
}
