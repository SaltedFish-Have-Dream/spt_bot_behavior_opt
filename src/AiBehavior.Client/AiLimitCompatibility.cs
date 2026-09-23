using System;
using System.Reflection;
using BepInEx.Bootstrap;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace AiBehavior.Client;

/// <summary>仅为已核对的 AI Limit 1.9.2 调整排序，不改数量上限、不操作其私有 Bot 列表或强制唤醒。</summary>
internal static class AiLimitCompatibility
{
    /// <summary>启动阶段验证可选插件版本及精确接口；不支持时保留原插件行为。</summary>
    internal static bool TryInstall(Harmony harmony, RaidRuntime runtime)
    {
        if (!Chainloader.PluginInfos.TryGetValue("com.dvize.ailimit", out var info)) return false; // 未安装时不创建额外补丁。
        if (info.Metadata.Version.ToString() != "1.9.2") // 未验证版本不能套用私有实现。
        {
            runtime.Diagnostics.Write("AI_LIMIT_COMPAT", 0, Time.time, $"enabled=False version={info.Metadata.Version} reason=unsupported-version"); // 一次性报告兼容边界。
            return false;
        }
        Type? component = info.Instance.GetType().Assembly.GetType("AILimit.AILimitComponent"); // 仅启动时按实际加载程序集查找类型。
        MethodInfo? target = component?.GetMethod("getMinDistanceToBot", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Player) }, null); // 已核对原函数返回平方距离并用于排序。
        if (target == null || target.ReturnType != typeof(float)) // 接口不一致时只跳过此适配。
        {
            runtime.Diagnostics.Write("AI_LIMIT_COMPAT", 0, Time.time, "enabled=False reason=signature-mismatch"); // 不把可选兼容失败变成整个模组启动失败。
            return false;
        }
        try { harmony.Patch(target, postfix: new HarmonyMethod(typeof(AiLimitCompatibility), nameof(Prioritize))); } // 热路径没有反射或全图搜索。
        catch (Exception exception)
        {
            harmony.Unpatch(target, HarmonyPatchType.Postfix, Plugin.Id); // 可选补丁失败时撤销可能留下的自有部分，不动其他插件。
            runtime.Log.LogWarning("[ABO] event=AI_LIMIT_COMPAT enabled=False reason=patch-failed detail=" + exception.Message); // 保留基础行为模组运行，兼容故障只报告一次。
            return false;
        }
        runtime.Diagnostics.Write("AI_LIMIT_COMPAT", 0, Time.time, "enabled=True version=1.9.2 mode=player-threat-priority maxDistance=120 botCap=unchanged wake=limiter-owned"); // 明确排序保护不等于无限活动数量。
        return true;
    }

    /// <summary>已有玩家事件的近处 Bot 优先进入原限额，AI 互战、过期线索和远处对象保持原排序。</summary>
    private static void Prioritize(Player __0, ref float __result)
    {
        RaidRuntime? runtime = Plugin.Runtime; // 插件卸载或未就绪时旁路。
        BotOwner? owner = __0?.AIData?.BotOwner; // 不把真人或无有效 AI 上下文的对象当作 Bot。
        if (runtime == null || owner == null || __result < 0 || __result > 14400 || float.IsNaN(__result) || !runtime.TryGet(owner, out BotAgent agent)) return; // 原始平方距离限定一百二十米。
        long start = runtime.BeginWork(AiBehavior.Core.WorkPhase.Perception); // 可选插件回调纳入原有插桩范围。
        float originalDistance = __result; // 异常时恢复原始排序值。
        try
        {
            if (!agent.HasPlayerActivity(Time.time)) return; // 仅存在有效真人情境时排序优先，不能被 AI 枪声触发。
            float distance = __result; // 只保存原函数已计算的距离，不额外读取玩家位置。
            __result = AiBehavior.Core.ActivityPriority.Score(distance, true); // 原插件照常只选数量上限内的对象。
            if (runtime.Diagnostics.Record(DiagnosticEvent.LimitPriority, agent, Time.time)) runtime.Diagnostics.Write("AI_LIMIT_PRIORITY", agent.Id, Time.time, FormattableString.Invariant($"distance={Mathf.Sqrt(distance):F1} active={owner.BotState == EBotState.Active} reason=player-context")); // 不宣称该对象必定获准活动。
        }
        catch (Exception exception) // 兼容调用失败时不能破坏原插件排序或全局更新。
        {
            __result = originalDistance; // 失败不得留下半完成的排序修改。
            runtime.Fail(agent, exception); // 当前 Bot 降级并退出后续自有排序。
        }
        finally { runtime.Charge(start); } // 无论是否调整均正确关闭计时。
    }
}
