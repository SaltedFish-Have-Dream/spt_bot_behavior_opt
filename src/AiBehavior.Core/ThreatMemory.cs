using System;
using System.Numerics;

namespace AiBehavior.Core;

public enum ObservationSource { Vision, Hearing }

/// <summary>观察位置是值快照，不持有游戏对象或隐藏目标坐标提供器。</summary>
public readonly struct Observation
{
    public readonly string Identity;
    public readonly ObservationSource Source;
    public readonly Vector3 Position;
    public readonly Vector3 AimPosition;
    public readonly double ObservedAt;
    public readonly double ExpiresAt;
    public readonly float Uncertainty;

    /// <summary>创建带原始时间和不确定度的有限线索。</summary>
    public Observation(string identity, ObservationSource source, Vector3 position, double observedAt, double expiresAt, float uncertainty, Vector3? aimPosition = null)
    {
        Identity = identity;
        Source = source;
        Position = position;
        AimPosition = aimPosition ?? position;
        ObservedAt = observedAt;
        ExpiresAt = expiresAt;
        Uncertainty = uncertainty;
    }
}

/// <summary>每个 Bot 最多保存四条记录，重复声源在一秒内合并。</summary>
public sealed class ThreatMemory
{
    public const int Capacity = 4;
    private readonly Observation[] _items = new Observation[Capacity];
    private readonly bool[] _occupied = new bool[Capacity];

    /// <summary>写入新线索，拒绝倒序事件并限制重复枪声的更新速度。</summary>
    public bool Observe(in Observation observation, double now)
    {
        if (string.IsNullOrEmpty(observation.Identity) || !Finite(observation.Position) || !Finite(observation.AimPosition) ||
            double.IsNaN(observation.ObservedAt) || double.IsInfinity(observation.ObservedAt) || observation.ObservedAt > now ||
            observation.ExpiresAt <= now || double.IsNaN(observation.ExpiresAt) || double.IsInfinity(observation.ExpiresAt)) return false; // 拒绝无效或无限寿命输入。
        int slot = -1; // 记录首个空槽或过期槽。
        int oldest = 0; // 满载时覆盖最旧记录。
        for (int index = 0; index < Capacity; index++) // 扫描固定四项，不随敌人数扩展。
        {
            if (!_occupied[index] || _items[index].ExpiresAt <= now) { if (slot < 0) slot = index; continue; } // 优先复用失效位置。
            if (_items[index].ObservedAt < _items[oldest].ObservedAt) oldest = index; // 保存最旧观察索引。
            if (_items[index].Identity != observation.Identity) continue; // 只合并相同来源对象。
            if (observation.ObservedAt <= _items[index].ObservedAt) return false; // 旧观察不能覆盖新观察。
            if (observation.Source == ObservationSource.Hearing && observation.ObservedAt - _items[index].ObservedAt < 1) return false; // 合并枪声风暴，且不覆盖新鲜视觉。
            _items[index] = observation; // 更新同一威胁的值快照。
            return true; // 不增加额外记录。
        }
        slot = slot < 0 ? oldest : slot; // 无空槽时执行固定容量淘汰。
        _items[slot] = observation; // 写入新观察。
        _occupied[slot] = true; // 标记槽位有效。
        return true;
    }

    /// <summary>读取指定目标的未过期记录，不延长其寿命。</summary>
    public bool TryGet(string identity, double now, out Observation observation)
    {
        for (int index = 0; index < Capacity; index++) // 遍历固定容量。
        {
            if (_occupied[index] && _items[index].Identity == identity && _items[index].ExpiresAt > now) // 同时验证身份和有效期。
            {
                observation = _items[index]; // 返回不可变副本。
                return true;
            }
        }
        observation = default; // 缺失时不给出伪造位置。
        return false;
    }

    /// <summary>取得最新有效线索，用于调查和有限搜索。</summary>
    public bool TryGetLatest(double now, out Observation observation)
    {
        int newest = -1; // 没有有效项时保持失败。
        for (int index = 0; index < Capacity; index++) // 数量上限与等级无关。
            if (_occupied[index] && _items[index].ExpiresAt > now && (newest < 0 || _items[index].ObservedAt > _items[newest].ObservedAt)) newest = index; // 保留最新有效项。
        observation = newest < 0 ? default : _items[newest]; // 不读取任何游戏对象。
        return newest >= 0;
    }

    /// <summary>丢弃已死亡、已完成搜索或明确失效的目标。</summary>
    public void Forget(string identity)
    {
        for (int index = 0; index < Capacity; index++) // 所有同名记录一并清理。
            if (_occupied[index] && _items[index].Identity == identity) { _occupied[index] = false; _items[index] = default; } // 释放字符串引用。
    }

    /// <summary>检查坐标没有非数字或无限值。</summary>
    public static bool Finite(Vector3 point)
    {
        return !(float.IsNaN(point.X) || float.IsNaN(point.Y) || float.IsNaN(point.Z) ||
            float.IsInfinity(point.X) || float.IsInfinity(point.Y) || float.IsInfinity(point.Z));
    }
}
