using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private int _cursor;
    private long _spentTicks;
    private int _workDepth;
    private int _workFrame = -1;
    private double _sumMilliseconds;
    private double _peakMilliseconds;
    private long _frames;
    private double _nextSummary;
    internal readonly PluginOptions Options;
    internal readonly ManualLogSource Log;
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
    internal long OverBudgetFrames;
    internal double MaxShotWait;

    /// <summary>初始化全局资源额度，不按 Bot 等级放大计算预算。</summary>
    public RaidRuntime(PluginOptions options, ManualLogSource log)
    {
        Options = options;
        Log = log;
        Rays = new WorkBudget(options.RayRate, 8);
        Paths = new WorkBudget(options.PathRate, 1);
        Samples = new WorkBudget(options.SampleRate, 3);
        Overlaps = new WorkBudget(options.OverlapRate, 1);
    }

    /// <summary>只为角色和 Brain 均匹配的 Bot 注册轻量上下文。</summary>
    internal void Register(BotOwner owner)
    {
        if (owner == null || owner.BotState != EBotState.Active || _byOwner.ContainsKey(owner)) return; // 激活失败或重复通知直接忽略。
        BotRole role = GameAdapter.ResolveRole(owner, Options.ManageScavs); // 用明确角色枚举分类。
        if (role == BotRole.Native) return; // Boss、护卫和未知角色不进入更新列表。
        string brain = owner.Brain?.BaseBrain?.ShortName() ?? ""; // 核对实际运行的 Brain，兼容替换失败时保留原生。
        if (brain != "PmcBear" && brain != "PmcUsec" && brain != "PMC" && brain != "Assault" && brain != "Marksman")
        { Log.LogWarning("未接管未知 Brain：" + brain); return; } // 只在注册阶段输出一次。
        var agent = new BotAgent(this, owner, ++_serial, role); // 单调编号防止对象重用时命中旧请求。
        _byOwner.Add(owner, agent); // 保存供补丁使用的直接索引。
        _byId.Add(agent.Id, agent); // 保存供查询结果验证的生命周期索引。
        _agents.Add(agent); // 加入共享调度序列。
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
    internal void Remove(BotOwner owner)
    {
        if (!_byOwner.TryGetValue(owner, out BotAgent agent)) return; // 允许重复销毁通知。
        agent.Dispose(); // 先释放本模组控制权。
        _byOwner.Remove(owner); // 清理对象索引。
        _byId.Remove(agent.Id); // 使旧请求无法匹配新实例。
        _agents.Remove(agent); // 删除共享更新项。
    }

    /// <summary>异常只降级当前 Bot，避免热循环持续抛错。</summary>
    internal void Fail(BotAgent agent, Exception exception)
    {
        Log.LogError($"Bot {agent.Id} 行为已回退原生：{exception}");
        Remove(agent.Owner);
    }

    /// <summary>先做不能延后的安全检查，再在软时间片内轮转决策。</summary>
    internal void Update(double now, int frame)
    {
        long start = BeginWork(); // 包括在此回调之前已经执行的同帧补丁工作。
        for (int index = _agents.Count - 1; index >= 0; index--) // 倒序允许直接移除死亡实例。
        {
            BotAgent agent = _agents[index]; // 当前上下文只由主线程持有。
            if (agent.Owner == null || agent.Owner.IsDead) { Remove(agent.Owner!); continue; } // Unity 销毁对象仍保留托管引用，可用于移除索引。
            try { agent.SafetyTick(now); } // 不把视线失效与恢复锁推迟到普通决策。
            catch (Exception exception) { Fail(agent, exception); } // 单 Bot 异常不拖垮整局。
        }
        Charge(start); // 关键检查也计入总耗时。
        int remaining = _agents.Count; // 本帧最多查看每个 Bot 一次。
        while (remaining-- > 0 && _agents.Count > 0 && HasTime(0.4)) // 为动作与查询预留剩余软时间片。
        {
            _cursor %= _agents.Count; // 数量变化后修正轮转索引。
            BotAgent agent = _agents[_cursor++]; // 保持 Bot 之间的公平顺序。
            if (now < agent.NextDecision || agent.Owner.BotState != EBotState.Active) continue; // 限频只作用于高层逻辑。
            start = BeginWork(); // 包含实际状态转换成本。
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
            long start = BeginWork(); // 同步 NavMesh 调用可能超出软时间片，需纳入峰值。
            try // 查询失败只影响当前 Bot。
            {
                if (!agent.Query.Step(request, now, frame)) Queue.Enqueue(request, now, _agents.Count); // 等待资源时不延长请求寿命。
            }
            catch (Exception exception) { Fail(agent, exception); } // 防止查询异常被无限重试。
            Charge(start); // 把物理和路径耗时计入插件工作。
        }
        double elapsed = _spentTicks * 1000d / Stopwatch.Frequency; // 仅计算实际执行时间，排除两回调之间的游戏工作。
        _sumMilliseconds += elapsed; // 累积平均值所需总量。
        _peakMilliseconds = Math.Max(_peakMilliseconds, elapsed); // 保留最差单帧新增耗时。
        _frames++; // 帧数用于汇总均值。
        if (elapsed > Options.MainThreadMilliseconds) OverBudgetFrames++; // 记录不可中断调用或安全检查造成的超时。
        if (Options.SummarySeconds <= 0 || now < _nextSummary) return; // 日志不开启高频字符串分配。
        _nextSummary = now + Options.SummarySeconds; // 设定下一次汇总时刻。
        Log.LogInfo($"AI汇总 bots={_agents.Count} workAvgMs={_sumMilliseconds / _frames:F3} workPeakMs={_peakMilliseconds:F3} overBudgetFrames={OverBudgetFrames} rayTokens={Rays.TotalUsed} rayCalls={RayCalls} paths={Paths.TotalUsed} samples={Samples.TotalUsed} overlaps={Overlaps.TotalUsed} pending={Queue.Count} expired={Queue.Expired} rejected={Queue.Rejected} queriesOk={CompletedQueries} queriesFailed={FailedQueries} shotPending={ShotQueue.Count} shotExpired={ShotQueue.Expired} shotWaitMaxMs={MaxShotWait * 1000:F1} blockedShots={BlockedShots} transitions={StateChanges}"); // 汇总不是整帧 FPS 或 SAIN 对照结论。
    }

    /// <summary>关键射击查询按固定容量队列轮转；每帧至少检查一项，避免普通决策耗尽软时间片。</summary>
    private void ProcessShots(double now, int frame)
    {
        int remaining = ShotQueue.Count; // 同一项本帧最多处理一次。
        int processed = 0; // 即使软预算已满也保留一次关键处理机会。
        while (remaining-- > 0 && (processed == 0 || HasTime()) && ShotQueue.TryDequeue(now, out WorkRequest request)) // 过期项不会进入物理阶段。
        {
            if (!_byId.TryGetValue(request.Owner, out BotAgent agent) || agent.Disposed || request.Generation != agent.Generation) continue; // 丢弃旧生命周期结果。
            long start = BeginWork(); // 将关键工作纳入超预算统计。
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
    internal long BeginWork()
    {
        if (_workDepth == 0 && _workFrame != Time.frameCount) // 新游戏帧才清空累计值。
        {
            _workFrame = Time.frameCount; // 保存真实帧编号。
            _spentTicks = 0; // 不丢弃同帧较早执行的补丁计时。
        }
        _workDepth++; // 内层入口不再次累加到总计时。
        return Stopwatch.GetTimestamp();
    }

    /// <summary>只累计最外层区间，包含其内部所有嵌套补丁成本。</summary>
    internal void Charge(long start)
    {
        _workDepth--; // 对应一次 BeginWork。
        if (_workDepth == 0) _spentTicks += Stopwatch.GetTimestamp() - start; // 防止同一同步工作被计算两次。
    }

    /// <summary>判断是否可以开始可推迟工作，不尝试中断已经执行的 Unity API。</summary>
    private bool HasTime(double fraction = 1)
    {
        return _spentTicks * 1000d / Stopwatch.Frequency < Options.MainThreadMilliseconds * fraction;
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
        _cursor = 0; // 恢复轮转起点。
        _spentTicks = 0; // 清空帧内工作量。
        _sumMilliseconds = _peakMilliseconds = _nextSummary = 0; // 清空时间汇总。
        MaxShotWait = 0; // 新战局重新记录射击排队峰值。
        _frames = BlockedShots = RayCalls = CompletedQueries = FailedQueries = StateChanges = OverBudgetFrames = 0; // 日志仅反映新战局。
    }
}
