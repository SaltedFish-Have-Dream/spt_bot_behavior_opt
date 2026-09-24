using System;
using System.Numerics;
using AiBehavior.Core;

namespace AiBehavior.Core.Tests;

/// <summary>独立验证玩家压力、掩体失败记忆、搜索节奏与动作连续性的边界。</summary>
internal static partial class Program
{
    /// <summary>同颗子弹的近弹和命中回调不会成倍加压，命中升级仍立即触发高压。</summary>
    private static void PressureEventMerging()
    {
        var pressure = new SuppressionPressure();
        Check(pressure.Observe(true, PressureEvent.NearBullet, 10), "near bullet adds pressure");
        Near(pressure.Value, 0.9f, "first near bullet weight");
        Check(pressure.Observe(true, PressureEvent.Hit, 10.1), "hit upgrades same window");
        Check(pressure.Level == PressureLevel.Pinned, "hit upgrade immediately pins");
        float first = pressure.Value; // 保存首次事件的结果，重复回调不能再次叠加。
        for (int index = 0; index < 1000; index++) Check(!pressure.Observe(true, PressureEvent.Hit, 10.1), "same window hit merged"); // 模拟补丁重复通知。
        Near(pressure.Value, first, "duplicate callbacks cannot inflate pressure");
        Check(pressure.Observe(true, PressureEvent.Hit, 10.25), "new window accepts real next hit");
        Check(pressure.Value <= 4, "pressure has a hard cap");
    }

    /// <summary>连续真实近弹能形成压制，不把单次枪声当作持续受压。</summary>
    private static void PressureBurstAndQuiet()
    {
        var pressure = new SuppressionPressure();
        for (int index = 0; index < 5; index++) pressure.Observe(true, PressureEvent.NearBullet, 20 + index * 0.3); // 四分之一秒窗口外的连续近弹。
        Check(pressure.Level == PressureLevel.Pinned, "sustained nearby fire pins");
        pressure.Advance(26.2); // 最后一发后五秒，压力不能继续阻止原有推进规则。
        Near(pressure.Value, 0, "full quiet window clears bounded pressure");
        Check(pressure.Level == PressureLevel.Calm, "quiet pressure returns to calm");
    }

    /// <summary>衰减按实际时间计算，相同事件在不同帧率下产生同一结果。</summary>
    private static void PressureFrameRateIndependence()
    {
        foreach (int fps in new[] { 30, 60, 144 }) // 独立覆盖低帧率和高帧率。
        {
            var pressure = new SuppressionPressure();
            pressure.Observe(true, PressureEvent.Hit, 10);
            for (int frame = 1; frame <= fps * 2; frame++) pressure.Advance(10 + (double)frame / fps); // 不按帧固定扣减。
            Check(Math.Abs(pressure.Value - 0.9f) < 0.0001, "pressure decay independent of fps");
            Check(pressure.Level == PressureLevel.Alert, "same pressure tier across fps");
        }
    }

    /// <summary>高压的退出缓冲避免阈值抖动，非法、倒序或非玩家事件不影响状态。</summary>
    private static void PressureSourceAndHysteresis()
    {
        var pressure = new SuppressionPressure();
        Check(!pressure.Observe(false, PressureEvent.Hit, 10), "AI damage ignored");
        Check(!pressure.Observe(true, PressureEvent.AllyWarning, 10), "ally warning is not personal pressure");
        Check(!pressure.Observe(true, (PressureEvent)99, 10), "unknown event ignored");
        pressure.Observe(true, PressureEvent.Hit, 10);
        pressure.Advance(11);
        Check(pressure.Level == PressureLevel.Pinned, "pressure stays pinned below entry threshold");
        float before = pressure.Value;
        Check(!pressure.Observe(true, PressureEvent.Hit, 10.5), "old event cannot refresh pressure");
        Check(!pressure.Observe(true, PressureEvent.Hit, double.NaN), "invalid event time ignored");
        pressure.Advance(double.PositiveInfinity);
        Near(pressure.Value, before, "invalid time leaves state unchanged");
        pressure.Advance(11.875);
        Check(pressure.Level == PressureLevel.Alert, "exit threshold releases high pressure");
        pressure.Clear();
        Check(pressure.Value == 0 && pressure.Level == PressureLevel.Calm, "context clear removes pressure");
        Check(pressure.Observe(true, PressureEvent.Hit, 0), "fresh context accepts new clock");
    }

    /// <summary>只有已到达位置的玩家命中有效，封锁范围与过期边界精确且不被读取续期。</summary>
    private static void FailedCoverEvidenceAndExpiry()
    {
        var memory = new FailedCoverMemory();
        Check(!memory.ObserveHit(false, true, Vector3.Zero, 10), "AI hit cannot reject cover");
        Check(!memory.ObserveHit(true, false, Vector3.Zero, 10), "hit on route cannot reject destination");
        Check(memory.ObserveHit(true, true, Vector3.Zero, 10), "player hit at cover recorded");
        Check(memory.Rejects(new Vector3(2, 0, 0), 10), "two meter boundary rejected");
        Check(!memory.Rejects(new Vector3(2.01f, 0, 0), 10), "outside failed region remains eligible");
        Check(!memory.Rejects(new Vector3(0, 3, 0), 10), "another floor not rejected by horizontal overlap");
        Check(memory.Rejects(Vector3.Zero, 21.999), "cover remains rejected before expiry");
        Check(!memory.Rejects(Vector3.Zero, 22), "read does not renew expiry");
    }

    /// <summary>固定四项会淘汰最早记录，相近重复命中合并，长局不会扩容。</summary>
    private static void FailedCoverCapacityAndStorm()
    {
        var memory = new FailedCoverMemory();
        for (int index = 0; index < 4; index++) memory.ObserveHit(true, true, new Vector3(index * 10, 0, 0), 10 + index); // 建立四个独立失效区域。
        memory.ObserveHit(true, true, Vector3.One, 14); // 第一处附近的新命中刷新同一槽，不挤掉其他位置。
        Check(memory.Rejects(new Vector3(10, 0, 0), 14), "nearby hit merged into existing slot");
        memory.ObserveHit(true, true, new Vector3(40, 0, 0), 15);
        Check(!memory.Rejects(new Vector3(10, 0, 0), 15), "oldest entry evicted at capacity");
        Check(memory.Rejects(Vector3.One, 15), "recent refreshed entry retained");
        for (int index = 0; index < 10000; index++) memory.ObserveHit(true, true, new Vector3(index * 10, 0, 0), 30 + index * 0.001); // 长时间命中流仍只保存四个位置。
        int rejected = 0;
        for (int index = 0; index < 10000; index++) if (memory.Rejects(new Vector3(index * 10, 0, 0), 40)) rejected++; // 独立统计可见记录数。
        Check(rejected == FailedCoverMemory.Capacity, "storm leaves exactly four rejected regions");
    }

    /// <summary>非法输入不能进入几何查询，旧命中不能倒退有效期限，清理幂等。</summary>
    private static void FailedCoverInvalidAndClear()
    {
        var memory = new FailedCoverMemory();
        Check(!memory.ObserveHit(true, true, new Vector3(float.NaN), 1), "invalid cover position ignored");
        Check(!memory.ObserveHit(true, true, Vector3.Zero, double.PositiveInfinity), "invalid cover time ignored");
        memory.ObserveHit(true, true, Vector3.Zero, 10);
        Check(!memory.ObserveHit(true, true, Vector3.Zero, 9), "old hit cannot shorten cover expiry");
        Check(!memory.Rejects(Vector3.Zero, 9), "future cover record not read before its event");
        Check(memory.Rejects(new Vector3(float.NaN), 10), "invalid projected candidate rejected");
        memory.Clear(); memory.Clear(); // 连续退出和注销不需要特殊顺序。
        Check(!memory.Rejects(Vector3.Zero, 10), "clear removes failed cover entries");
    }

    /// <summary>停看只发生在已验证路线的近段，重复执行不会续期或消费额外次数。</summary>
    private static void SearchObservationWindow()
    {
        var pacing = new SearchPacing();
        pacing.SetArea("player", new Vector3(15, 0, 0));
        Check(!pacing.TryPause(true, Vector3.Zero, 18.01f, 0.3f, 10, 40, out _), "long detour cannot be treated as close contact");
        Check(pacing.TryPause(true, Vector3.Zero, 18, 0.3f, 10, 40, out bool started) && started, "close route begins observation");
        double until = pacing.PauseUntil;
        for (int index = 1; index < 60; index++) Check(pacing.TryPause(true, Vector3.Zero, 18, 0.3f, 10 + index * 0.01, 40, out started) && !started, "waiting does not restart observation"); // 多帧保持同一窗口。
        Check(pacing.PauseUntil == until && pacing.Count == 1, "observation duration and count stable");
        Check(!pacing.TryPause(true, Vector3.Zero, 18, 0.3f, until, 40, out _), "exact end resumes movement");
    }

    /// <summary>同区域只允许两次观察，第二次需要冷却和实际前进，控制重入不会退还名额。</summary>
    private static void SearchObservationLimits()
    {
        var pacing = new SearchPacing();
        Vector3 anchor = new(15, 0, 0);
        pacing.SetArea("player", anchor);
        pacing.TryPause(true, Vector3.Zero, 15, 0.3f, 10, 50, out _);
        pacing.Cancel();
        pacing.SetArea("player", anchor + Vector3.UnitX); // 模拟同一地区枪声轻微误差和层重新接管。
        Check(pacing.Count == 1, "same area keeps spent count");
        Check(!pacing.TryPause(true, new Vector3(6, 0, 0), 9, 0.3f, 15.999, 50, out _), "full cooldown required");
        Check(!pacing.TryPause(true, new Vector3(5.99f, 0, 0), 9, 0.3f, 16, 50, out _), "spatial progress required");
        Check(pacing.TryPause(true, new Vector3(6, 0, 0), 9, 0.3f, 16, 50, out _), "second observation after progress");
        Check(!pacing.TryPause(true, new Vector3(12, 0, 0), 3, 0.3f, 22, 50, out _), "two observations are a hard limit");
        pacing.SetArea("player", anchor + new Vector3(5, 0, 0));
        Check(pacing.Count == 0, "genuinely new area resets count");
    }

    /// <summary>危险与短期线索优先，非法参数不会产生永久等待，等级只改变短暂停顿长度。</summary>
    private static void SearchObservationInterruptions()
    {
        var pacing = new SearchPacing();
        pacing.SetArea("player", Vector3.One);
        Check(!pacing.TryPause(true, Vector3.Zero, 10, 0.3f, 10, 11.999, out _), "short lived clue is not delayed");
        Check(!pacing.TryPause(true, Vector3.Zero, float.NaN, 0.3f, 10, 30, out _), "invalid route length does not pause");
        Check(!pacing.TryPause(true, Vector3.Zero, 10, 0.3f, double.NaN, 30, out _), "invalid clock does not pause");
        pacing.TryPause(true, Vector3.Zero, 10, 0.3f, 10, 30, out _);
        double lowDuration = pacing.PauseUntil - 10;
        Check(!pacing.TryPause(false, Vector3.Zero, 10, 0.3f, 10.1, 30, out _), "danger cancels wait immediately");
        Check(pacing.PauseUntil == 0 && pacing.Count == 1, "interrupt does not refund wait");
        Check(!pacing.TryPause(true, Vector3.Zero, 10, 0.3f, 10.2, 30, out _), "control resume cannot recreate wait");
        pacing.Clear(); pacing.SetArea("player", Vector3.One);
        pacing.TryPause(true, Vector3.Zero, 10, 0.85f, 10, 30, out _);
        Check(pacing.PauseUntil - 10 < lowDuration && pacing.Count == 1, "higher level shortens wait without more work");
        Check(!pacing.TryPause(true, Vector3.Zero, 10, 0.85f, 9, 30, out _), "backward clock cancels wait");
    }

    /// <summary>停看前只窥查缓存，真正移动才消费路线，到期不会因等待续命。</summary>
    private static void SearchPauseRouteIntegration()
    {
        var route = new SearchRoute();
        var pacing = new SearchPacing();
        Vector3[] corners = { Vector3.Zero, new(8, 0, 0), new(8, 0, 8) };
        var output = new Vector3[SearchRoute.Capacity + 2];
        Check(route.Load(corners, corners[0], corners[2], true, 10, out _), "pause route valid");
        pacing.SetArea("player", corners[2]);
        Check(route.CanTake(Vector3.Zero, 10), "route validated before pause");
        pacing.TryPause(true, Vector3.Zero, route.RemainingLength, 0.3f, 10, 40, out _);
        Near(route.RemainingLength, 16, "waiting does not consume route");
        Check(route.Take(Vector3.Zero, 10.8, output, out int count, out bool final), "route resumes after pause");
        Check(!final && output[count - 1] == new Vector3(8, 0, 4), "resume preserves bend rather than straight line");
        Near(route.RemainingLength, 4, "remaining length follows actual polyline");
        Check(!route.CanTake(output[count - 1], 18), "pause cannot extend original route lifetime");
        route.Clear();
        Check(route.RemainingLength == 0 && !route.CanTake(Vector3.Zero, 19), "handoff clears route eligibility");
    }

    /// <summary>视线波动与战术重抽不打断已经在执行的有效掩体路线。</summary>
    private static void CoverCommitmentAcrossSightChanges()
    {
        var policy = new DecisionPolicy();
        var skill = SkillProfile.Create(BotRole.Pmc, 60);
        var input = new DecisionInput { HasClue = true, Visible = true, Reacted = true, HasCover = true, CanMove = true };
        Check(policy.Decide(input, skill, 10, 0) == BehaviorState.Cover, "cover movement selected");
        input.MovingToCover = true;
        for (int index = 1; index <= 100; index++) // 模拟原生视线和战术情境反复变化。
        {
            input.Visible = index % 2 == 0;
            input.ContextVersion++;
            Check(policy.Decide(input, skill, 10 + index * 0.1, 1) == BehaviorState.Cover, "valid cover route survives normal observation changes");
        }
        input.NeedsRecovery = true;
        Check(policy.Decide(input, skill, 21, 1) == BehaviorState.Disengage, "low ammo finishes path to safety before starting recovery");
        input.AtCover = true;
        Check(policy.Decide(input, skill, 21.01, 1) == BehaviorState.Recover, "arrival permits immediate recovery");
    }

    /// <summary>掩体承诺不能挡住新危险、失效、失去线索或已开始的原生恢复。</summary>
    private static void CoverCommitmentEmergencyExit()
    {
        var skill = SkillProfile.Create(BotRole.Pmc, 60);
        var policy = new DecisionPolicy();
        var input = new DecisionInput { HasClue = true, Visible = true, Reacted = true, HasCover = true, CanMove = true, MovingToCover = true };
        policy.Decide(input, skill, 10, 0);
        input.HasCover = false;
        Check(policy.Decide(input, skill, 10.01, 0) == BehaviorState.Engage, "invalid cover releases minimum hold immediately");
        input.PlayerDanger = true;
        Check(policy.Decide(input, skill, 10.02, 0) == BehaviorState.Evade, "new player danger interrupts");
        input.PlayerDanger = false; input.RecoveryRunning = true;
        Check(policy.Decide(input, skill, 10.03, 0) == BehaviorState.Recover, "native recovery stays protected");
        input.RecoveryRunning = false; input.HasClue = false;
        Check(policy.Decide(input, skill, 10.04, 0) == BehaviorState.Native, "no clue returns native control");
        policy.Reset(BehaviorState.Cover); input.HasClue = input.HasCover = true; input.CanMove = false;
        Check(policy.Decide(input, skill, 10.05, 0) == BehaviorState.Engage, "stationary role cannot inherit moving commitment");
    }

    /// <summary>只有同一路线的掩体状态之间允许保留移动，其他状态必须清理旧路径。</summary>
    private static void CoverMoveHandoffMatrix()
    {
        foreach (BehaviorState current in Enum.GetValues<BehaviorState>()) // 覆盖全部状态组合，防止未来增加状态时自动继承路线。
        foreach (BehaviorState next in Enum.GetValues<BehaviorState>())
        {
            bool fromCover = current == BehaviorState.Cover || current == BehaviorState.Disengage || current == BehaviorState.Evade;
            bool toCover = next == BehaviorState.Cover || next == BehaviorState.Disengage || next == BehaviorState.Evade;
            Check(TacticalActionPolicy.KeepCoverMove(current, next, true) == (fromCover && toCover), "only cover family can preserve path");
            Check(!TacticalActionPolicy.KeepCoverMove(current, next, false), "invalid route never retained");
        }
    }

    /// <summary>还击需要真实防守条件，低压不能变成无掩体、盲射、移动中或恢复中射击许可。</summary>
    private static void DefensiveFireAndDangerFlow()
    {
        var pressure = new SuppressionPressure();
        var danger = new PlayerDanger();
        var memory = new FailedCoverMemory();
        danger.Observe(true, new Vector3(50, 0, 0), 10, 25);
        pressure.Observe(true, PressureEvent.Hit, 10);
        memory.ObserveHit(true, true, Vector3.Zero, 10);
        Check(!TacticalActionPolicy.CanReturnFire(true, false, true, false, pressure.Level, 11, 10), "high pressure withholds defensive fire");
        Check(memory.Rejects(Vector3.Zero, 11), "hit location cannot be reused for defense");
        pressure.Advance(12);
        Check(TacticalActionPolicy.CanReturnFire(true, false, true, false, pressure.Level, 12, 10), "new valid cover can return fire after pressure recedes");
        Check(!TacticalActionPolicy.CanReturnFire(false, false, true, false, pressure.Level, 12, 10), "no cover no defensive fire");
        Check(!TacticalActionPolicy.CanReturnFire(true, true, true, false, pressure.Level, 12, 10), "moving is not established cover");
        Check(!TacticalActionPolicy.CanReturnFire(true, false, false, false, pressure.Level, 12, 10), "heard clue cannot authorize fire");
        Check(!TacticalActionPolicy.CanReturnFire(true, false, true, true, pressure.Level, 12, 10), "native healing not interrupted");
        Check(!TacticalActionPolicy.CanReturnFire(true, false, true, false, pressure.Level, 12, 11.3), "new close threat retains flinch interval");
        Check(!TacticalActionPolicy.CanReturnFire(true, false, true, false, pressure.Level, double.PositiveInfinity, 10), "invalid defensive clock blocked");
        Check(danger.IsActive(14.999) && !danger.CanAdvance(14.999), "defensive fire does not shorten five second shelter window");
        pressure.Advance(15);
        Check(!danger.IsActive(15) && danger.CanAdvance(15) && pressure.Level == PressureLevel.Calm, "five second quiet still permits bounded advance");
    }

    /// <summary>热路径仅操作预分配对象与值，事件风暴中不创建托管垃圾；不代表游戏接口零分配。</summary>
    private static void TacticalHotPathAllocations()
    {
        var pressure = new SuppressionPressure();
        var memory = new FailedCoverMemory();
        var pacing = new SearchPacing();
        RunTacticalHotLoop(pressure, memory, pacing, 100); // 先预热所有方法和运行时入口。
        long before = GC.GetAllocatedBytesForCurrentThread(); // 只测当前线程的纯逻辑热循环。
        RunTacticalHotLoop(pressure, memory, pacing, 10000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0, "pure tactical hot loop allocates zero managed bytes");
    }

    /// <summary>用固定位置和既有身份反复处理事件、衰减、候选过滤与短暂停看，避免测试自身制造分配。</summary>
    private static void RunTacticalHotLoop(SuppressionPressure pressure, FailedCoverMemory memory, SearchPacing pacing, int count)
    {
        pressure.Clear(); memory.Clear(); pacing.Clear(); // 独立运行不继承预热时钟。
        pacing.SetArea("player", new Vector3(10, 0, 0)); // 字面量身份不产生热循环分配。
        for (int index = 0; index < count; index++) // 固定上限模拟事件密集时工作量。
        {
            double now = index * 0.1; // 使用值时间模拟衰减。
            pressure.Observe(true, index % 3 == 0 ? PressureEvent.Hit : PressureEvent.NearBullet, now); // 近弹、命中和合并分支均参与。
            pressure.Advance(now + 0.01); // 帧更新只修改标量。
            memory.ObserveHit(true, true, new Vector3(index % 5 * 10, 0, 0), now); // 固定四槽不断替换。
            memory.Rejects(Vector3.Zero, now); // 查询只遍历四项。
            pacing.TryPause(true, new Vector3(index % 20, 0, 0), 10, 0.3f, now, count + 100, out _); // 停看达到额度后保持常数工作。
            TacticalActionPolicy.CanReturnFire(true, false, true, false, pressure.Level, now, 0); // 还击判断不访问任何游戏对象。
        }
    }
}
