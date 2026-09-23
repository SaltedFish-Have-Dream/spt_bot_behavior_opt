using System.Numerics;

namespace AiBehavior.Core;

/// <summary>弹道只保留数值快照，不持有游戏对象或池化子弹实例。</summary>
public struct BulletTrace
{
    public Vector3 From;
    public Vector3 To;
    public Vector3 Origin;
    public double Deadline;
    public int NextBot;
}

/// <summary>玩家弹道的固定容量队列，满载时明确拒绝而不扩大每帧工作量。</summary>
public sealed class BulletTraceQueue
{
    private readonly BulletTrace[] _items = new BulletTrace[64];
    private int _head;
    public int Count { get; private set; }
    public long Dropped { get; private set; }
    public long Expired { get; private set; }

    /// <summary>入队最多六十四条已完成碰撞裁剪的玩家弹道段。</summary>
    public bool Enqueue(in BulletTrace trace)
    {
        if (Count == _items.Length || double.IsNaN(trace.Deadline) || double.IsInfinity(trace.Deadline) || trace.NextBot < 0 || !ThreatMemory.Finite(trace.From) || !ThreatMemory.Finite(trace.To) || !ThreatMemory.Finite(trace.Origin)) { Dropped++; return false; } // 非法或过载事件不进入热循环。
        _items[(_head + Count) % _items.Length] = trace; // 固定环形缓冲，无逐事件分配。
        Count++; // 每条轨迹都有有限等待时间。
        return true;
    }

    /// <summary>取得最早有效段；未完成的 Bot 检查可带游标重新入队。</summary>
    public bool TryDequeue(double now, out BulletTrace trace)
    {
        while (Count > 0) // 最多检查固定容量个到期段。
        {
            trace = _items[_head]; // 复制后立即释放槽位。
            _items[_head] = default; // 不保留上一条弹道。
            _head = (_head + 1) % _items.Length; // 公平轮转到下一个段。
            Count--; // 过期与成功都释放容量。
            if (trace.Deadline > now) return true; // 禁止很久以前的擦弹迟到触发避险。
            Expired++; // 过载丢失必须在日志中可见。
        }
        trace = default; // 明确没有待处理工作。
        return false;
    }

    /// <summary>注册列表移除成员后修正待处理游标，避免跳过左移后的下一名 Bot。</summary>
    public void RemovedBotAt(int index)
    {
        if (index < 0) return; // 未注册对象没有改变列表。
        for (int offset = 0; offset < Count; offset++) // 只修正现有项，最多六十四次。
        {
            int slot = (_head + offset) % _items.Length; // 环形缓冲中的实际位置。
            if (_items[slot].NextBot > index) _items[slot].NextBot--; // 只调整已经经过移除位置的游标。
        }
    }
}
