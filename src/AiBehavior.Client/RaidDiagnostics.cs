using System;
using System.Text;
using AiBehavior.Core;
using BepInEx.Logging;
using UnityEngine;

namespace AiBehavior.Client;

/// <summary>固定事件种类，不使用目标名称或坐标创建无界统计键。</summary>
internal enum DiagnosticEvent
{
    Registered, RegistrationFailed, NativeSkipped, BrainSkipped, Removed, Fallback,
    VisionSaved, SoundSaved, SoundMerged, SoundOutOfRange, ClueOutOfRange, SightChanged, MemoryExpired,
    Deactivated, Reactivated, ResourceReleased, DecisionServed, DecisionForced, DecisionDelayed,
    PlayerScopeEntered, PlayerScopeLeft, PlayerDanger, DangerProne, FarGunshotIgnored,
    StateChanged, ControlClaimed, ControlReleased, ActionEntered, PoseChanged, PoseRestored,
    QueryQueued, QueryRejected, QuerySucceeded, QueryFailed, QueryDeadline, QueryNoCandidates,
    QueryNavSample, QueryNoOcclusion, QueryPathIncomplete, QueryPathCorners, QueryPathBounds, Stuck,
    MoveStarted, SearchFinished, RecoveryRequested,
    AimBlocked, AimNotReady, ShotInactive, ShotNoSight, ShotCannotShoot, ShotNotReady, ShotReacting, ShotCooldown,
    ShotQueued, ShotQueueRejected, ShotExpired, ShotBudgetWait, ShotInvalidated,
    ShotWorldBlocked, ShotPlayerBlocked, ShotInvalidPoint, ShotValidated, ShotPermitStale,
    ShotAllowed, ShotNativeAccepted, ShotNativeRejected, Count
}

/// <summary>固定容量计数与全局日志限频；明细被抑制时仍保留完整事件总量。</summary>
internal sealed class RaidDiagnostics
{
    private static readonly string[] EventNames = Enum.GetNames(typeof(DiagnosticEvent));
    private readonly PluginOptions _options;
    private readonly ManualLogSource _log;
    private readonly long[] _counts = new long[(int)DiagnosticEvent.Count];
    private WorkBudget _events;
    private FirstSeenCache _unknownBrains = new();
    internal RaidRuntime? Runtime;
    internal readonly string Run = Guid.NewGuid().ToString("N").Substring(0, 8);
    internal int Raid;
    internal bool InRaid;
    internal double StartedAt;
    internal long Suppressed;

    /// <summary>创建独立日志额度，不占用任何行为查询令牌。</summary>
    internal RaidDiagnostics(PluginOptions options, ManualLogSource log)
    {
        _options = options;
        _log = log;
        _events = new WorkBudget(options.EventLogsPerSecond, options.EventLogsPerSecond);
    }

    /// <summary>首次收到 Bot 激活时标记战局；不把这一时刻称为地图加载开始。</summary>
    internal void ObserveRaid(double now)
    {
        if (InRaid) return; // 同局多个 Bot 激活只生成一个边界。
        InRaid = true; // 后续汇总归属于当前战局。
        Raid++; // 同一次进程中的连续战局具有独立编号。
        StartedAt = now; // 用于报告自首次 Bot 激活开始的经过时间。
        Write("RAID_OBSERVED", 0, now, "source=bot-activation"); // 生命周期边界始终记录，不受明细开关影响。
    }

    /// <summary>高频拒绝原因只做常数开销计数，不创建日志字符串。</summary>
    internal void Count(DiagnosticEvent kind)
    {
        _counts[(int)kind]++;
    }

    /// <summary>先计数再判断是否输出，每个 Bot 同类事件两秒一次并共享全局额度。</summary>
    internal bool Record(DiagnosticEvent kind, BotAgent? agent, double now)
    {
        Count(kind); // 无论是否输出明细，都保留执行证据。
        if (!_options.EventLogging) return false; // 关闭明细后完全跳过日志令牌和格式化。
        int index = (int)kind; // 事件键只有固定枚举值。
        if (agent != null && now < agent.NextDiagnosticAt[index]) { Suppressed++; return false; } // 相同 Bot 的抖动合并到计数。
        if (!_events.TryTake(now, Time.frameCount)) { Suppressed++; return false; } // 所有 Bot 共用限频，不随数量放大磁盘写入。
        if (agent != null) agent.NextDiagnosticAt[index] = now + 2; // 只有真正输出后才推进单体冷却。
        return true; // 调用者此时才构造具体字段。
    }

    /// <summary>输出可按进程、战局和匿名 Bot 序号关联的单行事件。</summary>
    internal void Write(string kind, int bot, double now, string details)
    {
        long start = Runtime?.BeginWork(WorkPhase.Logging) ?? -1; // 同步格式化与日志监听器耗时单独归类。
        try { _log.LogInfo(FormattableString.Invariant($"[ABO] run={Run} raid={Raid} t={now:F2} event={kind} bot={bot} {details}")); } // 不代表异步磁盘刷盘已结束。
        finally { Runtime?.Charge(start); } // 嵌套在感知或动作中的日志不重复累计总耗时。
    }

    /// <summary>未知脑型首次记录独立于普通明细开关和令牌，重复记录只累计总量。</summary>
    internal void UnknownBrain(int role, string roleName, string brain, double now)
    {
        Count(DiagnosticEvent.BrainSkipped); // 每个被旁路的 Bot 都保留计数。
        if (!_unknownBrains.ShouldReport(role, brain, out bool overflow)) return; // 固定集合防止批量出生刷屏。
        Write(overflow ? "BRAIN_SKIPPED_OVERFLOW" : "BRAIN_SKIPPED", 0, now,
            overflow ? "limit=32 action=count-only" : $"role={roleName} brain={brain} reason=unknown-brain first=true"); // 首次兼容诊断不会被普通事件抢走额度。
    }

    /// <summary>只在低频汇总阶段格式化固定数量的累计计数。</summary>
    internal string Counters()
    {
        var text = new StringBuilder(1024); // 分配只发生在汇总时，不发生在每次事件中。
        for (int index = 0; index < _counts.Length; index++) // 长度不受地图和 Bot 数影响。
            text.Append(' ').Append(EventNames[index]).Append('=').Append(_counts[index]); // 包含零值以区分未执行和未记录。
        text.Append(" suppressed=").Append(Suppressed); // 明细缺失不能被误认为事件没有发生。
        return text.ToString();
    }

    /// <summary>最终汇总完成后重置局内计数，保留进程标识和递增战局号。</summary>
    internal void Reset()
    {
        Array.Clear(_counts, 0, _counts.Length); // 不保留上一局行为计数。
        _events = new WorkBudget(_options.EventLogsPerSecond, _options.EventLogsPerSecond); // 新战局不继承日志令牌时间。
        _unknownBrains = new FirstSeenCache(); // 下一战局重新保留各脑型的首次证据。
        Suppressed = 0; // 重置明细抑制总量。
        InRaid = false; // 等待下一次真实 Bot 激活。
    }
}
