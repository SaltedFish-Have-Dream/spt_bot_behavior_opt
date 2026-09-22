using System;
using AiBehavior.Core;
using UnityEngine;
using UnityEngine.AI;

namespace AiBehavior.Client;

/// <summary>掩体和移动查询分帧推进；每一步的原生 API 调用都有独立预算。</summary>
internal sealed class BotQuery
{
    private readonly BotAgent _agent;
    private readonly Collider[] _colliders = new Collider[32];
    private readonly Vector3[] _candidates = new Vector3[6];
    private readonly NavMeshPath _path = new();
    private int _stage;
    private int _count;
    private int _candidate;
    private Vector3 _point;
    private string _lastFailure = "no-candidates";

    /// <summary>每个 Bot 复用固定工作区，禁止因地图密度无限扩容。</summary>
    internal BotQuery(BotAgent agent)
    {
        _agent = agent;
    }

    /// <summary>新动作请求重置有限状态，普通移动不需要扫描碰撞体。</summary>
    internal void Begin(in WorkRequest request)
    {
        _stage = request.Kind == QueryKind.Cover ? 0 : 1;
        _count = request.Kind == QueryKind.Cover ? 0 : 1;
        _candidate = 0;
        _lastFailure = "no-candidates";
        _candidates[0] = GameAdapter.Position(request.Position);
    }

    /// <summary>最多进行一次昂贵调用，返回 false 表示需要下一帧继续。</summary>
    internal bool Step(in WorkRequest request, double now, int frame)
    {
        RaidRuntime runtime = _agent.Runtime; // 所有 Bot 使用同一组额度。
        if (!_agent.Accepts(request)) return true; // 旧动作和已销毁对象不再处理。
        if (_stage == 0) // 首先在当前位置附近收集最多六个候选。
        {
            if (!runtime.Overlaps.TryTake(now, frame)) return false; // 额度不足时保持原截止时间。
            Gather(GameAdapter.Position(request.Threat)); // 内部仅一次 NonAlloc 重叠查询。
            _stage = 1; // 下一步单独领取导航采样额度。
            return false;
        }
        if (_candidate >= Math.Min(_count, request.Kind == QueryKind.Cover ? 2 : 1)) return Fail(now); // 最多验证两个掩体，不追求全局最优。
        if (_stage == 1) // 导航投影避免把目标设在墙内或悬空位置。
        {
            if (!runtime.Samples.TryTake(now, frame)) return false; // 位置采样也计入全局预算。
            if (!NavMesh.SamplePosition(_candidates[_candidate], out NavMeshHit navHit, 1.5f, NavMesh.AllAreas)) return NextCandidate(now, request, "nav-sample"); // 失败后只尝试下一个有限候选。
            _point = navHit.position; // 保存有效导航坐标。
            _stage = request.Kind == QueryKind.Cover ? 2 : 3; // 普通移动不增加掩体射线。
            return false;
        }
        if (_stage == 2) // 掩体必须实际阻挡已知威胁方向。
        {
            if (!runtime.Rays.TryTake(now, frame)) return false; // 新连射在动作阶段优先使用额度。
            Vector3 threat = GameAdapter.Position(request.Threat) + Vector3.up * 1.4f; // 只读取请求中固化的观察位置。
            runtime.RayCalls++; // 单独记录实际物理 API 调用。
            bool blocked = Physics.Linecast(_point + Vector3.up, threat, LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore); // 对固定姿态高度验证遮挡。
            if (!blocked) return NextCandidate(now, request, "no-occlusion"); // 没有遮挡的位置不能称为掩体。
            _stage = 3; // 路径检查放在后续帧执行。
            return false;
        }
        if (!runtime.Paths.TryTake(now, frame)) return false; // 每帧最多开始一次完整路径计算。
        _path.ClearCorners(); // 重用路径对象，不沿用上次结果。
        if (!NavMesh.CalculatePath(_agent.Owner.Position, _point, NavMesh.AllAreas, _path) || _path.status != NavMeshPathStatus.PathComplete)
            return NextCandidate(now, request, "path-incomplete"); // 部分路径也按失败处理。
        Vector3[] corners = _path.corners; // 仅成功路径创建角点副本，原生移动器会持有它。
        if (corners.Length < 2 || corners.Length > 128) return NextCandidate(now, request, "path-corners"); // 拒绝退化或异常复杂的路线。
        float length = 0; // 限制近处目标绕行过远的情况。
        for (int index = 1; index < corners.Length; index++) length += Vector3.Distance(corners[index - 1], corners[index]); // 只遍历这条有界路径。
        float limit = request.Kind == QueryKind.Cover ? 40 : _agent.Role == BotRole.Scav ? 35 : 80; // 各类移动有明确距离上限。
        if (length > limit || (corners[corners.Length - 1] - _point).sqrMagnitude > 2.25f) return NextCandidate(now, request, "path-bounds"); // 不能把远离目标的投影当成成功。
        _agent.QuerySucceeded(request, _point, corners, now); // 写入前再次由 Agent 核对动作代次。
        return true;
    }

    /// <summary>复用原生当前掩体，并从一次局部查询中粗选最近的候选。</summary>
    private void Gather(Vector3 threat)
    {
        _count = 0; // 每次请求清空候选计数。
        Vector3 origin = _agent.Owner.Position; // 所有候选限制在当前位置附近。
        CustomNavigationPoint? native = _agent.Owner.Memory.CurCustomCoverPoint; // 只读取已有位置，不触发原生全图搜索。
        if (native != null) AddCandidate(native.Position, origin); // 当前原生掩体也必须经过后续遮挡和路径验证。
        int count = Physics.OverlapSphereNonAlloc(origin, 12f, _colliders, LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore); // 结果上限三十二，满载不扩容。
        for (int index = 0; index < count; index++) // 对固定缓冲进行便宜的边界筛选。
        {
            Collider collider = _colliders[index]; // 只在本次收集期间持有场景对象。
            if (collider == null) continue; // 动态销毁时跳过失效结果。
            Bounds bounds = collider.bounds; // 获取粗略几何范围。
            if (bounds.size.y < 1 || bounds.size.x > 30 || bounds.size.z > 30) continue; // 排除地板和巨大建筑整体碰撞体。
            Vector3 away = bounds.center - threat; // 按已知威胁方向选择遮挡物背面。
            away.y = 0; // 导航候选保持水平偏移。
            if (away.sqrMagnitude < 0.01f) continue; // 不对零方向进行归一化。
            away.Normalize(); // 计算有限的背面偏移方向。
            float extent = Mathf.Abs(away.x) * bounds.extents.x + Mathf.Abs(away.z) * bounds.extents.z; // 估计该方向的包围盒半径。
            Vector3 point = bounds.center + away * (extent + 0.7f); // 留出 Bot 身体与墙面的距离。
            point.y = origin.y; // 最终高度由后续 NavMesh 投影确认。
            AddCandidate(point, origin); // 维护最多六个近处候选。
        }
        Array.Clear(_colliders, 0, _colliders.Length); // 不让缓存跨查询强引用场景碰撞体。
    }

    /// <summary>小数组插入排序，稳定保留六个最近且不重复的位置。</summary>
    private void AddCandidate(Vector3 point, Vector3 origin)
    {
        float distance = (point - origin).sqrMagnitude; // 粗选使用平方距离，不计算路线。
        if (distance < 0.64f || distance > 225 || !ThreatMemory.Finite(GameAdapter.Snapshot(point))) return; // 排除原地和越界候选。
        int slot = _count; // 新候选默认放在末尾。
        for (int index = 0; index < _count; index++) // 固定最多六项。
        {
            if ((_candidates[index] - point).sqrMagnitude < 1) return; // 重叠碰撞体不生成重复候选。
            if (slot == _count && distance < (_candidates[index] - origin).sqrMagnitude) slot = index; // 记录首次适合的排序位置。
        }
        if (slot >= _candidates.Length) return; // 数组满且更远时直接丢弃。
        int last = Math.Min(_count, _candidates.Length - 1); // 不访问固定缓冲之外的位置。
        for (int index = last; index > slot; index--) _candidates[index] = _candidates[index - 1]; // 为更近候选腾出一个位置。
        _candidates[slot] = point; // 插入候选值。
        _count = Math.Min(_candidates.Length, _count + 1); // 保持容量上限。
    }

    /// <summary>一个候选失败后转向下一个，耗尽候选时结束请求。</summary>
    private bool NextCandidate(double now, in WorkRequest request, string reason)
    {
        _lastFailure = reason; // 保留最后一个候选实际失败原因，使用固定字符串避免高频分配。
        _candidate++; // 每次失败只前进，禁止回到同一候选无限重试。
        _stage = 1; // 下一个候选从独立导航采样开始。
        return _candidate >= Math.Min(_count, request.Kind == QueryKind.Cover ? 2 : 1) && Fail(now); // 无候选时释放 Agent 等待锁。
    }

    /// <summary>统一处理无掩体、不可达或路径不完整的回退。</summary>
    private bool Fail(double now)
    {
        _agent.QueryFailed(now, _lastFailure);
        return true;
    }
}
