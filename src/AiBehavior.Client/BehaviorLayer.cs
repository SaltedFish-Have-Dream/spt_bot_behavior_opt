using System;
using AiBehavior.Core;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;

namespace AiBehavior.Client;

/// <summary>通过较高普通战斗优先级接管，原生避险、故障与撤离仍可抢占。</summary>
public sealed class BehaviorLayer : CustomLayer
{
    private readonly CustomLayer.Action _action;

    /// <summary>动作对象只创建一次，避免每次层询问产生分配。</summary>
    public BehaviorLayer(BotOwner botOwner, int priority) : base(botOwner, priority)
    {
        _action = new CustomLayer.Action(typeof(BehaviorLogic), "轻量拟真行为");
    }

    /// <summary>提供固定层名用于日志和 BigBrain 诊断。</summary>
    public override string GetName()
    {
        return "AI Behavior Opt";
    }

    /// <summary>只读取缓存状态与原生动作锁，不在激活判断中计算路线。</summary>
    public override bool IsActive()
    {
        return Plugin.Runtime?.TryGet(BotOwner, out BotAgent agent) == true && agent.ShouldControl;
    }

    /// <summary>返回缓存动作，由内部状态机进行有限状态迁移。</summary>
    public override CustomLayer.Action GetNextAction()
    {
        return _action;
    }

    /// <summary>同一动作持续执行到层失活，避免重复构建执行器。</summary>
    public override bool IsCurrentActionEnding()
    {
        return !IsActive();
    }
}

/// <summary>BigBrain 动作只负责控制权和必须逐帧执行的原生动作调用。</summary>
public sealed class BehaviorLogic : CustomLogic
{
    /// <summary>保存 BigBrain 提供的 BotOwner。</summary>
    public BehaviorLogic(BotOwner botOwner) : base(botOwner) { }

    /// <summary>取得控制时取消旧移动，保存需要还原的姿态。</summary>
    public override void Start()
    {
        if (Plugin.Runtime?.TryGet(BotOwner, out BotAgent agent) != true) return; // 销毁后不再取得控制。
        long start = agent.Runtime.BeginWork(WorkPhase.Lifecycle); // 把暂停巡逻和姿态交接纳入阶段统计。
        try { agent.Claim(); } // 一次性建立自有动作。
        catch (Exception exception) { agent.Runtime.Fail(agent, exception); } // 交接失败只降级当前 Bot。
        finally { agent.Runtime.Charge(start); } // 包含失败处理成本。
    }

    /// <summary>控制被原生高优先级行为抢占时及时清理。</summary>
    public override void Stop()
    {
        if (Plugin.Runtime?.TryGet(BotOwner, out BotAgent agent) != true) return; // 已注销的动作无需再次交接。
        long start = agent.Runtime.BeginWork(WorkPhase.Lifecycle); // 单独记录 BigBrain 主动交回控制的成本。
        try { agent.Release(); } // 幂等取消旧任务。
        catch (Exception exception) { agent.Runtime.Fail(agent, exception); } // 失效对象不持续抛错。
        finally { agent.Runtime.Charge(start); } // 嵌套清理不会重复累计。
    }

    /// <summary>执行已有决策，昂贵查询由全局队列另行处理。</summary>
    public override void Update(CustomLayer.ActionData data)
    {
        RaidRuntime? runtime = Plugin.Runtime; // 固定本次回调的运行时引用。
        if (runtime?.TryGet(BotOwner, out BotAgent agent) != true) return; // 销毁或回退后不再调用游戏对象。
        long start = runtime.BeginWork(WorkPhase.Action); // 动作执行成本也进入诊断。
        try { agent.Execute(Time.time); } // 只执行当前动作，不在这里重选战术。
        catch (Exception exception) { runtime.Fail(agent, exception); } // 当前 Bot 出错后交还原生。
        finally { runtime.Charge(start); } // 异常路径也计入耗时。
    }
}
