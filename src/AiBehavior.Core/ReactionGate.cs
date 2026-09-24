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

    /// <summary>首次发现并行等待反应和武器准备；已经就绪的同位置再露头可省略重复计时，原生瞄准另查。</summary>
    public bool CanFire(in SkillProfile profile, double now, float familiarFactor = 1f, bool readyOnRepeek = false)
    {
        if (float.IsNaN(familiarFactor) || familiarFactor < 0.8f || familiarFactor > 1f) familiarFactor = 1f; // 不允许调用者取消或放大等级门槛。
        if (readyOnRepeek && _target != null && now >= _seenAt && _aimAt <= _seenAt) return true; // 仅武器在重新目视起点就绪且此后未中断时，才跳过重复的模组计时。
        return _target != null && now >= Math.Max(_seenAt + profile.ReactionSeconds * familiarFactor, _aimAt + profile.AimSeconds * familiarFactor); // 两项等待并行，原生瞄准就绪仍须单独通过。
    }

    /// <summary>在动作退出、死亡或视线丢失时撤销就绪状态。</summary>
    public void Reset()
    {
        _target = null;
        _seenAt = double.PositiveInfinity;
        _aimAt = double.PositiveInfinity;
    }
}
