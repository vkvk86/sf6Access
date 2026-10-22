using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using REFrameworkNET;
using SF6Access.Hooks.WorldTour;
using static SF6Access.Services.WorldTour.LevelRoutePlanner;

namespace SF6Access.Services.WorldTour;

/// <summary>Routes through ladders when the destination's navmesh region is not connected to the player's.
/// Design and runtime evidence: docs/sf6-screens.md, "World Tour — multi-level routes".</summary>
internal static class LevelRoute
{
    internal enum Phase { Idle, Searching, Following, Failed }

    /// <summary>Where to walk now; <see cref="To"/> is a ladder end unless <see cref="Final"/>.
    /// <see cref="Partial"/>: the plan stops at the spot nearest the target's height. <see cref="Link"/>: the ladder, or -1.
    /// <see cref="Blocked"/>: no walk reaches this ladder's foot; the beacon points straight at it.</summary>
    internal readonly record struct Guidance(Phase Phase, Spot To, bool Final, bool Up, int Leg, int LaddersLeft, bool Partial, int Link,
                                             bool Blocked = false, bool Jump = false, bool JumpUp = false, bool Stair = false, bool Lift = false, bool Drop = false, float DropM = 0f);

    internal const float LevelM = LevelRoutePlanner.LevelM;
    // A scene with several ladders needs one question per ladder per direction in the outward
    // phase alone (7 ladders = 28), before either two-ladder pass has asked anything. The old
    // 48 ran out exactly at the budget ("48 questions (48 queries)" in the 2026-09-29 log) while
    // the target still had near ladders unexplored, so the search reported a partial plan it
    // never finished. 120 is about thirty seconds at FieldNavPath's 250 ms query rate — long to
    // wait, but the alternative the player already rejected is finding the ladder themselves.
    private const int QuestionBudget = 120;
    private const long RetryFailedMs = 30000; // Section streaming can bring other ladders into the scene.
    private const long BrokenLegMs = 3000;    // A leg must stay unreachable this long before replanning.
    private const int MaxReplans = 2;         // A plan that keeps breaking is not a plan.
    private const float NudgeM = 1f;          // About one stride: the floor a ladder serves starts within a step of its end.
    private const float LadderEndMarginM = 1f; // Standing at a ladder end, rail tops included, is not climbing.
    private static float ArriveM => MissionBeaconHooks.ARRIVED_M;
    private static float ReachM => 2f * MissionBeaconHooks.ARRIVED_M;
    private static readonly (float dx, float dz)[] Nudges = { (NudgeM, 0f), (-NudgeM, 0f), (0f, NudgeM), (0f, -NudgeM) };
    /// <summary>The sideways nudges are four; this is the one that comes AFTER them and is not
    /// sideways at all. See <see cref="Moved"/> — it is the try that moves a point onto its own
    /// floor, and until 2026-10-14 there was no such try: four steps around a point hanging a metre
    /// above the ground are still a metre above the ground, so the answer "no" was recorded four
    /// times and then believed.</summary>
    private static readonly int FloorNudge = Nudges.Length + 1;

    private static Phase _phase;
    private static string _key;
    private static Spot _target, _planTarget, _start, _failedFrom;
    private static LevelRoutePlanner _planner;
    private static IReadOnlyList<Leg> _plan;
    private static int _leg, _nudge, _queries, _replans, _budget, _probedLeg = -1;    private static bool _offFrom, _offTo, _askDirect, _abort, _silent, _retrySweep, _partial, _closest;
    private static long _failedAt, _brokenSince;
    private static long _searchStartedAt;
    private static ulong _failedLadders;
    // The measured drops join the plan for one objective only after its first search has come up
    // empty or short — see Links() and Finish(). Kept apart per objective so a scene where the
    // ordinary catalogues DO reach keeps every route it already had.
    private static bool _dropsOn;
    private static string _dropsKey;
    /// <summary>Whether the search that just ran already had the measured drops in it. Set by
    /// <see cref="Links"/>; a search that had them and still came up short has nothing more to
    /// find by looking at the ground again.</summary>
    private static bool _linksHadDrops;

    /// <summary>Starts a spoken ladder search; the direct walk from the player to the target has just failed.</summary>
    internal static void Begin(string key, Spot target, Spot player, bool askDirect)
    {
        int replans = _phase == Phase.Following && key == _key ? _replans + 1 : 0;
        Start(key, target, player, askDirect, QuestionBudget, silent: false);
        _replans = replans;
        LadderCatalog.BeginSweep();
        API.LogInfo(FormattableString.Invariant(
            $"[SF6Access] Level route: no walk from {Show(player)} to {Show(target)} ({target.Y - player.Y:+0.0;-0.0} m vertical); searching ladders"));
    }

    /// <summary>A partial plan has been climbed: the ladders go no nearer, the rest is up to the player.</summary>
    internal static bool AtClosest => _closest;
    internal static bool BackgroundSearch => _phase == Phase.Searching && _silent;

    /// <summary>True while a ladder search is running. The beacon says so out loud: a thirty-second
    /// search in silence is indistinguishable from a hung mod.</summary>
    internal static bool Searching => _phase == Phase.Searching;

    /// <summary>Milliseconds since the running search began, or 0 when none is running.</summary>
    internal static long SearchElapsedMs =>
        _phase == Phase.Searching && _searchStartedAt != 0 ? Environment.TickCount64 - _searchStartedAt : 0;

    /// <summary>The current phase and, while following a plan, the leg the player is on.</summary>
    internal static Guidance Guide(string key, Spot target, Spot player)
    {
        if (_phase != Phase.Idle && key != _key) Reset();
        _target = target;
        if (_phase != Phase.Following)
            return new Guidance(_phase == Phase.Searching && _silent ? Phase.Failed : _phase, target, true, false, 0, 0, false, -1);

        int leg = Progress(_plan, _leg, player, ArriveM, LevelM);
        if (leg != _leg)
        {
            API.LogInfo($"[SF6Access] Level route: leg {_leg} -> {leg} at {Show(player)}");
            _leg = leg;
            _brokenSince = 0;
        }
        var current = _plan[_leg];
        bool final = current.Link < 0;
        if (final && _partial)
        {
            // The end of a partial plan is the last ladder's top. It still belongs to that ladder until LegResult
            // confirms the player stands on the region it leads to: near the top the avatar can still snap to the
            // floor below, and an approach searched from there led back down a 131 m street route (2026-09-26).
            var ladder = _plan[_leg - 1];
            return new Guidance(Phase.Following, current.To, false, ladder.Up, _leg - 1, 1, true, ladder.Link,
                                false, ladder.Jump, JumpLink.IsStepUp(ladder.To.Y, ladder.Exit.Y), ladder.Stair, ladder.Lift,
                                ladder.Drop, ladder.To.Y - ladder.Exit.Y);
        }
        // A drifting target (a walking NPC, a contact point that follows the player) moves the last leg with it.
        var to = final ? new Spot(current.To.X + target.X - _planTarget.X, current.To.Y + target.Y - _planTarget.Y,
                                  current.To.Z + target.Z - _planTarget.Z) : current.To;
        if (current.Blocked && _probedLeg != _leg)
        {
            _probedLeg = _leg;
            ObstacleProbe.Log(_leg > 0 ? _plan[_leg - 1].Exit : player, current.To);
        }
        return new Guidance(Phase.Following, to, final, current.Up, _leg, _plan.Count - 1 - _leg, _partial, current.Link,
                            current.Blocked, current.Jump, current.Jump && JumpLink.IsStepUp(current.To.Y, current.Exit.Y),
                            current.Stair, current.Lift, current.Drop, current.To.Y - current.Exit.Y);
    }

    /// <summary>Feeds back the current leg's route answer; true means hold, the answer says nothing new.</summary>
    internal static bool LegResult(in FieldNavPath.PathAnswer answer, Spot player)
    {
        if (_phase != Phase.Following || answer.Throttled || !answer.Ok) return false;
        // No walk to a blocked ladder is the expected answer, not a broken plan.
        if (_plan[_leg].Blocked) { _brokenSince = 0; return false; }
        bool end = _partial && _plan[_leg].Link < 0;
        if (answer.Exists)
        {
            _brokenSince = 0;
            if (!end) return false;
            // A walk to the last ladder's top exists: the player stands on the region it leads to.
            API.LogInfo($"[SF6Access] Level route: partial plan climbed at {Show(player)}; {_target.Y - player.Y:+0.0;-0.0} m to the objective's height");
            Fail(player);
            _closest = true;
            return true;
        }
        if (answer.StartOffMesh || OnPlannedLadder(player)) return true;
        // Beside the last ladder's top: still stepping off it.
        if (end && Flat(player, _plan[_leg].To) <= ArriveM / 2) return true;
        long now = Environment.TickCount64;
        if (_brokenSince == 0) _brokenSince = now;
        else if (now - _brokenSince > BrokenLegMs)
        {
            API.LogInfo($"[SF6Access] Level route: leg {_leg} unreachable from {Show(player)}; replan {_replans + 1} of {MaxReplans}");
            // The player may have reached the target's region another way, so the direct walk is asked again.
            if (_replans < MaxReplans) Begin(_key, _target, player, askDirect: true);
            else Fail(player);
        }
        return true;
    }

    /// <summary>Advances a running search by at most one navmesh query; call every frame.</summary>
    internal static void Pump()
    {
        if (!FieldNavPath.Enabled) return;
        if (_phase == Phase.Failed) { Retry(); return; }
        if (_phase != Phase.Searching) return;
        if (_planner == null)
        {
            if (!LadderCatalog.Sweep()) return;
            // THE STAIRS, and they come before the planner is built on purpose (2026-10-05).
            // The owner's correction: "我要的不是我都站在楼梯前面了他才认出来，我要的是规划
            // 路线的时候就认出来" — a stair must be one of the connections the search may
            // route through, exactly as a ladder is, so the plan can read "walk to the stair,
            // up it, walk on" and the homing cue can lead there. Measuring the ground around
            // the player first is the only way a stair the player is not facing can ever be in
            // a plan at all; JumpLinkCatalog's line toward the objective cannot see it.
            if (StairCatalog.Sweeping) { StairCatalog.Sweep(); return; }
            // ...and the stairs on the engine's own route, wherever they are — the ones the sweep
            // around the player can never reach. Recalled and measured, no navmesh question.
            if (!RouteStairFinder.Done) { RouteStairFinder.Pump(); return; }
            // The crossings come last and cost a few frames of their own, and no navmesh
            // question at all.
            if (!JumpLinkCatalog.Done) { JumpLinkCatalog.Pump(); return; }
            _planner = new LevelRoutePlanner(_start, _target, Links(), _budget, _askDirect);
        }
        // Resume the search first so the answer to the last question inside the budget still counts.
        var next = _planner.Next();
        if (next is { } q && !_abort) Ask(q);
        else
        {
            // The budget is the planner's own: it stops asking once Asked reaches it, so a null
            // next here means the search finished, not that it was cut off. Log the totals either
            // way so a truncated search can never again look like "no route exists" (2026-09-29).
            if (!_abort && _planner.Asked >= _budget)
                API.LogInfo($"[SF6Access] Level route: question budget spent ({_planner.Asked}), {_queries} navmesh queries; what follows is what was reached");
            Finish();
        }
    }

    /// <summary>Every connection the planner may route through: the ladders in the scene, the
    /// stairs measured around the player, the stairs measured on the route to the objective —
    /// however far away those are — and the crossings measured in front of them.</summary>
    private static List<Link> Links()
    {
        var all = new List<Link>(LadderCatalog.Links);
        all.AddRange(StairCatalog.Links);
        all.AddRange(RouteStairFinder.Links);
        all.AddRange(JumpLinkCatalog.Links);
        // The lifts, last and shortest of all: a platform that carries the player between its own
        // stops. Added 2026-10-07 — see LiftCatalog.AddLinksTo for why its absence made places
        // reachable by riding look like places with no way in at all.
        LiftCatalog.AddLinksTo(all, WorldTourStateService.CityId);

        // <b>When the catalogues came back with nothing that changes floor at all, always.</b>
        // That is the case this was written for: 2026-10-07, Nayshall old town — the objective
        // eight metres below, every catalogue empty, "no plan after 0 questions over 0 ladders",
        // and the guide said to go and find an edge to jump off while eight measured drops stood
        // within twelve metres, six of them with a confirmed walk to their lip.
        //
        // <b>...and when they did NOT come back empty but still did not reach, on the second
        // search.</b> 2026-10-07, Metro City training centre, 17:13: the sweep found three flights
        // of stairs, so CanChangeLevel() was true and the ground around the player was never
        // measured — and the search then ran out after 23 questions with this written in its own
        // log: link #3's LOWER end was connected to the objective while NOTHING connected the
        // player to either of its ends. The way down to that end was a seven-metre drop standing
        // seven metres from the player's feet. It was never in the plan, because a stair somewhere
        // else in the scene had answered "yes" to a question about ladders.
        //
        // So the gate is not "is there a stair" but "did the stairs get us there". The first
        // search is left exactly as it was; only an objective whose first search produced no full
        // route comes back with the measured ground in hand. Nothing that already worked can
        // change, and what was blind is blind no longer.
        // _linksHadDrops: a scene whose every catalogue came back empty gets the drops the first
        // time and every time, so a second search there would measure the same ground and reach
        // the same conclusion — for nothing, and at the cost of every navmesh question again.
        _linksHadDrops = _dropsOn || !CanChangeLevel(all);
        if (_linksHadDrops) DescentScan.AddLinksTo(all);
        return all;
    }

    /// <summary>Do these connections contain any way between floors? If one does, the planner has
    /// a way down already and the measured drops stay out of it.</summary>
    private static bool CanChangeLevel(List<Link> links)
    {
        foreach (var l in links)
            if (MathF.Abs(l.High.Y - l.Low.Y) >= LevelM) return true;
        return false;
    }

    internal static void Reset()
    {
        _phase = Phase.Idle;
        _key = null;
        _planner = null;
        _plan = null;
        JumpLinkCatalog.Reset();
        StairCatalog.Reset();
        RouteStairFinder.Reset();
        _leg = _queries = _replans = 0;
        _probedLeg = -1;
        _brokenSince = 0;
        _abort = _silent = _retrySweep = _partial = _closest = false;
        _searchStartedAt = 0;
        ResetNudge();
    }

    private static void Start(string key, Spot target, Spot player, bool askDirect, int budget, bool silent)
    {
        Reset();
        _phase = Phase.Searching;
        _searchStartedAt = Environment.TickCount64;
        _key = key;
        // Drops are escalated per objective: a new key starts again without them, and an
        // objective that has already escalated keeps them on every retry and replan after it.
        if (_dropsKey != key) { _dropsOn = false; _dropsKey = null; }
        _target = _planTarget = target;
        _start = player;
        _askDirect = askDirect;
        _budget = budget;
        _silent = silent;
        // Alongside the ladders: the stairs around the player, measured before anything is
        // planned. A stair has no gimmick to read, so the ground around the player is swept —
        // see StairCatalog and the note in Pump.
        StairCatalog.BeginSweep(player);
        // ...and the stairs on the ROUTE TO THE OBJECTIVE, which may be any distance away. The
        // owner's correction, 2026-10-05: "我要的是他在规划路线的时候，就把下行的梯子规划出来，跟那个
        // 梯子距离玩家多远没有关系。假设玩家离梯子很远，但是要经过这个梯子怎么办呢？" The sweep
        // above is a circle around the player's own feet and can never reach a stair that lies at
        // the far end of the walk; the engine's already-computed route DOES pass through it, so the
        // route's own bends are where the far stairs are looked for. Recalled from the beacon's
        // cache, never queried — see RouteStairFinder.
        RouteStairFinder.Begin(target, player);
        // Alongside the ladders: the step and the gap in front of the player, measured on
        // the ground. Only ever reached once a walk has failed, and only able to ADD a
        // route where there was none.
        //
        // This belongs here, in the place every search starts from, and not in Begin().
        // It used to live in Begin() only: Retry() goes through Start() -> Reset() ->
        // JumpLinkCatalog.Reset(), which empties the found crossings and marks the scan
        // as never having run, and Pump()'s "if (!JumpLinkCatalog.Done)" gate then reads
        // Done == true and walks straight past it. Every thirty-second retry after the
        // first failure therefore planned over ladders alone and never measured the
        // player's own floor again (2026-10-04: the small scaffold was offered on the
        // first attempt and gone from every attempt after it). Eight rays per retry,
        // no navmesh question — less than one radar sweep.
        JumpLinkCatalog.Begin(player, target);
    }

    /// <summary>After a failure: quietly re-check the direct walk, and the ladders too if they or the player changed.</summary>
    private static void Retry()
    {
        if (Environment.TickCount64 - _failedAt < RetryFailedMs) return;
        if (!_retrySweep) { LadderCatalog.BeginSweep(); _retrySweep = true; }
        if (!LadderCatalog.Sweep()) return;
        var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        if (!me.ok) return;
        var player = new Spot(me.x, me.y, me.z);
        // A drop or a climb between the failure and this retry is a different world even when the
        // flat distance is small: from the roof of the SiRN building site the owner dropped to the
        // ground below (2026-10-02 22:37), the flat check still read "unchanged", so the retry
        // spent its single question on the direct walk, the engine snapped both ends onto the same
        // boundary node and answered "yes" — and the resulting one-question plan displaced the
        // honest end-of-walk state (the hint, the relay) for the rest of the night while the
        // player pushed a wall two metres from the objective.
        bool changed = LadderCatalog.Signature != _failedLadders || Flat(player, _failedFrom) > ReachM
                       || MathF.Abs(player.Y - _failedFrom.Y) >= LevelM;
        // ...and the floor the answers came from. Every ladder question was asked from _start:
        // standing five metres higher than that — because the player climbed the ladder the
        // plan offered, or one they found themselves — makes all of those answers stale, and
        // the check above cannot see it, because it compares against the spot where the search
        // gave up, which IS the new floor (2026-10-03 18:45: the plan was climbed to 22.7 m,
        // the objective was 6 m above and the one ladder that reached it had been judged
        // unreachable from 17.1 m; the retry then spent its single question on the direct
        // walk every thirty seconds and never asked a ladder again). One full search per
        // floor: _start moves with the retry, so standing still afterwards goes back to one.
        //
        // The gate is a STEP, not a floor (2026-10-05). It used to be LevelM (2 m) — a whole
        // avatar height — and that is exactly the height a flight of metal stairs does NOT
        // cross: the 2026-10-05 log has the player climbing a stair 0.89 m over the whole
        // scan, so `climbed` read false, the retry spent its ONE question on the direct walk,
        // answered no, and never looked at a ladder or a stair again — every thirty seconds
        // for the whole session (`question budget spent (1)`). A real ladder climbs 6.3 m
        // and passed the old gate, which is why "the nav is blind to stairs" and not "blind
        // to ladders". A stair's progress is a step, so a step is what the gate measures;
        // standing still still reads false and still costs one question.
        bool climbed = MathF.Abs(player.Y - _start.Y) >= JumpLink.StepM || Flat(player, _start) > ReachM;
        if (climbed && !changed)
            API.LogInfo($"[SF6Access] Level route: the player stands on another floor than the " +
                        $"search was run from ({Show(_start)} -> {Show(player)}); asking the ladders again in full");
        bool closest = _closest;
        Start(_key, _target, player, askDirect: true, changed || climbed ? QuestionBudget : 1, silent: true);
        _closest = closest;
    }

    private static void Ask(Question q)
    {
        Spot from = Moved(q.From, _offFrom), to = Moved(q.To, _offTo);
        var a = FieldNavPath.TryConnect(from.X, from.Y, from.Z, to.X, to.Y, to.Z);
        if (a.Throttled) return;
        _queries++;
        // Could not answer (no navmesh agent yet, streaming): ask again; the query budget bounds it.
        if (!a.Ok) return;
        if (!a.Exists && (a.StartOffMesh || a.EndOffMesh))
        {
            if (_nudge == 0) { _offFrom = a.StartOffMesh; _offTo = a.EndOffMesh; }
            if (_nudge < FloorNudge)
            {
                _nudge++;
                // A VERTICAL TRY WITH NO FLOOR TO MOVE TO IS DROPPED, not spent. It would be the very
                // same question asked at the very same point, and every question here comes out of the
                // one limiter every search in the field shares: thirty seconds is already the price of
                // knowing whether a route exists. See <see cref="Moved"/> for what the try does when
                // there IS a floor.
                if (_nudge == FloorNudge
                    && !((_offFrom && ObjectiveFloor.HasFloor(_planner.SpotOf(q.From)))
                         || (_offTo && ObjectiveFloor.HasFloor(_planner.SpotOf(q.To)))))
                    _nudge = FloorNudge + 1;
                return;
            }
            // No question can reach a target that stays off the navmesh.
            _abort = q.To == Target && _offTo;
            _planner.Answer(false);
            ResetNudge();
            return;
        }
        bool moved = _nudge > 0;
        _planner.Answer(a.Exists, moved && _offFrom ? from : null, moved && _offTo ? to : null);
        ResetNudge();
    }

    private static Spot Moved(int id, bool off)
    {
        var s = _planner.SpotOf(id);
        if (!off || _nudge == 0) return s;
        // THE LAST TRY IS VERTICAL, and it is here for the reason every other nudge cannot serve:
        // four sideways steps around a point that hangs half a metre over its own floor are still
        // half a metre over it. A ladder foot read off the wrong bone, a stair mouth measured to its
        // tread and not to the ground beside it, an objective marker nobody ever promised was at
        // floor level — one column each, and the column is what the engine has no node in.
        //
        // ObjectiveFloor measures the same column both ways (the floor below it, or the floor a
        // buried point stands under) and hands the point back unchanged when neither ray finds
        // anything near enough to belong to it. Nothing here is guessed: if no floor is measured the
        // try is identical to the sideways ones, and costs the same one question.
        if (_nudge == FloorNudge)
        {
            var floor = ObjectiveFloor.At(s);
            return floor.Equals(s) ? s : floor;
        }
        var (dx, dz) = Nudges[_nudge - 1];
        return new Spot(s.X + dx, s.Y, s.Z + dz);
    }

    private static void ResetNudge()
    {
        _nudge = 0;
        _offFrom = _offTo = false;
    }

    private static void Finish()
    {
        _plan = _planner.Plan;

        // <b>2026-10-07 — a search that did not arrive is the reason to look at the ground.</b>
        // A plan that stops short (or no plan at all) is the planner saying "of the connections
        // I was given, none reach". Until now that was the end of it: the guide announced the
        // short leg as a way down and the player walked into a wall. It is not the end of it —
        // the connections it was given were the objects in the catalogues, and a place where the
        // ground simply falls away is not an object. So the search is run once more, from where
        // the player stands now, with those measured drops in hand.
        //
        // Once per objective, so a scene with no way down is not searched for ever; and never
        // after _abort, where the objective itself has no navmesh node and no amount of looking
        // at the ground will conjure one. The player hears "still searching" meanwhile, which is
        // the truth, and the second attempt either arrives or hands back exactly what the first
        // one found — so this can only ever improve on it.
        if (!_dropsOn && !_linksHadDrops && !_abort && (_plan == null || _planner.Partial))
        {
            int had = _planner.LinkCount;
            int asked = _planner.Asked;
            _dropsOn = true;
            _dropsKey = _key;
            var here = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
            var from = here.ok ? new Spot(here.x, here.y, here.z) : _start;
            bool silent = _silent;
            // "The ladders go no nearer" is a verdict about the world, not about this search:
            // a stand reached by climbing is still reached when the search is run again.
            bool closest = _closest;
            API.LogInfo($"[SF6Access] Level route: {(_plan == null ? "no plan" : "only a partial plan")} after {asked} questions " +
                        $"over {had} links; asking again with the drops measured around the player");
            Start(_key, _target, from, askDirect: true, QuestionBudget, silent);
            _closest = closest;
            LadderCatalog.BeginSweep();
            return;
        }

        if (_plan != null)
        {
            bool silent = _silent;
            _phase = Phase.Following;
            _leg = 0;
            _silent = _closest = false;
            _partial = _planner.Partial;
            string end = _partial ? "nearest reachable spot" : "objective";
            API.LogInfo($"[SF6Access] Level route: {(_partial ? "partial " : "")}plan after {_planner.Asked} questions: " +
                        string.Join(" -> ", _plan.Select(l => l.Link < 0 ? $"{end} {Show(l.To)}"
                            : $"{(l.Blocked ? "blocked " : "")}ladder {(l.Up ? "up" : "down")} {Show(l.To)} to {Show(l.Exit)}")));
            if (_partial && !silent) LogFailure();
            return;
        }
        var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        if (!_silent) LogFailure();
        Fail(me.ok ? new Spot(me.x, me.y, me.z) : _start);
    }

    private static void Fail(Spot player)
    {
        _phase = Phase.Failed;
        _silent = _retrySweep = false;
        _failedAt = Environment.TickCount64;
        _failedFrom = player;
        _failedLadders = LadderCatalog.Signature;
    }

    /// <summary>The ladders nearest the target and what was learned about each end, for the next investigation.</summary>
    private static void LogFailure()
    {
        const int Shown = 8;
        string why = _abort ? ", target stays off the navmesh" : "";
        var line = new StringBuilder(
            $"[SF6Access] Level route: no {(_planner.Partial ? "full route" : "plan")} after {_planner.Asked} questions ({_queries} queries{why}) over {_planner.LinkCount} ladders");
        var nearest = Enumerable.Range(0, _planner.LinkCount)
            .OrderBy(i => Math.Min(Distance(_planner.SpotOf(LowId(i)), _target), Distance(_planner.SpotOf(HighId(i)), _target)))
            .Take(Shown);
        foreach (int i in nearest)
            line.Append($"; #{i} low {Show(_planner.SpotOf(LowId(i)))} {Relations(LowId(i))}, " +
                        $"high {Show(_planner.SpotOf(HighId(i)))} {Relations(HighId(i))}");
        API.LogInfo(line.ToString());
    }

    private static string Relations(int id) => $"[target {Word(_planner.Known(id, Target))}, player {Word(_planner.Known(Player, id))}]";
    private static string Word(int known) => known switch { 1 => "yes", 0 => "no", _ => "?" };

    /// <summary>Beside a planned ladder and well between its two ends: climbing, so no route answer means anything.</summary>
    private static bool OnPlannedLadder(Spot p)
        => _plan.Any(l => l.Link >= 0 && Flat(p, l.To) <= ArriveM / 2
                          && p.Y > MathF.Min(l.To.Y, l.Exit.Y) + LadderEndMarginM
                          && p.Y < MathF.Max(l.To.Y, l.Exit.Y) - LadderEndMarginM);

    private static string Show(Spot s) => FormattableString.Invariant($"({s.X:F1}, {s.Y:F1}, {s.Z:F1})");
}
