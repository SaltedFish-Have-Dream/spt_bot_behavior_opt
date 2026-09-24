using AiBehavior.Core;

namespace AiBehavior.Core.Tests;

/// <summary>受击同组近邻只能产生一个有来源、可行动的推进名额。</summary>
internal static partial class Program
{
    /// <summary>最近合格成员获选，远处、未告警和缺弹成员都不能抢名额。</summary>
    private static void SquadAdvanceSelectsOne()
    {
        SquadAdvanceChoice choice = default; // 每次直接命中使用独立的值选择器。
        choice.Consider(1, false, true, 1); // 没收到二十米告警者不能靠近就自动知晓玩家。
        choice.Consider(2, true, false, 4); // 缺弹成员先避险但不承担推进。
        choice.Consider(3, true, true, 225); // 第一个合格成员先进入候选。
        choice.Consider(4, true, true, 25); // 更近的合格成员替换候选。
        choice.Consider(5, true, true, 500); // 二十米外不能加入本次单跳告警。
        Check(choice.SelectedId == 4, "one nearest ready ally advances"); // 固定状态只保存一个匿名序号。
        choice.Consider(6, true, true, 25); // 相同距离不因注册顺序抖动。
        Check(choice.SelectedId == 4, "tie keeps stable earlier ally"); // 保持本轮稳定选择。
    }

    /// <summary>无合法成员时保持空位，下一次命中独立重新选择。</summary>
    private static void SquadAdvanceRejectsInvalid()
    {
        SquadAdvanceChoice choice = default; // 不跨命中事件持有队友引用。
        choice.Consider(1, true, true, float.NaN); // 非法距离不得绕过空间筛选。
        choice.Consider(0, true, true, 1); // 非法匿名序号不能成为目标。
        choice.Consider(2, true, true, 401); // 二十米以外保留原有个体行为。
        Check(choice.SelectedId == 0, "no valid ally leaves victim fallback"); // 受害者仍可沿用五秒后推进规则。
        choice.Consider(7, true, true, 400); // 二十米边界仍可告警。
        Check(choice.SelectedId == 7, "inclusive near-ally range"); // 精确空间边界可复测。
    }

    /// <summary>未获名额者在危险消退后观察原地，只有亲眼看见玩家时才交战。</summary>
    private static void SquadAdvanceNonLeaderHolds()
    {
        var policy = new DecisionPolicy(); // 独立策略不继承前一名 Bot 的动作承诺。
        SkillProfile skill = SkillProfile.Create(BotRole.Pmc, 20); // 等级只影响能力，不赋予额外推进名额。
        var input = new DecisionInput { HasClue = true, CanMove = true, HoldAfterSquadDanger = true, ContextVersion = 1 }; // 模拟五秒后仍有玩家危险线索。
        Check(policy.Decide(input, skill, 5, 0.5f) == BehaviorState.Observe, "unselected ally holds instead of searching"); // 未见玩家时不集体走向同一快照。
        input.Visible = true; // 新视觉来自本人，允许直接交战。
        input.Reacted = true; // 原有反应门仍需满足。
        Check(policy.Decide(input, skill, 5.1, 0.5f) == BehaviorState.Engage, "visible player allows covering fire"); // 小队身份不能阻止真实自卫。
        input.Visible = false; // 本人再次失去视线。
        input.AdvanceAfterDanger = true; // 新一轮选择让该成员获得唯一名额。
        input.HoldAfterSquadDanger = false; // 名额转移时清除守点标志。
        Check(policy.Decide(input, skill, 5.2, 0.5f) == BehaviorState.Advance, "selected ally can advance"); // 危险已过且武器状态允许时才推进。
    }
}
