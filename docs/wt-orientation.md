# World Tour — orientation and choosing where to go (2026-09-27)

Tester request (2026-09-26): *"necesitamos encontrar una mejor forma de localizarme por el mundo abierto  
y poder ir a cualquier sitio… aún falta anunciar zonas al pasar por ellas… decidir hacia qué punto quiero  
ir de cualquier sitio del mapa"*. The radar, beacon, route guidance and ladder routes exist; what was  
missing is (1) knowing which part of town you are in, hands-free, and (2) choosing a destination  
without the phone map's free cursor. Status of every piece: **PENDING IN-GAME VERIFICATION**.

## 1. What the user's other mods taught (research digest)

| Idea                                                                                                                                | Seen in                                                                      | Taken here                                               |
| ----------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------- | -------------------------------------------------------- |
| Category + nearest-first target picker built from the game's live markers (R3/V, L1/R1 categories, up/down items, A lock, B cancel) | Dragon Ball Z Kakarot (`nav_tracker.lua`), Diablo II (`docs/entity-scan.md`) | yes — the destination picker                             |
| Route-aware beacon, spoken reachability verdict                                                                                     | Diablo II (`docs/beacon.md`), Kakarot                                        | already existed (`BeaconDestination`, `LevelRoute`)      |
| Zone name on entry from the game's own tracker; first entry instant, only boundary flicker debounced                                | Among Us (`RoomWatcher`), Diablo II (`docs/rooms.md`)                        | yes — `ZoneHooks` + `RegionTracker`                      |
| Persist discovered places per map so a new session lists them at once                                                               | Diablo II (`docs/exploration.md`)                                            | yes — `MapIconSnapshot`, `SavedPlaces`                   |
| On-demand "where am I" macro readout, separate from turn-by-turn                                                                    | Diablo II (`docs/terrain-overview.md`)                                       | yes — Z (`WhereAmIReadout`)                              |
| Ambient cues must never interrupt/requeue into dialogue; one-shot events may                                                        | Kakarot                                                                      | zone lines queue, never interrupt; muted during dialogue |
| Remember selection by name, not index, when a list is rebuilt                                                                       | Kakarot (`screen_map.lua`)                                                   | the picker remembers the last category                   |

Kakarot never solved automatic zone-entry announcement in free roam; Among Us marks room entry as its most  
fragile item. Both argue for taking the zone from the game's own tracker, which is what §2 does.

## 2. Zones announced while walking — `Hooks/WorldTour/ZoneHooks.cs`

`ZoneHooks` is the **only** zone speaker. Sources, best first:

1. **The game's section banner** (`Hooks/WorldTour/SectionNoticeHooks.cs`). The banner component  
   `app.UICityHud_SectionNotice` is not reachable through `via.Scene.findComponents` (its scene poll never  
   logged "found" in any session), so the requests are **hooked** instead:  
   `app.worldtour.CitySectionManager.RequestSectionNotice(CitySectionInfo)`,  
   `app.UICityHud_SectionNotice.RequestSectionNotice(CitySectionInfo, bool)` and `.SetupNotice(RequestInfo)`  
   (`RequestInfo_SectionNotice.SectionInfo`). The section id comes from `CitySectionInfo.SectionId`; the  
   banner's own text (`mTextSection`, e.g. "Hong Hu Lu Chinatown - Plaza") is read on the next frames,  
   ignoring the previous banner's text, for up to a second; the section-table name is the fallback.
2. **The section id, polled** (`ZoneNameService`). **Root cause of the silence until now:**  
   `app.worldtour.CitySectionManager` is **not a managed singleton** — it is a plain object owned by  
   `app.GuiManager.AccessData` (`GUIAccessDataManager.SectionManager`, `GuiManager.cs:4653`,  
   `GUIAccessDataManager.cs:74`), so `API.GetManagedSingleton` returned null and every reading was 0.  
   `WorldTourStateService.GetSectionManager()` now goes through `GuiManager.AccessData`; the static  
   `app.Kpi.WT.GetCurrentSectionId()` (`Kpi.cs:397`) is the fallback. A section crossing waits  
   `BANNER_GRACE_MS` for the banner's richer wording before speaking the table name.
3. **Landmark region** — when no section has ever been named in this city: the area around the nearest  
   fast-travel point ("zona de Beat Square"), from `RegionTracker`: the first reading is immediate; a  
   change needs the new landmark to be nearer by 20 % of the distance to the current one (never less  
   than `ARRIVED_M`) for two refreshes (1 s). This is what the 2026-09-05 "nearest landmark" chatter  
   lacked. Suppressed for the rest of the visit once the game's banners have been heard.
4. **The city**, as the last resort.

Dedupe: sections by id, regions/city by name; a newer zone replaces one still waiting, except the banner  
for the same section. Zone lines are queued (never interrupt), muted in menus, fights, dialogue, the pad  
tutorial and the destination picker. `FieldAwarenessHooks`' arrival count ("N personas cerca", fired on a  
section change, at most every 20 s) is now queued too, so it cannot cut the zone name off.

## 3. "Where am I" — Z (`Services/WorldTour/WhereAmIReadout.cs`)

"En Hong Hu Lu Chinatown - Plaza. Beat Square, a las 3, a 40 metros. Mirando al norte. Destino:  
objetivo, a las 11, a 120 metros." — zone, nearest fast-travel landmark with camera-relative clock  
direction and distance, facing, and where the current guidance target lies (straight line). A height  
difference beyond `ARRIVED_M` is added ("12 metros más arriba").

## 4. Choosing a destination from anywhere — the picker (`Hooks/WorldTour/DestinationPickerHooks.cs`)

**Hold R3 / hold V** (long-press threshold = the user's Windows keyboard repeat delay,  
`ReadoutShortcut.SystemHoldDelayMs`, 500 ms by default). A **tap** of R3/V keeps its old meaning — turn  
the camera down the next leg of the route — but now fires on release. While open, the field's input is  
masked with the game's own one-frame lock (`FieldInputMask`, confirmed working 2026-09-09).

| Action                                       | Pad                         | Keyboard                 |
| -------------------------------------------- | --------------------------- | ------------------------ |
| next / previous place (nearest first, wraps) | d-pad down / up             | ↓ / ↑                    |
| next / previous category                     | d-pad right / left, R1 / L1 | → / ←, PageDown / PageUp |
| choose                                       | Decide (A / Cross)          | Enter                    |
| close without changing                       | Cancel (B / Circle), R3     | Backspace, V             |
| delete a saved spot                          | X / Square                  | Delete                   |

Each entry: "Style Lab Beauty Salon, tienda, a las 2, a 85 metros, 3 de 12". Choosing sets  
`MapSelectionMemory`, so the existing beacon, route guidance, ladder routes and R3 aiming all follow it;  
"Objetivo de la historia" hands guidance back to the mission; "Guardar este lugar" saves the current  
position as "<zone>, lugar N".

- **A destination on another level can now be reached by naming the way up.** Ladders and doors within  
  30 m (`AccessPointCatalog`, a full-scene sweep of `Om011000`/`Om016000`/`Om017000` — the same families  
  `FieldAccessCatalog` samples locally) appear in their own category. Choosing one walks the player to  
  the end nearer them and, on arrival, says how to use it (`wt.route.at_ladder_up/down`, `at_door`).  
  Before this the beacon pointed at a roof target and the walk dead-ended at the wall under it, with no  
  way to say "the ladder that goes up" (user report 2026-09-29). The picker remembers the kind, so  
  `BeaconDestination` keeps the access point off `LevelRoute` — asking for ladders *to* a ladder is not  
  a plan.
- **The chosen destination now survives menus, the phone and street fights.** `MapAimHooks` used to  
  clear it whenever `CanSpeak` went false; it is now forgotten only when control comes back in a  
  different city (`ForgetOtherCity`).
- After closing, the field stays masked until the closing button is released, so the game never sees  
  that press (A would otherwise talk to whoever stands in front). The picker closes itself for  
  dialogue, menus, fights and the pad tutorial (which also uses Decide).
- While the list has no shops/masters/missions/jobs, opening it adds once per session: "Para añadir  
  tiendas, maestros y misiones, abre una vez el mapa del teléfono".

Categories (`DestinationCategory`, cycled in this order, empty ones skipped): story objective, places  
(fast travel), ladders and doors nearby, shops and services, masters, missions, jobs, people nearby, map  
markerewqqs, my places.  
Sources (`DestinationCatalog`, live first; same name within `2 × ARRIVED_M` in a category = one place):

| Source                                                                                                                                                                                    | Status                                                                                         |
| ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------- |
| Mission objective (`MissionTargetService`)                                                                                                                                                | confirmed in game                                                                              |
| Fast-travel points (`FastTravelCatalog`, extracted from `ZoneNameService`)                                                                                                                | confirmed 2026-09-04 (8 points, Metro City)                                                    |
| Nearby named avatars (`AvatarFieldReader`, the N key's source)                                                                                                                            | confirmed 2026-07-20                                                                           |
| Ladders and doors within 30 m (`AccessPointCatalog`: `SceneComponents` sweep of `Om011000`/`Om016000`/`Om017000`, 1.5 s rescan, nearest-first; ladder names its near end "leads up/down") | **new 2026-09-29** — log `Access points: N ladders and doors in the scene, listed within 30 m` |

### Ladder-search budget (`LevelRoute`)

Choosing a destination and walking is the whole point — the player should not have to find the ladder.  
The automatic search (`LevelRoute` + `LevelRoutePlanner`) already does that; when it *seems* not to, it  
is almost always the search stopping early, not a missing feature.

- **`QuestionBudget = 120`** (2026-09-29, was 48). The outward phase asks one question per ladder per  
  direction before either two-ladder pass starts: 7 ladders = 28 questions. The old 48 ran out exactly  
  (`48 questions (48 queries)` in the log) while near ladders were still unexplored, and the result was  
  reported as a partial plan the search never earned. 120 ≈ 30 s at `FieldNavPath.MinQueryIntervalMs`  
  (250 ms); the wait is the price of not hunting by ear.
- **Endpoint nudges are not charged to the question budget.** `Ask` retries an off-mesh endpoint up to  
  `Nudges.Length` (4) times to complete *one* question; those retries no longer count as questions, so a  
  search is never cut short merely because ladder ends sit off the navmesh (which most do).
- **`LevelRoute.SearchElapsedMs` / `Searching`** exist so the beacon can speak progress:  
  `MissionBeaconHooks.AnnounceSearchProgress` says `wt.route.searching_still` every 6 s (silent for the  
  first 3 s, where the state-change sentence already covered it). Thirty seconds of silence is  
  indistinguishable from a hung mod.
- **Truncation is logged, never silent.** `Pump` writes `question budget spent (…)` when the planner  
  stops on its budget, so the log no longer shows a budget-cut search as a plain partial plan.
- **Going down searches like going up (reverted 2026-10-02).** An attempt to never hand a below-player  
  destination to `LevelRoute` (guiding to a drop-off instead, `DropsDown`/`DescendTo`/`Descend*`) was  
  rolled back at the owner's request: the planner searches ladders for a below destination exactly as  
  for an above one. The relay-hop work (`RelayPlanner`) and the end-of-walk hint suffix  
  (`wt.route.hint_down/up/blocked` via `RouteNotice.EndHint`) stay — they add information without  
  taking the bearing or the homing sound away.


### ⭐ The retry's 1-question budget, and why narrow stairs never get replanned (2026-10-05) — ROOT CAUSE FOUND

**Symptom, in the owner's words:** "现在这个导航看不见那些可以直接走上去的金属窄楼梯，于是就规划  
不出路线" — the navigation cannot see the narrow metal stairs you can simply walk up, so it plans no  
route.

**The stairs ARE seen. The link IS built. The plan is not restarted — that is the whole bug.**  
From the 2026-10-05 12:37 log, walking the stair in front of the player:

```
12:37:00 Jump scan: player (-45.5, 22.9, 35.3) to (-54.2, 34.3, 34.4);
         floors 0.5m=22.82 0.9m=22.95 1.3m=23.10 1.7m=23.25 2.1m=23.39 2.5m=23.54 2.9m=23.68 3.3m=23.71;
         crossings step (-45.5, 22.9, 35.3) -> (-46.4, 23.0, 35.2)
```

Eight samples rising 0.89 m over 2.8 m — `Build()` produced a `step` link, exactly as designed. And  
the player demonstrably climbed it: Y 22.9 at 12:37:00 → **23.71 at 12:38:01**.

**The real fault, `LevelRoute.Retry()` (`LevelRoute.cs:253`):**

```csharp
Start(_key, _target, player, askDirect: true, changed || climbed ? QuestionBudget : 1, silent: true);
```

`climbed` is `|player.Y - _start.Y| >= LevelM` (2 m) or `Flat(player,_start) > ReachM`; `changed` is  
the ladder signature, 3 m flat, or 2 m vertical. **A narrow stair raises the player 0.8–1.0 m — under  
`LevelM`.** The 2.4 m walk between two retries is under `ReachM` too. So both flags read false, the  
budget is **1**, and `LevelRoutePlanner.Search()` spends that one question on the direct  
`Question(Player, Target)` — which is false — and stops. Log, every 30 s, 12:35:30 → 12:40:02:

```
Level route: question budget spent (1), 1 navmesh queries; what follows is what was reached
```

**Why it looks like "blind to narrow stairs" and not "blind to ladders":** a real ladder climbs 6.3 m  
(`(-45.9,17.1,24.5) → (-45.9,23.4,24.5)`), over `LevelM`, so `climbed=true` and the FULL 120-question  
search reruns. A narrow stair climbs 0.8 m, under `LevelM`, so it never does. The threshold that  
gates a replan is a WHOLE FLOOR (2 m) while a stair flight is HALF A FLOOR — the stair falls in the gap.

**Two separate defects, do not conflate them:**

1. **`Retry` budget.** Narrow-stair progress is invisible to `changed || climbed`, so the search that  
   could plan past the stair is never restarted. This is the one the symptom is about.
2. **`JumpLinkCatalog` models one hop, not a run.** `Build()` emitted at most ONE step link and then  
   `break`ed, from a single 8-point line along player→target, 0.5–3.3 m, with no lateral clearance  
   probes. So it could name the FIRST riser of a stair and could not describe the stair as a path  
   (how long, which way, whether the sides are open). This is why, once the player stood on the  
   landing (`12:38:01` onward, `23.71 x8`, `crossings none`), the next flight was invisible — it was  
   either off the player→target line or beyond 3.3 m. Fix 1 alone restores replanning; fix 2 is what  
   makes a stair usable while walking it (and is what `WalkSteps`/`WallFollow` would want).

### ⭐⭐ Both fixed (2026-10-05): a stair is read as a flight, with a cliff and a wall

The owner's ask: "目标是让导航能带我走上楼梯，另外有时候楼梯是一面悬崖一面高墙，他也要能认出来" —  
walk them UP the stair, and recognise the stair that is a cliff one side and a wall the other.

**The pure logic lives in `Services/WorldTour/NarrowPath.cs`** — no engine, so it is unit-tested  
(`tests/Navigation.Tests`, 177 checks). Two pieces:

- **`NarrowPath.TryRead(...)`** reads a flight from the floor line: the first riser (a floor above the  
  player's own by `MinRiseM..StepM`), then every sample that climbs or holds, stopping at a riser too  
  tall, a hole, or a run that keeps descending. `risers < 2` returns false — **one riser is a kerb,  
  which `JumpLinkCatalog` already offers as a single crossing, and a flight must not duplicate it.**  
  Result: `Flight(FirstStep, Top, RiserCount, RiserM, TotalRiseM, RunM)`.
- **`NarrowPath.ReadSides(left, right)`** classifies each hand as `Floor` / `Drop` / `Wall` /  
  `Unknown` and answers `Cliff`, `Wall`, `CliffAndWall`, and which hand each is on. **Either hand  
  `Unknown` makes every verdict false, including `Open`** — an unmeasured side is not open ground.

**`JumpLinkCatalog` was rebuilt around it:**

- `Samples` 8 → **24** (0.5 m → 9.7 m), so a full flight fits in one scan; `RaysPerPump` 2 → 6.
- `Pump()` is now `PumpFloors()` → `PumpSides()` → `Build()`. The floor line is read first, then one  
  sideways probe pair per step of the flight.
- **`ProbeSide(i, left)`** is two measurements, because a cliff is an absence and an absence of a wall  
  is not a floor: `TryCastAcross` says whether anything stands out there (a contact classifying  
  `HitClass.Wall` within `SideReachM` = 1.2 m = *Wall*), and `TryFloorBelow` at the probe's far end  
  says whether anything can be stood on (within `SideFloorM` = `JumpLink.StepM` = *Floor*; otherwise  
  *Drop*, the cliff). Either probe failing is `Unknown`.
- **`AddFlight()`** emits **one link per riser** from the player's own floor upward, so  
  `LevelRoutePlanner` walks the stair a step at a time using the machinery it already has. `MaxLinks`  
  3 → 24.
- The log line gained the stair: `; stair 4 risers x 0.30 m, up 1.20 m over 4.0 m, sides L=CLIFF
  R=wall (cliff and wall)`. Read `sides` after a test: `?` on either hand means the probe could not  
  run, and `Drop` vs `Floor` is the cliff.

**And the `Retry` gate:** `climbed` is now `|player.Y - _start.Y| >= JumpLink.StepM` (1.2 m) instead  
of `LevelM` (2 m), with the reasoning recorded in the code. A stair's progress is a step, so a step is  
what the gate measures; standing still still reads false and still costs one question.

**Not done yet:** the cliff/wall verdict is in the sensor and the log but has NO spoken cue. Wiring it  
into `RouteNotice`/`WalkSteps` (e.g. " stairs: cliff on your left, wall on your right ") is the next  
piece, and it needs the owner's wording.

### ⭐⭐⭐ The correction that mattered: a stair must be found BEFORE planning (2026-10-05)

The owner's correction, after the first version of the stair work:  
"我要的不是我都站在楼梯前面了他才认出来，我要的是规划路线的时候就认出来，然后归航音带我走到  
目标处." — Not "recognise it once I am standing in front of it", but **recognise it while the route  
is being planned**, so the homing cue can lead to the objective.

That is a different thing entirely, and it is what `StairCatalog` (`Services/WorldTour/StairCatalog.cs`)  
is for. A stair has to be **one of the connections the planner may route through, exactly as a ladder  
is** — otherwise no plan can ever contain it, however well it is measured.

- **`StairCatalog`** sweeps the ground AROUND the player, in the gap the ladder sweep already leaves,  
  and **before the planner is built** (`LevelRoute.Pump`: `LadderCatalog.Sweep()` →  
  `StairCatalog.Sweep()` → `JumpLinkCatalog.Pump()` → `new LevelRoutePlanner(Links(), …)`).  
  Three passes, all rays and no navmesh question:
  1. **Coarse** — 16 directions × 6 samples (0.8–2.8 m). Keeps the directions that rise ≥ 0.5 m.
  2. **Fine** — up to 6 rising directions × 20 samples (0.5–8.5 m), each read by  
     `NarrowPath.TryRead` into a flight.
  3. **Sides** — one sideways probe pair every 3rd step, folded through  
     `NarrowPath.ClassifySide`: a wall, a floor level with the step, or air (the cliff).
- Each find is an ordinary `Link(Low, High, Jump: false, Stair: true)`; `Links()` is  
  ladders + stairs + crossings. Whether the player can walk to its foot, and whether its top leads to  
  the objective, are questions the planner asks the navmesh — the only judge. A stair the player  
  cannot reach is simply never chosen. **Additions only.**
- **`LevelRoutePlanner.Link` / `Leg` gained `Stair`**, `LevelRoute.Guidance` and  
  `BeaconDestination` carry it, and `RouteNotice` speaks it: `wt.route.stair` ("楼梯" /  
  "the stairs"), `wt.route.stair_step` ("找到能上去的路：往前 {0} 米有一段楼梯，走上去后继续走"),  
  `wt.route.stair_near` ("到了：这就是那段楼梯，走上去"). A stair is NOT a ladder (no action button)  
  and NOT a crossing (`Jump` false), so it is named on its own.
- Log lines to read after a test: `Stair sweep: player (…); N of 16 directions rise; M stairs; #0
  (…) -> (…) up X.XX m, sides L=CLIFF R=wall (cliff and wall)`, then the plan line  
  `Level route: plan after Q questions: …`. A stair in the plan is the whole point.
- Tests: `tests/Navigation.Tests` **183 checks** — a stair is a connection the plan goes up, the plan  
  walks to its foot and leaves at its top, and two stairs chain as two ladders do.

**Why the first version was not enough:** `JumpLinkCatalog` measures ONE line, from the player toward  
the objective. It finds a stair only when the player already faces one — which is "standing in front  
of it", precisely what the owner did not ask for — and it cannot see a stair off to the side. Both now  
run: the sweep for planning, the line for the crossing right under the player's feet.  
| `GuiManager.AccessData`: `GetAccessTargetData(ContactUIType.Legendary / NPC)` (masters; NPCs with `ShopComponent` = shop clerks) and `GetWTCityOMSetterData_OM(OMType.Shop)` (`WTCityOMSetter.GetDispName()` is a message **Guid**, resolved pinned, facilities cached per city) | **unverified** — log line `Destinations from the game's map registry: …` |  
| Phone map snapshot: `MapParam.DeviceMap` → `UIDeviceMapWindow.GetMapIconPanelList(ICON_GROUP_TYPE)` → panel `Info` (`TitleText`, `Type`, `GlobalPos`), every 2 s while the map is open (first capture 2 s after it opens; a group that reads empty keeps what was learned), saved per city to `reframework/data/sf6access_map_places.txt` (missions not saved) | **unverified** (the same `Info` read is confirmed for the cursor) — log `Map places captured - city …` |  
| Saved spots, `reframework/data/sf6access_saved_places.txt` | mod-side |

The phone map is the richest source (everything a sighted player sees). If the registry route fails,  
opening the phone map once per city is enough to fill the picker, in that session and later ones.

## 5. Other RE facts found for this work (decompiled, `sf6 code/`)

- `GUIAccessDataManager` (`app/GUIAccessDataManager.cs`) also holds `ScenarioZoneList`  
  (`ScenarioZoneGroup`, mission zones with `MissionZoneTarget.mRadius`) and `NpcDummyAccessTargetList`.
- `HudDef.ContactUIType`: `NPC=0, Legendary=1, OM=2, OtherPlayer=3` — masters are `Legendary`; shop  
  clerks, job givers and mission NPCs are all `NPC`, told apart by `WTNpcAccessTarget` members  
  (`ShopComponent`, `IsLegendaryNPC`, `MissionTargetInfo`, `ObjectPos`, `VisibleUI`).
- Player map pins are saved in `WTPlayerDataUI.GetCityPinInfoList(uint cityId)` → `PinInfo.Position`  
  (reached from `WTPlayerManager.getPlayerData(...)`.UI) — not used yet; the map snapshot covers pins.
- `udCityPointList.CityPoints` (`PointDataCityInfo`) has positions but no names.

## 6. In-game verification checklist

- [ ] `SectionNoticeHooks initialized (3 of 3 section notice hooks)`; walking between named parts of a  
  district logs `Section notice hook fired: …` and `Section notice: '<text>' (section N)` and speaks it once.
- [ ] `Section id N read through manager` (or `Kpi.WT`) appears — the polled route works.
- [ ] `Zone: CitySectionDataUserDataDict enumerated - N named sections from M of T records`.
- [ ] If neither fires: "zona de <landmark>" announcements while walking — too many, too few?
- [ ] Z: zone, landmark with clock direction, facing, destination — all correct?
- [ ] Hold R3 (and V): "Destinos. Lugares, 8. Beat Square, a las …". Tap R3 still aims, only on release.
- [ ] `Destination picker pad: decide=… cancel=… south=… east=…` and `Field input mask bound …`.
- [ ] While the picker is open the avatar does not move; d-pad/arrows browse; L1/R1 switch categories.
- [ ] Choose a shop: "Destino: …"; the beacon and R3 aim now lead there; arrival clears it.
- [ ] `Destinations from the game's map registry: N masters, N NPCs, N facilities` — report the numbers.
- [ ] Open the phone map, wait 2 s: `Map places captured - city 400: TRAVEL=…, FACILITY=…` — then the  
  picker lists those categories, also after restarting the game.
- [ ] "Guardar este lugar", walk away, choose it back; Delete/X removes it.
  - [ ] Zone lines never interrupt dialogue and are not cut off by the "N personas cerca" count.
