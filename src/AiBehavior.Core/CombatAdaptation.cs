using System;

namespace AiBehavior.Core;

/// <summary>固定交战倾向只改变战术选择概率，不改变等级成长或计算额度。</summary>
public enum CombatTemperament { Balanced, Cautious, Aggressive }

/// <summary>武器模式选择的纯值结果，具体武器兼容性由客户端核对。</summary>
public enum CombatFireMode { None, Single, Burst, FullAuto }

/// <summary>复用已知距离、能力和武器支持集做常数时间交战调整。</summary>
public static class CombatAdaptation
{
    /// <summary>PMC 出生时抽取一次稳定风格；Scav 保持固定模板，特殊角色不参与。</summary>
    public static CombatTemperament Temperament(BotRole role, float roll)
    {
        if (role != BotRole.Pmc || float.IsNaN(roll) || float.IsInfinity(roll)) return CombatTemperament.Balanced; // 不让固定角色随随机值改变行为。
        if (roll < 0.25f) return CombatTemperament.Cautious; // 少数 PMC 更偏好掩体。
        return roll >= 0.75f ? CombatTemperament.Aggressive : CombatTemperament.Balanced; // 其余保持稳定的均衡或主动风格。
    }

    /// <summary>风格只微调掩体倾向，高等级已有的战术成长仍作为主要基线。</summary>
    public static float TacticalProbability(float baseProbability, CombatTemperament temperament)
    {
        float bias = temperament == CombatTemperament.Cautious ? 0.12f : temperament == CombatTemperament.Aggressive ? -0.12f : 0f; // 单次固定偏置避免每帧重抽性格。
        return Math.Max(0f, Math.Min(1f, baseProbability + bias)); // 保持合法概率区间。
    }

    /// <summary>近距优先自动、远距优先单发，中距维持已有模式以避免来回切换。</summary>
    public static CombatFireMode SelectFireMode(float distance, bool single, bool burst, bool fullAuto, CombatFireMode current)
    {
        if (float.IsNaN(distance) || float.IsInfinity(distance) || distance < 0) return CombatFireMode.None; // 非法视觉距离不能改武器。
        if (distance <= 22) return fullAuto ? CombatFireMode.FullAuto : burst ? CombatFireMode.Burst : single ? CombatFireMode.Single : CombatFireMode.None; // 贴近交战选择武器确实支持的模式。
        if (distance >= 55) return single ? CombatFireMode.Single : burst ? CombatFireMode.Burst : fullAuto ? CombatFireMode.FullAuto : CombatFireMode.None; // 远处不让不支持单发的枪进入非法模式。
        if (current == CombatFireMode.Single && single || current == CombatFireMode.Burst && burst || current == CombatFireMode.FullAuto && fullAuto) return current; // 中距保持现状形成距离迟滞。
        return single ? CombatFireMode.Single : burst ? CombatFireMode.Burst : fullAuto ? CombatFireMode.FullAuto : CombatFireMode.None; // 当前模式失效时才回退到可用模式。
    }

    /// <summary>按真实视觉距离选择举枪瞄准，五至十米迟滞避免贴脸交战频繁切换动画。</summary>
    public static bool AimDownSights(float distance, bool currentlyAiming)
    {
        if (float.IsNaN(distance) || float.IsInfinity(distance) || distance < 0) return currentlyAiming; // 非法快照不改变原生瞄准状态。
        return distance > (currentlyAiming ? 5f : 10f); // 远距 ADS、贴脸腰射，中间维持当前状态。
    }
}
