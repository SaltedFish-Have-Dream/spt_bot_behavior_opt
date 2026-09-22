using AiBehavior.Core;
using EFT;
using UnityEngine;
using NumericsVector = System.Numerics.Vector3;

namespace AiBehavior.Client;

/// <summary>集中保存 EFT 40743 中已经核对的类型和坐标边界。</summary>
internal static class GameAdapter
{
    /// <summary>白名单判定角色，绝不将 pmcBot（Raider）误认为 PMC。</summary>
    internal static BotRole ResolveRole(BotOwner owner, bool scavs)
    {
        switch (owner.Profile.Info.Settings.Role) // 角色优先于阵营和显示名称。
        {
            case WildSpawnType.pmcBEAR:
            case WildSpawnType.pmcUSEC: return BotRole.Pmc; // 只有原生明确 PMC 角色参与成长。
            case WildSpawnType.assault:
            case WildSpawnType.assaultGroup: return scavs ? BotRole.Scav : BotRole.Native; // 普通 Scav 不使用等级。
            case WildSpawnType.marksman: return scavs ? BotRole.Marksman : BotRole.Native; // 保留驻点约束。
            default: return BotRole.Native; // Boss、护卫、特殊和未来角色保留原生。
        }
    }

    /// <summary>只承认真正视觉可见且足够新鲜的个人观测，不使用群体“感知”位置。</summary>
    internal static bool DirectlyVisible(EnemyInfo? enemy, double now)
    {
        return enemy != null && enemy.IsVisible && enemy.VisibleType == EEnemyPartVisibleType.Visible &&
            enemy.PersonalLastSeenTime <= now && now - enemy.PersonalLastSeenTime <= 0.35 &&
            enemy.Person?.HealthController?.IsAlive == true;
    }

    /// <summary>把 Unity 坐标转成纯逻辑值，切断游戏对象引用。</summary>
    internal static NumericsVector Snapshot(Vector3 point)
    {
        return new NumericsVector(point.x, point.y, point.z);
    }

    /// <summary>把已验证的记忆坐标交给主线程执行器。</summary>
    internal static Vector3 Position(NumericsVector point)
    {
        return new Vector3(point.X, point.Y, point.Z);
    }
}
