# World Tour auto-walk — plan and findings (2026-09-29)

> ## ⛔ STATUS 2026-09-29 22:2x — AUTO-WALK IS **OFF** IN THE SHIPPED BUILD.
>
> The owner, in game: *"我进入游戏选择目的地后还是会有自动导航，他不是 f12 的问题，是之前我们设计的按
> a 键寻路的问题，请关掉这个自动导航，他有时候会和我抢手柄."*
>
> **Why it was still running.** The F12 trial was already retired (NaviTrialHooks.TRIAL_ENABLED =
> false, the twenty-seventh finding). The thing the owner was feeling is the OTHER driver — the one
> that starts by itself the moment a destination is picked: `MissionBeaconHooks.Resolve` calls
> `AutoWalk.Drive` on every resolve. "抢手柄" is not a symptom there, it is the mechanism: `AutoWalk`
> registers an `AvatarInputController` at `FieldEvent` priority, which **outranks the player's own
> `Default` controller**, and re-issues its order every frame. For a blind player that is the exact
> thing this codebase's own safety rule forbids — the mod must never fight them for their avatar.
>
> **The switch.** `MissionBeaconHooks.AutoWalkEnabled` (a `static readonly bool`, currently `false`)
> guards the one call to `AutoWalk.Drive` in `Resolve`. Flip it to `true` and nothing else. The
> driver itself is untouched and still documented below.
>
> **What stays ON, deliberately.** Every read-only part of the feature: the homing cue, the route
> state notices ("已到达", "正在搜索路线"), the ladder grip line, and the turn-by-turn "右转 / 前方 12
> 米" guidance. A player who used to be walked to the target now has to walk there themselves, which
> makes the spoken directions *more* necessary, not less.
>
> **Deployed build: `5dcb00df152665bd9f639b947a6b4071`** (Release, verified against the staged DLL).
>
> ---
>
> Everything below is the historical record of how auto-walk was built and why it never became
> reliable. Read it before re-enabling: the last three crashes all came from this feature WRITING
> into the engine, and `SetChaseWalk`'s semantics were never actually settled.

**History (stage 2) — AND THE MOST IMPORTANT THING LEARNED SO FAR IS THAT NOTHING BEFORE NOW WAS
MEASURABLE.** Six defects closed in sequence: the request's `via.vec3` was never really being built
(thirteenth); `TargetType=Position` asks the engine to walk AT a co-ordinate rather than find a way
there (fourteenth); the destination can be somewhere no walk reaches (fifteenth); the gate asked its
own path query and lost the shared rate limit every time (sixteenth); the point being fed to
`StartNavi` was the beacon's homing cue, and for a ladder leg a ladder foot whose top had already
been rejected (seventeenth); and — the one that invalidates the evidence for all of them — **the
existing `AutoWalk` driver was running its own straight-line walk the whole time, on a stale
destination, in parallel with the trial** (eighteenth). `AutoWalk` now stands down completely while
`StartNavi` owns the avatar, and the trial measures the avatar's actual displacement before claiming
anything worked. Whether the engine's own pathfinder walks, and whether the request contents are
right, is **being asked properly for the first time**. Deployed as `00a76583`.
The twenty-sixth finding is the newest, and it is the wall: the 20:15 trial proved the mechanics
end-to-end — element stored at the array's native data pointer, read back, engine FOLLOWED it
(moved 4.29 m, **cos=0.757 TOWARD, encoding Point confirmed**) — and exposed the last defect in
the same breath: walk 1 wedged the avatar against a wall 3.16 m short (travelled (−1.07, −4.15)
against the requested bearing (+2.08, −4.21)), and walk 2, from the wedged position, moved 0.00 m.
**A one-point `RoutePos` hands the engine a destination and no route, so it walks AT the point and
presses into whatever stands between** — the "开始走了，但是开始顶墙" report. The fix: `RoutePos`
is now filled with the engine's OWN string-pulled bend list, **recalled** from the query the
beacon's `Resolve` ran milliseconds earlier (`FieldNavPath` caches the whole `RouteShape` per
query; `TryRecallRoute` returns it inside the same freshness window `RouteTo` trusts) — never
re-queried, which is the race the old log-only `ProbeRoute` lost on every press. The engine's
`set_Item`, proven a no-op through this API three times, is removed; the store is a direct
payload-relative write of every element at the array's own data pointer, each read back.
The twenty-fifth finding is the one that completes the autopsy: `via.Array` keeps its elements
**behind a native pointer at +0x10** (count at +0x18) — the old addressed write was overwriting
that pointer and count with float bits, so **every prior verdict was measured on wild-pointer
garbage** (15:43's "wrong sign" and the 0.42 m reflection were numerology; the Point/Offset
question was never tested). The encoding is reset to `Point`. The store now targets the data
area — engine `set_Item` first, then a payload-relative direct write at the game's own pointer —
verified by dumping those bytes before and after.
The twenty-third finding is the newest: with the array now correctly typed from the request's own
`RoutePos` metadata, `get_Item(0)` returns **null** on a fresh array — the element is reached as a
reference, so the fill is a **store** (`set_Item(0, buffer.View)`, the `AutoWalk` shape), verified
by calling `get_Item` again and requiring the element to come back.
The twenty-second finding: the route array was built from the **string** `"via.vec3[]"`, which on
this engine resolves to the **jagged** type `via.vec3[][]`, so its 16-byte element was an object
pointer — and writing three floats there corrupted a live managed object and **crashed the game**
on the second press. `TryIndexerWrite`, `ARRAY_DATA_OFFSET` and the `"via.vec3[]"` constant are
deleted with comments saying why they must not return.
The nineteenth finding is still the live one: the avatar walks the engine's own drawn route **in the
wrong sign** (177.9° off, 0.42 m from a reflection), so the open question is the `RoutePos`
*encoding* — `Point` vs `Offset` — which only the avatar's displacement can settle, and which the
signed verdict now toggles automatically (the 15:43/16:05 runs confirmed the toggle works:
`cos = -0.956` → switched to `Offset`).
The fourteenth finding's plan — filling `RoutePos` with the route's corners — remains valid for
reachable destinations and is **not yet enabled**. The obstacle-break case (`ObstacleHold`, the
eleventh finding) remains compiled but **not wired**.
**both vector actuators are closed, and that is proven rather than suspected.** `SetChaseWalk`
closed at three readings (all exactly 0.0 m) and
`AvatarPlayerControll.SetSlaveMoveVec` closed at rates 1 / 3 / 100 (all ≤ 0.2 m in
1500 ms), while the player's own manual walking measured 4.37 m/s in the same session —
so the avatar moves fine and is simply not being driven by either method. The controller
leak found in test 4 is fixed and **confirmed by test 5: the player can take over on every
target, no exceptions** — that half must not regress. History: test 1 crashed the player's
input (fixed, confirmed gone). Tests 2–5 did not crash. Test 4 **cost the player the
controls** on its second target — see "The fifth finding", the most important section
here. Tests 2, 3, 4 and 5 did not walk — see "The sixth finding" directly below.**

The user's request (2026-09-29): after selecting a mission objective and pressing A,
the avatar should **walk there by itself**, including going around obstacles and
climbing ladders, so the player never has to find the route.

> **This reverses the 2026-09-12 decision** ("Do not build auto-walk / teleport, and
> do not replace the radar", `sf6-screens.md` § "native navigation infrastructure
> leads"). That decision is superseded by the user's own 2026-09-29 request; the
> radar stays as-is either way (it is what the player uses when NOT auto-walking).

## The twenty-first finding: the write was never late — the diagnostic read before it

**The twentieth finding was a misreading, and the instrument that produced it is what proved it.**

The 15:52–15:53 element-byte dump showed the array holding *the value asked for on the previous
press*:

| press | asked | bytes actually held |
|---|---|---|
| 15:52:11 | -248.544 | **-248.544** ✅ |
| 15:52:45 | -253.378 | **-248.544** ❌ (previous value) |
| 15:53:03 | -245.911 | **-253.378** ❌ (previous value) |
| 15:53:08 | -245.911 | **-245.911** ✅ |

Read as "the write lands one press behind" this looked like a storage bug. It is not. **The dump
runs inside `WriteArrayElement0`, i.e. before that press's write.** So the bytes it prints are
necessarily the *previous* press's successful write — the array is a globalized shared instance
(`_sharedRouteObject`, reused at line 818), so the previous value genuinely persists. The table is
exactly what a *correct* write looks like when observed one step early. The two rows marked ✅ are
the ones where the same value was pressed twice in a row.

**What survives from the twentieth finding** — and it is the load-bearing part — is that
`via.vec3..ctor` **never works** on this engine:

```
Exception thrown in REMethodDefinition::invoke for via.vec3..ctor
No fix necessary
```

On *every* run this session. `No fix necessary` is REFramework saying the native call produced no
value this layer can box, so the constructed struct read back as `0.000` and the indexer route
always fell through to the addressed write — which is the route carrying every wrong-sign result.
The `.ctor` path is removed; the indexer now receives **`_routeElement.View`**, the address of a
`FieldOutBuffer`, exactly as `AutoWalk.Order` has always handed a `via.vec3` to `SetChaseWalk`.
A by-value 12-byte struct argument is a pointer to its bytes, so the buffer's address *is* the
value, and no construction is involved anywhere.

**Two rules, both kept:**
1. **A diagnostic must record when it ran, not just what it saw.** A read taken before the write
   it is meant to check will always look one step stale, and that artefact cost this session a
   whole finding. The dump now also prints `get_Item(0)`'s own return, so the address and the value
   are both attributable to the same instant.
2. **`InvokeBoxed`'s declared return type is an assertion, not a fact.** `get_Item` is declared to
   return a 12-byte value type; it is asked for a `ulong` and the result is only dereferenced after
   a non-zero check, so a shape mismatch reports itself in the log rather than silently producing
   a plausible pointer.

## The twenty-fifth finding: the elements live BEHIND a pointer — and every prior verdict was measured on garbage

**Symptom.** *"一样的，哪个目标都是不能自动行走。"* The before/after dumps came back **identical** —
`set_Item` neither threw nor changed anything in the array object's first 64 bytes. But decoding
those bytes revealed why, and it rewrites two earlier findings:

```
+0x00: 78 0e ad 11 00 00 00 00   klass (the array's type)
+0x10: c0 35 59 4d 01 00 00 00   0x14D5935C0 — a POINTER to the elements
+0x18: 01 00 00 00               count = 1
+0x1C: 01 00 00 00               capacity/version = 1
+0x20..: zeros
```

**This is RE Engine's `via.Array`: the elements live in NATIVE memory behind the pointer at
+0x10 — not inline in the array object.** The twenty-fourth finding's "element 0 is twelve inline
bytes" was wrong; the scan that refused the store was reading the pointer and the count, which
correctly never change.

**The crash autopsy, completed and corrected.** The old addressed write put floats at
+0x10..+0x1F — which is **the elements pointer and the count**, not a "header". Every run that
"stored the element" was overwriting the pointer with float bits and the count with a float's
bits. The engine then dereferenced a wild pointer to find its route:

- **15:43** — it walked 4.86 m toward whatever the garbage pointer led to. The "wrong sign" was
  never a Point/Offset confusion: **the destination was never the walk point at all.** The 0.42 m
  reflection match was numerology on garbage.
- **16:04** — the wild pointer faulted and killed the game.

**Consequence: the Point-vs-Offset question was never actually tested.** Every prior verdict,
including the one that flipped the encoding to `Offset`, was measured on a corrupted array. The
encoding is reset to `Point` for the first honest trial.

**The store, done right.** The array's data pointer is **read** (never written): `*(arrAddr+0x10)`,
with the count at `+0x18` logged. Then:

1. The engine's own `set_Item(0, buffer.View)` is tried first — harmless if it is a no-op, and the
   dumps now say whether it stored anything.
2. The **data area** is dumped before and after and scanned for the requested x/y/z triple. Found
   there → the store is real (whoever wrote it).
3. If not found, the fallback writes the three floats **at the data pointer itself** —
   payload-relative offsets (+0/+4/+8), the convention `FieldOutBuffer`'s ray-caster writes have
   proven for weeks, aimed by the game's **own** pointer, never a hand-computed address. It is
   read back from the same native address the engine will read from.
4. `arrAddr+0x10..+0x1F` is **never written again** — that region is the engine's pointer and
   count, and the 15:43 wrong-sign walk plus the 16:04 crash were both this file writing floats
   over exactly them.

**Deployed as `78ab2910`.** With the element genuinely in the array's own storage for the first
time, the next VERDICT is the first one that can be trusted — and it starts from `encoding=Point`.

## The twenty-fourth finding: get_Item CANNOT verify a value-type element — read the array's bytes instead

**Symptom.** *"已测试，所有目标无法寻路。"* With the array finally the right type (the 16:39 binding
line: `RoutePos declared as via.vec3[]`, creation: `element type=via.vec3 (16 bytes)`), every press
still refused — and this time the log is unambiguous about where:

```
set_Item(0, (-248.544, 0.169, -108.574)) did not throw, but get_Item(0) returns no element —
  the array stored nothing this code can see.
```

**The store did not fail — the VERIFICATION was wrong.** `get_Item(0)` returning null is what a
VALUE-element array *must* answer: element 0 is twelve inline bytes inside the array object, not a
reference to an object, so there is no `ManagedObject` to hand back. (The 16:05
`InvalidCastException: ... to type 'System.IConvertible'` was the same fact from the other side —
there `get_Item` *had* returned a `ManagedObject`, because that array was the jagged
`via.vec3[][]`, whose elements really are references.) Demanding a non-null `get_Item` refused a
store that may have been perfectly good. **The gate was rejecting correct walks.**

**The field dump settled what via.vec3 is:**

```
IsValueType=True; fields = [x@16, y@20, z@24, Zero@16, One@32, NegativeOne@48,
  AxisX@64, AxisY@80, AxisZ@96, Min@112, Max@128]
```

`via.vec3` **is** a value type. `x/y/z` are its instance fields, offset from a **boxed** base
(payload at +16 → payload-relative 0/4/8, which is exactly what `FieldOutBuffer.SetComponent`'s
proven ray-caster writes). `Zero/One/AxisX/AxisY/AxisZ/Min/Max` are **static** members — their
offsets point into the statics area and are not instance layout.

**That also completes the crash autopsy.** The old addressed write put the floats at
`arrAddr+0x10..0x1B` — **inside the array's own header**, where the 16:04 dump showed a type
pointer and length fields (`[c0 35 59 4d 01 00 00 00 | 01 00 00 00 | 01 00 00 00]`). Two presses of
floats over the type pointer and length, then the engine walked the array — three faults in one
millisecond. The write was never "one press late"; it was **twenty-four findings' worth of aimed at
the wrong sixteen bytes**.

**The new verification: read the array's own bytes.** The store is now
`set_Item(0, buffer.View)` — on the x64 ABI a by-value struct argument larger than eight bytes is
passed AS A POINTER, so the buffer's address in the argument slot is exactly what the engine's own
`set_Item` expects; it copies the bytes into the element's slot itself, wherever that is. Then the
array object's first 64 bytes are dumped **before and after** the call and the after-dump is
scanned for the requested x/y/z triple:

- **triple found inline** → the engine's own code put it there; success, and the offset is logged
  (the engine chose it — no header size or data offset is assumed anywhere);
- **the buffer's ADDRESS found** → the array holds a pointer where a copy was expected; a GC scan
  of that slot as an object reference is the 16:05 crash again — refused with the evidence;
- **neither** → the call neither threw nor stored anything visible — refused with both dumps.

The element buffer is now held for the **life of the process** (like the array): if the engine ever
stores its address, disposing it would be a dangling pointer under a live array — one 32-byte
unmanaged block per process is not a leak.

**Deployed as `c4a1a48a`.** What the next run answers, finally and directly: whether the engine's
own `set_Item` copies the point into the array (and at what offset), and then — after weeks of
proxies — whether `StartNavi` walks toward it, with `cos=` as the verdict.

## The twenty-third finding: get_Item returns null on a fresh array — the element must be STORED, not read

**Symptom.** *"已测试，全部目标都提示无法自动行走。"* Every press refused with
"无法自动行走" — and the log shows the array build dying at the same step every time:

```
route array created and globalized (@0x20066C43EE0, refs=1); element count=1, element type=via.vec3 (16 bytes)
get_Item(0) returned no element object — the route array is empty or its element type is not what RoutePos declares
```

**What this proves.** The twenty-second finding's fix worked: the array is now created from the
request's own `RoutePos` metadata and its element type is `via.vec3` — no longer the jagged
`via.vec3[][]`. But `get_Item(0)` returns **null** on that fresh array, and a genuine value-type
array can never return null: a `via.vec3[]` made of structs hands back element 0 immediately. So
the element is reached **as a reference** — `via.vec3` on this engine is a value type whose array
storage still surfaces through a level of indirection (consistent with `ValueTypeSize=16` and the
16-byte element), and an unset slot reads back as nothing.

**The consequence for the code.** Reading the element first is impossible — there is nothing there
yet. The fill must be a **store**: build the `via.vec3` in a `FieldOutBuffer` (the exact shape
`AutoWalk.Order` has always used to hand this engine a vec3 — a by-value struct argument is a
pointer to its bytes), and call **`set_Item(0, buffer.View)`** on the now-correctly-typed array.
The only reason `set_Item` ever threw was the jagged type at 16:04; with the array resolved from
`RoutePos`'s own metadata, `set_Item` belongs to `via.vec3[]`.

**The store is then tested by the engine's own read**: `get_Item(0)` is called again. If the store
was real, the element must now come back; if it is still null, that is the engine saying the array
stored nothing this code can see, and the walk is refused rather than run on an unverified request.
Either outcome is decisive, and both are logged in those words.

**Deployed as `e763f667`.**

## The twenty-second finding: the route array was the wrong type, and that crashed the game

**Symptom.** *"他确实说方向反了，然后我再次按F12，然后闪退了。"* The reversal was reported correctly
(`cos = -0.956`, encoding flipped to `Offset` as designed) — and the next press crashed the game.

**The engine named its own culprit.** At 16:04:59.321, in the same millisecond as the crash:

```
Exception thrown in REMethodDefinition::invoke for via.vec3[][].set_Item
0
No fix necessary
```

`via.vec3[][]` — **two** `[]`. `VEC3_ARRAY_TYPE` was the string `"via.vec3[]"`, and the TDB lookup for
it returned a **jagged** array type. So `CreateManagedArray(1)` built an array *whose element is
itself an array reference*, and every write into "element 0" went into an object pointer. The second
half of the route failed too, in the same frame:

```
get_Item(0) threw InvalidCastException: Unable to cast object of type 'REFrameworkNET.ManagedObject'
  to type 'System.IConvertible'
```

`get_Item` returns a **`ManagedObject`**, not an address — so `Convert.ToUInt64` on it could never
have worked, and the `ulong elemAddr` this code has been computing from it was never an address.

**The 16-byte tell was in the log the whole time.** The element dump read:

```
element bytes [c0 35 59 4d 01 00 00 00 01 00 00 00 01 00 00 00]   (16 bytes)
```

`c0 35 59 4d 01 00 00 00` is a live **object pointer** (`0x14D5935C0`); the two following words are
tags. And the binding line had already reported **`vec3 ValueTypeSize=16`** — this engine declares
`via.vec3` as 16 bytes, not 12, which is exactly why a 16-byte element *looked* like a `via.vec3`
and nobody challenged it. Writing three floats at `object+0x10` then overwrote the header of a real
managed object, and the engine crashed the moment it walked the route: three faults in one
millisecond — `set_Item`, `ValueType.GetHashCode`, `IEnumerable<uint>[].set_Item`.

**The old success line was true and worthless at once.** *"three floats read back from that address
(containment inside the array … element 16 bytes)"* — the read-back confirmed this code's own write
to memory the engine never reads as a coordinate. **A read-back through the same wrong address is
not verification; it is a mirror.**

**The reverse-engineering that ends this line of attack.** Decoding the *second* element dump proves
the plain addressed write was landing correctly all along:

```
bf d7 79 c3  →  -249.843      (the x asked on the previous press)
03 d3 2c 3e  →     0.169
1b d5 cb c2  →  -101.916
01 00 00 00  →  tag
```

So the float layout at `elemAddr+16/20/24` is right **when `elemAddr` is a `via.vec3`'s base** — it
is landing in the wrong *object*, not at the wrong offset. The crash came from the jagged-array
`set_Item`, and the wrong-sign walk came from a value the engine then read out of whatever that
pointer led to.

**The fix: stop naming the type in a string, and stop computing an element address by hand.**

1. The array type comes from the request's **own `RoutePos` field metadata** (`_naviTd.FindField("RoutePos").Type`),
   and the element type from that array type's own `GetElementType()` — which reports a jagged array
   as a jagged array. If the element is not `via.vec3`, the code **refuses instead of writing**.
2. The element is obtained as the **`ManagedObject` that `get_Item(0)` returns**, and `x/y/z` are
   written at **that object's own base** through its own field metadata. No array header, no element
   size, no inferred offset is involved anywhere.
3. The read-back calls `get_Item` **again** and requires the same address, so it cannot merely echo
   the object that was written to.
4. `TryIndexerWrite`, `ARRAY_DATA_OFFSET` (`0x10`) and the `"via.vec3[]"` constant are **deleted**,
   each with a comment saying why it must not come back.

**Deployed as `bf6d21ee`.**

**What the next run must confirm** (the log now prints all of it): the `RoutePos` declaration and its
element type, the element type's field list with offsets and `ValueTypeSize`, and either
`route element 0 written at 0x… (element object's own base)` or one of the new refusals. If the
element type turns out **not** to be `via.vec3`, that is the answer — and it is a far better answer
than another crash.

## The nineteenth finding: the avatar walks the right route in the wrong sign

**Symptom.** *"仍然顶墙，且继续背对目标方向，地面上面都如此。"* And with `AutoWalk` now standing
down (eighteenth finding), the movement is unambiguously `StartNavi`'s.

**Evidence — and this time the engine's own answer is the control.** Trial 15:43:36 recorded both:

```
walk point resolved to (-258.13, 0.17, -135.54) rather than the leg end (-244.04, 0.10, -121.56)
route probe — reachable; the engine's corridor has 2 point(s)
  Start=(-262.70, 0.00, -138.10)  Target=(-258.13, 0.17, -135.54)
  bend 1/2: (-260.14, …) from player 2.93 m flat, 6 o'clock
  bend 2/2: (-258.13, …) from player 5.24 m flat, 6 o'clock turn=0deg
VERDICT after 2500 ms — moved 4.86 m from (-262.70, -138.10) to (-267.02, -140.32)
```

The engine's corridor is `-262.70 → -260.14 → -258.13`: three collinear points marching in **+x**,
straight at the target. The avatar arrived at **-267.02**: it travelled the same line in **−x**.

**The geometry, computed rather than eyeballed:**

```
intended direction (dx, dz) = ( 4.57,  2.56)
actual   direction (dx, dz) = (-4.32, -2.22)
angle between them          = 177.9°          (180° is an exact reversal)
walk point reflected through the player = (-267.27, -140.66)
where the avatar actually ended         = (-267.02, -140.32)
distance between those two              = 0.42 m
```

**A walk that mirrors the destination through the player is the signature of a point/vector
confusion** — the array holds `P` and the engine consumes it as an offset (or the reverse), so the
result is `player − P` instead of `P`. Nothing about the destination selection is wrong; the number
is not being understood the way it was written. This is a different class of defect from every
finding before it: those were about whether a call could happen, this is about whether the value
survives the call intact.

**The fix is an experiment, not a belief.** `RouteValue` writes either the world co-ordinate
(`RouteEncoding.Point`) or the delta from the player (`RouteEncoding.Offset`). Both write cleanly and
both read back as what was written, so no amount of local verification can separate them — only the
avatar's displacement can. The verdict is therefore **signed**: it computes `cos` between the
requested bearing and the travelled one, and

- `cos < −0.5` → the avatar walked the line backwards → **switch the encoding automatically** and
  tell the player to press again, so one more press settles it;
- `cos > 0.5` and moved → the engine is walking the avatar, toward the target;
- did not move at all → the other failure entirely, with its own sentence.

The three outcomes never share wording, because they have three different fixes.

**The other state, for the record** (trial 15:44:58): `moved 0.00 m … the engine accepted StartNavi
and did NOT move the avatar`. So the trial now distinguishes **ignored**, **reversed**, and
**working** — and the 15:43 vs 15:44 runs show the first two are both real and both reproducible.
A `via.vec3` layout dump (published x/y/z offsets plus the element's first 16 bytes in hex) goes in
alongside, so the field-offset question can be read off directly rather than inferred from behaviour
a hundred metres away.

**Deployed as `4f34ffb693a1f25bddd36cf6a8aeb5cb`.**

## The eighteenth finding: two drivers, one avatar — and why nothing before it was measurable

**Symptom.** *"已测试，仍然顶墙，即使目标实际在身后。"* A walk that pushes forward while the target is
**behind** the player cannot be a pathfinding problem. It means the thing moving the avatar is not the
thing being tested.

**Evidence.** Trial 15:35–15:36:

```
15:35:13.932  Auto walk: trying semantics ABSOLUTE point (1/3); destination=(-248.54, 0.17, -108.57)
15:35:13.940  Auto walk: started (player input cancels it)
15:35:17.162  Navi trial: StartNavi(priority=4 (MenuUI), req) issued
15:35:19.476  Auto walk: STRAIGHT-LINE route in use; to=(-248.57, 0.17, -108.56)
15:36:05      Auto walk: STRAIGHT-LINE route in use; to=(-252.95, 0.00, -84.51)   ← player at -61.26
```

`AutoWalk` was walking **before** the trial started and kept walking long after. It is not a helper of
the trial — it is a **second, independent driver** that registers its own input controller and writes
its own vector every frame, and the trial was layered on top of it.

**Why this explains every symptom at once.** One driver pulling one way and another pulling another is
what "顶墙" looks like from outside. `AutoWalk` was holding a **stale leg end from an earlier target**
(`to=(-248.57, -108.56)` while the player had walked to `-61.26`), which is why it marched forward with
the goal behind him. It has no notion of turning, which is why nothing ever turned. And it is a third
answer to "which way is the objective", different from both the radar and `StartNavi` — the
long-standing *"走的方向和雷达指引的方向完全不同"*.

**The consequence that matters more than the bug.** Every previous observation about `StartNavi` —
the 47-second walk, the partial walks, the wall-pressing — was **two drivers superimposed**, and
therefore **unattributable**. No conclusion about whether `StartNavi` works could be drawn from any of
them. The request-content work of the thirteenth through seventeenth findings (the `via.vec3` write,
the reachability gate, aim-vs-leg-end, the address containment check) is all still correct and all
still **untested**, because the avatar was never being moved by the thing under test.

**The fix.** `AutoWalk.Drive` yields completely while `EngineIsDriving` is set: it issues nothing,
holds no controller, and says so in the log. That flag also feeds `LooksLikePlayerMovement`, so the
handover cannot be misread as a player takeover.

**And the flag now has exactly one writer.** It used to be refreshed every frame in
`NaviTrialHooks.Tick` from `NaviTrial.Active` — but both that hook and the beacon are
`LateUpdate.Post`, and **the order between two plugins is not guaranteed**, so the flag was read a
frame late. One frame is long enough for `AutoWalk` to write a straight-line order into a controller
while the engine's pathfinder is already walking the same avatar. It is now set in `NaviTrial.TryWalk`
at the instant `StartNavi` is issued, and cleared unconditionally in `NaviTrial.End`.

**The verdict mechanism.** The trial's only evidence of success used to be that the call returned — and
every actuator in this feature's history (`SetChaseWalk`, `SetSlaveMoveVec`) was accepted and ignored,
so that was never evidence of anything. `NaviTrial.MeasureWalk` now measures the avatar's own
displacement 2.5 s after the request and speaks one of two sentences: the engine is moving you, or the
engine accepted the request and did not move the avatar. With `AutoWalk` standing down, any movement
measured belongs to `StartNavi` and nothing else, which makes the reading unambiguous in a way it has
never been.

**Deployed as `33e841b69849f5d70a3d6363354f53bd`.**

## The seventeenth finding: the homing cue is not a destination, and a ladder foot is not a route

**Symptom, in the player's words.** *"仍然顶墙，无论上面还是下面的目标……在地面目标导航的时候他
直接从目标身边走过而不停留……而且走的方向和雷达指引的方向完全不同，这个问题以前也有。"* Four
observations, and all four have one cause.

**Evidence.** Trial 15:18–15:20, and the decisive pair:

```
15:19:52  word=任务目标  aim=(-229.26, 13.97, -104.32)  ladder=False  noRoute=True   searching=True
15:20:04  word=梯子      aim=(-234.64,  0.10,  -68.25)  ladder=True   noRoute=False
```

The word changed from *mission objective* to *ladder*. The aim moved from the objective (14 m up) to
a ladder foot on the ground. And `noRoute` **flipped from True to False**, which is why the gate
opened. The ladder search had already rejected that ladder's top:

```
Level route: no full route after 49 questions (49 queries) over 7 ladders
  #3 low (-234.6, 0.1, -68.2) [target no, player yes]
```

`[target no, player yes]` — its **bottom is reachable, its top is not**. So the walk was sent at a
ladder foot with no climb to perform: *"搜完路线再按F12就一直走直到顶墙"*.

The other half, on a flat target:

```
15:18:21  aim=(-248.90, 0.17, -103.16)   walk target=(-252.96, 0.00, -84.50)
15:19:03  aim=(-253.38, 0.17,  -69.03)   walk target=(-252.95, 0.00, -84.51)
```

`aim` slid **34 m** while the walk target stood still. `aim` is the beacon's *progressive homing cue* —
deliberately pushed off the objective so the sound is not pinned inside it. Feeding it to `StartNavi`
as a destination means the avatar reaches the cue, the beacon advances the cue, and the avatar walks
THROUGH the objective and out the other side. That is *"从目标身边走过而不停留"*, and it is also,
exactly, *"走的方向和雷达指引的方向完全不同"* — the radar points at the objective, the walk points at
the cue. One cause, three symptoms; the fourth (it never turns around, it just presses on) is the
absence of any refusal, because nothing had decided the destination was a problem.

**The fix — `BeaconDestination.TryStep`.** The gate previously asked *"does the engine say there is a
route?"*; it needed to ask *"is there a point worth walking to, and where is it?"* Those are different
questions, and only the second has an answer usable by a walk:

| Leg state | Walk point | Why |
|---|---|---|
| `ViaRoute` | `X/Y/Z` (the bend) | it is already a walkable point on the way to the leg end |
| `Ladder` | re-derived from the player's position | `Target` is a ladder foot whose top the planner rejected; never walk at it |
| `AtFoot` / `Closest` / `IsApproach` | refuse | every walkable metre is used; the rest is a climb or the player's own search |
| searching | refuse, out loud | no aim exists yet |

`TryStep` reuses `RouteTo`'s query cache rather than issuing a competing query, so it does not race the
pathfinder's rate limiter — the mistake that made the 15:08 gate a no-op.

**The false success I wrote (fourteenth form of the meta-failure).** Route 3 of `WriteArrayElement0`
writes at an **inferred** address (`object+0x10`) and then read the three floats back from it — which
of course matched, because those were the floats just written there. The log said *"read back and
confirmed"* while nothing had confirmed **that the address was inside the array**. It was reporting
success about a place the engine may never read. This is the fourth instance of the pattern this
document tracks (`GetUnderlyingType` for enums; `FieldOutBuffer.Component` returning `0f` for a missing
field; the `via.vec3` ctor dispatching into `ValueTuple`), and this time the author was the mod's own
new code.

The fix is the one check that separates a real write from a plausible-looking one: **containment.** The
array's `Length` and the element's `ValueTypeSize` give the data range; the target address must lie
inside it. `Length` is read by name (`get_Length` — this runtime publishes no `GetArrayLength`). When
either number is unreadable the containment is reported as `UNKNOWN` rather than assumed either way,
and the log now states only what was verified:

```
route element 0 written at 0x… — x=… y=… z=… (three floats read back from that address;
containment inside the array (data 0x…..0x…, length 1, element 16 bytes))
```

**Wording.** Every refusal goes through one method, `AutoWalkText.CannotWalkThere`, which branches by
*what the player should do* — still searching / a ladder is the way but its top does not reach / plainly
no route — because a blind player cannot see which of those happened. Three reasons, three sentences;
merging them would hide which one is about the objective.

**Deployed as `c0300461bb397a0266325e0f9dcbdb0e`.**

## The sixteenth finding: the gate asked its own question, and the question always lost

**Symptom.** After the fifteenth finding's gate was deployed, the player reported that *flat,
ground-level* targets also pressed into walls — "他走了，但平面或者上面的目标他都在顶墙". A gate that
only misbehaves on unreachable targets had to be a target-classification problem. A gate that
misbehaves on *every* target is not classifying anything at all.

**Evidence.** Trial 15:08, four presses of F12, four identical lines:

```
route probe — no answer (rate limited, or the player is off the navmesh);
not treating that as blocked, the walk proceeds and stuck-detection is the backstop
```

Four times out of four the gate obtained nothing. In the same window the beacon's own queries ran
freely (`15:08:45.169 … 15:09:07.723`), and a later attempt at `15:10:08.645` **did** get through and
returned `PathNotFound (startNode=13723, destNode=10421)` — proof that the query mechanism works and
that the destination really is unreachable. The gate simply never managed to ask.

**Root cause — mine, and it is a design error, not an API error.** `FieldNavPath` permits **one**
engine path query per **250 ms**, because `queryPathSync` is a blocking, city-wide A\*. The beacon
spends that budget continuously at roughly one ping per 1.3 s. The gate ran **milliseconds after
`BeaconDestination.Resolve()`**, which had just run the identical query for the identical target. So
the gate's query collided with the rate limiter essentially every time, returned `default`, and —
because `Unknown` deliberately proceeds — the gate permitted every walk it was built to refuse. It
was a no-op wearing the shape of a safety check.

**The answer was already in hand, and the code that made it said so.** `BeaconDestination.cs`
carries the note *"A second reader in the same interval can reuse the same successful query"* and a
small query cache (`_query` / `_cachedAt` / `_cached`) built for exactly this. `BeaconDestination`
also already computes the verdict the gate needs — `NoRoute = answer.Ok && !answer.Exists &&
!answer.StartOffMesh`, plus `Holding`, plus `AboveM` — and `MapAimHooks.cs` already refuses to march
a player at a rooftop on `dest.NoRoute`. The gate was re-deriving, one rate-limit window too late,
something that had already been computed and then thrown away.

**The fix.** `NaviTrial.TryWalk` takes the resolved destination and reads it:

```csharp
internal static void TryWalk(ManagedObject avatar, float x, float y, float z, in BeaconDestination dest)
```

The gate keys on `dest.NoRoute` (and `dest.Holding`/`dest.Searching` for the ladder-search case); it
never issues its own query. `ProbeRoute` is demoted to **log-only** and says in its own words that it
usually cannot answer — *"the bend list could not be read (rate limit collision with the beacon, or
no answer); the walk's permission came from the beacon's own cached query, not from here"* — and it
raises a `LogWarning` only when a fresh query **disagrees** with the beacon, because that
disagreement is the only evidence that one of the two is stale. `ResumeAfterObstacle` re-resolves
first: the obstacle disappearing is precisely a change in what is walkable.

**The lesson, and the pattern it completes.** *A diagnostic that usually cannot answer must never be
the thing that decides.* This is the fourth form of the same meta-failure this document has tracked
three times already — a mechanism that reports something other than the truth: `GetUnderlyingType()`
for enums (tenth finding), `FieldOutBuffer.Component` returning `0f` for a missing field, and the
`via.vec3` constructor selector dispatching into `ValueTuple` (thirteenth finding). Those three
reported **success while storing something wrong**. This one reported **nothing, and nothing was read
as permission**.

**Found and fixed in the same pass:** a duplicated code block left behind by an earlier edit
(an entire copy of the probe's tail) produced `CS1519` plus ~30 cascading syntax errors. Removed;
the build is clean (0 `error CS`, 0 `warning CS`, only the expected `MSB3021` copy-denial from the
running game). Staged and deployed as `11b29ff1e42cda2a3113d1b48d50b882`, `verify()` confirming
staged = deployed.

## The fifteenth finding: "pressing into a wall" is the engine saying no, not the steering failing

**The engine had already answered, and the walk asked anyway.** Trial 14:58 recorded, before the
walk started:

```
NavPath no route because: PathNotFound (startNode=10401, destNode=10421) — the destination IS a
valid navmesh node, but no walkable link reaches it from the player's node: it is on a
DISCONNECTED piece of mesh — an upper floor or roof entered through a door, lift or scripted
transition the navmesh does not model.
```

The objective was at `(-229.26, 12.18, -104.32)`; the player stood at `(-246.15, 0.10, -104.61)` —
**12.08 m apart vertically**. The destination was upstairs. There is no route, and the navmesh does
not model the way in.

**What "pressing into a wall" actually was.** The request carried `TargetType=Position` and one
co-ordinate. Given a destination with no route, the engine has nothing to follow and walks AT it —
and the first thing between the avatar and a point twelve metres up a building is a wall. The
avatar pressed against it, which is the correct behaviour for a straight-line request aimed at an
unreachable point. **No steering algorithm was ever going to fix this, and the fourteenth finding's
plan — fill `RoutePos` with the route's corners — was unusable here for the plainest possible
reason: the engine returned `points=0` because there is no route to read corners from.**

**The mod already knew, twice over, and nothing was listening.**

| System | What it said at the same moment |
|---|---|
| Terrain probe, every frame | `Cue BLOCKED beam=Left clear=0.77 m speed=0.00 m/s`, spoken as **"高墙"** |
| Ladder routing, already working | **"没有梯子能直达目的地，但有2个梯子能让你更靠近。第一个在30米外"** |
| `StartNavi` | walked into the wall |

The engine's verdict, the wall reading and the alternative route were all present. **The only thing
missing was anything that acted on them.**

**`FieldNavPathDiagnostics` predicted this exact failure and was right.** Its remarks on
`DestNodeNotFound` versus `PathNotFound` say it in as many words: *"The first is our problem to
solve; the second is the game's answer and must be respected. Guessing between them is how a beacon
ends up marching a blind player into a wall."* That distinction was written down before this bug
existed and is now the gate's whole logic.

**The fix is a gate, and its asymmetry is the design.** `FieldNavPath.ReadRouteTo` now returns a
`Reachability` alongside the corners, classified from the same `Diagnose` verdict the log already
prints — so the two cannot drift apart. `NaviTrial.TryWalk` consults it before building a request:

| Answer | What the walk does | Why |
|---|---|---|
| `Reachable` | proceeds | the engine found a route |
| `Unreachable` (`PathNotFound`) | **does not start**; says the destination has no walkable way, and how far above it is | the game has answered; steering cannot reach a place the mesh does not connect |
| `OffMesh` (`DestNodeNotFound`) | **does not start**; different wording | not a place one can stand at all, which is a different fact |
| `PlayerOffMesh` (`StartNodeNotFound`) | **does not start**; says to stand still and retry | the player is mid-jump or on a ladder; nothing routes until they land |
| `Unknown` — no answer obtained | **proceeds** | a rate limit or a momentary off-mesh reading must not silently disable the feature |

The last row is the deliberate asymmetry: **a wrong "keep going" is recoverable by the player, a
wrong "stop" is the avatar standing still with no explanation.** `ObstacleHold` remains the backstop
for any blockage this gate cannot see — a breakable prop on a reachable route, which is a different
situation from an unreachable destination and must not be given the same message.

**The wording carries the diagnosis.** Each answer gets its own spoken line, because collapsing them
would hide which one is about the destination: "no walkable way from here, about twelve metres above
you — look for stairs or a ladder" is the fact that makes the situation legible to a blind player,
and it is the opposite of useful to answer it with "something is blocking the way".

## The fourteenth finding: `TargetType=Position` means "walk at that point", not "take me there"

**The walk works, and it walks into walls.** The owner's report after the thirteenth finding's fix
was *"角色能走路了但撞到墙以后不会转弯，而是一直在那走"* — it walks now, but against an obstacle it
keeps pushing instead of going round. The first half of that sentence is the end of a long road: the
avatar is finally moved BY the engine, along a request this mod built.

**Why the straight line was never a bug in the engine.** The request says:

```
TargetType = Position(1)      // RouteTargetType: _Invalid=0, Position=1, Object=2
RoutePos   = [ one point ]    // the destination, and nothing else
FindingActionType = GoFront(0)
```

`FindingActionType` has exactly two members, `GoFront` and `Stand`. **It is not a pathfinding
switch** — it chooses between walking and standing still. And `Position` hands the engine a target
CO-ORDINATE, not a destination to be reasoned about. So the engine did exactly what it was asked:
head for the point. With one point there is no second route, and nothing in the request asks for
one, so a wall is simply something to press against. **The mod was asking for a straight line and
blaming the engine for producing one.**

**The value that does mean "take me there" is `Object(2)`** — and dump block 8 already called this
struct "the last route: real pathfinding". But `Object` requires `RouteObject : via.GameObject[]`,
and nothing in this codebase currently holds a `GameObject` for a destination: `BeaconDestination`
carries co-ordinates, a word and a key, and that is all. Reaching `Object` means growing the whole
destination chain a reference it does not have, which is a large change with its own failure modes.

**Meanwhile the route data was already in the building.** `FieldNavPath` — the service the homing
cue uses to answer "can I walk there, and where does it first bend?" — queries the engine's own
navmesh through `queryPathSync` and reads the resulting corridor with `getPathPointInfo(i)`. Its
`PathAnswer` already carries `HasCorner`, `CornerX/Y/Z` and `Points`: **the engine's own answer to
"where does the path turn?"**. The route to a destination round a corner exists, is read every
beacon ping, and is thrown away except for its first bend.

**What `AimPoint` was discarding.** It walks the whole corridor but feeds each point to
`RouteLookAhead`, whose contract is to stop at the first bend wider than 15° and return the point
just before it — correct for a homing cue, which needs ONE thing to point at, and lossy for a route,
which needs every bend. The new `FieldNavPath.ReadRoute` reads the same list with the same
`IsSkiped` filter and the same 64-point bound, and **keeps all of it**. So the two readers cannot
disagree about which points exist; they only disagree about how many are useful.

**The plan, and why it is two steps.** The corners are now logged by `NaviTrial.ProbeRoute` on every
F12 press **without changing what is sent to the engine** — the request still carries its single
point. The two questions being answered are different ones:

| Question | How it is answered | Why it must not be mixed with the other |
|---|---|---|
| Is the bend list correct — does it really step around the wall? | read the log | a wrong list filled into `RoutePos` produces a wrong walk with no way to tell which half was at fault |
| Does the engine honour a hand-supplied multi-point route? | walk it, and measure displacement | a promising-looking walk proves nothing about the data if the data was never checked |

The project has paid for confusing those two three times already (findings ten, twelve and thirteen),
and each time the log claimed success for something that had not happened. The probe log is written
to make its own verdict falsifiable: it states the point count, the bearing change at each bend, and
**whether the last bend actually lands within 2.5 m of the destination** — a list that stops short of
the target is marked as unusable rather than silently passed on.

**A log that disagreed with its own code.** The line announcing the call said
`StartNavi(FieldEvent, req)` on every single run, because the word was written into the string —
while the call itself carried `MenuUI(4)`, as the bind line two lines above correctly reported. No
behaviour was affected, but a log that contradicts the code sends the next reader to the wrong place,
which is the same failure as a success line for a failed write. It now prints the value that was
actually passed, with its enum name read from the TDB.

## The thirteenth finding: a method belongs to the type its `DeclaringType` names, and nothing else

**What the log said.** Re-reading trial 4's transcript closely turned up an error that had been
there in five of the six attempts and that the twelfth finding had walked straight past:

```
VMContext: Caught exception code c0000005
Exception thrown in REMethodDefinition::invoke for via.vec3..ctor
[TDB: System.ValueTuple`3<System.Single,System.Single,System.Single>..ctor]
```

REFramework dispatched `via.vec3`'s constructor into `ValueTuple<float,float,float>`'s. Nine
attempts, nine exceptions, zero successes. And immediately after each one this line was printed:

```
Navi trial: route element 0 written via set_Item(int, via.vec3) — x=… y=… z=…
```

**A success line, every time, for a write that never happened.**

**Why the selector was ambiguous.** `TryIndexerWrite` chose the constructor by parameter shape:

```csharp
if (c.Name != ".ctor") continue;
if (ps.Count == 3 && ps.All(p => p.Type?.FullName == "System.Single"))
{ ctor = c; break; }
```

"Three floats" is not a unique signature. `via.vec3` publishes seven constructors, and the engine
has other three-float constructors besides — `ValueTuple<float,float,float>` is one, and it is the
one the call actually reached. A parameter list can always be imitated; **ownership cannot.**

**Two rules, both now enforced in code:**

1. **A method belongs to the type its own `DeclaringType` names.** `TryIndexerWrite` now rejects any
   three-`Single` `.ctor` whose `DeclaringType.FullName` is not `via.vec3`, and says so in the log
   rather than silently picking the first candidate. (`Method.DeclaringType` is already the tool
   `FieldNavRadarService` and `FieldRayProbe` use to identify methods.)

2. **A constructor call is not a write until its result has been read back.** The new `VerifyVec3`
   reads the three floats back out of whatever `InvokeBoxed` returned and compares them with what
   was asked for. Rule 1 would not have caught the failure by itself in every case — the
   constructor reached *a* real method, and a build where the TDB's method list is complete but
   mislabelled would pass rule 1 while still constructing the wrong value.

**The third instance of the same failure mode in one session.** The mod has now been bitten three
times by a mechanism that reports success while storing something wrong:

| Where | Shape of the failure |
|---|---|
| `GetUnderlyingType()` for enum fields (twelfth finding's predecessor) | metadata returned a placeholder (`Int64`) that looked like an answer |
| The `FieldOutBuffer` route's `Component()`/`SetComponent()` | a missing field reads as `0f` and a failed write returns a bool nobody checked |
| `TryIndexerWrite`'s ctor selector (this finding) | a wrong method constructs garbage and returns normally |

The common thread is not carelessness about offsets — it is **trusting a return value that carries
no evidence**. `WriteArrayElement0`'s three routes now each read their own result back before
returning `true`, including a check that the element buffer actually holds the numbers that were
put into it, because `SetComponent` returning `false` and `Component` returning `0f` are
indistinguishable from success when three zeros are a legal coordinate.

**What this changes about the twelfth finding.** The avatar moving at all was real — `StartNavi`
bound, the priority handover worked, five enum fields landed with the boxed types the engine itself
named. But the route point handed to it was **undefined data**, which is a second and sufficient
explanation for "moves two steps then stops" that exists independently of the 54 ms takeover
cancel. Both bugs were live in the same run. The twelfth finding's fix is still correct and still
needed; this finding says it was not the only thing wrong.

## The twelfth finding: `StartNavi` walks — and the takeover rule cancelled it after 54 ms

**The engine's own pathfinder works.** Trial 4's log, verbatim:

```
Navi trial: StartNavi(FieldEvent, req) issued; player before=(-254.62, 0.11, -115.33) ok=True.
Navi trial ended (player took over).          <-- 54 milliseconds later
```

The owner's report is the whole diagnosis in one line: *"这次角色动了，但每次都只动两步就停了"* —
it moved, and it stopped after two steps. **Those two steps were the first time anything this mod
did moved the avatar.**

**The cause was this codebase's own safety rule, turned against itself.** `PlayerSpeedThresholdMps`
(0.8 m/s) exists so the player's claim on their own avatar is absolute, and its remarks say the
rule in as many words: *"anything at this speed that is not ours is the player"*. **The clause
"that is not ours" used to be free.** While the only actuators were the ones the engine ignored,
the avatar never moved because of anything the mod did, so a speed reading above the threshold
really was the player and the test could be taken at face value. `StartNavi` broke that premise
without touching the rule: the engine now moves the avatar at 2–4 m/s — the same reading as a
player pushing the stick — so the instant a walk began, the walk was cancelled as a takeover.
**Speed is a consequence; the rule needed the cause.**

**Why the obvious fix is closed.** Reading the player's raw stick would settle it, and this
codebase already tried that: `AvatarBase.GetMoveVec()` returns a **reference type**, and calling it
on 2026-09-29 left the game in `VMContext: Caught exception code c0000005` with the avatar unable
to move at all — flagged by the player immediately ("I can only turn the camera, not move the
character"). `AutoWalk`'s remarks carry the resulting rule: *no engine call that is not a plain
by-value read or a by-value actuator call*. A second attempt at the same idea is not on the table.

**The fix asks who holds the input slot.** While the engine walks for us, the slot carries the
priority this mod requested (`MenuUI`); the game hands it back to the player's own `Default`
controller the moment the player claims it. `NaviTrial.PlayerHasTheController` reads
`GetCurrentInputControllerPriority` — an **enum-returning by-value call**, used safely since probe
block 4 — and reports takeover when the priority has dropped below what was asked for. This is not
a new mechanism: it is the same fact the mod already relies on from the other direction, that a
registered controller owns the input slot (learned on 2026-09-29 when a leftover one cost the player
the controls). It fails towards "keep walking", because the opposite failure would cancel every
walk on the first flicker of an unreadable value, and the player can always press the key again.

**The same bug was in the auto-walk path.** `MissionBeaconHooks` and `AutoWalk.Drive` used the bare
speed test too. Both now call one shared predicate, `AutoWalk.LooksLikePlayerMovement`, which pairs
the threshold with `AutoWalk.EngineIsDriving` — a flag the driver itself publishes, so the question
"is this movement mine or the player's?" has exactly one answer in the codebase.

**Obstacle handling is built but deliberately not wired.** `ObstacleHold` (the eleventh finding) is
complete and compiled; its call site in `NaviTrialHooks.Tick` is commented out with instructions.
Reason: it was written in the same session that first made the walk work, and a build that tries to
prove both at once cannot say which one failed. The walk goes first.

## The eleventh finding: "the world is blocking me" is a third outcome, and it has its own handling

The owner raised this before any of it was tested, and it is the sharpest question asked about the
feature so far:

> 假设要到达目标点需要破坏某个障碍物的话，导航怎么走都是过不去的 … 能否先知道哪个是需要破坏的
> 障碍然后把角色导航到障碍物前面，玩家破坏障碍以后再重新开启导航

**Navigation has three outcomes and the mod only handled two.** Walked; blocked by the player
(handled by takeover); and — the gap — **blocked by the world**: navigation live, avatar not
moving, player not touching anything. A sighted player sees the crate. A blind player sees nothing
and concludes the mod is broken.

`ObstacleHold` closes it. The shape of the answer is the owner's own: detect the block, name the
obstruction and its direction, stop, wait for it to be broken, then ask for the same destination
again.

**Most of the pieces already existed**, which is why this ended up being a small change:

| need | already there |
|---|---|
| find intact breakables, and notice when one is gone | `BreakableCatalog` (rescans on a slow timer; a broken prop simply disappears) |
| the clock-hour wording the player already knows | `FieldDirectionService.ClockHour` (same geometry as the radar and the npc beacon) |
| the warning that this situation exists | `wt.route.blocked_ladder` / `wt.route.closest_level` already say "look for something to break with a master action" |

**Why the block is detected by speed, not by asking the engine.** Asking which obstacle closed the
path would need the engine to expose a path the mod can read, which no probe has shown it does.
Speed needs nothing from the engine and measures the thing the player actually cares about —
*am I getting closer or not?* It also cannot be fooled by a path that exists on paper but not in
practice, which is precisely the case `LevelRoutePlanner` already warns about ("a breakable obstacle
the navmesh does not model may close the rest").

**The resume signal is the catalog's own omission.** There is no "has it been broken yet" flag
anywhere in `ObstacleHold`: the journey resumes when no intact breakable is near the avatar any
more. One source of truth, nothing to keep in sync, nothing that can go stale.

**What it refuses to do.** It does not invent a culprit. If the avatar is stuck and no breakable is
within range, it says so instead of naming an object — a wrong cue is worse than a vague one for a
player who cannot check. Same principle as `BreakableCatalog`'s "a beacon over rubble is worse than
no beacon".

**Two implementation traps worth remembering.**

1. **A hold clears `Active`, so a check nested inside `if (Active)` would never run again** and the
   journey could never resume. The obstacle check in `NaviTrialHooks.Tick` therefore runs outside
   that block, guarded by `_held || NaviTrial.Active`. Caught while writing it; the comment in the
   code says why.
2. **A hold must NOT call `End`**, because `End` calls `ForceEndNavi` — which tells the engine to
   forget the navigation entirely. Right for a takeover or a finished walk, wrong here: the whole
   point of an obstacle hold is that the same journey continues. `SuspendForObstacle` clears the
   mod's own flag and leaves the engine's request alone.

**Still unproven**: whether the engine actually *stops* when the mod stops asking. The mod is not
what moves the avatar — the engine is — so `SuspendForObstacle`'s remarks say plainly that it stops
*asking*, and that the trial is what measures the rest.

## The tenth finding: an engine cast error is a complete specification, and `GetUnderlyingType` is not

Trial 2 reached `StartNavi` with a request whose five enum fields were **all still zero**. The log
said why, five times over, once per field:

```
TargetType        write failed: Unable to cast object of type 'System.Int64' to type 'System.Byte'.
FindingActionType write failed: Unable to cast object of type 'System.Int64' to type 'System.Int32'.
LoopType          write failed: Unable to cast object of type 'System.Int64' to type 'System.Byte'.
MoveType          write failed: Unable to cast object of type 'System.Int64' to type 'System.Byte'.
EndAction         write failed: Unable to cast object of type 'System.Int64' to type 'System.Byte'.
```

Each write was boxed as `Int64` because `Field.Type.GetUnderlyingType()` reported
`System.Int64` for all five. **It was wrong about all five.** `TypeDefinition.IsEnum()` is the only
enum-related member REFrameworkNET publishes; there is no enum-aware boxing helper anywhere on the
surface, so the conversion belongs to the engine, and the engine's own refusal is a *better*
statement of what it accepts than the metadata is. The five boxed types were taken from those five
messages verbatim, and the field offsets corroborate them — `LoopType` (+0x29) and `MoveType`
(+0x2A) are one byte apart, so neither can be four bytes wide.

**The failure was silent in the worst way.** `TargetType` stayed at `_Invalid(0)`, which is a
perfectly legal "no destination". The engine accepted the request, the call went through, the mod
said "auto walk started", and nothing moved. A half-filled request is *worse* than no request,
because no request fails loudly. `TryWalk` now treats both the route write and all five enum writes
as hard preconditions and refuses to call `StartNavi` otherwise.

**The generalisable rules.**

1. When the engine performs a conversion, an exception naming both the type given and the type
   required is a complete specification. Read it as one; do not re-derive the same fact from
   metadata that may be a placeholder. `GetUnderlyingType` is a placeholder for these enums.
2. An enum whose value 0 means "none/not set" turns a failed write into a *valid* request for
   nothing. Any such field needs its write verified before the call, not after the silence.

## The ninth finding: the request is a live object, and the route array must be a live object too

Trial 1 was refused, correctly, by the mod's own guard:

```
no request buffer — app.worldtour.avatar.AvatarControllBase.NaviRequestData is a REFERENCE type,
so this out/ref parameter is an address the engine writes a pointer or a whole record THROUGH;
no caller buffer can be shown correct for it -> call SKIPPED
```

Block 8 had said `IsValueType=False IsEnum=False ParentType=System.Object` all along — the guard
caught a misreading of the type's identity, which is what it is for. The fork it enforces is the
whole mechanism:

| | value type | reference type |
|---|---|---|
| example here | `via.vec3` | `NaviRequestData`, `via.vec3[]` |
| mechanism | `FieldOutBuffer` (unmanaged, never moves) | `FieldProbeService.SharedInstance` / `CreateManagedArray`, then `Globalize()` |
| what the field holds | the value itself | an address the engine follows to the **live object** |

So **both** the request and the route array are now real, globalized engine objects, and fields are
written **by name** through `Field.SetDataBoxed(addr, value, isValueType: false)`. The hand-rolled
array header from the earlier attempt is gone, and with it the one number that was inferred rather
than read (`ARRAY_DATA_OFFSET = 0x10`): giving the engine a block that merely *resembles* an array
is not the same as giving it the object its own type system will walk.

**Guard the crash is still guarded.** `CreateValueType` remains forbidden for exactly the reason
`docs/sf6-architecture.md` has carried since 2026-09-04: it allocates a managed `byte[]` on the GC
heap and `ValueType.Ptr()` returns an address taken inside a `fixed` block that has *already been
released*, so a collection during the native call relocates the array while the engine keeps
writing; the buffer is also exactly `ValueTypeSize` with no slack. The symptom is the worst kind:
correct-looking data, then a silent death seconds later, nothing in the log. `FieldOutBuffer` is
the only buffer handed to native code, and it stays limited to the genuine value type.

## The seventh finding: the actuators were never the entry point — `NaviRequestData` is

Probe block 8, read in game 2026-09-29 13:15, returned the struct's full layout. The important
part is not its size (128 bytes, `ValueTypeSize` 112). It is this enum:

```
+0x2A MoveType : MoveActionType
        Walk = 0, Run = 1, ChaseRun = 2, ChaseWalk = 3,
        CrowdToRun_Low = 4, CrowdToRun_High = 5, CrowdStand = 6, Runaway = 7
```

`SetChaseWalk` and `SetSlaveMoveVec` are not the game's way of moving an avatar. They are helpers
that write into a request like this one. That is why both were accepted and ignored: **they filled
fields in a request that was never running.** A rate of 100 — a hundred times the player's own
measured 4.37 m/s — moved nothing, because nothing was consuming it. This also retires the "which
actuator is right?" question entirely: there is one entry point, `StartNavi`, and the walk/run/chase
distinction is a field value inside its request.

<p align="center"><b>And it cannot be hand-filled.</b></p>

Five of its fields are engine object references — `RouteObject : via.GameObject[]`,
`RoutePos : via.vec3[]`, `MoveArg : MoveActionArgBase`, `WTNpcAIComponents : app.worldtour.WTNpcAI`,
`OnNavigationFinish` (a delegate). `RoutePos` is the destination itself, so a request cannot be built
out of plain values. That is a hard stop under this project's rule, and it is why probe block 9 reads
the *shape* of the APIs needed to supply them — `CreateManagedArray`, `via.vec3`'s own
constructibility, and whether `MoveArg` may simply be left null — **without allocating anything**.
`CreateManagedArray` exists in this REFramework build but has never been used in this codebase, so
its behaviour (argument meaning, element initialisation, and crucially whether the result is a
per-thread LOCAL object the engine reclaims at end of frame) is unknown. Allocating one to observe it
would be handing the engine exactly the value the rule forbids. Block 9 answers it by reading, so the
next step is a decision.

**One more lead, for stage 3.** `AvatarInputController` carries
`SetCustomRouteExAction(int exActionId)` / `RemoveCustomRouteExAction()` — "custom route extended
action". That is very likely the slot through which mission scripts drive special traversal
(the around-the-obstacle and up-the-ladder moves the owner specifically asked for), and it is worth a
probe once plain walking works.

## The sixth finding: both actuators are accepted and ignored — the avatar is not refusing, it is not being asked

**The measurement that closes the argument.** In the fifth test cycle the collision
cues reported the player's *own* manual walking at `speed=4.37 m/s`, then `1.34`, then
`1.58`. The engine moves this avatar, and moves it well. In the same session:

| Actuator | Readings tried | Result |
|---|---|---|
| `AvatarInputController.SetChaseWalk` | 3 semantics | **all three closed exactly 0.0 m** |
| `AvatarPlayerControll.SetSlaveMoveVec` + `SetSlaveMoveRate` | rates 1 / 3 / 100 | **all three ≤ 0.2 m over 1500 ms** |

A rate of 100 is a hundred times the player's own measured speed. Nothing moved. So the
conclusion is not "the value is too small" or "the sign is inverted" — it is that **these
two methods do not drive this avatar at all.** They are accepted (no fault, no crash, no
exception) and then ignored, which is the same failure shape as the `GetMoveVec` crash
was: a method that exists on the type but is not on the code path the game uses for the
player's avatar.

**Therefore the guessing stops.** The remaining route is the game's own entry point,
`AvatarBase.StartNavi(PriorityType, AvatarControllBase.NaviRequestData)` with
`ForceEndNavi()`. It is strictly better than every vector approach tried so far, and for
exactly the reason the owner asked for this feature in the first place: it is
*pathfinding*, not a direction — it can round a corner and it can take a ladder. A
straight-line vector never can.

<p align="center"><b>It is also the route the mod cannot take blindly.</b></p>

`NaviRequestData` is a struct on `AvatarControllBase` with no published constructor in the
dump, so it has to be created and filled field by field — and `docs/sf6-architecture.md`
has one rule that outranks convenience: **a value the mod did not itself obtain must never
be handed to the engine.** A struct whose layout has been guessed is exactly that. So
probe block 8 dumps the layout first — size, every field with its `OffsetFromBase`, every
`.ctor`, every enum-typed field expanded to its members, and an explicit list of any field
that would make a hand-built request impossible (a pointer, a by-ref, or an engine object
reference). The build after that is a decision made on read data, not a guess.

## The fifth finding: the controller was leaking, and it cost the player the controls

Fourth in-game test, and it produced the worst bug of the series — worse than the crash,
because it was silent. The player's report: *"选中不能自动走的目标是可以接管的，后来我选了其他
的目标，他没有提示我不能自动走，但手柄不能接管了，并且角色没有自动走"* — the first target
behaved (takeover worked, the "cannot auto-walk here" line played), but **the second target
cost them the controller entirely.**

The log is unambiguous. Three controllers were created, but only two sets of
`trying semantics` lines exist (6 = 2 × 3):

```
12:46:52  input controller created at FieldEvent priority   -> walk 1, calibration ran
12:47:19  input controller created at FieldEvent priority   -> walk 2, calibration ran
12:47:42  input controller created at FieldEvent priority   -> walk 3, NOTHING AFTER IT
```

Walk 3 created a controller and then **never issued a single order — no `trying semantics`,
no calibration, no straight-line log.** And from 12:47:35 to the end of the session every
collision cue reads `speed=0.00 m/s` while still firing: the avatar was frozen with its
input slot held.

**Root cause — `EnsureController` ran before the checks that could abandon the walk.**
The order of operations in `Drive` was:

1. the takeover check — where `if (_active)` guarded the *spoken line*, not the teardown;
2. `EnsureController(...)` ← **a controller now exists**;
3. …and only then the arrival test, the calibration window and the `Order` call.

Any return between (2) and a successful `Order` left a controller registered at
`FieldEvent` priority with nobody driving it. And `_active` — the flag the takeover branch
tested — is only set at the very *end* of a successful frame, so it was still `false`; the
branch therefore did nothing at all: no teardown, no spoken line, no stop.

The game's controller registry is keyed **only by priority**, so a controller that exists
keeps winning the input slot whatever it does with it. That is the whole bug: the avatar
could not move because a dead controller outranked the player's own.

**Two fixes, both structural rather than defensive.**

1. **The controller is now created last** — after the destination choice, after the arrival
   test, after the calibration window, on the only path certain to issue an order this
   frame. An abandoned walk can no longer leave one behind, because it never made one.
2. **`Retire` was split into two different ideas.** `_retired` is a verdict on the *engine*
   ("this VM is broken, never touch it again this session"); failing to drive the avatar to
   one target is a verdict on *that target*. They used to be the same call, which meant the
   straight-line hand-off — which tears the chase controller down **on purpose**, because
   that controller is the thing that failed — also poisoned the session, so the next target
   could not walk either. Now `RetireController` (destroy, forget) and `Retire` (destroy,
   forget, **and** declare the session over) are separate, and `StopAndCancel` — which every
   stop path runs through — calls `ReleaseController` unconditionally. The destroy is a
   no-op when there is no controller, so it is always safe to call and never safe to skip.

**Takeover moved to every frame.** It was checked only inside `Resolve`, on a 500 ms grid.
For steering that is fine; for handing controls back it is not — a blind player who pushes
the stick and feels nothing for half a second pushes harder and concludes the game has
frozen. A per-frame check now runs in `Tick`, with its **own** position cache (two
observers, two windows; sharing one would shrink the window to a single frame and turn a
3 m/s walk into a spike). It can only ever *end* a walk, never start one, so it cannot fight
the driver. The threshold is published as `AutoWalk.TakeoverSpeedMps` so there is exactly
one definition of "the player is steering".

**`SetSlaveMoveVec`'s rate is now measured too.** The straight-line route was also accepted-
and-ignored: 21 s of orders, `speed=0.00 m/s` throughout. `SetSlaveMoveRate(float)`'s unit is
not published anywhere this mod can read, and `1f` (read as "one walk") did nothing, so the
rate is now a candidate list `{1f, 3f, 100f}` — if the unit is m/s then 3 is a walk, if it is
cm/s then 100 is — each given its own window and measured, winner kept for the session. When
every rate fails the route declares itself dead honestly: it clears the component, stops, and
speaks `wt.walk.unavailable`, rather than pulling forever.

---

## The fourth finding: `SetChaseWalk` is closed — three readings, all 0.0 m

The third test ran all three semantics candidates. Every one closed **exactly 0.0 m**,
against the same 4.63 m point:

```
trying semantics ABSOLUTE point (1/3); destination=(-248.54, 0.17, -108.57) player=(-246.15, 0.10, -104.61) dist=4.63 m
the ABSOLUTE point reading did not move the avatar (4.6 -> 4.6 m over 1484 ms, closed 0.0 m). Trying POINT then face next (2 of 3).
trying semantics POINT then face (2/3); destination=(-248.54, 0.17, -108.57) player=(-246.15, 0.10, -104.61) dist=4.63 m
the POINT then face reading did not move the avatar (4.6 -> 4.6 m over 1500 ms, closed 0.0 m). Trying RELATIVE vector next (3 of 3).
trying semantics RELATIVE vector (3/3); destination=(-248.54, 0.17, -108.57) player=(-246.15, 0.10, -104.61) dist=4.63 m
CALIBRATION FAILED for every reading of SetChaseWalk (ABSOLUTE point, POINT then face, RELATIVE vector) - the last closed 0.0 m over 1500 ms.
```

**Honesty requires two notes about this result — the second is a correction to what the
earlier sections of this document claimed.**

1. **The three readings are not a fair test of the method, and the log proves it.** All
   three were aimed at `(-248.54, 0.17, -108.57)` — the beacon's *aim* point, **4.63 m**
   away. The route's first leg actually ends **17.13 m** away, at the foot of ladder #4
   (`Level route: #4 low (-244.0, 0.1, -121.6) [target no, player yes]`). So the bug the
   third finding describes was **still in effect**: the driver was never asked to walk
   17 m, only 4.6 m — and 4.6 m is inside `ARRIVED_M` (4 m), so the order was "go where
   you already are". Measuring a no-op cannot distinguish one semantics from another.
2. **The leg-end switch is real in the source but does not fire in play.** `Drive` takes
   both points and prefers the leg — *unless* `aimFlat <= StopM + AimSlackM`, i.e. the
   aim point is within 5.5 m. At 4.63 m that test is **true**, so the aim point is picked
   by design. And 4.63 m is not a coincidence: it is the stand-off radius the beacon
   *deliberately* holds you at. The aim ring (4 m) and `StopM` (4 m) sit on top of each
   other by construction, so **on any objective the beacon reports as "17 metres, 12 m
   above", the driver ends up aiming at a point that is already inside its own stop
   radius.** That is structural, not a one-off.

**Decision, and why this is not simply another guess.** The player is being used as a test
harness, and a fourth "maybe this reading works" run costs them another session to settle a
question the log above already answers. `SetChaseWalk` is therefore closed as **does not
drive this avatar under any of the three readings**, and the walk moves to the route that
needs **none** of that unknown semantics.

| Route | Why it is now the fallback's first choice |
|---|---|
| `AvatarPlayerControll.SetSlaveMoveVec(vec3)` | The argument is a plain world-space **direction** — the one coordinate space this mod has already proven it can both read and write (the player's position is read in it every frame). Confirmed present in game 2026-09-29 10:49: `Components.Controll = app.worldtour.avatar.AvatarPlayerControll`, `IsDontUseAIMap = False`, `SetSlaveMoveVec(vec3)` and `SetSlaveMoveRate(float)` both found. No controller, no priority, no semantics. |
| `SetChaseWalk` | Kept in the source behind the measured-failure branch. If a later build gets the point selection right and a candidate measures a real closing, it is adopted automatically — the candidate list is not deleted. |

**The fallback's weaknesses, stated up front so they are not discovered in the field.**
It has no pathfinding at all, so it walks into walls and **cannot take a ladder or open a
door** — exactly the two things the owner asked for (`途中要经过梯子或其他物体也自动规划`).
And its direction is **persistent state** on the movement component, so it must be
cancelled explicitly with `SetSlaveMoveVec(0,0,0)`; merely ceasing to issue it would leave
the avatar walking away forever. `StopAndCancel` exists for that, and every stop path — the
safety hand-over, arrival, unavailable — goes through it.

---

## The third finding: it was driving to the wrong point

Second in-game test, and the crash was gone as intended — the player reported taking
the controls back mid-walk works. But the avatar still did not walk, and the log
explained why in one line:

```
Auto walk: order semantics in flight = ABSOLUTE world point;
    sent vec=(-248.54, 0.17, -108.57)  viewPos=(-246.15, 0.10, -104.61)
...
the ABSOLUTE world-point reading of SetChaseWalk did not move the avatar
    (4.6 -> 4.6 m over 1500 ms, closed 0.0 m)
```

**The point being walked to was only 4.63 m away** — of which the vertical part was
0.07 m. The objective was reported as *"17 metres, 12 m above"* and the ladder foot the
route had just picked was **17.1 m away**. A destination 4.6 m away, at the player's own
height, is not the objective, not the ladder and not anything on the route.

**It is the beacon's aim point.** `BeaconDestination.X/Y/Z` is deliberately pushed off
the objective so the homing sound is not pinned inside the objective's own head — a
mission marker sitting on the player's exact position is inaudible, and the whole point
of the aim point is to give the ear something to steer by. `dest.TargetX/Y/Z` is what
the code comments call *"End of the current leg: the destination, a ladder end or an
approach point"* — the place the player is actually going.

The first build fed the aim point to the walk order. So the order was, in effect,
"walk 4.63 m to a spot the design deliberately put in the wrong place", which the avatar
would have walked and then stopped — and because the arrival band is also 4 m, the
stand-off check and the aim ring sat at the same radius and the two cancelled into
"0.0 m closed, nothing happened". **The aim point is for the ears; the leg end is for
the feet.** `Drive` now takes both and drives the leg end, using the aim point only once
the avatar is already within `AimSlackM` of it, so the landing spot still matches what
the beacon calls "arrived".

## The second finding: the semantics were guessed twice, and both were wrong

With a correct destination the reading of `(vec3, vec3, bool)` still has to be right, and
neither of the first two guesses moved the avatar. Rather than guess a third time in
source and ask the player for yet another test cycle, the reading is now a **candidate
list tried in game and measured**, and the one that closes the distance is kept for the
session (`AutoWalk.SemanticsOrder`):

| Candidate | The order means |
|---|---|
| `ABSOLUTE point` | vec = the destination world point |
| `POINT then face` | vec = destination world point; second vec3 = the direction to face on arrival |
| `RELATIVE vector` | vec = destination relative to the second vec3 |

`POINT then face` is the one that the sibling overload argues for: it takes a
`GameObject viewObj` where this one takes `vec3 viewPos`, and that substitution is the
shape of `SetCrowdStand(targetObj, targetRotDir)` from the same dump — *go there, and end
facing this*. A walk that ends facing something is exactly what "chase walk" is for.
Each candidate gets a full window (~1.2 s / 6 orders) and the total has to go the right
way; a wrong reading does not creep, it walks the other way at full speed. The log now
prints `trying semantics X (n/3)` with the exact destination and player coordinates, and
then `MEASURED SEMANTICS = X - this reading is kept for the session` on success.

## The 2026-09-29 crash and what it cost

First deploy, first in-game test. The player reported, verbatim: **"我导航任务目标，
他搜索几秒以后一直重复这里不能自动行走，于是我换了一个和我同平面的目标，他不再提示
不能自动行走，但角色也没走，并且我只能转动镜头，不能移动角色"** — the search ran, the
walk refused with the "cannot auto-walk here" line, and then **the avatar stopped
accepting movement input while the camera still worked.**

The log said exactly what happened:

```
Auto-walk bound: CreateInputController + SetChaseWalk(vec3, vec3, bool) at FieldEvent
Auto-walk: input controller created at FieldEvent priority
VMContext: Caught exception code c0000005                     (x3)
Exception thrown in REMethodDefinition::invoke for app.worldtour.avatar.AvatarInputController.SetChaseWalk  (x3)
CALIBRATION FAILED - after 10500 ms ... 54109 ms              (90+ times, distance stuck at 4.6 m)
```

**Root cause.** The driver read `AvatarBase.GetMoveVec()` **every frame** to detect
the player's own stick, and that method **returns a reference type**. This codebase
already has the rule written down in `FieldProbeService`: *"a by-ref parameter whose
type is a REFERENCE type gets NO buffer and NO call — that is the rule that stopped
the probe crashing the game."* A returned reference is the same shape through the
callee's own out slot. `c0000005` is an access violation; the input VM was left
broken, which is precisely "the camera turns but the character does not move".

**What changed as a result (all deployed):**
1. **`GetMoveVec` is gone.** The player's stick is now detected from **the player's own
   world position**, differenced between two frames — the same reader the beacon
   already uses for everything else, and the only source for it that calls no engine
   method at all. Horizontal only, so a kerb does not read as the player steering.
2. **A failed order tears the controller down.** `AvatarBase.DestroyInputController
   (PriorityType)` is resolved and called, so a controller that consumed the player's
   input slot but did nothing can never be left registered. Before, it was merely
   dropped.
3. **A faulted order retires the walk for the session.** Re-issuing it every frame is
   how one crash became ninety repeats.
4. **The order is re-issued every frame** instead of every 250 ms. The 250 ms
   assumption ("the order is a persistent state") is exactly what makes a revoked
   order indistinguishable from a wrong argument, and one 250 ms sample is exactly the
   shape of a false negative. Every frame is correct under both readings and costs one
   engine call — `FieldRayCaster` makes 450 a second.
5. **Calibration takes six samples over ~1 s** and accepts any net closing, instead of
   firing on a single 1.5 s comparison. It still catches a wrong direction immediately,
   because a wrong direction does not creep — it walks the other way at full speed.
6. **Both readings of the vec3 pair are tried.** If the ABSOLUTE-world-point form does
   not close the distance, the driver flips to the RELATIVE form `(target − player)`
   and tries that before giving up. This is the only thing that can settle the
   ambiguity, and it now happens automatically inside one session. The log line
   `order semantics in flight = …` prints the three numbers actually sent, so the
   question can be answered from a log without another build.

**Not yet established:** the crash is *attributed*, not *proven*, though the second test
is strong evidence — the player could take the controls back and no `c0000005` or
`REMethodDefinition::invoke` line appeared again. The `GetMoveVec` call remains the
best-supported explanation because it is the only call in the driver that violates a rule
this codebase already paid for.

## Why "it searches but does not move"

Nothing in the mod writes to the avatar. `MissionBeaconHooks` only SPEAKS route
states and plays the homing cue; `LevelRoute` only plans. So "选中目标后开始搜索、
但角色不动" is the design working, not a fault. Auto-walk is new machinery, not a
bug fix.

## What the probe found (2026-09-29, in game)

The user held a stick and turned the camera ~102° (−170.4° → 87.5°, so the turn
was real) between two F9 presses. `GetVelocity()` stayed (0,0,0) both times, so the
raw-move-vector measurement did not resolve (the avatar was not actually moving in
that moment) — **but it does not matter**, because the probe found something better.

**The game exposes purpose-built action methods**, so auto-walk does not have to
push raw movement vectors (whose coordinate space is unknown) into the engine at all:

| Method | What it does |
|---|---|
| `SetChaseWalk(vec3, GameObject viewObj, bool mirror)` / `(vec3, vec3 viewPos, bool mirror)` | Walk toward a point via the game's own walk driver. **Sign/space NOT yet verified** — see Calibration. |
| `SetChaseRun(vec3, …, FrontMoveActionType, bool)` | Run toward a point. |
| `SetLadderStart(GameObject ladderOm)` | **Start climbing that ladder object.** |
| `SetLadderMoveDir(bool isUp)` | Up/down while on a ladder. |
| `SetLadderRapidDown()` | Fast slide down. |
| `SetDoorAction(GameObject om, vec3 doorDir, OpenActionType actType)` | **Open a door.** |
| `SetDoorAction_ForSceneChange(GameObject om)` | The scene-transition door. |
| `SetButtonPress(GameObject om, vec3 switchDir)` | Press a switch. |
| `SetWalkEndForOM()` | End a walk headed for an object. |
| `SetRunaway(vec3 dir)`, `SetStandTurn(vec3 dir)`, `SetDoContact()`, `SetDashTurbo()` | Flee / turn in place / make contact / dash. |
| `SetMove(vec3)`, `SetMoveRate(float)`, `SetAngleRate(float)` | Raw movement (space still unknown — now only a fallback). |

These are the same methods the game uses for NPC walk-in interactions, so they
respect doors and ladders instead of pushing a vector into a wall.

**Other confirmed facts (correcting the old doc notes):**
- The base class is **`app.worldtour.avatar.AvatarBase`**, NOT `app.avatar.AvatarBase`
  (the old note was wrong, which is why every `AvatarBase` lookup came back empty).
- `AvatarPlayer` (= `WTPlayerManager.GetAvatarPlayer()`) is
  `app.worldtour.avatar.AvatarPlayer`.
- `AvatarInputController` has a public constructor:
  `.ctor(AvatarBase avatar, AvatarInputControllerManager inputMgr, PriorityType priority, bool destroyOnRequest, bool destroyOnRequestSuccess)`
  — so a controller can be `CreateInstance`d directly, no factory needed.
- `AvatarInputControllerManager` publishes `DefaultInputController`,
  `AddedInputControllerList`, `CurrentTargetController`, `MoveVec` (a ValueType),
  and `IsEnableInput() = True`.
- `avatar.Components.Controll` = **`app.worldtour.avatar.AvatarPlayerControll`**, and
  `SetSlaveMoveVec(vec3)` / `SetSlaveMoveRate(float)` / `SetSlaveAngleRate(float)` are
  all present (`IsDontUseAIMap = False`) — the documented straight-line fallback is
  reachable.
- `GetCurrentInputControllerPriority() = 1` (= `Default`) while walking, so the field
  is reading the player's own controller; ours must be at a higher priority.
- Priority order (confirmed): `_Invalid=0, Default=1, __DebugDefault=2, FieldEvent=3,
  MenuUI=4, Flow=5, BattleIn=6, __DebugForce=7`.

## The plan, revised around the findings

**Stage 2 — DEPLOYED: drive the avatar with `SetChaseWalk`.**
Code: `Services/WorldTour/AutoWalk.cs` (driver), `Services/WorldTour/AutoWalkText.cs`
(lines), wired into `MissionBeaconHooks.Resolve` (it already computes the per-leg aim).

1. `AvatarBase.CreateInputController(PriorityType.FieldEvent)` — the game allocates and
   registers the controller. `FieldEvent` (3) is above the player's `Default` (1) so we
   win, below `MenuUI` (4) so the pause menu/phone still win.
2. **Every frame** it issues `SetChaseWalk(vec, playerPos, false)` — the earlier
   "each `OrderMs` (250 ms)" throttling is gone: a single throttled order is
   indistinguishable from a single no-op, and it was one of the reasons the
   2026-09-29 test could not tell "the order did nothing" from "the order was never
   sent". Re-issuing every frame is what the game's own NPC walk-in driver does.
3. Stops at 4 m (`ARRIVED_M`) from the leg end.

**The safety rule (non-negotiable).** The player moving their own avatar cancels
auto-walk immediately (their own position moving faster than
`PlayerSpeedThresholdMps` → release, speak `wt.walk.player_took_over`). Arrival,
target-loss and leaving the field also stop it. While driving, the beacon's
turn-by-turn speech is suppressed (the homing cue keeps playing) so the player is
never told to turn while the mod is already turning them. **The detector must never be
another engine call shaped like `GetMoveVec` — see the crash section.**

**Calibration — the sign/space of `SetChaseWalk` is NOT verified yet.** The TDB cannot
say whether the vec3 is an absolute world point or a direction relative to `viewPos`.
Guessing wrong would march a blind player backwards, so the code does what
`CameraAimService` already established as the rule: **measure the effect, do not trust a
read-back**. It compares the distance to the target before vs after six samples over
about a second, and, when a reading fails to close the distance, **advances to the next
candidate in `SemanticsOrder`**:

| # | `Semantics` | What it assumes | What it sends |
|---|---|---|---|
| 1 | `AbsolutePoint` | `vec` is the destination in world space; `viewPos` is where the walker looks from | `vec = target`, `viewPos = player` |
| 2 | `PointThenFace` | `vec` is the destination, `viewPos` is a *look* direction rather than a position — the shape the sibling `SetChaseWalk(vec3, GameObject viewObj, bool)` overload implies | `vec = target`, `viewPos = target − player` (y flattened) |
| 3 | `RelativeVector` | `vec` is a direction from `viewPos` | `vec = target − player`, `viewPos = player` |

The first candidate that measures a net closing is **kept for the session** and logged as
`MEASURED SEMANTICS = … - this reading is kept for the session`. If all three are spent,
the controller is torn down, `wt.walk.unavailable` is spoken, and the walk is **retired
for the session** — it never keeps pulling. Each candidate logs its exact numbers
(`trying semantics X (n/3); destination=(…); player=(…); dist=… m`) so the next log alone
decides it without another build.

**Stage 3: ladders and doors via the purpose-built actions (NOT STARTED).**
- ladder: walk to the foot → `SetLadderStart(ladderGameObject)` → `SetLadderMoveDir(true)`;
- door: walk to it → `SetDoorAction(om, dir, OpenActionType…)`;
- switch: `SetButtonPress(om, dir)`; end a walk to an object: `SetWalkEndForOM()`.
- These replace the earlier "synthesise the action button press" idea (the user's
  "自动按键"), which was the riskier approach.

## Stage 1b — the second probe run (DEPLOYED)

The probe was corrected and to be re-run once (F9):
- `AvatarBase` now resolved under the RIGHT name, so its factory methods and the
  controller constructor are listed;
- `AvatarInputControllerManager`'s full method surface is dumped;
- a new block prints every purpose-built action method with its exact signature;
- the raw move-vector block still runs, but is no longer load-bearing.

## Routes considered and not chosen (recorded, not re-derive)

| Route | Why not (for now) |
|---|---|
| `SetMove(vec3)` raw movement | Space unknown; superseded by `SetChaseWalk`, kept as fallback. |
| `AvatarBase.GetMoveVec()` to read the player's stick | **Crashed the input VM on 2026-09-29** — returns a reference type. Never again; see the crash section. The steering detector is now built from the player's own per-frame world position, no engine call at all. |
| `AvatarNaviControllBase.IsDontUseAIMap` + `SetSlaveMoveVec/Rate` | Straight-line only — no pathfinding, so still walks into walls. Reachable; last-resort fallback. |
| `AvatarBase.WarpImmediately(ref vec3)` (teleport) | Technically safest, but skips "walk there" entirely and can jump past trigger volumes the mission needs. Rejected by the user. |
| `AvatarBase.StartNavi(PriorityType, NaviRequestData)` | `NaviRequestData` has no factory → `CreateInstance`, the crash-prone family. Last resort. |

## Build / deploy note

The csproj default `SF6Path` is `D:\games\steam\...`; this machine's game is at
`C:\Program Files (x86)\Steam\...`, so builds MUST pass
`-p:SF6Path="C:\Program Files (x86)\Steam\steamapps\common\Street Fighter 6"`.
`MSB3021` (copy denied) is expected and harmless — the DLL is produced; deploy with
`elevate-deploy.py`.

