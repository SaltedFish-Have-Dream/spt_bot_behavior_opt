using System;
using System.Numerics;

namespace AiBehavior.Core;

/// <summary>同时限制长期调用速率和单帧突发，长停顿后不补跑欠账。</summary>
public sealed class WorkBudget
{
    private readonly double _rate;
    private readonly int _capacity;
    private double _tokens;
    private double _lastTime = double.NaN;
    private int _frame = -1;
    private int _usedInFrame;
    public long TotalUsed { get; private set; }

    /// <summary>创建正数速率与容量的令牌桶。</summary>
    public WorkBudget(double rate, int capacity)
    {
        if (rate <= 0 || double.IsNaN(rate) || double.IsInfinity(rate) || capacity < 1) throw new ArgumentOutOfRangeException(nameof(rate));
        _rate = rate;
        _capacity = capacity;
        _tokens = capacity;
    }

    /// <summary>尝试为一个不可分割查询组领取令牌，失败时不扣除额度。</summary>
    public bool TryTake(double now, int frame, int count = 1)
    {
        if (count < 1 || count > _capacity || double.IsNaN(now) || double.IsInfinity(now)) return false; // 非法请求不能突破限制。
        if (double.IsNaN(_lastTime)) _lastTime = now; // 首次调用从当前时间开始补充。
        _tokens = Math.Min(_capacity, _tokens + Math.Max(0, now - _lastTime) * _rate); // 停顿期间最多补满桶。
        _lastTime = Math.Max(_lastTime, now); // 时钟回退不重复发放令牌。
        if (_frame != frame) { _frame = frame; _usedInFrame = 0; } // 单帧额度独立重置。
        if (_tokens + 0.000001 < count || _usedInFrame + count > _capacity) return false; // 同时遵守两种上限。
        _tokens -= count; // 只有成功领取才扣费。
        _usedInFrame += count; // 更新帧内用量。
        TotalUsed += count; // 汇总真实获得的额度。
        return true;
    }
}

public enum QueryKind { Move, Cover, Shot, Search }

/// <summary>带 Bot 生命周期与动作代次的请求，结果不允许跨动作复用。</summary>
public readonly struct WorkRequest
{
    public readonly int Owner;
    public readonly int Generation;
    public readonly QueryKind Kind;
    public readonly Vector3 Position;
    public readonly Vector3 Threat;
    public readonly double Deadline;

    /// <summary>创建有明确有效期的查询快照。</summary>
    public WorkRequest(int owner, int generation, QueryKind kind, Vector3 position, Vector3 threat, double deadline)
    {
        Owner = owner;
        Generation = generation;
        Kind = kind;
        Position = position;
        Threat = threat;
        Deadline = deadline;
    }
}

/// <summary>固定容量且按槽位轮转的请求队列，同一 Bot 同类请求去重。</summary>
public sealed class WorkQueue
{
    private readonly WorkRequest[] _items;
    private readonly bool[] _occupied;
    private int _cursor;
    public int Count { get; private set; }
    public long Expired { get; private set; }
    public long Rejected { get; private set; }

    /// <summary>分配有上限的队列，默认不超过 256 项。</summary>
    public WorkQueue(int capacity = 256)
    {
        if (capacity < 1 || capacity > 256) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new WorkRequest[capacity];
        _occupied = new bool[capacity];
    }

    /// <summary>相同请求保持原截止时间，避免更新操作让无效任务永不过期。</summary>
    public bool Enqueue(in WorkRequest request, double now, int activeBots)
    {
        if (request.Deadline <= now || double.IsNaN(request.Deadline) || double.IsInfinity(request.Deadline) ||
            !ThreatMemory.Finite(request.Position) || !ThreatMemory.Finite(request.Threat)) { Rejected++; return false; } // 无效请求不得进入队列。
        int free = -1; // 查找可复用槽位。
        for (int index = 0; index < _items.Length; index++) // 固定容量使遍历成本有界。
        {
            if (_occupied[index] && _items[index].Deadline <= now) { Remove(index); Expired++; } // 入队时同步清理过期任务。
            if (!_occupied[index]) { if (free < 0) free = index; continue; } // 记住首个空位。
            WorkRequest old = _items[index]; // 读取待合并的请求。
            if (old.Owner != request.Owner || old.Kind != request.Kind) continue; // 不覆盖其他 Bot 的任务。
            if (request.Generation < old.Generation) { Rejected++; return false; } // 迟到的旧动作不得覆盖新代次。
            _items[index] = old.Generation == request.Generation
                ? new WorkRequest(request.Owner, request.Generation, request.Kind, request.Position, request.Threat, Math.Min(old.Deadline, request.Deadline))
                : request; // 相同代次合并，新的动作代次替换旧任务。
            return true;
        }
        if (free < 0 || Count >= Math.Min(_items.Length, Math.Max(0L, (long)activeBots * 4))) { Rejected++; return false; } // 限制总容量与相对活跃规模。
        _items[free] = request; // 保存值快照。
        _occupied[free] = true; // 标记已占用。
        Count++; // 维护待处理总量。
        return true;
    }

    /// <summary>轮转领取下一项有效任务，过期任务被清除。</summary>
    public bool TryDequeue(double now, out WorkRequest request)
    {
        for (int checkedCount = 0; checkedCount < _items.Length; checkedCount++) // 最多扫描固定数组一次。
        {
            int index = _cursor; // 从上次结束位置继续，实现公平轮转。
            _cursor = (_cursor + 1) % _items.Length; // 更新下次扫描起点。
            if (!_occupied[index]) continue; // 跳过空槽。
            request = _items[index]; // 复制后释放槽位。
            Remove(index); // 防止异常执行使任务永久占用队列。
            if (request.Deadline <= now) { Expired++; continue; } // 过期结果不进入执行阶段。
            return true;
        }
        request = default; // 明确返回没有工作。
        return false;
    }

    /// <summary>Bot 销毁或动作退出时取消其全部任务。</summary>
    public void Cancel(int owner)
    {
        for (int index = 0; index < _items.Length; index++) // 扫描固定容量。
            if (_occupied[index] && _items[index].Owner == owner) Remove(index); // 只释放属于该 Bot 的槽位。
    }

    /// <summary>释放单个占用槽并维护计数。</summary>
    private void Remove(int index)
    {
        _occupied[index] = false;
        _items[index] = default;
        Count--;
    }
}
