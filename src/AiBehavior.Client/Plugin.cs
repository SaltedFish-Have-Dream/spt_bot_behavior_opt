using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BepInEx;
using BepInEx.Bootstrap;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace AiBehavior.Client;

[BepInPlugin(Id, "AI Behavior Opt", "0.1.7")]
[BepInDependency("xyz.drakia.bigbrain", "1.5.0")]
[BepInDependency("xyz.drakia.waypoints", "1.9.0")]
[BepInIncompatibility("me.sol.sain")]
[BepInIncompatibility("com.chazut.orbit")]
[BepInDependency("com.dvize.ailimit", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Id = "local.aibehavior.opt";
    internal static RaidRuntime? Runtime;
    private Harmony? _harmony;

    /// <summary>验证版本及冲突后，注册有角色限制的行为层和精确签名补丁。</summary>
    private void Awake()
    {
        try // 初始化失败时保留原生游戏能力。
        {
            var options = new PluginOptions(Config); // 配置仅在启动阶段读取。
            Logger.LogInfo($"[ABO] event=START version=0.1.7 build={typeof(Plugin).Module.ModuleVersionId:N} utc={DateTime.UtcNow:O} enabled={options.Enabled}"); // 二进制标识用于确认实际加载的是本次日志版本。
            if (!options.Enabled) { Logger.LogInfo("AI Behavior Opt 已关闭。"); return; } // 显式关闭时不注册任何运行逻辑。
            if (Chainloader.PluginInfos.ContainsKey("me.sol.sain") || Chainloader.PluginInfos.ContainsKey("com.chazut.orbit")) // 双重检查避免重复接管。
                throw new InvalidOperationException("请在独立测试配置中停用 SAIN 和 ORBIT 后再启用本模组。");
            string version = FileVersionInfo.GetVersionInfo(BepInEx.Paths.ExecutablePath).ProductVersion ?? ""; // 完全限定名称，避开游戏同名 Paths 类型。
            if (!version.StartsWith("0.16.9.5-40743-", StringComparison.Ordinal)) throw new InvalidOperationException("当前仅验证 EFT 构建 40743，检测到：" + version); // 未知构建不尝试补丁。
            string sptVersion = FileVersionInfo.GetVersionInfo(Path.Combine(BepInEx.Paths.PluginPath, "spt", "spt-core.dll")).ProductVersion ?? ""; // 核对本地 SPT 客户端版本。
            string brainVersion = FileVersionInfo.GetVersionInfo(typeof(BrainManager).Assembly.Location).ProductVersion ?? ""; // 核对实际加载的 BigBrain。
            if (!Chainloader.PluginInfos.TryGetValue("xyz.drakia.waypoints", out var waypointsInfo) || waypointsInfo.Metadata.Version.ToString() != "1.9.0") throw new InvalidOperationException("当前仅支持已加载的 Waypoints 1.9.0。"); // 前置必须实际被加载，不能只检查文件存在。
            if (sptVersion.Split('+')[0] != "4.1.5" || brainVersion.Split('+')[0] != "1.5.0") throw new InvalidOperationException("首版仅支持 SPT 4.1.5 与 BigBrain 1.5.0。"); // 未验证组合保留原生。
            Runtime = new RaidRuntime(options, Logger); // 创建共享运行时而非逐 Bot MonoBehaviour。
            Runtime.Diagnostics.Write("PLAYER_RULES", 0, Time.time, "mode=local-human-only closeMeters=30 searchMeters=120 dangerSeconds=5 nearBulletMeters=2.5 allyMeters=20 searchStepMeters=12 boss=native"); // 固定行为边界随启动日志留档。
            Runtime.Diagnostics.Write("TACTICAL_RULES", 0, Time.time, $"coverFailureMemory={options.CoverFailureMemory} suppressionResponse={options.SuppressionResponse} searchObservation={options.SearchObservation} coverCommitment={options.CoverCommitment} failedCoverCapacity=4 failedCoverSeconds=12 pressureWindowSeconds=0.25 searchPauseLimit=2"); // 启动时记录四个独立开关及有界策略，方便实测逐项对照。
            Runtime.Diagnostics.Write("NAVIGATION_RULES", 0, Time.time, "dependency=Waypoints version=1.9.0 completePathsOnly=True escapeMeters=5 repositionMeters=3 searchFailureSeconds=15"); // Waypoints 提供地图导航网格，行为路径仍需本模组完整验证。
            Runtime.Diagnostics.Write("VISION_RULES", 0, Time.time, "source=personal-confirmed-vision watchSeconds=1.25 repeekSeconds=8 sameAreaMeters=6 readinessFactor=0.8"); // 再次探头只缩短新增准备等待，不修改原生视觉能力。
            Runtime.Diagnostics.Write("CONFIG", 0, Time.time, FormattableString.Invariant($"eft={version} spt={sptVersion} bigBrain={brainVersion} scavs={options.ManageScavs} levels={options.MinimumLevel}-{options.MaximumLevel} softBudgetMs={options.MainThreadMilliseconds:F2} rays={options.RayRate} paths={options.PathRate} samples={options.SampleRate} overlaps={options.OverlapRate} summarySeconds={options.SummarySeconds} eventLogging={options.EventLogging} eventRate={options.EventLogsPerSecond} traceBots={options.TraceBots}")); // 只输出与本次行为诊断相关的配置。
            _harmony = new Harmony(Id); // 使用独立补丁标识，便于清理。
            _harmony.PatchAll(typeof(Plugin).Assembly); // 编译期类型与运行期签名共同验证补丁目标。
            bool limitCompatibility = AiLimitCompatibility.TryInstall(_harmony, Runtime); // 可选兼容只影响已有真人情境的排序。
            int patchedMethods = 0; // 启动时核对当前 Harmony 标识实际拥有的目标数量。
            foreach (var method in _harmony.GetPatchedMethods()) // 反射枚举仅发生一次，不进入 Bot 热循环。
            {
                patchedMethods++; // 一个方法上的前置和后置合计为一个补丁目标。
                Runtime.Diagnostics.Write("PATCH_APPLIED", 0, Time.time, $"target={method.DeclaringType?.FullName}.{method.Name}"); // 记录入口名称便于逐项判断是否完成接入。
            }
            var roles = new List<WildSpawnType> { WildSpawnType.pmcBEAR, WildSpawnType.pmcUSEC }; // 明确区分 PMC 与 Raider。
            if (options.ManageScavs) roles.AddRange(new[] { WildSpawnType.assault, WildSpawnType.assaultGroup, WildSpawnType.marksman }); // 特殊 Scav 不自动纳入。
            var brains = new List<string> { "PmcBear", "PmcUsec", "PMC", "Assault", "Marksman" }; // 仅使用本地程序集核对过的 Brain。
            int layerId = BrainManager.AddCustomLayer(typeof(BehaviorLayer), brains, 71, roles); // 高于普通战斗和请求层，低于 78/80 的故障与避险层。
            Runtime.Diagnostics.Write("READY", 0, Time.time, $"patchedMethods={patchedMethods} expectedMethods={(limitCompatibility ? 12 : 11)} layerId={layerId} priority=71"); // 可选限流排序补丁单独计数，初始化不能替代行为验收。
            Logger.LogInfo("AI Behavior Opt 0.1.7 已初始化：SPT 4.1.5 / EFT 40743 / BigBrain 1.5.0 / Waypoints 1.9.0。尚需战局验证。");
        }
        catch (Exception exception) // 防止半初始化状态留下持续覆盖。
        {
            Shutdown(); // 撤销本插件补丁并释放运行时。
            Logger.LogError("[ABO] event=INIT_FAILED AI Behavior Opt 初始化失败，保留原生行为：" + exception); // 初始化错误仅输出一次。
        }
    }

    /// <summary>每帧先处理轻量的生命周期与视线安全检查。</summary>
    private void Update()
    {
        Runtime?.Update(Time.time, Time.frameCount);
    }

    /// <summary>在动作执行之后使用剩余射线预算处理可延后的查询。</summary>
    private void LateUpdate()
    {
        Runtime?.ProcessQueries(Time.time, Time.frameCount);
    }

    /// <summary>插件销毁时只撤销自身资源。</summary>
    private void OnDestroy()
    {
        Shutdown();
    }

    /// <summary>幂等清理，残留的 BigBrain 层因无运行时自动退出。</summary>
    private void Shutdown()
    {
        Runtime?.Dispose();
        Runtime = null;
        _harmony?.UnpatchSelf();
        _harmony = null;
    }
}
