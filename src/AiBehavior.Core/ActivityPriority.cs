using System;

namespace AiBehavior.Core;

/// <summary>为活动限流提供有界排序分数，不改变可选 Bot 数量和原生激活职责。</summary>
public static class ActivityPriority
{
    /// <summary>只提升一百二十米内已确认真人情境的排序，其余输入保持不变。</summary>
    public static float Score(float distanceSquared, bool playerContext)
    {
        if (!playerContext || float.IsNaN(distanceSquared) || distanceSquared < 0 || distanceSquared > 14400) return distanceSquared; // 非法输入、远处与 AI 互战均不提升。
        return -1 + distanceSquared / 100000000f; // 提升组内部仍保持远近次序，调用方的数量截断继续生效。
    }
}
