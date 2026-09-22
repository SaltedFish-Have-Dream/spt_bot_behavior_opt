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
            Console.WriteLine($"PASS: 17 scenarios, {_assertions} assertions."); // 输出实际验证数量。
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
