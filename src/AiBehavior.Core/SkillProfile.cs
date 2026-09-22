using System;

namespace AiBehavior.Core;

/// <summary>仅显式支持普通角色，其余角色使用原生行为。</summary>
public enum BotRole { Native, Scav, Marksman, Pmc }

/// <summary>每个 Bot 创建时生成的能力快照，不包含调度频率或查询额度。</summary>
public readonly struct SkillProfile
{
    public readonly float ReactionSeconds;
    public readonly float AimSeconds;
    public readonly float HearingError;
    public readonly float MemorySeconds;
    public readonly float TacticalProbability;
    public readonly float ReportSeconds;

    /// <summary>保存已经验证的能力参数。</summary>
    public SkillProfile(float reaction, float aim, float hearing, float memory, float tactical, float report)
    {
        ReactionSeconds = reaction;
        AimSeconds = aim;
        HearingError = hearing;
        MemorySeconds = memory;
        TacticalProbability = tactical;
        ReportSeconds = report;
    }

    /// <summary>按 Bot 自身角色与等级生成快照，固定角色不读取等级曲线。</summary>
    public static SkillProfile Create(BotRole role, int level, int minimum = 1, int maximum = 60)
    {
        if (role == BotRole.Scav) return new SkillProfile(1.1f, 1f, 10f, 6f, 0.3f, 1.2f); // 普通 Scav 使用固定模板。
        if (role == BotRole.Marksman) return new SkillProfile(1f, 0.9f, 8f, 8f, 0.45f, 1.2f); // 狙击 Scav 使用独立固定模板。
        if (role != BotRole.Pmc) return default; // 特殊角色不接收通用能力覆盖。
        if (minimum < 1 || maximum <= minimum) { minimum = 1; maximum = 60; } // 非法配置整体回退，避免除零。
        double factor = ((double)level - minimum) / ((double)maximum - minimum); // 使用双精度避免极端整数溢出。
        float t = (float)Math.Max(0, Math.Min(1, factor)); // 成长区间外固定在端点。
        return new SkillProfile(0.9f - 0.6f * t, 0.8f - 0.45f * t, 8f - 5f * t,
            6f + 8f * t, 0.3f + 0.55f * t, 1.2f - 0.8f * t); // 所有受等级控制的基线使用同一线性因子。
    }
}

/// <summary>独立于 Unity 全局随机状态的稳定随机序列。</summary>
public sealed class BotRandom
{
    private uint _state;

    /// <summary>根据角色标识构造可重复的非零种子。</summary>
    public BotRandom(string identity)
    {
        uint seed = 2166136261; // 使用稳定散列，避免平台相关的字符串散列。
        foreach (char character in identity) seed = unchecked((seed ^ character) * 16777619); // 每个字符合并到种子。
        _state = seed == 0 ? 1u : seed; // 防止移位序列停留在零状态。
    }

    /// <summary>只在新线索或新决策上下文产生时抽样。</summary>
    public float Next01()
    {
        _state ^= _state << 13; // 混合低位。
        _state ^= _state >> 17; // 混合高位。
        _state ^= _state << 5; // 完成有界、无分配的状态更新。
        return (_state >> 8) / 16777216f; // 返回小于一的概率值。
    }
}
