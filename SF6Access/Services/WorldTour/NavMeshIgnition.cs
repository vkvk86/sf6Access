using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>The navmesh ignition: one deliberate, armed, DISK-TRACKED attempt to read the city's
/// own walkable map — the thing a sighted player gets for free by looking.</b>
///
/// <para><b>Why this file exists at all.</b> The owner's requirement (2026-10-03 22:58) is that
/// the FIRST visit to a place must be enough: "第一次来也一定能找到目标，因为明眼玩家都能走到".
/// Rays cannot do that — they see sixteen metres. Only the engine's navmesh sees the whole
/// city, and it is the exact counterpart of what sighted players use. The reader
/// (<see cref="NavMeshNodes"/>) was written in September and has never completed a run: its
/// first attempt (2026-09-08) took the game down with a clean log, and it has been switched
/// off ever since. What stands between this mod and a real city map is one question:
/// <i>which call kills the process?</i></para>
///
/// <para><b>A native fault is not a .NET exception.</b> An access violation walks straight
/// through every try/catch, and — the part that matters here — the log lines written before it
/// are NOT guaranteed to have reached the disk. That is why the crashed session left a clean
/// log and why guessing from the log is not enough: this file writes the step number to a file
/// BEFORE making the call, so a crash leaves "step=4" on disk and nothing else. The next
/// launch reads that and knows precisely which call never returned.</para>
///
/// <para><b>The circuit breaker.</b> If a run did not finish, the route is TRIPPED: no engine
/// call is ever made again, and pressing the key only says so. A player must not be able to
/// crash their own game twice by pressing a key twice. Recovery is deliberate: delete
/// <c>reframework/data/sf6access_navmesh_probe.txt</c>.</para>
///
/// <para><b>Nothing here is polled.</b> It runs when the key is pressed and never otherwise,
/// and it holds no engine object across the call — results are turned into plain numbers
/// immediately, which is the house rule this codebase has paid for three times.</para>
/// </summary>
internal static class NavMeshIgnition
{
    private const string FILE = "sf6access_navmesh_probe.txt";
    private const string TYPE_FILE = "sf6access_navmesh_type.txt";
    private const string ESSENTIAL_FILE = "sf6access_navmesh_essential.txt";

    /// <summary>Raised every time the step list changes. A marker left by an OLDER generation
    /// is evidence about code that no longer exists, so it is thrown away and the route
    /// re-armed automatically. Without this the player would have to delete a file by hand
    /// to test a new probe — and a probe that cannot be retested is a probe that stays
    /// switched off.</summary>
    private const int PROBE_GEN = 2;

    /// <summary>
    /// <b>2026-10-03: this route is retired. The two crashes that retired it are the evidence.</b>
    ///
    /// <para>Generation 1 died reading the node's own fields (<c>Pos</c>, <c>Wall</c>).
    /// Generation 2 died enumerating the node's TYPE — pure metadata, no field value touched,
    /// and a step deliberately designed so that it could not be the one to die. Two different
    /// operations, one common cause: <b>the object handed back by <c>queryClosestNode(vec3)</c>
    /// is not a usable engine reference.</b></para>
    ///
    /// <para>The shape of the failure says what it is. Step 3 of generation 2 —
    /// <c>GetTypeDefinition()</c> plus <c>GetFullName()</c> — returned without faulting, and
    /// the very next use of what it returned faulted. A live object does not behave like that;
    /// a wrapper around a dead one does. <c>NodeInfo</c> is almost certainly a struct returned
    /// BY VALUE, and boxing that as <c>typeof(object)</c> cannot produce a reference to
    /// anything — it produces a handle to a temporary. That is the same class of bug this
    /// codebase already pays for elsewhere, and there is no way to use such a value without
    /// touching it.</para>
    ///
    /// <para>So nothing downstream of that call is safe, and the route is closed rather than
    /// probed a third time. The city map does not need it: <see cref="FieldNavPath"/> — the
    /// engine's own pathfinder — has never crashed, already returns real route polylines, and
    /// already yields node IDs. That is where the map will come from.</para>
    /// </summary>
    private const bool RETIRED = true;

    private static bool _loaded;
    private static bool _tripped;
    private static int _diedAtStep;
    private static string _diedDoing = "";

    /// <summary>True once a previous attempt failed to come back. No engine navmesh call is
    /// made again while this is set.</summary>
    internal static bool Tripped => _tripped;

    /// <summary>The step number written before the call that did not return, for the log and
    /// for the spoken line.</summary>
    internal static int DiedAtStep => _diedAtStep;

    /// <summary>What that step was about to do, read back from the marker.</summary>
    internal static string DiedDoing => _diedDoing;

    /// <summary>How many ways out the last successful run found — the number that says "there
    /// is a map here" more plainly than any coordinate does.</summary>
    internal static int LastLinks { get; private set; }

    private static string FilePath => Path.Combine(ObjectDumper.DumpDir, FILE);

    // ---------------------------------------------------------------------------------
    // the marker
    // ---------------------------------------------------------------------------------

    /// <summary>Write the marker, synchronously, every time. Not buffered, not deferred: the
    /// whole point of the file is to survive a process that is about to die.</summary>
    private static void Mark(string what)
    {
        try
        {
            Directory.CreateDirectory(ObjectDumper.DumpDir);
            File.WriteAllText(FilePath, FormattableString.Invariant($"gen={PROBE_GEN} {what}"));
        }
        catch (Exception ex)
        {
            API.LogWarning("[SF6Access] Navmesh ignition: could not write its marker: " + ex.Message);
        }
    }

    /// <summary>Read what the LAST run left behind. A file that says "step=N" means the game
    /// died during step N; a file that says "ok" means the whole route ran and is safe.</summary>
    internal static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        // A retired route has no marker to interpret: whatever is on disk describes code that
        // will never run again, and reading it would only relabel an old crash as a new one.
        // The const makes everything below unreachable today, and that is deliberate — it is
        // left standing so the evidence and the route can be read together.
#pragma warning disable CS0162
        if (RETIRED) return;
        try
        {
            if (!File.Exists(FilePath)) return;
            string text = File.ReadAllText(FilePath).Trim();

            int gen = ReadGen(text);
            if (gen != PROBE_GEN)
            {
                // Evidence about a step list that is no longer the one that would run. Throw
                // it away and arm again, so a new probe can be tested without asking the
                // player to go looking for a file.
                // One interpolated string, not two added together: string concatenation turns
                // a FormattableString back into a plain string, and a comma decimal separator
                // in a log line is not a number anyone can read (docs/sf6-screens.md).
                API.LogInfo(FormattableString.Invariant(
                    $"[SF6Access] Navmesh ignition: discarding a marker from generation {gen}; this probe is generation {PROBE_GEN}, so the route is armed again"));
                try { File.Delete(FilePath); } catch { }
                return;
            }

            if (text.StartsWith("gen", StringComparison.Ordinal) && text.Contains("tripped"))
            {
                _tripped = true;
                _diedAtStep = ReadStep(text);
                _diedDoing = text;
                API.LogWarning($"[SF6Access] Navmesh ignition: TRIPPED from a previous crash at step {_diedAtStep}");
                return;
            }
            if (text.Contains("done ok"))
            {
                API.LogInfo("[SF6Access] Navmesh ignition: a previous run completed; the route is armed");
                return;
            }
            if (text.Contains("unreadable"))
            {
                // It answered "nothing here" without dying. Not a trip: another spot may have
                // a map to read.
                API.LogInfo("[SF6Access] Navmesh ignition: the last run found nothing to read; armed to try again");
                return;
            }
            // Reached here: the game was killed mid-run. This is the answer the September
            // crash could not give.
            Trip(ReadStep(text), text);
        }
            catch (Exception ex)
            {
                API.LogWarning("[SF6Access] Navmesh ignition: could not read its marker: " + ex.Message);
            }
#pragma warning restore CS0162
    }

    private static int ReadStep(string text)
    {
        int at = text.IndexOf("step=", StringComparison.Ordinal);
        if (at < 0) return 0;
        return int.TryParse(text.Substring(at + 5).Split(' ')[0], out int n) ? n : 0;
    }

    /// <summary>Which step list wrote this marker. A different generation means the marker
    /// describes code that has since been changed, so it proves nothing.</summary>
    private static int ReadGen(string text)
    {
        int at = text.IndexOf("gen=", StringComparison.Ordinal);
        if (at < 0) return 0;
        return int.TryParse(text.Substring(at + 4).Split(' ')[0], out int n) ? n : 0;
    }

    /// <summary>Break the circuit: never call into the navmesh again, and say why.</summary>
    private static void Trip(int step, string doing)
    {
        _tripped = true;
        _diedAtStep = step;
        _diedDoing = doing;
        API.LogError($"[SF6Access] Navmesh ignition: the game died during step {step} ({doing}). " +
                     "The navmesh route is switched off for good; delete " + FILE + " to arm it again.");
        Mark($"tripped step={step} {doing}");
    }

    // ---------------------------------------------------------------------------------
    // the run
    // ---------------------------------------------------------------------------------

    internal enum Verdict { Ok, Tripped, NoPlayer, Unreadable, Retired }

    /// <summary>One armed attempt. Every step writes its number first, so a crash names
    /// itself. Returns what happened and a line for the log and the player.</summary>
    internal static (Verdict verdict, string line) Run()
    {
        // Retired before anything is read, let alone called: this key must never again be
        // able to take the game down.
        if (RETIRED)
        {
            API.LogInfo("[SF6Access] Navmesh ignition: RETIRED. The node route crashed the game " +
                        "twice on 2026-10-03 (fields, then type metadata) and is closed. The city " +
                        "map is being built from the pathfinder instead.");
            return (Verdict.Retired, "");
        }
#pragma warning disable CS0162

        Load();
        if (_tripped) return (Verdict.Tripped, $"step {_diedAtStep}");

        var player = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        if (!player.ok) return (Verdict.NoPlayer, "");

        // Fresh trace: the reader prints every step before it runs, once per process.
        NavMeshNodes.Reset();
        API.LogInfo(FormattableString.Invariant(
            $"[SF6Access] Navmesh ignition: arming, player at ({player.x:F2}, {player.y:F2}, {player.z:F2})"));

        // --- 1: the map handle (proven in game, September 4th) ---
        Mark("step=1 findMapHandle()");
        var handle = NavMeshNodes.FindHandle();
        if (handle == null) return Unreadable(1);

        // --- 2: the polygon under the player. Generation 1 reached step 3, so this call
        // CAME BACK: the prime suspect of two months is cleared. ---
        Mark("step=2 queryClosestNode(vec3)");
        var node = NavMeshNodes.ClosestNode(handle, player.x, player.y, player.z);
        if (node == null) return Unreadable(2);

        // --- 3: WHAT came back, asked of the type and not of the instance. No field is
        // read, so this cannot be the call that dies. ---
        Mark("step=3 node type definition (metadata only, no field read)");
        var td = node.GetTypeDefinition();
        string typeName = td?.GetFullName() ?? "?";
        string valueType = TryValueType(td);

        // --- 4: the type's members, written to a file. Signatures only. ---
        Mark("step=4 dumping the node type to " + TYPE_FILE);
        DumpType(td, FormattableString.Invariant(
            $"returned by queryClosestNode(vec3) at ({player.x:F2}, {player.y:F2}, {player.z:F2})"));

        // --- 5: how many corners the polygon has - a plain integer, no geometry yet ---
        Mark("step=5 getGlobalVertexCount()");
        int corners = NavMeshNodes.VertexCount(node);

        // --- 6: the corners themselves, in world space (first vec3 read off a node) ---
        Mark("step=6 getGlobalVertex(uint) on the player node");
        var mine = NavMeshNodes.GlobalVertices(node, 6, "player node");

        // --- 7: the ways out of it ---
        Mark("step=7 queryLinkToNodes()/getNodeCount()");
        var links = NavMeshNodes.LinkedNodes(node, out int reported);

        int neighbourVerts = 0;
        if (links.Count > 0)
        {
            Mark("step=8 getGlobalVertex() on linked node 0");
            neighbourVerts = NavMeshNodes.GlobalVertices(links[0], 8, "linked node 0").Count;
        }

        // Everything a city map actually needs is now ON DISK. A crash past this point
        // still leaves a usable answer behind instead of only a step number.
        WriteEssentials(typeName, valueType, corners, mine, links.Count, reported, neighbourVerts);

        // --- 9/10: plain fields on the node. Generation 1 died on exactly these two, and
        // NOTHING needs them: a node's centre is the average of its own corners, which
        // step 6 already read. They run LAST so that if they kill the process again, steps
        // 1 to 8 stand proven and can simply be built on. ---
        Mark("step=9 NodeInfo.Pos field (optional - the suspected killer)");
        var pos = NavMeshNodes.Pos(node);

        Mark("step=10 NodeInfo.Wall field (optional)");
        bool wall = NavMeshNodes.IsWall(node);

        // Every step returned. The route is safe and the city map is reachable.
        Mark("done ok");
        LastLinks = links.Count;
        var line = FormattableString.Invariant(
            $"type={typeName} (valueType={valueType}) node ({pos.x:F2}, {pos.y:F2}, {pos.z:F2}) wall={wall} corners={corners}/{mine.Count} links={links.Count}/{reported} firstNeighbourCorners={neighbourVerts}");
        API.LogInfo("[SF6Access] Navmesh ignition: THE ROUTE RAN — " + line +
                    ". A city map is reachable from here.");
        return (Verdict.Ok, line);
    }
#pragma warning restore CS0162

    /// <summary>Say whether the node is a struct or a class. That single word decides how a
    /// returned value may be read at all, and it is a fact about the TYPE, so it is safe to
    /// ask before anything touches the instance.</summary>
    private static string TryValueType(TypeDefinition td)
    {
        try { return td == null ? "?" : td.IsValueType() ? "yes" : "no"; }
        catch (Exception ex) { return "? (" + ex.GetType().Name + ")"; }
    }

    /// <summary>Write the node type's members to a file. Signatures and field NAMES only: no
    /// value is read off the instance, so this step can never be the one that dies, and what
    /// it leaves behind is what the next attempt is designed from.</summary>
    private static void DumpType(TypeDefinition td, string note)
    {
        var sb = new StringBuilder();
        sb.AppendLine(FormattableString.Invariant($"navmesh node type (generation {PROBE_GEN})"));
        sb.AppendLine("what: " + note);
        try
        {
            if (td == null) { sb.AppendLine("type definition: null"); }
            else
            {
                sb.AppendLine("full name: " + (td.GetFullName() ?? "?"));
                sb.AppendLine("is value type: " + TryValueType(td));
                sb.AppendLine();
                var fields = td.GetFields();
                sb.AppendLine(FormattableString.Invariant($"fields ({fields?.Count ?? 0}):"));
                if (fields != null)
                {
                    foreach (var f in fields)
                    {
                        try { sb.AppendLine("  " + (f.Name ?? "?")); } catch { }
                    }
                }
                sb.AppendLine();
                ObjectDumper.DumpMethodSignatures(sb, td, "");
            }
        }
        catch (Exception ex) { sb.AppendLine("dump failed: " + ex.Message); }
        Write(TYPE_FILE, sb.ToString());
    }

    /// <summary>Put what the map needs on disk BEFORE the steps that might kill the process.
    /// A crash at step 9 then still leaves a real polygon and a real link count to design
    /// from, instead of only the number of the step that died.</summary>
    private static void WriteEssentials(string typeName, string valueType, int corners,
        List<(float x, float y, float z)> mine, int links, int reported, int neighbourVerts)
    {
        var sb = new StringBuilder();
        sb.AppendLine(FormattableString.Invariant($"navmesh essentials (generation {PROBE_GEN})"));
        sb.AppendLine("node type: " + typeName);
        sb.AppendLine("is value type: " + valueType);
        sb.AppendLine(FormattableString.Invariant($"corners reported: {corners}"));
        sb.AppendLine(FormattableString.Invariant($"corners read: {mine.Count}"));
        foreach (var v in mine)
            sb.AppendLine(FormattableString.Invariant($"  ({v.x:F2}, {v.y:F2}, {v.z:F2})"));
        sb.AppendLine(FormattableString.Invariant($"links read: {links} of {reported} reported"));
        sb.AppendLine(FormattableString.Invariant($"corners on linked node 0: {neighbourVerts}"));
        Write(ESSENTIAL_FILE, sb.ToString());
    }

    private static void Write(string name, string text)
    {
        try
        {
            Directory.CreateDirectory(ObjectDumper.DumpDir);
            File.WriteAllText(Path.Combine(ObjectDumper.DumpDir, name), text);
        }
        catch (Exception ex)
        {
            API.LogWarning("[SF6Access] Navmesh ignition: could not write " + name + ": " + ex.Message);
        }
    }

    private static (Verdict, string) Unreadable(int step)
    {
        // Did not crash — it simply has nothing to say. That is not a trip: the route may
        // simply not be available here (no city loaded, standing on nothing).
        Mark($"unreadable step={step}");
        string why = NavMeshNodes.Route;
        API.LogWarning($"[SF6Access] Navmesh ignition: stopped at step {step}: {why}");
        return (Verdict.Unreadable, step.ToString());
    }
}
