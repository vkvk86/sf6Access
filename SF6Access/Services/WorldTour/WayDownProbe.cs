using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using REFrameworkNET;
using SF6Access.Services;
using static SF6Access.Services.WorldTour.LevelRoutePlanner;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>Shift+F5 — “从这里到底怎么下去？” 一次按键，把所有下去的手段全部测量一遍。</b>
///
/// <para><b>为什么需要它。</b> 2026-10-07，纳夏尔旧城区：任务目标在玩家下方 8 米，导航先说
/// “六百多米”，后说“十八米”，玩家走过去撞上一堵高墙。两个数字都出自真实测量，但它们
/// 回答的不是同一个问题——一个是“沿路走过去有多远”，一个是“到了那个落脚点之后离目标还
/// 有多远”。玩家没有办法核对，只能猜该信哪个。猜不是导航。</para>
///
/// <para><b>它做什么。</b> 把“下去”这件事拆成候选，逐个用 navmesh 问两个问题，只报告两个
/// 问题都答“是”的那一条：
/// <list type="number">
/// <item>从玩家站的地方，能不能走到这个手段的<b>入口</b>（梯子上端／楼梯口／升降机这层）；</item>
/// <item>从这个手段的<b>出口</b>（梯子下端／楼梯底／升降机那层），能不能走到目标。</item>
/// </list>
/// 两条都通才叫“通”。只走完第一条的那是“走得过去、下不来”，正是玩家撞墙的那种。</para>
///
/// <para><b>候选零号：什么都不用，直接走。</b> 这条专门回答“六百米是不是正确路线”。如果
/// navmesh 说纯走路可达，探测会直说“不用找路，直接走就能到，N 米”——那么那个 N 就是
/// 正确答案，任何“更近的落脚点”都只是在描述半路上的某个点。</para>
///
/// <para><b>它不做什么。</b> 不改任何一条现有引导，不接管导航，不自动行走。它是加法：一个
/// 按键、一份可以自己核对的清单。测量期间导航照旧说话，探测只在结束时插一句结论。</para>
///
/// <para><b>代价。</b> 每个候选两次 navmesh 查询，全局限流器每 250 ms 放行一次，所以十个
/// 候选约五秒。期间先播报“正在探测，共 N 个候选”，绝不静默。</para>
/// </summary>
internal static class WayDownProbe
{
    /// <summary>一种“下去的手段”。<see cref="Walk"/> 是零号候选：不用任何手段。</summary>
    internal enum Kind { Walk, Ladder, Stair, Jump, Lift, Drop }

    private sealed class Cand
    {
        internal Kind Kind;

        /// <summary>入口：玩家要先走到的那一端。</summary>
        internal float Ax, Ay, Az;

        /// <summary>出口：动作做完后所站的那一端。</summary>
        internal float Bx, By, Bz;

        /// <summary>0 = 两段都没问，1 = 已问入口，2 = 两段都问完。</summary>
        internal int Stage;

        /// <summary>玩家到入口的实测路长；<see cref="float.NaN"/> = 走不到。</summary>
        internal float InM = float.NaN;

        /// <summary>出口到目标的实测路长；<see cref="float.NaN"/> = 走不到。</summary>
        internal float OutM = float.NaN;

        internal bool InOk, OutOk;

        /// <summary>两段都通。</summary>
        internal bool Through => InOk && OutOk;

        /// <summary>走过去的总长：入口那段 + 手段自身的跨度 + 出口那段。</summary>
        internal float TotalM
        {
            get
            {
                float span = MathF.Sqrt((Ax - Bx) * (Ax - Bx) + (Ay - By) * (Ay - By) + (Az - Bz) * (Az - Bz));
                return (float.IsNaN(InM) ? 0f : InM) + (Kind == Kind.Walk ? 0f : span) + (float.IsNaN(OutM) ? 0f : OutM);
            }
        }
    }

    private static readonly List<Cand> Cands = new();

    private static bool _running;
    private static bool _timedOut;
    private static int _i;
    private static long _startedAt;
    private static float _px, _py, _pz, _tx, _ty, _tz;

    // --- 径向地面扫描 ---
    private static bool _scanning;
    private static int _dirIdx;
    private static readonly List<Cand> Scanned = new();

    // --- 目标的两个高度：标记自己说的，和它脚下的地 ---
    private static float _tyRaw, _tyGround;
    private static int _pass;

    /// <summary>第一遍量到的最好的那半条路。第二遍是为了找一个更好的，不是为了丢掉它——
    /// 万一第二遍问的那个高度也没通，能下去这件事仍然是真的，仍然要说。</summary>
    private static Cand _kept;

    /// <summary>扫描多少个方向。楼梯扫描报告里用的是十六个，方位词的精度也在这附近。</summary>
    private const int ScanDirs = 16;

    /// <summary>每个方向上打射线的距离，单位米。半米起步是为了不让玩家脚边的一块地砖被当成
    /// 一处落差；十一米半收尾是 DropEdge 那种“近处可跳边缘”的量级。</summary>
    private static readonly float[] ScanRadii =
        { 1.5f, 2.5f, 3.5f, 4.5f, 5.5f, 6.5f, 7.5f, 8.5f, 9.5f, 10.5f, 11.5f };

    /// <summary>低于这个落差不算“下去”，交给台阶和跨跳去处理；探测只关心换层。</summary>
    private const float MinDescentM = 2.0f;

    /// <summary>高过这个的就是墙不是地——玩家走不过去，继续往那个方向探没有意义。</summary>
    private const float BlockRiseM = 1.5f;

    /// <summary>Beyond this the probe is describing somewhere the player has left. Long enough for
    /// twenty candidates at the shared limiter's rate, with room to spare.</summary>
    private const long GiveUpMs = 30000;

    /// <summary>探测正在进行：导航照旧，只是多了一个后台测量。</summary>
    internal static bool Running => _running;

    /// <summary><b>按键：重新收集候选并开始测量。</b> 玩家坐标和目标都自己读——和导航读的是
    /// 同一份，所以探测回答的正是导航在回答的那个问题，不会是另一个问题的答案。</summary>
    internal static bool Start()
    {
        var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        if (!me.ok)
        {
            // Logged, not just spoken: a spoken sentence is gone in two seconds and cannot be
            // grepped. Everything that stops a probe has to survive in the log.
            API.LogInfo("[SF6Access] Way down: not started - the player's position is unreadable");
            ScreenReaderService.Speak(Get("probe_no_player", "Cannot read your position, the probe did not start"), interrupt: true);
            return false;
        }
        var target = MissionTargetService.Find();
        if (!target.Ok)
        {
            API.LogInfo("[SF6Access] Way down: not started - there is no mission objective to probe");
            ScreenReaderService.Speak(Get("probe_no_target", "There is no objective to probe right now"), interrupt: true);
            return false;
        }
        // <b>目标有两个高度，两个都要问。</b> 任务标记的 Y 挂在设计者挑的那根骨头上——脚底、
        // 胸口，或者干脆是空中的一点，游戏从不保证它下面有地。2026-10-07 这一幕的教训：
        // 八处落差全部实测，落得最低的三处地面都在 -9.8，而目标标称 -8.8。差一米，navmesh
        // 上就没有节点，于是八条路的第二段无一例外地 NO ROUTE。那不是八条都不通，是问的
        // 那个点根本不在地上。
        _tyRaw = target.Y;
        _tyGround = FloorUnder(target.X, target.Y, target.Z);
        _pass = 0;
        Start(me.x, me.y, me.z, target.X, _tyRaw, target.Z);
        return true;
    }

    /// <summary><b>一个点脚下的真地面。</b> 从略高于它的地方往下打，够远，好接住悬在坑上的
    /// 那种标记。射线跑不了或者下面什么都没有时原样退回——那不是“地面就是这里”，
    /// 是“量不出来”，二者不能混。</summary>
    private static float FloorUnder(float x, float y, float z)
        => FieldRayCaster.TryFloorBelow(x, y + DropEdge.ProbeAboveM, z, DropEdge.ProbeDropM, out float fy)
           && float.IsFinite(fy) ? fy : y;

    private static void Start(float px, float py, float pz, float tx, float ty, float tz)
    {
        _px = px; _py = py; _pz = pz;
        _tx = tx; _ty = ty; _tz = tz;
        Cands.Clear();
        _i = 0;
        _running = true;
        _startedAt = Environment.TickCount64;

        _timedOut = false;
        _kept = null;
        Collect();

        // 扫描先行：登记过的通道可能是空的（这一幕就是——梯子和升降机一条都没有），而真正的
        // 下去之处往往只是“一块更低的地”，从没被任何目录登记过。先自己把周围量一遍，再拿
        // 全部候选去问 navmesh。扫描期间照样说话，绝不静默。
        _scanning = true;
        _dirIdx = 0;
        Scanned.Clear();

        API.LogInfo(string.Format(CultureInfo.InvariantCulture,
            "[SF6Access] Way down: probing from ({0:F1}, {1:F1}, {2:F1}) to ({3:F1}, {4:F1}, {5:F1}) - " +
            "{6} candidates registered, {7:F1} m of height to lose - sweeping {8} directions",
            px, py, pz, tx, ty, tz, Cands.Count, py - ty, ScanDirs));

        ScreenReaderService.Speak(Get("probe_scan", "Sweeping the ground around you: {0} directions", ScanDirs), interrupt: true);
    }

    /// <summary><b>收集全部候选。</b> 纯内存操作，一次做完：场景里登记过的每一条通道，
    /// 加上已经测量到的可跳边缘，加上零号的“直接走”。</summary>
    private static void Collect()
    {
        // --- 零号：什么都不用，直接走过去。这一条回答“那个大数字是不是正确路线” ---
        Cands.Add(new Cand { Kind = Kind.Walk, Ax = _tx, Ay = _ty, Az = _tz, Bx = _tx, By = _ty, Bz = _tz });

        // --- 场景里登记过的通道。LadderCatalog 只在被扫过之后才有内容，而扫没扫过取决于
        //     导航有没有跑过；这里补一次，免得“0 条通道”只是“还没扫”而不是“真的没有”。 ---
        if (LadderCatalog.Links.Count == 0) SweepLadders();

        var links = new List<Link>(LadderCatalog.Links);
        links.AddRange(StairCatalog.Links);
        links.AddRange(RouteStairFinder.Links);
        links.AddRange(JumpLinkCatalog.Links);
        LiftCatalog.AddLinksTo(links, WorldTourStateService.CityId);

        bool goingDown = _ty < _py - 0.5f;
        foreach (var l in links)
        {
            // 下去时先走到高的那端、从低的那端出来；上去时反过来。方向错了的话两个问题
            // 都会答“不通”，于是这条自然被淘汰——不需要额外的规则。
            var entry = goingDown ? l.High : l.Low;
            var exit = goingDown ? l.Low : l.High;

            var kind = l.Lift ? Kind.Lift : l.Jump ? Kind.Jump : l.Stair ? Kind.Stair : Kind.Ladder;
            Cands.Add(new Cand
            {
                Kind = kind,
                Ax = entry.X, Ay = entry.Y, Az = entry.Z,
                Bx = exit.X, By = exit.Y, Bz = exit.Z,
            });
        }

        // --- 已经测量到的可跳边缘。落点由 DropEdgeFinder 实测过，不是估算。 ---
        if (DropEdgeFinder.HasEdge)
        {
            Cands.Add(new Cand
            {
                Kind = Kind.Drop,
                Ax = DropEdgeFinder.X, Ay = DropEdgeFinder.Y, Az = DropEdgeFinder.Z,
                Bx = DropEdgeFinder.LandX, By = DropEdgeFinder.LandY, Bz = DropEdgeFinder.LandZ,
            });
        }
    }

    /// <summary><b>把最有希望的排到前面，零号除外。</b> 出口离目标越近，这条越可能是答案。
    /// 排这个序不是为了让答案更准——是为了让<b>来不及测完的时候</b>被测过的那几条正好是最
    /// 有希望的那几条。一个跑了半截的探测，说“这几条都不通”比说“都没测”有用得多。</summary>
    private static void OrderByPromise()
    {
        if (Cands.Count < 3) return;
        var head = Cands[0];                                  // 零号：直接走，永远第一个
        var rest = Cands.GetRange(1, Cands.Count - 1);
        rest.Sort((a, b) => Gap(a).CompareTo(Gap(b)));
        Cands.Clear();
        Cands.Add(head);
        Cands.AddRange(rest);
    }

    /// <summary>这条的出口离目标还有多远（只算水平，跨层的落差另算）。</summary>
    private static float Gap(Cand c)
    {
        float dx = c.Bx - _tx, dy = c.By - _ty, dz = c.Bz - _tz;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>把梯子的扫描跑完。分批读，最多二十批，防止一个坏列表把这里拖成死循环。</summary>
    private static void SweepLadders()
    {
        LadderCatalog.BeginSweep();
        for (int pass = 0; pass < 20; pass++)
            if (LadderCatalog.Sweep()) break;
    }

    /// <summary><b>扫描阶段：每帧量一个方向的地面剖面。</b>
    ///
    /// <para>射线不是 navmesh 查询，不走那个 250 毫秒的全局限流器，所以可以在这里一次打十几
    /// 发；但它是引擎调用，仍然一帧一个方向、量到十一米半就停，不把一帧拉长。</para>
    ///
    /// <para><b>它找的是什么。</b> 一个方向上，地面从玩家脚下的高度落下去的地方。登记目录里
    /// 没有“这里有一处可以下去”这种条目——梯子和升降机才是对象，一处断崖或者一段陡坎不是。
    /// 2026-10-07 那一幕正是如此：楼梯扫描的剖面日志白纸黑字量到了方向 13/14/15 上有
    /// 7.26 / 7.05 / 7.18 米的地面，而三个目录里一条能用的都没有，于是探测报告“没有一条走
    /// 得通”。那不是没有路，是没人去量那一处。</para></summary>
    private static void PumpScan()
    {
        try
        {
            double angle = 2.0 * Math.PI * _dirIdx / ScanDirs;
            float dx = (float)Math.Sin(angle), dz = (float)Math.Cos(angle);

            float bestDrop = 0f;                 // 这个方向上量到的最大落差
            float ex = _px, ey = _py, ez = _pz;  // 入口：落差之前最后一块站得住的地面
            float lx = _px, ly = _py, lz = _pz;  // 出口：落下去之后所站的地面
            float prevR = 0f, prevY = _py;

            for (int i = 0; i < ScanRadii.Length; i++)
            {
                float r = ScanRadii[i];
                float sx = _px + dx * r, sz = _pz + dz * r;

                // True 只表示“射线跑了”。NaN 是“跑了，下面什么都没有”——那就是空气，
                // 再往远处问也没有意义，这个方向到此为止。
                if (!FieldRayCaster.TryFloorBelow(sx, _py + DropEdge.ProbeAboveM, sz,
                                                  DropEdge.ProbeDropM, out float fy)) break;
                if (!float.IsFinite(fy)) break;
                // 挡在前面的是墙不是路。
                if (fy - _py > BlockRiseM) break;

                if (_py - fy > bestDrop)
                {
                    bestDrop = _py - fy;
                    ex = _px + dx * prevR; ey = prevY; ez = _pz + dz * prevR;
                    lx = sx; ly = fy; lz = sz;
                }

                prevR = r; prevY = fy;
            }

            if (bestDrop >= MinDescentM)
            {
                Scanned.Add(new Cand
                {
                    Kind = Kind.Drop,
                    Ax = ex, Ay = ey, Az = ez,
                    Bx = lx, By = ly, Bz = lz,
                });
                API.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "[SF6Access] Way down: direction {0} drops {1:F1} m - edge ({2:F1}, {3:F1}, {4:F1}) " +
                    "onto ({5:F1}, {6:F1}, {7:F1})",
                    _dirIdx, bestDrop, ex, ey, ez, lx, ly, lz));
            }
        }
        catch (Exception ex)
        {
            // 一条射线坏了不能让整次探测消失：那一帧的归航音还在后面排着。
            API.LogError($"[SF6Access] Way down: the sweep threw on direction {_dirIdx} - {ex.Message}");
        }

        _dirIdx++;
    }

    /// <summary>扫描量完：把量到的落差并进候选，全部重新排序，然后才开始问 navmesh。</summary>
    private static void FinishScan()
    {
        _scanning = false;

        // 落点几乎重合的不重复计：一次 navmesh 查询要 250 毫秒的限流额度，值得省。
        foreach (var s in Scanned)
        {
            bool dup = false;
            foreach (var c in Cands)
                if (MathF.Abs(c.Bx - s.Bx) < 1.5f && MathF.Abs(c.By - s.By) < 1.5f && MathF.Abs(c.Bz - s.Bz) < 1.5f)
                { dup = true; break; }
            if (!dup) Cands.Add(s);
        }
        OrderByPromise();

        API.LogInfo(string.Format(CultureInfo.InvariantCulture,
            "[SF6Access] Way down: sweep found {0} way(s) down; {1} candidates in all",
            Scanned.Count, Cands.Count));

        if (Cands.Count == 0)
        {
            Finish();
            return;
        }
        ScreenReaderService.Speak(Get("probe_start", "Probing the way through: {0} candidates", Cands.Count), interrupt: true);
    }

    /// <summary><b>每帧调用：这一帧最多问一次 navmesh。</b> 限流器是全局共享的，抢多了会把
    /// 正在进行的导航一起拖慢，所以这里永远只问一个。</summary>
    internal static void Pump()
    {
        if (!_running) return;

        if (_scanning)
        {
            if (_dirIdx >= ScanDirs) { FinishScan(); return; }
            PumpScan();
            return;
        }

        if (Environment.TickCount64 - _startedAt > GiveUpMs)
        {
            // Timed out is NOT the same as "nothing works" — and saying otherwise would be the
            // worst thing this probe could do: it would tell the player there is no way down when
            // the truth is that it ran out of time before asking. The verdict says which.
            _timedOut = true;
            API.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[SF6Access] Way down: ran out of time after {0} ms, {1} of {2} candidates measured",
                GiveUpMs, _i, Cands.Count));
            Finish();
            return;
        }

        if (_i >= Cands.Count) { Finish(); return; }

        var c = Cands[_i];

        if (c.Stage == 0)
        {
            var a = FieldNavPath.TryConnect(_px, _py, _pz, c.Ax, c.Ay, c.Az);
            // 被限流不是答案：这一帧什么都别说，下一帧再问同一个。
            if (a.Throttled) return;
            c.InOk = a.Ok && a.Exists && !a.StartOffMesh;
            c.InM = c.InOk ? a.PathM : float.NaN;
            // 零号候选只有一段：走得到目标就完事，没有“出口”这一问。
            c.Stage = c.Kind == Kind.Walk ? 2 : 1;
            if (c.Kind == Kind.Walk) { c.OutOk = c.InOk; c.OutM = 0f; _i++; }
            return;
        }

        if (c.Stage == 1)
        {
            var b = FieldNavPath.TryConnect(c.Bx, c.By, c.Bz, _tx, _ty, _tz);
            if (b.Throttled) return;
            c.OutOk = b.Ok && b.Exists && !b.StartOffMesh;
            c.OutM = c.OutOk ? b.PathM : float.NaN;
            c.Stage = 2;
            _i++;
        }
    }

    /// <summary><b>收尾：念结论，写一份可以自己核对的清单。</b></summary>
    private static void Finish()
    {
        // <b>第一遍两手空空、而目标的真地面和它自己报的高度不是一回事：拿地面那一版再问一遍。</b>
        // 只有第一遍一条结果都没量出来才重来——量出来了就照第一遍说，不多话。
        if (_pass == 0 && MathF.Abs(_tyGround - _tyRaw) > 0.05f && !AnyResult())
        {
            API.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[SF6Access] Way down: nothing reached the marker's own height {0:F1} - asking again about " +
                "the floor beneath it, {1:F1} ({2:+0.0;-0.0} m)", _tyRaw, _tyGround, _tyGround - _tyRaw));
            Retry(_tyGround);
            return;
        }

        _running = false;
        string spoken = Verdict();
        API.LogInfo($"[SF6Access] Way down: {spoken}");
        ScreenReaderService.Speak(spoken, interrupt: true);
        Write(spoken);
    }

    /// <summary><b>这一遍里最好的那半条路</b>：走得到入口、也真能下去，只是落地之后到目标
    /// 还差一段。落地离目标最近的那个优先。</summary>
    private static Cand BestDown()
    {
        Cand best = null;
        foreach (var c in Cands)
        {
            if (!c.InOk || c.OutOk || c.Kind == Kind.Walk) continue;
            if (best == null || Gap(c) < Gap(best)) best = c;
        }
        return best;
    }

    /// <summary><b>这一遍量出了一条完整的路吗。</b>
    ///
    /// <para>只看“两段都通”。<b>“走得到、下得去，只是落地之后到目标不通”不算结果</b>——
    /// 2026-10-07 第四轮就栽在这里：第一遍量出一条半截路，于是这句判成“有结果”，第二遍
    /// 永远不跑，文件里留着 <c>this run asked about -8.8</c>，而真正的地面 -9.8 一次也没
    /// 被问过。半截路本身就是“还没问对”的症候，把它当成问对了，正好把修正挡在门外。</para></summary>
    private static bool AnyResult()
    {
        foreach (var c in Cands)
            if (c.Through) return true;
        return false;
    }

    /// <summary><b>换一个目标高度，把已经收集好的候选重新问一遍。</b> 扫描结果与高度无关，
    /// 不必重扫；只有“出口到目标”那一问要重来。</summary>
    private static void Retry(float ty)
    {
        _kept = BestDown();
        _ty = ty;
        _pass = 1;
        _i = 0;
        _scanning = false;
        _running = true;
        _startedAt = Environment.TickCount64;
        foreach (var c in Cands)
        {
            c.Stage = 0;
            c.InOk = c.OutOk = false;
            c.InM = c.OutM = float.NaN;
            // 零号候选的两端就是目标本身，换了高度它也得跟着换。
            if (c.Kind == Kind.Walk) c.Ay = c.By = ty;
        }
    }

    /// <summary>结论。<b>零号候选优先</b>：既然什么都不用就能走到，任何“更近的落脚点”都
    /// 只是半路上的一个点，说它反而让人以为要走别的路。</summary>
    private static string Verdict()
    {
        int through = 0;
        foreach (var c in Cands) if (c.Through) through++;

        if (Cands.Count == 0)
            return Get("probe_none_candidate", "Probe failed: nothing in the scene to measure");

        var walk = Cands[0];
        if (walk.Through)
            return Get("probe_walk", "No way down needed: you can simply walk there, {0} meters", Round(walk.TotalM));

        if (through == 0 && _timedOut)
            return Get("probe_timeout",
                "Probe ran out of time: {0} candidates, only {1} measured - too early to say there is no way",
                Cands.Count, _i);

        // <b>“没有一条两段都通”和“没有一条能下去”是两件不同的事，必须分开说。</b>
        // 走得到下降点、只是落地之后到目标那段 navmesh 说不通——这不是死路，这是“先下去”。
        // 目标常常挂在另一块网格上（它自己的标记 Y 从来没被吸附过），人到了下面再问一次
        // 往往就通了。2026-10-07 那一幕，探测说的正是“没有一条能真的走通”，而真正该说的
        // 是“六米外就有一处七米落差”。把这两件事混成一句话，就是让人以为没有路。
        Cand down = BestDown() ?? _kept;
        if (down != null)
            return Get("probe_partial",
                "Probe done: {0} candidates, none of them work end to end. But one of them really does get you down: " +
                "walk {1} meters to a drop to your {2}, {3} meters down, landing {4} meters from the objective. " +
                "The walk from the landing to the objective has no route yet - ask again once you are down there",
                Cands.Count, Round(down.InM), Bearing(down.Ax, down.Az),
                Round(down.Ay - down.By), Round(Gap(down)));

        if (through == 0)
            return Get("probe_none", "Probe done: {0} candidates, none of them actually get you there", Cands.Count);

        Cand best = null;
        foreach (var c in Cands)
        {
            if (!c.Through) continue;
            if (best == null || c.TotalM < best.TotalM) best = c;
        }
        if (best == null) return Get("probe_none", "Probe done: {0} candidates, none of them actually get you there", Cands.Count);

        return Get("probe_best",
            "Probe done: {0} candidates, {1} of them work. Shortest is {2}: walk {3} meters to the {4}, {5}, then {6} meters from the objective, {7} meters in all",
            Cands.Count, through, KindWord(best.Kind), Round(best.InM), Bearing(best.Ax, best.Az),
            ActionWord(best.Kind), Round(best.OutM), Round(best.TotalM));
    }

    /// <summary>写文件：念过一遍就过去了，盲人玩家可以拿这份清单慢慢核对。</summary>
    private static void Write(string verdict)
    {
        try
        {
            Directory.CreateDirectory(ObjectDumper.DumpDir);
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture,
                $"way down probe {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"from ({_px:F1}, {_py:F1}, {_pz:F1})  to ({_tx:F1}, {_ty:F1}, {_tz:F1})  " +
                $"height to lose {_py - _ty:F1} m\r\n");
            if (MathF.Abs(_tyGround - _tyRaw) > 0.05f)
                sb.Append(CultureInfo.InvariantCulture,
                    $"marker says y={_tyRaw:F1} but the floor beneath it is y={_tyGround:F1} " +
                    $"({_tyGround - _tyRaw:+0.0;-0.0} m) - this run asked about {_ty:F1}\r\n");
            sb.Append("verdict: ").Append(verdict).Append("\r\n\r\n");

            foreach (var c in Cands)
            {
                sb.Append(CultureInfo.InvariantCulture,
                    $"{KindWord(c.Kind)}  " +
                    $"entry ({c.Ax:F1}, {c.Ay:F1}, {c.Az:F1})  " +
                    $"exit ({c.Bx:F1}, {c.By:F1}, {c.Bz:F1})\r\n");
                sb.Append("    walk to entry: ").Append(c.InOk ? $"{c.InM:F1} m" : "NO ROUTE").Append("\r\n");
                if (c.Kind != Kind.Walk)
                    sb.Append("    exit to target: ").Append(c.OutOk ? $"{c.OutM:F1} m" : "NO ROUTE").Append("\r\n");
                sb.Append("    total: ").Append(c.Through ? $"{c.TotalM:F1} m  ** USEABLE **" : "unuseable").Append("\r\n\r\n");
            }

            string path = Path.Combine(ObjectDumper.DumpDir, "sf6access_waydown.txt");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            API.LogInfo($"[SF6Access] Way down: list written to {path}");
        }
        catch (Exception ex)
        {
            API.LogError($"[SF6Access] Way down: could not write the list - {ex.Message}");
        }
    }

    /// <summary>从玩家到某点的方位词：复用导航自己用的那套罗盘。</summary>
    private static string Bearing(float x, float z)
    {
        var dir = new FieldDirectionService.FlatDir(x - _px, z - _pz, true);
        if (!dir.Ok) return Get("probe_bearing_unknown", "over there");
        int sector = FieldHeadingService.Sector(FieldHeadingService.HeadingDegrees(dir));
        return FieldHeadingService.SectorName(sector);
    }

    private static int Round(float m) => (int)Math.Round(m, MidpointRounding.AwayFromZero);

    private static string KindWord(Kind k) => LangFile.Get("wt.probe.kind_" + k.ToString().ToLowerInvariant(),
        k switch
        {
            Kind.Walk => "walking",
            Kind.Ladder => "a ladder",
            Kind.Stair => "a staircase",
            Kind.Jump => "a step",
            Kind.Lift => "a lift",
            _ => "a drop",
        });

    private static string ActionWord(Kind k) => LangFile.Get("wt.probe.act_" + k.ToString().ToLowerInvariant(),
        k switch
        {
            Kind.Walk => "keep walking",
            Kind.Ladder => "climb the ladder",
            Kind.Stair => "take the staircase",
            Kind.Jump => "step across",
            Kind.Lift => "ride the lift",
            _ => "jump down here",
        });

    private static string Get(string key, string fallback, params object[] args)
    {
        string s = LangFile.Get("wt.probe." + key, fallback);
        return args != null && args.Length > 0 ? string.Format(CultureInfo.InvariantCulture, s, args) : s;
    }

    /// <summary>离开场地／换目标：丢掉这次测量。</summary>
    internal static void Reset()
    {
        _running = false;
        _scanning = false;
        _dirIdx = 0;
        _i = 0;
        _pass = 0;
        Cands.Clear();
        Scanned.Clear();
    }
}
