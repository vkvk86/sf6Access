using System;
using System.Collections.Generic;
using System.Globalization;
using REFrameworkNET;
using static SF6Access.Services.WorldTour.LevelRoutePlanner;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>把“周围哪里能下去”量成导航能用的连接。</b>
///
/// <para><b>为什么需要它。</b> 梯子、楼梯、升降机都是场景里的<b>对象</b>，所以各有各的目录，
/// 规划器能拿到它们。而“一处更低的地”——一道断崖、一段陡坎、一个跳下去就到的平台——
/// 不是对象，从来没有目录，于是从来不进规划表。2026-10-07 纳夏尔旧城区：任务目标在玩家
/// 下方八米，<c>Level route: no plan after 0 questions over 0 ladders</c>，四个目录全是空的；
/// 而同一时刻，以玩家为心、十二米为半径的一圈里，实测站着八处落差，其中六处 navmesh 确认
/// 走得到。不是没有路，是<b>没有一种手段被登记过</b>。</para>
///
/// <para><b>它做什么。</b> 以玩家为心，十六个方向，每个方向从一米半到十一米半打射线量地面
/// 高度。一个方向上地面落下去超过 <see cref="MinDescentM"/> 的，就记成一条连接：上端是落差
/// 之前最后一块站得住的地，下端是实测的落点。就这样，没有猜测。</para>
///
    /// <para><b>代价。</b> 一次扫描二百二十四发射线。所以结果<b>缓存</b>：玩家走开
    /// <see cref="RefreshM"/> 以上、或者距上次扫描超过 <see cref="RefreshMs"/>，才重新量一遍。
    /// 射线不是 navmesh 查询，不占那个 250 毫秒的全局限流额度。</para>
///
/// <para><b>它不做什么。</b> 不改变任何一条已有连接，不接管规划。它只在
/// <see cref="LevelRoute"/> 发现自己手里一条能换层的连接都没有时才被问到——见那里的说明。
/// 有梯子可用的地方，这里一个字都不会多说。</para>
/// </summary>
internal static class DescentScan
{
    /// <summary>扫描多少个方向。楼梯扫描报告里用的也是十六个。</summary>
    private const int ScanDirs = 16;

    /// <summary>每个方向打射线的距离，单位米。半米起步，免得脚边一块地砖被当成落差。
    ///
    /// <para>近处密、远处疏，这是故意的。近处的一米决定"上沿在哪"——迈出去的那一步踩的
    /// 就是它，差半米就指错边。远处只问一件事：这一带到底有没有更低的地，所以三米一跳够了。
    ///
    /// <b>原来只到 11.5 米</b>（2026-10-07 的第一版）。梅特隆训练中心那天，目标在下方七米、
    /// 水平二十二米，而能把人送到目标那一层的落差在七米开外——刚好在射程内，所以没暴露
    /// 问题。但同样一处落差，玩家站在十一米外就完全看不见了，而"走出去两步再问一遍"对一个
    /// 看不见的人来说不是办法。射程放到二十米，代价是每多打六十四发射线，换的是不再有
    /// 这种"差一点就量到"的运气。</para></summary>
    private static readonly float[] ScanRadii =
        { 1.5f, 2.5f, 3.5f, 4.5f, 5.5f, 6.5f, 7.5f, 8.5f, 9.5f, 11f, 13f, 15.5f, 18.5f, 22f };

    /// <summary>低于这个不算换层，交给台阶和跨跳处理。</summary>
    private const float MinDescentM = 2.0f;

    /// <summary>挡在前面高过这个的是墙不是地，往那个方向再探没有意义。</summary>
    private const float BlockRiseM = 1.5f;

    private const float RefreshM = 3f;
    private const long RefreshMs = 6000;

    private static readonly List<Link> _links = new();
    private static float _cx, _cy, _cz;
    private static long _at;
    private static bool _have;

    /// <summary><b>把量到的落差加进规划表。</b> 只有调用方确认自己一条能换层的连接都没有时
    /// 才该调；有梯子可用的地方不该被这里改写。</summary>
    internal static void AddLinksTo(List<Link> links)
    {
        Ensure();
        links.AddRange(_links);
    }

    /// <summary><b>保证手里的落差数据还新鲜。</b> 归航音也会问这里，而它不能等规划器哪天
    /// 想起来调 <see cref="AddLinksTo"/>：规划失败的场景恰恰是声音最不能缺席的场景。
    /// 缓存命中就立刻返回，走动超过 <see cref="RefreshM"/> 或超过 <see cref="RefreshMs"/>
    /// 才真的重扫。扫描坏了不能让调用方跟着坏，旧结果留着用。</summary>
    internal static void Ensure()
    {
        try
        {
            Refresh();
        }
        catch (Exception ex)
        {
            API.LogError($"[SF6Access] Descent scan: threw - {ex.Message}");
        }
    }

    /// <summary>缓存还新鲜就直接用，否则重扫一遍。</summary>
    private static void Refresh()
    {
        var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        // 读不到位置就保留上一次的结果：旧的地图知识好过没有，而且这一帧不该白扫。
        if (!me.ok) return;

        if (_have)
        {
            float moved = MathF.Sqrt((me.x - _cx) * (me.x - _cx) + (me.z - _cz) * (me.z - _cz));
            if (moved < RefreshM && MathF.Abs(me.y - _cy) < RefreshM
                && Environment.TickCount64 - _at < RefreshMs) return;
        }

        Scan(me.x, me.y, me.z);
        _cx = me.x; _cy = me.y; _cz = me.z;
        _at = Environment.TickCount64;
        _have = true;
    }

    /// <summary><b>量一圈。</b> 一个方向一条：这一条的上沿是落差之前最后一块站得住的地面，
    /// 下沿是射线实测到的落点。两端都是量出来的，没有一处是估的。</summary>
    private static void Scan(float px, float py, float pz)
    {
        _links.Clear();
        int found = 0;

        for (int d = 0; d < ScanDirs; d++)
        {
            double angle = 2.0 * Math.PI * d / ScanDirs;
            float dx = (float)Math.Sin(angle), dz = (float)Math.Cos(angle);

            float bestDrop = 0f;
            float ex = px, ey = py, ez = pz;   // 上沿
            float lx = px, ly = py, lz = pz;   // 落点
            float prevR = 0f, prevY = py;

            for (int i = 0; i < ScanRadii.Length; i++)
            {
                float r = ScanRadii[i];
                float sx = px + dx * r, sz = pz + dz * r;

                // True 只表示射线跑了。NaN 是“跑了，下面什么都没有” —— 那就是空气，
                // 再往远处问也没有意义，这个方向到此为止。
                if (!FieldRayCaster.TryFloorBelow(sx, py + DropEdge.ProbeAboveM, sz,
                                                  DropEdge.ProbeDropM, out float fy)) break;
                if (!float.IsFinite(fy)) break;
                if (fy - py > BlockRiseM) break;

                if (py - fy > bestDrop)
                {
                    bestDrop = py - fy;
                    ex = px + dx * prevR; ey = prevY; ez = pz + dz * prevR;
                    lx = sx; ly = fy; lz = sz;
                }

                prevR = r; prevY = fy;
            }

            if (bestDrop < MinDescentM) continue;

            _links.Add(new Link(new Spot(lx, ly, lz), new Spot(ex, ey, ez), Drop: true));
            found++;
        }

        API.LogInfo(string.Format(CultureInfo.InvariantCulture,
            "[SF6Access] Descent scan: {0} drop(s) around ({1:F1}, {2:F1}, {3:F1})", found, px, py, pz));

        // 每条都写下来。计数只说明"有"，说明不了"在哪"——而"规划器为什么没接上"这个问题，
        // 答案永远是某个坐标，不是某个数字。上沿和落点都是量出来的，一并写，省得下次再从
        // 头推一遍。
        for (int i = 0; i < _links.Count; i++)
        {
            var l = _links[i];
            API.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[SF6Access] Descent scan: drop #{0}: lip ({1:F1}, {2:F1}, {3:F1}) -> landing ({4:F1}, {5:F1}, {6:F1}), {7:F1} m down",
                i, l.High.X, l.High.Y, l.High.Z, l.Low.X, l.Low.Y, l.Low.Z, l.High.Y - l.Low.Y));
        }
    }

    /// <summary><b>玩家已经站到落差上沿时要往哪一边迈出去。</b>
    ///
    /// <para>归航音指向的是“这一步的终点”。走到上沿，那个终点就在脚下，水平差归零，
    /// 左右声道都回到中间——声音还在响，方向却没有了。偏偏这一刻是最需要方向的一刻：
    /// 脚下是一处九米的落差，往前一步是下去，往后一步是白走。</para>
    ///
    /// <para>所以这里给出<b>落点</b>：上沿离玩家最近的那条落差，它实测的着陆点在哪。
    /// 归航音朝那儿响，玩家朝那儿迈，落下去之后规划重新做，声音转向真正的目标。
    /// 这就是“归航音带着走过去”的最后一米——不是告诉他在哪儿，是告诉他往哪儿迈。</para></summary>
    internal static bool TryLandingNear(float px, float py, float pz, out float lx, out float ly, out float lz)
    {
        lx = ly = lz = 0f;
        if (_links.Count == 0) return false;

        float best = float.MaxValue;
        bool found = false;
        float bx = 0f, by = 0f, bz = 0f;
        // 按<b>上沿</b>离玩家的远近挑，不是按落点：挑的是玩家脚下踩着的那一条。
        // 落点离得近不代表那是他站的地方——上沿才是。
        foreach (var l in _links)
        {
            float dx = l.High.X - px, dz = l.High.Z - pz;
            float d = dx * dx + dz * dz;
            if (d >= best) continue;
            best = d;
            bx = l.Low.X; by = l.Low.Y; bz = l.Low.Z;
            found = true;
        }
        // 上沿得真在他脚下：三米之外那条是别人的落差，指过去就是把他带错边。
        if (!found || best > LipM * LipM) return false;
        lx = bx; ly = by; lz = bz;
        return true;
    }

    /// <summary>算“他脚下踩着的就是这条落差的上沿”的容许范围，单位米。
    ///
    /// <para>比扫描半径小得多，但比“贴着站”宽：归航音问的时候，玩家可能已经站在上沿，
    /// 也可能差一两米，而<b>目标在下方这个事实已经确认</b>，此时指一条六米外的落点仍然
    /// 是对的——他先走过去，再迈下去。再宽就开始指到别人的落差上去了。</para></summary>
    private const float LipM = 6f;

    internal static void Reset()
    {
        _links.Clear();
        _have = false;
    }
}
