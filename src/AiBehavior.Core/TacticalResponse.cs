using System;
using System.Numerics;

namespace AiBehavior.Core;

public enum PressureEvent { NearBullet, Hit, AllyWarning }
public enum PressureLevel { Calm, Alert, Pinned }

/// <summary>真人近弹与命中形成有界压力；只保存标量，不扫描弹道或修改原生能力。</summary>
public sealed class SuppressionPressure
{
    private double _updatedAt = double.NegativeInfinity;
    private double _windowAt = double.NegativeInfinity;
    private float _windowWeight;
    public float Value { get; private set; }
    public PressureLevel Level { get; private set; }

    /// <summary>同一四分之一秒内合并为最强事件，近弹后命中只补差值，友方报告不累积个人压力。</summary>
    public bool Observe(bool localHuman, PressureEvent kind, double now)
    {
        if (!localHuman || (kind != PressureEvent.NearBullet && kind != PressureEvent.Hit) || !ValidTime(now) || now < _updatedAt) return false; // 未确认来源、非个人危险和倒序事件不能加压。
        Advance(now); // 先按实际经过时间衰减，调用频率不改变结果。
        float weight = kind == PressureEvent.Hit ? 2.5f : 0.9f; // 命中立即进入高压，多次近弹才达到高压。
        if (now - _windowAt >= 0.25) { _windowAt = now; _windowWeight = 0; } // 固定事件窗口不被重复回调无限推后。
        float added = Math.Max(0, weight - _windowWeight); // 同一窗口只补更强事件，防止近弹和受击双计。
        _windowWeight = Math.Max(weight, _windowWeight); // 保存窗口已计入的最大权重。
        Value = Math.Min(4, Value + added); // 自动射击也不会形成无限压力。
        if (kind == PressureEvent.Hit && added > 0) Value = Math.Max(2.5f, Value); // 同窗口近弹升级为命中时仍能立即进入高压，重复命中回调不重新抬底。
        UpdateLevel(); // 新命中即时生效，不等待普通决策频率。
        return added > 0; // 调用者可区分新压力和已合并事件。
    }

    /// <summary>每秒衰减零点八，高压进入和退出使用不同阈值；五秒安静足以消退满压力。</summary>
    public void Advance(double now)
    {
        if (!ValidTime(now) || now < _updatedAt) return; // 非法或倒退时间不增加压力，也不提前解除压力。
        if (!double.IsNegativeInfinity(_updatedAt)) Value = (float)Math.Max(0, Value - (now - _updatedAt) * 0.8); // 常数时间更新，不逐帧叠加固定量。
        _updatedAt = now; // 保存真实时间基准。
        UpdateLevel(); // 只有跨过阈值才改变行为等级。
    }

    /// <summary>死亡或结束当前调查后清空值状态，不向下次玩家情境泄漏压力。</summary>
    public void Clear()
    {
        Value = _windowWeight = 0;
        Level = PressureLevel.Calm;
        _updatedAt = _windowAt = double.NegativeInfinity;
    }

    /// <summary>进入高压需达到二点五，降至一才退出，避免阈值附近反复切换还击意图。</summary>
    private void UpdateLevel()
    {
        if (Value >= 2.5f || (Level == PressureLevel.Pinned && Value > 1)) Level = PressureLevel.Pinned; // 高压具有退出缓冲。
        else Level = Value > 0 ? PressureLevel.Alert : PressureLevel.Calm; // 低压不取消原有五秒避险计时。
    }

    /// <summary>只接收有限的非负战局时间。</summary>
    private static bool ValidTime(double now)
    {
        return !double.IsNaN(now) && !double.IsInfinity(now) && now >= 0;
    }
}

/// <summary>记录本 Bot 在掩体处遭玩家命中的位置，固定四项，十二秒后自然失效。</summary>
public sealed class FailedCoverMemory
{
    public const int Capacity = 4;
    public const double Lifetime = 12;
    private readonly Vector3[] _positions = new Vector3[Capacity];
    private readonly double[] _expires = new double[Capacity];
    private double _latestAt = double.NegativeInfinity;

    /// <summary>只有个人命中且实际到达掩体才封锁该点，近弹和友方报告不能证明掩体失效。</summary>
    public bool ObserveHit(bool localHuman, bool atCover, Vector3 position, double now)
    {
        if (!localHuman || !atCover || !ThreatMemory.Finite(position) || !ValidTime(now) || now < _latestAt) return false; // 不能用全局枪声、倒序事件或非法坐标封锁候选。
        _latestAt = now; // 新事件不得缩短或倒退已记录的封锁时间。
        int slot = 0; // 满容量时替换最早到期的一项。
        for (int index = 0; index < Capacity; index++) // 上限始终为四，不随 Bot 数或战局时长扩容。
        {
            if (_expires[index] > now && Vector3.DistanceSquared(position, _positions[index]) <= 4) { slot = index; break; } // 相近位置合并，不被同一掩体占满全部槽位。
            if (_expires[index] < _expires[slot]) slot = index; // 优先复用失效或最早到期条目。
        }
        _positions[slot] = position; // 只存受击时的掩体坐标，没有玩家位置引用。
        _expires[slot] = now + Lifetime; // 只有新命中可以更新失效时间。
        return true;
    }

    /// <summary>在采样前后及路径接收时检查两米邻域，不通过读取延长封锁。</summary>
    public bool Rejects(Vector3 position, double now)
    {
        if (!ThreatMemory.Finite(position) || !ValidTime(now)) return true; // 非法候选不能进入原生查询。
        for (int index = 0; index < Capacity; index++) // 每次最多四个平方距离，无导航或物理 API。
            if (_expires[index] > now && now >= _expires[index] - Lifetime && Vector3.DistanceSquared(position, _positions[index]) <= 4) return true; // 不同高度按三维距离判定，时钟倒退不误用未来记录。
        return false;
    }

    /// <summary>玩家失效或 Bot 销毁后解除所有短期封锁。</summary>
    public void Clear()
    {
        Array.Clear(_expires, 0, Capacity);
        _latestAt = double.NegativeInfinity;
    }

    /// <summary>失效记录只使用有限的非负战局时间。</summary>
    private static bool ValidTime(double now)
    {
        return !double.IsNaN(now) && !double.IsInfinity(now) && now >= 0;
    }
}

/// <summary>高压还击与掩体动作连续性的值判断，不读取游戏对象。</summary>
public static class TacticalActionPolicy
{
    /// <summary>只有本人近期中弹、掩体已证实不可用且当前无法目视玩家时才允许本模组主动趴伏。</summary>
    public static bool CanProneAfterHit(bool personalHit, bool coverUnavailable, bool hasCover, bool queryPending, bool visible, bool moving)
    {
        return personalHit && coverUnavailable && !hasCover && !queryPending && !visible && !moving; // 对枪、近弹和队友告警都不能使 Bot 主动卧倒。
    }

    /// <summary>本模组持有玩家交战控制时，撤销接管后新出现的卧姿，保留接管前已有的原生卧姿。</summary>
    public static bool ShouldClearVisibleProne(bool controlled, bool layerSelected, bool visible, bool playerTarget, bool originallyProne, bool currentlyProne)
    {
        return controlled && layerSelected && visible && playerTarget && !originallyProne && currentlyProne; // 不越过原生层控制权或改动原有狙击卧姿。
    }

    /// <summary>到达有效掩体、已过短暂受惊窗口且压力回落后才尝试还击，实际射击仍须原生验证。</summary>
    public static bool CanReturnFire(bool atCover, bool moving, bool visible, bool recovering, PressureLevel pressure, double now, double lastDanger)
    {
        return atCover && !moving && visible && !recovering && pressure != PressureLevel.Pinned &&
            !double.IsNaN(now) && !double.IsInfinity(now) && !double.IsNaN(lastDanger) && !double.IsInfinity(lastDanger) && now - lastDanger >= 0.75;
    }

    /// <summary>掩体确实不可用且当前无需等待卧姿处理时，允许 Bot 在短暂受惊后自卫。</summary>
    public static bool CanExposedReturnFire(bool coverUnavailable, bool moving, bool queryPending, bool visible, bool recovering, bool weaponReady, bool postureResolved, PressureLevel pressure, double now, double lastDanger)
    {
        return coverUnavailable && !moving && !queryPending && visible && !recovering && weaponReady && postureResolved && pressure != PressureLevel.Pinned &&
            !double.IsNaN(now) && !double.IsInfinity(now) && !double.IsNaN(lastDanger) && !double.IsInfinity(lastDanger) && now - lastDanger >= 0.75; // 保留初始避险窗口和高压停火，不为自卫新增物理查询。
    }

    /// <summary>只在同一个有效掩体的移动动作之间保留原生路线，搜索和恢复不继承它。</summary>
    public static bool KeepCoverMove(BehaviorState current, BehaviorState next, bool validMove)
    {
        return validMove && IsCoverMove(current) && IsCoverMove(next);
    }

    /// <summary>这三种状态允许沿已验证的掩体路线移动。</summary>
    private static bool IsCoverMove(BehaviorState state)
    {
        return state == BehaviorState.Cover || state == BehaviorState.Disengage || state == BehaviorState.Evade;
    }
}
