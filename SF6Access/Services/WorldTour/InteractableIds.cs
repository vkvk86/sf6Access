using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>The serial numbers anonymous interactables answer to — assigned once, then never changed.</b>
///
/// <para><b>Why they must not move (owner, 2026-10-04).</b> The first numbering put every
/// anonymous object under its place in the nearest-first list, which is fine for reading a list
/// once and useless for anything else: the owner's words were 我要他编号永远不变，我才知道我有没有
/// 交互过. A number that reshuffles every time the player takes three steps cannot be written
/// down, cannot be recognised tomorrow, and cannot be crossed off. So a number here is bound to
/// the OBJECT, not to the moment: once given, it is that object's name for good.</para>
///
/// <para><b>What the identity is.</b> An interactable does not move and carries no id the mod
/// can read, so its identity is WHERE IT STANDS: the city plus its position in half-metre cells.
/// Two readings of the same machine give the same number even after a restart, even from the
/// other source — <see cref="SceneTargetCatalog"/> walking the scene and
/// <see cref="InteractableCatalog"/> reading the game's own access list now agree on what to
/// call the thing standing between them, which also stops the picker listing it twice.</para>
///
/// <para><b>The trade, said out loud.</b> Two different objects sharing one half-metre cell share
/// one number, and the picker then keeps one of them. That is the cost of an identity made of
/// nothing but position; it is chosen over numbers that move, because a missed duplicate is a
/// nuisance while a renumbered object is a lie about what has already been done.</para>
///
/// <para><b>When a number is given.</b> Only when the object is actually offered, never while
/// sweeping: the file grows by what the player has been shown, so it cannot fill up with objects
/// nobody ever listed. Written on change and throttled, the same discipline
/// <see cref="TourService"/> keeps.</para>
/// </summary>
internal static class InteractableIds
{
    private const string FILE = "sf6access_interactable_ids.txt";

    /// <summary>Identity cell, in metres. Objects are static and metres apart; a finer cell
    /// would split one object into two numbers after the smallest change in how its position
    /// is read.</summary>
    private const float CELL_M = 0.5f;

    private const long SAVE_MS = 1000;

    private static readonly Dictionary<string, int> _numbers = new(StringComparer.Ordinal);
    private static int _nextNumber;
    private static bool _loaded, _dirty;
    private static long _savedAt;

    /// <summary>This object's number, forever the same. The first call mints it; every later
    /// call — next frame, next session, from the other catalog — returns the same one.</summary>
    internal static int Number(uint city, float x, float y, float z)
    {
        Load();
        string key = Key(city, x, y, z);
        if (_numbers.TryGetValue(key, out int already)) return already;

        int number = _nextNumber + 1;
        _numbers[key] = number;
        _nextNumber = number;
        _dirty = true;
        Save();
        return number;
    }

    /// <summary>How many anonymous objects have been numbered so far. The owner cannot see the
    /// file, so this goes in the log line that reports what the list holds.</summary>
    internal static int Count
    {
        get { Load(); return _nextNumber; }
    }

    internal static string Key(uint city, float x, float y, float z) =>
        string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}",
                      city, Cell(x), Cell(y), Cell(z));

    private static int Cell(float v) => (int)MathF.Round(v / CELL_M);

    private static string FilePath => Path.Combine(ObjectDumper.DumpDir, FILE);

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] f = line.Split('\t');
                if (f.Length < 2) continue;
                if (!int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) continue;
                _numbers[f[1]] = n;
                if (n > _nextNumber) _nextNumber = n;
            }
            API.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[SF6Access] Interactable numbers: {0} objects already numbered from {1}",
                _numbers.Count, FILE));
        }
        catch { }
        Save(force: true);
    }

    /// <summary>Written when the numbering changes, at most once a second. Whole file each time:
    /// it is a few hundred short lines, and a half-written one would renumber everything.</summary>
    internal static void Save(bool force = false)
    {
        if (!_dirty && !force) return;
        long now = Environment.TickCount64;
        if (!force && now - _savedAt < SAVE_MS) return;
        _savedAt = now;
        _dirty = false;
        try
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("# number\tidentity (city|x|y|z in half-metre cells) - assigned once, never reused");
            foreach (var pair in _numbers)
                sb.AppendLine(string.Join("\t", pair.Value.ToString(inv), pair.Key));
            File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }
}
