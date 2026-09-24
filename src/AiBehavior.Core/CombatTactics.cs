using System;
using System.Numerics;

namespace AiBehavior.Core;

/// <summary>以个人线索快照选择短距突击、伏击、换掩体和手雷机会，不读取游戏对象。</summary>
public static class CombatTactics
{
    /// <summary>只让有近期亲眼目击的健康 PMC 趁失视短距推进，等级通过既有战术概率成长。</summary>
    public static bool RushAfterSight(BotRole role, in SkillProfile skill, CombatTemperament temperament, in Observation clue, Vector3 self,
        double now, float encounterRoll, bool visible, bool danger, bool recovery, bool canMove, bool hasAmmo)
    {
        if (role != BotRole.Pmc || !canMove || !hasAmmo || visible || danger || recovery || clue.Source != ObservationSource.Vision) return false; // 声音、队友报告和失能状态都不能授予冲锋资格。
        if (!ThreatMemory.Finite(self) || !ThreatMemory.Finite(clue.Position) || double.IsNaN(now) || double.IsInfinity(now)) return false; // 异常坐标和时钟直接退回普通搜索。
        double age = now - clue.ObservedAt; // 只使用本人最后一次看见玩家的旧坐标。
        if (age < 1.25 || age > 5 || now >= clue.ExpiresAt) return false; // 先守旧出口，随后仅在短时机会窗口前压。
        float rangeSquared = Vector3.DistanceSquared(self, clue.Position); // 平方距离避免进入热路径的开方。
        if (rangeSquared < 36 || rangeSquared > 484 || Math.Abs(self.Y - clue.Position.Y) > 2.5f) return false; // 贴脸和跨楼层由原生或已有调查处理。
        if (float.IsNaN(encounterRoll) || float.IsInfinity(encounterRoll) || encounterRoll < 0 || encounterRoll >= 1) return false; // 非法抽样不能获得额外进攻资格。
        float bias = temperament == CombatTemperament.Aggressive ? 0.12f : temperament == CombatTemperament.Cautious ? -0.12f : 0f; // 固定风格微调机会，不改变等级曲线。
        float probability = Math.Max(0, Math.Min(1, skill.TacticalProbability + bias)); // 成长概率在等级区间内仍线性提高。
        return encounterRoll < probability; // 每次实质敌情只抽样一次，不按帧提高中奖率。
    }

    /// <summary>近距离脚步建立一次短暂伏击观察，不把枪声或估计点变成射击许可。</summary>
    public static bool WaitOnFootstep(in Observation clue, Vector3 self, double now, bool visible, bool danger, bool recovery, bool canMove)
    {
        if (!canMove || visible || danger || recovery || clue.Source != ObservationSource.Hearing || !ThreatMemory.Finite(self) || !ThreatMemory.Finite(clue.Position)) return false; // 只对本人听到的玩家脚步短暂守候。
        double age = now - clue.ObservedAt; // 无需为伏击另建每帧计时器。
        return age >= 0 && age < 1.2 && now < clue.ExpiresAt && Vector3.DistanceSquared(self, clue.Position) <= 324 && Math.Abs(self.Y - clue.Position.Y) <= 2.5f; // 超时后恢复原有有限搜索。
    }

    /// <summary>在掩体处失去刚才亲眼看到的玩家后，只允许一次局部换位机会。</summary>
    public static bool ShiftCover(in Observation clue, double now, bool visible, bool danger, bool recovery, bool hasCover, bool atCover, bool canMove)
    {
        if (visible || danger || recovery || !hasCover || !atCover || !canMove || clue.Source != ObservationSource.Vision) return false; // 可见交火与危险避险优先于换掩体。
        double age = now - clue.ObservedAt; // 视觉时间不由声音或隐藏玩家位置续期。
        return age >= 2.5 && age <= 7 && now < clue.ExpiresAt; // 守点结束后才尝试局部换位。
    }

    /// <summary>战术手雷仅针对新鲜个人视觉旧点，有限距离和同层条件先于昂贵轨迹查询。</summary>
    public static bool ThrowAtLastSeen(in Observation clue, Vector3 self, double now, bool visible, bool danger, bool recovery, bool moving)
    {
        if (visible || danger || recovery || moving || clue.Source != ObservationSource.Vision || !ThreatMemory.Finite(self) || !ThreatMemory.Finite(clue.Position)) return false; // 不向声音误差区或当前可见目标抢投掷动作。
        double age = now - clue.ObservedAt; // 过旧目标不能使用手雷主动搜索。
        float distanceSquared = Vector3.DistanceSquared(self, clue.Position); // 投掷距离只用快照和平方值。
        return age >= 1.5 && age <= 6 && now < clue.ExpiresAt && distanceSquared >= 144 && distanceSquared <= 1225 && Math.Abs(self.Y - clue.Position.Y) <= 2.5f; // 12～35 米同层旧点才进入原生轨迹验证。
    }
}
