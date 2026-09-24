using System.Numerics;
using AiBehavior.Core;

namespace AiBehavior.Core.Tests;

/// <summary>验证新增的有限导航反馈不会扩大为无界路线请求。</summary>
internal static partial class Program
{
    /// <summary>无掩体且卧姿受限时只允许合法、有限、带冷却的撤离候选。</summary>
    private static void EscapeFallbackBoundaries()
    {
        var fallback = new EscapeFallback();
        Vector3 bot = new(10, 0, 0); // 与危险方向相距十米。
        Vector3 threat = Vector3.Zero; // 仅使用已知事件快照。
        Check(!fallback.TryPlan(false, bot, threat, 10, out _), "prone available does not escape"); // 未证明回退条件时不撤离。
        Check(fallback.TryPlan(true, bot, threat, 10, out Vector3 first), "first escape candidate"); // 首次允许短距离撤离。
        Check(first.X > bot.X && Vector3.Distance(first, bot) <= 6, "escape increases threat distance within bound"); // 撤离方向与范围必须合理。
        Check(!fallback.TryPlan(true, bot, threat, 11, out _), "escape cooldown"); // 一秒后不能重复申请。
        Check(fallback.TryPlan(true, bot, threat, 13, out Vector3 second), "alternate escape candidate"); // 第二次应避开同一条线。
        Check(Vector3.Distance(first, second) > 2 && Vector3.Distance(second, bot) <= 6, "alternate does not repeat blocked path"); // 新候选实际改变方向。
        Check(!fallback.TryPlan(true, bot, threat, 100, out _), "escape hard cap"); // 长时间危险也不能无限重算。
        fallback.StartDanger(); // 独立危险窗口可以重新使用有限额度。
        Check(fallback.TryPlan(true, bot, threat, 101, out _), "fresh danger resets escape"); // 新窗口不继承已用名额。
        Check(!AdaptiveMovement.TryEscapePoint(bot, bot, 0, out _), "zero threat direction rejected"); // 不随机制造方向。
    }

    /// <summary>只有同一目标短期连续挡枪才触发换位，切换目标后重新计数。</summary>
    private static void BlockedShotRepositionBoundaries()
    {
        var feedback = new BlockedShotFeedback();
        Check(!feedback.Observe("player", true, 10, out _), "first block insufficient"); // 单次挡枪不浪费路径额度。
        Check(!feedback.Observe("player", true, 11, out _), "second block insufficient"); // 必须连续三次。
        Check(feedback.Observe("player", true, 12, out int first) && first == 0, "third block first side"); // 固定先尝试一侧。
        Check(!feedback.Observe("player", true, 13, out _), "reposition cooldown active"); // 冷却内不能触发第二次。
        Check(!feedback.Observe("player", true, 20, out _), "stale blocks reset streak"); // 隔久的阻挡不拼接。
        Check(!feedback.Observe("player", true, 21, out _), "new streak requires three"); // 两次仍不足。
        Check(feedback.Observe("player", true, 22, out int second) && second == 1, "second side after cooldown"); // 第二次换另一侧。
        for (int index = 0; index < 10; index++) Check(!feedback.Observe("player", true, 40 + index, out _), "reposition hard cap"); // 同目标不能无限换位。
        Check(!feedback.Observe("new-player", true, 60, out _), "new target resets streak"); // 换目标不能沿用旧次数。
        Check(!feedback.Observe("new-player", true, 61, out _), "new target second block"); // 新目标同样需要三个阻挡。
        Check(feedback.Observe("new-player", true, 62, out int reset) && reset == 0, "new target can reposition"); // 新上下文可以重新尝试。
        Check(AdaptiveMovement.TrySideStep(Vector3.Zero, new Vector3(0, 0, 10), 0, out Vector3 left) &&
            AdaptiveMovement.TrySideStep(Vector3.Zero, new Vector3(0, 0, 10), 1, out Vector3 right) && left.X == -right.X, "side steps oppose"); // 两个候选分处玩家线两侧。
        Check(!AdaptiveMovement.TrySideStep(Vector3.Zero, new Vector3(1, 0, 0), 0, out _), "point blank side step rejected"); // 贴脸时不横移穿越目标。
    }

    /// <summary>短期失败区只能压制相同近邻，过期和环形替换后允许重新尝试。</summary>
    private static void SearchFailureMemoryBoundaries()
    {
        var memory = new SearchFailureMemory();
        Check(!memory.Rejects(Vector3.Zero, 0), "empty search failure memory"); // 空缓存不能误拒绝原点。
        for (int index = 0; index < 3; index++) memory.Record(new Vector3(index * 20, 0, 0), 10); // 三个独立区域分别记忆。
        for (int index = 0; index < 3; index++) Check(memory.Rejects(new Vector3(index * 20 + 3, 0, 0), 12), "failed region skipped"); // 五米内候选跳过。
        Check(!memory.Rejects(new Vector3(6, 0, 0), 12), "different region remains searchable"); // 六米外仍可搜索。
        Check(!memory.Rejects(Vector3.Zero, 25), "failure expiry restores search"); // 动态门变化后重新验证。
        memory.Record(Vector3.Zero, 30); // 重建三个容量的环形顺序。
        memory.Record(new Vector3(20, 0, 0), 30);
        memory.Record(new Vector3(40, 0, 0), 30);
        memory.Record(new Vector3(60, 0, 0), 30); // 第四处替换最旧记录。
        Check(!memory.Rejects(Vector3.Zero, 31), "oldest failure replaced"); // 容量不能随长期战局扩大。
        Check(memory.Rejects(new Vector3(60, 0, 0), 31), "newest failure retained"); // 最新失败仍受保护。
        memory.Clear(); // 玩家情境退出时清理。
        Check(!memory.Rejects(new Vector3(60, 0, 0), 31), "clear removes failed region"); // 旧地图记录不能跨局。
    }
}
