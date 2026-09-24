using System.Numerics;
using AiBehavior.Core;

namespace AiBehavior.Core.Tests;

/// <summary>验证主动侧移的视觉来源、时间、距离和次数均有硬边界。</summary>
internal static partial class Program
{
    /// <summary>稳定近距交战后才提出一次三米候选，未获资格时不消耗机会。</summary>
    private static void FootworkStableEngagement()
    {
        var footwork = new EngagementFootwork(0);
        Vector3 bot = Vector3.Zero; // 当前 Bot 所在位置。
        Vector3 player = new(0, 0, 12); // 只使用真实视觉快照。
        Check(!footwork.TryPlan(true, bot, player, 10, out _), "no vision no footwork"); // 无个人视觉不能计划。
        footwork.ObserveVisible("player", 10); // 首次真实看见玩家。
        Check(!footwork.TryPlan(true, bot, player, 11.49, out _), "engagement must settle"); // 未满一点五秒仍原位交战。
        footwork.ObserveVisible("player", 11.5); // 验证此刻视觉仍新鲜。
        Check(!footwork.TryPlan(false, bot, player, 11.5, out _), "safety block does not consume slot"); // 避险、恢复或有掩体时不侧移。
        Check(footwork.TryPlan(true, bot, player, 11.5, out Vector3 point) && Vector3.Distance(bot, point) == 3, "single short side step"); // 候选严格限制为三米。
        footwork.MarkRequested(); // 模拟导航请求已成功排队。
        footwork.ObserveVisible("player", 11.6); // 连续交战继续保持视觉。
        Check(!footwork.TryPlan(true, bot, player, 11.6, out _), "one proactive step per engagement"); // 不在每帧重算路径。
    }

    /// <summary>贴脸、远距和短暂失视不能造成无界或错误范围的换位。</summary>
    private static void FootworkDistanceAndSightBounds()
    {
        var footwork = new EngagementFootwork(1);
        Vector3 bot = Vector3.Zero;
        footwork.ObserveVisible("player", 10); // 为边界测试创建合法交战窗口。
        footwork.ObserveVisible("player", 11.5); // 满足最短交战时间。
        Check(!footwork.TryPlan(true, bot, new Vector3(0, 0, 5.99f), 11.5, out _), "point blank excluded"); // 贴脸时不横穿玩家。
        Check(!footwork.TryPlan(true, bot, new Vector3(0, 0, 25.01f), 11.5, out _), "long range excluded"); // 远距保持原有交火选择。
        Check(!footwork.TryPlan(true, bot, new Vector3(0, 0, 12), 12, out _), "stale sight excluded"); // 0.35 秒外旧快照不触发换位。
        footwork.ObserveVisible("player", 12); // 视线更新仍属于同一交战。
        Check(footwork.TryPlan(true, bot, new Vector3(0, 0, 6), 12, out Vector3 left) && left.X > 0, "near boundary and alternate side"); // 六米边界及另一侧候选有效。
        footwork.MarkRequested(); // 本轮名额用完。
        footwork.ObserveVisible("player", 17.9); // 五点九秒失视不能刷新名额。
        Check(!footwork.TryPlan(true, bot, new Vector3(0, 0, 12), 17.9, out _), "brief loss keeps used slot"); // 避免视觉抖动反复侧移。
        footwork.ObserveVisible("player", 24); // 超过六秒才是新交战窗口。
        Check(!footwork.TryPlan(true, bot, new Vector3(0, 0, 12), 24, out _), "new engagement still waits"); // 新窗口也不能瞬移。
        footwork.ObserveVisible("player", 25.5);
        Check(footwork.TryPlan(true, bot, new Vector3(0, 0, 25), 25.5, out _), "far boundary remains eligible"); // 二十五米边界有效。
    }

    /// <summary>挡枪换位与主动侧移共用窗口名额，换目标和清理恢复独立机会。</summary>
    private static void FootworkOtherRepositionAndLifecycle()
    {
        var footwork = new EngagementFootwork(0);
        Vector3 bot = Vector3.Zero;
        Vector3 player = new(0, 0, 15);
        footwork.ObserveVisible("player", 10); // 首次目视交战。
        footwork.ObserveVisible("player", 12); // 到达可考虑侧移的时刻。
        footwork.MarkOtherReposition(); // 之前挡枪已经提出一次换位。
        Check(!footwork.TryPlan(true, bot, player, 12, out _), "blocked shot step prevents duplicate"); // 不紧接着再次主动移动。
        footwork.ObserveVisible("other", 13); // 新目标不能继承旧名额。
        Check(!footwork.TryPlan(true, bot, player, 13, out _), "target switch restarts hold"); // 换目标需要重新稳定交战。
        footwork.ObserveVisible("other", 14.5);
        Check(footwork.TryPlan(true, bot, player, 14.5, out _), "new target gets bounded slot"); // 新目标可独立侧移。
        footwork.Clear(); // 离开玩家情境或 Bot 销毁。
        Check(!footwork.TryPlan(true, bot, player, 20, out _), "clear removes stale target"); // 不跨上下文复用视觉。
    }

    /// <summary>贴脸后撤需要稳定的个人视觉，且目标不能重叠或超出短距边界。</summary>
    private static void CloseRangeRetreatBoundaries()
    {
        var footwork = new EngagementFootwork(0);
        Vector3 bot = Vector3.Zero; // 单位测试使用当前 Bot 位置快照。
        Vector3 player = new(0, 0, 3); // 玩家正前方三米，合法后撤应朝负 Z。
        footwork.ObserveVisible("player", 10); // 建立个人真实视觉窗口。
        Check(!footwork.TryPlanCloseRetreat(true, bot, player, 10.49, out _), "close-range hold required"); // 短暂视觉闪现不能瞬间后撤。
        footwork.ObserveVisible("player", 10.5); // 再次真实看见玩家。
        Check(!footwork.TryPlanCloseRetreat(false, bot, player, 10.5, out _), "danger or recovery blocks retreat"); // 安全前置失败不消费名额。
        Check(footwork.TryPlanCloseRetreat(true, bot, player, 10.5, out Vector3 point) && point == new Vector3(0, 0, -3), "short retreat moves away"); // 候选离玩家更远且只移动三米。
        Check(!footwork.TryPlanCloseRetreat(true, bot, new Vector3(0, 0, 0.99f), 10.5, out _), "overlap direction rejected"); // 一米内方向不稳定。
        Check(!footwork.TryPlanCloseRetreat(true, bot, new Vector3(0, 0, 6), 10.5, out _), "six meters uses side step"); // 与六米以上侧移边界互斥。
        Check(!footwork.TryPlanCloseRetreat(true, bot, new Vector3(0, 3, 3), 10.5, out _), "different floor is not face-to-face"); // 楼层高度超过边界时不后撤。
        Check(!footwork.TryPlanCloseRetreat(true, bot, player, 10.9, out _), "stale sight rejects retreat"); // 旧视觉不生成新路线。
        Check(!AdaptiveMovement.TryBackStep(bot, new Vector3(float.NaN, 0, 3), out _), "invalid point never reaches navmesh"); // 非法位置不能进入 Unity 导航。
    }

    /// <summary>贴脸后撤与六米以上侧移共用一次机会，失视和换目标遵守既有窗口。</summary>
    private static void CloseRangeRetreatSharedSlot()
    {
        var footwork = new EngagementFootwork(1);
        Vector3 bot = Vector3.Zero;
        footwork.ObserveVisible("player", 10); // 观察窗口开始。
        footwork.ObserveVisible("player", 10.5); // 满足后撤稳定时间。
        Check(footwork.TryPlanCloseRetreat(true, bot, new Vector3(3, 0, 0), 10.5, out Vector3 point) && point.X == -3, "close retreat direction follows sight"); // 朝远离玩家的方向移动。
        footwork.MarkRequested(); // 模拟路线已成功排队。
        footwork.ObserveVisible("player", 12); // 后续距离改变但仍是同一持续交战。
        Check(!footwork.TryPlan(true, bot, new Vector3(0, 0, 12), 12, out _), "retreat consumes side-step slot"); // 不能后撤后再立即横跳。
        footwork.ObserveVisible("player", 18.1); // 失视超过六秒建立新窗口。
        footwork.ObserveVisible("player", 19.6); // 新窗口达到侧移稳定时间。
        Check(footwork.TryPlan(true, bot, new Vector3(0, 0, 12), 19.6, out _), "new encounter gets one movement"); // 独立交战仍有一次机会。
        footwork.MarkOtherReposition(); // 挡枪侧移占用新窗口。
        Check(!footwork.TryPlanCloseRetreat(true, bot, new Vector3(0, 0, 3), 19.6, out _), "blocked-shot movement consumes retreat slot"); // 挡枪换位后也不继续后撤。
    }
}
