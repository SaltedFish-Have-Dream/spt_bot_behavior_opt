using System;
using System.Numerics;

namespace AiBehavior.Core;

/// <summary>沿已验证路线接近线索前短暂停看，每个区域最多两次，不新增查询也不延长线索。</summary>
public sealed class SearchPacing
{
    private string? _identity;
    private Vector3 _anchor;
    private Vector3 _lastPausePosition;
    private double _nextPauseAt;
    private double _lastTime;
    public int Count { get; private set; }
    public double PauseUntil { get; private set; }

    /// <summary>真正换身份或换区域才重置名额，重新接管和同区域重复枪声不重置。</summary>
    public void SetArea(string identity, Vector3 anchor)
    {
        if (string.IsNullOrEmpty(identity) || !ThreatMemory.Finite(anchor)) return; // 非法线索不能创建观察窗口。
        if (_identity == identity && Vector3.DistanceSquared(_anchor, anchor) <= 16) return; // 小幅误差和控制交接仍属于同一区域。
        Clear(); // 新区域获得独立的有限观察次数。
        _identity = identity; // 保存已有身份字符串，不构造派生键。
        _anchor = anchor; // 仅保留当时的观察快照。
    }

    /// <summary>只在剩余合法路线十八米内停看，冷却六秒且需前进六米，紧急条件立即取消。</summary>
    public bool TryPause(bool allowed, Vector3 position, float remaining, float tacticalProbability, double now, double expires, out bool started)
    {
        started = false; // 重复执行不能被记录为新的一次停看。
        if (!allowed || _identity == null || !ThreatMemory.Finite(position) || double.IsNaN(now) || double.IsInfinity(now) || now < _lastTime ||
            double.IsNaN(expires) || double.IsInfinity(expires) || expires <= now || float.IsNaN(remaining) || float.IsInfinity(remaining) || remaining <= 1.2f || remaining > 18)
        { Cancel(); return false; } // 可见交火、近弹、恢复、失效路线或过期线索优先。
        _lastTime = now; // 防止时钟回退延长停看。
        if (now < PauseUntil) return true; // 活动窗口只读取，不刷新终点。
        if (Count >= 2 || now < _nextPauseAt || expires - now < 2 || (Count > 0 && Vector3.DistanceSquared(position, _lastPausePosition) < 36)) return false; // 不连续原地停看，也不给即将过期的线索再增加等待。
        float ability = float.IsNaN(tacticalProbability) ? 0.3f : Math.Max(0, Math.Min(1, tacticalProbability)); // 参数只改变等待长度，不改变额度。
        PauseUntil = now + 0.45 + 0.35 * (1 - ability); // 默认低等级略谨慎，高等级更快完成同一次观察。
        _nextPauseAt = now + 6; // 冷却不受同一枪声刷新影响。
        _lastPausePosition = position; // 下次必须有实际空间进展。
        Count++; // 次数只增不减，取消也不会退款重开。
        started = true; // 日志只在真正开始时记录一次。
        return true;
    }

    /// <summary>危险或原生抢占立即结束当前停看，但保留本区域名额和冷却。</summary>
    public void Cancel()
    {
        PauseUntil = 0;
    }

    /// <summary>调查结束或新区域开始时清理全部值状态。</summary>
    public void Clear()
    {
        _identity = null;
        Count = 0;
        PauseUntil = _nextPauseAt = _lastTime = 0;
        _anchor = _lastPausePosition = default;
    }
}
