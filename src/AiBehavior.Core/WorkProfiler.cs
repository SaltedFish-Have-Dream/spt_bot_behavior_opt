using System;

namespace AiBehavior.Core;

/// <summary>固定阶段统计；嵌套区间按独占时间归属，阶段总量可以相加。</summary>
public enum WorkPhase { Safety, Decision, Action, Perception, Shooting, Query, Lifecycle, Logging, ActionAim, ActionAimClear, ActionSearch, ActionMove, ActionEvade, ActionRecovery, ActionPosture, Count }

/// <summary>无需逐帧分配的主线程计时器，由调用方提供单调时钟刻度。</summary>
public sealed class WorkProfiler
{
    private readonly WorkPhase[] _stack = new WorkPhase[32];
    private int _depth;
    private int _overflowDepth;
    private long _last;
    private bool _frameOpen;
    private readonly long[] _frameTicks = new long[(int)WorkPhase.Count];
    private readonly long[] _frameCalls = new long[(int)WorkPhase.Count];
    private readonly long[] _frameCallPeaks = new long[(int)WorkPhase.Count];
    public readonly long[] TotalTicks = new long[(int)WorkPhase.Count];
    public readonly long[] Calls = new long[(int)WorkPhase.Count];
    public readonly long[] PeakCallTicks = new long[(int)WorkPhase.Count];
    public readonly long[] PeakFramePhases = new long[(int)WorkPhase.Count];
    public int Frame { get; private set; } = -1;
    public long FrameTicks { get; private set; }
    public long TotalFrameTicks { get; private set; }
    public long Frames { get; private set; }
    public long PeakFrameTicks { get; private set; }
    public int PeakFrame { get; private set; }
    public double PeakTime { get; private set; }
    public long OverBudgetFrames { get; private set; }
    public long StackOverflows { get; private set; }
    private double _frameTime;

    /// <summary>进入新帧前结算上一帧，包含 LateUpdate 之后才执行的动作回调。</summary>
    public void AdvanceFrame(int frame, double time, long budgetTicks)
    {
        if (_frameOpen && Frame == frame) return; // 同帧多个入口共享累计值。
        CompleteFrame(budgetTicks); // 只结算一次完整观察帧。
        Frame = frame; // 保存 Unity 帧号用于定位峰值。
        _frameTime = time; // 保留局内时钟，便于与战斗事件对齐。
        _frameOpen = true; // 新帧即使没有 Bot 工作也参与均值。
        FrameTicks = 0; // 清空本帧软预算占用。
        Array.Clear(_frameTicks, 0, _frameTicks.Length); // 固定数组复用，不分配逐帧容器。
        Array.Clear(_frameCalls, 0, _frameCalls.Length); // 调用次数也按完整帧结算。
        Array.Clear(_frameCallPeaks, 0, _frameCallPeaks.Length); // 清空本帧单次调用峰值。
    }

    /// <summary>结束最后一帧；重复汇总或结束通知不重复统计。</summary>
    public void CompleteFrame(long budgetTicks)
    {
        if (!_frameOpen || _depth != 0 || _overflowDepth != 0) return; // 只结算已闭合的同步工作区间。
        _frameOpen = false; // 防止重复累计。
        Frames++; // 均值分母只包含观察到的游戏帧。
        TotalFrameTicks += FrameTicks; // 帧总量不包含区间之间的游戏工作。
        for (int index = 0; index < _frameTicks.Length; index++) // 所有对外累计值使用相同的已结束帧边界。
        {
            TotalTicks[index] += _frameTicks[index]; // 阶段独占总量之和等于帧总量。
            Calls[index] += _frameCalls[index]; // 当前未结束帧不提前混入分母。
            PeakCallTicks[index] = Math.Max(PeakCallTicks[index], _frameCallPeaks[index]); // 保存已结算调用的峰值。
        }
        if (FrameTicks > budgetTicks) OverBudgetFrames++; // 记录软预算无法阻止的超时。
        if (FrameTicks <= PeakFrameTicks) return; // 仅新峰值复制分阶段组成。
        PeakFrameTicks = FrameTicks; // 保存累计最差帧。
        PeakFrame = Frame; // 记录真实帧号。
        PeakTime = _frameTime; // 记录该帧局内时间。
        Array.Copy(_frameTicks, PeakFramePhases, _frameTicks.Length); // 与最差帧对应，不能拿各阶段独立峰值相加。
    }

    /// <summary>开始阶段区间，暂停父阶段的独占计时。</summary>
    public void Begin(WorkPhase phase, long timestamp)
    {
        if (_depth == _stack.Length) { _overflowDepth++; StackOverflows++; return; } // 极端递归并入父阶段，不抛错影响游戏。
        Accrue(timestamp); // 把进入子阶段前的时间留给父阶段。
        _stack[_depth++] = phase; // 固定栈保存嵌套归属。
        _frameCalls[(int)phase]++; // 单次调用峰值使用包含子调用的时长。
        _last = timestamp; // 子阶段从当前时刻开始。
    }

    /// <summary>结束阶段并恢复父阶段；单次峰值包含子阶段但总耗时不重复计算。</summary>
    public void End(long start, long timestamp)
    {
        if (_overflowDepth > 0) { _overflowDepth--; return; } // 未入固定栈的嵌套成本归属最深父阶段。
        if (_depth == 0) return; // 防御卸载期间的迟到结束通知。
        Accrue(timestamp); // 结算当前独占区间。
        int phase = (int)_stack[--_depth]; // 弹出后后续时间归属父阶段。
        _frameCallPeaks[phase] = Math.Max(_frameCallPeaks[phase], timestamp - start); // 保存包含子调用的单次峰值供诊断。
        _last = timestamp; // 区间外的游戏成本不会计入下次入口。
    }

    /// <summary>把相邻计时边界之间的刻度只归给栈顶阶段。</summary>
    private void Accrue(long timestamp)
    {
        if (_depth == 0) return; // 两次插件回调之间的游戏耗时不属于本模组。
        long ticks = Math.Max(0, timestamp - _last); // 防止异常时钟倒退污染累计值。
        int phase = (int)_stack[_depth - 1]; // 栈顶代表当前真实执行阶段。
        _frameTicks[phase] += ticks; // 保留当前帧阶段组成。
        FrameTicks += ticks; // 每段时长仅计入总量一次。
    }
}
