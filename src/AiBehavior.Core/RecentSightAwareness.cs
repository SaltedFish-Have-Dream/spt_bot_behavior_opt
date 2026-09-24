using System;
using System.Numerics;

namespace AiBehavior.Core;

/// <summary>只根据个人真实视觉快照保留短暂守点与同区域再次探头资格。</summary>
public sealed class RecentSightAwareness
{
    private string? _identity;
    private Vector3 _lastPosition;
    private double _lastSeenAt = double.NegativeInfinity;
    private double _watchUntil;
    private double _repeekUntil;
    private bool _lost;
    private bool _primed;

    /// <summary>原生个人视觉已确认可见时，更新本 Bot 自己看见的位置。</summary>
    public void ObserveVisible(string? identity, Vector3 position, double now)
    {
        if (string.IsNullOrEmpty(identity) || !ThreatMemory.Finite(position) || double.IsNaN(now) || double.IsInfinity(now) || now < 0 || now < _lastSeenAt) return; // 无效或倒序观察不能刷新守点。
        if (_identity != identity) Clear(); // 换目标不能继承上一个玩家的熟悉位置。
        _identity = identity; // 只保存当前被本 Bot 亲自看见的目标身份。
        _lastPosition = position; // 坐标为个人视觉快照，不持有玩家 Transform。
        _lastSeenAt = now; // 只有真实可见时才更新最近目击时间。
    }

    /// <summary>视线真正从可见转为不可见时，短暂观察旧出口并建立再发现窗口。</summary>
    public bool MarkLost(double now)
    {
        if (_identity == null || double.IsNaN(now) || double.IsInfinity(now) || now < _lastSeenAt || now - _lastSeenAt > 0.75 || _lost) return false; // 长时间没有视觉证据不能凭旧点守候。
        _lost = true; // 同一次失视只能建立一次守点窗口。
        _primed = false; // 上一次再发现优势不能跨新的遮挡反复叠加。
        _watchUntil = now + 1.25; // 短停留后交还已有有限搜索路线。
        _repeekUntil = now + 8; // 同位置再出现的识别资格有独立八秒上限。
        return true;
    }

    /// <summary>仅最近个人目击位置可供守点使用，不允许跟随隐藏玩家更新。</summary>
    public bool CanWatch(string? identity, double now, out Vector3 point)
    {
        point = default; // 不符合条件时不得输出旧瞄准坐标。
        if (!_lost || _identity != identity || double.IsNaN(now) || double.IsInfinity(now) || now < _lastSeenAt || now >= _watchUntil) return false; // 过期、换目标或倒序时间立即失效。
        point = _lastPosition; // 只交给原生转向，不授予射击许可。
        return true;
    }

    /// <summary>同一真人在旧位置六米内再次被真实看见时，允许本次连续可见窗口缩短准备时间。</summary>
    public bool TryReacquire(string? identity, Vector3 visiblePosition, double now)
    {
        if (!_lost || _identity != identity || !ThreatMemory.Finite(visiblePosition) || double.IsNaN(now) || double.IsInfinity(now) || now < _lastSeenAt) return false; // 只有前一次失视的同一目标可获资格。
        bool familiar = now < _repeekUntil && Vector3.DistanceSquared(_lastPosition, visiblePosition) <= 36; // 必须靠真正的新视觉验证旧位置。
        _lost = false; // 即使是远处新位置，也不能再守着旧出口。
        _watchUntil = 0; // 重新看见后立即结束守点。
        _primed = familiar; // 熟悉优势只持续这一次连续可见窗口。
        return familiar;
    }

    /// <summary>当前连续可见窗口中是否具备已核实的同位置再次探头优势。</summary>
    public bool IsPrimed(string? identity)
    {
        return _primed && _identity == identity; // 不对其他目标或尚未确认视线的对象生效。
    }

    /// <summary>玩家情境结束、目标死亡或 Bot 销毁时删除全部个人视觉状态。</summary>
    public void Clear()
    {
        _identity = null; // 释放目标身份引用。
        _lastPosition = default; // 不把旧地图位置带入下一次接敌。
        _lastSeenAt = double.NegativeInfinity; // 下次目击从新时钟开始。
        _watchUntil = _repeekUntil = 0; // 过期资格不能跨生命周期恢复。
        _lost = _primed = false; // 清除守点和再发现标志。
    }
}
