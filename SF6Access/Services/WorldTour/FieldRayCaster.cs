using System;
using System.Globalization;
using REFrameworkNET;
using SF6Access.Services;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// The World Tour navigation radar's PURE SENSING layer: one segment cast per
/// direction, with the PLAYER'S OWN collision filter. It announces nothing and owns
/// no keys. Reading the contacts back and classifying them is
/// <see cref="FieldRayContacts"/>; the thresholds are <see cref="FieldRayMetrics"/>.
///
/// <para><b>The filter is the whole point.</b> The existing sweep casts with
/// <c>eFilterInfo.TerrainRayFilter</c>, measured in game as <c>layer=3:TerrainRay
/// mask=0x8 [TCStopCamera]</c> — what stops the CAMERA, not the avatar, which is why
/// fences and props the capsule collides with were invisible while kerbs the avatar
/// climbs read as walls. The avatar's real filter is published by the game:
/// <c>CharacterController.FilterInfo</c> (<c>layer=2:Character group=1374 mask=0xA</c>,
/// confirmed in game 2026-09-04), and <c>app.CollisionSystem</c> has a
/// <c>castRayAll</c> overload taking a <c>via.physics.FilterInfo</c> directly. Its
/// group/subgroup being the avatar's should also keep the ray off the avatar; the
/// contact's owning GameObject is compared against the avatar's anyway.</para>
///
/// <para><b>Own segments.</b> Every sideways ray World Tour publishes reaches
/// 0.40–0.60 m (measured) and stretching one only amplifies its grazing-angle noise,
/// so the segment is ours: origin at the avatar's transform (third-person — the
/// avatar, not the camera) at the capsule's own mid-height, direction from the caller.</para>
///
/// <para><b>The CastRayResult is one globalized container for the whole process</b>
/// (<see cref="FieldProbeService.SharedInstance"/>), cleared before every cast. Three
/// other lifetimes were tried in play on 2026-09-09 and ALL THREE killed the game, so
/// this is the record rather than a preference.</para>
/// <list type="bullet">
/// <item><b>Why any of them died.</b> <c>CreateInstance</c> runs the game's Activator
///   on the calling thread, and RE Engine calls what comes back a LOCAL object: negative
///   reference count, an index into that thread's own table, reclaimed by the engine's
///   frame GC. Capcom's own design note is explicit that "all objects created from C#
///   will be local objects". Nothing AddRefs it, so nothing keeps it alive.</item>
/// <item><b>One per CAST (450/s).</b> The object dies and REFramework's finalizer then
///   dereferences the reclaimed engine object FROM THE GC THREAD:
///   <c>AccessViolationException at ManagedObject.Internal_Finalize / GC.RunFinalizers</c>.
///   The REFramework log stopped dead, the fault being on a thread its handler does not
///   cover. Fatal about 15 s after the radar armed.</item>
/// <item><b>One per PROCESS, not globalized.</b> The engine reclaimed it between frames,
///   so <c>castRayAll</c> wrote into freed memory (<c>c0000005</c>) and then
///   <c>result.Call("clear")</c> took the process down, the managed stack naming
///   <c>CastColumn</c>. Fatal instantly.</item>
/// <item><b>One per SWEEP (30/s).</b> The same use-after-free at a fifteenth of the
///   rate. Rate never fixed this; it only changed how long we lasted.</item>
/// </list>
/// <para><b>And note what none of that bought us:</b> an <c>AccessViolationException</c>
/// is a corrupted-state exception, so the <c>catch</c> wrapping <c>CastColumn</c> does
/// NOT catch it. There was no log line and no recovery, just a dead game — "it is inside
/// a try/catch" is false comfort for any engine call here.</para>
/// <para>The vec3 endpoints are unmanaged
/// <see cref="FieldOutBuffer"/>s, never a managed <c>CreateValueType</c>; the
/// <c>castRay(..., out HitResult, ...)</c> overloads are NOT called, <c>HitResult</c>
/// being a reference type behind an out parameter — the call that killed the game
/// (<c>FieldRayProbe</c>). Lookups are cached, nothing throws out, nothing logs twice.</para>
/// </summary>
public static class FieldRayCaster
{
    private const string PLAYER_MANAGER = "app.worldtour.WTPlayerManager";
    private const string COLLISION_SYSTEM = "app.CollisionSystem";

    /// <summary>The <c>disableBackFacingTriangleHit</c> argument. False = hide
    /// nothing: the classifier decides what is not a wall, and a surface dropped inside
    /// the engine cannot be reasoned about out here. What the game passes for it is not
    /// readable from the decompiled headers, so nothing is assumed.</summary>
    private const bool DISABLE_BACK_FACING = false;

    /// <summary>How many heights a body-column sweep casts at. Not a tuning number:
    /// the collision capsule's cylindrical core is bounded by its two cap centres and
    /// has one midpoint, so a column that covers the body has exactly these three
    /// heights — see <see cref="FieldRayMetrics.BodyHeights"/>.</summary>
    public const int BODY_HEIGHTS = 3;

    // --- cached lookups: once per process, never per frame ---
    private static Method _castRayAll;
    private static TypeDefinition _vecType, _resultType;
    private static FieldOutBuffer _start, _end;
    private static bool _routeTried, _summaryLogged, _failLogged;

    /// <summary>Which cast overload is bound, with which filter and which derived
    /// thresholds — logged once, so an in-game test can say what the sensor actually
    /// ran on. "unresolved" until the first successful cast.</summary>
    public static string RouteDescription { get; private set; } = "unresolved";

    /// <summary>Cast one horizontal segment of <paramref name="maxDistance"/> metres
    /// from the avatar's waist along <paramref name="dirWorld"/> (a world ground-plane
    /// unit direction — the type the rest of the World Tour navigation code passes
    /// around). False means the sensor could not run at all: "no information", which
    /// the caller must never read as "the way is open".</summary>
    public static bool TryCast(FieldDirectionService.FlatDir dirWorld, float maxDistance, out RayHit hit)
    {
        hit = default;
        if (!dirWorld.Ok) return false;
        var one = new RayHit[1];
        if (!TryCastMany(new[] { dirWorld }, maxDistance, one)) return false;
        hit = one[0];
        return true;
    }

    /// <summary>Cast several directions as ONE sample: the avatar, its filter and every
    /// derived threshold are resolved once and every ray leaves the same origin at the
    /// same instant. That matters twice over — it is the difference between four
    /// lookup chains a sample and one, and it is what stops two directions of the same
    /// reading from disagreeing because the avatar moved between them, which the audit
    /// of the old radar named as a real source of contradictory cues.
    ///
    /// <para><paramref name="hits"/> is filled in step with <paramref name="dirs"/> and
    /// must be at least as long. A direction that is unusable leaves its slot default —
    /// <c>Hit == false</c> there means "not measured", exactly as a false return means
    /// "no information", never "the way is open".</para></summary>
    public static bool TryCastMany(FieldDirectionService.FlatDir[] dirs, float maxDistance, RayHit[] hits)
        => CastColumn(dirs, maxDistance, 1, hits, everyRay: false);

    /// <summary>Cast every direction at every height of the avatar's body column —
    /// <see cref="BODY_HEIGHTS"/> casts per direction, all from the same origin and
    /// the same instant, which is the RE7 mod's own nine-cast evaluation
    /// (<c>Re7Access/src/RadarService.cs</c>: three beams at the capsule core's
    /// bottom, middle and top). Height 0 is the lowest.
    ///
    /// <para><paramref name="hits"/> is indexed <c>direction * BODY_HEIGHTS +
    /// height</c> and must be at least that long. The heights themselves come from
    /// the live capsule (<see cref="FieldRayMetrics.BodyHeights"/>), so a capsule
    /// with no cylindrical core casts three rays at the waist rather than at
    /// invented offsets.</para>
    ///
    /// <para><b>All or nothing</b>, unlike <see cref="TryCastMany"/>: a body column
    /// with one ray missing would read as a clear height that was never measured, and
    /// "not measured" must never reach a caller as "open". False means take no
    /// reading at all this evaluation.</para></summary>
    public static bool TryCastBodyStack(FieldDirectionService.FlatDir[] dirs, float maxDistance, RayHit[] hits)
        => CastColumn(dirs, maxDistance, BODY_HEIGHTS, hits, everyRay: true);

    /// <summary><b>Where the ground is under one point of the world, in metres Y — or
    /// NaN when there is no ground under it at all.</b> The floor probe: the one
    /// measurement that turns "the navmesh refuses" into a number a jump can be judged
    /// against (<see cref="JumpLink"/>).
    ///
    /// <para><b>True means the cast RAN, not that it found a floor.</b> A floorY of NaN
    /// is a hole — the edge of a gap, or the far side of a drop — and it is exactly what
    /// the caller is looking for. False is "the sensor could not run", which no caller
    /// may read as "there is nothing under there".</para>
    ///
    /// <para><b>Same cast, same container, same filter as the radar</b> — the avatar's own
    /// <c>CharacterController.FilterInfo</c>, which is the filter that knows the ground
    /// an avatar stands on. The geometry is ours: straight down from
    /// <paramref name="aboveY"/>, the only direction whose answer is a floor height.
    /// Nothing is allocated, nothing is created; the lifetime rules this class was
    /// written around are kept exactly as they are.</para>
    ///
    /// <para>This is the first cast in this mod that does not start at the avatar, and
    /// that is the whole reason it exists: you cannot measure a gap you are standing in
    /// front of by casting from inside yourself.</para></summary>
    public static bool TryFloorBelow(float x, float aboveY, float z, float drop, out float floorY)
    {
        floorY = float.NaN;
        if (!(drop > 0f)) return false;
        try
        {
            if (!EnsureRoute()) return false;
            var result = FieldProbeService.SharedInstance(_resultType);
            if (result == null) return false;

            var avatar = PlayerAvatar();
            var filter = FieldProbeService.Member(CharaController(avatar), "FilterInfo") as ManagedObject;
            if (filter == null) return false;
            if (!Write(_start, x, aboveY, z) || !Write(_end, x, aboveY - drop, z)) return false;

            result.Call("clear");
            _castRayAll.InvokeBoxed(null, null,
                new object[] { _start.View, _end.View, result, filter, DISABLE_BACK_FACING });

            if (!FieldRayContacts.Nearest(result, out float d)) return true;   // ran: nothing under it
            floorY = aboveY - d;
            return true;
        }
        catch (Exception ex)
        {
            if (!_failLogged)
            {
                _failLogged = true;
                API.LogWarning($"[SF6Access] FieldRayCaster floor probe failed: {ex.GetType().Name}: {ex.Message}");
            }
            return false;
        }
    }

    /// <summary><b>How far a body can walk from ANY point of the world, in one direction.</b>
    /// The horizontal twin of <see cref="TryFloorBelow"/>: both exist because every other cast in
    /// this mod leaves the AVATAR, and a route round a corner cannot be measured from inside the
    /// person standing at the corner's near side. You cannot ask "what is past that wall" with a
    /// ray that starts behind the player's own navel.
    ///
    /// <para><b>True means the cast RAN, not that the way is open.</b> <c>hit.Hit == false</c> means
    /// nothing stopped the ray inside <paramref name="maxDistance"/>, which is what an open hop looks
    /// like. False is "the sensor could not run", which no caller may read as either.</para>
    ///
    /// <para><b>Same cast, same container, same filter, same classification</b> as the radar's own
    /// body casts: the avatar's <c>CharacterController.FilterInfo</c>, folded through
    /// <see cref="FieldRayContacts.Gather"/>, so a ramp is not a wall and the avatar's own capsule
    /// is not an obstacle. Only the ORIGIN differs, and nothing else about the lifetime rules
    /// changes — no allocation, no <c>CreateInstance</c>, no second container.</para></summary>
    public static bool TryCastAcross(float ox, float oy, float oz, FieldDirectionService.FlatDir dir,
                                     float maxDistance, out RayHit hit)
    {
        hit = default;
        if (!dir.Ok || !(maxDistance > 0f)) return false;
        try
        {
            if (!EnsureRoute()) return false;
            var result = FieldProbeService.SharedInstance(_resultType);
            if (result == null) return false;

            var avatar = PlayerAvatar();
            var cc = CharaController(avatar);
            var filter = FieldProbeService.Member(cc, "FilterInfo") as ManagedObject;
            if (filter == null) return false;

            var feet = AvatarFieldReader.ReadPlayerPos(null);
            if (!feet.ok) return false;
            float slopeCos = FieldRayMetrics.SlopeCos(cc);
            float waist = FieldRayMetrics.WaistAboveFeet(avatar, cc, feet.y, _vecType);
            float step = FieldRayMetrics.StepAboveFeet(avatar, feet.y, waist, _vecType);
            ulong self = SelfAddress(avatar);

            if (!Write(_start, ox, oy, oz) ||
                !Write(_end, ox + dir.X * maxDistance, oy, oz + dir.Z * maxDistance)) return false;

            result.Call("clear");
            _castRayAll.InvokeBoxed(null, null,
                new object[] { _start.View, _end.View, result, filter, DISABLE_BACK_FACING });

            // Classify against the floor UNDER THIS RAY, not the player's: a step is judged from
            // the ground the walker would be standing on, and a ray across a street is a metre
            // lower or higher than the one the avatar happens to be on.
            hit = FieldRayContacts.Gather(result, oy - FloorToWaistM, self, slopeCos, step);
            return true;
        }
        catch (Exception ex)
        {
            if (!_failLogged)
            {
                _failLogged = true;
                API.LogWarning($"[SF6Access] FieldRayCaster across probe failed: {ex.GetType().Name}: {ex.Message}");
            }
            return false;
        }
    }

    /// <summary>Where a body-column cast sits above the floor it deals with, in metres. The radar
    /// reads the live capsule; a remote ray has no capsule, only the caller's floor height, so this
    /// is the same height by another route: what the middle of that same capsule would be.</summary>
    private const float FloorToWaistM = 0.9f;

    /// <summary>The avatar the player is controlling, through the World Tour player
    /// manager. One place, because every reader here needs the same one.</summary>
    public static ManagedObject PlayerAvatar()
    {
        var pm = API.GetManagedSingleton(PLAYER_MANAGER) as ManagedObject;
        return FlowHelper.Call(pm, "GetAvatarPlayer") as ManagedObject;
    }

    /// <summary>Reusable origin heights, so a 30 Hz sweep allocates nothing.</summary>
    private static readonly float[] OriginY = new float[BODY_HEIGHTS];

    /// <summary>Lateral offsets of a body sweep, in capsule radii: the centre line and
    /// the two sides of the body. Not a tuning number — it is what "does my body fit"
    /// means, the widest pair of points the capsule occupies across its direction of
    /// travel.</summary>
    private static readonly float[] SweepOffsetRadii = { 0f, -1f, 1f };

    private static bool CastColumn(FieldDirectionService.FlatDir[] dirs, float maxDistance,
                                   int heights, RayHit[] hits, bool everyRay)
    {
        if (dirs == null || hits == null || heights < 1 || heights > BODY_HEIGHTS ||
            hits.Length < dirs.Length * heights || !(maxDistance > 0f)) return false;
        try
        {
            if (!EnsureRoute()) return false;

            // ONE globalized container for every cast this process ever makes, cleared
            // before each one. See FieldProbeService.SharedInstance: a container made
            // with NewInstance is a per-thread LOCAL object the engine reclaims, which
            // is what took the game down twice on 2026-09-09 whichever lifetime it was
            // given.
            var result = FieldProbeService.SharedInstance(_resultType);
            if (result == null) return false;

            var avatar = PlayerAvatar();
            var cc = CharaController(avatar);
            var filter = FieldProbeService.Member(cc, "FilterInfo") as ManagedObject;
            var feet = AvatarFieldReader.ReadPlayerPos(null);
            if (filter == null || !feet.ok) return false;

            float waist = FieldRayMetrics.WaistAboveFeet(avatar, cc, feet.y, _vecType);
            if (!(waist > 0f)) return false;
            float slopeCos = FieldRayMetrics.SlopeCos(cc);
            float step = FieldRayMetrics.StepAboveFeet(avatar, feet.y, waist, _vecType);
            ulong self = SelfAddress(avatar);
            float bodyRadius = FieldRayMetrics.Radius(avatar, cc);

            // Which row carries the body sweep: the waist, the row the open/close
            // channel reads. The other two rows stay single rays — they only feed the
            // stairs probe and the approach tone, which ask about a surface, not about
            // whether a body fits.
            int sweptRow = heights == BODY_HEIGHTS ? 1 : 0;
            if (heights == 1) OriginY[0] = feet.y + waist;
            else
            {
                if (!FieldRayMetrics.BodyHeights(avatar, cc, feet.y, _vecType,
                                                 out float lo, out float mi, out float hi))
                    return false;
                OriginY[0] = feet.y + lo;
                OriginY[1] = feet.y + mi;
                OriginY[2] = feet.y + hi;
            }

            bool any = false;
            int measured = 0, wanted = 0;
            for (int i = 0; i < dirs.Length; i++)
            {
                var d = dirs[i];
                if (d.Ok) wanted += heights;
                for (int h = 0; h < heights; h++)
                {
                    int slot = i * heights + h;
                    hits[slot] = default;
                    if (!d.Ok) continue;
                    float oy = OriginY[h];

                    // The body sweep. A single ray asks "what is the nearest surface
                    // along this line", and that question is unstable in a continuous
                    // world: a third-person follow camera drifts a fraction of a degree
                    // between samples, the ray crosses a depth edge, and the answer jumps
                    // by however deep that edge is — 21 m in the session that prompted
                    // this, with the beam flipping back and forth between two static
                    // surfaces 9 cm apart on each return. Three parallel rays a capsule
                    // radius apart ask a different question — "how far can my BODY get" —
                    // and its answer is a property of the space, not of one contact point:
                    // the near side of that edge clips the body's flank even when the
                    // centre line misses it, so it does not flip.
                    bool sweep = h == sweptRow && bodyRadius > 0f;
                    float perpX = -d.Z, perpZ = d.X;
                    var best = default(RayHit);
                    bool got = false;

                    for (int k = 0; k < (sweep ? SweepOffsetRadii.Length : 1); k++)
                    {
                        float off = sweep ? SweepOffsetRadii[k] * bodyRadius : 0f;
                        float sx = feet.x + perpX * off, sz = feet.z + perpZ * off;
                        if (!Write(_start, sx, oy, sz) ||
                            !Write(_end, sx + d.X * maxDistance, oy, sz + d.Z * maxDistance))
                            continue;

                        result.Call("clear");
                        _castRayAll.InvokeBoxed(null, null,
                            new object[] { _start.View, _end.View, result, filter, DISABLE_BACK_FACING });

                        var one = FieldRayContacts.Gather(result, feet.y, self, slopeCos, step);
                        // The body reaches as far as its most obstructed side.
                        if (!got || Nearer(one, best)) { best = one; got = true; }
                    }

                    if (!got) continue;
                    hits[slot] = best;
                    any = true;
                    measured++;
                }
            }

            if (any) LogSummaryOnce(filter);
            return everyRay ? wanted > 0 && measured == wanted : any;
        }
        catch (Exception ex)
        {
            if (!_failLogged)
            {
                _failLogged = true;
                API.LogWarning($"[SF6Access] FieldRayCaster failed: {ex.GetType().Name}: {ex.Message}");
            }
            return false;
        }
    }


    /// <summary>Whether <paramref name="candidate"/> stops the body sooner than
    /// <paramref name="best"/>. A miss never wins: a side of the body that met nothing
    /// says nothing about how far the body can go.</summary>
    private static bool Nearer(in RayHit candidate, in RayHit best)
    {
        if (!candidate.Hit) return false;
        return !best.Hit || candidate.Distance < best.Distance;
    }

    /// <summary>The avatar's collision radius, in metres — how close to a wall the
    /// waist origin can physically get, so it is where a proximity cue must already be
    /// at full strength. Read from the same capsule the casts use; 0 when unreadable.</summary>
    public static float CapsuleRadius()
    {
        try
        {
            var avatar = PlayerAvatar();
            return FieldRayMetrics.Radius(avatar, CharaController(avatar));
        }
        catch { return 0f; }
    }

    /// <summary>Bind the ONE overload this class exists for:
    /// <c>castRayAll(vec3, vec3, CastRayResult, via.physics.FilterInfo, bool)</c>. Picked
    /// by SHAPE, never a signature string — five parameters, a value type first, and a
    /// parameter 4 that is a REFERENCE type, which is what tells it from its twin taking
    /// the <c>eFilterInfo</c> enum (a value type). No silent fallback: absent, this
    /// sensor casts nothing and says so.</summary>
    private static bool EnsureRoute()
    {
        if (_routeTried) return _castRayAll != null;
        _routeTried = true;

        var methods = TDB.Get()?.FindType(COLLISION_SYSTEM)?.GetMethods();
        if (methods != null)
            foreach (var m in methods)
            {
                if (m.Name != "castRayAll") continue;
                var ps = m.GetParameters();
                if (ps == null || ps.Count != 5) continue;
                if (ps[0].Type?.IsValueType() != true) continue;
                if (ps[3].Type == null || ps[3].Type.IsValueType()) continue;
                _castRayAll = m;
                _vecType = ps[0].Type;
                _resultType = ps[2].Type;
                break;
            }
        if (_castRayAll == null)
        {
            API.LogWarning($"[SF6Access] FieldRayCaster: no {COLLISION_SYSTEM}.castRayAll overload takes a " +
                           "via.physics.FilterInfo — the player-filtered sensor is UNAVAILABLE and casts " +
                           "nothing, rather than falling back to the camera's terrain filter unannounced.");
            return false;
        }

        _start ??= FieldOutBuffer.Acquire(_vecType);
        _end ??= FieldOutBuffer.Acquire(_vecType);
        if (_start == null || _end == null)
        {
            API.LogWarning("[SF6Access] FieldRayCaster: " + FieldOutBuffer.Refusal(_vecType));
            _castRayAll = null;
            return false;
        }
        if (FieldRayContacts.Bind(_resultType)) return true;

        API.LogWarning("[SF6Access] FieldRayCaster: CastRayResult publishes no getContactPoint(UInt32); " +
                       "a cast whose contacts cannot be measured is not made at all.");
        _castRayAll = null;
        return false;
    }

    /// <summary>The avatar's capsule. <c>AvatarBase.Components</c> is ONE object and
    /// never a collection — iterating it returns null, a trap already hit once here.</summary>
    private static ManagedObject CharaController(ManagedObject avatar)
    {
        var comps = FieldProbeService.Member(avatar, "Components") as ManagedObject;
        return FieldProbeService.Member(comps, "CharacterController") as ManagedObject;
    }

    private static ulong SelfAddress(ManagedObject avatar)
    {
        try { return (FlowHelper.Call(avatar, "get_GameObject") as ManagedObject)?.GetAddress() ?? 0UL; }
        catch { return 0UL; }
    }

    private static bool Write(FieldOutBuffer buf, float x, float y, float z)
    {
        if (!buf.SetComponent("x", x) || !buf.SetComponent("y", y) || !buf.SetComponent("z", z)) return false;
        // Read back through the same field metadata the engine uses: evidence the value
        // landed where native code looks for it, not a half-filled struct.
        return buf.Component("x") == x && buf.Component("y") == y && buf.Component("z") == z;
    }

    /// <summary>One line, once: the bound overload, the filter it casts with, and every
    /// threshold with the API it came from. Whatever could not be read names itself and
    /// what is lost with it — silent fallbacks are what the audit blamed.</summary>
    private static void LogSummaryOnce(ManagedObject filter)
    {
        if (_summaryLogged) return;
        _summaryLogged = true;
        uint layer = U(FieldProbeService.Member(filter, "Layer", typeof(uint)));
        uint group = U(FieldProbeService.Member(filter, "Group", typeof(uint)));
        uint mask = U(FieldProbeService.Member(filter, "MaskBits", typeof(uint)));

        RouteDescription =
            COLLISION_SYSTEM + ".castRayAll(vec3, vec3, " + (_resultType?.FullName ?? "?") + ", " +
            (filter.GetTypeDefinition()?.GetFullName() ?? "?") + ", bool) with the avatar's own " +
            "CharacterController.FilterInfo layer=" + layer.ToString(CultureInfo.InvariantCulture) +
            " group=" + group.ToString(CultureInfo.InvariantCulture) + " mask=0x" + mask.ToString("X") +
            "; " + FieldRayMetrics.Describe() + "; layer names " +
            (FieldRayContacts.LayerNamesBound ? "ok" : "MISSING (every contact counts as architectural)");
        API.LogInfo("[SF6Access] FieldRayCaster route: " + RouteDescription);
    }

    private static uint U(object boxed)
    {
        try { return boxed == null ? 0u : Convert.ToUInt32(boxed); }
        catch { return 0u; }
    }
}
