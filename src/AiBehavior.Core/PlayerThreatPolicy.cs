using System;
using System.Numerics;

namespace AiBehavior.Core;

public enum GunshotBand { Ignore, Close, Search }

/// <summary>真人来源、距离分段和有限推进的纯逻辑规则。</summary>
public static class PlayerThreatPolicy
{
    /// <summary>仅本机真人玩家属于当前单人版本的增强范围，未知对象不能冒充玩家。</summary>
    public static bool IsLocalHuman(bool known, bool isAi, bool isYourPlayer)
    {
        return known && !isAi && isYourPlayer;
    }

    /// <summary>枪声只在确实听到时触发，三十米边界归近距，一百二十米边界归搜索。</summary>
    public static GunshotBand ClassifyGunshot(bool localHuman, bool heard, float distance)
    {
        if (!localHuman || !heard || float.IsNaN(distance) || distance < 0 || distance > 120) return GunshotBand.Ignore; // 未听见和远处枪声不唤醒新增逻辑。
        return distance <= 30 ? GunshotBand.Close : GunshotBand.Search; // 近距统一快速警戒，中距只形成估计区域。
    }

    /// <summary>远距定位误差连续增长，能力系数来自固定角色或 PMC 等级快照。</summary>
    public static float GunshotError(in SkillProfile skill, float distance)
    {
        if (distance <= 30) return 0; // 近距记住声音发生位置，但不授予视觉或实时追踪。
        float factor = 0.5f + 1.5f * Math.Min(1, (distance - 30) / 90); // 三十至一百二十米从半倍增长到两倍。
        return skill.HearingError * factor;
    }

    /// <summary>远处枪声调查使用独立的有限寿命，允许分段接近而不通过读取续期。</summary>
    public static double SearchLifetime(float distance)
    {
        return Math.Max(12, Math.Min(45, distance / 3 + 6));
    }

    /// <summary>混战时普通 AI 目标交给原生，玩家造成的短期紧急危险可以暂时优先。</summary>
    public static bool ShouldManage(bool active, bool playerAlive, bool otherTarget, bool playerTarget, bool playerClue, bool playerDanger)
    {
        return active && playerAlive && (playerDanger || (!otherTarget && (playerTarget || playerClue)));
    }

    /// <summary>每次只向快照方向移动十二米，避免远声源发起一次超长导航请求。</summary>
    public static Vector3 NextSearchStep(Vector3 origin, Vector3 target)
    {
        Vector3 delta = target - origin; // 只能传入事件快照或合法视觉位置。
        float length = delta.Length(); // 步长限制不会随 PMC 等级扩大。
        return length <= 12 ? target : origin + delta * (12 / length);
    }

    /// <summary>计算人物与实际弹道线段的最近距离，线段终点必须裁剪到真实碰撞位置。</summary>
    public static float SegmentDistanceSquared(Vector3 point, Vector3 from, Vector3 to)
    {
        Vector3 delta = to - from; // 不把射线延长到墙后或子弹尚未飞到的位置。
        float lengthSquared = delta.LengthSquared(); // 退化弹着点同样可以表达附近危险。
        float t = lengthSquared < 0.0001f ? 0 : Math.Max(0, Math.Min(1, Vector3.Dot(point - from, delta) / lengthSquared)); // 投影限制在线段内。
        return Vector3.DistanceSquared(point, from + delta * t); // 无物理 API、无临时容器。
    }
}

/// <summary>只接收已核验真人来源的危险，AI 伤害和重复广播不会刷新五秒计时。</summary>
public sealed class PlayerDanger
{
    public double LastDangerAt { get; private set; } = double.NegativeInfinity;
    public double SearchUntil { get; private set; }
    public Vector3 Position { get; private set; }
    public double CloseAlertUntil { get; private set; }

    /// <summary>记录玩家受击、近弹或一次友方告警，保存带误差且不再跟随射手的快照。</summary>
    public bool Observe(bool localHuman, Vector3 position, double now, double searchSeconds)
    {
        if (!localHuman || !ThreatMemory.Finite(position) || double.IsNaN(now) || double.IsInfinity(now) || double.IsNaN(searchSeconds) || double.IsInfinity(searchSeconds) || now < LastDangerAt) return false; // 未知来源、无效寿命与倒序事件都不能延长压制。
        LastDangerAt = now; // 新的真实危险重置五秒安全窗口。
        SearchUntil = now + Math.Max(6, Math.Min(45, searchSeconds)); // 危险结束后仍有有限的调查机会。
        Position = position; // 不保存敌人 Transform 或位置回调。
        return true;
    }

    /// <summary>近距枪声短期解除等级准备门槛，原生瞄准和直接视线仍由客户端检查。</summary>
    public void AlertClose(double now)
    {
        CloseAlertUntil = now + 2;
    }

    /// <summary>只有最近五秒内的新危险才维持紧急避险。</summary>
    public bool IsActive(double now)
    {
        return now >= LastDangerAt && now < LastDangerAt + 5;
    }

    /// <summary>安静满五秒后允许对最后危险区域做有限推进。</summary>
    public bool CanAdvance(double now)
    {
        return now >= LastDangerAt + 5 && now < SearchUntil;
    }

    /// <summary>搜索完成或目标失效后清除危险推进意图。</summary>
    public void Clear()
    {
        LastDangerAt = double.NegativeInfinity;
        SearchUntil = CloseAlertUntil = 0;
        Position = default;
    }
}
