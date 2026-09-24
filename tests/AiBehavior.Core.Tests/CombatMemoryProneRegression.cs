using System.Numerics;
using AiBehavior.Core;

namespace AiBehavior.Core.Tests;

/// <summary>检查同层交战记忆和本人受击趴伏门槛，避免两条行为链相互放大。</summary>
internal static partial class Program
{
    /// <summary>持续同层近战保持有限搜索记忆，首次目击、跨楼层与远距仍用角色基础时长。</summary>
    private static void CombatMemorySameFloor()
    {
        SkillProfile scav = SkillProfile.Create(BotRole.Scav, 1); // 固定模板不随等级变化。
        SkillProfile lowPmc = SkillProfile.Create(BotRole.Pmc, 1); // 低等级仍应记住房间内的刚交战玩家。
        SkillProfile highPmc = SkillProfile.Create(BotRole.Pmc, 60); // 高等级仍保持线性成长优势。
        Near((float)CombatMemoryPolicy.VisionSeconds(BotRole.Scav, scav, Vector3.Zero, new Vector3(10, 0, 0), true), 18, "scav close combat memory"); // 近距同层 Scav 固定十八秒。
        Near((float)CombatMemoryPolicy.VisionSeconds(BotRole.Pmc, lowPmc, Vector3.Zero, new Vector3(10, 0, 0), true), 23, "low PMC close combat memory"); // 低等级 PMC 二十三秒。
        Near((float)CombatMemoryPolicy.VisionSeconds(BotRole.Pmc, highPmc, Vector3.Zero, new Vector3(10, 0, 0), true), 27, "high PMC close combat memory"); // 高等级 PMC 二十七秒。
        Near((float)CombatMemoryPolicy.VisionSeconds(BotRole.Pmc, lowPmc, Vector3.Zero, new Vector3(10, 0, 0), false), lowPmc.MemorySeconds, "single glance baseline"); // 一次短暂目击不延长记忆。
        Near((float)CombatMemoryPolicy.VisionSeconds(BotRole.Pmc, lowPmc, Vector3.Zero, new Vector3(10, 3, 0), true), lowPmc.MemorySeconds, "other floor baseline"); // 跨层不伪装成同层搜索。
        Near((float)CombatMemoryPolicy.VisionSeconds(BotRole.Pmc, lowPmc, Vector3.Zero, new Vector3(36, 0, 0), true), lowPmc.MemorySeconds, "long range baseline"); // 远距仍遵循原等级参数。
        Near((float)CombatMemoryPolicy.VisionSeconds(BotRole.Pmc, lowPmc, Vector3.Zero, new Vector3(35, 2.5f, 0), true), 23, "inclusive same-floor boundary"); // 边界上仍属于局部交战。
    }

    /// <summary>新脚步保留自己的估计位置和时间，却不能把先前较长的视觉期限压缩到八秒。</summary>
    private static void CombatMemorySoundDoesNotShorten()
    {
        var memory = new ThreatMemory(); // 使用真实记忆合并逻辑验证失视后线索寿命。
        Check(memory.Observe(new Observation("player", ObservationSource.Vision, new Vector3(2, 0, 0), 10, 33, 0), 10), "combat sight saved"); // 模拟低等级 PMC 的二十三秒近战记忆。
        Check(memory.Observe(new Observation("player", ObservationSource.Hearing, new Vector3(4, 0, 0), 11, 19, 4), 11), "new footstep saved"); // 玩家进入房间后脚步可更新搜索区域。
        Check(memory.TryGet("player", 19, out Observation heard) && heard.Source == ObservationSource.Hearing && heard.Position.X == 4 && heard.ExpiresAt == 33, "sound location without early forgetting"); // 新坐标不把目标寿命压短。
        Check(memory.TryGet("player", 32.99, out _), "combat memory remains before expiry"); // 读取不改变实际期限。
        Check(!memory.TryGet("player", 33, out _), "combat memory still expires"); // 准确到期后不可无限追踪。
    }

    /// <summary>区域候选查完后不提前删目标，守点在战斗资格或线索任一期限到达时结束。</summary>
    private static void CheckedCombatAreaHoldsUntilExpiry()
    {
        Check(!CombatMemoryPolicy.HoldCheckedArea(false, 12, 33, 33), "unfinished area continues search"); // 未查完仍按已有路线探索。
        Check(CombatMemoryPolicy.HoldCheckedArea(true, 12, 33, 33), "checked area keeps recent combat clue"); // 查完区域仍保留刚交战目标。
        Check(CombatMemoryPolicy.HoldCheckedArea(true, 32.99, 33, 33), "hold remains before expiry"); // 截止前仍守点。
        Check(!CombatMemoryPolicy.HoldCheckedArea(true, 33, 33, 33), "hold ends at combat expiry"); // 到期即可交还原生。
        Check(!CombatMemoryPolicy.HoldCheckedArea(true, 20, 33, 19), "expired clue cannot be held"); // 不覆盖目标线索本身的更短期限。
        Check(!CombatMemoryPolicy.HoldCheckedArea(true, double.NaN, 33, 33), "invalid time cannot hold"); // 非法时钟不生成永久守点。
        Check(!CombatMemoryPolicy.HoldCheckedArea(true, 20, double.PositiveInfinity, 33), "unbounded combat memory rejected"); // 非法期限不能永久接管。
    }

    /// <summary>本人命中证据有独立五秒期限，只有确认无掩体且丢失目标时才允许主动趴伏。</summary>
    private static void ProneRequiresPersonalHitAndLostSight()
    {
        var danger = new PlayerDanger(); // 近弹与本人中弹共用危险计时，但趴伏资格单独保存。
        Check(danger.Observe(true, Vector3.Zero, 10, 20), "near bullet accepted"); // 近弹只形成避险。
        Check(!danger.HasRecentPersonalHit(10.5), "near bullet cannot grant prone"); // 没有本人命中证据。
        Check(!TacticalActionPolicy.CanProneAfterHit(false, true, false, false, false, false), "near bullet cannot prone"); // 无掩体也不能凭近弹卧倒。
        Check(danger.Observe(true, Vector3.Zero, 11, 20, true), "personal hit accepted"); // 真正命中建立独立窗口。
        Check(danger.HasRecentPersonalHit(11), "personal hit grants temporary evidence"); // 命中瞬间可以准备后续动作。
        Check(!TacticalActionPolicy.CanProneAfterHit(true, true, false, false, true, false), "visible duel stays upright"); // 玩家仍在枪线内时优先交火。
        Check(!TacticalActionPolicy.CanProneAfterHit(true, false, false, false, false, false), "cover not ruled out"); // 掩体还在查询时不可提前趴伏。
        Check(!TacticalActionPolicy.CanProneAfterHit(true, true, true, false, false, false), "valid cover wins"); // 有可用掩体优先进入掩体。
        Check(!TacticalActionPolicy.CanProneAfterHit(true, true, false, true, false, false), "pending cover query wins"); // 尚在查询不等于已经证实失败。
        Check(!TacticalActionPolicy.CanProneAfterHit(true, true, false, false, false, true), "moving bot cannot prone"); // 撤离途中不打断路线。
        Check(TacticalActionPolicy.CanProneAfterHit(true, true, false, false, false, false), "hit no cover no visual permits prone"); // 三项必要条件齐全才可卧倒。
        Check(danger.Observe(true, Vector3.Zero, 15.5, 20), "later near bullet accepted"); // 新近弹可以更新危险，但不能续本人命中。
        Check(!danger.HasRecentPersonalHit(16), "near bullet does not renew hit evidence"); // 满五秒后的旧命中不能再次趴伏。
        danger.Clear(); // 离开玩家情境清除独立命中时间。
        Check(!danger.HasRecentPersonalHit(16), "clear removes hit evidence"); // 不跨战局保留。
    }

    /// <summary>外部行为在本层接管后新写卧姿才可于可见交火中撤销，原生原有卧姿和其他层不受影响。</summary>
    private static void VisibleDuelProneOwnership()
    {
        Check(TacticalActionPolicy.ShouldClearVisibleProne(true, true, true, true, false, true), "new prone during visible duel cleared"); // 覆盖非本模组入口造成的交火卧姿。
        Check(!TacticalActionPolicy.ShouldClearVisibleProne(true, true, true, true, true, true), "preexisting native prone retained"); // 接管前已有原生卧姿不强改。
        Check(!TacticalActionPolicy.ShouldClearVisibleProne(false, true, true, true, false, true), "native layer keeps control"); // 未持有资源时不写原生姿态。
        Check(!TacticalActionPolicy.ShouldClearVisibleProne(true, false, true, true, false, true), "another selected layer keeps control"); // BigBrain 抢占时不争夺姿态。
        Check(!TacticalActionPolicy.ShouldClearVisibleProne(true, true, false, true, false, true), "lost sight can stay prone"); // 合法失视避险不被清理。
        Check(!TacticalActionPolicy.ShouldClearVisibleProne(true, true, true, false, false, true), "AI target untouched"); // 仅本机玩家交火。
        Check(!TacticalActionPolicy.ShouldClearVisibleProne(true, true, true, true, false, false), "already standing remains stable"); // 热循环中不重复调用卧姿接口。
    }
}
