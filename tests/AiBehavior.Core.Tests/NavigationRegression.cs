using System;
using System.Numerics;
using AiBehavior.Core;

namespace AiBehavior.Core.Tests;

/// <summary>针对海岸线导航、有限重试、活动排序及动作分阶段计时的回归。</summary>
internal static partial class Program
{
    /// <summary>相邻建筑后方的目标可合法绕路，分段保留转角，不切穿障碍。</summary>
    private static void RouteAroundObstacle()
    {
        Vector3[] path = { Vector3.Zero, new(0, 0, 25), new(8, 0, 25), new(8, 0, 0) };
        var route = new SearchRoute();
        Check(route.Load(path, path[0], path[3], true, 10, out _), "near target permits 58 meter detour");
        Near(route.Length, 58, "full route length retained");
        var output = new Vector3[SearchRoute.Capacity + 2];
        Vector3 position = path[0];
        float total = 0;
        bool final = false;
        for (int step = 0; step < 10 && !final; step++) // 按折线前进，不能用最终位置直切。
        {
            Check(route.Take(position, 10 + step * 0.1, output, out int count, out final), "detour segment available");
            float length = 0;
            for (int index = 1; index < count; index++) length += Vector3.Distance(output[index - 1], output[index]); // 转角同样消耗步长。
            Check(length <= 12.0001f, "detour step bounded");
            if (step == 0) Check(output[count - 1] == new Vector3(0, 0, 12), "may initially move away from target to avoid wall");
            total += length;
            position = output[count - 1];
        }
        Check(final && position == path[3], "detour reaches endpoint");
        Near(total, 58, "no corners skipped");
    }

    /// <summary>路线仅在时效和起点均有效时复用，失败加载或交接不留下旧路线。</summary>
    private static void RouteCacheAndInvalidation()
    {
        var route = new SearchRoute();
        Vector3[] path = { Vector3.Zero, new(100, 0, 0) };
        var output = new Vector3[SearchRoute.Capacity + 2];
        Check(route.Load(path, path[0], path[1], true, 10, out _), "cache initialized");
        Check(!route.Take(Vector3.Zero, double.NaN, output, out _, out _), "invalid time rejected");
        Check(!route.Take(Vector3.Zero, 9, output, out _, out _), "backward clock rejected");
        Check(!route.Take(new Vector3(2, 0, 0), 10, output, out _, out _), "source deviation rejected");
        Check(!route.Take(Vector3.Zero, 10, new Vector3[2], out _, out _), "short buffer cannot consume route");
        Check(route.Take(Vector3.Zero, 10, output, out int count, out _), "failed reads leave route untouched");
        Vector3 endpoint = output[count - 1];
        Check(route.Take(endpoint - Vector3.UnitX * 0.6f, 17.99, output, out count, out _), "arrival tolerance allows cached next segment");
        Check(!route.Take(output[count - 1], 18, output, out _, out _), "reads do not renew cache lifetime");
        Check(!route.Load(path, path[0], path[1], false, 20, out _), "partial route rejected");
        Check(!route.Take(Vector3.Zero, 20, output, out _, out _), "failed load clears old cache");
        Check(route.Load(path, path[0], path[1], true, 20, out _), "new request can reload");
        route.Clear(); // 与实际控制交接使用相同入口。
        Check(!route.Take(Vector3.Zero, 20, output, out _, out _), "handoff cannot replay movement");
    }

    /// <summary>起终点、部分路径、非法数据和过长绕行具有独立失败原因。</summary>
    private static void RouteValidationReasons()
    {
        var route = new SearchRoute();
        Vector3[] path = { Vector3.Zero, new(10, 0, 0) };
        Check(!route.Load(path, path[0], path[1], false, 0, out string reason) && reason == "path-incomplete", "partial path diagnosed");
        Check(!route.Load(path, new(0, 4, 0), path[1], true, 0, out reason) && reason == "path-source", "source floor mismatch diagnosed");
        Check(!route.Load(path, path[0], new(10, 4, 0), true, 0, out reason) && reason == "path-endpoint", "endpoint mismatch diagnosed");
        Vector3[] detour = { Vector3.Zero, new(0, 0, 200), new(1, 0, 200), new(1, 0, 0) };
        Check(!route.Load(detour, detour[0], detour[3], true, 0, out reason) && reason == "path-length", "oversized detour rejected");
        Check(route.Length > 360, "failed length retained for diagnostics");
        Check(!route.Load(new Vector3[129], Vector3.Zero, Vector3.Zero, true, 0, out reason) && reason == "path-corners", "corner capacity bounded");
        Check(!route.Load(new[] { Vector3.Zero, new Vector3(float.NaN, 0, 0) }, path[0], path[1], true, 0, out _), "nonfinite corner rejected");
        Check(!route.Load(path, path[0], path[1], true, double.PositiveInfinity, out _), "infinite lifetime rejected");
        Check(!route.Load(new[] { Vector3.Zero, Vector3.Zero }, Vector3.Zero, Vector3.Zero, true, 0, out _), "zero length is not movement");
    }

    /// <summary>多组曲折坡地路线的每条输出边都必须落在原折线上，总长和终点保持一致。</summary>
    private static void RouteManyCorners()
    {
        var output = new Vector3[SearchRoute.Capacity + 2];
        for (int sample = 0; sample < 40; sample++) // 覆盖多种角点数量与连续坡度。
        {
            var path = new Vector3[3 + sample];
            float expected = 0;
            for (int index = 0; index < path.Length; index++) // 交替左右转向，直切会失败。
            {
                path[index] = new Vector3(index * 2, index * 0.1f, index % 2 * 3);
                if (index > 0) expected += Vector3.Distance(path[index - 1], path[index]);
            }
            var route = new SearchRoute();
            Check(route.Load(path, path[0], path[path.Length - 1], true, 0, out _), "solved slope accepted");
            Vector3 current = path[0];
            float actual = 0;
            bool final = false;
            for (int step = 0; step < 40 && !final; step++) // 有限时钟内复用路线。
            {
                Check(route.Take(current, step * 0.01, output, out int count, out final), "polyline segment available");
                float length = 0;
                for (int index = 1; index < count; index++) // 两端都在同一原边才能证明没有跨角点抄近道。
                {
                    bool onEdge = false;
                    for (int edge = 1; edge < path.Length; edge++) // 仅测试独立检查折线几何。
                        if (PlayerThreatPolicy.SegmentDistanceSquared(output[index - 1], path[edge - 1], path[edge]) < 0.0001f && PlayerThreatPolicy.SegmentDistanceSquared(output[index], path[edge - 1], path[edge]) < 0.0001f) onEdge = true;
                    Check(onEdge, "no segment cuts across navigation corner");
                    length += Vector3.Distance(output[index - 1], output[index]);
                }
                Check(length <= 12.0001f, "path length per step bounded");
                actual += length;
                current = output[count - 1];
            }
            Check(final && current == path[path.Length - 1], "polyline reaches endpoint");
            Check(Math.Abs(actual - expected) < 0.001f, "full polyline length preserved");
        }
    }

    /// <summary>失败位置只额外尝试一轮，已到达位置和过期线索不重复工作。</summary>
    private static void SearchFailureRetry()
    {
        var progress = new SearchProgress();
        progress.CompletePoint(true); // 已访问位置不再重走。
        progress.CompletePoint(false);
        progress.CompletePoint(false);
        Check(progress.Index == 3 && progress.Reached == 1 && progress.Failed == 2, "failure is not arrival");
        Check(!progress.Retry(13, 20), "remaining clue life too short");
        Check(progress.Retry(12, 20) && progress.Index == 1, "retry skips visited point");
        progress.CompletePoint(true); // 障碍移除后可以恢复成功。
        progress.CompletePoint(false);
        Check(progress.Reached == 2 && progress.Failed == 1, "success replaces prior failure");
        Check(!progress.Retry(12, 40), "only one additional pass");
        progress.CompletePoint(true);
        Check(progress.Reached == 2 && progress.Failed == 1, "late callback cannot overrun points");
        progress.Reset();
        for (int index = 0; index < 3; index++) progress.CompletePoint(true); // 正常完成应直接退出。
        Check(!progress.Retry(0, 40) && progress.Failed == 0, "completed search never retries");
        progress.Reset();
        for (int index = 0; index < 3; index++) progress.CompletePoint(false);
        Check(!progress.Retry(double.NaN, 40) && !progress.Retry(0, double.PositiveInfinity), "invalid lifetime cannot prolong search");
    }

    /// <summary>玩家威胁在原限额中优先，但不能把所有候选强行全部激活。</summary>
    private static void ActivityPriorityKeepsLimit()
    {
        Check(ActivityPriority.Score(14400, true) < 0, "120 meter boundary protected");
        Check(ActivityPriority.Score(14400.1f, true) == 14400.1f, "outside scope unchanged");
        Check(ActivityPriority.Score(400, false) == 400, "AI interaction unchanged");
        Check(float.IsNaN(ActivityPriority.Score(float.NaN, true)), "invalid original score preserved");
        Check(ActivityPriority.Score(100, true) < ActivityPriority.Score(10000, true), "priority group ordered by distance");
        var candidates = new (int Id, float Score)[32];
        for (int index = 0; index < candidates.Length; index++) candidates[index] = (index, ActivityPriority.Score((index + 1) * 100, index >= 20)); // 十二个真实威胁竞争十个原生席位。
        // 模拟原限流器排序，不向生产代码引入排序或扩容。
        Array.Sort(candidates, (left, right) => left.Score.CompareTo(right.Score));
        int selected = 0;
        for (int index = 0; index < 10; index++) { Check(candidates[index].Id >= 20, "player threat preferred over idle"); selected++; } // 原上限仍为十。
        Check(selected == 10 && candidates[10].Score < 0, "some threats remain limited when cap reached");
    }

    /// <summary>动作慢调用按子阶段定位，父动作与日志不能重复计算同一时间。</summary>
    private static void ActionTimingBreakdown()
    {
        var profiler = new WorkProfiler();
        profiler.AdvanceFrame(1, 1, 50);
        profiler.Begin(WorkPhase.Action, 0);
        profiler.Begin(WorkPhase.ActionSearch, 2);
        profiler.Begin(WorkPhase.ActionMove, 5);
        profiler.Begin(WorkPhase.Logging, 65);
        profiler.End(65, 67);
        profiler.End(5, 75);
        profiler.End(2, 80);
        profiler.End(0, 83);
        profiler.CompleteFrame(50);
        Check(profiler.TotalFrameTicks == 83 && profiler.OverBudgetFrames == 1, "spike preserves frame total");
        Check(profiler.TotalTicks[(int)WorkPhase.ActionMove] == 68 && profiler.TotalTicks[(int)WorkPhase.ActionSearch] == 8, "native move separated from search bookkeeping");
        Check(profiler.TotalTicks[(int)WorkPhase.Action] == 5 && profiler.TotalTicks[(int)WorkPhase.Logging] == 2, "parent and log remain exclusive");
        long sum = 0;
        foreach (long ticks in profiler.TotalTicks) sum += ticks; // 新阶段不能破坏总量守恒。
        Check(sum == 83 && profiler.PeakCallTicks[(int)WorkPhase.ActionMove] == 70, "inclusive peak never added to totals");
    }
}
