using System;
using System.Numerics;

namespace AiBehavior.Core;

/// <summary>保存已求解的导航折线，沿真实路线切分十二米路段，不再朝声源直线取中间点。</summary>
public sealed class SearchRoute
{
    public const int Capacity = 128;
    public const float StepLength = 12;
    private readonly Vector3[] _corners = new Vector3[Capacity];
    private int _count;
    private int _next;
    private Vector3 _head;
    private double _expires;
    public float Length { get; private set; }

    /// <summary>验证完整路径、投影起终点和有限绕路长度，成功后复制到固定缓存。</summary>
    public bool Load(ReadOnlySpan<Vector3> corners, Vector3 origin, Vector3 target, bool complete, double now, out string reason)
    {
        Clear(); // 失败不能保留上一条路线。
        reason = "path-incomplete"; // 部分路径不伪装成抵达目标。
        if (!complete) return false; // 避免在导航分区边界反复假推进。
        reason = "path-corners"; // 角点数量与坐标独立诊断。
        if (corners.Length < 2 || corners.Length > Capacity || !ThreatMemory.Finite(origin) || !ThreatMemory.Finite(target) || double.IsNaN(now) || double.IsInfinity(now)) return false; // 不能无限缓存或传递非法坐标。
        float length = 0; // 只扫描有界折线一次。
        for (int index = 0; index < corners.Length; index++) // 原始数组长度已验证。
        {
            if (!ThreatMemory.Finite(corners[index])) return false; // 拒绝非有限角点。
            if (index > 0) length += Vector3.Distance(corners[index - 1], corners[index]); // 计算实际绕行距离。
        }
        Length = length; // 失败日志也保留实际路线长度，不能只给出零值。
        reason = "path-source"; // 起点不能跨过断层或从远处导航岛开始。
        if (Vector3.DistanceSquared(origin, corners[0]) > 2.25f) return false; // 与本地起点采样半径一致。
        reason = "path-endpoint"; // 终点偏离与过长不再混为一种错误。
        if (Vector3.DistanceSquared(target, corners[corners.Length - 1]) > 2.25f) return false; // 目标是已投影的候选。
        reason = "path-length"; // 长路由完整目标距离决定，不把十二米路段当作整条路线。
        float limit = Math.Max(80, Math.Min(360, Vector3.Distance(origin, target) * 3 + 20)); // 近处也可绕过建筑，所有角色共用有限导航上限。
        if (length > limit || length < 0.1f) return false; // 拒绝超长和退化路线。
        corners.CopyTo(_corners); // 只持有值坐标，不引用游戏对象。
        _count = corners.Length; // 标记缓存有效。
        _next = 1; // 第零点作为第一段起点。
        _head = corners[0]; // 后续切分都沿这条折线。
        _expires = now + 8; // 定期重新验证动态导航，读取不会续期。
        Length = length; // 诊断保留完整路径长度。
        reason = "ok"; // 成功后才允许提交动作。
        return true;
    }

    /// <summary>从缓存取下一段；偏离、到期或容量不足时拒绝，不跨墙连接新的起点。</summary>
    public bool Take(Vector3 origin, double now, Span<Vector3> output, out int count, out bool final)
    {
        count = 0; // 失败不暴露旧段。
        final = false; // 缺路不等于已经到达。
        if (_count < 2 || _next >= _count || output.Length < Capacity + 2 || !ThreatMemory.Finite(origin) || double.IsNaN(now) || double.IsInfinity(now) || now >= _expires || now < _expires - 8 || Vector3.DistanceSquared(origin, _head) > 2.25f) return false; // 调用方重新申请共享查询。
        output[count++] = origin; // 原生移动器从实际位置开始。
        float remaining = StepLength; // 包含连接上次段终点的距离。
        float join = Vector3.Distance(origin, _head); // 容忍原生到达半径内的正常偏差。
        if (join > 0.01f) { output[count++] = _head; remaining -= join; } // 先回到已验证折线，不能抄近道越过转角。
        while (_next < _count && remaining > 0.001f) // 每次最多遍历固定一百二十八个角点。
        {
            Vector3 next = _corners[_next]; // 只使用已经求解的路径。
            float distance = Vector3.Distance(_head, next); // 计算当前边剩余长度。
            if (distance < 0.001f) { _head = next; _next++; continue; } // 重复角点不消耗步长且必须推进游标。
            if (distance <= remaining) // 整条边能够放进本段。
            {
                output[count++] = next; // 保留每个转角，不能用终点连线替代。
                _head = next; // 下一条边从当前末端开始。
                _next++; // 该边只消费一次。
                remaining -= distance; // 按路径长度扣减。
            }
            else // 长边沿边插值，保持在已验证折线上。
            {
                _head += (next - _head) * (remaining / distance); // 截取剩余步长。
                output[count++] = _head; // 当前段以插值点结束。
                remaining = 0; // 同一调用不超出十二米。
            }
        }
        final = _next >= _count; // 只有消费完整路线才标记末段。
        return count >= 2; // 原生接口不能接受单点移动。
    }

    /// <summary>路径失效、控制交接或候选变化时解除缓存。</summary>
    public void Clear()
    {
        _count = _next = 0;
        _expires = Length = 0;
    }
}

/// <summary>三个搜索候选分别记录到达与失败，只允许一次有期限的失败重试轮。</summary>
public sealed class SearchProgress
{
    private int _reached;
    private int _failed;
    private int _pass;
    public int Index { get; private set; }
    public int Reached { get; private set; }
    public int Failed { get; private set; }

    /// <summary>新声源区域开始独立的一轮搜索，不从旧区域继承完成标记。</summary>
    public void Reset()
    {
        _reached = _failed = _pass = Index = Reached = Failed = 0;
    }

    /// <summary>确认实际到达或连续失败后推进候选，失败不能计作搜索完成。</summary>
    public void CompletePoint(bool reached)
    {
        if (Index >= 3) return; // 重复回调不重复记数。
        int bit = 1 << Index; // 三个候选共用固定掩码。
        if (reached) // 只有实际到达才记为完成。
        {
            if ((_reached & bit) == 0) Reached++; // 相同位置只统计一次。
            _reached |= bit; // 重试轮跳过已经到达的位置。
            if ((_failed & bit) != 0) { _failed &= ~bit; Failed--; } // 后续成功可以解除该点失败。
        }
        else if ((_failed & bit) == 0) { _failed |= bit; Failed++; } // 多次失败仍属于同一候选。
        Index++; // 下一次只处理一个候选。
        while (Index < 3 && (_reached & (1 << Index)) != 0) Index++; // 重试轮不再走已到达点。
    }

    /// <summary>线索还剩至少八秒时，允许一次冷却后的替代尝试，不刷新线索寿命。</summary>
    public bool Retry(double now, double expires)
    {
        if (Index < 3 || Failed == 0 || _pass != 0 || double.IsNaN(now) || double.IsInfinity(now) || double.IsNaN(expires) || double.IsInfinity(expires) || expires - now < 8) return false; // 有界重试，避免永久追击。
        _pass++; // 同一区域只有一次额外机会。
        Index = 0; // 重新检查未到达候选。
        while (Index < 3 && (_reached & (1 << Index)) != 0) Index++; // 已到达点不耗费新查询。
        return Index < 3;
    }
}
