using System;
using System.Numerics;
using AiBehavior.Core;

namespace AiBehavior.Core.Tests;

/// <summary>不依赖游戏进程的行为回归检查，失败返回非零退出码。</summary>
internal static class Program
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
            Console.WriteLine($"PASS: 23 scenarios, {_assertions} assertions."); // 输出实际验证数量。
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
