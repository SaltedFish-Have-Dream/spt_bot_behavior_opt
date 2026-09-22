using System;

namespace AiBehavior.Core;

/// <summary>分别追踪察觉与武器准备，避免将并行延迟错误相加。</summary>
public sealed class ReactionGate
{
    private string? _target;
    private double _seenAt = double.PositiveInfinity;
    private double _aimAt = double.PositiveInfinity;

    /// <summary>用当前可见性更新计时；失去视线或切换目标会重新察觉。</summary>
    public void Update(string? identity, bool visible, bool weaponReady, double now)
    {
        if (!visible || string.IsNullOrEmpty(identity)) { Reset(); return; } // 遮挡后不继续精确跟踪或沿用反应计时。
        if (_target != identity) { _target = identity; _seenAt = now; _aimAt = double.PositiveInfinity; } // 新目标重新建立计时。
        if (!weaponReady) _aimAt = double.PositiveInfinity; // 换弹或切枪打断瞄准准备。
        else if (double.IsPositiveInfinity(_aimAt)) _aimAt = now; // 武器首次就绪时开始稳定计时。
    }

    /// <summary>两个准备过程都结束后才允许射击，原生射击条件仍需单独检查。</summary>
    public bool CanFire(in SkillProfile profile, double now)
    {
        return _target != null && now >= Math.Max(_seenAt + profile.ReactionSeconds, _aimAt + profile.AimSeconds);
    }

    /// <summary>在动作退出、死亡或视线丢失时撤销就绪状态。</summary>
    public void Reset()
    {
        _target = null;
        _seenAt = double.PositiveInfinity;
        _aimAt = double.PositiveInfinity;
    }
}
