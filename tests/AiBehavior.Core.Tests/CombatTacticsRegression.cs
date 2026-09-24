using System.Numerics;
using AiBehavior.Core;

namespace AiBehavior.Core.Tests;

/// <summary>验证 SAIN 战斗思路的轻量实现只消费玩家个人线索快照。</summary>
internal static partial class Program
{
    /// <summary>等级能力只影响 PMC 短距机会，声音、失能和跨楼层均不能授予冲锋。</summary>
    private static void CombatRushRequiresPersonalSight()
    {
        var sight = new Observation("player", ObservationSource.Vision, new Vector3(12, 0, 0), 5, 25, 0); // 本人目击后的固定旧点。
        var sound = new Observation("player", ObservationSource.Gunshot, sight.Position, 5, 25, 5); // 枪声不能冒充个人视觉。
        SkillProfile high = SkillProfile.Create(BotRole.Pmc, 60); // 高等级达到主动机会阈值。
        SkillProfile low = SkillProfile.Create(BotRole.Pmc, 1); // 低等级仍可沿普通调查前进。
        Check(CombatTactics.RushAfterSight(BotRole.Pmc, high, CombatTemperament.Balanced, sight, Vector3.Zero, 6.3, 0.5f, false, false, false, true, true), "recent sight allows high level rush"); // 守点之后才突击。
        Check(!CombatTactics.RushAfterSight(BotRole.Pmc, low, CombatTemperament.Balanced, sight, Vector3.Zero, 6.3, 0.5f, false, false, false, true, true), "low level less likely to rush"); // 成长提高机会而不改变调度额度。
        Check(CombatTactics.RushAfterSight(BotRole.Pmc, low, CombatTemperament.Balanced, sight, Vector3.Zero, 6.3, 0.25f, false, false, false, true, true), "low level still has a chance"); // 低等级不被硬阈值永久排除。
        Check(!CombatTactics.RushAfterSight(BotRole.Scav, high, CombatTemperament.Aggressive, sight, Vector3.Zero, 6.3, 0.5f, false, false, false, true, true), "scav fixed template"); // 固定角色不读取 PMC 成长。
        Check(!CombatTactics.RushAfterSight(BotRole.Pmc, high, CombatTemperament.Balanced, sound, Vector3.Zero, 6.3, 0.5f, false, false, false, true, true), "sound not direct sight"); // 不凭听觉冲锋到精确点。
        Check(!CombatTactics.RushAfterSight(BotRole.Pmc, high, CombatTemperament.Balanced, sight, new Vector3(0, 4, 0), 6.3, 0.5f, false, false, false, true, true), "different floor blocked"); // 高差排除错误楼层。
        Check(!CombatTactics.RushAfterSight(BotRole.Pmc, high, CombatTemperament.Balanced, sight, Vector3.Zero, 6.3, 0.5f, true, false, false, true, true), "visible fight takes priority"); // 真实可见时保留射击窗口。
        Check(!CombatTactics.RushAfterSight(BotRole.Pmc, high, CombatTemperament.Balanced, sight, Vector3.Zero, 6.3, 0.5f, false, true, false, true, true), "danger blocks rush"); // 本人受压先避险。
        Check(!CombatTactics.RushAfterSight(BotRole.Pmc, high, CombatTemperament.Balanced, sight, Vector3.Zero, 10.1, 0.5f, false, false, false, true, true), "old sight expires rush"); // 旧点不维持无限主动性。
    }

    /// <summary>脚步伏击和失视换掩体均只在短窗口成立。</summary>
    private static void CombatAmbushAndShiftExpire()
    {
        var footstep = new Observation("player", ObservationSource.Hearing, new Vector3(10, 0, 0), 5, 9, 4); // 已由原生听觉确认的声音估计点。
        var sight = new Observation("player", ObservationSource.Vision, new Vector3(10, 0, 0), 5, 20, 0); // 已由个人视觉确认的旧位置。
        Check(CombatTactics.WaitOnFootstep(footstep, Vector3.Zero, 5.5, false, false, false, true), "near footstep allows brief ambush"); // 短时间停看。
        Check(!CombatTactics.WaitOnFootstep(footstep, Vector3.Zero, 6.2, false, false, false, true), "ambush expires"); // 超时恢复调查。
        Check(!CombatTactics.WaitOnFootstep(sight, Vector3.Zero, 5.5, false, false, false, true), "visual not footstep ambush"); // 视觉由旧出口守点处理。
        Check(CombatTactics.ShiftCover(sight, 8, false, false, false, true, true, true), "lost sight can shift cover"); // 守点结束后有限换位。
        Check(!CombatTactics.ShiftCover(sight, 8, true, false, false, true, true, true), "visible fight blocks shift"); // 对枪不突然离开掩体。
        Check(!CombatTactics.ShiftCover(sight, 8, false, false, false, true, false, true), "must stand at old cover"); // 途中不反复申请另一处。
        Check(!CombatTactics.ShiftCover(sight, 13, false, false, false, true, true, true), "old sight does not keep shifting"); // 已过七秒窗口退出。
    }

    /// <summary>只向近期视觉旧点开放一次原生投掷候选，不使用带误差的听觉点。</summary>
    private static void CombatGrenadeRejectsUnsafeClues()
    {
        var sight = new Observation("player", ObservationSource.Vision, new Vector3(20, 0, 0), 5, 20, 0); // 中距离同层个人目击。
        var sound = new Observation("player", ObservationSource.Gunshot, sight.Position, 5, 20, 4); // 带误差的枪声点。
        Check(CombatTactics.ThrowAtLastSeen(sight, Vector3.Zero, 7, false, false, false, false), "recent old sight allows grenade check"); // 此结果只进入友军和轨迹验证。
        Check(!CombatTactics.ThrowAtLastSeen(sound, Vector3.Zero, 7, false, false, false, false), "gunshot not grenade target"); // 估计区不能当精确投掷点。
        Check(!CombatTactics.ThrowAtLastSeen(sight, Vector3.Zero, 7, true, false, false, false), "visible target handled by gun"); // 对枪不抢投掷动画。
        Check(!CombatTactics.ThrowAtLastSeen(sight, new Vector3(10, 0, 0), 7, false, false, false, false), "close grenade rejected"); // 十二米内禁止。
        Check(!CombatTactics.ThrowAtLastSeen(sight, new Vector3(0, 4, 0), 7, false, false, false, false), "different floor grenade rejected"); // 避免跨楼层旧点。
        Check(!CombatTactics.ThrowAtLastSeen(sight, Vector3.Zero, 12, false, false, false, false), "stale grenade rejected"); // 六秒以上不再尝试。
    }

    /// <summary>危急避险、恢复和实时可见射击始终高于新增机会动作。</summary>
    private static void CombatTacticDecisionPriority()
    {
        var policy = new DecisionPolicy(); // 每项使用独立策略实例验证优先级。
        var skill = SkillProfile.Create(BotRole.Pmc, 60); // 使用能取得突击资格的等级。
        var input = new DecisionInput { HasClue = true, CanMove = true, AmbushFootstep = true, RushAfterSight = true, ShiftCover = true }; // 同时模拟多个候选，优先次序必须确定。
        Check(policy.Decide(input, skill, 1, 0.5f) == BehaviorState.Observe, "ambush before optional movement"); // 新近脚步先停看。
        input.PlayerDanger = true; // 玩家枪弹危险具有最高优先级。
        Check(policy.Decide(input, skill, 1.1, 0.5f) == BehaviorState.Evade, "danger preempts tactics"); // 不能被动作承诺锁住。
        input.PlayerDanger = false; input.RecoveryRunning = true; // 已开始的原生恢复接管。
        Check(policy.Decide(input, skill, 1.2, 0.5f) == BehaviorState.Recover, "native recovery preempts tactics"); // 不强制切枪或冲锋。
        policy.Reset(); input.RecoveryRunning = false; input.AmbushFootstep = false; input.ShiftCover = false; input.Visible = true; input.Reacted = true; // 可见真人交火已满足全部准备。
        Check(policy.Decide(input, skill, 2, 0.5f) == BehaviorState.Engage, "visible target keeps firing window"); // 隐藏旧点机会不能取代当前枪线。
    }

    /// <summary>近距腰射与远距 ADS 之间保持五米迟滞，异常距离不覆盖原生选择。</summary>
    private static void CombatAimDistanceHysteresis()
    {
        Check(!CombatAdaptation.AimDownSights(4, true), "close fight leaves ads"); // 五米内不继续举枪。
        Check(CombatAdaptation.AimDownSights(11, false), "distant fight enters ads"); // 十米外稳定瞄准。
        Check(CombatAdaptation.AimDownSights(7, true), "middle distance keeps ads"); // 已经举枪时不突然切回腰射。
        Check(!CombatAdaptation.AimDownSights(7, false), "middle distance keeps hipfire"); // 已经腰射时不反复举枪。
        Check(CombatAdaptation.AimDownSights(float.NaN, true), "invalid distance preserves native aim"); // 非法快照不能写动画。
    }
}
