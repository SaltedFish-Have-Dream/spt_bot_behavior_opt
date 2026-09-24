using System;
using System.Numerics;

namespace AiBehavior.Core;

/// <summary>只根据已有玩家线索计算短距离候选，不读取游戏对象或申请额外导航额度。</summary>
public static class AdaptiveMovement
{
    /// <summary>掩体和卧姿都不可用时，最多尝试两处远离威胁的短距离位置。</summary>
    public static bool TryEscapePoint(Vector3 origin, Vector3 threat, int attempt, out Vector3 point)
    {
        point = default; // 无效方向不能进入导航队列。
        if (!ThreatMemory.Finite(origin) || !ThreatMemory.Finite(threat) || attempt < 0 || attempt > 1) return false; // 拒绝无界轮次和非法快照。
        Vector3 away = new(origin.X - threat.X, 0, origin.Z - threat.Z); // 高度差不改变水平撤离方向。
        if (away.LengthSquared() < 1) return false; // 重合或过近时不猜测随机方向。
        away = Vector3.Normalize(away); // 只对有限且足够长的向量归一化。
        Vector3 side = new(-away.Z, 0, away.X); // 第二次尝试与第一次错开，不重复撞同一堵墙。
        point = origin + away * (attempt == 0 ? 5 : 4) + side * (attempt == 0 ? 0 : 3); // 两次都增加与威胁的平面距离。
        return ThreatMemory.Finite(point); // 极端坐标溢出不能进入 Unity API。
    }

    /// <summary>枪线连续受阻时，只尝试目标方向左右两侧的近处换位。</summary>
    public static bool TrySideStep(Vector3 origin, Vector3 target, int attempt, out Vector3 point)
    {
        point = default; // 无效目标不生成移动请求。
        if (!ThreatMemory.Finite(origin) || !ThreatMemory.Finite(target) || attempt < 0 || attempt > 1) return false; // 每个目标最多两侧。
        Vector3 direction = new(target.X - origin.X, 0, target.Z - origin.Z); // 不能用楼层差推断平面侧移。
        if (direction.LengthSquared() < 4) return false; // 贴脸交战不横移穿过玩家。
        direction = Vector3.Normalize(direction); // 已核对长度后再计算正交方向。
        Vector3 side = new(-direction.Z, 0, direction.X); // 固定侧移避免逐次随机重规划。
        point = origin + side * (attempt == 0 ? 3 : -3); // 两次最多各三米，实际可达性留给共享导航查询。
        return ThreatMemory.Finite(point); // 溢出候选不能进入原生移动器。
    }

    /// <summary>贴脸且已亲眼看见玩家时提出三米反向候选，实际可达性仍由完整路径验证。</summary>
    public static bool TryBackStep(Vector3 origin, Vector3 target, out Vector3 point)
    {
        point = default; // 无效输入不能沿用上次目的地。
        if (!ThreatMemory.Finite(origin) || !ThreatMemory.Finite(target)) return false; // 非法坐标不能进入导航。
        if (Math.Abs(target.Y - origin.Y) > 2.5f) return false; // 不把上下楼层的水平接近误判为贴脸交战。
        Vector3 away = new(origin.X - target.X, 0, origin.Z - target.Z); // 只按水平距离后撤，不把楼层差变成移动方向。
        float distanceSquared = away.LengthSquared(); // 一次平方距离用于贴脸和最大触发范围。
        if (distanceSquared < 1 || distanceSquared >= 36) return false; // 重叠目标方向不可信，六米以上交给原有侧移。
        point = origin + Vector3.Normalize(away) * 3; // 固定短距离，不生成多候选或逐帧随机点。
        return ThreatMemory.Finite(point); // 极端坐标溢出时拒绝请求。
    }
}

/// <summary>同一段持续危险中限制无掩体撤离次数，不让连续近弹无限产生路径请求。</summary>
public sealed class EscapeFallback
{
    private int _attempts;
    private double _nextAt;

    /// <summary>只有危险窗口真正结束再重新开始时，才恢复两次短距离候选额度。</summary>
    public void StartDanger()
    {
        _attempts = 0;
        _nextAt = 0;
    }

    /// <summary>只有掩体已验证失败、卧姿确实受阻并持有控制时才产生候选。</summary>
    public bool TryPlan(bool allowed, Vector3 origin, Vector3 threat, double now, out Vector3 point)
    {
        point = default; // 未获资格时不泄漏旧位置。
        if (!allowed || double.IsNaN(now) || double.IsInfinity(now) || now < 0 || now < _nextAt || _attempts >= 2) return false; // 冷却与硬上限共同约束。
        if (!AdaptiveMovement.TryEscapePoint(origin, threat, _attempts, out point)) return false; // 坐标不能用时不消耗名额。
        _attempts++; // 即使后续导航失败也不无限重试同方向。
        _nextAt = now + 3; // 单 Bot 至少三秒才会请求另一侧。
        return true;
    }
}

/// <summary>连续世界障碍拒绝后最多申请两次换位，不把单发失误当作重规划理由。</summary>
public sealed class BlockedShotFeedback
{
    private string? _identity;
    private int _consecutive;
    private int _attempts;
    private double _lastAt = double.NegativeInfinity;
    private double _nextAt;

    /// <summary>相同玩家五秒内三次真实枪线受阻，且动作可移动时才产生一次换位机会。</summary>
    public bool Observe(string? identity, bool allowed, double now, out int attempt)
    {
        attempt = -1; // 返回假时不能使用旧的换位序号。
        if (!allowed || string.IsNullOrEmpty(identity) || double.IsNaN(now) || double.IsInfinity(now) || now < 0 || now < _lastAt) return false; // AI 目标和过期事件不触发。
        if (_identity != identity) { _identity = identity; _attempts = _consecutive = 0; _nextAt = 0; } // 换目标后允许新的有限尝试。
        _consecutive = now - _lastAt > 5 ? 1 : Math.Min(3, _consecutive + 1); // 隔很久的零星阻挡不能拼成连续失败。
        _lastAt = now; // 时间基准只由真实世界阻挡更新。
        if (_consecutive < 3 || _attempts >= 2 || now < _nextAt) return false; // 两侧都失败后停止主动换位。
        attempt = _attempts++; // 输出确定的左/右位置，不随机扩张。
        _consecutive = 0; // 下一次必须重新观察三次真实受阻。
        _nextAt = now + 8; // 状态保持期间不能频繁抢共享导航额度。
        return true;
    }

    /// <summary>成功获得安全枪线时清除连续失败，但保留已消耗的换位额度。</summary>
    public void ClearStreak()
    {
        _consecutive = 0;
    }

    /// <summary>主动交战侧移成功排队后，挡枪换位至少等待八秒并重新累计真实阻挡。</summary>
    public void CooldownAfterExternal(string? identity, double now)
    {
        if (string.IsNullOrEmpty(identity) || double.IsNaN(now) || double.IsInfinity(now) || now < 0) return; // 无效事件不能冻结合法换位。
        if (_identity != identity) { _identity = identity; _attempts = 0; } // 新目标使用自己的挡枪额度。
        _consecutive = 0; // 之前零星阻挡不能与移动后的枪线拼接。
        _lastAt = now; // 后续连续阻挡从实际移动开始重算。
        _nextAt = Math.Max(_nextAt, now + 8); // 不允许紧接着重复提交另一条侧移路线。
    }

    /// <summary>玩家情境退出后清除目标及额度，下一次独立接敌重新开始。</summary>
    public void Clear()
    {
        _identity = null;
        _consecutive = _attempts = 0;
        _lastAt = double.NegativeInfinity;
        _nextAt = 0;
    }
}

/// <summary>记住最近一次重复不可达的搜索点，短期过滤同一区域的新枪声候选。</summary>
public sealed class SearchFailureMemory
{
    private readonly Vector3[] _points = new Vector3[3];
    private readonly double[] _expires = new double[3];
    private int _next;

    /// <summary>只在已完成两次失败时记录位置，不把一次导航波动当作永久断路。</summary>
    public void Record(Vector3 point, double now)
    {
        if (!ThreatMemory.Finite(point) || double.IsNaN(now) || double.IsInfinity(now) || now < 0) return; // 非法坐标不污染后续搜索。
        _points[_next] = point; // 只保存三个观察点的值，不持有游戏对象。
        _expires[_next] = now + 15; // 动态门变化后允许再次调查。
        _next = (_next + 1) % _points.Length; // 固定环形容量避免同一区域失败无限增长。
    }

    /// <summary>十五秒内跳过原点五米邻域，其余区域和过期后仍可重新验证。</summary>
    public bool Rejects(Vector3 point, double now)
    {
        if (!ThreatMemory.Finite(point) || double.IsNaN(now) || double.IsInfinity(now) || now < 0) return false; // 无效数据不能命中旧记录。
        for (int index = 0; index < _points.Length; index++) // 单次最多比较三个固定记录。
            if (_expires[index] > now && now >= _expires[index] - 15 && Vector3.DistanceSquared(point, _points[index]) <= 25) return true; // 平方距离不增加 NavMesh 调用。
        return false; // 过期或区域不同可以再次查询。
    }

    /// <summary>玩家死亡或 Bot 销毁时丢弃临时不可达记录。</summary>
    public void Clear()
    {
        Array.Clear(_expires, 0, _expires.Length); // 不保留旧地图上的失败时效。
        Array.Clear(_points, 0, _points.Length); // 与 Bot 生命周期同步释放位置快照。
        _next = 0; // 下次搜索从首槽开始。
    }
}
