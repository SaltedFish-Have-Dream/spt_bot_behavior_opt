using System;
using System.Numerics;

namespace AiBehavior.Core;

/// <summary>近距持续目视交战时只提出一次短距离侧移候选，不扫描世界或突破导航额度。</summary>
public sealed class EngagementFootwork
{
    private readonly int _side;
    private string? _identity;
    private double _engagedAt;
    private double _lastVisibleAt = double.NegativeInfinity;
    private bool _used;

    /// <summary>按 Bot 固定奇偶侧别创建个体，不在每帧重新随机方向。</summary>
    public EngagementFootwork(int side)
    {
        _side = side == 0 ? 0 : 1; // 仅使用已经有界的左右两侧候选。
    }

    /// <summary>只在个人视觉确认本机玩家时维持交战窗口；长时间失视才开始新的窗口。</summary>
    public void ObserveVisible(string? identity, double now)
    {
        if (string.IsNullOrEmpty(identity) || double.IsNaN(now) || double.IsInfinity(now) || now < 0 || now < _lastVisibleAt) return; // 无效或倒序回调不推进机会。
        if (_identity != identity || now - _lastVisibleAt > 6) // 换目标或离开交战六秒后才获得新的单次机会。
        {
            _identity = identity; // 同目标短时视觉抖动仍沿用同一窗口。
            _engagedAt = now; // 必须持续交战一段时间才产生自然换位。
            _used = false; // 新窗口最多提出一次主动侧移。
        }
        _lastVisibleAt = now; // 只由已确认的个人视觉更新。
    }

    /// <summary>六至二十五米的开阔交战持续一点五秒后，输出一次三米侧移候选。</summary>
    public bool TryPlan(bool allowed, Vector3 origin, Vector3 player, double now, out Vector3 point)
    {
        point = default; // 条件不满足时不能继续使用上次候选。
        if (!allowed || _used || _identity == null || !ThreatMemory.Finite(origin) || !ThreatMemory.Finite(player) || double.IsNaN(now) || double.IsInfinity(now) || now < _lastVisibleAt || now - _lastVisibleAt > 0.35 || now - _engagedAt < 1.5) return false; // 只在当前新鲜视觉和稳定交战中规划。
        float dx = player.X - origin.X; // 只比较水平交战距离。
        float dz = player.Z - origin.Z; // 楼层高度不改变近距侧移资格。
        float distanceSquared = dx * dx + dz * dz; // 平方距离避免每帧开方。
        if (distanceSquared < 36 || distanceSquared > 625) return false; // 贴脸或远距离保持原有掩体/射击策略。
        return AdaptiveMovement.TrySideStep(origin, player, _side, out point); // 真正可达性继续由共享完整路径查询验证。
    }

    /// <summary>成功排队后消耗本交战窗口名额，不能每帧重新规划。</summary>
    public void MarkRequested()
    {
        if (_identity != null) _used = true; // 尚无有效目标时不消耗下次接敌机会。
    }

    /// <summary>挡枪反馈已经提出换位时，共用同一名额避免紧接着再次侧移。</summary>
    public void MarkOtherReposition()
    {
        if (_identity != null) _used = true; // 不改变原有挡枪反馈的两侧安全上限。
    }

    /// <summary>玩家情境结束、死亡或 Bot 销毁时释放身份和短期窗口。</summary>
    public void Clear()
    {
        _identity = null; // 不保留旧玩家身份引用。
        _engagedAt = 0; // 下次视觉从新交战时刻计算。
        _lastVisibleAt = double.NegativeInfinity; // 独立生命周期不继承旧时钟。
        _used = false; // 新上下文可以重新侧移一次。
    }
}
