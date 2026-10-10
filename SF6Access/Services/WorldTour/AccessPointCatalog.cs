using System;
using System.Collections.Generic;
using REFrameworkNET;
using static SF6Access.Services.WorldTour.LevelRoutePlanner;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// Ladders and doors as choosable destinations: a full-scene sweep of the same object
/// types <see cref="FieldAccessCatalog"/> samples locally, kept as world positions so the
/// destination picker can list them without an open map. A destination on another level is
/// exactly the case the picker could not serve before: the beacon pointed at it and the walk
/// dead-ended at the wall under it, with no way to name the ladder that goes up.
/// </summary>
internal static class AccessPointCatalog
{
    /// <summary>How far a ladder or door may be to make the list. Was 30 m, the radar's
    /// own reach ("everything the player can hear is also a choice"); raised to 80 m on
    /// 2026-09-29 because a staircase or a door 40 m away is still the way up to a place
    /// the player can see on the map, and the list sorts nearest-first, so the extra
    /// entries sit behind the near ones instead of pushing them out.</summary>
    internal const float RangeM = 80f;

    // Same families FieldAccessCatalog walks: ladders, then two door variants.
    private const string LADDER_TYPE = "app.worldtour.om.Om011000";
    private const string DOOR_TYPE_A = "app.worldtour.om.Om016000";
    private const string DOOR_TYPE_B = "app.worldtour.om.Om017000";
    private static readonly string[] Types = { LADDER_TYPE, DOOR_TYPE_A, DOOR_TYPE_B };

    private const int BatchSize = 32;      // Same per-pass object bound as the other scene catalogs.
    private const long RescanMs = 1500;    // A full sweep this often; streaming changes the set.

    /// <summary>One choosable access point: where to stand, where it leads, and what to say.</summary>
    internal readonly record struct Point(
        string Text, MapSelectionMemory.SelectionKind Kind, float X, float Y, float Z,
        float OtherX, float OtherY, float OtherZ, bool Up, float BottomRungM, ulong Access);

    private static readonly List<Point> Found = new();
    private static readonly HashSet<ulong> Seen = new();
    private static int _cursor = -1;
    private static int _family;
    private static long _nextScan;
    private static readonly List<Point> Snapshot = new();
    private static float _snapX, _snapZ;
    private static long _snapAt;
    private static bool _logged;

    /// <summary>The known points within <see cref="RangeM"/> of the player, nearest first.
    /// A player who has not moved reuses the last answer; the sweep itself runs on its own clock.</summary>
    internal static IReadOnlyList<Point> Near(float px, float py, float pz, long now)
    {
        Pump(now);
        bool samePlace = Snapshot.Count > 0 && MathF.Abs(px - _snapX) < 1f && MathF.Abs(pz - _snapZ) < 1f;
        if (samePlace && Snapshot.Count > 0) return Snapshot;

        Snapshot.Clear();
        _snapX = px; _snapZ = pz; _snapAt = now;
        float rangeSq = RangeM * RangeM;
        foreach (var p in Found)
        {
            float dx = p.X - px, dz = p.Z - pz;
            if (dx * dx + dz * dz > rangeSq) continue;
            Snapshot.Add(p);
        }
        // Nearest first: the player walks to the closest ladder, not the one that streams in first.
        Snapshot.Sort((a, b) => FlatSq(a, px, pz).CompareTo(FlatSq(b, px, pz)));
        return Snapshot;
    }

    /// <summary>Resets the sweep so a city or section change does not list the previous place's doors.</summary>
    internal static void Reset()
    {
        Found.Clear();
        Snapshot.Clear();
        Seen.Clear();
        _cursor = -1;
        _family = 0;
        _nextScan = 0;
        _snapAt = 0;
    }

    private static float FlatSq(in Point p, float px, float pz)
    {
        float dx = p.X - px, dz = p.Z - pz;
        return dx * dx + dz * dz;
    }

    /// <summary>Advances a sweep by at most one batch; starts a new one when the rescan clock elapses.</summary>
    private static void Pump(long now)
    {
        if (_cursor >= 0)
        {
            Sweep();
            return;
        }
        if (now < _nextScan) return;
        _nextScan = now + RescanMs;
        Found.Clear();
        Seen.Clear();
        _family = 0;
        _cursor = 0;
        Sweep();
    }

    private static void Sweep()
    {
        if (_cursor < 0) return;
        var list = FieldProbeService.SceneComponents(Types[_family]);
        int count = FlowHelper.GetListCount(list);
        int end = Math.Min(count, _cursor + BatchSize);
        for (; _cursor < end; _cursor++)
        {
            var om = FlowHelper.GetListItem(list, _cursor);
            if (om == null) continue;
            if (!FieldAccessCatalog.IsLive(om)) continue;
            if (!PointOf(_family, om, out var point)) continue;
            if (!Seen.Add(om.GetAddress())) continue;
            Found.Add(point);
        }
        if (_cursor < count) return;

        // This family is done; move to the next, then close the sweep.
        _family++;
        _cursor = _family < Types.Length ? 0 : -1;
        if (_cursor != -1) return;
        if (!_logged)
        {
            _logged = true;
            API.LogInfo(FormattableString.Invariant(
                $"[SF6Access] Access points: {Found.Count} ladders and doors in the scene, listed within {RangeM:F0} m"));
        }
    }

    /// <summary>Reads a component into a choosable point: a ladder answers with both ends and the
    /// end nearer the player becomes the stand-in; a door answers with its own position.</summary>
    private static bool PointOf(int family, ManagedObject om, out Point point)
    {
        point = default;
        if (family == 0)
        {
            if (!LadderCatalog.TryReadEnds(om, out var low, out var high)) return false;
            // The ladder is climbed from whichever end is nearer, so name it by what it does
            // from here: standing low it "leads up", standing high it "leads down".
            var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
            bool fromLow = !me.ok || FieldAccessCatalog.DistanceSq(low.X, low.Y, low.Z, me.x, me.y, me.z)
                                   <= FieldAccessCatalog.DistanceSq(high.X, high.Y, high.Z, me.x, me.y, me.z);
            var near = fromLow ? low : high;
            var far = fromLow ? high : low;
            float rung = FieldAccessCatalog.Vector(om, "StartPoint", out var _, out float sy, out var _) ? sy - low.Y : 0f;
            var access = MemberAccess.ReadFieldsFirst(om, "AccessTarget") as ManagedObject;
            string text = fromLow ? ExplorationText.Get("ladder_up", "Ladder leading up")
                                  : ExplorationText.Get("ladder_down", "Ladder leading down");
            var kind = fromLow ? MapSelectionMemory.SelectionKind.LadderUp
                               : MapSelectionMemory.SelectionKind.LadderDown;
            point = new Point(text, kind, near.X, near.Y, near.Z, far.X, far.Y, far.Z, fromLow,
                              float.IsFinite(rung) ? rung : 0f, access?.GetAddress() ?? 0);
            return true;
        }

        var go = FlowHelper.Call(om, "get_GameObject") as ManagedObject;
        var transform = FlowHelper.Call(go, "get_Transform");
        if (!FieldAccessCatalog.Vector(transform, "Position", out float x, out float y, out float z)) return false;

        string member = family == 1 ? "State" : "CurrentState";
        string state = FlowHelper.ResolveEnumName(Types[family] + (family == 1 ? ".OpenState" : ".StateType"),
            FlowHelper.ReadIntField(om, member, -1));
        if (state == null) return false;
        bool opened = state == "Open" || state == "Opened" || state == "OpenedReverse";
        string label = opened ? ExplorationText.Get("door_open", "Open door")
                              : ExplorationText.Get("door_closed", "Closed door");
        if (!opened && family == 1 &&
            FlowHelper.ReadByteField(om, "Lock", -1) == LockAlways)
            label = ExplorationText.Get("door_locked", "Blocked door");

        var doorAccess = MemberAccess.ReadFieldsFirst(om, "AccessTarget") as ManagedObject;
        string name = FlowHelper.CleanTags(FlowHelper.Call(doorAccess, "GetDispName") as string)?.Trim();
        if (!string.IsNullOrWhiteSpace(name)) label += ", " + name;
        point = new Point(label, MapSelectionMemory.SelectionKind.Door, x, y, z, x, y, z, false, 0f,
                          doorAccess?.GetAddress() ?? 0);
        return true;
    }

    // Om016000.LockType.AlwaysLock, resolved once like FieldAccessCatalog does.
    private static List<(string name, int value)> _lockKinds;
    private static List<(string name, int value)> LockKinds => _lockKinds ??=
        FieldProbeService.ReadEnum(DOOR_TYPE_A + ".LockType", byteWidth: true);

    private static int LockAlways
    {
        get
        {
            foreach (var (name, value) in LockKinds)
                if (name == "AlwaysLock") return value;
            return -1;
        }
    }
}
