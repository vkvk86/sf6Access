using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>Auto-walk stage 1 — a READ-ONLY probe. It never moves the avatar.</b>
///
/// <para><b>Why it exists.</b> The beacon speaks a route but nothing in this mod
/// writes to the avatar (user decision 2026-09-12, "no auto-walk"; reversed by the
/// user 2026-09-29: "选中任务目标以后角色自动走过去，包括绕行，爬梯子"). The
/// documented route is input synthesis — create an input controller at a priority
/// the game honours and feed it a move vector — but ONE fact is missing and cannot
/// be guessed: the SPACE of <c>AvatarInputController.SetMove(vec3)</c>. Compared
/// against the camera it may be a WORLD direction ("walk north"), a CAMERA-relative
/// stick value ("push the stick this way"), or a raw unit input; picking wrong
/// sends a blind player somewhere arbitrary, which is worse than not moving.</para>
///
/// <para><b>How the space is derived, without moving anything.</b> The game
/// already publishes the answer twice, for free:
/// <list type="number">
/// <item><c>AvatarBase.GetCurrentInputMoveVec(out vec3)</c> — what the avatar is
///   moving by this frame;</item>
/// <item><c>AvatarInputController.GetMoveVec()</c> — the stick value the
///   controller holds.</item>
/// </list>
/// While the player HOLDS A STICK and rotates the camera, a world-space value
/// stays put as the camera turns (it is a bearing on the map) while a
/// camera-relative one rotates WITH the camera. Sampling both across a few frames
/// of stick-and-turn separates the two, and also says whether the values are
/// unit-length (a direction) or scaled by push depth (an analogue stick).</para>
///
/// <para><b>Everything here is a by-value read or a by-name member lookup.</b> No
/// <c>ref</c>/<c>out</c> parameter is filled except through
/// <see cref="FieldOutBuffer"/>, which is the one buffer this codebase has proven
/// safe. Nothing is cached across frames.</para>
///
/// <para>Findings go to a dump file; the caller (<c>AutoWalkProbeHooks</c>, F9)
/// writes it. Design notes: <c>docs/wt-autowalk.md</c>.</para>
/// </summary>
internal static class AutoWalkProbe
{
    // The avatar is reached the same way FieldProbeHooks reaches it, so the probe
    // describes the object the rest of the mod already talks to.
    private const string PLAYER_MANAGER = "app.worldtour.WTPlayerManager";
    private const string CONTROLLER_TYPE = "app.worldtour.avatar.AvatarInputController";
    // The base class lives under worldtour, NOT app.avatar — the earlier doc note
    // ("app.avatar.AvatarBase") was wrong and made every AvatarBase lookup come back
    // empty on the 2026-09-29 run. Both spellings are tried so the probe is right
    // whichever the game actually ships.
    private const string BASE_TYPE = "app.worldtour.avatar.AvatarBase";
    private const string BASE_TYPE_ALT = "app.avatar.AvatarBase";
    private const string MANAGER_TYPE = "app.worldtour.avatar.AvatarInputControllerManager";
    private const string PRIORITY_ENUM = "app.worldtour.avatar.AvatarInputController.PriorityType";

    /// <summary>One entry of the input-controller API surface, as the TDB reports
    /// it. Recorded so a call can be shaped against the REAL signature instead of
    /// a remembered one.</summary>
    private readonly struct Api(string owner, string signature)
    {
        public readonly string Owner = owner;
        public readonly string Signature = signature;
    }

    /// <summary>Writes the whole probe into <paramref name="sb"/>. Read-only.</summary>
    internal static void Dump(StringBuilder sb)
    {
        sb.AppendLine("=== SF6 AUTO-WALK PROBE (stage 1: what can move the avatar) ===");
        sb.AppendLine(FormattableString.Invariant($"time={DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
        sb.AppendLine("READ-ONLY: nothing below calls SetMove or any other writer.");
        sb.AppendLine();

        var avatar = ResolveAvatar(sb);
        Block(sb, "1. THE PRIORITY ENUM", () => DumpPriorityEnum(sb));
        Block(sb, "2. AvatarBase — CONTROLLER / WARP / NAVIGATION METHODS", () => DumpBaseType(sb));
        Block(sb, "3. AvatarInputController — FULL METHOD SURFACE", () => DumpTypeMethods(sb, CONTROLLER_TYPE, null));
        Block(sb, "3b. AvatarInputControllerManager — FULL METHOD SURFACE", () => DumpTypeMethods(sb, MANAGER_TYPE, null));
        Block(sb, "4. AvatarBase INPUT CONTROLLER STATE (read-only)", () => DumpControllerState(sb, avatar));
        Block(sb, "5. MOVE-VECTOR READINGS (hold a stick and turn the camera)", () => DumpMoveReadings(sb, avatar));
        Block(sb, "6. LADDER / ACCESS-TARGET ACTION KEYS", () => DumpActionKeys(sb));
        Block(sb, "7. PURPOSE-BUILT ACTION METHODS (the real prize)", () => DumpActionMethods(sb));
        Block(sb, "8. StartNavi REQUEST STRUCT (the last route: real pathfinding)",
              () => DumpNaviRequest(sb));
        Block(sb, "9. CAN A NaviRequestData BE BUILT? (allocates nothing)",
              () => DumpNaviBuildability(sb));
        sb.AppendLine("=== END ===");
    }

    // The controller methods that do the job the user described, WITHOUT needing
    // SetMove's unknown space: walk-to-a-point, climb a ladder, open a door, press a
    // switch, and end a walk at an object. Found in the 2026-09-29 dump.
    private static readonly string[] ACTION_METHODS =
    {
        "SetChaseWalk", "SetChaseRun", "SetChaseRunMode", "SetRunaway",
        "SetLadderStart", "SetLadderMoveDir", "SetLadderRapidDown",
        "SetDoorAction", "SetDoorAction_ForSceneChange",
        "SetButtonPress", "SetWalkEndForOM",
        "SetStandTurn", "SetDoContact", "SetDashTurbo",
        "SetMove", "SetMoveRate", "SetAngleRate",
    };

    /// <summary>What each purpose-built action method takes and returns, so stage 2
    /// can drive the game's own walk/climb/open actions instead of synthesising
    /// raw movement — which sidesteps the unknown space of <c>SetMove</c>.</summary>
    private static void DumpActionMethods(StringBuilder sb)
    {
        var td = TDB.Get().FindType(CONTROLLER_TYPE);
        if (td == null) { sb.AppendLine($"{CONTROLLER_TYPE}: NOT FOUND"); return; }

        var wanted = new HashSet<string>(ACTION_METHODS);
        var methods = td.GetMethods();
        if (methods == null) { sb.AppendLine("no methods"); return; }
        foreach (var m in methods)
        {
            if (!wanted.Contains(m.Name)) continue;
            sb.AppendLine($"  {Signature(m)}");
        }
        sb.AppendLine();
        sb.AppendLine("  NOTES (from the dump, 2026-09-29):");
        sb.AppendLine("  - SetChaseWalk(vec3, GameObject viewObj, bool mirror) / (vec3, vec3 viewPos, bool mirror):");
        sb.AppendLine("      'walk toward a world point' — the game's OWN walk driver, no stick needed.");
        sb.AppendLine("  - SetLadderStart(GameObject ladderOm): begin climbing THAT ladder object.");
        sb.AppendLine("  - SetLadderMoveDir(bool isUp): up or down while on a ladder.");
        sb.AppendLine("  - SetDoorAction(GameObject om, vec3 doorDir, OpenActionType actType): open a door.");
        sb.AppendLine("  - SetButtonPress(GameObject om, vec3 switchDir): press a switch.");
        sb.AppendLine("  - SetWalkEndForOM(): end a walk that was headed for an object.");
        sb.AppendLine("  These are what the game itself uses for NPC/walk-in interactions, so they");
        sb.AppendLine("  respect geometry (doors, ladders) rather than pushing a raw vector into walls.");
    }

    /// <summary>The base class, under whichever name the game ships, with the
    /// methods that matter for making and driving a controller.</summary>
    private static void DumpBaseType(StringBuilder sb)
    {
        var td = TDB.Get().FindType(BASE_TYPE) ?? TDB.Get().FindType(BASE_TYPE_ALT);
        if (td == null)
        {
            sb.AppendLine($"{BASE_TYPE} / {BASE_TYPE_ALT}: NEITHER found in the TDB");
            return;
        }
        sb.AppendLine($"resolved base type = {td.GetFullName()}");
        DumpTypeMethods(sb, td.GetFullName(), BASE_METHODS);

        // The factories are what stage 2 needs; resolve them by shape so a wrong
        // overload cannot be picked silently. The controller's own constructor
        // (seen in the 2026-09-29 dump) is the fallback if no factory exists.
        foreach (string name in new[] { "CreateInputController", "CreateSingleRequestInputController", "DestroyInputController" })
        {
            var m1 = FieldProbeService.FindByShape(td, name, 1, "PriorityType");
            var m3 = FieldProbeService.FindByShape(td, name, 3, "PriorityType");
            sb.AppendLine($"  {name}: 1-arg(PriorityType)={(m1 == null ? "no" : "YES")} " +
                          $"3-arg(PriorityType,bool,bool)={(m3 == null ? "no" : "YES")}");
        }
        // The controller's own constructor, so a direct CreateInstance is possible.
        var ctor = TDB.Get().FindType(CONTROLLER_TYPE)?.GetMethods();
        sb.AppendLine($"  {CONTROLLER_TYPE} constructors:");
        if (ctor != null)
            foreach (var m in ctor)
                if (m.Name == ".ctor") sb.AppendLine($"    {Signature(m)}");
    }

    // ---------- the avatar ----------

    private static ManagedObject ResolveAvatar(StringBuilder sb)
    {
        try
        {
            var pm = API.GetManagedSingleton(PLAYER_MANAGER) as ManagedObject;
            var avatar = pm == null ? null : FlowHelper.Call(pm, "GetAvatarPlayer") as ManagedObject;
            sb.AppendLine($"{PLAYER_MANAGER}.GetAvatarPlayer = {avatar?.GetTypeDefinition()?.GetFullName() ?? "null"}");
            return avatar;
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[avatar lookup failed: {ex.Message}]");
            return null;
        }
    }

    // ---------- 1. the priority enum ----------

    private static void DumpPriorityEnum(StringBuilder sb)
    {
        // The priority decides WHOSE input the game reads. The controller we create
        // must sit above Default for the game to use it, and below the menus so the
        // player can still open the pause menu and stop us.
        var members = FieldProbeService.ReadEnum(PRIORITY_ENUM, true);
        if (members.Count == 0) members = FieldProbeService.ReadEnum(PRIORITY_ENUM, false);
        if (members.Count == 0) { sb.AppendLine($"{PRIORITY_ENUM}: NOT FOUND in the TDB"); return; }

        sb.AppendLine($"{PRIORITY_ENUM} (declared value per member):");
        foreach (var (name, value) in members) sb.AppendLine($"  {name} = {value}");
        sb.AppendLine("documented order, low to high: _Invalid, Default, __DebugDefault, FieldEvent, MenuUI, Flow, BattleIn, __DebugForce");
    }

    // ---------- 2/3. method surface ----------

    // The methods that matter for moving the avatar, recorded by NAME so the probe
    // reports the real signatures rather than a remembered set.
    private static readonly string[] BASE_METHODS =
    {
        "CreateInputController", "CreateSingleRequestInputController", "DestroyInputController",
        "GetCurrentInputControllerPriority", "GetCurrentInputMoveVec", "GetMoveVec",
        "WarpImmediately", "RequestWarp", "IsRequestingWarp",
        "StartNavi", "ForceEndNavi", "IsNavigationWorking",
        "Components", "GetVelocity",
    };

    /// <summary>Every overload of every interesting method on a type, as the TDB
    /// spells it. <paramref name="names"/> null means "all methods".</summary>
    private static void DumpTypeMethods(StringBuilder sb, string typeName, string[] names)
    {
        var td = TDB.Get().FindType(typeName);
        if (td == null) { sb.AppendLine($"{typeName}: NOT FOUND in the TDB"); return; }
        sb.AppendLine($"{typeName}: found");

        var wanted = names == null ? null : new HashSet<string>(names);
        int shown = 0;
        // Walk the type AND its parents: SetMove lives on the concrete controller,
        // but the base classes publish the manager and the factory methods.
        for (var t = td; t != null; t = t.ParentType)
        {
            List<Method> methods;
            try { methods = t.GetMethods(); } catch { continue; }
            if (methods == null) continue;
            foreach (var m in methods)
            {
                if (wanted != null && !wanted.Contains(m.Name)) continue;
                sb.AppendLine($"  {Signature(m)}   [{t.GetFullName()}]");
                shown++;
            }
        }
        sb.AppendLine($"  ({shown} method(s) listed)");
    }

    /// <summary>A method's written signature: name + parameter types in order, so an
    /// overload can be matched exactly (this codebase has been bitten by overloads
    /// resolving to the wrong member by name alone).</summary>
    private static string Signature(Method m)
    {
        var sb = new StringBuilder(m.Name);
        sb.Append('(');
        try
        {
            var ps = m.GetParameters();
            if (ps != null)
            {
                for (int i = 0; i < ps.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append($"{ps[i].Type?.FullName ?? "?"} {ps[i].Name ?? "p" + i}");
                }
            }
        }
        catch { sb.Append("? (parameters unreadable)"); }
        sb.Append(')');
        return sb.ToString();
    }

    // ---------- 4. controller state, read-only ----------

    private static void DumpControllerState(StringBuilder sb, ManagedObject avatar)
    {
        if (avatar == null) { sb.AppendLine("no avatar"); return; }

        // GetCurrentInputControllerPriority() : PriorityType — which controller the
        // game is reading right now. If this is not "Default" the player is in a
        // menu, an event or a cutscene, and our controller would be ignored.
        try
        {
            object pri = FlowHelper.Call(avatar, "GetCurrentInputControllerPriority");
            sb.AppendLine($"  GetCurrentInputControllerPriority() = {pri ?? "null"}");
        }
        catch (Exception ex) { sb.AppendLine($"  priority read failed: {ex.Message}"); }

        // Prefer the type's OWN factory so we look at the controller the game built,
        // not one we made.
        var mgr = FieldProbeService.Member(avatar, "InputManager") as ManagedObject;
        sb.AppendLine($"  InputManager = {mgr?.GetTypeDefinition()?.GetFullName() ?? "null"}");
        if (mgr != null)
        {
            foreach (string member in new[]
                     {
                         "DefaultInputController", "AddedInputControllerList",
                         "CurrentTargetController", "MoveVec",
                     })
            {
                object v = FieldProbeService.Member(mgr, member);
                string shown = v is ManagedObject mo ? mo.GetTypeDefinition()?.GetFullName() ?? "?" : v?.ToString() ?? "null";
                sb.AppendLine($"    InputManager.{member} = {shown}");
            }
            try
            {
                object enabled = FlowHelper.Call(mgr, "IsEnableInput");
                sb.AppendLine($"    InputManager.IsEnableInput() = {enabled ?? "null"}");
            }
            catch (Exception ex) { sb.AppendLine($"    IsEnableInput() failed: {ex.Message}"); }
        }

        // The documented controller factory, resolved by shape so the overload is
        // certain: the one-argument form takes a PriorityType.
        var td = TDB.Get().FindType(BASE_TYPE) ?? TDB.Get().FindType(BASE_TYPE_ALT);
        var create = FieldProbeService.FindByShape(td, "CreateInputController", 1, "PriorityType");
        sb.AppendLine($"  CreateInputController(PriorityType) = {(create == null ? "NOT FOUND" : "found")}");
        var create3 = FieldProbeService.FindByShape(td, "CreateInputController", 3, "PriorityType");
        sb.AppendLine($"  CreateInputController(PriorityType, bool, bool) = {(create3 == null ? "NOT FOUND" : "found")}");

        // The component that actually drives the player: AvatarPlayerControll. Its
        // SetSlaveMoveVec is the documented straight-line escape hatch (stage 2
        // alternative) — record whether it is reachable.
        var comps = FieldProbeService.Member(avatar, "Components") as ManagedObject;
        var controll = comps == null ? null : FieldProbeService.Member(comps, "Controll") as ManagedObject;
        sb.AppendLine($"  Components = {comps?.GetTypeDefinition()?.GetFullName() ?? "null"}");
        sb.AppendLine($"  Components.Controll = {controll?.GetTypeDefinition()?.GetFullName() ?? "null"}");
        if (controll != null)
        {
            var ctd = controll.GetTypeDefinition();
            sb.AppendLine($"    IsDontUseAIMap = {FieldProbeService.Member(controll, "IsDontUseAIMap")?.ToString() ?? "null"}");
            sb.AppendLine($"    SetSlaveMoveVec(vec3) = {(FieldProbeService.FindByShape(ctd, "SetSlaveMoveVec", 1, "vec3") == null ? "NOT FOUND" : "found")}");
            sb.AppendLine($"    SetSlaveMoveRate(float) = {(FieldProbeService.FindByShape(ctd, "SetSlaveMoveRate", 1, "Single") == null ? "NOT FOUND" : "found")}");
            sb.AppendLine($"    SetSlaveAngleRate(float) = {(FieldProbeService.FindByShape(ctd, "SetSlaveAngleRate", 1, "Single") == null ? "NOT FOUND" : "found")}");
        }
    }

    // ---------- 5. the measurements that decide the coordinate space ----------

    /// <summary><b>The game's OWN navigation entry point, and the one route left.</b>
    /// <c>AvatarBase.StartNavi(PriorityType, AvatarControllBase.NaviRequestData)</c> plus
    /// <c>ForceEndNavi()</c> is how the game itself sends an avatar somewhere, and it is
    /// strictly better than anything this mod has tried: it is pathfinding, not a vector,
    /// so it can round a corner and take a ladder — the two things the owner actually asked
    /// for and the two things the straight-line route can never do.
    ///
    /// <para><b>Why it is not already used.</b> <c>NaviRequestData</c> is a struct on
    /// <c>AvatarControllBase</c> with no published constructor, so it has to be
    /// <c>CreateInstance</c>d and filled field by field — and this codebase has an
    /// expensive rule about calling into types whose layout it has not read (see
    /// <c>docs/sf6-architecture.md</c>). The layout is exactly what this block dumps:
    /// every field with its type and offset, every constructor, and the enum the request
    /// takes. Read-only, like everything else here.</para></summary>
    private static void DumpNaviRequest(StringBuilder sb)
    {
        const string NAVI_TYPE = "app.worldtour.avatar.AvatarControllBase.NaviRequestData";
        const string CONTROLL_BASE = "app.worldtour.avatar.AvatarControllBase";
        var td = TDB.Get().FindType(NAVI_TYPE);
        if (td == null)
        {
            sb.AppendLine($"{NAVI_TYPE} = NOT FOUND");
            // The outer type may carry it nested under a different name; list what IS there
            // so the next attempt is informed rather than another guess.
            var outer = TDB.Get().FindType(CONTROLL_BASE);
            sb.AppendLine($"  {CONTROLL_BASE} = {(outer == null ? "NOT FOUND either" : "found")}");
            return;
        }
        sb.AppendLine($"{NAVI_TYPE} = {td.GetFullName()}");

        // Size first: it is the one number that says whether a CreateInstance can be
        // attempted at all, and -- read against the field offsets below -- whether the
        // struct is the small "point at a destination" request or something with
        // object references in it (which cannot be synthesised safely).
        try
        {
            sb.AppendLine($"  Size             = {td.Size}");
            sb.AppendLine($"  ValueTypeSize    = {td.ValueTypeSize}");
        }
        catch (Exception ex) { sb.AppendLine($"  size read failed: {ex.Message}"); }
        try
        {
            sb.AppendLine($"  IsValueType={td.IsValueType()} IsEnum={td.IsEnum()} " +
                          $"ParentType={td.ParentType?.GetFullName() ?? "-none-"}");
        }
        catch (Exception ex) { sb.AppendLine($"  kind read failed: {ex.Message}"); }

        // Constructors are NOT a separate collection in this API: a constructor is the
        // method named ".ctor" in the ordinary method list. (An earlier draft of this
        // block called TypeDefinition.GetConstructors(), which does not exist.)
        sb.AppendLine("  constructors (.ctor entries in the method list):");
        try
        {
            int n = 0;
            foreach (var m in td.GetMethods())
            {
                if (m.Name != ".ctor") continue;
                sb.AppendLine($"    {Signature(m)}");
                n++;
            }
            if (n == 0) sb.AppendLine("    (none published) -- CreateInstance + field writes is the only way in");
        }
        catch (Exception ex) { sb.AppendLine($"    failed: {ex.Message}"); }

        // Fields with their offsets. OffsetFromBase is where the field sits inside the
        // instance, which is what has to be known before writing one: a wrong offset
        // writes over a neighbouring field and the request goes out as garbage.
        sb.AppendLine("  fields:");
        try
        {
            int n = 0;
            foreach (var f in td.GetFields())
            {
                string t, off, flags;
                try { t = f.Type?.FullName ?? "?"; } catch { t = "?"; }
                try { off = f.OffsetFromBase.ToString("X"); } catch { off = "?"; }
                try { flags = f.IsStatic() ? " [static]" : ""; } catch { flags = ""; }
                sb.AppendLine($"    +0x{off,-4} {t} {f.Name}{flags}");
                n++;
            }
            if (n == 0) sb.AppendLine("    (no fields published) -- the struct carries no data we can address");
        }
        catch (Exception ex) { sb.AppendLine($"    failed: {ex.Message}"); }

        // A field whose type is an enum is almost certainly the "kind of navigation"
        // switch and its values are the vocabulary the call is written in; missing it
        // would make the struct impossible to fill correctly even with a known layout.
        // Nested types are not enumerable from the outer type in this API, so each
        // field's own type is the way in: any enum field gets dumped in full.
        sb.AppendLine("  enum-typed fields, expanded:");
        try
        {
            int n = 0;
            foreach (var f in td.GetFields())
            {
                TypeDefinition ft;
                try { ft = f.Type; } catch { continue; }
                if (ft == null) continue;
                bool isEnum;
                try { isEnum = ft.IsEnum(); } catch { isEnum = false; }
                if (!isEnum) continue;
                sb.AppendLine($"    {f.Name} : {ft.GetFullName()}");
                // Read both widths, like the priority enum: a byte-backed enum read as
                // int drags in the neighbour's bytes and reports wrong values.
                var members = FieldProbeService.ReadEnum(ft.GetFullName(), true);
                if (members.Count == 0) members = FieldProbeService.ReadEnum(ft.GetFullName(), false);
                foreach (var (name, value) in members) sb.AppendLine($"      {name} = {value}");
                n++;
            }
            if (n == 0) sb.AppendLine("    (no enum-typed fields)");
        }
        catch (Exception ex) { sb.AppendLine($"    failed: {ex.Message}"); }

        // A pointer- or reference-typed field is a hard stop: the request would have to
        // carry a live engine object, and this codebase's rule (docs/sf6-architecture.md)
        // is that a value the mod did not itself obtain must never be handed to the
        // engine. Reported explicitly so the next step is a decision, not a discovery.
        sb.AppendLine("  fields that would block a hand-built request:");
        try
        {
            int n = 0;
            foreach (var f in td.GetFields())
            {
                TypeDefinition ft;
                try { ft = f.Type; } catch { continue; }
                if (ft == null) continue;
                string why = null;
                try { if (ft.IsPointer()) why = "pointer"; } catch { }
                try { if (why == null && ft.IsByRef()) why = "by-ref"; } catch { }
                try
                {
                    if (why == null && !ft.IsValueType())
                    {
                        // A managed-object field: the request needs an object graph.
                        string fn = ft.GetFullName() ?? "";
                        if (fn.StartsWith("app.") || fn.StartsWith("via.")) why = "engine object reference";
                    }
                }
                catch { }
                if (why == null) continue;
                sb.AppendLine($"    {f.Name} : {ft.GetFullName()} -- {why}");
                n++;
            }
            if (n == 0) sb.AppendLine("    (none -- every field is a plain value; a hand-built request is viable)");
        }
        catch (Exception ex) { sb.AppendLine($"    failed: {ex.Message}"); }
    }

    // ---------- 9. can a NaviRequestData actually be built? ----------

    /// <summary><b>Answers the one question block 8 left open, WITHOUT building
    /// anything.</b> Block 8 proved <c>NaviRequestData</c> carries five engine object
    /// references, so the request cannot be hand-filled from plain values. The two
    /// that matter are <c>RoutePos : via.vec3[]</c> (the destination itself) and
    /// <c>MoveArg : MoveActionArgBase</c>. This block interrogates the SHAPE of the
    /// APIs needed to supply them — which overloads exist, what they take, what they
    /// return — and allocates nothing.
    ///
    /// <para><b>Why nothing is allocated here.</b> <c>TypeDefinition.CreateManagedArray</c>
    /// exists in this REFramework build (verified by reading the assembly) but has
    /// never been used anywhere in this codebase, so its behaviour is unknown: which
    /// argument is the length, whether the elements start null or zeroed, whether the
    /// result is a per-thread LOCAL object the engine reclaims at the end of the frame
    /// (which <c>docs/sf6-architecture.md</c> says is the default for anything C#
    /// creates). The rule that stops this mod crashing the game is "read the shape
    /// before you call it", and this block is that read. An array created to observe
    /// would be exactly the value the rule forbids handing over.</para>
    ///
    /// <para>Read-only, like every other block.</para></summary>
    private static void DumpNaviBuildability(StringBuilder sb)
    {
        // (a) How do you make an array at all? Every overload, verbatim.
        sb.AppendLine("(a) TypeDefinition.CreateManagedArray / CreateValueType / CreateInstance:");
        var tdt = TDB.Get().FindType("via.vec3");
        DumpMembersNamed(sb, typeof(TypeDefinition), new[]
        {
            "CreateManagedArray", "CreateValueType", "CreateInstance",
            "GetElementType", "IsValueType", "IsEnum",
        });
        sb.AppendLine();

        // (b) What IS via.vec3? The codebase already reads one, but whether it can be
        // WRITTEN (created) decides the whole route — a value type is creatable, a
        // generated interface is not (see FieldProbeService's remarks).
        sb.AppendLine("(b) via.vec3 — the element type:");
        if (tdt == null) sb.AppendLine("    via.vec3: NOT FOUND");
        else
        {
            try
            {
                sb.AppendLine($"    FullName     = {tdt.GetFullName()}");
                sb.AppendLine($"    Size         = {tdt.Size}   ValueTypeSize = {tdt.ValueTypeSize}");
                sb.AppendLine($"    IsValueType  = {tdt.IsValueType()}   IsEnum = {tdt.IsEnum()}");
                sb.AppendLine($"    IsPointer    = {tdt.IsPointer()}   IsByRef = {tdt.IsByRef()}");
                sb.AppendLine($"    ParentType   = {tdt.ParentType?.GetFullName() ?? "-none-"}");
                sb.AppendLine($"    DeclaringType= {tdt.GetDeclaringType()?.GetFullName() ?? "-none-"}");
                sb.AppendLine($"    ElementType  = {tdt.GetElementType()?.GetFullName() ?? "-none- (not an array)"}");
                sb.AppendLine($"    constructors = {CountCtors(tdt)}");
                sb.AppendLine("    fields:");
                foreach (var f in tdt.GetFields())
                {
                    string t = "?", off = "?";
                    try { t = f.Type?.FullName ?? "?"; } catch { }
                    try { off = f.OffsetFromBase.ToString("X"); } catch { }
                    sb.AppendLine($"      +0x{off,-4} {t} {f.Name}");
                }
                sb.AppendLine("    methods (x/y/z setters are what a fill needs):");
                foreach (var m in tdt.GetMethods())
                {
                    if (m.Name == "GetType" || m.Name == "ToString" || m.Name == "Equals"
                        || m.Name == "GetHashCode") continue;
                    sb.AppendLine($"      {Signature(m)}");
                }
            }
            catch (Exception ex) { sb.AppendLine($"    failed: {ex.Message}"); }
        }
        sb.AppendLine();

        // (c) MoveArg : MoveActionArgBase — the other referenced field. If it is
        // abstract or has no parameterless constructor, it must be left null (likely
        // fine for MoveType=Walk), and that decides whether the request is buildable.
        sb.AppendLine("(c) MoveActionArgBase — the MoveArg field's type:");
        const string ARG = "app.worldtour.avatar.AvatarControllBase.NaviRequestData.MoveActionArgBase";
        var tda = TDB.Get().FindType(ARG) ?? TDB.Get().FindType(
            "app.worldtour.avatar.AvatarControllBase.MoveActionArgBase");
        if (tda == null) sb.AppendLine($"    {ARG}: NOT FOUND (leaving it null is then the only option)");
        else
        {
            try
            {
                sb.AppendLine($"    FullName    = {tda.GetFullName()}");
                sb.AppendLine($"    Size        = {tda.Size}   IsValueType = {tda.IsValueType()}");
                sb.AppendLine($"    DeclaringType = {tda.GetDeclaringType()?.GetFullName() ?? "-none-"}");
                var rt = tda.GetRuntimeType();
                if (rt != null)
                {
                    sb.AppendLine($"    runtime IsAbstract = {FlowHelper.Call(rt, "get_IsAbstract") ?? "?"}");
                    sb.AppendLine($"    runtime IsInterface = {FlowHelper.Call(rt, "get_IsInterface") ?? "?"}");
                }
                sb.AppendLine($"    .ctor count = {CountCtors(tda)}");
                foreach (var m in tda.GetMethods())
                    if (m.Name == ".ctor") sb.AppendLine($"      {Signature(m)}");
                sb.AppendLine("    fields:");
                foreach (var f in tda.GetFields())
                {
                    string t = "?";
                    try { t = f.Type?.FullName ?? "?"; } catch { }
                    sb.AppendLine($"      {t} {f.Name}");
                }
                sb.AppendLine($"    derived types in the TDB: {string.Join(", ",
                    DerivedTypes(ARG, "app.worldtour.avatar.AvatarControllBase.NaviRequestData.MoveActionArg"))}");
            }
            catch (Exception ex) { sb.AppendLine($"    failed: {ex.Message}"); }
        }
        sb.AppendLine();

        // (d) Is a delegate field allowed to be null? Rather than guess, look at
        // whether the field's type is a delegate at all, and report the fact.
        sb.AppendLine("(d) the remaining referenced fields — what would have to be supplied:");
        var navi = TDB.Get().FindType("app.worldtour.avatar.AvatarControllBase.NaviRequestData");
        if (navi != null)
        {
            foreach (var f in navi.GetFields())
            {
                string fn;
                try { fn = f.Name; } catch { continue; }
                if (fn != "RouteObject" && fn != "MoveArg" && fn != "WTNpcAIComponents"
                    && fn != "OnNavigationFinish" && fn != "RoutePos") continue;
                TypeDefinition ft = null;
                try { ft = f.Type; } catch { }
                string kind = "?";
                if (ft != null)
                {
                    try { kind = ft.IsValueType() ? "value type" : "reference type"; } catch { }
                }
                string el = null;
                try { el = ft?.GetElementType()?.GetFullName(); } catch { }
                sb.AppendLine($"    {fn,-20} {ft?.GetFullName() ?? "?",-70} {kind}" +
                              (el != null ? $"  <element {el}>" : ""));
            }
            sb.AppendLine("    NOTE: a null field is only safe if the game's own call path tolerates it.");
            sb.AppendLine("    TargetType=Position means RouteObject is never read; MoveType=Walk");
            sb.AppendLine("    means MoveArg is the natural candidate for 'not needed'. Both are");
            sb.AppendLine("    hypotheses until the first real StartNavi call, one at a time.");
        }

        sb.AppendLine();
        sb.AppendLine("  VERDICT GUIDE (for the next step):");
        sb.AppendLine("   - via.vec3 creatable + CreateManagedArray(1-arg len) -> request is buildable.");
        sb.AppendLine("   - via.vec3 NOT creatable -> RoutePos cannot be supplied -> StartNavi is out,");
        sb.AppendLine("     and the remaining route is WarpImmediately(pos, dir) (3 overloads confirmed).");
    }

    /// <summary>All members of a CLR type by name (the wrapper's own API surface, not
    /// the game's) — used to read the exact signature of an REFramework helper before
    /// calling it for the first time.</summary>
    private static void DumpMembersNamed(StringBuilder sb, Type clr, string[] names)
    {
        foreach (string n in names)
        {
            var hits = new List<string>();
            foreach (var m in clr.GetMethods(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                         | System.Reflection.BindingFlags.Static))
            {
                if (m.Name != n) continue;
                var ps = new List<string>();
                foreach (var p in m.GetParameters()) ps.Add($"{Simple(p.ParameterType)} {p.Name}");
                hits.Add($"{Simple(m.ReturnType)} {m.Name}({string.Join(", ", ps)})");
            }
            if (hits.Count == 0) sb.AppendLine($"    {n}: NOT PRESENT");
            else foreach (string h in hits) sb.AppendLine($"    {h}");
        }
    }

    private static string Simple(Type t) =>
        t == null ? "?" : t.IsGenericType
            ? $"{t.Name.Split('`')[0]}<{string.Join(", ", Array.ConvertAll(t.GetGenericArguments(), Simple))}>"
            : t.Name;

    private static int CountCtors(TypeDefinition td)
    {
        int n = 0;
        try { foreach (var m in td.GetMethods()) if (m.Name == ".ctor") n++; } catch { }
        return n;
    }

    /// <summary>Types in the TDB whose parent chain includes <paramref name="baseName"/>.
    ///
    /// <para><b>The TDB's real surface — read off the assembly, not guessed:</b>
    /// <c>uint GetNumTypes()</c>, <c>TypeDefinition GetType(uint index)</c>,
    /// <c>TypeDefinition GetType(string name)</c>, <c>FindType(string)</c>,
    /// <c>FindTypeByFqn(uint)</c>, <c>Module GetModule(uint)</c>, plus a
    /// <c>Types</c> collection. Two earlier drafts of this method guessed wrong in
    /// different ways — <c>GetType(i)</c> against a <c>TypeDefinition</c>, then
    /// <c>GetTypeT(index)</c> (which is a generic <c>GetTypeT&lt;T&gt;()</c> taking
    /// NO arguments) — and both are recorded in <c>docs/sf6-architecture.md</c>: the
    /// reflection surface here is NOT .NET's, so the shape is read from the assembly
    /// before it is called.</para>
    ///
    /// <para>The scan is bounded at 60 names because a full TDB walk is tens of
    /// thousands of types.</para></summary>
    private static List<string> DerivedTypes(string baseName, string prefix)
    {
        var found = new List<string>();
        try
        {
            var tdb = TDB.Get();
            uint n = 0;
            try { n = tdb.GetNumTypes(); } catch { }
            for (uint i = 0; i < n && found.Count < 60; i++)
            {
                TypeDefinition td = null;
                try { td = tdb.GetType(i); } catch { }
                if (td == null) continue;
                string fn = null;
                try { fn = td.GetFullName(); } catch { }
                if (fn == null || !fn.StartsWith(prefix, StringComparison.Ordinal)) continue;
                for (var p = td.ParentType; p != null; p = p.ParentType)
                {
                    string pn = null;
                    try { pn = p.GetFullName(); } catch { }
                    if (pn == baseName) { found.Add(fn); break; }
                }
            }
        }
        catch { }
        return found;
    }

    /// <summary>
    /// <b>The measurement the whole feature turns on.</b> Reads the camera bearing,
    /// the avatar's published input move vector, and the resolved controller's move
    /// vector in the SAME frame. The player must hold a stick and rotate the camera
    /// between presses; a world-space vector holds its bearing as the camera turns,
    /// a camera-relative one follows it. Each press appends one line, so several
    /// presses build the comparison. Read-only.
    /// </summary>
    private static void DumpMoveReadings(StringBuilder sb, ManagedObject avatar)
    {
        if (avatar == null) { sb.AppendLine("no avatar"); return; }

        float cam = CameraBearing(sb);
        var (px, py, pz, ok) = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        sb.AppendLine(FormattableString.Invariant(
            $"  camera bearing = {cam:F1} deg (clock from camera forward); player=({px:F2}, {py:F2}, {pz:F2}) ok={ok}"));

        // (a) AvatarBase.GetCurrentInputMoveVec(out vec3) — the avatar's own view of
        // its movement this frame.
        sb.AppendLine($"  (a) AvatarBase.GetCurrentInputMoveVec(out vec3):");
        DumpOutVec(sb, avatar, "GetCurrentInputMoveVec", BASE_TYPE);

        // (b) the resolved controller's GetMoveVec().
        var ctrl = ResolveDefaultController(avatar);
        sb.AppendLine($"  (b) resolved controller = {ctrl?.GetTypeDefinition()?.GetFullName() ?? "null"}");
        if (ctrl != null)
        {
            foreach (string m in new[] { "GetMoveVec", "get_MoveVec", "GetMove" })
            {
                try
                {
                    object v = FlowHelper.Call(ctrl, m);
                    if (v == null) continue;
                    sb.AppendLine(FormattableString.Invariant(
                        $"      {m}() = ({FieldProbeService.Vec(v)}) len={Length(v):F3}"));
                }
                catch (Exception ex) { sb.AppendLine($"      {m}() failed: {ex.Message}"); }
            }
            sb.AppendLine($"      controller MoveVec = {FieldProbeService.Vec(FieldProbeService.Member(ctrl, "MoveVec"))}");
            sb.AppendLine($"      controller PriorityType = {FieldProbeService.Member(ctrl, "PriorityType")?.ToString() ?? "null"}");
        }

        // (c) the avatar's actual velocity — the ground truth for "what the move
        // vector produced", regardless of which space it was in.
        try
        {
            sb.AppendLine(FormattableString.Invariant(
                $"  (c) AvatarBase.GetVelocity() = ({FieldProbeService.Vec(FlowHelper.Call(avatar, "GetVelocity"))})"));
        }
        catch (Exception ex) { sb.AppendLine($"  (c) GetVelocity() failed: {ex.Message}"); }

        sb.AppendLine();
        sb.AppendLine("  HOW TO READ: hold the left stick in one direction, press F9, then");
        sb.AppendLine("  rotate the camera ~90 deg while still holding the same stick, and press F9");
        sb.AppendLine("  again. If the (a)/(b) bearing stays the same while the camera turns, the");
        sb.AppendLine("  vector is WORLD space; if it rotates with the camera, it is camera-relative.");
        sb.AppendLine("  A length near 1.0 means a direction; a smaller, varying length means an");
        sb.AppendLine("  analogue stick value carrying push depth.");
    }

    /// <summary>A method with one <c>out vec3</c>, through the one safe buffer.</summary>
    private static void DumpOutVec(StringBuilder sb, ManagedObject owner, string methodName, string typeName)
    {
        var td = TDB.Get().FindType(typeName);
        var m = FieldProbeService.FindByShape(td, methodName, 1, "vec3");
        if (m == null) { sb.AppendLine($"      {methodName}(out vec3): NOT FOUND"); return; }

        var ps = m.GetParameters();
        var outType = ps != null && ps.Count > 0 ? ps[0].Type : null;
        using var buf = FieldOutBuffer.Acquire(outType);
        if (buf == null) { sb.AppendLine($"      {methodName}: buffer refused ({FieldOutBuffer.Refusal(outType)})"); return; }

        try
        {
            m.InvokeBoxed(typeof(void), owner, new object[] { buf.View });
            sb.AppendLine(FormattableString.Invariant(
                $"      = ({buf.Component("x"):F3}, {buf.Component("y"):F3}, {buf.Component("z"):F3})"));
        }
        catch (Exception ex) { sb.AppendLine($"      {methodName} failed: {ex.Message}"); }
    }

    /// <summary>The controller the game is reading, preferring the manager's own
    /// current target and falling back to the default. Never created by us.</summary>
    private static ManagedObject ResolveDefaultController(ManagedObject avatar)
    {
        var mgr = FieldProbeService.Member(avatar, "InputManager") as ManagedObject;
        if (mgr == null) return null;
        return FieldProbeService.Member(mgr, "CurrentTargetController") as ManagedObject
               ?? FieldProbeService.Member(mgr, "DefaultInputController") as ManagedObject;
    }

    private static float CameraBearing(StringBuilder sb)
    {
        try
        {
            var f = FieldDirectionService.GetCameraForward();
            if (!f.Ok) return 0f;
            // bearing 0 = camera forward (-Z convention of the service's own clock).
            return MathF.Atan2(f.X, -f.Z) * (180f / MathF.PI);
        }
        catch { return 0f; }
    }

    private static float Length(object vec)
    {
        float x = FlowHelper.ReadVecComponent(vec, "x");
        float y = FlowHelper.ReadVecComponent(vec, "y");
        float z = FlowHelper.ReadVecComponent(vec, "z");
        return MathF.Sqrt(x * x + y * y + z * z);
    }

    // ---------- 6. the keys we would need to press ----------

    /// <summary>What the game offers at a ladder/door and which input slot the
    /// action key is on, so stage 3 can press it. Recorded, not pressed.</summary>
    private static void DumpActionKeys(StringBuilder sb)
    {
        try
        {
            var prompts = FieldActionReader.Current(Environment.TickCount64);
            if (prompts == null || prompts.Count == 0)
            {
                sb.AppendLine("  current access prompts: none (stand at a ladder/door and press F9)");
            }
            else
            {
                foreach (var p in prompts)
                    sb.AppendLine(FormattableString.Invariant(
                        $"  prompt key='{p.Key}' text='{p.Text}' target=0x{p.Target:X}"));
            }
        }
        catch (Exception ex) { sb.AppendLine($"  action read failed: {ex.Message}"); }

        sb.AppendLine("  (the field action key is the same one the player presses to climb/open;");
        sb.AppendLine("   stage 3 must supply it through the input controller, not a keyboard event)");
    }

    private static void Block(StringBuilder sb, string title, Action body)
    {
        sb.AppendLine($"========== {title} ==========");
        try { body(); }
        catch (Exception ex) { sb.AppendLine($"[BLOCK FAILED: {ex.GetType().Name}: {ex.Message}]"); }
        sb.AppendLine();
    }
}
