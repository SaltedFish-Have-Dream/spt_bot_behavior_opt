using System;
using System.Numerics;

namespace AiBehavior.Core;

/// <summary>只根据已确认视觉与 Bot 自身位置决定近距同层交战的有限记忆寿命。</summary>
public static class CombatMemoryPolicy
{
    /// <summary>持续目视的同层近战保留较久的最后已知位置，首次远距或跨层目击沿用角色基线。</summary>
    public static double VisionSeconds(BotRole role, in SkillProfile skill, Vector3 botPosition, Vector3 seenPosition, bool sustained)
    {
        if (role == BotRole.Native || !sustained || !ThreatMemory.Finite(botPosition) || !ThreatMemory.Finite(seenPosition)) return skill.MemorySeconds; // 非受管角色、无效快照或一次短暂露面均不取得长期记忆。
        if (Math.Abs(botPosition.Y - seenPosition.Y) > 2.5f) return skill.MemorySeconds; // 高差超过同层容差时不延长搜索。
        float x = botPosition.X - seenPosition.X; // 只计算已看见位置的水平距离。
        float z = botPosition.Z - seenPosition.Z; // 不读取玩家失视后的实时位置。
        if (x * x + z * z > 35 * 35) return skill.MemorySeconds; // 远距狙击仍使用原有等级记忆。
        double minimum = role == BotRole.Scav ? 18 : role == BotRole.Marksman ? 20 : 20 + skill.MemorySeconds * 0.5; // 固定角色不随等级变化，PMC 保持线性成长。
        return Math.Max(skill.MemorySeconds, minimum); // 寿命只在真实视觉再次出现时刷新，不靠每帧读取续期。
    }

    /// <summary>同层交战的三个候选点已查完时，只在原有视觉线索未过期期间守住最后区域。</summary>
    public static bool HoldCheckedArea(bool allChecked, double now, double combatUntil, double clueExpiresAt)
    {
        return allChecked && !double.IsNaN(now) && !double.IsInfinity(now) && !double.IsNaN(combatUntil) && !double.IsInfinity(combatUntil) &&
            !double.IsNaN(clueExpiresAt) && !double.IsInfinity(clueExpiresAt) && now >= 0 && now < combatUntil && now < clueExpiresAt; // 到期、非法时钟或尚未查完都不能无限守点。
    }
}
