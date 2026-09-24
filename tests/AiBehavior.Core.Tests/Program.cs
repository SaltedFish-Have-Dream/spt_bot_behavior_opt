using System;
using System.Numerics;
using AiBehavior.Core;

namespace AiBehavior.Core.Tests;

/// <summary>不依赖游戏进程的行为回归检查，失败返回非零退出码。</summary>
internal static partial class Program
{
    private static int _assertions;

    /// <summary>运行涉及成长、公平信息、状态与资源限制的场景检查。</summary>
    private static int Main()
    {
        try // 任一断言失败即阻止发布打包。
        {
            SkillCurve(); // 检查所有等级的连续性和端点。
            FixedRoles(); // 检查 Scav 与狙击模板不受等级影响。
            InvalidLevels(); // 检查配置和等级异常回退。
            MemoryCapacityAndExpiry(); // 检查内存有界与精确过期边界。
            MemorySoundStorm(); // 检查重复声音不能刷新寿命和位置。
            MemorySnapshots(); // 检查坐标、瞄准与时间均为值快照。
            InvalidObservations(); // 检查无效坐标和无限时间被拒绝。
            ReactionParallel(); // 检查反应和准备并行而非串行相加。
            ReactionLossAndWeaponChange(); // 检查遮挡、换目标和换弹的重置。
            BudgetAcrossFrameRates(); // 检查额度不会随 FPS 成比例增加。
            BudgetPauseAndAtomicity(); // 检查长停顿、整组扣费和时钟回退。
            QueueDeduplication(); // 检查去重、代次和截止时间不延长。
            QueueFairnessAndCancellation(); // 检查热点请求不能饿死其他 Bot。
            QueueLimitsAndInvalidData(); // 检查容量、无活跃 Bot 与非法请求。
            DecisionSafetyAndRecovery(); // 检查恢复锁和丢失线索的退出。
            DecisionDoesNotReroll(); // 检查同一情境不能每 tick 重抽概率。
            StableRandom(); // 检查随机隔离和概率范围。
            LifecycleReleaseOnce(); // 检查长时间停用和原生层射击资源的幂等释放。
            DecisionUnderExhaustedBudget(); // 检查持续超预算时轮转仍有进展。
            InvestigationBoundariesAndExit(); // 检查超距、到期、恢复与重新接敌。
            UnknownBrainFirstEvidence(); // 检查首次兼容信息不依赖普通事件额度且容量有界。
            WorkTimingNestedAndLate(); // 检查嵌套独占归属和 LateUpdate 之后的回调。
            WorkTimingDeepNesting(); // 检查极端重入不会抛错或重复累计。
            LocalPlayerScope(); // 检查真人身份、混战交接与玩家死亡后的退出。
            GunshotDistances(); // 检查三十米和一百二十米的开闭边界。
            GunshotAccuracy(); // 检查距离、等级和固定角色的定位误差。
            PlayerDangerWindow(); // 检查真实危险刷新与五秒后有限推进。
            PlayerDangerInvalidAndClose(); // 检查无效来源和近枪不能延长危险。
            PlayerDangerDecisions(); // 检查紧急避险、低血量和缺弹的优先级。
            SegmentedSearch(); // 检查远距搜索步长、有限寿命和不可超越目标。
            BulletCollisionGeometry(); // 检查擦弹、弹着点和墙后线段外的排除。
            BulletQueueLimitsAndExpiry(); // 检查轨迹容量、过期、游标与非法输入。
            PlayerEventBudgets(); // 检查弹道与同组告警的帧率无关限额。
            PostureDangerStorm(); // 检查连射和移动重规划不能在同一危险窗口反复蹲起。
            PostureStableIntent(); // 检查普通动作的短暂切换不会提交姿态。
            PostureOwnershipHandoff(); // 检查未写姿态、重复清理与原生抢占后的归属。
            PostureRecoveryAndProne(); // 检查恢复及原生卧姿退出后的姿态衔接。
            CoverArrivalBoundaries(); // 检查掩体边界小幅位移不会反复触发起步。
            PostureInvalidInputs(); // 检查非法时间、姿态和无控制状态不提交动作。
            RouteAroundObstacle(); // 检查路线保留转角和高度，不沿直线穿墙。
            RouteCacheAndInvalidation(); // 检查缓存寿命、偏离、失败清理和错误输入。
            RouteValidationReasons(); // 检查源点、终点、绕路和不完整路径独立归因。
            RouteManyCorners(); // 检查不同曲折路线的步长、完整覆盖和容量上限。
            SearchFailureRetry(); // 检查失败与实际到达分开、有期限且仅一次重试。
            ActivityPriorityKeepsLimit(); // 检查玩家情境排序优先但原数量限制不变。
            ActionTimingBreakdown(); // 检查动作子阶段不会双计总耗时。
            PressureEventMerging(); // 检查近弹与命中去重和直接命中升级。
            PressureBurstAndQuiet(); // 检查连续近弹与五秒安静恢复。
            PressureFrameRateIndependence(); // 检查标量衰减不依赖帧率。
            PressureSourceAndHysteresis(); // 检查来源、非法时间与高压迟滞。
            FailedCoverEvidenceAndExpiry(); // 检查失效证据、空间范围和到期。
            FailedCoverCapacityAndStorm(); // 检查四项硬容量和高频替换。
            FailedCoverInvalidAndClear(); // 检查非法候选、倒序命中与清理。
            SearchObservationWindow(); // 检查停看不会逐帧续期。
            SearchObservationLimits(); // 检查次数、冷却、空间进展与重新接管。
            SearchObservationInterruptions(); // 检查危险抢占和即将过期的线索。
            SearchPauseRouteIntegration(); // 检查停看不会消费或延长导航路线。
            CoverCommitmentAcrossSightChanges(); // 检查普通条件变化不打断掩体路线。
            CoverCommitmentEmergencyExit(); // 检查危险与恢复仍可打断承诺。
            CoverMoveHandoffMatrix(); // 检查所有状态间的路线保留条件。
            DefensiveFireAndDangerFlow(); // 检查压力、失效掩体和五秒推进的组合。
            TacticalHotPathAllocations(); // 检查纯逻辑事件热循环的托管分配。
            EscapeFallbackBoundaries(); // 检查贴近危险、两次撤离和冷却结束。
            BlockedShotRepositionBoundaries(); // 检查目标切换、零星阻挡和两侧限额。
            SearchFailureMemoryBoundaries(); // 检查固定容量、空间范围和到期恢复。
            SightWatchWindow(); // 检查失视守点只使用最后真实视觉快照。
            RepeekBoundaries(); // 检查同区域再次探头的身份、距离和时效。
            WatchDecisionAndReaction(); // 检查危险优先级与等级反应边界。
            FootworkStableEngagement(); // 检查近距交战稳定后只提出一次候选。
            FootworkDistanceAndSightBounds(); // 检查近远距离和短暂失视边界。
            FootworkOtherRepositionAndLifecycle(); // 检查挡枪换位去重与目标清理。
            Console.WriteLine($"PASS: 71 scenarios, {_assertions} assertions."); // 输出实际验证数量。
            return 0;
        }
        catch (Exception exception) // 明确报告失败而不是继续生成包。
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    /// <summary>比较所有六条曲线每一级的增量与设计端点。</summary>
    private static void SkillCurve()
    {
        float[] start = { 0.9f, 0.8f, 8, 6, 0.3f, 1.2f }; // 低等级设计基线。
        float[] end = { 0.3f, 0.35f, 3, 14, 0.85f, 0.4f }; // 高等级设计基线。
        for (int level = 1; level <= 60; level++) // 验证整个区间而非只验证实现中的示例等级。
        {
            float[] values = Values(SkillProfile.Create(BotRole.Pmc, level)); // 获取当前全部能力。
            for (int index = 0; index < values.Length; index++) // 每个参数使用独立期望值。
                Near(values[index], start[index] + (end[index] - start[index]) * (level - 1) / 59, "level curve"); // 保证线性。
        }
        Near(SkillProfile.Create(BotRole.Pmc, 20).ReactionSeconds, 0.70677966f, "level 20 reaction"); // 校验非整齐中间点。
        Near(SkillProfile.Create(BotRole.Pmc, 40).MemorySeconds, 11.288136f, "level 40 memory"); // 校验增长方向参数。
    }

    /// <summary>固定角色在相同配置下不随等级变化。</summary>
    private static void FixedRoles()
    {
        foreach (BotRole role in new[] { BotRole.Scav, BotRole.Marksman, BotRole.Native }) // 覆盖全部不成长角色。
            for (int level = 1; level < 120; level++) // 同时覆盖成长区间外。
                EqualProfile(SkillProfile.Create(role, 1), SkillProfile.Create(role, level), "fixed role"); // 能力快照完全相同。
    }

    /// <summary>验证饱和、整数边界和无效配置回退。</summary>
    private static void InvalidLevels()
    {
        EqualProfile(SkillProfile.Create(BotRole.Pmc, int.MinValue), SkillProfile.Create(BotRole.Pmc, 1), "lower clamp");
        EqualProfile(SkillProfile.Create(BotRole.Pmc, int.MaxValue), SkillProfile.Create(BotRole.Pmc, 60), "upper clamp");
        EqualProfile(SkillProfile.Create(BotRole.Pmc, 20, 10, 10), SkillProfile.Create(BotRole.Pmc, 20), "invalid range");
        Near(SkillProfile.Create(BotRole.Pmc, 10, 10, 20).ReactionSeconds, 0.9f, "custom lower bound");
        Near(SkillProfile.Create(BotRole.Pmc, 20, 10, 20).ReactionSeconds, 0.3f, "custom upper bound");
    }

    /// <summary>第五个威胁应淘汰旧项，而不是扩容。</summary>
    private static void MemoryCapacityAndExpiry()
    {
        var memory = new ThreatMemory();
        for (int index = 0; index < 5; index++) memory.Observe(new Observation("target" + index, ObservationSource.Vision, new Vector3(index), index, 20, 0), index); // 输入五个独立威胁。
        Check(!memory.TryGet("target0", 5, out _), "oldest evicted"); // 固定四条记录。
        Check(memory.TryGet("target4", 19.99, out _), "before expiry"); // 截止前仍可搜索。
        Check(!memory.TryGetLatest(20, out _), "exact expiry"); // 到期时不能延长。
        memory.Forget("target4");
        Check(!memory.TryGet("target4", 5, out _), "explicit forget");
    }

    /// <summary>连续枪声不能在一个合并窗口内提高定位精度或刷新寿命。</summary>
    private static void MemorySoundStorm()
    {
        var memory = new ThreatMemory();
        memory.Observe(new Observation("sound", ObservationSource.Hearing, new Vector3(8, 0, 0), 1, 4, 8), 1); // 初始带误差声音。
        for (int index = 1; index <= 90; index++) // 模拟每秒一百次重复事件。
            Check(!memory.Observe(new Observation("sound", ObservationSource.Hearing, Vector3.Zero, 1 + index * 0.01, 9, 0), 1 + index * 0.01), "sound coalesced"); // 不向真实中心收敛。
        Check(memory.TryGet("sound", 2, out Observation observation), "sound retained");
        Near(observation.Position.X, 8, "sound position fixed");
        Near((float)observation.ExpiresAt, 4, "sound expiry fixed");
        Check(memory.Observe(new Observation("sound", ObservationSource.Vision, new Vector3(3), 2.1, 8, 0), 2.1), "new sight replaces sound"); // 真正视觉允许改善定位。
    }

    /// <summary>外部目标移动不能改变已有记忆与瞄准点。</summary>
    private static void MemorySnapshots()
    {
        var memory = new ThreatMemory();
        Vector3 position = new(1, 2, 3);
        memory.Observe(new Observation("enemy", ObservationSource.Vision, position, 1, 7, 0, position + Vector3.UnitY), 1);
        position = new Vector3(100); // 模拟目标移到墙后另一位置。
        Check(memory.TryGet("enemy", 2, out Observation saved), "snapshot found");
        Check(saved.Position == new Vector3(1, 2, 3) && saved.AimPosition == new Vector3(1, 3, 3), "no hidden tracking");
        Check(!memory.Observe(new Observation("enemy", ObservationSource.Vision, position, 0.5, 7, 0), 2), "out of order rejected");
        Check(!memory.TryGetLatest(7, out _), "reading did not refresh expiry");
    }

    /// <summary>异常输入不能传播到 Unity 物理和路径调用。</summary>
    private static void InvalidObservations()
    {
        var memory = new ThreatMemory();
        Check(!memory.Observe(new Observation("bad", ObservationSource.Vision, new Vector3(float.NaN), 0, 3, 0), 1), "NaN position rejected");
        Check(!memory.Observe(new Observation("bad", ObservationSource.Vision, Vector3.Zero, 2, 3, 0), 1), "future observation rejected");
        Check(!memory.Observe(new Observation("bad", ObservationSource.Vision, Vector3.Zero, 0, double.PositiveInfinity, 0), 1), "infinite memory rejected");
        Check(!memory.Observe(new Observation("bad", ObservationSource.Vision, Vector3.Zero, double.NegativeInfinity, 3, 0), 1), "invalid timestamp rejected");
    }

    /// <summary>并行就绪取最大值，不能把反应和瞄准时间相加。</summary>
    private static void ReactionParallel()
    {
        var gate = new ReactionGate();
        SkillProfile skill = SkillProfile.Create(BotRole.Pmc, 1);
        gate.Update("enemy", true, true, 10);
        Check(!gate.CanFire(skill, 10.79), "not ready early");
        Check(gate.CanFire(skill, 10.901), "parallel not additive");
        gate.Update("enemy", true, true, 11);
        Check(gate.CanFire(skill, 11), "continuous sight does not reset");
    }

    /// <summary>遮挡、目标替换和武器准备改变会撤销旧的射击许可。</summary>
    private static void ReactionLossAndWeaponChange()
    {
        var gate = new ReactionGate();
        SkillProfile skill = SkillProfile.Create(BotRole.Pmc, 60);
        gate.Update("first", true, true, 1);
        Check(gate.CanFire(skill, 2), "initial ready");
        gate.Update("first", false, true, 2);
        Check(!gate.CanFire(skill, 20), "loss blocks shooting");
        gate.Update("second", true, false, 20);
        Check(!gate.CanFire(skill, 21), "unready weapon blocks");
        gate.Update("second", true, true, 21);
        Check(!gate.CanFire(skill, 21.1), "new weapon needs stabilization");
        Check(gate.CanFire(skill, 21.351), "weapon eventually ready");
        gate.Update("third", true, true, 22);
        Check(!gate.CanFire(skill, 22.1), "target switch resets");
    }

    /// <summary>不同帧率下持续额度接近相同的每秒速率。</summary>
    private static void BudgetAcrossFrameRates()
    {
        foreach (int fps in new[] { 30, 60, 144 }) // 覆盖低、中、高帧率。
        {
            var budget = new WorkBudget(120, 8);
            for (int frame = 0; frame < fps * 10; frame++) // 模拟十秒持续饱和负载。
            {
                int spent = 0;
                while (budget.TryTake((double)frame / fps, frame)) spent++; // 尽量取完当前可用额度。
                Check(spent <= 8, "per-frame ceiling"); // 帧内不能超突发上限。
            }
            Check(budget.TotalUsed <= 1208 && budget.TotalUsed >= 1190, "rate independent of FPS"); // 十秒上限只取决于速率与初始桶。
        }
    }

    /// <summary>暂停后只补满桶，整组查询领取失败不消耗残余令牌。</summary>
    private static void BudgetPauseAndAtomicity()
    {
        var budget = new WorkBudget(1, 2);
        Check(budget.TryTake(0, 1), "first token");
        Check(!budget.TryTake(0, 1, 2), "atomic group denied");
        Check(budget.TryTake(0, 1), "failed group did not spend");
        Check(!budget.TryTake(0, 1), "frame drained");
        Check(budget.TryTake(1000, 2, 2), "pause fills at most capacity");
        Check(!budget.TryTake(1000, 2), "no backlog burst");
        Check(!budget.TryTake(999, 3), "clock rollback no refill");
    }

    /// <summary>相同动作去重不续命，旧代次不能覆盖新的动作。</summary>
    private static void QueueDeduplication()
    {
        var queue = new WorkQueue(4);
        queue.Enqueue(Request(1, 1, 1), 0, 1);
        queue.Enqueue(Request(1, 1, 3), 0.1, 1);
        Check(queue.Count == 1, "duplicate count bounded");
        Check(queue.TryDequeue(0.5, out WorkRequest result) && result.Deadline == 1, "merge preserves deadline");
        queue.Enqueue(Request(1, 2, 3), 1, 1);
        Check(!queue.Enqueue(Request(1, 1, 4), 1, 1), "stale generation rejected");
        Check(queue.TryDequeue(2, out result) && result.Generation == 2, "new generation retained");
    }

    /// <summary>重新入队的热点任务不能总在其他 Bot 前面。</summary>
    private static void QueueFairnessAndCancellation()
    {
        var queue = new WorkQueue(4);
        queue.Enqueue(Request(1, 1, 4), 0, 3);
        queue.Enqueue(Request(2, 1, 4), 0, 3);
        queue.Enqueue(Request(3, 1, 4), 0, 3);
        Check(queue.TryDequeue(0, out WorkRequest first) && first.Owner == 1, "first owner");
        queue.Enqueue(first, 0, 3);
        Check(queue.TryDequeue(0, out WorkRequest second) && second.Owner == 2, "round robin fairness");
        queue.Cancel(1);
        Check(queue.Count == 1, "cancel all owner requests");
        Check(!queue.TryDequeue(5, out _), "expired query never executed");
        Check(queue.Count == 0 && queue.Expired == 1, "expired slot released");
    }

    /// <summary>请求总量和非法输入有明确边界。</summary>
    private static void QueueLimitsAndInvalidData()
    {
        var queue = new WorkQueue(2);
        Check(!queue.Enqueue(Request(1, 0, 1), 0, 0), "no active bots no queue");
        Check(queue.Enqueue(Request(1, 0, 1), 0, 2), "first queued");
        Check(queue.Enqueue(Request(2, 0, 1), 0, 2), "second queued");
        Check(!queue.Enqueue(Request(3, 0, 1), 0, 2), "capacity ceiling");
        Check(!queue.Enqueue(Request(3, 0, double.NaN), 0, 2), "invalid deadline rejected");
        Check(!queue.Enqueue(Request(3, 0, 0), 0, 2), "expired on entry rejected");
    }

    /// <summary>恢复锁优先于战斗，线索过期会退出搜索。</summary>
    private static void DecisionSafetyAndRecovery()
    {
        var policy = new DecisionPolicy();
        SkillProfile skill = SkillProfile.Create(BotRole.Pmc, 20);
        var input = new DecisionInput { HasClue = true, Visible = true, Reacted = true, RecoveryRunning = true };
        Check(policy.Decide(input, skill, 0, 0) == BehaviorState.Recover, "recovery lock");
        input.RecoveryRunning = false;
        Check(policy.Decide(input, skill, 1, 0) == BehaviorState.Engage, "recovery complete");
        input.Visible = false;
        Check(policy.Decide(input, skill, 1.1, 0) == BehaviorState.Search, "lost vision searches");
        input.HasClue = false;
        Check(policy.Decide(input, skill, 1.2, 0) == BehaviorState.Native, "expiry returns native");
        input.NeedsRecovery = true;
        Check(policy.Decide(input, skill, 2, 0) == BehaviorState.Recover, "idle recovery");
    }

    /// <summary>连续更新不通过重新抽样把低等级选择概率抬高。</summary>
    private static void DecisionDoesNotReroll()
    {
        var policy = new DecisionPolicy();
        SkillProfile skill = SkillProfile.Create(BotRole.Pmc, 1);
        var input = new DecisionInput { HasClue = true, Visible = true, Reacted = true, HasCover = true, CanMove = true, ContextVersion = 1 };
        Check(policy.Decide(input, skill, 0, 0.99f) == BehaviorState.Engage, "initial non-tactical choice");
        for (int index = 1; index < 30; index++) Check(policy.Decide(input, skill, index, 0) == BehaviorState.Engage, "same context does not reroll"); // 后续随机值不能改变首次抽样。
        input.ContextVersion++;
        Check(policy.Decide(input, skill, 31, 0) == BehaviorState.Cover, "new context may choose cover");
        input.CanMove = false;
        Check(policy.Decide(input, skill, 32, 0) == BehaviorState.Engage, "marksman never moves to cover");
    }

    /// <summary>相同身份得到相同序列，每次输出都属于合法概率范围。</summary>
    private static void StableRandom()
    {
        var first = new BotRandom("same-profile");
        var second = new BotRandom("same-profile");
        for (int index = 0; index < 100; index++) // 覆盖多个内部状态更新。
        {
            float value = first.Next01();
            Check(value == second.Next01() && value >= 0 && value < 1, "stable bounded random");
        }
    }

    /// <summary>模拟约五万次停用更新，清理只能发生一次，重新排队后恢复清理责任。</summary>
    private static void LifecycleReleaseOnce()
    {
        var lifecycle = new BotLifecycle(); // 与客户端共享真实生命周期门控。
        var queue = new WorkQueue(); // 检查资源清理对旧任务的实际影响。
        lifecycle.OwnResources(); // 模拟首次取得控制。
        queue.Enqueue(Request(1, lifecycle.Generation, 100), 0, 1); // 保存释放前的动作代次。
        Check(lifecycle.SetActive(false), "first inactive edge"); // 只允许一次停用边沿。
        int releases = 0; // 用清理执行次数验证热循环没有扫描队列。
        for (int index = 0; index < 50000; index++) // 复现日志中约五万次重复停用更新。
        {
            Check(!lifecycle.SetActive(false), "paused state has no repeated edge"); // 相同状态不触发停用逻辑。
            if (lifecycle.TryRelease()) { queue.Cancel(1); releases++; } // 即使外部重复调用 Release，也只有首次清理。
        }
        Check(releases == 1 && lifecycle.Generation == 1 && queue.Count == 0, "one cleanup and one invalidation");
        Check(lifecycle.SetActive(true) && !lifecycle.SetActive(true), "one reactivation edge");
        lifecycle.OwnResources(); // 模拟没有自有动作控制权的原生射击入口排队。
        Check(queue.Enqueue(Request(1, lifecycle.Generation, 100), 0, 1), "native action can queue a fresh shot");
        Check(lifecycle.TryRelease(), "native layer shot still owns cleanup responsibility");
        queue.Cancel(1); // 模拟 Dispose 与层 Stop 共用清理入口。
        Check(!lifecycle.TryRelease() && lifecycle.Generation == 2 && queue.Count == 0, "repeat stop and dispose are idempotent");
    }

    /// <summary>软预算始终耗尽时每帧最多服务一名到期 Bot，热点项不能饿死其他候选。</summary>
    private static void DecisionUnderExhaustedBudget()
    {
        var scheduler = new DecisionScheduler(); // 使用客户端实际调度器。
        var visits = new int[31]; // 其中前三项模拟长期停用。
        for (int frame = 0; frame < 56; frame++) // 两轮应覆盖全部二十八名活动 Bot。
        {
            scheduler.BeginFrame(visits.Length); // 每帧扫描上限由当前受管量决定。
            int served = 0; // 验证一次保障机会不会变成无限超预算执行。
            int scanned = 0; // 包含跳过停用项的实际扫描量。
            while (scheduler.TryNext(visits.Length, false, out int index)) // 持续模拟安全检查已占满预算。
            {
                scanned++; // 每次选取只检查一个候选。
                if (index < 3) continue; // 停用项不能消耗关键保障机会。
                visits[index]++; // 包括热点项在内都保持到期，检验公平性。
                scheduler.Served(); // 真实执行后才消耗保障机会。
                served++; // 记录本帧实际执行量。
            }
            Check(served == 1 && scanned <= visits.Length, "one guaranteed decision with bounded scan");
        }
        for (int index = 3; index < visits.Length; index++) Check(visits[index] == 2, "each active bot served fairly"); // 不允许只更新列表头部。
        scheduler.BeginFrame(31); // 模拟全体均未到期的空转扫描。
        int candidates = 0; // 不调用 Served 时也必须在固定扫描上限结束。
        while (scheduler.TryNext(31, false, out _)) candidates++; // 没有到期项不能形成无限循环。
        Check(candidates == 31, "not-due scan is bounded");
        var removal = new DecisionScheduler(); // 单独检验执行期间的异常注销。
        removal.BeginFrame(3);
        Check(removal.TryNext(3, false, out int first) && first == 0, "first candidate selected");
        removal.Served();
        removal.RemovedAt(0); // 删除刚服务的 Bot，原第二项左移至下标零。
        removal.BeginFrame(2);
        Check(removal.TryNext(2, false, out int next) && next == 0, "removal does not skip next bot");
        removal.BeginFrame(0);
        Check(!removal.TryNext(0, true, out _), "empty raid has no decision");
    }

    /// <summary>范围边界与记忆失效均有明确退出；已开始的恢复不被调查结束取消。</summary>
    private static void InvestigationBoundariesAndExit()
    {
        Check(InvestigationPolicy.InRange(BotRole.Scav, Vector3.Zero, new Vector3(25, 0, 0)), "scav boundary allowed");
        Check(!InvestigationPolicy.InRange(BotRole.Scav, Vector3.Zero, new Vector3(25.01f, 0, 0)), "scav out of range rejected");
        Check(InvestigationPolicy.InRange(BotRole.Pmc, Vector3.Zero, new Vector3(60, 0, 0)), "pmc boundary allowed");
        Check(!InvestigationPolicy.InRange(BotRole.Pmc, Vector3.Zero, new Vector3(60.01f, 0, 0)), "pmc out of range rejected");
        Check(InvestigationPolicy.InRange(BotRole.Marksman, Vector3.Zero, new Vector3(200, 0, 0)), "stationary marksman may observe remote sound");
        Check(!InvestigationPolicy.InRange(BotRole.Marksman, Vector3.Zero, new Vector3(float.NaN, 0, 0)), "invalid snapshot rejected");
        var memory = new ThreatMemory(); // 使用真实记忆到期逻辑，不把单元测试误称为游戏集成测试。
        memory.Observe(new Observation("sound", ObservationSource.Hearing, Vector3.One, 0, 0.1, 3), 0);
        var policy = new DecisionPolicy();
        SkillProfile skill = SkillProfile.Create(BotRole.Pmc, 20);
        var input = new DecisionInput { HasClue = true, SoundOnly = true };
        Check(policy.Decide(input, skill, 0, 0) == BehaviorState.Investigate, "valid sound starts investigation");
        Check(!memory.TryGetLatest(0.1, out _), "sound expires during minimum hold");
        policy.Reset(); // 快速退出不再等待共享调度器调用 Decide。
        Check(policy.State == BehaviorState.Native, "expiry immediately releases cached investigation state");
        input.Visible = true;
        input.SoundOnly = false;
        input.Reacted = true;
        Check(policy.Decide(input, skill, 0.11, 0) == BehaviorState.Engage, "fresh enemy is not held by old investigation");
        policy.Reset(BehaviorState.Recover); // 已开始恢复的调查退出保留恢复状态。
        input.HasClue = false;
        input.RecoveryRunning = true;
        Check(policy.Decide(input, skill, 0.12, 0) == BehaviorState.Recover, "investigation exit preserves recovery");
    }

    /// <summary>普通日志额度耗尽后仍保留未知脑型首次证据，重复名称与溢出均有界。</summary>
    private static void UnknownBrainFirstEvidence()
    {
        var events = new WorkBudget(8, 8); // 复现批量注册耗尽普通事件额度。
        Check(events.TryTake(0, 0, 8) && !events.TryTake(0, 0), "ordinary event budget exhausted");
        var cache = new FirstSeenCache(); // 首次兼容记录不共享上述令牌。
        Check(cache.ShouldReport(51, "unknown", out bool overflow) && !overflow, "first unknown brain survives exhaustion");
        Check(!cache.ShouldReport(51, "unknown", out _), "same role and brain only logged once");
        Check(cache.ShouldReport(52, "unknown", out _), "different role retains evidence");
        for (int index = 0; index < 30; index++) Check(cache.ShouldReport(index, "other", out overflow) && !overflow, "fill bounded registry"); // 总容量为三十二种组合。
        Check(cache.ShouldReport(99, "overflow", out overflow) && overflow, "overflow emits one explicit warning");
        Check(!cache.ShouldReport(100, "overflow2", out _) && !cache.ShouldReport(51, "unknown", out _), "overflow and duplicate cannot flood");
        Check(new FirstSeenCache().ShouldReport(51, "unknown", out _), "next raid retains first evidence again");
    }

    /// <summary>用确定性时间线验证嵌套日志不双计、回调间空隙不计、晚回调不丢失。</summary>
    private static void WorkTimingNestedAndLate()
    {
        var profiler = new WorkProfiler();
        profiler.AdvanceFrame(10, 1, 32);
        profiler.Begin(WorkPhase.Safety, 100); // 安全检查内嵌两刻度日志。
        profiler.Begin(WorkPhase.Logging, 110);
        profiler.End(110, 112);
        profiler.End(100, 130);
        profiler.Begin(WorkPhase.Action, 200); // 模拟 LateUpdate 后才执行的动作，中间七十刻度游戏工作不计。
        profiler.End(200, 205);
        profiler.AdvanceFrame(11, 2, 32); // 只有进入下一帧时才结算上一帧。
        Check(profiler.Frames == 1 && profiler.TotalFrameTicks == 35 && profiler.OverBudgetFrames == 1, "late action included without callback gap");
        Check(profiler.TotalTicks[(int)WorkPhase.Safety] == 28 && profiler.TotalTicks[(int)WorkPhase.Logging] == 2, "nested exclusive attribution");
        Check(profiler.PeakCallTicks[(int)WorkPhase.Safety] == 30 && profiler.PeakFramePhases[(int)WorkPhase.Action] == 5, "call peak includes child but frame phases do not overlap");
        profiler.Begin(WorkPhase.Action, 1100);
        profiler.Begin(WorkPhase.Shooting, 1105);
        profiler.End(1105, 1115);
        profiler.End(1100, 1120);
        profiler.CompleteFrame(32);
        profiler.CompleteFrame(32); // 战局重复结束不能再次计入最后一帧。
        Check(profiler.Frames == 2 && profiler.TotalFrameTicks == 55 && profiler.PeakFrameTicks == 35, "nested shoot and final flush counted once");
        Check(profiler.PeakFrame == 10 && profiler.PeakTime == 1, "peak keeps matching frame and time");
        long phaseSum = 0;
        foreach (long ticks in profiler.TotalTicks) phaseSum += ticks; // 累计独占阶段之和必须等于插件区间总量。
        Check(phaseSum == 55, "exclusive phase totals reconcile");
        profiler.AdvanceFrame(12, 3, 32);
        profiler.Begin(WorkPhase.Query, 2000);
        profiler.End(2000, 2050);
        profiler.CompleteFrame(32);
        Check(profiler.PeakFramePhases[(int)WorkPhase.Query] == 50 && profiler.PeakFramePhases[(int)WorkPhase.Safety] == 0, "new peak replaces entire phase snapshot");
    }

    /// <summary>极深同步重入并入父阶段，固定栈溢出不改变游戏执行和总耗时。</summary>
    private static void WorkTimingDeepNesting()
    {
        var profiler = new WorkProfiler();
        profiler.AdvanceFrame(1, 0, 200);
        for (int index = 0; index < 64; index++) profiler.Begin(WorkPhase.Action, index); // 超过固定栈容量时不扩容。
        for (int index = 63; index >= 0; index--) profiler.End(index, 128 - index); // 按真实同步调用顺序退出。
        profiler.CompleteFrame(200);
        Check(profiler.StackOverflows == 32 && profiler.TotalFrameTicks == 128, "deep nesting remains bounded and singly counted");
        Check(profiler.Calls[(int)WorkPhase.Action] == 32 && profiler.PeakCallTicks[(int)WorkPhase.Action] == 128, "overflow merges into parent timing");
    }

    /// <summary>只有本机真人能唤醒增强，普通 AI 目标优先交还原生，真实危险允许临时避险。</summary>
    private static void LocalPlayerScope()
    {
        Check(PlayerThreatPolicy.IsLocalHuman(true, false, true), "local human accepted");
        Check(!PlayerThreatPolicy.IsLocalHuman(true, true, true), "AI cannot impersonate human");
        Check(!PlayerThreatPolicy.IsLocalHuman(false, false, true), "unknown source rejected");
        Check(!PlayerThreatPolicy.IsLocalHuman(true, false, false), "remote actor outside single-player scope");
        Check(!PlayerThreatPolicy.ShouldManage(true, true, false, false, false, false), "idle bot stays native");
        Check(PlayerThreatPolicy.ShouldManage(true, true, false, true, false, false), "native player sight activates scope");
        Check(PlayerThreatPolicy.ShouldManage(true, true, false, false, true, false), "player sound activates search without goal");
        Check(!PlayerThreatPolicy.ShouldManage(true, true, true, false, true, false), "AI combat wins over stale player memory");
        Check(PlayerThreatPolicy.ShouldManage(true, true, true, false, false, true), "player hit can interrupt AI combat briefly");
        Check(!PlayerThreatPolicy.ShouldManage(false, true, false, true, true, true), "inactive bot cannot take control");
        Check(!PlayerThreatPolicy.ShouldManage(true, false, false, true, true, true), "dead player releases scope even during danger");
        var memory = new ThreatMemory(); // 离开无效玩家情境时必须能清除所有类型的记忆。
        memory.Observe(new Observation("player", ObservationSource.Danger, Vector3.One, 1, 20, 3), 1);
        memory.Clear();
        Check(!memory.TryGetLatest(2, out _), "player invalidation clears danger memory");
    }

    /// <summary>距离分段独立于等级，不能以未听见或非法距离创建调查。</summary>
    private static void GunshotDistances()
    {
        Check(PlayerThreatPolicy.ClassifyGunshot(true, true, 0) == GunshotBand.Close, "zero distance close");
        Check(PlayerThreatPolicy.ClassifyGunshot(true, true, 30) == GunshotBand.Close, "30 inclusive close");
        Check(PlayerThreatPolicy.ClassifyGunshot(true, true, 30.01f) == GunshotBand.Search, "above 30 search");
        Check(PlayerThreatPolicy.ClassifyGunshot(true, true, 120) == GunshotBand.Search, "120 inclusive search");
        Check(PlayerThreatPolicy.ClassifyGunshot(true, true, 120.01f) == GunshotBand.Ignore, "beyond 120 ignore");
        Check(PlayerThreatPolicy.ClassifyGunshot(false, true, 10) == GunshotBand.Ignore, "AI gun ignored");
        Check(PlayerThreatPolicy.ClassifyGunshot(true, false, 10) == GunshotBand.Ignore, "unheard gun ignored");
        foreach (float distance in new[] { -1f, float.NaN, float.NegativeInfinity, float.PositiveInfinity }) // 非法值不得穿过边界检查。
            Check(PlayerThreatPolicy.ClassifyGunshot(true, true, distance) == GunshotBand.Ignore, "invalid gun distance");
    }

    /// <summary>远距误差随距离增大、随 PMC 等级缩小；固定模板不借用 PMC 等级。</summary>
    private static void GunshotAccuracy()
    {
        Near(PlayerThreatPolicy.GunshotError(SkillProfile.Create(BotRole.Pmc, 1), 120), 16, "level 1 distant error");
        Near(PlayerThreatPolicy.GunshotError(SkillProfile.Create(BotRole.Pmc, 60), 120), 6, "level 60 distant error");
        Near(PlayerThreatPolicy.GunshotError(SkillProfile.Create(BotRole.Scav, 1), 120), 20, "scav distant error");
        for (int level = 1; level <= 60; level++) // 近距豁免对整个等级区间生效。
        {
            SkillProfile skill = SkillProfile.Create(BotRole.Pmc, level); // 每个等级只读能力快照。
            Near(PlayerThreatPolicy.GunshotError(skill, 30), 0, "close gun precise snapshot at every level");
            Check(PlayerThreatPolicy.GunshotError(skill, 60) < PlayerThreatPolicy.GunshotError(skill, 120), "distance increases uncertainty");
            Near(PlayerThreatPolicy.GunshotError(SkillProfile.Create(BotRole.Scav, level), 120), 20, "scav error fixed across levels");
            Near(PlayerThreatPolicy.GunshotError(SkillProfile.Create(BotRole.Marksman, level), 120), 16, "marksman error fixed across levels");
            if (level < 60) Check(PlayerThreatPolicy.GunshotError(skill, 90) > PlayerThreatPolicy.GunshotError(SkillProfile.Create(BotRole.Pmc, level + 1), 90), "PMC level reduces uncertainty monotonically"); // 等级增加不能反向扩大误差。
        }
    }

    /// <summary>真实命中重置五秒安全窗口，AI 伤害和倒序事件不能刷新。</summary>
    private static void PlayerDangerWindow()
    {
        var danger = new PlayerDanger();
        Check(!danger.IsActive(0) && !danger.CanAdvance(0), "new bot has no danger");
        Check(danger.Observe(true, new Vector3(80, 0, 0), 10, 30), "player danger accepted");
        Check(danger.IsActive(10) && danger.IsActive(14.999), "danger holds before five seconds");
        Check(!danger.CanAdvance(14.999) && !danger.IsActive(15) && danger.CanAdvance(15), "exact five seconds changes from evade to advance");
        Check(!danger.Observe(false, Vector3.Zero, 16, 30) && danger.LastDangerAt == 10, "AI damage does not refresh player danger");
        Check(danger.Observe(true, new Vector3(90, 0, 0), 17, 20), "new player hit restarts safety window");
        Check(danger.IsActive(21.999) && danger.CanAdvance(22), "latest actual danger controls timer");
        Check(!danger.Observe(true, Vector3.One, 16, 45) && danger.Position.X == 90, "out-of-order danger cannot relocate snapshot");
        Check(danger.CanAdvance(36.999) && !danger.CanAdvance(37), "advance ends at fixed deadline");
        danger.Clear();
        Check(!danger.IsActive(18) && !danger.CanAdvance(18) && danger.CloseAlertUntil == 0, "clear removes all player intentions");
    }

    /// <summary>近距枪声只提供短暂反应豁免；无效快照不能污染危险计时。</summary>
    private static void PlayerDangerInvalidAndClose()
    {
        var danger = new PlayerDanger();
        Check(!danger.Observe(true, new Vector3(float.NaN), 1, 10), "invalid danger point rejected");
        Check(!danger.Observe(true, Vector3.Zero, double.PositiveInfinity, 10), "invalid danger time rejected");
        Check(!danger.Observe(true, Vector3.Zero, 1, double.NaN), "invalid search lifetime rejected");
        danger.AlertClose(2); // 只听见枪声也能进入近距快速警戒。
        Check(danger.CloseAlertUntil == 4 && !danger.IsActive(2) && !danger.CanAdvance(5), "close sound alone is not a hit");
        danger.Observe(true, Vector3.One, 10, 12); // 构造已有近弹危险。
        danger.AlertClose(14.9); // 持续的普通枪声不能重置五秒危险窗口。
        Check(danger.LastDangerAt == 10 && !danger.IsActive(15) && danger.CanAdvance(15), "close alert does not prolong suppression");
        Check(danger.Position == Vector3.One, "close alert does not track hidden player position");
    }

    /// <summary>紧急危险及时覆盖普通动作，安全期推进不压过缺弹、伤势与原生恢复。</summary>
    private static void PlayerDangerDecisions()
    {
        SkillProfile skill = SkillProfile.Create(BotRole.Pmc, 1);
        var policy = new DecisionPolicy();
        var input = new DecisionInput { HasClue = true, Visible = true, Reacted = true, CanMove = true };
        Check(policy.Decide(input, skill, 0, 1) == BehaviorState.Engage, "start native-legal engagement");
        input.PlayerDanger = true; // 发生在普通动作最短承诺期内。
        Check(policy.Decide(input, skill, 0.01, 1) == BehaviorState.Evade, "player danger immediately overrides hold");
        input.RecoveryRunning = true; // Evade 执行器只压低姿态，不取消原生手部动作。
        Check(policy.Decide(input, skill, 0.02, 1) == BehaviorState.Evade, "danger state also represents protected ongoing recovery");
        input.PlayerDanger = false;
        input.AdvanceAfterDanger = true;
        Check(policy.Decide(input, skill, 5.01, 1) == BehaviorState.Recover, "ongoing recovery blocks advance");
        input.RecoveryRunning = false;
        Check(policy.Decide(input, skill, 6, 1) == BehaviorState.Advance, "healthy bot advances after safety window");
        input.LowHealth = true;
        Check(policy.Decide(input, skill, 7, 1) == BehaviorState.Observe, "badly wounded bot does not rush");
        input.NeedsRecovery = true;
        Check(policy.Decide(input, skill, 8, 1) == BehaviorState.Recover, "empty magazine or treatment need blocks rush");
        input.HasCover = true;
        Check(policy.Decide(input, skill, 9, 1) == BehaviorState.Disengage, "recovery need favors reachable cover");
        input.NeedsRecovery = input.LowHealth = input.HasCover = input.Visible = false;
        input.CanMove = false;
        Check(policy.Decide(input, SkillProfile.Create(BotRole.Marksman, 1), 10, 1) != BehaviorState.Advance, "stationary marksman does not pursue");
        input.HasClue = false;
        Check(policy.Decide(input, skill, 10.01, 1) == BehaviorState.Native, "expired danger clue releases native control");
    }

    /// <summary>一百二十米外的命中仍可有限调查，但任何一步都不能越过快照目标。</summary>
    private static void SegmentedSearch()
    {
        Vector3 target = new(100, 30, -50);
        Vector3 position = Vector3.Zero;
        var route = new SearchRoute();
        Check(route.Load(new[] { position, target }, position, target, true, 0, out _), "valid solved slope accepted");
        var segment = new Vector3[SearchRoute.Capacity + 2];
        for (int step = 0; step < 20 && position != target; step++) // 任意斜向已求解路线都应在有限次数到达。
        {
            Check(route.Take(position, step * 0.1, segment, out int count, out _), "cached route segment available");
            Vector3 next = segment[count - 1]; // 调用实际生产路线消费规则。
            Check(Vector3.Distance(position, next) <= 12.00002f, "segment bounded by twelve meters");
            Check(Vector3.Distance(next, target) < Vector3.Distance(position, target), "segment makes progress without overshoot");
            position = next; // 下一次规划以实际新位置为起点。
        }
        Check(position == target, "segmented search reaches snapshot");
        Check(!route.Take(target, 2, segment, out _, out _), "completed route cannot repeat old movement");
        Check(PlayerThreatPolicy.SearchLifetime(0) == 12 && PlayerThreatPolicy.SearchLifetime(120) == 45 && PlayerThreatPolicy.SearchLifetime(1000) == 45, "search lifetime bounded at both ends");
        var danger = new PlayerDanger();
        Check(danger.Observe(true, new Vector3(1000, 0, 0), 1, PlayerThreatPolicy.SearchLifetime(1000)) && danger.IsActive(1), "direct distant hit bypasses sound radius");
        Check(!danger.CanAdvance(46), "distant hit cannot create indefinite pursuit");
    }

    /// <summary>几何检测只使用实际飞行段，不能延长穿墙，也能识别退化弹着点。</summary>
    private static void BulletCollisionGeometry()
    {
        Vector3 from = Vector3.Zero;
        Vector3 to = new(10, 0, 0); // 假定原生碰撞把轨迹裁剪在十米处的墙面。
        Near(PlayerThreatPolicy.SegmentDistanceSquared(new Vector3(5, 2.5f, 0), from, to), 6.25f, "near bullet boundary");
        Check(PlayerThreatPolicy.SegmentDistanceSquared(new Vector3(5, 2.51f, 0), from, to) > 6.25f, "outside near radius");
        Check(PlayerThreatPolicy.SegmentDistanceSquared(new Vector3(15, 0, 0), from, to) > 6.25f, "wall-clipped segment does not reach distant bot behind wall");
        Check(PlayerThreatPolicy.SegmentDistanceSquared(new Vector3(-3, 0, 0), from, to) > 6.25f, "segment does not extend behind muzzle");
        Near(PlayerThreatPolicy.SegmentDistanceSquared(new Vector3(10, 2, 0), to, to), 4, "near impact detected for degenerate segment");
        Near(PlayerThreatPolicy.SegmentDistanceSquared(new Vector3(5, 2.5f, 0), to, from), 6.25f, "segment direction independent");
    }

    /// <summary>弹道风暴固定在六十四项，重排不续期，删除 Bot 后游标正确补偿。</summary>
    private static void BulletQueueLimitsAndExpiry()
    {
        var queue = new BulletTraceQueue();
        for (int index = 0; index < 64; index++) // 填满固定容量而不依赖客户端对象。
            Check(queue.Enqueue(new BulletTrace { From = Vector3.Zero, To = Vector3.One, Origin = new Vector3(index, 0, 0), Deadline = 1, NextBot = 3 }), "bounded trace accepted");
        Check(!queue.Enqueue(new BulletTrace { Deadline = 2 }) && queue.Count == 64 && queue.Dropped == 1, "overload rejected without allocation");
        queue.RemovedBotAt(1); // 已经过的列表成员移除，下一待处理成员左移一位。
        Check(queue.TryDequeue(0.5, out BulletTrace trace) && trace.NextBot == 2 && trace.Origin.X == 0, "FIFO snapshot and cursor repair");
        trace.NextBot = 6;
        Check(queue.Enqueue(trace) && queue.Count == 64, "unfinished work requeues with original deadline");
        Check(!queue.TryDequeue(1, out _) && queue.Count == 0 && queue.Expired == 64, "exact deadline expires even requeued work");
        Check(!queue.Enqueue(new BulletTrace { Deadline = double.NaN }), "NaN deadline rejected");
        Check(!queue.Enqueue(new BulletTrace { Deadline = double.PositiveInfinity }), "infinite trace rejected");
        Check(!queue.Enqueue(new BulletTrace { Deadline = 2, NextBot = -1 }), "invalid cursor rejected");
        Check(!queue.Enqueue(new BulletTrace { Deadline = 2, To = new Vector3(float.NaN) }), "invalid geometry rejected");
        Check(queue.Enqueue(new BulletTrace { Deadline = 2, NextBot = 1 }), "queue reusable after overload");
        queue.RemovedBotAt(1); // 尚未经过的成员被移除时无需回退。
        Check(queue.TryDequeue(1.5, out trace) && trace.NextBot == 1, "unvisited removal does not decrement cursor");
    }

    /// <summary>玩家连射和霰弹的工作量受全局预算限制，低帧率和暂停不补跑欠账。</summary>
    private static void PlayerEventBudgets()
    {
        foreach (int fps in new[] { 30, 60, 144 }) // 比较不同帧率下同一秒内的固定几何工作上限。
        {
            var geometry = new WorkBudget(6000, 256); // 与真实弹道消费使用相同参数。
            for (int frame = 0; frame < fps; frame++) // 模拟队列一直有工作。
            {
                int granted = 0; // 单帧不允许超过二百五十六次。
                while (geometry.TryTake((double)frame / fps, frame)) granted++; // 持续申请直到额度用尽。
                Check(granted <= 256, "near-bullet per-frame budget");
            }
            Check(geometry.TotalUsed <= 6256 && geometry.TotalUsed >= 6000, "geometry total independent of FPS");
            Check(geometry.TryTake(100, fps, 256) && !geometry.TryTake(100, fps), "pause cannot accumulate extra geometry burst");
        }
        var broadcast = new WorkBudget(4, 1); // 告警的第一条立即放行，后续传播合并。
        Check(broadcast.TryTake(0, 0) && !broadcast.TryTake(0, 0), "shotgun pellets share single broadcast");
        Check(!broadcast.TryTake(0.1, 1) && broadcast.TryTake(0.25, 2), "friend alert limited to four per second");
        Check(broadcast.TryTake(100, 3) && !broadcast.TryTake(100, 3), "friend alert does not catch up after pause");
    }

    /// <summary>连续危险中即使移动、停留和重规划交替发生，也只提交一次压低姿态。</summary>
    private static void PostureDangerStorm()
    {
        foreach (int fps in new[] { 30, 60, 144 }) // 姿态写入次数不能随帧率和连续事件数量增长。
        {
            var posture = new PosturePolicy();
            float nativePose = 0.9f;
            posture.Acquire(nativePose, 0);
            Check(posture.TryApply(BehaviorState.Evade, true, false, false, nativePose, 0, out float target) && target == 0, "first danger crouches immediately");
            nativePose = target; // 模拟已提交给原生的目标值，不模拟动画插值。
            for (int frame = 1; frame <= fps * 5; frame++) // 覆盖五秒连射与交替的掩体到达条件。
                Check(!posture.TryApply(frame % 2 == 0 ? BehaviorState.Evade : BehaviorState.Cover, true, false, frame % 3 == 0, nativePose, (double)frame / fps, out _), "repeated danger and route changes cannot stand up");
            Check(!posture.TryApply(BehaviorState.Advance, false, false, false, nativePose, 5.1, out _), "safe advance waits before standing");
            Check(!posture.TryApply(BehaviorState.Advance, false, false, false, nativePose, 5.84, out _), "ordinary rise not yet stable");
            Check(posture.TryApply(BehaviorState.Advance, false, false, false, nativePose, 5.86, out target) && target == 0.9f, "stable advance stands once");
            Check(!posture.TryApply(BehaviorState.Advance, false, false, false, target, 6, out _), "next path segment does not reapply standing");
        }
    }

    /// <summary>掩体或行为状态短暂反复不能触发动画反转，持续的新意图才会生效。</summary>
    private static void PostureStableIntent()
    {
        var posture = new PosturePolicy();
        posture.Acquire(0.9f, 0);
        for (int index = 1; index <= 50; index++) // 模拟两百毫秒以内反复进出掩体条件。
            Check(!posture.TryApply(BehaviorState.Engage, false, false, index % 2 == 1, 0.9f, index * 0.1, out _), "short cover flicker cannot crouch");
        Check(!posture.TryApply(BehaviorState.Cover, false, false, true, 0.9f, 6, out _), "new cover intention starts timer");
        Check(!posture.TryApply(BehaviorState.Cover, false, false, true, 0.9f, 6.749, out _), "cover posture waits full window");
        Check(posture.TryApply(BehaviorState.Engage, false, false, true, 0.9f, 6.75, out float target) && target == 0, "same cover intent survives behavior-state change");
        Check(!posture.TryApply(BehaviorState.Search, false, false, false, 0, 7, out _), "brief search does not stand immediately");
        Check(!posture.TryApply(BehaviorState.Engage, false, false, true, 0, 7.2, out _), "return to cover cancels pending rise");
        Check(!posture.TryApply(BehaviorState.Search, false, false, false, 0, 8, out _), "new departure restarts full window");
        Check(posture.TryApply(BehaviorState.Search, false, false, false, 0, 8.75, out target) && target == 0.9f, "stable departure eventually restores mobile posture");
        Check(posture.TryApply(BehaviorState.Evade, true, false, false, 0.9f, 8.76, out target) && target == 0, "new hit bypasses ordinary posture hold");
    }

    /// <summary>层交接不无条件恢复旧姿态，不停止已经属于原生的新姿态。</summary>
    private static void PostureOwnershipHandoff()
    {
        var posture = new PosturePolicy();
        posture.Acquire(0.9f, 0);
        Check(!posture.TryRelease(0.9f, out _), "unmodified posture requires no restoration");
        posture.Acquire(0.9f, 1);
        Check(posture.TryApply(BehaviorState.Evade, true, false, false, 0.9f, 1, out float target), "owned danger posture written");
        Check(posture.TryRelease(target, out float restored) && restored == 0.9f, "own unchanged target restores original once");
        Check(!posture.TryRelease(target, out _), "repeated layer stop does not restore again");
        Check(!posture.TryApply(BehaviorState.Evade, true, false, false, 0.9f, 2, out _), "released layer cannot write more posture");
        posture.Acquire(0.9f, 3);
        posture.TryApply(BehaviorState.Evade, true, false, false, 0.9f, 3, out _);
        Check(!posture.TryRelease(0.4f, out _), "new native posture is not overwritten by old saved target");
        posture.Acquire(0, 4);
        Check(!posture.TryApply(BehaviorState.Evade, true, false, false, 0, 4, out _), "already crouched native target stays untouched");
        Check(!posture.TryRelease(0, out _), "no ownership of preexisting crouch");
    }

    /// <summary>原生恢复保持低姿态，自有卧姿退出产生的目标改变可被统一控制器识别。</summary>
    private static void PostureRecoveryAndProne()
    {
        var posture = new PosturePolicy();
        posture.Acquire(0.9f, 0);
        posture.TryApply(BehaviorState.Evade, true, false, false, 0.9f, 0, out _);
        Check(!posture.TryApply(BehaviorState.Recover, false, true, false, 0, 6, out _), "healing does not stand after danger ends");
        Check(!posture.TryApply(BehaviorState.Advance, false, true, false, 0, 6.2, out _), "running recovery overrides movement posture");
        posture.ResumeAfterProne(0.4f); // 原生 BotLay 结束卧姿时会把过低的目标抬高。
        Check(posture.TryApply(BehaviorState.Evade, true, false, false, 0.4f, 7, out float target) && target == 0, "prone exit can return to stable danger crouch");
        Check(!posture.TryApply(BehaviorState.Evade, true, false, false, 0, 7.1, out _), "prone resynchronization is not repeated every frame");
        Check(posture.TryRelease(0, out float restored) && restored == 0.9f, "prone exit preserves original restoration target");
        posture.ResumeAfterProne(0.4f);
        Check(!posture.TryApply(BehaviorState.Evade, true, false, false, 0.4f, 8, out _), "prone exit cannot reacquire released layer");
    }

    /// <summary>掩体边缘小幅漂移不会循环起步，真实离开或新掩体仍能及时重置。</summary>
    private static void CoverArrivalBoundaries()
    {
        var arrival = new CoverArrival();
        Check(!arrival.Update(true, 1.45f), "outside enter threshold not arrived");
        Check(arrival.Update(true, 1.44f), "exact enter threshold arrives");
        for (int index = 0; index < 1000; index++) // 复現避险和移动分支逐帧读取同一个阈值的情况。
            Check(arrival.Update(true, index % 2 == 0 ? 1.43f : 1.45f), "cover boundary jitter preserves arrival");
        Check(arrival.Update(true, 3.24f), "exact leave threshold still arrived");
        Check(!arrival.Update(true, 3.241f), "real departure releases arrival");
        Check(!arrival.Update(true, 2), "outside enter threshold cannot instantly reenter");
        Check(arrival.Update(true, 1), "return near cover arrives again");
        arrival.Reset();
        Check(!arrival.Update(true, 2), "new cover does not inherit old arrival");
        arrival.Update(true, 1);
        Check(!arrival.Update(false, 1) && !arrival.Update(true, 2), "expired cover clears hysteresis");
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, -1f }) // 非法位置不能缓存为已经安全到达。
            Check(!arrival.Update(true, invalid), "invalid cover distance rejected");
    }

    /// <summary>非法值、无控制权和原生状态不能把姿态写进游戏执行器。</summary>
    private static void PostureInvalidInputs()
    {
        var posture = new PosturePolicy();
        Check(!posture.TryApply(BehaviorState.Evade, true, false, false, 0.9f, 0, out _), "no lease means no posture write");
        posture.Acquire(0.9f, 0);
        Check(!posture.TryApply(BehaviorState.Native, true, false, false, 0.9f, 1, out _), "native state bypasses even old danger");
        Check(!posture.TryApply(BehaviorState.Evade, true, false, false, float.NaN, 1, out _), "invalid native target rejected");
        Check(!posture.TryApply(BehaviorState.Evade, true, false, false, 0.9f, double.NaN, out _), "invalid time cannot bypass settling");
        Check(!posture.TryApply(BehaviorState.Evade, true, false, false, 0.9f, double.PositiveInfinity, out _), "infinite time rejected");
        posture.Acquire(float.NaN, 2);
        Check(posture.TryApply(BehaviorState.Evade, true, false, false, 0.9f, 2, out float target) && target == 0, "invalid saved target uses finite fallback");
        Check(!posture.TryRelease(float.NaN, out _), "invalid current target not restored");
    }

    /// <summary>构造不依赖 Unity 的队列样本。</summary>
    private static WorkRequest Request(int owner, int generation, double deadline)
    {
        return new WorkRequest(owner, generation, QueryKind.Move, Vector3.Zero, Vector3.One, deadline);
    }

    /// <summary>以统一顺序导出六条曲线用于比较。</summary>
    private static float[] Values(SkillProfile profile)
    {
        return new[] { profile.ReactionSeconds, profile.AimSeconds, profile.HearingError, profile.MemorySeconds, profile.TacticalProbability, profile.ReportSeconds };
    }

    /// <summary>比较整个能力快照。</summary>
    private static void EqualProfile(SkillProfile first, SkillProfile second, string reason)
    {
        float[] left = Values(first);
        float[] right = Values(second);
        for (int index = 0; index < left.Length; index++) Near(left[index], right[index], reason);
    }

    /// <summary>浮点断言容忍正常计算舍入，不容忍曲线改变。</summary>
    private static void Near(float actual, float expected, string reason)
    {
        Check(Math.Abs(actual - expected) < 0.00002f, reason + $" actual={actual} expected={expected}");
    }

    /// <summary>统计断言并在失败时提供可定位原因。</summary>
    private static void Check(bool condition, string reason)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(reason);
    }
}
