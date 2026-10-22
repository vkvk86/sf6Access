using System;
using System.Globalization;
using REFrameworkNET;
using static SF6Access.Services.WorldTour.LevelRoutePlanner;

namespace SF6Access.Services.WorldTour;

/// <summary>Selected destination and the aim toward it: a route bend, a ladder on the way, or an unconfirmed nearby approach.</summary>
public readonly struct BeaconDestination
{
    public bool Ok { get; init; }
    /// <summary>The aim is the next bend of a walkable route.</summary>
    public bool ViaRoute { get; init; }
    public bool IsApproach { get; init; }
    /// <summary>At the foot of the destination: the stand-in is reached and the aim is the destination itself.</summary>
    public bool AtFoot { get; init; }
    public bool IsTracked { get; init; }
    public bool NoRoute { get; init; }
    /// <summary>The path query could not produce a readable answer; no steering direction is confirmed.</summary>
    public bool Unconfirmed { get; init; }
    public bool TargetLost { get; init; }
    public bool InContact { get; init; }
    /// <summary>A ladder search is running; there is no aim yet.</summary>
    public bool Searching { get; init; }
    /// <summary>No news this refresh: rate limited, or the avatar is off the navmesh (jumping, on a ladder).</summary>
    public bool Holding { get; init; }
    /// <summary>A cached answer may still guide, but the next native query belongs to this reader.</summary>
    public bool WaitingForQuery { get; init; }
    public long SampledAt { get; init; }
    /// <summary>This leg ends at a ladder rather than at the destination.</summary>
    public bool Ladder { get; init; }
    public bool LadderUp { get; init; }
    /// <summary>This leg ends at a crossing rather than at the destination: a step up onto
    /// a ledge or a jump over a gap, measured and judged by <see cref="JumpLink"/>.</summary>
    public bool Jump { get; init; }
    /// <summary>The crossing goes UP — a step onto something, not a gap to clear.</summary>
    public bool JumpUp { get; init; }
    /// <summary>This leg ends at a flight of narrow stairs the player walks (no action
    /// button), found by <see cref="StairCatalog"/> before the route was planned. Up or down:
    /// since 2026-10-05 a flight may run either way (the owner's ask), and which one it is
    /// decides the single word the sentence must get right.</summary>
    public bool Stair { get; init; }
    /// <summary>The stair leg goes DOWN — the plane walks down the flight rather than up it
    /// (<see cref="NarrowPath.Flight.GoesDown"/>). A down-flight is only planned when its sides
    /// were measured and neither is air, so this flag is never set on a guess.</summary>
    public bool StairDown { get; init; }
    /// <summary><b>This leg ends at a lift — 升降机 — the platform that carries the player
    /// between floors (2026-10-07).</b>
    ///
    /// <para>A lift is the one connection a ladder search never had. Going DOWN seven metres,
    /// the answer is very often not stairs and not an edge to leap off: it is standing on a
    /// platform and letting it carry you. <see cref="LiftCatalog"/> reads every stop height out
    /// of the engine (<c>UpHeight</c>/<c>UnderHeight</c>/<c>StopHeightDataList</c> for
    /// <c>Om006000</c>, plus <c>SetSceneId</c> for the scene lift <c>Om029000</c>), and its
    /// shafts were already in the picker — but never in a plan, so a route out of a cellar or
    /// down a building shaft was reported as unreachable when it plainly was not.</para></summary>
    public bool Lift { get; init; }
    /// <summary>The lift leg goes DOWN: the platform takes the player to a lower stop. The
    /// sentence has to name the direction, because "stand on the lift" alone leaves someone
    /// waiting on a platform going the other way.</summary>
    public bool LiftDown { get; init; }
    /// <summary><b>This leg ends by stepping off a drop — 落差 (2026-10-07).</b>
    ///
    /// <para>A drop is the one way down that is not an object. Ladders, stairs and lifts are all
    /// things placed in the scene, and all of them are catalogued; a place where the ground simply
    /// falls away is not, so it appeared in no plan at all. <see cref="DescentScan"/> measures it
    /// the only way it can be measured — rays at the ground, in a ring around the player.</para>
    ///
    /// <para>Naming it apart from <see cref="Jump"/> is not cosmetic. A jump is a step up onto
    /// something; a drop is nine metres of nothing. Telling someone to step across that is worse
    /// than saying nothing at all.</para></summary>
    public bool Descent { get; init; }
    /// <summary>How far the fall is, in metres — measured, because "a long way" tells a blind
    /// player nothing about whether to brace for it.</summary>
    public float DescentM { get; init; }
    /// <summary><b>\u8fd9\u6bb5\u697c\u68af\u5b9e\u6d4b\u7684\u9ad8\u5ea6\u5dee\uff0c\u7c73\u3002</b> 2026-10-07\uff1a\u4ee5\u524d\u53ea\u8bf4\u300c\u524d\u65b9 6 \u7c73\u6709\u4e00\u6bb5\u697c\u68af\u300d\uff0c
    /// \u4e0d\u8bf4\u5b83\u964d\u591a\u5c11\u3002\u800c\u73a9\u5bb6\u8981\u4e0b 9 \u7c73\uff0c\u90a3\u6bb5\u697c\u68af\u53ea\u964d 1.5 \u7c73\u2014\u2014\u4e0d\u5ff5\u51fa\u8fd9\u4e2a\u6570\uff0c
    /// \u4ed6\u65e0\u6cd5\u5224\u65ad\u8fd9\u662f\u4e0d\u662f\u90a3\u6761\u8def\uff0c\u53ea\u80fd\u4e00\u904d\u904d\u5730\u8d70\u8fc7\u53bb\u8bd5\u3002\u5ff5\u5b9e\u6d4b\u7c73\u6570\u5c31\u662f\u8ba9\u4ed6\u80fd\u6838\u5bf9\u3002</summary>
    public float StairM { get; init; }
    /// <summary><b>\u8d70\u5b8c\u8fd9\u4e00\u6bb5\u4e4b\u540e\uff0c\u79bb\u76ee\u6807\u90a3\u4e00\u5c42\u8fd8\u5dee\u591a\u5c11\u7c73\u3002</b>
    ///
    /// <para>\u975e\u96f6\u7684\u8bdd\u5c31\u662f\u5728\u8bf4\uff1a\u8fd9\u6761\u8def\u5230\u4e0d\u4e86\u76ee\u7684\u5730\u3002 2026-10-07 \u5b9e\u6d4b\uff1a\u89c4\u5212\u5668\u81ea\u5df1\u5199\u4e86
    /// <c>partial plan</c> \u548c <c>no full route</c>\uff0c\u64ad\u62a5\u5374\u8bf4\u300c\u627e\u5230\u80fd\u4e0b\u53bb\u7684\u8def\u300d\u2014\u2014\u660e\u77e5\u5230\u4e0d\u4e86\uff0c\u5374\u7528\u786e\u5b9a\u8bed\u6c14\u3002
    /// \u8fd9\u4e2a\u6570\u662f\u5dee\u591a\u5c11\uff0c\u4e0d\u662f\u300c\u8fd8\u5dee\u4e00\u70b9\u300d\u3002</para></summary>
    public float ShortM { get; init; }
    /// <summary><b>No walk reaches the near end of the leg being followed.</b> The planner picked
    /// the leg; the navmesh, asked for the walk to it, said PathNotFound — it stands on a piece of
    /// mesh nothing walks onto.
    ///
    /// <para>This is not "there is no way down". It is narrower and more useful: the thing named
    /// may well exist and may well descend, but the player cannot walk to it from here, so the
    /// metres quoted for it are a straight line through whatever stands between. Saying "N meters
    /// on" over that number describes a walk that cannot happen, and the player finds that out by
    /// walking into a wall. Set only for a leg that has not reached the objective
    /// (<c>!level.Final</c>), because a final leg's own end IS the objective.</para></summary>
    public bool AimUnreachable { get; init; }
    /// <summary>The ladders lead only as near as they can; the destination is beyond.</summary>
    public bool Partial { get; init; }
    /// <summary>No walk reaches this ladder: something the navmesh does not model stands in the way.</summary>
    public bool Blocked { get; init; }
    /// <summary>A partial route has been climbed; guiding on toward the destination from there.</summary>
    public bool Closest { get; init; }
    /// <summary>A relay hop: the stand-in has done its job and this leg continues to another
    /// reachable place nearer the objective. Still a walk, so every turn and distance works.</summary>
    public bool RelayHop { get; init; }
    /// <summary>The hop ends at the EDGE of the floor: a fall, measured by <see cref="DropEdge"/>
    /// and found by <see cref="DropEdgeFinder"/>, is the only thing the navmesh cannot be asked
    /// about. The walk to it is an ordinary route; the last metres are the player's own drop.</summary>
    public bool Drop { get; init; }
    /// <summary>How far that fall is, in metres.</summary>
    public float DropM { get; init; }
    /// <summary>A route MEASURED out of the world with rays rather than asked of the navmesh: it
    /// does not cross anything that stops a body, because every hop of it was chosen from a cast.
    /// Offered only where the engine's own answer has contradicted itself.</summary>
    public bool Round { get; init; }
    /// <summary>A relay search is running on behalf of a stand already reached: until it answers,
    /// nothing is pointed at, because pointing at the objective aims through the wall between.</summary>
    public bool RelaySearching { get; init; }
    /// <summary>The destination IS a ladder or door the picker listed: guide to it, then say how to use it.</summary>
    public MapSelectionMemory.SelectionKind AccessKind { get; init; }
    /// <summary>The ladder leg's access target (0 when unknown) and lowest rung above its floor.</summary>
    public ulong LadderAccess { get; init; }
    public float BottomRungM { get; init; }
    public int Leg { get; init; }
    public int LaddersLeft { get; init; }
    /// <summary>Aim point: a route bend, or the leg's end.</summary>
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    /// <summary>End of the current leg: the destination, a ladder end or an approach point.</summary>
    public float TargetX { get; init; }
    public float TargetY { get; init; }
    public float TargetZ { get; init; }
    /// <summary><b>Where the OBJECTIVE really is, whatever the current leg happens to end at.</b>
    /// <para><see cref="TargetY"/> is the end of the leg being walked — on a drop leg that is the
    /// lip, level with the player, so it says nothing about whether the goal is below. This is the
    /// goal's own height, kept alongside it, because "is it underneath me" is a question about the
    /// goal and not about the leg. Zero when unknown (nothing filled it in).</para></summary>
    public float GoalY { get; init; }
    /// <summary>The objective's own X, kept apart from <see cref="X"/> — the same reason
    /// <see cref="GoalY"/> exists: while a leg is being followed, <see cref="X"/> is the leg's own
    /// end and not the objective at all, so nothing here can point at the objective without them.</summary>
    public float GoalX { get; init; }
    /// <summary>The objective's own Z — see <see cref="GoalX"/>.</summary>
    public float GoalZ { get; init; }
    /// <summary>The objective's own NAME, kept apart from <see cref="Word"/> for the same reason:
    /// while a leg is followed, <see cref="Word"/> is the leg's own name ("the stairs"), so a
    /// bearing that has been swung back onto the objective would still announce the stairs.</summary>
    public string GoalWord { get; init; }
    public float AboveM { get; init; }
    public float RouteM { get; init; }
    /// <summary>The route's STRAIGHT-line length, or 0 when the engine could not report it. Kept
    /// beside <see cref="RouteM"/> so a walk that goes a long way round can be spoken as one
    /// instead of being announced as an honest distance.</summary>
    public float StraightM { get; init; }
    public string Word { get; init; }
    public string Key { get; init; }
    public int Turn { get; init; }

    /// <summary>How far out of the way this walk goes: path length over straight-line length, or 0
    /// when either is unknown. One number for "the navmesh is sending me the long way round",
    /// because a route round an obstacle it cannot model (a jump, a ledge, a breakable) is still
    /// the only route it knows.</summary>
    public float DetourRatio => StraightM > 0f && RouteM > 0f ? RouteM / StraightM : 0f;

    /// <summary>A mission target farther than this from where it was a refresh ago is a different target (the
    /// next of several), not the same one walking: twice the arrival band, like the route's own reach.</summary>
    private static float MissionJumpM => 2f * Hooks.WorldTour.MissionBeaconHooks.ARRIVED_M;

    private static string _selection, _missionSlot;
    private static Spot _missionAt;
    private static int _missionGeneration;
    private static readonly RouteQueryCache<FieldNavPath.PathAnswer> Cache = new(FieldNavPath.MinQueryIntervalMs * 2);

    public static BeaconDestination Resolve()
    {
        // THE OBJECTIVE IS RESOLVED FIRST AND ONCE, before the player's own pick is consulted, so
        // that an arrival at it is noted even while they are being guided somewhere they chose
        // themselves. Walking past the objective on the way to a shop is still having been at the
        // objective — and a remembered map pick stays set for as long as the player is in the
        // city, so asking only in the branch below would mean that one pick silenced the history
        // for the rest of the session.
        var mission = MissionTargetService.Find();
        if (mission.Ok) NoteReached(mission);

        if (MapSelectionMemory.Exploring)
        {
            Select(null);
            LevelRoute.Reset();
            return default;
        }
        var picked = MapSelectionMemory.Refresh();
        if (picked.Ok)
        {
            Select(picked.Key);
            if (picked.Lost || picked.Pending)
                return new BeaconDestination
                {
                    Ok = true, IsTracked = true, Word = picked.Label, Key = picked.Key,
                    X = picked.X, Y = picked.Y, Z = picked.Z,
                    TargetX = picked.X, TargetY = picked.Y, TargetZ = picked.Z,
                    GoalX = picked.X, GoalY = picked.Y, GoalZ = picked.Z, GoalWord = picked.Label,
                    Holding = picked.Pending, Unconfirmed = picked.Lost, TargetLost = picked.Lost,
                    AccessKind = picked.Kind,
                    SampledAt = Environment.TickCount64,
                };
            return Route(picked.X, picked.Y, picked.Z, picked.Label, true, picked.Kind) with { InContact = picked.InContact };
        }
        if (!mission.Ok) { Select(null); LevelRoute.Reset(); return default; }
        Select(MissionKey(mission));
        return Route(mission.X, mission.Y, mission.Z, LocalizedText.MissionWord(), false,
                     MapSelectionMemory.SelectionKind.Point);
    }

    /// <summary>How near the player must come to an objective before it is kept in the history,
    /// in metres. The owner's own figure (2026-10-06): "只要靠近任务目标两米以内就把这个目标加到历史
    /// 目标里". Deliberately tighter than <see cref="Hooks.WorldTour.MissionBeaconHooks.ARRIVED_M"/>
    /// (4 m), which is the band that says "you are there" for the purposes of the spoken guide —
    /// an entry here is a claim that the player can be walked back to this exact spot, so it is
    /// held to the closer standard.</summary>
    private const float ReachedM = 2f;

    /// <summary>
    /// <b>Keep the objective once the player has actually STOOD at it.</b>
    ///
    /// <para>The first version recorded an objective the moment the story handed it over, which
    /// filled the history with places the player had never been: the very first entry was a
    /// rooftop 200 m away, recorded because the game was pointing at it, and its coordinates were
    /// a promise nobody could walk. The owner's correction names the moment that actually means
    /// something — being within two metres of the thing — and it is the right one for a blind
    /// player especially: an entry recorded on arrival carries a position the player has verified
    /// by standing in it, which is the only kind of entry worth navigating back to.</para>
    ///
    /// <para>The distance measured is the HORIZONTAL one; the height difference is passed along so
    /// it can be logged, but it does not decide. See the note at the measurement itself.</para>
    ///
    /// <para>Called twice a second from <see cref="Resolve"/>, which is plenty: reaching an
    /// objective is something a player stops at, not something they run through.</para>
    /// </summary>
    private static void NoteReached(in MissionTargetService.Target mission)
    {
        var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        if (!me.ok) return;

        float dx = mission.X - me.x, dz = mission.Z - me.z;
        float flat = MathF.Sqrt(dx * dx + dz * dz);
        if (flat > ReachedM) return;

        // THE HEIGHT DIFFERENCE IS MEASURED BUT NOT USED AS A GATE, and that is deliberate.
        // A mission marker's raw Y is not a storey indicator: live on 2026-10-05 a marker read
        // Y=68.0 against the player's 73.7 — a "5.7 m difference" between two points on the SAME
        // flat floor, because a marker sits on its own object's origin chain. Gating on that
        // (this once used LevelM = 2 m) would have refused to record that objective forever, and
        // refused it silently: the player would stand on the objective and the history would not
        // grow, with nothing anywhere saying why. The owner's standing rule for this list is
        // "一条都不能少" — not one may be missing — so completeness beats precision here: the
        // horizontal distance decides, and the height difference is logged so a marker that
        // really is on another floor is visible in the log rather than being guessed at.
        MissionHistory.Record(mission.Slot, mission.Name, mission.X, mission.Y, mission.Z,
                              flat, mission.Y - me.y);
    }

    /// <summary>One key per objective, kept while the game swaps the object that holds it.</summary>
    private static string MissionKey(in MissionTargetService.Target mission)
    {
        var at = new Spot(mission.X, mission.Y, mission.Z);
        if (mission.Slot != _missionSlot || Distance(at, _missionAt) > MissionJumpM)
        {
            _missionSlot = mission.Slot;
            _missionGeneration++;
        }
        _missionAt = at;
        return $"mission:{mission.Slot}:{_missionGeneration}";
    }

    private static void Select(string key)
    {
        if (_selection == key) return;
        _selection = key;
        Cache.Clear();
        ApproachFinder.Reset();
        // A new objective is a new column of world: its floor is somebody else's answer.
        ObjectiveFloor.Reset();
        // A relay searched for the previous selection is a hop toward somewhere else: planning
        // is per-objective, and leaving it behind would guide toward the last target's stand-in.
        RelayPlanner.Reset();
        DropEdgeFinder.Reset();
        RoundPlanner.Reset();
    }

    public static void Reset() { Select(null); Cache.Clear(); }

    private static BeaconDestination Route(float x, float y, float z, string word, bool tracked,
                                           MapSelectionMemory.SelectionKind accessKind)
    {
        var plain = new BeaconDestination
        {
            Ok = true, X = x, Y = y, Z = z, TargetX = x, TargetY = y, TargetZ = z, GoalX = x, GoalY = y, GoalZ = z, GoalWord = word,
            Word = word, IsTracked = tracked, Key = _selection, AccessKind = accessKind, SampledAt = Environment.TickCount64,
        };
        if (!FieldNavPath.Enabled) return plain;
        var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        if (!me.ok) return plain with { Holding = true };
        var player = new Spot(me.x, me.y, me.z);
        var target = new Spot(x, y, z);
        // <b>Put the marker on the floor it belongs to, once, before anything asks a question about
        // it.</b> See <see cref="ObjectiveFloor"/>: an objective's Y hangs from whatever bone its
        // designer picked, and a point in the air has no navmesh node — every route asked about it
        // is refused, and the refusal looks exactly like a wall.
        var aim = ObjectiveFloor.Aim(target);
        // The destination is itself the way up (a ladder the picker listed): the beacon walks
        // the player to it and then says how to use it. Handing it to LevelRoute would ask for
        // ladders TO the ladder, which is not a plan anyone can follow.
        if (accessKind != MapSelectionMemory.SelectionKind.Point)
        {
            var toAccess = RouteTo(plain, aim, word, out var accessAnswer);
            return toAccess with { AccessKind = accessKind, NoRoute = accessAnswer.Ok && !accessAnswer.Exists && !accessAnswer.StartOffMesh };
        }
        // Everything from here on decides things ABOUT THAT HEIGHT — which floor the objective is
        // on, whether a landing or a hop lands beside it, how many metres of it are still missing —
        // so it is told the floor, not the point in the air above it. Two metres is the band every
        // one of those judgements is made in, and a marker that hangs is a metre of that band spent
        // on a metre nobody can stand in.
        var onFloor = aim.Equals(target) ? plain : plain with { TargetY = aim.Y, GoalY = aim.Y };
        var level = LevelRoute.Guide(_selection, aim, player);
        switch (level.Phase)
        {
            case LevelRoute.Phase.Searching:
                return onFloor with { Searching = true, NoRoute = true };
            case LevelRoute.Phase.Failed:
                return Approach(onFloor, player);
            case LevelRoute.Phase.Following:
                // A plan can be followed while the world contradicts it. This is the exact shape of
                // the 13:51 run: the objective three metres away, `plan: objective` claimed by the
                // navmesh, and the player pushing the same hoarding through every turn instruction
                // the plan gave them. So before a route is trusted, ask whether a body can actually
                // get there — and follow the MEASURED way when one exists.
                if (TryRound(onFloor, player, true, out var measured)) return measured;
                var leg = RouteTo(onFloor, level.To, level.Final ? word
                                  : level.Lift ? RouteNotice.LiftWord()
                                  : level.Drop ? RouteNotice.DropWord()
                                  : level.Stair ? RouteNotice.StairWord()
                                  : level.Jump ? RouteNotice.JumpWord() : RouteNotice.LadderWord(), out var legAnswer);
                bool hold = LevelRoute.LegResult(legAnswer, player);
                var grip = LadderCatalog.GripOf(level.Link);
                NoteLeg(level, onFloor.GoalY, player.Y, leg.NoRoute && !level.Final);
                return leg with
                {
                    Holding = leg.Holding || hold, NoRoute = leg.NoRoute && !hold && !level.Blocked, Blocked = level.Blocked,
                    Ladder = !level.Final && !level.Jump && !level.Stair && !level.Lift && !level.Drop,
                    Jump = !level.Final && level.Jump, JumpUp = level.JumpUp, Stair = !level.Final && level.Stair,
                    // The fall's own height, from the plan's own two ends. Measured by the rays
                    // that found it, so it is not a guess about how far down the ground is.
                    Descent = !level.Final && level.Drop, DescentM = level.DropM,
                    // The stair's own height change, and how much is still missing after it. Both
                    // come from the plan's own two ends, so they are measured, not supposed. The
                    // plan's last-but-one field already carries To.Y - Exit.Y for every kind of
                    // leg, not only for drops — a stair is not exempt from saying how far it goes.
                    StairM = MathF.Abs(level.DropM), ShortM = ShortOf(onFloor.GoalY, level),
                    // The lift's own direction: down says "ride it down", and getting that word
                    // wrong leaves someone standing on a platform going the other way.
                    Lift = !level.Final && level.Lift, LiftDown = !level.Final && level.Lift && !level.Up,
                    // The stair's own direction: the leg carries Up from the planner, and for a
                    // stair link "up" means the link's own Low -> High, which is the ground's own
                    // way. So walking it the other way is walking down it.
                    StairDown = !level.Final && level.Stair && !level.Up,
                    LadderUp = level.Up, Leg = level.Leg, LaddersLeft = level.LaddersLeft,
                    Partial = level.Partial, LadderAccess = grip.Access, BottomRungM = level.Up ? grip.BottomRungM : 0f,
                    // THE WALK TO THIS LEG DOES NOT EXIST. The planner chose the leg; the navmesh,
                    // asked how to reach its near end, answered PathNotFound — the leg stands on a
                    // piece of mesh nothing walks onto (2026-10-07, 17:22: a flight of stairs
                    // 42 metres away across a wall, and the words said "Found a way down" over a
                    // distance that was the straight line through that wall). The metres spoken for
                    // such a leg are the straight line, because there is no path length to speak —
                    // so saying "N meters on" describes a walk that cannot happen. This flag is
                    // what stops it, and what swings the cue back to the objective.
                    AimUnreachable = leg.NoRoute && !level.Final,
                };
        }
        var direct = RouteTo(plain, target, word, out var answer);
        if (!direct.NoRoute) return direct;

        // <b>2026-10-14 — ask about the floor before deciding nobody can walk to the marker.</b>
        //
        // A mission marker's Y is a point on some object's own origin chain, not a floor: it can
        // hang half a metre above the platform it belongs to, or be buried a metre inside it, and
        // then every question this file asks is asked about that number. Every one comes back
        // DestNodeNotFound, and the refusals stack up into a verdict about the world — "no ladder
        // reach it", "no route exists" — when what they really describe is a point nobody stands in.
        //
        // The first attempt at this (2026-10-07) only ran when the engine said EndOffMesh, and only
        // ever looked DOWN. Both limits cost routes. Looking down alone cannot find the floor a
        // marker buried inside it stands ON — Metro City training centre, y=-9.8 asked when the
        // walkable floor overhead was -8.0 — and waiting for EndOffMesh meant the narrow case was
        // the ONLY case, though a marker may be refused for a hundred reasons at once.
        //
        // ObjectiveFloor asks once per marker, both ways along the column, and hands the marker back
        // whole when neither ray finds anything near enough to belong to it. It costs one navmesh
        // question, only ever after the direct walk has already failed, and nothing is offered from
        // it unless the walk to the floor is separately confirmed.
        if (!aim.Equals(target))
        {
            var toFloor = RouteTo(plain, aim, word, out var floorAnswer);
            if (!floorAnswer.Throttled && floorAnswer.Ok && floorAnswer.Exists) return toFloor;
        }

        // <b>Before declaring a ladder search, ask whether a stand at the player's OWN level can be
        // reached.</b> The objective's raw Y is not a storey indicator: a mission NPC's marker sits
        // on the NPC's own origin chain, which can be metres below the floor the player walks on.
        // Live, 2026-10-05: the marker read Y=68.0 against the player's 73.7 — a "5.7 m descent"
        // that did not exist — the engine refused the point itself, and a full ladder search ran
        // (and said "no ladder found") for an objective at the end of a flat 6 m walk. The navmesh
        // agreed the ground was flat once asked at the player's own height. So when the height
        // difference is within one step and a reachable stand exists, that stand IS the answer and
        // no ladder is involved. This only ever runs where the old code was about to search
        // ladders anyway, and it only ever returns a point the navmesh confirmed walkable.
        //
        // ASKED ABOUT THE FLOOR since 2026-10-14, which is what that 2026-10-05 evening was asking
        // for and could not get: the height this compares is the marker's, so a marker a metre off
        // its own floor could read as a climb and be handed to the ladder search — the same wrong
        // question asked by a slower route. `aim` is that marker's floor when one was measured, and
        // the marker itself when none was, so nothing that worked here changed.
        if (MathF.Abs(aim.Y - player.Y) < JumpLink.StepM)
        {
            ApproachFinder.Probe(aim.X, aim.Y, aim.Z, player.X, player.Y, player.Z);
            if (ApproachFinder.TryGet(aim.X, aim.Y, aim.Z, player.Y, out float ax, out float ay, out float az)
                && MathF.Abs(ay - player.Y) < LevelRoute.LevelM)
            {
                var nearby = RouteTo(plain, new Spot(ax, ay, az), word, out var nearbyAnswer);
                if (!nearbyAnswer.Ok || nearbyAnswer.Exists)
                    return nearby with { IsApproach = true, AboveM = aim.Y - ay };
            }
        }

        // ...and the ladder search searches for the FLOOR, not for the marker hanging over it. A
        // plan is a chain of navmesh questions and every one of them ends at this point; asking them
        // about air is how a reachable objective ends up logged as having no route at all.
        LevelRoute.Begin(_selection, aim, player, askDirect: answer.EndOffMesh);
        return onFloor with { Searching = true, NoRoute = true };
    }

    // ── The proof, written down ────────────────────────────────────────────────────────────
    //
    // A player who cannot see the stairs cannot check them, and a guide that says "there are
    // stairs here" without saying how far they go is asking to be taken on faith. Faith is not
    // good enough when the answer decides whether someone walks six metres for nothing. So every
    // level-changing leg writes its own evidence: what it is, the height it covers, where it
    // leaves the player, and how much of the way is still missing. Once per leg, on change.
    /// <summary>Write what this leg is and how far it goes — the same numbers the player is told,
    /// so that a claim about stairs can be checked against the world instead of believed.</summary>
    private static void NoteLeg(in LevelRoute.Guidance level, float goalY, float playerY, bool unreachable)
    {
        string kind = level.Lift ? "lift" : level.Drop ? "drop" : level.Stair ? "stair"
                     : level.Jump ? "step" : "ladder";
        float after = level.To.Y - level.DropM;      // where the player stands once it is walked
        float missing = ShortOf(goalY, level);
        string key = string.Format(CultureInfo.InvariantCulture,
            "{0}|{1:F1}|{2:F1}|{3:F1}|{4:F1}", kind, level.To.X, level.To.Y, level.To.Z, after);
        if (key == _notedLeg) return;
        _notedLeg = key;
        API.LogInfo(string.Format(CultureInfo.InvariantCulture,
            "[SF6Access] Leg offered: {0} {1}, {2:F1} m of height, y {3:F1} -> {4:F1}" +
            (unreachable ? " - NO WALK REACHES it, the metres spoken are the straight line" : "") +
            (missing > 0f ? ", {5:F1} m still missing to the objective floor at y {6:F1} - IT DOES NOT ARRIVE"
                          : ", and it arrives") +
            " (player at y {7:F1})",
            level.Partial ? "PARTIAL" : "full plan", kind, MathF.Abs(level.DropM),
            level.To.Y, after, missing, goalY, playerY));
    }
    private static string _notedLeg;

    private static float ShortOf(float goalY, in LevelRoute.Guidance level)
    {
        // Only a PARTIAL plan is short. A full plan has further legs to walk and those legs
        // are the rest of the way down: calling them "missing" would be a lie of the same kind.
        if (!level.Partial || level.Final) return 0f;
        if (goalY == 0f) return 0f;   // the objective's own height is unknown - do not guess it
        float after = level.To.Y - level.DropM;    // where the player stands once this leg ends
        float gap = MathF.Abs(after - goalY);
        return gap >= LevelRoute.LevelM ? gap : 0f;
    }


    // ── What took the place of TryGroundOf ────────────────────────────────────────────────
    //
    // This file used to carry its own floor probe: one ray downward from just above the marker,
    // used once, only when the engine's answer said EndOffMesh. Both of those limits cost routes.
    // Looking down alone cannot find the floor a marker buried inside its own object stands ON
    // (2026-10-07, Metro City: the marker read -9.8 and the walkable floor overhead was -8.0), and
    // waiting for EndOffMesh meant only the narrowest case was ever measured.
    //
    // <see cref="ObjectiveFloor"/> is that probe grown up and moved out: two rays along the one
    // column, measured once per marker, handed back whole when neither finds a floor near enough to
    // belong to it. Everything above asks about what it returns.


    /// <summary>
    /// <b>Is there a way round that has been MEASURED rather than claimed?</b>
    ///
    /// <para>Everything else here believes one of two things: the navmesh refuses this objective,
    /// or it permits it. At a hoarding it does both within one minute, so this asks neither and
    /// believes only a <c>CastRayResult</c> that nothing stopped.</para>
    ///
    /// <para><paramref name="mustReach"/> is the difference between replacing a route and rescuing
    /// one. Following a plan, a diver is followed only when it REALLY reaches the objective —
    /// otherwise it would hijack a long legitimate walk every time something stood across a
    /// straight line, which during a detour it always does. Once the walk has been declared over
    /// there is nothing to hijack, so getting nearer than anywhere else counts.</para>
    /// </summary>
    private static bool TryRound(BeaconDestination plain, Spot player, bool mustReach,
                                 out BeaconDestination found)
    {
        found = default;
        // <b>Height no longer ends the question — until 2026-10-07 it did, and that was backwards.</b>
        // RoundPlanner is the only search here that looks for a way ROUND rather than merely a way
        // nearer, and a destination seven metres below is exactly a place you do not reach by
        // walking toward it. The line that used to stand here returned false for anything involving
        // floors, handing the whole problem to RelayPlanner (a stand, then five short hops) and
        // DropEdgeFinder (a leap off an edge) — and leaving the lateral search idle for the one
        // case it was built for.
        //
        // It runs across floors now, on one condition: a CROSS-FLOOR hop is handed back only when
        // the navmesh confirms somebody can actually walk to it. On one floor a hop that merely
        // gets nearer is worth offering, because nothing claims a route and the player otherwise
        // has nothing. Across floors the same thing would be a confident walk to a ledge nobody can
        // step off, which is the lie this file's rules exist to prevent.
        bool crossFloor = MathF.Abs(player.Y - plain.TargetY) >= LevelM;
        if (Flat(player, new Spot(plain.TargetX, plain.TargetY, plain.TargetZ)) > RoundPlanner.RangeM)
            return false;

        // Only when something stands ACROSS the straight line. Nothing between them means the
        // route already works and there is nothing to find a way round.
        WayRound.Probe(player.X, player.Z, plain.TargetX, plain.TargetZ);
        if (!WayRound.HasWall) return false;

        RoundPlanner.Probe(player.X, player.Y, player.Z, plain.TargetX, plain.TargetY, plain.TargetZ);
        if (!RoundPlanner.HasHop) return false;
        RoundPlanner.Abandoned(player.X, player.Z);
        if (!RoundPlanner.HasHop) return false;
        if (mustReach && !RoundPlanner.ReachesGoal) return false;

        var hop = new Spot(RoundPlanner.X, RoundPlanner.Y, RoundPlanner.Z);
        if (Flat(player, hop) <= Hooks.WorldTour.MissionBeaconHooks.ARRIVED_M
            && MathF.Abs(player.Y - hop.Y) < LevelM)
        {
            // Standing on this hop already: hand over the next one rather than pointing at a
            // place the player has reached, and let this route finish quietly.
            RoundPlanner.Arrived(hop.X, hop.Y, hop.Z);
            return false;
        }
        var next = RouteTo(plain, hop, plain.Word, out var hopAnswer);
        // The condition promised above: across floors, only a hop the navmesh vouches for is worth
        // guiding to. MustReach already demands this of a plan being followed; the shape of the
        // question is identical when there is no plan to hijack. "No answer yet" is refused too:
        // an unconfirmed hop is exactly the confident walk-to-a-ledge this file forbids, and the
        // next refresh asks again.
        if (crossFloor && !(hopAnswer.Ok && hopAnswer.Exists)) return false;
        found = next with
        {
            IsApproach = true, Round = true, AboveM = plain.TargetY - hop.Y,
            NoRoute = hopAnswer.Ok && !hopAnswer.Exists && !hopAnswer.StartOffMesh,
        };
        return true;
    }

    /// <summary>After a failed or partial ladder search: the nearest reachable point below or above the destination.</summary>
    private static BeaconDestination Approach(BeaconDestination plain, Spot player)
    {
        plain = plain with { Closest = LevelRoute.AtClosest };
        // Up a partial route, a stand-in reached only by leaving the block (a 133 m street walk from the roof,
        // 2026-09-26) is not "the foot of" anything: the beacon points at the destination instead.
        float longest = plain.Closest ? Distance(player, new Spot(plain.TargetX, plain.TargetY, plain.TargetZ)) + NeighbourhoodM
                                      : float.PositiveInfinity;
        bool haveStand = ApproachFinder.TryGet(plain.TargetX, plain.TargetY, plain.TargetZ, player.Y,
                                               out float sx, out float sy, out float sz);
        if (!haveStand)
        {
            ApproachFinder.Probe(plain.TargetX, plain.TargetY, plain.TargetZ, player.X, player.Y, player.Z, longestM: longest);
            // The alarm is spoken ONLY once the approach search has genuinely run out of candidates.
            // Until then the walk is not over: it is being measured. Found live, 2026-10-05 — the
            // ladder search failed (correctly), the probe was still working through its ring, and
            // the beacon said "no ladder found" five seconds before it said the true thing
            // ("the objective's foot, 12 o'clock, 6 meters"). Withholding the alarm keeps
            // <see cref="RouteNotice.Searching"/>'s words in place instead, which say a search is
            // running — which is exactly what is happening.
            // <b>2026-10-07: the deadlock that swallowed every objective more than a floor
            // away.</b> It used to return here unconditionally once the ring was used up — and
            // everything below this point then never ran at all. See the long comment further
            // down for why staying is the fix. Nothing changes while candidates remain: the
            // alarm is still withheld exactly as it was.
            if (!ApproachFinder.Exhausted)
                return plain with
                {
                    NoRoute = false,
                    Searching = true,
                    AboveM = plain.TargetY - player.Y,
                    WaitingForQuery = ApproachFinder.WaitingForQuery,
                };
        }
        var stand = new Spot(sx, sy, sz);
        // haveStand guards this: when the ring ran dry TryGet handed back nothing, and sx/sy/sz
        // are zeros — a "place" nobody is standing in and must not be credited as arrived at.
        if (haveStand && Flat(player, stand) <= Hooks.WorldTour.MissionBeaconHooks.ARRIVED_M && MathF.Abs(player.Y - sy) < LevelM)
            ApproachFinder.MarkArrived();
        // At the foot the stand-in has done its job: circling it helps nobody (2026-09-26, 16 m from the boss on
        // his roof), so the sound points at the destination while the player looks for the way up.
        //
        // <b>2026-10-07 — the deadlock this condition used to be.</b>
        // Everything inside this block, from the relay search to the drop-edge scan to the
        // measured way round, used to be reachable only through <c>ApproachFinder.Arrived</c>.
        // But ApproachFinder probes its candidates at the PLAYER'S OWN HEIGHT
        // (<c>ApproachFinder.cs</c>: <c>TryReach(cx, py, cz)</c>) and nowhere else. With the
        // objective seven metres below, the player is standing on the roof of it: the search
        // asks whether the roof around them is walkable, hears yes, and takes that as its
        // answer. It never once reaches the floor the objective is actually on, so it never
        // finds a stand, so <c>Arrived</c> stays false for ever — and the relay, the drop and
        // the way round were never asked even one question. That is how a destination a sighted
        // player simply walks to ends up reported as unreachable.
        //
        // Exhausted says what Arrived says from the other side: this search has nothing further
        // to offer, hand the stage to the searches that DO try the objective's own level. There
        // is nothing here to hijack, and every place handed back below is still one the navmesh
        // itself confirmed walkable.
        if (ApproachFinder.Arrived || ApproachFinder.Exhausted)
        {
            // ... and yet pointing there is what walked the player into a wall at Metro City's
            // building site (owner, 2026-10-02): from the nearest reachable stand, the straight
            // line to the objective runs through whatever put it out of the navmesh's reach.
            // Before settling, ASK WHETHER SOMEWHERE ELSE IS WALKABLE AND NEARER. That is the
            // whole difference between "no way in" and "round this way". See RelayPlanner.
            if (RelayPlanner.HasHop)
            {
                var hop = new Spot(RelayPlanner.X, RelayPlanner.Y, RelayPlanner.Z);
                if (Flat(player, hop) <= Hooks.WorldTour.MissionBeaconHooks.ARRIVED_M
                    && MathF.Abs(player.Y - hop.Y) < LevelM)
                {
                    RelayPlanner.Arrived(hop.X, hop.Y, hop.Z);
                }
                else
                {
                    var next = RouteTo(plain, hop, plain.Word, out var hopAnswer);
                    return next with
                    {
                        IsApproach = true, RelayHop = true, AboveM = plain.TargetY - hop.Y,
                        NoRoute = hopAnswer.Ok && !hopAnswer.Exists && !hopAnswer.StartOffMesh,
                    };
                }
            }
            if (RelayPlanner.Legs < RelayPlanner.MaxLegs && !RelayPlanner.Exhausted)
                RelayPlanner.Probe(plain.TargetX, plain.TargetY, plain.TargetZ, player.X, player.Y, player.Z);

            // THE OBJECTIVE IS BELOW AND NOTHING WALKS TO IT. Before the walk is declared over,
            // look for the edge of the floor the player is standing on. A fall is the one
            // connection a navmesh never models, so this is exactly the case the engine's one bit
            // cannot answer — and "look for an edge to drop from" without a bearing or a distance
            // is the sentence that left the owner five metres above the objective with nowhere
            // named to go (2026-10-03, 13:08: startNode 23061, destNode 22753, PathNotFound, five
            // ladders asked, no relay stand nearer than the one they were already on).
            //
            // The walk TO the edge is an ordinary route: every sample between the player and it is
            // this same floor, unbroken, so every turn and distance already works. Only the last
            // metres are the player's own drop, and <see cref="RouteNotice"/> says how far.
            // SCANNING RUNS ALONGSIDE the relay search, not after it. The relay has to ask the
            // engine about some fifty candidates at four a second, so waiting for it to exhaust
            // meant an edge found a second later was offered thirteen seconds later — and in the
            // 13:33 run the player had stepped off the roof on their own before it ever spoke.
            // The relay still gets the first refusal: a walk that gets nearer beats a fall.
            if (player.Y - plain.TargetY >= LevelM)
                DropEdgeFinder.Probe(player.X, player.Y, player.Z,
                                     plain.TargetX, plain.TargetY, plain.TargetZ);

            if (!RelayPlanner.HasHop && RelayPlanner.Exhausted && player.Y - plain.TargetY >= LevelM)
            {
                if (DropEdgeFinder.HasEdge)
                {
                    var edge = new Spot(DropEdgeFinder.X, DropEdgeFinder.Y, DropEdgeFinder.Z);
                    if (Flat(player, edge) <= Hooks.WorldTour.MissionBeaconHooks.ARRIVED_M
                        && MathF.Abs(player.Y - edge.Y) < LevelM)
                    {
                        // Standing on it and still up here: either they stepped off (the floor
                        // changes and the search restarts on its own) or this edge bought nothing.
                        DropEdgeFinder.Spent();
                    }
                    else
                    {
                        var next = RouteTo(plain, edge, plain.Word, out var edgeAnswer);
                        return next with
                        {
                            IsApproach = true, RelayHop = true, Drop = true, DropM = DropEdgeFinder.DropM,
                            AboveM = plain.TargetY - edge.Y,
                            NoRoute = edgeAnswer.Ok && !edgeAnswer.Exists && !edgeAnswer.StartOffMesh,
                        };
                    }
                }
            }
            // A WAY ROUND, MEASURED INSTEAD OF ASKED. The engine has already answered about this
            // objective and either refused it or offered a route that walked the player into the same
            // hoarding twice (2026-10-03, SiRN: plan found AND PathNotFound for one objective in one
            // minute). Rather than argue with either answer, ask the WORLD: a search whose every hop is
            // a CastRayResult, so a route it returns is one nothing physical stands across.
            //
            // Within a street's length: what stands between further than that is the city, and no
            // local ray sees round the city. Since 2026-10-07 no longer limited to one floor — see
            // TryRound. Everything else — the bearing, the homing cue, the end-of-walk hint —
            // carries on exactly as it did; this only ever offers a walkable first hop where there
            // was none, which is the one thing adding can do. Nothing to hijack here, so even
            // getting nearer than anywhere else measured is worth offering — and across floors
            // only a hop the navmesh itself confirms, which TryRound enforces.
            if (TryRound(plain, player, mustReach: false, out var roundHop)) return roundHop;
            // Nothing nearer walked to (yet): the walk is over and every remaining metre belongs to
            // the player. NoRoute here is what switches the pointing sound off, so the guide says
            // where the objective is without pretending there is a line to it.
            // AtFoot means "standing at the foot of it", and IsApproach means "there is a place worth
            // walking to" — both are claims about a stand that EXISTS. After the unlock above this
            // line is reachable with no stand at all (ApproachFinder ran dry), and asserting either
            // would turn the last sentence into a claim about a place nobody has reached. With no
            // stand it falls back to precisely what it said before: no route, no approach, no foot,
            // which is the honest word until one of the searches above finds something real.
            return plain with
            {
                IsApproach = haveStand, AtFoot = haveStand, NoRoute = true,
                RelaySearching = RelayPlanner.Searching || DropEdgeFinder.Searching || RoundPlanner.Searching,
                AboveM = plain.TargetY - player.Y,
            };
            }
        var dest = RouteTo(plain, stand, plain.Word, out var _);
        return dest with { IsApproach = true, AboveM = plain.TargetY - sy };
    }

    /// <summary>
    /// <b>The point a walk should actually be asked to reach — and the reason it is not simply
    /// <c>TargetX/Y/Z</c>.</b> Returns false when this destination must not be walked to at all.
    ///
    /// <para><b>Why <c>Target</c> alone is wrong (the seventeenth finding).</b> The trial run of
    /// 2026-09-29 15:20 fed <c>dest.TargetX/Y/Z</c> straight into <c>StartNavi</c>. Two different
    /// things go wrong, depending on the leg:</para>
    ///
    /// <list type="bullet">
    /// <item><b>A ladder leg.</b> <c>Target</c> is a LADDER FOOT whose TOP the ladder planner has
    /// already proved unreachable — the 15:20 log says so outright:
    /// <c>#3 low (-234.6, 0.1, -68.2) [target no, player yes]</c>, i.e. its bottom is reachable and
    /// its top is not. Handing that foot to the engine as a <c>Position</c> destination produces a
    /// walk that ends standing at a ladder and pressing into it, with no climb to perform, because
    /// the engine was never told this was a ladder — <c>LadderAccess</c> and <c>BottomRungM</c>
    /// exist for the caller that eventually does tell it.</item>
    /// <item><b>A route leg.</b> <c>X/Y/Z</c> is the first BEND of the route (the homing cue),
    /// deliberately pushed off the objective so the sound is not pinned inside it. Walking to a
    /// bend reaches the bend and stops — and because the beacon advances the bend as the player
    /// advances, the avatar appears to walk THROUGH the objective and out the other side. That is
    /// the reported "walks past the target without stopping".</item>
    /// </list>
    ///
    /// <para><b>What this does instead.</b> It asks the pathfinder for a reachable point that is
    /// genuinely <i>toward</i> the goal: for a route leg, the goal is the leg end and the answer's
    /// first bend is already on a walkable route, so that bend is correct. For a ladder leg, or for
    /// any leg whose own end the planner or the engine has cast doubt on, the point is re-derived
    /// from the player's own position so the walk is always given somewhere a walk can reach.</para>
    ///
    /// <para><b>Costs nothing on the rate limiter.</b> The goal this consults
    /// (<c>X/Y/Z</c> or <c>TargetX/Y/Z</c>) has just been queried by <see cref="Route"/>, so this
    /// reuses the same <see cref="RouteTo"/> cache rather than issuing a competing query — the
    /// mistake that made the 15:08 gate a no-op.</para>
    /// </summary>
    internal bool TryStep(out float x, out float y, out float z, out string why)
    {
        x = y = z = 0f;
        why = null;

        if (!Ok) { why = "no destination"; return false; }
        if (NoRoute) { why = "the engine reports no walkable route"; return false; }
        if (Searching) { why = "a ladder route is still being searched"; return false; }
        // Holding means "no news this refresh", not "blocked": failing towards "keep going" is the
        // recoverable side (the player can stop; the engine has stuck-detection), and the gate that
        // must never fire is a silent refusal.
        if (AtFoot || Closest || IsApproach)
        {
            // Standing at the foot with the objective above: nothing further to walk to, the walk
            // would only circle what it cannot climb. The caller decides what to say; this refuses.
            why = "already as close as a walk can get (the rest is a climb or a search the player does)";
            return false;
        }

        if (ViaRoute)
        {
            // X/Y/Z IS a route bend: a walkable point on the way to the leg end. Correct as-is.
            x = X; y = Y; z = Z;
            return true;
        }

        if (Ladder)
        {
            // The leg ends at a ladder whose top the planner already rejected. Walk to a point that
            // is reachable from HERE and nearer the goal, never to the ladder foot itself.
            var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
            if (me.ok && !Near(TargetX, TargetY, TargetZ, me.x, me.y, me.z))
            {
                var step = StepAmong(TargetX, TargetY, TargetZ, me);
                if (step.Ok)
                {
                    x = step.X; y = step.Y; z = step.Z;
                    return true;
                }
            }
            why = "the ladder leg leads nowhere the engine will route to";
            return false;
        }

        // No route recorded and not a ladder: the leg end is the destination itself.
        x = TargetX; y = TargetY; z = TargetZ;
        return true;
    }

    /// <summary>Is the player already within the mod's arrival band of this point, on the level?</summary>
    private static bool Near(float ax, float ay, float az, float bx, float by, float bz)
    {
        float dx = ax - bx, dz = az - bz;
        return MathF.Sqrt(dx * dx + dz * dz) <= Hooks.WorldTour.MissionBeaconHooks.ARRIVED_M
               && MathF.Abs(ay - by) < 2f;
    }

    /// <summary>Ask for the goal's route and take its first bend, or the goal itself when the
    /// engine reports a route with no readable bend. Reuses <see cref="RouteTo"/>'s cache.</summary>
    private static BeaconDestination StepAmong(float gx, float gy, float gz, (float x, float y, float z, bool ok) me)
    {
        var probe = new BeaconDestination { Ok = true, X = gx, Y = gy, Z = gz, TargetX = gx, TargetY = gy, TargetZ = gz };
        var step = RouteTo(probe, new Spot(gx, gy, gz), probe.Word, out var answer);
        return step with { NoRoute = answer.Ok && !answer.Exists && !answer.StartOffMesh };
    }

    private static BeaconDestination RouteTo(BeaconDestination plain, Spot q, string word, out FieldNavPath.PathAnswer answer)
    {
        long now = Environment.TickCount64;
        answer = FieldNavPath.TryReach(q.X, q.Y, q.Z);
        bool waiting = answer.Throttled;
        long sampledAt = waiting ? 0 : now;
        // Reuse keeps its original age and still reserves a refresh; an actual failure replaces it.
        if (waiting && Cache.TryRead(_selection, (q.X, q.Y, q.Z), now, out var cached, out sampledAt))
            answer = cached;
        else if (!waiting) Cache.Store(_selection, (q.X, q.Y, q.Z), answer, sampledAt);
        bool readableCorner = answer.HasCorner && float.IsFinite(answer.CornerX)
            && float.IsFinite(answer.CornerY) && float.IsFinite(answer.CornerZ);
        var status = RouteAnswerPolicy.Classify(answer.Ok, answer.Exists, answer.Throttled,
            answer.StartOffMesh, readableCorner);
        bool route = status == RouteAnswerStatus.Confirmed;
        return plain with
        {
            X = route ? answer.CornerX : q.X, Y = route ? answer.CornerY : q.Y, Z = route ? answer.CornerZ : q.Z,
            TargetX = q.X, TargetY = q.Y, TargetZ = q.Z, Word = word,
            ViaRoute = route, RouteM = route && float.IsFinite(answer.PathM) && answer.PathM > 0 ? answer.PathM : 0,
            StraightM = route ? answer.StraightM : 0,
            Turn = route ? answer.Turn : 0,
            NoRoute = status == RouteAnswerStatus.Unreachable,
            Unconfirmed = status == RouteAnswerStatus.Unknown, Holding = status == RouteAnswerStatus.Pending,
            WaitingForQuery = waiting, SampledAt = sampledAt,
        };
    }
}
