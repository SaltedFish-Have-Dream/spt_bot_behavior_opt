namespace AiBehavior.Core;

/// <summary>每帧至少服务一个到期项，其余工作遵守软预算；扫描和执行均有界。</summary>
public sealed class DecisionScheduler
{
    private int _cursor;
    private int _remaining;
    private int _served;

    /// <summary>固定本帧最大扫描量，Bot 出生不会扩张正在执行的循环。</summary>
    public void BeginFrame(int count)
    {
        _remaining = count;
        _served = 0;
    }

    /// <summary>未服务到期项前允许越过软预算，至少轮到一个有效 Bot。</summary>
    public bool TryNext(int count, bool hasTime, out int index)
    {
        index = -1; // 无候选时不给调用者无效的列表下标。
        if (count <= 0 || _remaining <= 0 || (_served > 0 && !hasTime)) return false; // 空列表和预算边界及时结束。
        _remaining--; // 每次查看都消耗扫描机会，失活项不能导致无限循环。
        if (_cursor >= count) _cursor = 0; // 删除 Bot 后修正索引。
        index = _cursor++; // 保留跨帧位置，持续超预算时也能公平前进。
        return true;
    }

    /// <summary>只在真正开始一个到期决策后消耗本帧保障机会。</summary>
    public void Served()
    {
        _served++;
    }

    /// <summary>补偿删除造成的列表左移，避免跳过被删除项的下一位。</summary>
    public void RemovedAt(int index)
    {
        if (index >= 0 && index < _cursor) _cursor--;
    }
}
