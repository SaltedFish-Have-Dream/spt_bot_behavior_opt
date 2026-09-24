using AiBehavior.Core;

namespace AiBehavior.Core.Tests;

/// <summary>验证固定风格与支持集武器模式不会覆盖危险优先级或产生非法切换。</summary>
internal static partial class Program
{
    /// <summary>PMC 固定风格只微调战术概率，Scav 与特殊角色保持既定模板。</summary>
    private static void CombatTemperamentBoundaries()
    {
        Check(CombatAdaptation.Temperament(BotRole.Pmc, 0.1f) == CombatTemperament.Cautious, "low roll is cautious"); // 出生时确定谨慎风格。
        Check(CombatAdaptation.Temperament(BotRole.Pmc, 0.5f) == CombatTemperament.Balanced, "middle roll is balanced"); // 中间区间保持基线。
        Check(CombatAdaptation.Temperament(BotRole.Pmc, 0.9f) == CombatTemperament.Aggressive, "high roll is aggressive"); // 高区间形成主动风格。
        Check(CombatAdaptation.Temperament(BotRole.Scav, 0.1f) == CombatTemperament.Balanced, "scav stays fixed"); // Scav 不因为等级或抽签拥有 PMC 性格。
        Check(CombatAdaptation.Temperament(BotRole.Native, 0.9f) == CombatTemperament.Balanced, "native role unaffected"); // Boss 等不进入自有风格。
        Check(CombatAdaptation.TacticalProbability(0.85f, CombatTemperament.Cautious) > CombatAdaptation.TacticalProbability(0.85f, CombatTemperament.Aggressive), "temperaments keep distinct choices"); // 等级仍是共同能力基线。
        Check(CombatAdaptation.TacticalProbability(1f, CombatTemperament.Cautious) == 1f && CombatAdaptation.TacticalProbability(0f, CombatTemperament.Aggressive) == 0f, "style probability clamped"); // 极端设置不越界。
    }

    /// <summary>相同等级与视觉下风格影响普通掩体选择，真实危险仍强制避险。</summary>
    private static void CombatTemperamentDecision()
    {
        SkillProfile profile = new(0.6f, 0.5f, 6, 10, 0.5f, 0.8f); // 只固定测试所需的能力基线。
        var cautious = new DecisionPolicy(); // 两名 Bot 使用独立的动作承诺。
        var aggressive = new DecisionPolicy();
        DecisionInput input = new() { HasClue = true, Visible = true, Reacted = true, CanMove = true, HasCover = true, ContextVersion = 1, Temperament = CombatTemperament.Cautious }; // 合法掩体与真实视线同时存在。
        Check(cautious.Decide(input, profile, 10, 0.55f) == BehaviorState.Cover, "cautious bot uses cover"); // 谨慎偏置跨过同一随机门槛。
        input.Temperament = CombatTemperament.Aggressive; // 只改变出生时风格。
        Check(aggressive.Decide(input, profile, 10, 0.55f) == BehaviorState.Engage, "aggressive bot holds fire line"); // 主动 Bot 仍保有原地交战机会。
        input.PlayerDanger = true; // 玩家直接命中不接受性格覆盖。
        input.ContextVersion = 2;
        Check(aggressive.Decide(input, profile, 10.1, 0.9f) == BehaviorState.Evade, "danger overrides aggression"); // 保命优先级高于风格。
        input.PlayerDanger = false; // 队友收到告警后不持有推进名额。
        input.AdvanceAfterDanger = false;
        input.ContextVersion = 3;
        Check(aggressive.Decide(input, profile, 16, 0.9f) != BehaviorState.Advance, "unselected squad member does not push"); // 单人推进门控可由现有决策值输入保证。
    }

    /// <summary>近远距离只选武器支持的模式，中间距离用迟滞避免反复切换。</summary>
    private static void CombatFireModeBoundaries()
    {
        Check(CombatAdaptation.SelectFireMode(10, true, true, true, CombatFireMode.Single) == CombatFireMode.FullAuto, "near chooses supported auto"); // 近距提高火力密度。
        Check(CombatAdaptation.SelectFireMode(70, true, true, true, CombatFireMode.FullAuto) == CombatFireMode.Single, "far chooses supported single"); // 远距保留原生瞄准和后坐力。
        Check(CombatAdaptation.SelectFireMode(35, true, true, true, CombatFireMode.FullAuto) == CombatFireMode.FullAuto, "middle preserves mode"); // 中间区间不来回写动画。
        Check(CombatAdaptation.SelectFireMode(10, true, true, false, CombatFireMode.Single) == CombatFireMode.Burst, "auto absent falls back to burst"); // 不请求武器不支持的自动模式。
        Check(CombatAdaptation.SelectFireMode(70, false, true, true, CombatFireMode.FullAuto) == CombatFireMode.Burst, "single absent falls back to burst"); // 不请求不存在的单发模式。
        Check(CombatAdaptation.SelectFireMode(10, false, false, false, CombatFireMode.None) == CombatFireMode.None, "unsupported weapon stays native"); // 无三种模式时不改原生状态。
        Check(CombatAdaptation.SelectFireMode(float.NaN, true, true, true, CombatFireMode.Single) == CombatFireMode.None, "invalid distance cannot switch"); // 非法视觉距离不能写武器。
    }
}
