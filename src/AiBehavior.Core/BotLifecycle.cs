namespace AiBehavior.Core;

/// <summary>记录激活边沿与资源所有权，重复停用和释放不再推进动作代次。</summary>
public sealed class BotLifecycle
{
    public bool Active { get; private set; } = true;
    public int Generation { get; private set; }
    private bool _ownsResources;

    /// <summary>只在激活状态真正改变时报告边沿。</summary>
    public bool SetActive(bool active)
    {
        if (active == Active) return false; // 同一停用期不会重复进入清理分支。
        Active = active; // 保存新状态，恢复时可立即唤醒决策。
        return true;
    }

    /// <summary>取得动作控制或提交射击任务后登记清理责任。</summary>
    public void OwnResources()
    {
        _ownsResources = true;
    }

    /// <summary>使旧状态的查询结果失效，但保留当前控制权的清理责任。</summary>
    public void InvalidateRequests()
    {
        Generation++;
    }

    /// <summary>消费一次清理责任；原生层提交的射击任务也必须清理。</summary>
    public bool TryRelease()
    {
        if (!_ownsResources) return false; // 重复 Stop、停用和 Dispose 均为常数时间空操作。
        _ownsResources = false; // 在访问游戏对象前先撤销责任，异常也不会重复交接。
        InvalidateRequests(); // 已出队的旧结果同样失效。
        return true;
    }
}
