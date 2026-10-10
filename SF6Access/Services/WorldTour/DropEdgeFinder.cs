using System;
using System.Collections.Generic;
using System.Globalization;
using REFrameworkNET;
using SF6Access.Hooks.WorldTour;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>The edge of the floor the player is standing on — found by measuring the ground, because
/// the navmesh cannot be asked about a fall.</b>
///
/// <para><b>Why measurement.</b> When the objective is below the player and no walk reaches it,
/// the engine has answered everything it knows: two floors, no link. What it does not model is
/// the one move that connects them — stepping off the edge — so the mod has been reduced to
/// "look for an edge to drop from", which is true and useless. The edge is a place, it has a
/// bearing and a distance, and a ray straight down finds it: samples walking outward from the
/// player in every direction, and the first one whose ground is not this floor any more.</para>
///
/// <para><b>Unbroken ground is what makes the standing point safe.</b> A sector is only read up to
/// the first sample that is not the player's own floor, so the point offered is reached by
/// continuous platform from where they stand — never across a hole the ray found earlier in the
/// same direction. The samples between are the walk.</para>
///
/// <para><b>The landing is what makes it worth offering</b> (<see cref="DropEdge.Worth"/>): the
/// floor at the bottom must be the floor the objective stands on. A fall onto another storey buys
/// height and nothing else.</para>
///
/// <para><b>Only ever run when the walk is already over.</b> No route, no ladder that reaches, no
/// relay hop nearer: the same gate every addition here is held to. A route that works is left
/// alone.</para>
///
/// <para><b>Cost.</b> Sixteen directions, nine samples, two rays per frame: well under a second,
/// and not one navmesh query — the walk to the edge is an ordinary route the beacon already knows
/// how to guide. The rays are the same call the radar makes thirty times a second.</para>
/// </summary>
internal static class DropEdgeFinder
{
    /// <summary>Rays per frame. Two, matching the jump scan: a search finishes in under a second
    /// instead of eight and the player is not kept waiting on a route that has failed anyway.</summary>
    private const int RaysPerPump = 2;

    /// <summary>How many edges may be offered for one objective before the search gives up: past
    /// this, proposing falls is worse than saying the walk is over.</summary>
    private const int MaxSpent = 3;

    private static readonly float[] Floor = new float[DropEdge.Samples];
    private static readonly int[] Order = new int[DropEdge.Sectors];

    private static float _px, _py, _pz, _tx, _ty, _tz;
    private static int _sector, _sample;
    private static int _standIdx = -1;
    private static float _standFloor;
    private static bool _scanning, _done, _have, _logged;

    private static float _standX, _standY, _standZ, _landX, _landY, _landZ, _dropM;
    private static readonly List<(float X, float Z)> _spent = new();

    /// <summary>True when an edge has been found and is worth walking to.</summary>
    internal static bool HasEdge => _have;

    /// <summary>True while a search is running and has nothing yet.</summary>
    internal static bool Searching => _scanning && !_done && !_have;

    /// <summary>The point to walk to: the last solid ground before the drop.</summary>
    internal static float X => _standX;
    internal static float Y => _standY;
    internal static float Z => _standZ;

    /// <summary>How far the fall is, in metres.</summary>
    internal static float DropM => _dropM;

    /// <summary>Where the fall ends: the ground the player would land on, measured by the same
    /// probe that found the edge. Exposed 2026-10-07 for <see cref="WayDownProbe"/>, which needs
    /// a real landing point to ask "and from THERE, can you reach the objective?" — an estimated
    /// one would make the answer worthless.</summary>
    internal static float LandX => _landX;
    internal static float LandY => _landY;
    internal static float LandZ => _landZ;

    /// <summary><b>Spend this tick's rays on the next sample.</b> Stops at the first edge that
    /// earns one, because the directions are searched outward from the bearing to the objective
    /// and the nearest edge on the most promising bearing is the one a player wants first.</summary>
    internal static void Probe(float px, float py, float pz, float tx, float ty, float tz)
    {
        Retarget(px, py, pz, tx, ty, tz);
        if (_done || _have) return;
        if (!_scanning) Begin();
        for (int r = 0; r < RaysPerPump && !_done && !_have; r++) MeasureOne();
    }

    /// <summary>The player has reached the edge and stepped off (or chosen not to): it is not
    /// offered again, and a different edge is looked for.</summary>
    internal static void Spent()
    {
        if (!_have) return;
        float x = _standX, y = _standY, z = _standZ;
        _spent.Add((x, z));
        if (_spent.Count > MaxSpent) _spent.RemoveAt(0);
        // The SEARCH starts over, the ledger does not: an edge that has already been offered must
        // not be offered again, or the player is walked back to the fall they chose not to take.
        ClearScan();
        API.LogInfo(string.Format(CultureInfo.InvariantCulture,
            "[SF6Access] Drop: edge at ({0:F1}, {1:F1}, {2:F1}) spent - looking for another", x, y, z));
    }

    /// <summary>Forget everything, including which edges have been offered: a new objective, or
    /// another field. A player who has changed floor gets a fresh ledger too — the edges up there
    /// are not the ones down here.</summary>
    internal static void Reset()
    {
        ClearScan();
        _spent.Clear();
    }

    private static void ClearScan()
    {
        _scanning = _done = _have = _logged = false;
        _sector = _sample = 0;
        _standIdx = -1;
        _standFloor = 0f;
        _standX = _standY = _standZ = _landX = _landY = _landZ = _dropM = 0f;
        Array.Clear(Floor, 0, Floor.Length);
    }

    private static void Begin()
    {
        _scanning = true;
        _sector = _sample = 0;
        _standIdx = -1;
        Array.Clear(Floor, 0, Floor.Length);
        BuildOrder();
    }

    /// <summary>Directions nearest the bearing to the objective first: the edge a player would
    /// look for is the one on the way there.</summary>
    private static void BuildOrder()
    {
        double bearing = Math.Atan2(_tx - _px, _tz - _pz);
        var list = new List<(int Sector, double Off)>(DropEdge.Sectors);
        for (int s = 0; s < DropEdge.Sectors; s++)
        {
            double a = 2.0 * Math.PI * s / DropEdge.Sectors;
            list.Add((s, Math.Abs(Math.Atan2(Math.Sin(a - bearing), Math.Cos(a - bearing)))));
        }
        list.Sort((p, q) => p.Off.CompareTo(q.Off));
        for (int i = 0; i < list.Count; i++) Order[i] = list[i].Sector;
    }

    private static void MeasureOne()
    {
        if (_sector >= DropEdge.Sectors) { Finish(); return; }
        int s = Order[_sector];
        double a = 2.0 * Math.PI * s / DropEdge.Sectors;
        float sin = (float)Math.Sin(a), cos = (float)Math.Cos(a);

        int i = _sample;
        _sample++;
        float d = DropEdge.SampleM(i);
        float x = _px + sin * d, z = _pz + cos * d;

        bool ran = FieldRayCaster.TryFloorBelow(x, _py + DropEdge.ProbeAboveM, z,
                                                DropEdge.ProbeDropM, out float f);
        // Not run (the cast failed) or no floor within the probe: a void has no landing to judge.
        // Either way this direction has nothing to offer and the search moves on.
        if (!ran || float.IsNaN(f)) { NextSector(); return; }

        if (DropEdge.SameFloor(f, _py))
        {
            // Still this platform: the walk to here is unbroken ground, so it is a standing point.
            Floor[i] = f;
            _standIdx = i;
            _standFloor = f;
            if (_sample >= DropEdge.Samples) NextSector();
            return;
        }

        Consider(s, sin, cos, i, f);
        NextSector();
    }

    /// <summary>A sample whose ground is not the player's floor, with solid ground behind it: the
    /// edge. Offer it when <see cref="DropEdge.Worth"/> says the landing is the objective's floor
    /// and this edge has not been spent already.</summary>
    private static void Consider(int sector, float sin, float cos, int i, float landingY)
    {
        if (_standIdx < 0) return;
        if (!DropEdge.Worth(_standFloor, _py, landingY, _ty)) return;

        float sd = DropEdge.SampleM(_standIdx);
        float sx = _px + sin * sd, sz = _pz + cos * sd;
        if (Spent(sx, sz)) return;

        float ld = DropEdge.SampleM(i);
        _standX = sx; _standY = _standFloor; _standZ = sz;
        _landX = _px + sin * ld; _landY = landingY; _landZ = _pz + cos * ld;
        _dropM = _standFloor - landingY;
        _have = true;

        API.LogInfo(string.Format(CultureInfo.InvariantCulture,
            "[SF6Access] Drop: edge on sector {0} at ({1:F1}, {2:F1}, {3:F1}), {4:F1} m from the player, " +
            "dropping {5:F1} m onto ({6:F1}, {7:F1}, {8:F1}) - {9:F1} m from the objective, which is {10:F1} m below",
            sector, sx, _standFloor, sz, Dist(sx, _standFloor, sz, _px, _py, _pz), _dropM,
            _landX, _landY, _landZ, Dist(_landX, _landY, _landZ, _tx, _ty, _tz), _py - _ty));
    }

    private static void NextSector()
    {
        _sector++;
        _sample = 0;
        _standIdx = -1;
        _standFloor = 0f;
    }

    private static void Finish()
    {
        _scanning = false;
        _done = true;
        if (_logged || _have) return;
        _logged = true;
        API.LogInfo(string.Format(CultureInfo.InvariantCulture,
            "[SF6Access] Drop: no edge in {0} directions out to {1:F1} m from ({2:F1}, {3:F1}, {4:F1}) " +
            "lands on the objective's floor at {5:F1} m - the walk ends here",
            DropEdge.Sectors, DropEdge.SampleM(DropEdge.Samples - 1), _px, _py, _pz, _ty));
    }

    /// <summary>Start over when the objective has moved or the player has changed floor — a drop
    /// has happened, and an edge found from the storey above is a walk to the wrong place. A player
    /// WALKING toward an edge already found must not lose it, so only those two restart it.</summary>
    private static void Retarget(float px, float py, float pz, float tx, float ty, float tz)
    {
        float moved = Dist(tx, ty, tz, _tx, _ty, _tz);
        bool floorChanged = MathF.Abs(py - _py) >= LevelRoutePlanner.LevelM;
        if (moved <= MissionBeaconHooks.ARRIVED_M && !floorChanged)
        {
            // An edge already in hand is kept while the player WALKS TO it — losing it mid-walk
            // would strand them between two halves of one instruction. So is a search still
            // running: restarting it every frame is how a search never finishes.
            if (_have || _scanning) { _px = px; _py = py; _pz = pz; return; }
            // A finished search that found nothing is re-run once the player has walked a real
            // distance: the edge is somewhere on this floor, and standing still is not how it is
            // found. Twice the arrival band, the beacon's own definition of one move.
            float dx = px - _px, dz = pz - _pz;
            if (_done && MathF.Sqrt(dx * dx + dz * dz) <= 2f * MissionBeaconHooks.ARRIVED_M)
            { _px = px; _py = py; _pz = pz; return; }
        }
        Reset();
        _px = px; _py = py; _pz = pz;
        _tx = tx; _ty = ty; _tz = tz;
    }

    private static bool Spent(float x, float z)
    {
        foreach (var s in _spent)
            if (Dist(s.X, _standFloor, s.Z, x, _standFloor, z) <= MissionBeaconHooks.ARRIVED_M) return true;
        return false;
    }

    private static float Dist(float ax, float ay, float az, float bx, float by, float bz)
    {
        float dx = bx - ax, dy = by - ay, dz = bz - az;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
