using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using AiBehavior.Core;
using BepInEx.Logging;
using EFT;
using UnityEngine;

namespace AiBehavior.Client;

/// <summary>战局内共享调度器；所有入口均在 Unity 主线程执行。</summary>
public sealed class RaidRuntime : IDisposable
{
    private readonly Dictionary<BotOwner, BotAgent> _byOwner = new();
    private readonly Dictionary<int, BotAgent> _byId = new();
    private readonly List<BotAgent> _agents = new();
    private int _serial;
    private DecisionScheduler _decisions = new();
    private WorkProfiler _profiler = new();
    private double _maxDecisionWait;
    private double _decisionWaitSum;
    private long _decisionCount;
    private bool _endingRaid;
    private double _nextSummary;
    internal readonly PluginOptions Options;
    internal readonly ManualLogSource Log;
    internal readonly RaidDiagnostics Diagnostics;
    internal WorkQueue Queue = new();
    internal WorkQueue ShotQueue = new();
    internal WorkBudget Rays;
    internal WorkBudget Paths;
    internal WorkBudget Samples;
    internal WorkBudget Overlaps;
    internal long BlockedShots;
    internal long RayCalls;
    internal long CompletedQueries;
    internal long FailedQueries;
    internal long StateChanges;
    internal double MaxShotWait;

    /// <summary>初始化全局资源额度，不按 Bot 等级放大计算预算。</summary>
    public RaidRuntime(PluginOptions options, ManualLogSource log)
    {
        Options = options;
        Log = log;
        Diagnostics = new RaidDiagnostics(options, log);
        Diagnostics.Runtime = this; // 日志区间与行为区间共用同一嵌套计时器。
        Rays = new WorkBudget(options.RayRate, 8);
        Paths = new WorkBudget(options.PathRate, 1);
        Samples = new WorkBudget(options.SampleRate, 3);
        Overlaps = new WorkBudget(options.OverlapRate, 1);
    }

    /// <summary>只为角色和 Brain 均匹配的 Bot 注册轻量上下文。</summary>
    internal void Register(BotOwner owner)
    {
        if (owner == null || owner.BotState != EBotState.Active || _byOwner.ContainsKey(owner)) return; // 激活失败或重复通知直接忽略。
        double now = Time.time; // 生命周期日志与行为使用相同局内时钟。
        Diagnostics.ObserveRaid(now); // 即使全是原生角色也能看到激活入口确实执行。
        BotRole role = GameAdapter.ResolveRole(owner, Options.ManageScavs); // 用明确角色枚举分类。
        if (role == BotRole.Native) // Boss、护卫和未知角色不进入更新列表。
        {
            if (Diagnostics.Record(DiagnosticEvent.NativeSkipped, null, now)) Diagnostics.Write("NATIVE_SKIPPED", 0, now, $"role={owner.Profile.Info.Settings.Role} reason=role-filter"); // 不记录昵称或完整档案 ID。
            return;
        }
        string brain = owner.Brain?.BaseBrain?.ShortName() ?? ""; // 核对实际运行的 Brain，兼容替换失败时保留原生。
        if (brain != "PmcBear" && brain != "PmcUsec" && brain != "PMC" && brain != "Assault" && brain != "Marksman")
        {
            Diagnostics.UnknownBrain((int)owner.Profile.Info.Settings.Role, owner.Profile.Info.Settings.Role.ToString(), brain, now); // 首次未知脑型不消耗普通明细额度。
            return;
        }
        var agent = new BotAgent(this, owner, ++_serial, role); // 单调编号防止对象重用时命中旧请求。
        _byOwner.Add(owner, agent); // 保存供补丁使用的直接索引。
        _byId.Add(agent.Id, agent); // 保存供查询结果验证的生命周期索引。
        _agents.Add(agent); // 加入共享调度序列。
        if (Diagnostics.Record(DiagnosticEvent.Registered, agent, now)) Diagnostics.Write("REGISTERED", agent.Id, now, FormattableString.Invariant($"role={owner.Profile.Info.Settings.Role} brain={brain} level={owner.Profile.Info.Level} reaction={agent.Skill.ReactionSeconds:F3} aim={agent.Skill.AimSeconds:F3}")); // 注册只能证明上下文存在，不能替代实际动作证据。
        if (Options.TraceBots) Log.LogInfo($"注册 {agent.Id} role={owner.Profile.Info.Settings.Role} brain={brain} level={owner.Profile.Info.Level} reaction={agent.Skill.ReactionSeconds:F3} aim={agent.Skill.AimSeconds:F3}"); // 诊断默认关闭。
    }

    /// <summary>读取有效上下文，不在高频补丁中搜索场景或反射。</summary>
    internal bool TryGet(BotOwner owner, out BotAgent agent)
    {
        if (owner != null && _byOwner.TryGetValue(owner, out agent!) && !agent.Disposed) return true;
        agent = null!;
        return false;
    }

    /// <summary>销毁前撤销动作与请求，移除所有对游戏对象的引用。</summary>
    internal void Remove(BotOwner owner, string reason = "bot-dispose")
    {
        if (!_byOwner.TryGetValue(owner, out BotAgent agent)) return; // 允许重复销毁通知。
        agent.Dispose(); // 先释放本模组控制权。
        _byOwner.Remove(owner); // 清理对象索引。
        _byId.Remove(agent.Id); // 使旧请求无法匹配新实例。
        int removedIndex = _agents.IndexOf(agent); // 删除前保留原位置供轮转游标修正。
        _decisions.RemovedAt(removedIndex); // 不跳过左移后的下一名候选。
        if (removedIndex >= 0) _agents.RemoveAt(removedIndex); // 删除共享更新项。
        if (Diagnostics.Record(DiagnosticEvent.Removed, agent, Time.time)) Diagnostics.Write("REMOVED", agent.Id, Time.time, $"reason={reason} remaining={_agents.Count}"); // 先完成释放，再记录索引剩余量。
    }

    /// <summary>异常只降级当前 Bot，避免热循环持续抛错。</summary>
    internal void Fail(BotAgent agent, Exception exception)
    {
        Diagnostics.Count(DiagnosticEvent.Fallback); // 异常不受明细限频，汇总保留降级数量。
        Log.LogError($"[ABO] run={Diagnostics.Run} raid={Diagnostics.Raid} event=FALLBACK bot={agent.Id} state={agent.State} generation={agent.Generation} controlled={agent.Controlled} reason={exception}");
        Remove(agent.Owner, "exception-fallback");
    }

    /// <summary>先做不能延后的安全检查，再在软时间片内轮转决策。</summary>
    internal void Update(double now, int frame)
    {
        long start = BeginWork(WorkPhase.Safety); // 安全检查含停用边沿处理，不进行物理查询。
        for (int index = _agents.Count - 1; index >= 0; index--) // 倒序允许直接移除死亡实例。
        {
            BotAgent agent = _agents[index]; // 当前上下文只由主线程持有。
            if (agent.Owner == null || agent.Owner.IsDead) { Remove(agent.Owner!, "dead-or-destroyed"); continue; } // Unity 销毁对象仍保留托管引用，可用于移除索引。
            try { agent.SafetyTick(now); } // 不把视线失效与恢复锁推迟到普通决策。
            catch (Exception exception) { Fail(agent, exception); } // 单 Bot 异常不拖垮整局。
        }
        Charge(start); // 关键检查也计入总耗时。
        _decisions.BeginFrame(_agents.Count); // 本帧最多查看初始列表长度个候选。
        while (_decisions.TryNext(_agents.Count, HasTime(0.4), out int candidate)) // 至少一个到期决策不受已消耗软预算阻塞。
        {
            BotAgent agent = _agents[candidate]; // 轮转位置跨帧保留，不按等级排序。
            if (now < agent.NextDecision || agent.Owner.BotState != EBotState.Active) continue; // 限频只作用于高层逻辑。
            double wait = Math.Max(0, now - agent.NextDecision); // 使用局内时钟记录超过截止时刻的实际等待。
            _maxDecisionWait = Math.Max(_maxDecisionWait, wait); // 累计峰值不会被后续快速响应稀释。
            _decisionWaitSum += wait; // 平均值只统计实际开始执行的到期决策。
            _decisionCount++; // 与等待总量使用相同分母。
            Diagnostics.Count(DiagnosticEvent.DecisionServed); // 保留每局执行总量。
            if (!HasTime(0.4)) Diagnostics.Count(DiagnosticEvent.DecisionForced); // 明确记录软预算外的保障机会。
            if (wait > 0.1) Diagnostics.Count(DiagnosticEvent.DecisionDelayed); // 大于一百毫秒的迟到可用于排查局内体感。
            _decisions.Served(); // 即使异常也只保障一次到期执行机会。
            start = BeginWork(WorkPhase.Decision); // 包含实际状态转换成本。
            try { agent.Decide(now); } // 等级不影响执行频率。
            catch (Exception exception) { Fail(agent, exception); } // 失败后交还原生控制。
            Charge(start); // 累计本帧新增主线程耗时。
        }
    }

    /// <summary>处理有界查询的一小步，耗尽预算的请求保留截止时间后重新排队。</summary>
    internal void ProcessQueries(double now, int frame)
    {
        ProcessShots(now, frame); // 按 Bot 轮转验证射击，避免固定更新顺序垄断额度。
        int attempts = Queue.Count; // 本帧不重复处理刚重新入队的同一项。
        while (attempts-- > 0 && HasTime() && Queue.TryDequeue(now, out WorkRequest request)) // 轮转任务并删除过期项。
        {
            if (!_byId.TryGetValue(request.Owner, out BotAgent agent) || !agent.Accepts(request)) continue; // 检查生命周期、动作代次和控制权。
            long start = BeginWork(WorkPhase.Query); // 普通查询包含导航、采样和掩体检测，单次不可中断。
            try // 查询失败只影响当前 Bot。
            {
                if (!agent.Query.Step(request, now, frame)) Queue.Enqueue(request, now, _agents.Count); // 等待资源时不延长请求寿命。
            }
            catch (Exception exception) { Fail(agent, exception); } // 防止查询异常被无限重试。
            Charge(start); // 把物理和路径耗时计入插件工作。
        }
        if (Options.SummarySeconds <= 0 || now < _nextSummary) return; // 日志不开启高频字符串分配。
        _nextSummary = now + Options.SummarySeconds; // 设定下一次汇总时刻。
        if (Diagnostics.InRaid) WriteSummary(now, "periodic"); // 即使所有受管 Bot 已死亡也保留本局最终总量。
        else Diagnostics.Write("HEARTBEAT", 0, now, "status=waiting-for-bot-activation"); // 没有激活入口时明确显示插件仍在更新。
    }

    /// <summary>低频输出当前执行状态和累计原因计数，最终汇总不依赖周期是否已到。</summary>
    private void WriteSummary(double now, string reason)
    {
        long start = BeginWork(WorkPhase.Logging); // 低频汇总的字符串格式化也纳入日志阶段。
        try { WriteSummaryDetails(now, reason); } // 当前汇总将在下一次帧结算时计入总量。
        finally { Charge(start); } // 结束边界最终汇总不递归计量自身。
    }

    /// <summary>输出已结算帧统计、当前活动规模和到期决策积压。</summary>
    private void WriteSummaryDetails(double now, string reason)
    {
        int controlled = 0; // 区分完成注册和真正获得行为层控制权。
        int visible = 0; // 只读取已经缓存的感知状态。
        int active = 0; // 区分注册数和实际参与安全检查的活动数。
        int due = 0; // 记录尚未得到服务的到期项，避免只看已服务等待值。
        double oldestDue = 0; // 当前积压最大迟到值。
        var states = new int[(int)BehaviorState.Disengage + 1]; // 状态集合固定，不建立按敌人扩张的统计表。
        for (int index = 0; index < _agents.Count; index++) // 仅汇总时扫描受管列表。
        {
            BotAgent agent = _agents[index]; // 不访问新的敌情或进行物理查询。
            if (agent.Controlled) controlled++; // 记录实际持有控制权的 Bot 数。
            if (agent.HasVisibleTarget) visible++; // 记录已知直接可见目标数量。
            states[(int)agent.State]++; // 记录各个缓存决策状态分布。
            if (agent.Owner != null && agent.Owner.BotState == EBotState.Active) // 停用项不计入决策积压。
            {
                active++; // 活动和停用数量总和等于受管数。
                if (agent.NextDecision <= now) { due++; oldestDue = Math.Max(oldestDue, now - agent.NextDecision); } // 同时保留未服务的最坏等待。
            }
        }
        double tickMs = 1000d / Stopwatch.Frequency; // 所有阶段和总量使用相同换算。
        Diagnostics.Write("SUMMARY", 0, now, FormattableString.Invariant($"AI汇总 reason={reason} elapsed={now - Diagnostics.StartedAt:F1} bots={_agents.Count} active={active} inactive={_agents.Count - active} controlled={controlled} visible={visible} states={string.Join(",", states)} workFrames={_profiler.Frames} workAvgMs={(_profiler.Frames == 0 ? 0 : _profiler.TotalFrameTicks * tickMs / _profiler.Frames):F3} workPeakMs={_profiler.PeakFrameTicks * tickMs:F3} overBudgetFrames={_profiler.OverBudgetFrames} decisionDue={due} decisionOldestDueMs={oldestDue * 1000:F1} decisionWaitAvgMs={(_decisionCount == 0 ? 0 : _decisionWaitSum * 1000 / _decisionCount):F1} decisionWaitMaxMs={_maxDecisionWait * 1000:F1} rayTokens={Rays.TotalUsed} rayCalls={RayCalls} paths={Paths.TotalUsed} samples={Samples.TotalUsed} overlaps={Overlaps.TotalUsed} pending={Queue.Count} expired={Queue.Expired} rejected={Queue.Rejected} queriesOk={CompletedQueries} queriesFailed={FailedQueries} shotPending={ShotQueue.Count} shotExpired={ShotQueue.Expired} shotWaitMaxMs={MaxShotWait * 1000:F1} blockedShots={BlockedShots} transitions={StateChanges}")); // 帧总量统计到上一已结束帧，不能视为完整游戏帧时间。
        var phases = new StringBuilder(1024); // 仅每次低频汇总分配一次固定规模缓冲。
        for (int index = 0; index < (int)WorkPhase.Count; index++) // 阶段数量固定，不为每个 Bot 建立计时表。
            phases.Append(FormattableString.Invariant($" {((WorkPhase)index)}TotalMs={_profiler.TotalTicks[index] * tickMs:F3} {((WorkPhase)index)}Calls={_profiler.Calls[index]} {((WorkPhase)index)}CallPeakMs={_profiler.PeakCallTicks[index] * tickMs:F3} {((WorkPhase)index)}AtPeakMs={_profiler.PeakFramePhases[index] * tickMs:F3}")); // 独占总量与包含子调用的单次峰值明确分开。
        Diagnostics.Write("TIMING", 0, now, FormattableString.Invariant($"scope=raid-cumulative peakFrame={_profiler.PeakFrame} peakT={_profiler.PeakTime:F2} stackOverflows={_profiler.StackOverflows}") + phases); // 最差帧组成可以与峰值时刻的感知和查询事件对齐。
        Diagnostics.Write("COUNTERS", 0, now, "scope=raid-cumulative" + Diagnostics.Counters()); // 固定事件原因即使被限频也不会消失。
    }

    /// <summary>关键射击查询按固定容量队列轮转；每帧至少检查一项，避免普通决策耗尽软时间片。</summary>
    private void ProcessShots(double now, int frame)
    {
        int remaining = ShotQueue.Count; // 同一项本帧最多处理一次。
        int processed = 0; // 即使软预算已满也保留一次关键处理机会。
        while (remaining-- > 0 && (processed == 0 || HasTime()) && ShotQueue.TryDequeue(now, out WorkRequest request)) // 过期项不会进入物理阶段。
        {
            if (!_byId.TryGetValue(request.Owner, out BotAgent agent) || agent.Disposed || request.Generation != agent.Generation) continue; // 丢弃旧生命周期结果。
            long start = BeginWork(WorkPhase.Shooting); // 与普通导航分开记录射击验证成本。
            try // 单 Bot 查询异常不影响其他排队者。
            {
                if (!agent.VerifyShot(request, now, frame)) ShotQueue.Enqueue(request, now, _agents.Count); // 暂无令牌时排到后面，截止时间不变。
            }
            catch (Exception exception) { Fail(agent, exception); } // 出错后交还原生行为。
            finally { Charge(start); } // 所有路径都结束计时区间。
            processed++; // 后续工作重新遵守软时间片。
        }
    }

    /// <summary>建立可嵌套的计时区间，避免动作内部的射击补丁被重复累计。</summary>
    internal long BeginWork(WorkPhase phase)
    {
        if (!Diagnostics.InRaid || _endingRaid) return -1; // 菜单与最终汇总不影响战局均值。
        _profiler.AdvanceFrame(Time.frameCount, Time.time, (long)(Options.MainThreadMilliseconds * Stopwatch.Frequency / 1000)); // 到下一帧才结算上一帧，保留晚于 LateUpdate 的回调。
        long start = Stopwatch.GetTimestamp(); // 只计同步插件区间，不计两次回调之间的游戏工作。
        _profiler.Begin(phase, start); // 嵌套阶段独占归属，避免射击和日志重复累计。
        return start;
    }

    /// <summary>结束当前阶段区间，嵌套成本独占归属且总时长不重复累加。</summary>
    internal void Charge(long start)
    {
        if (start >= 0) _profiler.End(start, Stopwatch.GetTimestamp()); // 菜单入口没有开始计时，不改变区间栈。
    }

    /// <summary>判断是否可以开始可推迟工作，不尝试中断已经执行的 Unity API。</summary>
    private bool HasTime(double fraction = 1)
    {
        return _profiler.FrameTicks * 1000d / Stopwatch.Frequency < Options.MainThreadMilliseconds * fraction;
    }

    /// <summary>使用当前活跃规模入队，避免每个 Bot 扩充全局队列。</summary>
    internal bool Enqueue(in WorkRequest request, double now)
    {
        if (Queue.Count + ShotQueue.Count >= Math.Min(256L, (long)_agents.Count * 4)) return false; // 两类队列共享总量上限。
        return Queue.Enqueue(request, now, _agents.Count);
    }

    /// <summary>为新连射单独排队，查询仍共享全局物理额度。</summary>
    internal bool EnqueueShot(in WorkRequest request, double now)
    {
        if (Queue.Count + ShotQueue.Count >= Math.Min(256L, (long)_agents.Count * 4)) return false; // 射击优先不代表可以无限排队。
        return ShotQueue.Enqueue(request, now, _agents.Count);
    }

    /// <summary>战局结束后清空所有对象与计数，保持实例可服务下一战局。</summary>
    public void Dispose()
    {
        EndRaid("plugin-shutdown");
    }

    /// <summary>保留清理前的诊断结果，再释放所有局内对象并明确报告清理边界。</summary>
    internal void EndRaid(string reason)
    {
        bool hadRaid = Diagnostics.InRaid; // 防止重复 Dispose 输出虚假的第二局结束。
        _profiler.CompleteFrame((long)(Options.MainThreadMilliseconds * Stopwatch.Frequency / 1000)); // 结算最后一帧，重复结束不会双计。
        _endingRaid = true; // 最终汇总和清场排除在战局工作统计之外。
        if (hadRaid) WriteSummary(Time.time, reason); // 短战局即使没达到汇总间隔，也有最终执行证据。
        for (int index = _agents.Count - 1; index >= 0; index--) _agents[index].Dispose(); // 解除动作持有与队列请求。
        _agents.Clear(); // 不跨局持有 Bot。
        _byOwner.Clear(); // 释放对象键。
        _byId.Clear(); // 释放结果路由。
        Queue = new WorkQueue(); // 清除旧任务及其坐标快照。
        ShotQueue = new WorkQueue(); // 清空旧战局的待验证射击。
        Rays = new WorkBudget(Options.RayRate, 8); // 每局重新建立统计与令牌。
        Paths = new WorkBudget(Options.PathRate, 1); // 不继承上一局的额度时间戳。
        Samples = new WorkBudget(Options.SampleRate, 3); // 重置采样统计。
        Overlaps = new WorkBudget(Options.OverlapRate, 1); // 重置局部扫描统计。
        _decisions = new DecisionScheduler(); // 恢复轮转起点。
        _profiler = new WorkProfiler(); // 不跨局保留阶段或峰值。
        _maxDecisionWait = _decisionWaitSum = _nextSummary = 0; // 清空时间汇总。
        _decisionCount = 0; // 与平均等待的分母同步重置。
        MaxShotWait = 0; // 新战局重新记录射击排队峰值。
        BlockedShots = RayCalls = CompletedQueries = FailedQueries = StateChanges = 0; // 日志仅反映新战局。
        if (hadRaid) Diagnostics.Write("RAID_END", 0, Time.time, $"reason={reason} remainingBots={_agents.Count} pending={Queue.Count + ShotQueue.Count}"); // 清理后输出真实索引数量。
        Diagnostics.Reset(); // 最后清除统计，保留进程标识和战局序号。
        _endingRaid = false; // 同一插件实例可以服务下一战局。
    }
}
