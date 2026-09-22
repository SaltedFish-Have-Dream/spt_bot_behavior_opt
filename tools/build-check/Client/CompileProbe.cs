using System;
using BepInEx;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using HarmonyLib;
using SAIN.Components;
using SAIN.Interop;
using SAIN.Preset.Shared;
using SPT.Reflection.Patching;

namespace BotBehavior.BuildCheck;

// 仅验证客户端依赖和继承关系，不注册插件或改动 Bot 行为。
public abstract class PluginCompileProbe : BaseUnityPlugin
{
    public static Type[] ReferencedTypes => new[]
    {
        typeof(BotOwner), typeof(BotComponent), typeof(SAINCompileConstants),
        typeof(ModulePatch), typeof(Harmony), typeof(BrainManager)
    };
}

// 编译 SAIN 公开接口调用，验证已安装 DLL 的实际签名。
public static class SainCompileProbe
{
    public static string ReadPersonality(BotOwner bot) => SAINExternal.GetPersonality(bot);
}

// 验证 BigBrain 自定义动作接口；此类不会被注册或执行。
public sealed class LogicCompileProbe : CustomLogic
{
    public LogicCompileProbe(BotOwner bot) : base(bot) { }
    public override void Update(CustomLayer.ActionData data) { }
}
