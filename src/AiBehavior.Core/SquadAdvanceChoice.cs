namespace AiBehavior.Core;

/// <summary>一次受击告警中以固定大小的值状态选择一名合格推进者。</summary>
public struct SquadAdvanceChoice
{
    private float _nearest;
    public int SelectedId { get; private set; }

    /// <summary>只比较确实收到近邻告警且有行动能力的成员，距离相同按匿名序号稳定选择。</summary>
    public void Consider(int id, bool alerted, bool ready, float distanceSquared)
    {
        if (id <= 0 || !alerted || !ready || float.IsNaN(distanceSquared) || float.IsInfinity(distanceSquared) || distanceSquared < 0 || distanceSquared > 400) return; // 无效对象与超过二十米成员不能成为推进者。
        if (SelectedId != 0 && (distanceSquared > _nearest || distanceSquared == _nearest && id >= SelectedId)) return; // 现有最近成员不被更远或相同距离的后出生者替换。
        SelectedId = id; // 保存本次唯一匿名成员序号。
        _nearest = distanceSquared; // 后续只需要平方距离，无导航或额外容器。
    }
}
