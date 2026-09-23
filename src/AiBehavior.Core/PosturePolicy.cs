using System;

namespace AiBehavior.Core;

/// <summary>统一管理自有姿态意图；危险立即压低，普通改变稳定后提交，重复事件不重复写姿态。</summary>
public sealed class PosturePolicy
{
    public const double SettleSeconds = 0.75;
    private bool _owned;
    private bool _written;
    private float _savedPose;
    private float _lastWrittenPose;
    private float _pendingPose;
    private double _pendingSince;
    public float TargetPose { get; private set; }

    /// <summary>取得行为控制时保存当前目标姿态，不把实际动画的中间高度当作控制目标。</summary>
    public void Acquire(float nativePose, double now)
    {
        _owned = true; // 只有自有行为层持有控制时才可改变姿态。
        _written = false; // 新控制周期不继承旧动作的写入责任。
        _savedPose = ValidPose(nativePose) ? nativePose : 0.9f; // 无效原生值不传播到姿态执行器。
        TargetPose = _pendingPose = _savedPose; // 开始时保留已有姿态，而非先站起来再蹲下。
        _pendingSince = now; // 普通新意图需要完整稳定窗口。
    }

    /// <summary>根据当前行为产生唯一姿态目标，只有目标真实改变且需要写入时才返回成功。</summary>
    public bool TryApply(BehaviorState state, bool playerDanger, bool recoveryRunning, bool atCover, float nativePose, double now, out float target)
    {
        target = TargetPose; // 未提交时调用者不能使用尚在等待的姿态。
        if (!_owned || state == BehaviorState.Native || !ValidPose(nativePose) || double.IsNaN(now) || double.IsInfinity(now)) return false; // 交还原生或非法输入时不写动作。
        bool urgent = playerDanger || state == BehaviorState.Evade; // 危险压低不受普通切换等待阻塞。
        float requested = urgent || recoveryRunning || state == BehaviorState.Recover || atCover ? 0 : 0.9f; // 同一危险中移动与静止采用相同姿态。
        if (Math.Abs(requested - _pendingPose) > 0.01f) // 只有意图改变才重置稳定计时。
        {
            _pendingPose = requested; // 记录本次候选，瞬时的掩体或视线波动不能直接起身。
            _pendingSince = now; // 普通意图连续保持零点七五秒后生效。
        }
        if (Math.Abs(requested - TargetPose) <= 0.01f) return false; // 连射、重复路径与逐帧执行不重复写同一姿态。
        if (!urgent && now - _pendingSince < SettleSeconds) return false; // 普通起身和蹲下都要经过稳定窗口。
        TargetPose = target = requested; // 提交稳定目标，实际身体过渡继续由原生完成。
        if (Math.Abs(nativePose - target) <= 0.01f) return false; // 原生已是目标值时不冒领写入责任。
        _lastWrittenPose = target; // 清理时仅能恢复仍等于自己最后写入的姿态。
        _written = true; // 明确本控制周期发生过自有姿态写入。
        return true; // 调用者只需执行一次原生 SetPose。
    }

    /// <summary>自有卧姿退出会由原生抬高目标，重新同步这一已知变化，下一次统一决策再选择姿态。</summary>
    public void ResumeAfterProne(float nativePose)
    {
        if (_owned && ValidPose(nativePose)) TargetPose = nativePose; // 不重新取得已释放控制，也不覆盖最初保存的姿态。
    }

    /// <summary>只恢复仍由自己持有的目标；原生或其他层已接手的姿态不能被旧快照覆盖。</summary>
    public bool TryRelease(float nativePose, out float restorePose)
    {
        restorePose = _savedPose; // 恢复的是取得控制前的原生目标，而非上次动画中间值。
        bool restore = _owned && _written && ValidPose(nativePose) && Math.Abs(nativePose - _lastWrittenPose) <= 0.01f && Math.Abs(nativePose - _savedPose) > 0.01f; // 没写过、已被覆盖或无需改变时不设置姿态。
        _owned = _written = false; // 释放一次后禁止重复清理和继续写姿态。
        return restore; // 调用者先结束自有卧姿再按该决定恢复。
    }

    /// <summary>姿态目标只接受零至一的有限值。</summary>
    private static bool ValidPose(float value)
    {
        return !float.IsNaN(value) && value >= 0 && value <= 1;
    }
}

/// <summary>掩体到达使用不同的进入和离开距离，避免站在边缘时交战与移动来回切换。</summary>
public sealed class CoverArrival
{
    private bool _arrived;

    /// <summary>一点二米内判定到达，已经到达后超过一点八米才离开。</summary>
    public bool Update(bool hasCover, float distanceSquared)
    {
        if (!hasCover || float.IsNaN(distanceSquared) || float.IsInfinity(distanceSquared) || distanceSquared < 0) return _arrived = false; // 无有效掩体或距离时不沿用旧到达状态。
        _arrived = _arrived ? distanceSquared <= 3.24f : distanceSquared <= 1.44f; // 同一边界附近的小幅位移不改变动作选择。
        return _arrived; // 所有姿态、恢复和移动分支共用同一个判断。
    }

    /// <summary>掩体候选改变或控制交还时立即解除旧位置的到达记忆。</summary>
    public void Reset()
    {
        _arrived = false;
    }
}
