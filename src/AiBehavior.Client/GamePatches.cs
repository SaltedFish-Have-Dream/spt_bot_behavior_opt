using System;
using AiBehavior.Core;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace AiBehavior.Client;

[HarmonyPatch(typeof(BotOwner), nameof(BotOwner.method_10))]
internal static class ActivatePatch
{
    /// <summary>目标版本完整激活后注册，避免访问尚未初始化的武器和 Brain。</summary>
    [HarmonyPostfix]
    private static void Postfix(BotOwner __instance)
    {
        try { Plugin.Runtime?.Register(__instance); }
        catch (Exception exception) { Plugin.Runtime?.Log.LogError("Bot 注册失败，保留原生：" + exception); }
    }
}

[HarmonyPatch(typeof(BotOwner), nameof(BotOwner.Dispose))]
internal static class DisposeBotPatch
{
    /// <summary>原生对象释放前取消任务和动作。</summary>
    [HarmonyPrefix]
    private static void Prefix(BotOwner __instance)
    {
        Plugin.Runtime?.Remove(__instance);
    }
}

[HarmonyPatch(typeof(GameWorld), nameof(GameWorld.Dispose))]
internal static class DisposeWorldPatch
{
    /// <summary>离开战局时释放所有 Bot 引用，下次战局重新建立上下文。</summary>
    [HarmonyPrefix]
    private static void Prefix()
    {
        Plugin.Runtime?.Dispose();
    }
}

[HarmonyPatch(typeof(EnemyInfo), nameof(EnemyInfo.CheckLookEnemy))]
internal static class VisionPatch
{
    /// <summary>读取原生已完成的个人视觉检查，不新增敌人扫描。</summary>
    [HarmonyPostfix]
    private static void Postfix(EnemyInfo __instance)
    {
        RaidRuntime? runtime = Plugin.Runtime; // 保存当前运行时，避免卸载时二次读取。
        if (runtime?.TryGet(__instance.Owner, out BotAgent agent) != true) return; // 原生角色完全旁路。
        long start = runtime.BeginWork(); // 观察处理成本也计入诊断。
        try { agent.ObserveVision(__instance, Time.time); } // 仅真正可见的个人位置可写入记忆。
        catch (Exception exception) { runtime.Fail(agent, exception); } // 错误只降级相关 Bot。
        finally { runtime.Charge(start); } // 异常处理也需要计时。
    }
}

[HarmonyPatch(typeof(BotHearingSensor), nameof(BotHearingSensor.SoundHeared))]
internal static class HearingPatch
{
    /// <summary>复用原生声音过滤结果，替换已听到声音的定位和调查分支。</summary>
    [HarmonyPrefix]
    private static bool Prefix(BotHearingSensor __instance, IPlayer enemy, Vector3 pos, bool wasHeard)
    {
        RaidRuntime? runtime = Plugin.Runtime; // 只操作已注册的角色。
        if (!wasHeard || runtime?.TryGet(__instance._botOwner, out BotAgent agent) != true) return true; // 未听见的近弹危险仍交原生处理。
        if (enemy != null && enemy.AIData.IsAI && agent.Owner.BotsGroup.Contains(enemy.AIData.BotOwner)) return false; // 同队声音不当成敌情。
        long start = runtime.BeginWork(); // 听觉事件风暴必须反映在统计中。
        try { agent.ObserveSound(enemy?.ProfileId ?? "unknown-sound", pos, Time.time); return false; } // 不再让原生分支向全队广播精确位置。
        catch (Exception exception) { runtime.Fail(agent, exception); return true; } // 失效后恢复原生听觉。
        finally { runtime.Charge(start); } // 合并后的低成本事件同样计时。
    }
}

[HarmonyPatch(typeof(BotMemory), nameof(BotMemory.GoalEnemy), MethodType.Setter)]
internal static class GoalEnemyPatch
{
    /// <summary>阻止没有个人视觉或有效线索的旧敌人被重新设为精确目标。</summary>
    [HarmonyPrefix]
    private static void Prefix(BotMemory __instance, ref EnemyInfo value)
    {
        if (value == null || Plugin.Runtime?.TryGet(__instance._owner, out BotAgent agent) != true) return; // 未接管角色和清空操作不改写。
        if (!GameAdapter.DirectlyVisible(value, Time.time) && !agent.Memory.TryGet(value.ProfileId, Time.time, out _)) value = null!; // 允许直接新视觉，拒绝隐藏且无记忆的目标。
    }
}

[HarmonyPatch(typeof(BotAimingData), nameof(BotAimingData.SetTarget))]
internal static class AimTargetPatch
{
    /// <summary>原生层恢复时也不能直接瞄准已经不可见的敌人。</summary>
    [HarmonyPrefix]
    internal static bool Prefix(BotAimingData __instance, ref Vector3 __0)
    {
        if (Plugin.Runtime?.TryGet(__instance._owner, out BotAgent agent) != true) return true; // 特殊角色保留原生。
        if (agent.TryVisibleObservation(__instance._owner.Memory.GoalEnemy, Time.time, out Observation observation)) // 只允许使用个人视觉快照。
        { __0 = GameAdapter.Position(observation.AimPosition); return true; } // 原生动作同样不能读取两次观察之间的实时隐藏坐标。
        __instance.LoseTarget(); // 清除瞄准状态，不把输入的隐藏坐标写入瞄准器。
        return false;
    }
}

[HarmonyPatch(typeof(BotAimingData), nameof(BotAimingData.UpdateTarget))]
internal static class AimUpdatePatch
{
    /// <summary>覆盖原生瞄准器的另一条位置更新入口，防止绕过 SetTarget 检查。</summary>
    [HarmonyPrefix]
    private static bool Prefix(BotAimingData __instance, ref Vector3 __0)
    {
        return AimTargetPatch.Prefix(__instance, ref __0);
    }
}

[HarmonyPatch(typeof(ShootData), nameof(ShootData.Shoot))]
internal static class ShootPatch
{
    /// <summary>对自有和原生动作统一应用反应门槛、视线和预算检查。</summary>
    [HarmonyPrefix]
    private static bool Prefix(ShootData __instance, ref bool __result)
    {
        RaidRuntime? runtime = Plugin.Runtime; // 只取一次共享运行时。
        if (runtime?.TryGet(__instance._owner, out BotAgent agent) != true) return true; // Boss 等角色完全保留原生射击。
        long start = runtime.BeginWork(); // 将物理验证计入实际新增耗时。
        try // 单 Bot 异常允许原生回退。
        {
            if (agent.AllowShot(Time.time, Time.frameCount)) return true; // 原生 Shoot 继续核对其全部条件。
            runtime.BlockedShots++; // 统计门控发生次数，便于排查长时间等待。
            __result = false; // 保持调用者可识别的失败返回值。
            return false; // 本次不按下扳机。
        }
        catch (Exception exception) { runtime.Fail(agent, exception); return true; } // 只关闭出错 Bot 的接管。
        finally { runtime.Charge(start); } // 无论允许、拒绝还是异常都计时。
    }
}
