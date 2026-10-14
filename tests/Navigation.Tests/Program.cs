using SF6Access.Services.WorldTour;

// Dependency-free behavioral tests: run with dotnet run --project tests/Navigation.Tests.
int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    passed++;
}

var route = new RouteLookAhead(0, 0, .5f);
Check(route.Add(0, 0, 0) && !route.HasPoint, "ignore the route origin");
Check(route.Add(0, 0, 1), "retain a close corner candidate");
Check(!route.Add(2, 0, 1), "stop before cutting a right-angle corner");
Check(route.X == 0 && route.Z == 1, "aim remains at the one-metre corner");
Check(route.Turn == -1, "positive X from positive Z is left in the calibrated game basis");

route = new RouteLookAhead(0, 0, .5f);
route.Add(0, 0, 1);
route.Add(-2, 0, 1);
Check(route.Turn == 1, "negative X from positive Z is right");

route = new RouteLookAhead(0, 0, .5f);
Check(route.Add(0, 0, .3f), "corner just outside half a body radius survives");
Check(!route.Add(1, 0, .3f) && route.Z == .3f, "very close bend is not discarded as a wiggle");

route = new RouteLookAhead(0, 0, .5f);
route.Add(0, 0, 2);
Check(!route.Add(0, 0, 4) && route.Z == 4 && route.Turn == 0, "bounded straight look-ahead");

route = new RouteLookAhead(0, 0, .5f);
route.Add(.01f, 0, -1);
Check(route.Add(-.01f, 0, -2) && route.Z == -2, "bearing wraps across 180 degrees without a false turn");

route = new RouteLookAhead(20, 40, .5f);
route.Add(20, 5, 41);
Check(!route.Add(22, 5, 41) && route.X == 20 && route.Y == 5, "world translation and elevation preserve the corner");
Check(!route.Add(float.NaN, 0, 0) && route.X == 20, "invalid points cannot replace a valid aim");

var memory = new NavigationNoticeMemory();
Check(memory.Wants("door-1", "closed", 10, false, 0), "new access is announced");
memory.Spoke("door-1", "closed", 10, false, 0);
Check(!memory.Wants("door-1", "closed", 10, false, 5000), "unchanged access stays silent");
Check(memory.Wants("door-2", "closed", 10, false, 5000), "same description does not merge different doors");
Check(!memory.Wants("door-1", "closed", 4, false, 1000), "approach updates leave time for speech");
Check(memory.Wants("door-1", "closed", 4, false, 5000), "halving distance announces approach");
memory.Spoke("door-1", "closed", 4, false, 5000);
Check(memory.Wants("door-1", "closed", 4, true, 10000), "turning toward an access confirms alignment");
memory.Spoke("door-1", "closed", 4, true, 10000);
Check(!memory.Wants("door-1", "closed", 4, true, 15000), "holding alignment does not chatter");
Check(memory.Wants("door-1", "open", 4, true, 15001), "door availability changes are news");
Check(!memory.Wants("bad", "open", float.NaN, false, 15002), "invalid distance is not a discovery");
Check(!memory.Wants("bad", "open", -1, false, 15002), "negative distance is refused");
memory.Prune(40002);
Check(memory.Wants("door-1", "closed", 4, true, 40002), "streamed-out identities expire");
memory.Spoke("door-1", "closed", 4, true, 40002);
memory.Clear();
Check(memory.Wants("door-1", "closed", 4, true, 40003), "new area has no stale announcements");

var first = new NavReading(FrontBlock.Blocked, FrontProfile.Wall, true, 1, false, false, false, true);
var sides = new NavReading(FrontBlock.Blocked, FrontProfile.Wall, true, .5f, false, true, true, true);
Check(first.SameStateAs(sides), "side flicker cannot suppress a confirmed front warning");
var drop = new NavReading(FrontBlock.Blocked, FrontProfile.Wall, true, 1, false, false, false, false);
Check(!first.SameStateAs(drop), "ground loss remains a state change");
Check(!first.SameStateAs(default), "missing sensor data is not a valid baseline");
// Level routes: ground (y 0), roof A (y 12, x < 50), roof C (y 12, x >= 50), roof B (y 20), tower D (y 30).
string Region(LevelRoutePlanner.Spot s) => s.Y < 6 ? "ground" : s.Y > 25 ? "D" : s.Y > 16 ? "B" : s.X < 50 ? "A" : "C";
LevelRoutePlanner Plan(LevelRoutePlanner.Spot from, LevelRoutePlanner.Spot to, LevelRoutePlanner.Link[] links,
                       int budget = 48, Func<LevelRoutePlanner.Spot, LevelRoutePlanner.Spot>? snap = null)
{
    var planner = new LevelRoutePlanner(from, to, links, budget);
    while (planner.Next() is { } q)
    {
        var a = planner.SpotOf(q.From);
        var b = planner.SpotOf(q.To);
        if (snap == null) { planner.Answer(Region(a) == Region(b)); continue; }
        var sa = snap(a); var sb = snap(b);
        planner.Answer(Region(sa) == Region(sb), sa, sb);
    }
    return planner;
}
var ground = new LevelRoutePlanner.Spot(5, 0, 5);
var roofA = new LevelRoutePlanner.Spot(5, 12, 5);
var roofB = new LevelRoutePlanner.Spot(25, 20, 0);
var toA = new LevelRoutePlanner.Link(new(0, 0, -1), new(0, 12, 0));
var aToB = new LevelRoutePlanner.Link(new(10, 12, 0), new(20, 20, 0));
var toC = new LevelRoutePlanner.Link(new(100, 0, 0), new(100, 12, 50));

var level = Plan(ground, roofA, new[] { toC, toA });
Check(level.Plan is { Count: 2 } && level.Plan[0].Link == 1 && level.Plan[0].Up, "one ladder up to a roof");
Check(level.Plan![0].To == toA.Low && level.Plan[0].Exit == toA.High, "walk to the ladder foot, leave at its top");
Check(level.Plan[1].Link == -1 && level.Plan[1].To == roofA, "last leg is the destination");
Check(level.Asked == 2, "nearest ladder answers in two questions");

level = Plan(roofA, new(5, 0, 20), new[] { toA });
Check(level.Plan is { Count: 2 } && !level.Plan[0].Up && level.Plan[0].To == toA.High, "one ladder down from a roof");

level = Plan(ground, roofB, new[] { toC, aToB, toA });
Check(level.Plan is { Count: 3 } && level.Plan[0].Link == 2 && level.Plan[1].Link == 1, "two ladders chain ground, roof A, roof B");

var tower = new LevelRoutePlanner.Spot(30, 30, 10);
level = Plan(ground, tower, new[] { toC, toA, aToB });
Check(level.Finished && level.Partial && level.Plan is { Count: 3 } && level.Plan[0].Link == 1 && level.Plan[1].Link == 2,
      "an unreachable tower climbs as high as the ladders go");
Check(level.Plan![2].Link == -1 && level.Plan[2].To == aToB.High, "a partial plan stops at the highest reached ladder top");
level = Plan(roofB, tower, new[] { toC, toA, aToB });
Check(level.Finished && level.Plan == null, "no ladder nearer the target's height, no plan");
level = Plan(ground, new(80, 30, 80), new[] { toC, toA, aToB });
Check(level.Finished && level.Plan == null, "a ladder top in another block is not a destination");
var hidden = new LevelRoutePlanner.Link(new(60, 30, 0), new(60, 40, 0));
level = Plan(ground, tower, new[] { hidden, toA });
Check(level.Partial && level.Plan is { Count: 2 } && level.Plan[0].Link == 1, "a ladder whose foot cannot be walked to is left out");
level = Plan(ground, new(9, 1, 9), new[] { toA }, budget: 48);
Check(level.Plan == null, "a destination on the player's own level gets no partial climb");
level = Plan(ground, roofA, new[] { toC, toA }, budget: 1);
Check(level.Finished && level.Plan == null && level.Asked == 1, "question budget bounds the search");
level = Plan(ground, roofA, new[] { toC, toA }, budget: 2);
Check(level.Plan is { Count: 2 }, "the last answer inside the budget can still complete a plan");
level = Plan(ground, roofA, Array.Empty<LevelRoutePlanner.Link>());
Check(level.Finished && level.Plan == null && level.Asked == 0, "no ladders, no questions");

// --- Stairs: found by the sweep BEFORE planning, and routed through like a ladder
// (2026-10-05, the owner's ask: "规划路线的时候就认出来，然后归航音带我走到目标处") ---
// A stair is not a crossing: the player walks up it with no action button, and it must be
// one of the connections the search may plan through, not something noticed at its foot.
var stairUp = new LevelRoutePlanner.Link(new(5, 0, 0), new(5, 12, 0), false, true);
Check(stairUp.Stair && !stairUp.Jump, "a stair is a stair and not a crossing");
Check(!toA.Stair, "a ladder is not a stair");
level = Plan(ground, roofA, new[] { stairUp });
Check(level.Plan is { Count: 2 } && level.Plan[0].Stair && level.Plan[0].Up,
      "a stair the sweep found is a connection the plan goes up, exactly as a ladder is");
Check(level.Plan![0].To == stairUp.Low && level.Plan[0].Exit == stairUp.High,
      "the plan walks to the foot of the stair and leaves at its top");
Check(level.Plan[1].Link == -1 && level.Plan[1].To == roofA,
      "the stair's top is followed by the walk to the destination");

// ...and the same stair walked the OTHER way. The owner's ask, 2026-10-05: "我要的不仅是按F4以后他能走
// 下行楼梯，而是找任务目标的时候也能找下行楼梯." A flight is a connection with two directions, exactly
// as a ladder is, so a mission search that must get from a roof DOWN to the ground has to be able to
// plan the descent — and it must say "down", not "up".
var roofDown = new LevelRoutePlanner.Spot(5, 12, 5);
level = Plan(roofA, ground, new[] { stairUp });
Check(level.Plan is { Count: 2 } && level.Plan[0].Stair && !level.Plan[0].Up,
      "a stair is planned DOWN when the destination is below: the search finds a descent, not only a climb");
Check(level.Plan![0].To == stairUp.High && level.Plan[0].Exit == stairUp.Low,
      "walking down, the plan walks to the stair's HIGH end and leaves at its low end");
Check(level.Plan[1].Link == -1 && level.Plan[1].To == ground,
      "the stair's foot is followed by the walk to the destination below");
// The two directions of one stair are two different legs, and the one that is chosen is the one
// that reaches the destination — never a climbing leg for a destination down a storey.
Check(Plan(roofA, ground, new[] { stairUp }).Plan![0].Up == false
      && Plan(ground, roofA, new[] { stairUp }).Plan![0].Up,
      "one stair serves both directions: up when the target is above, down when it is below");
// Two flights chain downward as they chain upward — a descent with a landing is still a route.
var stairDown2 = new LevelRoutePlanner.Link(new(5, -12, 0), new(5, 0, 0), false, true);
var cellar = new LevelRoutePlanner.Spot(5, -12, 5);
level = Plan(ground, cellar, new[] { stairDown2, stairUp });
Check(level.Plan is { Count: 2 } && level.Plan[0].Stair && !level.Plan[0].Up
      && level.Plan[0].To == stairDown2.High && level.Plan[0].Exit == stairDown2.Low,
      "a second descent is chained from the landing, exactly as an up-flight is");
// Two stairs chain, as two ladders do.
var stairOn = new LevelRoutePlanner.Link(new(5, 12, 0), new(5, 20, 0), false, true);
level = Plan(ground, roofB, new[] { stairOn, stairUp });
Check(level.Plan is { Count: 3 } && level.Plan[0].Stair && level.Plan[1].Stair
      && level.Plan[0].Link == 1 && level.Plan[1].Link == 0,
      "two stairs chain: walk to the first, up it, up the second");

// --- Lifts: the platform that carries the player between floors (2026-10-07) ---
// Of all the ways down seven metres, riding is the one a ladder search never modelled, and
// it was missing entirely: the planner's link table held ladders, stairs and crossings and
// nothing else. A lift's two ends stand at the SAME map position and differ only in height,
// so nothing about the search changes — it is a Link like any other. These two cases are the
// shape of the reported failure: a destination directly above or below the player.
var shaft = new LevelRoutePlanner.Link(new(30, 0, 10), new(30, 20, 10), Lift: true);
Check(shaft.Lift && !shaft.Stair && !shaft.Jump, "a lift is a lift, not a stair and not a crossing");
Check(!stairUp.Lift, "a stair is not a lift");
var belowDeck = new LevelRoutePlanner.Spot(30, 0, 12);
var aboveDeck = new LevelRoutePlanner.Spot(30, 20, 12);
level = Plan(aboveDeck, belowDeck, new[] { shaft });
Check(level.Plan is { Count: 2 } && level.Plan[0].Lift && !level.Plan[0].Up,
      "a lift plans DOWN to a destination below: the player rides it, they do not climb");
Check(level.Plan![0].To == shaft.High && level.Plan[0].Exit == shaft.Low,
      "riding down, the plan walks to the lift's TOP stop and leaves at the bottom one");
Check(level.Plan[1].Link == -1 && level.Plan[1].To == belowDeck,
      "the lift's lower stop is followed by the walk to the destination");
level = Plan(belowDeck, aboveDeck, new[] { shaft });
Check(level.Plan is { Count: 2 } && level.Plan[0].Lift && level.Plan[0].Up
      && level.Plan[0].To == shaft.Low && level.Plan[0].Exit == shaft.High,
      "the same lift plans UP when the destination is above");
// The flag has to survive into the leg: it is the only thing that tells the beacon to say
// "stand on it" instead of "face it and press the action button", and a player told to climb
// a lift platform steps onto it and walks off the far side.
Check(Plan(aboveDeck, belowDeck, new[] { shaft }).Plan![0].Lift
      && !Plan(ground, roofA, new[] { toA }).Plan![0].Lift,
      "a lift leg carries its own kind, so the words fit what the player has to do");

// --- Descents: where the ground simply falls away (2026-10-07) ---
// A drop is the one way down that is no object at all. Ladders, stairs and lifts are placed in
// the scene and catalogued; a place where the ground falls away is neither, so it was in no
// catalogue and therefore in no plan — while eight of them stood measured within twelve metres
// of the player. These are the shape of that failure: the destination below, and the nearest
// way down not directly underneath it.
var ledge = new LevelRoutePlanner.Link(new(72, -10, 80), new(70, -1, 78), Drop: true);
Check(ledge.Drop && !ledge.Lift && !ledge.Stair && !ledge.Jump,
      "a descent is a descent, not a lift, a stair or a crossing");
Check(!shaft.Drop, "a lift is not a descent");
var onTop = new LevelRoutePlanner.Spot(66, -1, 77);
var atBottom = new LevelRoutePlanner.Spot(74, -10, 82);
level = Plan(onTop, atBottom, new[] { ledge });
Check(level.Plan is { Count: 2 } && level.Plan[0].Drop && !level.Plan[0].Up,
      "a descent plans DOWN to a destination below: the player steps off, they do not climb");
Check(level.Plan![0].To == ledge.High && level.Plan[0].Exit == ledge.Low,
      "going down, the plan walks to the lip of the drop and leaves at the landing");
Check(level.Plan[1].Link == -1 && level.Plan[1].To == atBottom,
      "the landing is followed by the walk to the destination");
// Nine metres is not a step. If this flag leaked onto an ordinary leg, the beacon would say
// "step across" about a fall, which is worse than saying nothing.
Check(Plan(onTop, atBottom, new[] { ledge }).Plan![0].Drop
      && !Plan(ground, roofA, new[] { toA }).Plan![0].Drop,
      "a descent leg carries its own kind, so the words fit a fall and not a step");

// --- 2026-10-14, Metro City training centre twice in one night: the marker was never on the floor ---
//
// Two evenings apart, the same metric picture. An objective read y=-8.8 while every drop that could
// reach it landed at -9.8: eight measured ways down, and not one second leg the engine would confirm.
// Later the same week the marker read -9.8 while the walkable floor ABOVE it was -8.0. Neither is a
// story about stairs or walls. Both are a question asked about a point no avatar has ever stood in,
// which a navmesh answers the only way it can: there is no node there, therefore there is no route.
//
// DropEdge.FloorOf is the judgement; ObjectiveFloor is what measures the two floors it chooses
// between. Nothing here touches the engine, because nothing here has to.
Check(DropEdge.Hangs(-8.8f, -9.8f), "a marker a metre above its own floor is hanging, not standing on anything");
Check(!DropEdge.Hangs(-9.8f, -9.8f), "a marker already on the floor is left exactly where it is");
Check(!DropEdge.Hangs(-8.0f, -20f), "twelve metres of air is another floor, not a hanging marker");
Check(DropEdge.FloorOf(-8.8f, -9.8f, float.NaN) == -9.8f,
      "the floor UNDER a hanging marker is what every route is asked about");
Check(DropEdge.FloorOf(-9.8f, -25f, -8.0f) == -8.0f,
      "the floor a buried marker stands ON is what every route is asked about");
Check(DropEdge.FloorOf(-8.8f, -9.8f, -8.0f) == -8.0f,
      "of two floors in one column, the nearer one is the marker's own");
Check(float.IsNaN(DropEdge.FloorOf(-8.8f, float.NaN, float.NaN)),
      "no floor measured means nothing corrected: the marker is handed back whole");
Check(float.IsNaN(DropEdge.FloorOf(-8.8f, -9.0f, float.NaN)),
      "a marker twenty centimetres off the ground is standing on it, and no question moves");

// --- 2026-10-07, 17:13, Metro City training centre: the measured shape of one night's failure ---
// The objective stood 7.2 m below and 22 m away. The sweep found three flights of stairs, so the
// ground around the player was never measured, and the search ran out after 23 questions with
// this written in its own log: "#3 low (67.6, -9.8, -88.7) [target yes, player no], high
// (68.5, -7.1, -85.3) [target no, player no]" — the stair's foot was on the objective's floor
// and nothing at all reached the stair. The way down to it was a seven-metre drop standing
// seven metres from the player's feet, and it was never in the plan, because a stair elsewhere
// in the same scene had already answered "yes" to "can anything here change floor?".
//
// These are the real numbers. What follows is the proof that putting the drop in the plan is
// what opens the route — the whole reason Links() now asks for it again when a search comes up
// short.
LevelRoutePlanner PlanIn(LevelRoutePlanner.Spot from, LevelRoutePlanner.Spot to, LevelRoutePlanner.Link[] links)
{
    var planner = new LevelRoutePlanner(from, to, links, 120);
    while (planner.Next() is { } q)
        planner.Answer(TcRegion(planner.SpotOf(q.From)) == TcRegion(planner.SpotOf(q.To)));
    return planner;
}
// Five floors: the player's own, the stair's landing at -4.4, the isolated lip at +0.7, the
// landing at -7.1, and the objective's floor below that.
string TcRegion(LevelRoutePlanner.Spot s)
    => s.Y > 0f ? "lip" : s.Y > -2f ? "floor" : s.Y > -6f ? "mid4" : s.Y > -8f ? "mid7" : "target";

var tcPlayer = new LevelRoutePlanner.Spot(70.4f, -0.8f, -78.0f);
var tcTarget = new LevelRoutePlanner.Spot(66.6f, -8.0f, -92.5f);
// #0 the stair the plan actually offered: both its ends unreachable. #1 and #2 two flights on
// the player's own floor. #3 the one that matters: its foot is on the objective's floor and its
// top is not. #4 a 0.4 m step on the player's floor — noise, and the player CAN walk to its top.
var tc0 = new LevelRoutePlanner.Link(new(77.3f, -4.4f, -84.8f), new(77.8f, 0.7f, -85.4f), Stair: true);
var tc1 = new LevelRoutePlanner.Link(new(68.7f, -0.8f, -79.7f), new(66.8f, -0.6f, -80.5f), Stair: true);
var tc2 = new LevelRoutePlanner.Link(new(65.3f, -0.8f, -81.1f), new(67.6f, -0.6f, -80.8f), Stair: true);
var tc3 = new LevelRoutePlanner.Link(new(67.6f, -9.8f, -88.7f), new(68.5f, -7.1f, -85.3f), Stair: true);
var tc4 = new LevelRoutePlanner.Link(new(69.2f, -1.2f, -82.7f), new(69.4f, -0.8f, -81.9f), Stair: true);
// FIRST SEARCH: the five catalogued connections, exactly as the scene gave them up.
level = PlanIn(tcPlayer, tcTarget, new[] { tc0, tc1, tc2, tc3, tc4 });
Check(level.Finished && level.Partial && level.Plan is { Count: 2 } && level.Plan[0].Link == 0 && level.Plan[0].Blocked,
      "three stairs in the scene and not one of them reaches the objective: a blocked hop is all there is");
// ...and the drop the rays measured: a lip on the player's own floor, a landing on the very
// floor #3's top stands on.
var tcDrop = new LevelRoutePlanner.Link(new(68.5f, -7.1f, -85.3f), new(69.6f, -0.8f, -83.0f), Drop: true);
level = PlanIn(tcPlayer, tcTarget, new[] { tc0, tc1, tc2, tc3, tc4, tcDrop });
Check(level.Plan is { Count: 3 } && !level.Partial,
      "with the drop measured, the route opens: walk to the lip, step off, then take the stair down");
Check(level.Plan![0].Drop && level.Plan[0].To == tcDrop.High && level.Plan[0].Exit == tcDrop.Low,
      "the first leg is the drop: walked to its lip, left at its landing");
Check(level.Plan[1].Link == 3 && level.Plan[1].Stair && !level.Plan[1].Up && level.Plan[1].To == tc3.High,
      "the second leg is the stair whose foot is on the objective's floor, taken downwards");
Check(level.Plan[2].Link == -1 && level.Plan[2].To == tcTarget,
      "the last leg is the objective itself, not a stand-in for it");

var moved = new LevelRoutePlanner.Spot(1, 12, 0);
level = Plan(ground, roofA, new[] { toA }, snap: s => s == toA.High ? moved : s);
Check(level.Plan![0].Exit == moved, "an endpoint moved onto the navmesh replaces the ladder end");

level = new LevelRoutePlanner(ground, roofA, new[] { toA }, 48);
Check(level.Known(LevelRoutePlanner.Player, LevelRoutePlanner.Target) == 0, "direct failure is known from the start");
var twin = new LevelRoutePlanner.Link(new(0, 0, -2), new(0, 12, 1));
level = new LevelRoutePlanner(ground, new(80, 30, 80), new[] { toA, twin }, 48);
int targetQuestions = 0;
while (level.Next() is { } tq)
{
    if (tq.To == LevelRoutePlanner.Target) targetQuestions++;
    level.Answer(Region(level.SpotOf(tq.From)) == Region(level.SpotOf(tq.To)));
}
Check(targetQuestions == 4, "each ladder end is asked about the target once");
level = new LevelRoutePlanner(ground, new(9, 0, 9), new[] { toA }, 48, askDirect: true);
Check(level.Next() is { From: LevelRoutePlanner.Player, To: LevelRoutePlanner.Target }, "an off-mesh target asks the direct walk first");
level.Answer(true, to: new LevelRoutePlanner.Spot(9, 0, 8));
Check(level.Next() == null && level.Plan is { Count: 1 } && level.Plan[0].To == new LevelRoutePlanner.Spot(9, 0, 8),
      "a target moved onto the navmesh can be walked to directly");

// --- Bidirectional: the target's world is searched too, and the fronts splice ---
var bToD = new LevelRoutePlanner.Link(new(20, 20, 5), new(30, 30, 10));
level = Plan(ground, tower, new[] { toC, toA, aToB, bToD });
Check(level.Plan is { Count: 4 } && !level.Partial, "a three-ladder chain: both fronts are searched and meet");
Check(level.Plan![0].Link == 1 && level.Plan[0].Up && level.Plan[0].To == toA.Low && level.Plan[0].Exit == toA.High,
      "the spliced route starts with the player's own ladder up");
Check(level.Plan[1].Link == 2 && level.Plan[1].Up && level.Plan[2].Link == 3 && level.Plan[2].Up,
      "the target's ladders are replayed backwards, each climbed from the player's side");
Check(level.Plan[3].Link == -1 && level.Plan[3].To == tower, "the spliced route ends at the objective");
Check(level.Asked < 30, "meeting halfway answers fewer questions than the budget allows");

level = Plan(tower, ground, new[] { toC, toA, aToB, bToD });
Check(level.Plan is { Count: 4 } && !level.Partial && level.Plan[0].Link == 3 && !level.Plan[0].Up
      && level.Plan[1].Link == 2 && !level.Plan[1].Up && level.Plan[2].Link == 1 && !level.Plan[2].Up,
      "the same chain searched from the roof splices the same ladders down");

// Chapter 2-2: street, a 5 m roof split by a sign the navmesh does not model, a 10 m roof, the boss apart.
var boss = new LevelRoutePlanner.Spot(15, 12.2f, 17);
string Block(LevelRoutePlanner.Spot s) => s == boss ? "boss" : s.Y < 2.5f ? "street" : s.Y >= 7.5f ? "roof10" : s.Z < 8 ? "roof5" : "behindSign";
var ladderA = new LevelRoutePlanner.Link(new(0, 0, 0), new(0, 5, 0));
var ladderB = new LevelRoutePlanner.Link(new(0, 4.8f, 10.5f), new(0, 10.1f, 10.5f));
level = new LevelRoutePlanner(new(20, 0, -20), boss, new[] { ladderB, ladderA }, 48);
while (level.Next() is { } bq) level.Answer(Block(level.SpotOf(bq.From)) == Block(level.SpotOf(bq.To)));
Check(level.Partial && level.Plan is { Count: 3 } && level.Plan[0].Link == 1 && !level.Plan[0].Blocked,
      "a sign-split roof: the first ladder is walked to");
Check(level.Plan![1].Link == 0 && level.Plan[1].Blocked && level.Plan[2].To == ladderB.High,
      "the ladder behind the sign is a blocked leg, and the plan stops at its top");

var legs = new[]
{
    new LevelRoutePlanner.Leg(toA.Low, toA.High, 0, true),
    new LevelRoutePlanner.Leg(roofA, roofA, -1, false),
};
Check(LevelRoutePlanner.Progress(legs, 0, new(20, 0, 20), 4, 2) == 0, "walking to the ladder stays on the first leg");
Check(LevelRoutePlanner.Progress(legs, 0, new(0.5f, 12, 0.5f), 4, 2) == 1, "standing at the ladder top advances");
Check(LevelRoutePlanner.Progress(legs, 0, new(0, 7, 0), 4, 2) == 0, "halfway up the ladder is not the top yet");
Check(LevelRoutePlanner.Progress(legs, 1, new(6, 12, 6), 4, 2) == 1, "walking on the roof keeps the last leg");
Check(LevelRoutePlanner.Progress(legs, 1, new(1, 0, -1), 4, 2) == 0, "back down at the foot returns to the ladder leg");
Check(LevelRoutePlanner.Progress(legs, 1, new(40, 0, 40), 4, 2) == 1, "a fall far away is left to the route check");
var shortLadder = new[]
{
    new LevelRoutePlanner.Leg(new(0, 0, 0), new(0, 4, 0), 0, true),
    new LevelRoutePlanner.Leg(new(3, 4, 3), new(3, 4, 3), -1, false),
};
Check(LevelRoutePlanner.Progress(shortLadder, 0, new(6, 2.5f, 0), 4, 2) == 0, "a ramp beside a short ladder is not its top");

// --- Orientation: destination picker ---
var places = new[]
{
    new Destination("Save", null!, DestinationCategory.Saved, 0, 0, 0, DestinationAction.SaveHere),
    new Destination("Far shop", "shop", DestinationCategory.Shops, 100, 0, 0),
    new Destination("Near shop", "shop", DestinationCategory.Shops, 10, 0, 0),
    new Destination("Near shop", "shop", DestinationCategory.Shops, 12, 0, 1),
    new Destination("Beat Square", null!, DestinationCategory.Places, 0, 0, 50),
    new Destination("Beat Square", null!, DestinationCategory.Places, 0, 0, -80),
    new Destination("My spot", null!, DestinationCategory.Saved, 5, 0, 5),
    new Destination("  ", null!, DestinationCategory.People, 1, 0, 1),
};
var groups = DestinationPicker.Build(places, 0, 0, 8);
Check(groups.Count == 3, "empty names are dropped and only non-empty categories remain");
Check(groups[0].Category == DestinationCategory.Places && groups[2].Category == DestinationCategory.Saved,
      "categories follow the enum order");
Check(groups[1].Items.Count == 2 && groups[1].Items[0].Name == "Near shop", "same name within the merge radius is one place, nearest first");
Check(groups[0].Items.Count == 2 && groups[0].Items[0].Z == 50, "same name far apart stays two places");
Check(groups[2].Items[0].Action == DestinationAction.SaveHere, "position-less actions sort on top");
var picker = new DestinationPicker();
picker.Load(groups, DestinationCategory.Shops);
Check(picker.Category == DestinationCategory.Shops && picker.Index == 0, "opens on the preferred category, nearest item");
picker.MoveItem(-1);
Check(picker.Index == 1, "moving up from the first item wraps to the last");
picker.MoveCategory(1);
Check(picker.Category == DestinationCategory.Saved && picker.Index == 0, "next category resets to its first item");
picker.MoveCategory(1);
Check(picker.Category == DestinationCategory.Places, "categories wrap around");
picker.Load(groups, DestinationCategory.Masters);
Check(picker.Category == DestinationCategory.Places, "a missing preferred category falls back to the first");
picker.Load(new List<DestinationPicker.Group>(), DestinationCategory.Places);
picker.MoveItem(1); picker.MoveCategory(1);
Check(picker.IsEmpty, "an empty picker ignores movement");
Check(new Destination("x", "shop", DestinationCategory.Shops, 1, 0, 0).Label == "x, shop", "label joins the kind word");
Check(!new Destination("x", null!, DestinationCategory.Shops, 0, 0, 0).HasPosition, "an exact origin is no position");
Check(DestinationPicker.Build(new[] { new Destination("Lost", null!, DestinationCategory.Shops, 0, 0, 0) }, 0, 0, 8).Count == 0,
      "a place whose position failed to read is not offered");

// --- Orientation: tap versus hold ---
var gesture = new HoldTapDetector(500);
Check(gesture.Update(true, 0) == HoldTapDetector.Gesture.None, "press starts silently");
Check(gesture.Update(false, 200) == HoldTapDetector.Gesture.Tap, "short press is a tap on release");
gesture.Update(true, 1000);
Check(gesture.Update(true, 1499) == HoldTapDetector.Gesture.None, "not held long enough yet");
Check(gesture.Update(true, 1500) == HoldTapDetector.Gesture.Hold, "hold fires once at the threshold");
Check(gesture.Update(true, 3000) == HoldTapDetector.Gesture.None, "hold does not repeat");
Check(gesture.Update(false, 3100) == HoldTapDetector.Gesture.None, "release after a hold is not a tap");
gesture.Update(true, 4000);
gesture.Swallow();
Check(gesture.Update(false, 4100) == HoldTapDetector.Gesture.None, "a swallowed press does not tap");
gesture.Swallow();
Check(gesture.Update(false, 4200) == HoldTapDetector.Gesture.None && gesture.Update(true, 4300) == HoldTapDetector.Gesture.None
      && gesture.Update(false, 4400) == HoldTapDetector.Gesture.Tap, "swallowing ends at the next release");

// --- Orientation: landmark regions with hysteresis ---
var landmarks = new[]
{
    new RegionTracker.Landmark("A", 0, 0),
    new RegionTracker.Landmark("B", 100, 0),
    new RegionTracker.Landmark("A", -200, 0),
};
var region = new RegionTracker(4, 0.2f, 1000);
Check(region.Update(landmarks, 10, 0, 0) && region.Current == "A", "first reading is immediate");
Check(!region.Update(landmarks, 52, 0, 100), "just past the midpoint is not a change");
Check(!region.Update(landmarks, 60, 0, 200) && region.Current == "A", "a clear lead must also hold for a while");
Check(!region.Update(landmarks, 60, 0, 900), "still within the dwell");
Check(region.Update(landmarks, 60, 0, 1200) && region.Current == "B", "a held clear lead changes the region");
Check(!region.Update(landmarks, 45, 0, 1300) && region.Current == "B", "walking back a little does not flip back");
Check(!region.Update(landmarks, 20, 0, 1400), "a lead that appears restarts the dwell");
Check(!region.Update(landmarks, 60, 0, 2000) && region.Current == "B", "a lead that vanished resets the candidate");
region.Update(landmarks, -150, 0, 3000);
Check(region.Update(landmarks, -150, 0, 4100) && region.Current == "A", "a shared name counts its nearest copy");
region.Reset();
Check(region.Current == null && !region.Update(Array.Empty<RegionTracker.Landmark>(), 0, 0, 0), "no landmarks, no region");

// --- Orientation: place file ---
var entry = new PlaceFile.Entry(400, 2, 7, "Style Lab", -36.25f, 0.1f, -110f);
Check(PlaceFile.TryParse(PlaceFile.Format(entry), out var parsed) && parsed == entry, "a place survives a round trip");
Check(PlaceFile.TryParse(PlaceFile.Format(entry with { Name = "a\tb\nc" }), out parsed) && parsed.Name == "a b c",
      "tabs and newlines in names cannot break the file");
Check(!PlaceFile.TryParse("400\t2\t7\tName\tx\t0\t0", out _), "a bad number is refused");
Check(!PlaceFile.TryParse("400\t2\t7\t \t1\t0\t0", out _), "an empty name is refused");
Check(!PlaceFile.TryParse("400\t2\tName\t1\t0\t0", out _), "a short line is refused");

// --- Jumps: the owner's own thresholds (2026-10-03): a metre of gap, 1.2 metres of obstacle ---
Check(JumpLink.StepsUp(1.2f, 0f), "an obstacle exactly at the owner's limit is jumped");
Check(JumpLink.StepsUp(0.8f, 0f), "a kerb is stepped up onto");
Check(!JumpLink.StepsUp(1.3f, 0f), "above the limit it is a climb, not a jump");
Check(!JumpLink.StepsUp(6f, 0f), "a floor six metres up is not an obstacle");
Check(!JumpLink.StepsUp(0f, 0f), "the same floor is not an obstacle at all");
Check(!JumpLink.StepsUp(-2f, 0f), "a floor below is a drop, not an obstacle");
Check(!JumpLink.StepsUp(float.NaN, 0f) && !JumpLink.StepsUp(0.5f, float.NaN), "an unmeasured height is never a jump");
Check(JumpLink.StepsUp(1.2f, 0.1f), "the limit is a rise, not an absolute height");

Check(JumpLink.Crossable(1f), "a gap exactly a metre wide is crossed");
Check(JumpLink.Crossable(0.4f), "a narrow gap is crossed");
Check(!JumpLink.Crossable(1.05f), "a wider gap is gone round");
Check(!JumpLink.Crossable(0f), "no gap at all is not a crossing");
Check(!JumpLink.Crossable(-0.5f) && !JumpLink.Crossable(float.NaN), "an unmeasured width is never a crossing");

Check(JumpLink.Landable(0f, 0f), "the far side at the same height is landable");
Check(JumpLink.Landable(-1.2f, 0f), "a ledge a metre two down is still a hop");
Check(!JumpLink.Landable(-3f, 0f), "a three-metre drop is a drop, not a hop");

Check(JumpLink.IsStepUp(0f, 0.9f), "a rise is spoken as a step up");
Check(!JumpLink.IsStepUp(0f, 0f), "a level crossing is spoken as a gap");
Check(!JumpLink.IsStepUp(0f, -0.4f), "stepping down is not spoken as a step up");

// --- Narrow stairs: a run of risers with a cliff on one side and a wall on the other
// (2026-10-05, the owner's ask: "让导航能带我走上楼梯 … 一面悬崖一面高墙也要能认出来") ---
// The floor line of a real flight: 0.4 m samples, a 0.30 m riser every other sample, from
// the player's own floor (y 0) up to a landing. Two consecutive samples per tread is what
// the 0.4 m spacing gives a 0.8 m stride; the flat sample before the first riser is the
// foot, which the flight must include so the walk to it is the ground already underfoot.
const float step = 0.4f;
float[] Stair() => new[] { 0f, 0f, 0.3f, 0.3f, 0.6f, 0.6f, 0.9f, 0.9f, 1.2f, 1.2f };
bool Fits(float[] floors, out NarrowPath.Flight flight)
    => NarrowPath.TryRead(i => floors[i], _ => true, floors.Length, floors[0], step, 6f, out flight);

Check(Fits(Stair(), out var flight) && flight.RiserCount == 4 && flight.FirstStep == 2 && flight.Top == 9,
      "four risers of a flight are read, from the first riser to the landing");
Check(MathF.Abs(flight.TotalRiseM - 1.2f) < 0.001f, "the flight's rise is the floor it ends on minus the player's own");
Check(MathF.Abs(flight.RiserM - 0.3f) < 0.001f, "the riser is the mean of the risers walked");

// One riser is a kerb, which JumpLinkCatalog already offers as a single step; this class
// must not duplicate it as a "flight".
Check(!Fits(new[] { 0f, 0f, 0.3f, 0.3f }, out _), "one riser is a step, not a flight");

// A stair does not descend: a run that falls away ends at the last tread, and a toe that
// falls a little is still a toe.
Check(Fits(new[] { 0f, 0f, 0.3f, 0.3f, 0.6f, 0.6f, -2f, -2f }, out var cut)
      && cut.Top <= 5, "a floor that falls away after the run ends the flight at the last tread");

// Too tall to step: the run stops there rather than claiming a wall as a riser. Two risers
// stand below it, so the flight is real — the wall the run runs into is simply not its step.
Check(Fits(new[] { 0f, 0f, 0.3f, 0.3f, 0.6f, 0.6f, 2.0f, 2.0f, 2.3f, 2.3f }, out var tall)
      && tall.Top <= 5 && tall.RiserCount == 2, "a rise taller than a step is not part of the flight");

// A hole (no floor) ends the run: a gap is not a step.
Check(Fits(new[] { 0f, 0f, 0.3f, 0.3f, 0.6f, 0.6f, float.NaN, float.NaN }, out var holed)
      && holed.Top <= 5 && holed.RiserCount == 2, "a hole in the floor ends the flight");

// A single riser and then something unsteppable is no flight at all: one step is a kerb,
// and JumpLinkCatalog offers that as a crossing. Two risers are what make a stair.
Check(!Fits(new[] { 0f, 0f, 0.3f, 0.3f, 2.0f, 2.0f }, out _),
      "one riser before a wall is a kerb, not a flight to be walked");

// Beyond the reach the flight may not be claimed at all.
float[] longStair = new float[20];
for (int i = 0; i < 20; i++) longStair[i] = 0.3f * (i / 2);
Check(!NarrowPath.TryRead(i => longStair[i], _ => true, 20, 0f, step, 1.5f, out _),
      "a flight longer than the reach is refused, not truncated");
Check(NarrowPath.TryRead(i => longStair[i], _ => true, 20, 0f, step, 20f, out var big)
      && big.RiserCount > 4, "the same stair is read once the reach allows it");

// "Not measured" is never a riser and never a hole.
Check(Fits(new[] { 0f, 0f, 0.3f, 0.3f, 0.6f, 0.6f }, out var tread) && tread.RiserCount == 2,
      "a measured tread is read");
Check(!NarrowPath.TryRead(i => 0f, i => i < 2, 8, 0f, step, 6f, out _),
      "no riser among the measured samples, no flight");
// A stair must begin on the player's own floor: a line that starts a metre and a half up is
// another storey's stair, and a flight read from it would be a flight the player is not
// standing at the foot of.
var upper = new[] { 1.5f, 1.5f, 1.8f, 1.8f, 2.1f, 2.1f };
Check(!NarrowPath.TryRead(i => upper[i], _ => true, upper.Length, 0f, step, 6f, out _),
      "a flight is steps up from where the player stands, never from a sample of its own");

// --- which side is which (the cliff-and-wall stair) ---
var cliffAndWall = NarrowPath.ReadSides(NarrowPath.Side.Drop, NarrowPath.Side.Wall);
Check(cliffAndWall.CliffAndWall, "air one side and a wall the other is the cliff-and-wall stair");
Check(cliffAndWall.CliffLeft && cliffAndWall.WallRight, "the cliff and the wall are named by hand");
Check(!cliffAndWall.Open, "a sided stair is not open both ways");

var openStair = NarrowPath.ReadSides(NarrowPath.Side.Floor, NarrowPath.Side.Floor);
Check(openStair.Open && !openStair.CliffAndWall, "floors both sides is an open stair");
var walled = NarrowPath.ReadSides(NarrowPath.Side.Wall, NarrowPath.Side.Wall);
Check(walled.Wall && !walled.Cliff, "walls both sides: a walled stair, no cliff");
var dropped = NarrowPath.ReadSides(NarrowPath.Side.Drop, NarrowPath.Side.Drop);
Check(dropped.Cliff && !dropped.Wall, "air both sides: a cliff-sided stair, no wall");

// A probe that could not run is evidence of NOTHING: no cliff, no wall, never "open".
var unmeasured = NarrowPath.ReadSides(NarrowPath.Side.Unknown, NarrowPath.Side.Drop);
Check(!unmeasured.Cliff && !unmeasured.Wall && !unmeasured.Open,
      "an unmeasured hand claims neither a cliff nor open ground");
var halfUnknown = NarrowPath.ReadSides(NarrowPath.Side.Wall, NarrowPath.Side.Unknown);
Check(!halfUnknown.Cliff && !halfUnknown.Wall, "one unknown hand silences both readings");

// --- Walking a stair a step at a time (F4's right-hand wall following, 2026-10-05) ---
// The owner's report: "有那种窄梯子他是没有带我走上去的，注意，我说的是不用交互的梯子". F4 knew
// about walls and cliffs and had no word for the ground RISING, so it walked past every
// flight of stairs it met. StairStep is that word, and it answers with the metres to walk.
const float stairStep = StairStep.SampleStepM;   // 0.4 m, the spacing the catalogues read a flight at
float[] Filled(int n, Func<int, float> f)
{
    var a = new float[n];
    for (int i = 0; i < n; i++) a[i] = f(i);
    return a;
}
// A flight as the samples see it: a stride of level ground (two samples a tread), a riser
// every other sample, and then the ground FALLING away past the top — which is what ends the
// run, because NarrowPath follows a level landing to the end of the line. The flight must also
// FIT inside StairStep.ReachM: a flight longer than the reach is refused outright rather than
// truncated, and that refusal is asserted further down.
float[] WalkableStair() => Filled(StairStep.Samples,
    i => i < 2 ? 0f : i < 10 ? 0.3f * (i / 2) : -3f);

bool ReadsStair(float[] floors, out float walkM, out NarrowPath.Flight flight)
    => StairStep.TryRead(i => floors[i], _ => true, floors.Length, 0f, stairStep,
                         StairStep.ReachM, out walkM, out flight);

Check(ReadsStair(WalkableStair(), out var stairWalk, out var stairFlight) && stairFlight.RiserCount > 2,
      "a flight of stairs under the line ahead is read as a stair the player walks up");
// The first riser sits at sample 2 (0.5 + 2 x 0.4 = 1.3 m). The step must not be longer than
// that plus one tread's walk, and never longer than MaxStepM.
Check(stairWalk > StairStep.MinStepM && stairWalk <= StairStep.MaxStepM,
      "the stair step is a tread long and no longer, so the next sentence comes on the tread");
Check(StairStep.SampleAt(stairFlight.FirstStep, stairStep) < 1.5f,
      "the first riser is named where it is, about a stride out");

// Level ground is not a stair: F4 already covers it as an ordinary step, and a stair step here
// would be a second captain describing the same street.
Check(!ReadsStair(Filled(StairStep.Samples, _ => 0f), out _, out _),
      "flat ground is not a stair");

// A hole in the middle of the run is not a way up. This is the half that makes StairStep worth
// having apart from NarrowPath: a flight read from a line with no floor in it is a stair nobody
// can walk, and it must not be offered.
var stairHole = WalkableStair();
stairHole[4] = float.NaN;
Check(!StairStep.TryRead(i => stairHole[i], _ => true, stairHole.Length, 0f, stairStep, StairStep.ReachM, out _, out _),
      "a hole between the player and the stair is not a way up");

// An unmeasured sample is not a hole either, and it stops the walk where it is: "not measured"
// must never reach the player as "walk over it".
var unmeasuredLine = WalkableStair();
Check(!StairStep.TryRead(i => unmeasuredLine[i], i => i != 3, unmeasuredLine.Length, 0f, stairStep,
                         StairStep.ReachM, out _, out _),
      "an unmeasured sample stops the stair walk rather than being stepped over");

// A rise too tall to step is a wall, not a riser: the run ends before it, so the flight is
// read up to the wall and the STEP is still a tread — it must never claim the ground past it.
var tooTall = Filled(StairStep.Samples, i => i < 2 ? 0f : i < 10 ? 0.3f * (i / 2) : 4f);
Check(StairStep.TryRead(i => tooTall[i], _ => true, tooTall.Length, 0f, stairStep, StairStep.ReachM,
                        out float shortWalk, out var wallFlight)
      && wallFlight.RiserCount >= 2 && shortWalk <= StairStep.MaxStepM,
      "a wall past the top of the run is not walked through: the step stays a tread");
Check(StairStep.SampleAt(wallFlight.Top, stairStep) < StairStep.SampleAt(wallFlight.Top + 1, stairStep),
      "the wall's own sample is never the last tread of the flight");

// The foot of the stair is the ground under the player. A line that only becomes steps later,
// with no measured ground before them, is another storey's stair and not theirs.
Check(!StairStep.TryRead(i => WalkableStair()[i], i => i >= 2, StairStep.Samples, 0f, stairStep,
                         StairStep.ReachM, out _, out _),
      "a stair whose foot was not measured is not the player's stair");

// Sampled distances are the ones the casts are made at, so the sentence and the rays agree.
Check(MathF.Abs(StairStep.SampleAt(0, stairStep) - StairStep.FirstSampleM) < 0.001f,
      "the first sample sits a stride from the feet");
Check(MathF.Abs(StairStep.SampleAt(StairStep.Samples - 1, stairStep)
                - (StairStep.FirstSampleM + (StairStep.Samples - 1) * StairStep.SampleStepM)) < 0.001f,
      "the line reaches past a flight without reaching past the stair");
Check(StairStep.MaxStepM < StairStep.StepM, "a stair step is shorter than an ordinary wall-following step");
Check(StairStep.MaxStepM > StairStep.MoveGateM,
      "a stair step is long enough that walking it clears the move gate - the feet can always answer it");
Check(StairStep.MinStepM > 0f, "the shortest stair step is still worth walking");
Check(StairStep.ReachM < StairStep.SampleAt(StairStep.Samples, StairStep.SampleStepM) + 1f,
      "the line reaches past the reach it may claim");

// --- A DOWN-flight is a flight too (2026-10-05) ---
// The owner's correction: "导航不能只认得出上行的梯子或斜坡，也要能认出下行的斜坡或梯子，注意，我说
// 的是那种很窄的不用交互的，让导航在规划路线的时候能规划进去". The reader found only a riser ABOVE
// the player, so a flight leaving a landing downward was invisible to the plan. The same samples
// read downward are a descent, and the planner may route down it as it routes up a climb.
float[] DownStair() => Filled(StairStep.Samples,
    i => i < 2 ? 0f : i < 10 ? -0.3f * (i / 2) : -3f);

bool Reads(float[] floors, out NarrowPath.Flight f) =>
    NarrowPath.TryRead(i => floors[i], _ => true, floors.Length, 0f, stairStep, StairStep.ReachM, out f);

Check(Reads(DownStair(), out var downFlight) && downFlight.GoesDown,
      "ground FALLING in steps is read as a flight, not as a cliff");
Check(downFlight.Way == NarrowPath.Way.Down && downFlight.SignedM < 0f,
      "a down-flight says which way it goes and its signed change is negative");
Check(downFlight.RiserM > 0f, "a tread's height is positive whichever way the flight runs");
Check(Reads(WalkableStair(), out var upFlight) && !upFlight.GoesDown && upFlight.SignedM > 0f,
      "an up-flight still reads as up, and nothing about it changed");
// A descent is a flight the player can WALK; the step-walk gate must not throw it away as a drop.
Check(StairStep.TryRead(i => DownStair()[i], _ => true, StairStep.Samples, 0f, stairStep,
                        StairStep.ReachM, out float downWalk, out var downStepFlight, sidesSafe: true)
      && downStepFlight.GoesDown && downWalk > StairStep.MinStepM && downWalk <= StairStep.MaxStepM,
      "a down-flight is walked a tread at a time, exactly as an up-flight is");
// ...and a caller with no side measurement may not be walked down one.
Check(!StairStep.TryRead(i => DownStair()[i], _ => true, StairStep.Samples, 0f, stairStep,
                         StairStep.ReachM, out _, out _, sidesSafe: false),
      "a down-flight with no measured sides is refused rather than walked");
Check(StairStep.TryRead(i => WalkableStair()[i], _ => true, StairStep.Samples, 0f, stairStep,
                        StairStep.ReachM, out _, out _, sidesSafe: false),
      "an up-flight needs no side reading: going up, the ground is re-measured at the top");
// A run that turns back on itself ends where it turns: a landing with stairs off it is one
// connection each way, and a bent flight is not a flight.
var bentUp = Filled(StairStep.Samples, i => i < 2 ? 0f : i < 8 ? 0.3f * (i / 2) : -0.3f * (i / 2));
Check(NarrowPath.TryRead(i => bentUp[i], _ => true, StairStep.Samples, 0f, stairStep,
                         StairStep.ReachM, out var bentRead) && !bentRead.GoesDown && bentRead.Top <= 7,
      "a flight that turns downhill is read up to the turn and no further");
// The safety verdict, tested directly: a measured floor one side and a wall the other is the
// stair this is for; air anywhere is a refusal.
var wideStair = NarrowPath.ReadSides(NarrowPath.Side.Floor, NarrowPath.Side.Wall);
Check(NarrowPath.SafeToDescend(wideStair), "a stair with a wall one side and a floor the other may be walked down");
Check(!NarrowPath.SafeToDescend(NarrowPath.ReadSides(NarrowPath.Side.Drop, NarrowPath.Side.Wall)),
      "air on one side: a descent is refused, whatever the other hand holds");
Check(!NarrowPath.SafeToDescend(NarrowPath.ReadSides(NarrowPath.Side.Drop, NarrowPath.Side.Drop)),
      "air both sides: a descent is refused");
Check(!NarrowPath.SafeToDescend(NarrowPath.ReadSides(NarrowPath.Side.Unknown, NarrowPath.Side.Floor)),
      "an unmeasured hand is not evidence of ground: a descent is refused");

// --- The mission search's OWN stair reader must emit a descent (2026-10-05) ---
// JumpLinkCatalog.AddFlight turns a read flight into one link per riser. Its riser test used to be
// signed for a CLIMB only (`y - prevY < MinRiseM`), which is true of no tread of a descent - so a
// down-flight found by the player-facing line was measured, logged and then SILENTLY DROPPED, with
// no link at all. That is exactly the owner's "找任务目标的时候找不到下行楼梯". The rule is now taken
// from the flight's own way, and this proves a descent gets its links.
int Treads(bool down, float[] floors)
{
    if (!NarrowPath.TryRead(i => floors[i], _ => true, floors.Length, 0f, stairStep,
                            StairStep.ReachM, out var f)) return 0;

    int links = 0;
    float prevY = 0f;
    for (int i = f.FirstStep; i <= f.Top; i++)
    {
        float change = floors[i] - prevY;
        if (down ? -change < JumpLink.MinRiseM : change < JumpLink.MinRiseM) continue;
        links++;
        prevY = floors[i];
    }
    return links;
}
int upTreads = Treads(false, WalkableStair());
int downTreads = Treads(true, DownStair());
Check(upTreads >= 2, "an up-flight emits one link per riser: the climb the search already had");
Check(downTreads >= 2, "a down-flight emits one link per riser too: the descent the search was blind to");
Check(downTreads == upTreads, "a descent of the same shape yields the same number of links as the climb");
// ...and the OLD rule, kept here as the counter-example: signed for a climb only, it would find
// NOTHING on the descent. This is the defect, proved rather than asserted.
int OldRule(bool down, float[] floors)
{
    if (!NarrowPath.TryRead(i => floors[i], _ => true, floors.Length, 0f, stairStep,
                            StairStep.ReachM, out var f)) return 0;
    int links = 0; float prevY = 0f;
    for (int i = f.FirstStep; i <= f.Top; i++)
    {
        if (floors[i] - prevY < JumpLink.MinRiseM) continue;
        links++; prevY = floors[i];
    }
    return links;
}
Check(OldRule(false, WalkableStair()) >= 2 && OldRule(true, DownStair()) == 0,
      "the climb-signed riser test found every tread upward and NONE downward - the bug this fixed");

// The descent's link is shaped for the planner: the lower end is Low so the two directions of travel
// stay "up" and "down", and it is a STAIR rather than a jump, so the route can say "down".
var descentLink = new LevelRoutePlanner.Link(new(5, -3, 0), new(5, 0, 0), false, true);
Check(descentLink.Stair && !descentLink.Jump, "a descent's link is a stair link, not a jump");
Check(descentLink.Low.Y < descentLink.High.Y, "a descent's link still names its ends by height");
Check(Plan(ground, new(5, -6, 5), new[] { stairDown2 }).Plan is { Count: 2 } p2
      && p2[0].Stair && !p2[0].Up,
      "the mission search plans down a descent link and marks the leg as a descent");

// --- The stairs on the ENGINE'S OWN ROUTE, however far away (2026-10-05) ---
// The owner's correction: "我要的是他在规划路线的时候，就把下行的梯子规划出来，跟那个梯子距离玩家多远没有
// 关系。假设玩家离梯子很远，但是要经过这个梯子怎么办呢？" StairCatalog is a circle of rays around the
// PLAYER and can never reach a stair at the far end of the walk; the engine's route passes through it,
// so the route's own bends are where the far stairs are looked for. This part is pure geometry.
((float X, float Y, float Z)[] corners, int bends) Route()
{
    // A route that walks flat, drops a storey, walks flat again, then climbs a storey.
    var list = new List<(float, float, float)>();
    for (int i = 0; i <= 10; i++) list.Add((i, 0f, 0f));            // flat
    list.Add((12f, -4f, 2f));                                        // DOWN a storey
    for (int i = 0; i <= 10; i++) list.Add((12f, -4f, 4f + i));      // flat again
    list.Add((12f, 0f, 16f));                                        // UP a storey
    return (list.ToArray(), 0);
}
var (routeCorners, _) = Route();
var routeBends = RouteBend.Find(routeCorners, JumpLink.StepM, max: 8);
Check(routeBends.Count >= 2, "a route that changes storey is read as having bends to measure");
Check(routeBends.Exists(b => b.RiseM < 0f), "a descent in the route is a bend with a negative rise");
Check(routeBends.TrueForAll(b => MathF.Abs(MathF.Sqrt(b.DirX * b.DirX + b.DirZ * b.DirZ) - 1f) < 0.01f),
      "every bend carries the route's own direction as a unit vector");
// The bend sits ON the route, which is what lets the floor samples fall on ground the engine chose.
Check(routeBends.TrueForAll(b => routeCorners.Any(c =>
          MathF.Abs(c.X - b.At.X) < 2f && MathF.Abs(c.Z - b.At.Z) < 2f)),
      "every bend is on the route's own line, not beside it");
// Flat ground has no bends: a walk that never changes storey offers no stair.
var flatOnly = new (float, float, float)[12];
for (int i = 0; i < flatOnly.Length; i++) flatOnly[i] = (i, 0f, 0f);
Check(RouteBend.Find(flatOnly, JumpLink.StepM, max: 8).Count == 0,
      "a route with no change of level offers no stair at all");
// A kerb is not a storey: level changes below the step limit are not bends.
var kerby = new (float, float, float)[] { (0, 0, 0), (5, 0.3f, 0), (10, 0.3f, 0) };
Check(RouteBend.Find(kerby, JumpLink.StepM, max: 8).Count == 0,
      "a route step within the walk limit is not a change of storey");
// A pure vertical corner has no ground direction and so no line to sample along: it is skipped
// rather than measured from a guess.
var vertical = new (float, float, float)[] { (0, 0, 0), (0, 5, 0), (0, 10, 0) };
Check(RouteBend.Find(vertical, JumpLink.StepM, max: 8).Count == 0,
      "a bend with no ground direction is skipped: there is no line to lay samples along");
// The cap keeps the FAR end of the route - the end the sweep around the player could not reach.
var manyBends = new (float, float, float)[40];
for (int i = 0; i < manyBends.Length; i++) manyBends[i] = (i * 2, (i % 2) * 4f, 0);
var capped = RouteBend.Find(manyBends, JumpLink.StepM, max: 3);
Check(capped.Count == 3, "the bend cap bounds what one route may contribute");
Check(capped[^1].At.X > capped[0].At.X,
      "the cap keeps the DEEPEST bends, so the far stair is not dropped for the player's own block");
// An empty or one-point route is no route, not a crash and not a guess.
Check(RouteBend.Find(Array.Empty<(float, float, float)>(), JumpLink.StepM, 8).Count == 0
      && RouteBend.Find(new (float, float, float)[] { (1, 2, 3) }, JumpLink.StepM, 8).Count == 0,
      "no route corners means no bends, never an exception");

// ---- The fallback: a straight line of ground, when the engine refuses to give a route ----------
// The owner's report, 2026-10-05: the engine said "no route" because it does not model the narrow
// stair as a way, so the recall cache is empty exactly when the stair is needed. Measuring the
// ground along the line to the objective does not need the engine's permission.
var lineFrom = new LevelRoutePlanner.Spot(0f, 10f, 0f);
var lineFloors = new float[] { 10f, 10f, 6f, 6f, 6f, 6f };
var lineBends = RouteBend.AlongLine(lineFrom, i => lineFloors[i], _ => true, lineFloors.Length,
                                    2f, 1.5f, JumpLink.StepM, 4, 1f, 0f);
Check(lineBends.Count == 1, "a straight line that drops a storey is read as one bend to measure");
Check(lineBends.Count == 1 && lineBends[0].RiseM < 0f,
      "the storey change on the line is signed: a descent is a negative rise");
Check(lineBends.Count == 1 && MathF.Abs(lineBends[0].At.Y - 8f) < 0.01f,
      "the line bend sits between the two floors it fell between");
Check(lineBends.Count == 1 && MathF.Abs(lineBends[0].DirX - 1f) < 0.01f
      && MathF.Abs(lineBends[0].DirZ) < 0.01f,
      "a line bend carries the line's own direction");
Check(RouteBend.AlongLine(lineFrom, _ => 10f, _ => true, 6, 2f, 1.5f, JumpLink.StepM, 4, 1f, 0f).Count == 0,
      "a line that never changes storey offers no stair");
var kerbLine = new float[] { 10f, 10f, 10.3f, 10.3f, 10.3f };
Check(RouteBend.AlongLine(lineFrom, i => kerbLine[i], _ => true, kerbLine.Length, 2f, 1.5f,
                          JumpLink.StepM, 4, 1f, 0f).Count == 0,
      "a kerb on the line is within the walk limit and is not a change of storey");
var holey = new float[] { 10f, 10f, 6f, 6f, 6f };
var holeyOk = new bool[] { true, true, false, true, true };
Check(RouteBend.AlongLine(lineFrom, i => holey[i], i => holeyOk[i], holey.Length, 2f, 1.5f,
                          JumpLink.StepM, 4, 1f, 0f).Count == 0,
      "an unmeasured sample breaks the run, so ground across a hole is never called a flight");
var nanny = new float[] { 10f, 10f, float.NaN, 6f, 6f };
Check(RouteBend.AlongLine(lineFrom, i => nanny[i], _ => true, nanny.Length, 2f, 1.5f,
                          JumpLink.StepM, 4, 1f, 0f).Count == 0,
      "a hole in the line breaks the run too: nothing is measured across it");
var stairLine = new float[] { 10f, 10f, 4f, 4f, -2f, -2f, -8f, -8f };
var nearBends = RouteBend.AlongLine(lineFrom, i => stairLine[i], _ => true, stairLine.Length,
                                    2f, 1.5f, JumpLink.StepM, max: 2, dirX: 1f, dirZ: 0f);
Check(nearBends.Count == 2, "the line's bend cap bounds what one line may contribute");
Check(nearBends.Count == 2 && nearBends[0].At.X < nearBends[1].At.X,
      "the cap keeps the NEAREST bends of a line: the stair the player reaches first is the one offered");
Check(RouteBend.AlongLine(lineFrom, _ => 10f, _ => true, 1, 2f, 1.5f, JumpLink.StepM, 4, 1f, 0f).Count == 0
      && RouteBend.AlongLine(lineFrom, _ => 10f, _ => true, 0, 2f, 1.5f, JumpLink.StepM, 4, 1f, 0f).Count == 0,
      "a line with fewer than two samples yields no bends, never an exception");
// The line's samples are the CALLER's own cast positions: from + dir * (firstM + i * stepM).
Check(MathF.Abs(RouteBend.AlongLine(new LevelRoutePlanner.Spot(5f, 10f, 7f), i => (i == 0 ? 10f : 6f), _ => true, 2,
                                    2f, 1.5f, JumpLink.StepM, 4, 0f, 1f)[0].At.X - 5f) < 0.01f,
      "a line bend is placed on the caller's own line, using the caller's spacing");

// The stride must REACH the objective. Found live, 2026-10-05: a 5.70 m descent whose floor was
// 6.07 m away was sampled at 2.0, 3.5 and 5.0 m with a fixed 1.5 m stride - the line stopped a
// metre short, the landing was never measured, and the whole roof read as one flat slab.
// Reproduce the arithmetic the finder now uses: strides rounded UP, then fitted to land on it.
float LineStride(float len, float firstM, float stepM, int cap, out int samples)
{
    int strides = (int)MathF.Ceiling((len - firstM) / stepM);
    if (strides < 1) strides = 1;
    bool capped = strides + 1 > cap;
    if (capped) strides = cap - 1;
    samples = strides + 1;
    return capped ? (len - firstM) / strides : stepM;
}
const float liveLen = 6.07f;
float liveStride = LineStride(liveLen, 2f, 1.5f, 160, out int liveSamples);
Check(liveSamples == 4, "a 6.07 m objective needs four samples, not the three a fixed stride gave");
Check(liveStride * (liveSamples - 1) + 2f >= liveLen - 0.01f,
      "the last sample REACHES the objective, so its own floor is read - the live failure was a line that stopped short");
Check(liveStride <= 1.5f, "fitting never makes the stride coarser than the samples were chosen for");
// A distance that IS a whole multiple keeps the nominal stride and does not add a sample.
Check(MathF.Abs(LineStride(2f + 3f * 1.5f, 2f, 1.5f, 160, out int exact) - 1.5f) < 0.01f && exact == 4,
      "a distance that is a whole number of strides keeps the nominal stride");
// Very far objectives are capped, and the cap still lands the last sample on the objective.
float farStride = LineStride(500f, 2f, 1.5f, 160, out int farSamples);
Check(farSamples == 160, "a five-hundred-metre objective is capped to the sample limit");
Check(MathF.Abs(farStride * 159 + 2f - 500f) < 0.01f,
      "even the capped line lands its last sample on the objective, only coarser");

// The line is read in BOTH directions: the owner asked for up-going stairs as well, 2026-10-05.
// The reader is direction-blind (|change| opens a bend, the sign carries which way it goes), so a
// climb on the line must give a bend with a POSITIVE rise and the same shape as a descent.
var upLine = new float[] { 0f, 0f, 5f, 5f, 5f, 5f };
var upBends = RouteBend.AlongLine(new LevelRoutePlanner.Spot(0f, 0f, 0f), i => upLine[i], _ => true,
                                  upLine.Length, 2f, 1.5f, JumpLink.StepM, 4, 1f, 0f);
Check(upBends.Count == 1 && upBends[0].RiseM > 0f,
      "a line that CLIMBS a storey is a bend with a positive rise: up-going stairs are found too");
// A climb and a descent on the same line are two separate bends.
var bothWays = new float[] { 0f, 0f, 5f, 5f, 0f, 0f };
var bothBends = RouteBend.AlongLine(new LevelRoutePlanner.Spot(0f, 0f, 0f), i => bothWays[i], _ => true,
                                    bothWays.Length, 2f, 1.5f, JumpLink.StepM, 4, 1f, 0f);
Check(bothBends.Count == 2 && bothBends[0].RiseM > 0f && bothBends[1].RiseM < 0f,
      "a line that climbs then descends gives one bend each way, in the order they occur");

// A stair the route runs through becomes an ordinary stair link the planner may route through.
var routeStair = new LevelRoutePlanner.Link(new(100, -1.8f, 100), new(100, 0f, 94), false, true);
Check(routeStair.Stair && !routeStair.Jump && routeStair.Low.Y < routeStair.High.Y,
      "a far stair found on the route is an ordinary stair link, named by its two heights");
Check(Plan(ground, new(100, -3, 100), new[] { routeStair }).Plan is { Count: 2 } rp
      && rp[0].Stair && !rp[0].Up,
      "a stair a hundred metres away is planned into the route when it must be gone down");


Check(!DropEdge.IsDrop(6.3f, 6.3f), "the same floor is not a drop");
Check(!DropEdge.IsDrop(6.3f, 5.8f), "half a metre down is a kerb, which a walk already covers");
Check(!DropEdge.IsDrop(6.3f, 6.9f), "a floor above is not a drop at all");
Check(DropEdge.IsDrop(6.3f, 4.8f), "a metre and a half down is a drop");
Check(DropEdge.IsDrop(6.3f, 0.7f), "five metres down is a drop");
Check(!DropEdge.IsDrop(6.3f, -8f), "thirteen metres down is a leap, not a way down");

Check(DropEdge.SameFloor(6.3f, 6.3f), "the floor underfoot is this platform");
Check(DropEdge.SameFloor(6.34f, 6.3f), "a few centimetres of slope is still this platform");
Check(!DropEdge.SameFloor(7.4f, 6.3f), "a floor a metre up is another platform");
Check(!DropEdge.SameFloor(0.7f, 6.3f), "the yard below is not the roof the player stands on");

Check(DropEdge.LandsOn(0.7f, 0.7f), "landing on the objective's own floor");
Check(DropEdge.LandsOn(1.9f, 0.7f), "a metre of slope on that floor still counts as it");
Check(!DropEdge.LandsOn(4.2f, 0.7f), "landing three metres above it is another storey");
Check(!DropEdge.LandsOn(-6f, 0.7f), "landing in a pit below it is another storey");

Check(DropEdge.Worth(6.3f, 6.3f, 0.7f, 0.7f), "a five metre drop onto the objective's floor is offered");
Check(!DropEdge.Worth(6.3f, 6.3f, 4.2f, 0.7f), "a drop onto a floor three metres above it is not");
Check(!DropEdge.Worth(6.3f, 6.3f, 5.8f, 0.7f), "a step down onto nothing is not a drop");
Check(!DropEdge.Worth(0.7f, 6.3f, 0.7f, 0.7f), "a standing point the player is not on is not offered");

Check(DropEdge.Score(0f, 0f, 3f, 4f) == 5f, "the landing is scored by the flat metres it leaves");
Check(DropEdge.Score(3f, 4f, 0f, 0f) == 5f, "and scored the same from either side");
Check(DropEdge.SampleM(0) == DropEdge.FirstSampleM, "the first sample is a stride out");
Check(DropEdge.SampleM(DropEdge.Samples - 1) > DropEdge.SampleM(0), "samples walk outward");

// --- The way ROUND: one hop of a route measured out of the world (2026-10-03, SiRN hoarding) ---
Check(RoundStep.LengthM(false, 0f, RoundStep.ReachM, RoundStep.HopM, RoundStep.ClearM,
                        RoundStep.MinHopM) == RoundStep.HopM,
      "nothing in the way makes the hop its whole length");
Check(RoundStep.LengthM(true, 1.5f, RoundStep.ReachM, RoundStep.HopM, RoundStep.ClearM,
                        RoundStep.MinHopM) == 1.5f - RoundStep.ClearM,
      "something at 1.5 m stops the hop short of it by one body-width");
Check(RoundStep.LengthM(true, 10f, RoundStep.ReachM, RoundStep.HopM, RoundStep.ClearM,
                        RoundStep.MinHopM) == RoundStep.HopM,
      "something beyond the hop is not this hop's problem");
Check(RoundStep.LengthM(true, 1.0f, RoundStep.ReachM, RoundStep.HopM, RoundStep.ClearM,
                        RoundStep.MinHopM) == 0f,
      "a metre of space is not a hop worth walking");
Check(RoundStep.LengthM(true, 0.4f, RoundStep.ReachM, RoundStep.HopM, RoundStep.ClearM,
                        RoundStep.MinHopM) == 0f,
      "standing against the wall is not a route");
Check(RoundStep.LengthM(false, 0f, 0f, RoundStep.HopM, RoundStep.ClearM, RoundStep.MinHopM) == 0f,
      "a ray that was never sent offers no hop at all");
Check(RoundStep.LengthM(true, float.NaN, RoundStep.ReachM, RoundStep.HopM, RoundStep.ClearM,
                        RoundStep.MinHopM) == 0f,
      "an unreadable contact is never read as open ground");
Check(RoundStep.ReachM > RoundStep.HopM, "the reach always asks past the end of the hop");

Check(RoundStep.Reaches(false, 0f, 3f, 0.4f), "clear ground all the way is the objective reached");
Check(RoundStep.Reaches(true, 2.6f, 3f, 0.4f), "clear to within a step of it is reached");
Check(!RoundStep.Reaches(true, 1.2f, 3f, 0.4f), "a wall a third of the way there still stands between");
Check(RoundStep.Reaches(true, 0.5f, 0.3f, 0.4f), "already standing on it needs no cast");

// The squeezed step. A walker pressed into a corner has room to move, just less than a comfortable
// stride asks for; refusing outright would turn "no comfortable route" into "no route", a lie.
Check(RoundStep.LengthM(true, 1.10f, RoundStep.ReachM, RoundStep.HopM,
                        RoundStep.ClearM, RoundStep.MinHopM) == 0f,
    "a metre of clearance is refused as a comfortable step");
Check(RoundStep.LengthM(true, 1.10f, RoundStep.ReachM, RoundStep.HopM,
                        RoundStep.SqueezeClearM, RoundStep.SqueezeMinM) > 0f,
    "the same metre is still a step once squeezed");
Check(RoundStep.LengthM(true, 0.50f, RoundStep.ReachM, RoundStep.HopM,
                        RoundStep.SqueezeClearM, RoundStep.SqueezeMinM) == 0f,
    "half a metre is not room enough even squeezed");
Check(RoundStep.SqueezeClearM < RoundStep.ClearM && RoundStep.SqueezeMinM < RoundStep.MinHopM,
    "the squeezed pair is always the tighter of the two");

// ---- When the walk is REALLY over, and when the guide must not say it is -----------------------
// The 2026-10-05 run: a mission NPC 6.07 m in front of the player on the same floor. The ladder
// search found nothing (correctly) and the beacon announced "no ladder found" — then five seconds
// later the measured approach point arrived and the guide said the true thing. The player heard
// the alarm first. A stand that can be reached IS a route, so it outranks "no route".
Check(WalkEnd.WalkIsOver(true, false, false, false), "nothing to walk to at all: the walk is over");
Check(!WalkEnd.WalkIsOver(true, true, false, false),
      "an APPROACH point known means the walk is NOT over, however loudly the ladder search failed");
Check(!WalkEnd.WalkIsOver(true, false, true, false),
      "standing at the foot of the objective is not 'no route'");
Check(!WalkEnd.WalkIsOver(true, false, false, true),
      "a known nearest reachable stand is not 'no route'");
Check(!WalkEnd.WalkIsOver(false, false, false, false), "a plain route is not the end of the walk");
// The key follows the same order, so the sentence and the decision cannot drift apart.
Check(WalkEnd.Key(true, false, false, false) == "no-route",
      "the alarm is only spoken when there is genuinely nothing to walk to");
Check(WalkEnd.Key(true, true, false, false) == "approach",
      "an approach point yields the approach guidance, not the alarm - the live 2026-10-05 fault");
Check(WalkEnd.Key(true, false, false, true) == "closest", "the nearest stand outranks the alarm too");
Check(WalkEnd.Key(false, true, false, false) == "approach", "an approach within a normal route stays approach");
Check(WalkEnd.Key(false, false, false, false) == "route", "the ordinary case is the ordinary word");
// The objective's raw Y is not a storey: the live NPC read 5.70 m below the player and stood on
// the same walkable floor. Anything within one step must be treated as "same level", so the ladder
// search is not started for a flat walk.
Check(MathF.Abs(-5.70f) >= JumpLink.StepM,
      "the live mission NPC's raw height difference (5.70 m) is far past the step limit on paper");
Check(MathF.Abs(73.7f - 73.7f) < JumpLink.StepM,
      "but the STAND the navmesh found is at the player's own height, which is within one step");
Check(MathF.Abs(0f) < JumpLink.StepM && MathF.Abs(JumpLink.StepM - 0.01f) < JumpLink.StepM,
      "the same-level gate admits everything up to a single step, and nothing beyond it");
// ---- Following a stair the way feet do: one tread at a time, turning when it turns -----------
// The owner's correction, 2026-10-05: "我要的是导航一定能认出这种窄梯子，无论他远或者近，另外，梯子
// 也是可能转弯的". Every reader before this laid ONE STRAIGHT LINE of floor samples across the
// stair, and a stair that turns leaves the line at the corner — into the wall, or into the open
// side — so the flight reads as nothing, or as a stub that ends mid-stair: on the disconnected
// treads, where the planner's navmesh question fails and the route stops at the mouth. That is
// the live case. The follow walks the flight instead, and these lay out synthetic stairs —
// straight, turning, doubling back — to prove it without a game running.

// (1) A straight stair within the reader's own budget. The follow and the line judge a tread by
// the same numbers, so here they MUST agree — the follow is not a second, softer rule.
var small = new TreadWorld();
small.Flight(0.4f, 0f, 1f, 0f, 8, 9.7f, -0.3f);           // eight treads of 0.30 m
small.Box(4.6f, 0f, 7.3f, 1.4f, 0.6f);                    // the landing they arrive at
var smallFollow = StairFollow.Follow(small.Floor, 0f, 10f, 0f, 1f, 0f, true, 0.4f);
Check(smallFollow.Ok && smallFollow.Down && smallFollow.Turns == 0, "a straight stair down is a flight");
Check(smallFollow.Risers == 9, "eight treads and the step onto the landing: nine risers");
Check(MathF.Abs(smallFollow.RiseM + 2.7f) < 0.001f && MathF.Abs(smallFollow.EndY - 7.3f) < 0.001f,
      "the follow reads the whole 2.70 m and ends ON the landing");
Check(NarrowPath.TryRead(i => small.At(0.4f * (i + 1), 10f, 0f),
                         i => float.IsFinite(small.At(0.4f * (i + 1), 10f, 0f)),
                         20, 10f, 0.4f, 8.4f, out var smallRead)
      && smallRead.RiserCount == smallFollow.Risers
      && MathF.Abs(smallRead.SignedM - smallFollow.RiseM) < 0.001f,
      "on a straight stair the follow and the line agree - the same rule, walked instead of sampled");

// (1b) ...and a straight stair longer than the reader's budget, which is the ordinary city stair:
// sixteen treads and the landing. The line's own cap stops it one riser short of the landing —
// mid-flight, on the disconnected treads, where the planner's navmesh question fails and the
// route stops at the mouth. The follow's budget does not, because a follow still on its treads is
// still on its stair. This is the whole reason the follow exists.
var straight = new TreadWorld();
straight.Flight(0.4f, 0f, 1f, 0f, 16, 9.7f, -0.3f);
straight.Box(7.6f, 0f, 4.9f, 1.4f, 0.6f);
var straightFollow = StairFollow.Follow(straight.Floor, 0f, 10f, 0f, 1f, 0f, true, 0.4f);
Check(straightFollow.Risers == 17, "sixteen treads and the step onto the landing: seventeen risers");
Check(MathF.Abs(straightFollow.RiseM + 5.1f) < 0.001f, "the follow reads the whole 5.10 m descent");
Check(MathF.Abs(straightFollow.EndY - 4.9f) < 0.001f,
      "the follow ends ON the landing, not a riser short of it - the 2026-10-05 fault");
Check(NarrowPath.TryRead(i => straight.At(0.4f * (i + 1), 10f, 0f),
                         i => float.IsFinite(straight.At(0.4f * (i + 1), 10f, 0f)),
                         20, 10f, 0.4f, 8.4f, out var straightRead)
      && straightRead.RiserCount == NarrowPath.MaxSteps
      && straightRead.RiserCount < straightFollow.Risers
      && MathF.Abs(straightRead.SignedM) < MathF.Abs(straightFollow.RiseM),
      "the line's tread budget stops it a riser short even on a straight stair; the follow reaches the landing");

// (2) The turning stair: a quarter turn, and the straight line dies at the corner.
var turn = new TreadWorld();
turn.Flight(0.4f, 0f, 1f, 0f, 5, 9.7f, -0.3f);           // five treads out, then the corner
turn.Flight(2.0f, 0.4f, 0f, 1f, 8, 8.2f, -0.3f);         // eight treads round it
turn.Box(2.0f, 4.2f, 5.8f, 0.2f, 0.8f);                  // the landing it arrives at
var turnFollow = StairFollow.Follow(turn.Floor, 0f, 10f, 0f, 1f, 0f, true, 0.4f);
Check(turnFollow.Ok && turnFollow.Turns == 1, "the follow turns once where the stair turns");
Check(turnFollow.Risers == 14, "every tread of both flights is counted, the corner included");
Check(MathF.Abs(turnFollow.RiseM + 4.2f) < 0.001f, "the whole 4.20 m of an L stair is read");
Check(MathF.Abs(turnFollow.EndX - 2.0f) < 0.001f && MathF.Abs(turnFollow.EndZ - 4.8f) < 0.001f,
      "it ends on the SECOND flight's landing - the corner did not stop it");
Check(MathF.Abs(turnFollow.EndY - 5.8f) < 0.001f,
      "and on connected ground, not mid-flight where no navmesh question can be answered");
Check(NarrowPath.TryRead(i => turn.At(0.4f * (i + 1), 10f, 0f),
                         i => float.IsFinite(turn.At(0.4f * (i + 1), 10f, 0f)),
                         20, 10f, 0.4f, 8.4f, out var turnRead)
      && MathF.Abs(turnRead.SignedM) < MathF.Abs(turnFollow.RiseM),
      "the straight line stops at the corner: a turning stair is only read whole by walking it");

// (3) The switchback: two turns, and the way back runs alongside the way out.
var back = new TreadWorld();
back.Flight(0.4f, 0f, 1f, 0f, 5, 9.7f, -0.3f);           // five treads out
back.Box(2.4f, 0.4f, 8.5f, 0.2f, 0.6f);                  // the landing, level with the last tread
back.Box(2.4f, 1.2f, 8.2f);                              // the first tread of the way back
back.Flight(2.0f, 1.2f, -1f, 0f, 5, 7.9f, -0.3f);        // five treads back, alongside the first
back.Box(-0.8f, 1.2f, 6.4f, 1.0f, 0.2f);                 // the landing at the bottom
var backFollow = StairFollow.Follow(back.Floor, 0f, 10f, 0f, 1f, 0f, true, 0.4f);
Check(backFollow.Ok && backFollow.Turns == 2, "a switchback turns twice: onto the landing and back");
Check(backFollow.Risers == 12, "both flights are counted; the landing between them is not a riser");
Check(MathF.Abs(backFollow.EndX + 1.2f) < 0.001f && MathF.Abs(backFollow.EndZ - 1.2f) < 0.001f,
      "the follow ends back along the second flight, past where it started");
Check(MathF.Abs(backFollow.RiseM + 3.6f) < 0.001f, "and reads the whole 3.60 m of the switchback");

// (4) The same L stair, climbed. Up is not a special case: it turns just the same.
var climb = new TreadWorld();
climb.Flight(0.4f, 0f, 1f, 0f, 5, 10.3f, 0.3f);
climb.Flight(2.0f, 0.4f, 0f, 1f, 8, 11.8f, 0.3f);
climb.Box(2.0f, 4.2f, 14.2f, 0.2f, 0.8f);
var climbFollow = StairFollow.Follow(climb.Floor, 0f, 10f, 0f, 1f, 0f, false, 0.4f);
Check(climbFollow.Ok && !climbFollow.Down, "an up-flight is followed as a climb");
Check(climbFollow.Turns == 1 && climbFollow.Risers == 14, "the L stair, climbed and turned");
Check(MathF.Abs(climbFollow.RiseM - 4.2f) < 0.001f, "up four metres twenty, signed as a climb");
Check(MathF.Abs(climbFollow.EndY - 14.2f) < 0.001f, "ending on the landing at the top");

// (5) What a follow must NOT call a stair. A kerb is one riser and is JumpLinkCatalog's business;
// a flat corridor is not a stair at all, and a follow that started on one would wander the floor.
var kerb = new TreadWorld();
kerb.Box(0.4f, 0f, 9.85f, 0.2f, 0.6f);
kerb.Box(2.0f, 0f, 9.85f, 1.4f, 0.6f);
var kerbFollow = StairFollow.Follow(kerb.Floor, 0f, 10f, 0f, 1f, 0f, true, 0.4f);
Check(!kerbFollow.Ok && kerbFollow.Risers == 1, "one riser is a kerb, not a stair");
var corridor = new TreadWorld();
corridor.Box(3.0f, 0f, 10f, 3.0f, 0.6f);
var corridorFollow = StairFollow.Follow(corridor.Floor, 0f, 10f, 0f, 1f, 0f, true, 0.4f);
Check(!corridorFollow.Ok && corridorFollow.Risers == 0,
      "a follow standing on flat ground is not a stair - a corridor is never walked as one");
var dropWorld = new TreadWorld();
dropWorld.Box(3.0f, 0f, 4.0f, 2.6f, 0.6f);               // the ground a storey below, from 0.4 out
var dropFollow = StairFollow.Follow(dropWorld.Floor, 0f, 10f, 0f, 1f, 0f, true, 0.4f);
Check(!dropFollow.Ok && dropFollow.Risers == 0 && dropFollow.Treads.Count == 0,
      "a storey of open air is not a stair: the follow says so instead of walking into it");

// (5b) UNEVEN GROUND IS NOT A STAIR — the live failure of 2026-10-05. Standing on an office
// floor three metres from the objective, the sweep's follow walked thirty-two treads across it
// and reported "32 risers, 2 turn(s), rise 3.06 m" from a mouth to an end thirteen metres away.
// Three metres over thirty-two treads is nine centimetres a tread: a slope, not a stair. The
// flight the owner had walked before was never reached, because the budget was spent on this.
// A TREAD gate stricter than the STEP gate is the repair: a foot may step where a follow may
// not walk, and ground is not walked as a flight.
var slope = new TreadWorld();
for (int k = 0; k < 30; k++) slope.Box(0.4f * k, 0f, 10f - 0.09f * k);
var slopeFollow = StairFollow.Follow(slope.Floor, 0f, 10f, 0f, 1f, 0f, true, 0.4f);
Check(!slopeFollow.Ok && slopeFollow.Risers == 0,
      "ground that falls nine centimetres a stride is a slope, not a stair - a follow will not walk it");
Check(StairFollow.MinTreadM > JumpLink.MinRiseM,
      "the tread gate is stricter than the step gate: a foot may step where a follow may not walk");
Check(!StairFollow.IsTread(-0.11f, true) && StairFollow.IsTread(-0.30f, true)
      && !StairFollow.IsTread(0.11f, false) && StairFollow.IsTread(0.30f, false),
      "eleven centimetres is ground, thirty is a tread - one gate, both ways up and down");
Check(!StairFollow.IsTread(-1.5f, true),
      "and a drop past the step limit is still not a tread, however a stair is defined");

// (6) A flight that stops short is reported as far as it goes, and no further.
var stub = new TreadWorld();
stub.Flight(0.4f, 0f, 1f, 0f, 3, 9.7f, -0.3f);
var stubFollow = StairFollow.Follow(stub.Floor, 0f, 10f, 0f, 1f, 0f, true, 0.4f);
Check(stubFollow.Ok && stubFollow.Risers == 3 && stubFollow.Turns == 0,
      "three treads are a short flight, and are counted as three");
Check(MathF.Abs(stubFollow.EndX - 1.2f) < 0.001f && MathF.Abs(stubFollow.EndY - 9.1f) < 0.001f,
      "the follow stops where the treads do - it never invents the rest of the flight");

// (7) The tread budget bounds the walk, and it is larger than the straight reader's: a line that
// runs past its stair is reading runaway ground, a follow still on its treads is still on its stair.
var budgetWorld = new TreadWorld();
budgetWorld.Flight(0.4f, 0f, 1f, 0f, 40, 9.7f, -0.15f);
var budgetFollow = StairFollow.Follow(budgetWorld.Floor, 0f, 10f, 0f, 1f, 0f, true, 0.4f);
Check(budgetFollow.Risers == StairFollow.MaxTreads, "the tread budget bounds the walk, as it says it does");
Check(StairFollow.MaxTreads > NarrowPath.MaxSteps,
      "and it is larger than the straight reader's - a follow does not run away from its stair");

// (8) The engine callers drive a follower one tread at a time, inside their own ray budget.
var walker = new StairFollow.Follower(turn.Floor, 0f, 10f, 0f, 1f, 0f, true, 0.4f);
Check(walker.Step() && walker.Step() && walker.Step() && walker.Treads.Count == 3,
      "three steps is three treads, and the walk can stop there for the frame");
while (walker.Step()) { }
var resumed = walker.ToFollowed();
resumed.Treads.AddRange(walker.Treads);
Check(resumed.Risers == turnFollow.Risers && resumed.Turns == turnFollow.Turns
      && MathF.Abs(resumed.RiseM - turnFollow.RiseM) < 0.001f,
      "a follow driven a tread at a time is the same follow as one driven in a single call");

// (9) The one judgement every step is made by, on its own.
Check(StairFollow.Verdict(9.7f, 10f, true, false, 0) == 1, "a tread below is a riser going down");
Check(StairFollow.Verdict(10.3f, 10f, false, false, 0) == 1, "a tread above is a riser going up");
Check(StairFollow.Verdict(9.7f, 10f, false, false, 0) == -1, "a step the wrong way is not a tread");
Check(StairFollow.Verdict(10f, 10f, true, false, 0) == -1,
      "level ground before the first riser is not a stair");
Check(StairFollow.Verdict(10f, 10f, true, true, 0) == 0, "level ground after the first riser is a landing");
Check(StairFollow.Verdict(10f, 10f, true, true, StairFollow.MaxLandings) == -1,
      "the landing budget is spent by then - a floor that stays level forever is not a stair");
Check(StairFollow.Verdict(8.7f, 10f, true, false, 0) == -1, "a drop past the step limit is not a tread");
Check(StairFollow.Verdict(11.5f, 10f, false, false, 0) == -1, "a wall a storey high is not a tread either");
Check(StairFollow.Verdict(float.NaN, 10f, true, true, 0) == -1, "a hole is not a tread, ever");

// (10) The turns are tried as heading rotations, gentle ones first.
var (turnX, turnZ) = StairFollow.Rotate(1f, 0f, 90f);
Check(MathF.Abs(turnX) < 0.001f && MathF.Abs(turnZ - 1f) < 0.001f, "a quarter turn off +X faces +Z");
(turnX, turnZ) = StairFollow.Rotate(1f, 0f, 180f);
Check(MathF.Abs(turnX + 1f) < 0.001f && MathF.Abs(turnZ) < 0.001f, "a half turn reverses the heading");
(turnX, turnZ) = StairFollow.Rotate(0f, 1f, 90f);
Check(MathF.Abs(turnX + 1f) < 0.001f && MathF.Abs(turnZ) < 0.001f, "the same quarter turn, from +Z");

Console.WriteLine($"Passed {passed} navigation checks.");

/// <summary>A synthetic stair, so a straight flight, a turning one and a switchback can be laid
/// out and walked with no game running: boxes of ground at heights. A point inside a box stands
/// on it; a point in no box is air.</summary>
sealed class TreadWorld
{
    private readonly List<(float x0, float x1, float z0, float z1, float y)> _boxes = new();

    /// <summary>One box of ground, centred on (cx, cz) at height y.</summary>
    internal TreadWorld Box(float cx, float cz, float y, float halfX = 0.2f, float halfZ = 0.2f)
    {
        _boxes.Add((cx - halfX, cx + halfX, cz - halfZ, cz + halfZ, y));
        return this;
    }

    /// <summary>One flight of treads from (x, z) along (dx, dz): <paramref name="count"/> treads a
    /// stride apart, each <paramref name="riser"/> above — or below, when negative — the last.
    /// The treads are a stride apart and the follow steps a stride at a time, so one step is one
    /// tread, exactly as a walker's foot takes them.</summary>
    internal TreadWorld Flight(float x, float z, float dx, float dz, int count, float firstY, float riser)
    {
        for (int k = 0; k < count; k++) Box(x + dx * 0.4f * k, z + dz * 0.4f * k, firstY + riser * k);
        return this;
    }

    /// <summary>The floor under a point, or NaN for air — the <see cref="StairFollow.FloorAt"/> a
    /// follow asks. The standing height is this world's business, never the caller's.</summary>
    internal float At(float x, float standingY, float z)
    {
        foreach (var (x0, x1, z0, z1, y) in _boxes)
            if (x >= x0 && x <= x1 && z >= z0 && z <= z1) return y;
        return float.NaN;
    }

    internal StairFollow.FloorAt Floor => At;
}
