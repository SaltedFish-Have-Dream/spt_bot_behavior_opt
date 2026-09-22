using System.Numerics;

namespace AiBehavior.Core;

/// <summary>调查范围只取决于角色，不随 PMC 等级放大查询成本。</summary>
public static class InvestigationPolicy
{
    /// <summary>在接管和搜索两处统一检查快照范围，狙击模板只观察而不离岗。</summary>
    public static bool InRange(BotRole role, Vector3 origin, Vector3 clue)
    {
        if (!ThreatMemory.Finite(origin) || !ThreatMemory.Finite(clue)) return false; // 非法坐标不进入导航或转向。
        if (role == BotRole.Marksman) return true; // 狙击手不提交调查移动，保留远处声音警戒。
        float limit = role == BotRole.Scav ? 25 : 60; // 与已有搜索半径保持一致。
        return Vector3.DistanceSquared(origin, clue) <= limit * limit; // 用平方距离避免重复开方。
    }
}
