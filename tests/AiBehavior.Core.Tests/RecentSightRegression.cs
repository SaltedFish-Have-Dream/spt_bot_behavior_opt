using System.Numerics;
using AiBehavior.Core;

namespace AiBehavior.Core.Tests;

/// <summary>验证再次探头只能利用本 Bot 的真实旧视觉，而不能变成隐藏追踪。</summary>
internal static partial class Program
{
    /// <summary>失视只武装一次短守点，过期后不再返回旧位置。</summary>
    private static void SightWatchWindow()
    {
        var awareness = new RecentSightAwareness();
        Check(!awareness.MarkLost(10), "never seen cannot watch"); // 没有亲眼看见不能守点。
        awareness.ObserveVisible("player", new Vector3(10, 0, 0), 10); // 有效的个人视觉快照。
        Check(awareness.MarkLost(10.1), "fresh loss arms watch"); // 真实可见转不可见才武装。
        Check(!awareness.MarkLost(10.2), "same loss cannot refresh watch"); // 重复回调不能延长窗口。
        Check(awareness.CanWatch("player", 11.3, out Vector3 point) && point.X == 10, "watch old snapshot"); // 守的是最后位置。
        Check(!awareness.CanWatch("player", 11.35, out _), "watch expiry exact"); // 到期应交回搜索。
        Check(!awareness.CanWatch("other", 10.5, out _), "other target cannot use snapshot"); // 身份变化不能共享位置。
        awareness.Clear(); // 模拟离开玩家情境。
        Check(!awareness.CanWatch("player", 10.5, out _), "context clear removes watch"); // 旧窗口不能复活。
    }

    /// <summary>再次探头必须在原位置六米内由新视觉证明，时间与目标切换有边界。</summary>
    private static void RepeekBoundaries()
    {
        var awareness = new RecentSightAwareness();
        awareness.ObserveVisible("player", new Vector3(10, 0, 0), 10); // 固定旧视觉点。
        Check(awareness.MarkLost(10.2), "loss accepted"); // 建立再发现八秒时效。
        Check(!awareness.TryReacquire("other", new Vector3(10, 0, 0), 11), "different identity rejected"); // 不把别人的位置当成再发现。
        Check(awareness.TryReacquire("player", new Vector3(16, 0, 0), 11), "same edge within six meters"); // 六米边界有效。
        Check(awareness.IsPrimed("player"), "recognized target primed"); // 本次连续可见窗口获得资格。
        Check(!awareness.IsPrimed("other"), "boost identity isolated"); // 其他目标不能使用。
        awareness.ObserveVisible("player", new Vector3(16, 0, 0), 11); // 再次视觉更新新位置。
        Check(awareness.MarkLost(11.1) && !awareness.IsPrimed("player"), "new loss clears previous boost"); // 新遮挡需要再次真实确认。
        Check(!awareness.TryReacquire("player", new Vector3(23, 0, 0), 12), "different area no boost"); // 七米外不得借旧掩体优势。
        awareness.ObserveVisible("player", Vector3.Zero, 20); // 新一次独立接敌。
        Check(awareness.MarkLost(20.1), "new encounter armed"); // 确认窗口重新开始。
        Check(!awareness.TryReacquire("player", Vector3.Zero, 28.1), "eight-second expiry"); // 到期不能提升反应。
        awareness.ObserveVisible("other", Vector3.Zero, 30); // 目标切换会清空旧身份。
        Check(!awareness.IsPrimed("player") && awareness.MarkLost(30.1), "target switch fresh context"); // 新目标独立计时。
    }

    /// <summary>守点动作保留危险与恢复优先级，再发现仍遵守等级和原生准备门槛。</summary>
    private static void WatchDecisionAndReaction()
    {
        var policy = new DecisionPolicy();
        SkillProfile low = SkillProfile.Create(BotRole.Pmc, 1); // 低等级反应和瞄准等待更长。
        var input = new DecisionInput { HasClue = true, Visible = true, Reacted = true, CanMove = true }; // 初始为正常可见交战。
        Check(policy.Decide(input, low, 10, 0) == BehaviorState.Engage, "visible combat before loss"); // 先建立正常交战状态。
        input.Visible = false; input.Reacted = false; input.WatchLastSeen = true; // 转为失视守点。
        Check(policy.Decide(input, low, 10.1, 0) == BehaviorState.Observe, "short watch after loss"); // 不能直接奔向隐藏玩家。
        input.PlayerDanger = true; // 玩家再次命中时必须抢占守点。
        Check(policy.Decide(input, low, 10.2, 0) == BehaviorState.Evade, "danger interrupts watch"); // 保留避险优先。
        input.PlayerDanger = false; input.RecoveryRunning = true; // 已经开始的原生恢复仍优先。
        Check(policy.Decide(input, low, 10.3, 0) == BehaviorState.Recover, "recovery interrupts watch"); // 不取消换弹或治疗。
        policy.Reset(); input.RecoveryRunning = false; input.WatchLastSeen = true; // 独立复测守点到期。
        Check(policy.Decide(input, low, 11, 0) == BehaviorState.Observe, "watch resumes in fresh context"); // 可在新决策中短暂守点。
        input.WatchLastSeen = false; input.SoundOnly = true; // 守点结束后剩余合法线索进入调查。
        Check(policy.Decide(input, low, 12, 0) == BehaviorState.Investigate, "watch expiry resumes investigation"); // 不能永远原地观察。
        var gate = new ReactionGate();
        gate.Update("player", true, true, 20); // 真正重新可见且武器已就绪。
        Check(!gate.CanFire(low, 20.7, 0.8f) && gate.CanFire(low, 20.73, 0.8f), "low level remains delayed after repeek"); // 熟悉优势不是瞬发。
        Check(!gate.CanFire(low, 20.73) && gate.CanFire(low, 20.91), "default low level longer"); // 原有等级基线保持。
        SkillProfile high = SkillProfile.Create(BotRole.Pmc, 60); // 高等级仍按同一系数缩放。
        Check(gate.CanFire(high, 20.3, 0.8f) && !gate.CanFire(high, 20.3), "high level linear advantage retained"); // 缩放不改等级排序。
        gate.Update("player", false, true, 21); // 再次失视必须重置射击许可。
        Check(!gate.CanFire(high, 100, 0.8f), "lost sight cannot fire with repeek boost"); // 任何再发现优势都不能穿墙射击。
    }
}
