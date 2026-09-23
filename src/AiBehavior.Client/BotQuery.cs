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
    private Vector3 _origin;
    private Vector3 _requested;
    private float _length;
    private QueryKind _kind;
    private string _lastFailure = "no-candidates";

    /// <summary>每个 Bot 复用固定工作区，禁止因地图密度无限扩容。</summary>
    internal BotQuery(BotAgent agent)
    {
        _agent = agent;
    }

    /// <summary>新动作请求重置有限状态，普通移动不需要扫描碰撞体。</summary>
    internal void Begin(in WorkRequest request)
    {
        _stage = -1; // 所有路线先验证真实起点，避免把源点异常误记为终点不可达。
        _kind = request.Kind; // 失败明细必须区分调查和掩体。
        _count = request.Kind == QueryKind.Cover ? 0 : request.Kind == QueryKind.Search ? 4 : 1; // 搜索最多四次有限候选验证。
        _candidate = 0; // 每个请求从最精确的位置开始。
        _length = 0; // 不继承上一请求的路径长度。
        _point = _requested = GameAdapter.Position(request.Position); // 只使用固化的事件位置。
        _lastFailure = "no-candidates"; // 尚未发生验证失败。
        _candidates[0] = _requested; // 首次仍用一点五米采样。
        if (request.Kind != QueryKind.Search) return; // 普通近移和掩体不扩大原有候选。
        _candidates[1] = _requested; // 第二次最多四米投影，处理斜坡和离网格的声源。
        Vector3 direction = _requested - _agent.Owner.Position; // 确定两个有限侧面替代点。
        direction.y = 0; // 不把楼层高度混入侧向方向。
        Vector3 side = direction.sqrMagnitude > 0.01f ? Vector3.Cross(Vector3.up, direction.normalized) * 3 : Vector3.right * 3; // 侧移固定三米，不随 Bot 等级增加工作。
        _candidates[2] = _requested + side; // 一侧不可达时仍可试另一侧。
        _candidates[3] = _requested - side; // 单次请求绝不无限扩散采样。
    }

    /// <summary>最多进行一次昂贵调用，返回 false 表示需要下一帧继续。</summary>
    internal bool Step(in WorkRequest request, double now, int frame)
    {
        RaidRuntime runtime = _agent.Runtime; // 所有 Bot 使用同一组额度。
        if (!_agent.Accepts(request)) return true; // 旧动作和已销毁对象不再处理。
        if (_stage == -1) // 起点采样单独占用一次共享额度。
        {
            if (!runtime.Samples.TryTake(now, frame)) return false; // 预算不足时保持原截止时间。
            _origin = _agent.Owner.Position; // 捕获查询开始时的实际 Bot 位置。
            if (!NavMesh.SamplePosition(_origin, out NavMeshHit sourceHit, 1.5f, NavMesh.AllAreas) || Mathf.Abs(sourceHit.position.y - _origin.y) > 1) // 不投影到明显不同的楼层。
            { _lastFailure = "nav-source"; return Fail(now); } // 源点问题不能通过遍历所有目标候选解决。
            _origin = sourceHit.position; // 路径求解从合法导航位置开始。
            _stage = request.Kind == QueryKind.Cover ? 0 : 1; // 后续仍分帧处理。
            return false;
        }
        if (_stage == 0) // 首先在当前位置附近收集最多六个候选。
        {
            if (!runtime.Overlaps.TryTake(now, frame)) return false; // 额度不足时保持原截止时间。
            Gather(GameAdapter.Position(request.Threat)); // 内部仅一次 NonAlloc 重叠查询。
            _stage = 1; // 下一步单独领取导航采样额度。
            return false;
        }
        if (_candidate >= Math.Min(_count, request.Kind == QueryKind.Cover ? 2 : request.Kind == QueryKind.Search ? 4 : 1)) return Fail(now); // 掩体最多两个、搜索最多四个候选。
        if (_stage == 1) // 导航投影避免把目标设在墙内或悬空位置。
        {
            if (!runtime.Samples.TryTake(now, frame)) return false; // 位置采样也计入全局预算。
            float radius = request.Kind == QueryKind.Search && _candidate == 1 ? 4 : 1.5f; // 只有第二次搜索候选允许一次有限扩采样。
            if (!NavMesh.SamplePosition(_candidates[_candidate], out NavMeshHit navHit, radius, NavMesh.AllAreas) || Mathf.Abs(navHit.position.y - _candidates[_candidate].y) > 3) return NextCandidate(now, request, "nav-sample"); // 不把更远楼层当作就近目标。
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
        if ((_agent.Owner.Position - _origin).sqrMagnitude > 2.25f) { _lastFailure = "path-source"; return Fail(now); } // 排队期间源点变化后重新申请，不能复用远处起点。
        _path.ClearCorners(); // 重用路径对象，不沿用上次结果。
        if (!NavMesh.CalculatePath(_origin, _point, NavMesh.AllAreas, _path) || _path.status != NavMeshPathStatus.PathComplete)
            return NextCandidate(now, request, "path-incomplete"); // 部分路径也按失败处理。
        Vector3[] corners = _path.corners; // 仅成功路径创建角点副本，原生移动器会持有它。
        if (corners.Length < 1 || corners.Length > 128) return NextCandidate(now, request, "path-corners"); // 已在目标处的单角点交给下面的到达判断。
        _length = 0; // 保留本候选实际路线长度。
        for (int index = 1; index < corners.Length; index++) _length += Vector3.Distance(corners[index - 1], corners[index]); // 有界计算用于近处到达及失败诊断。
        if (request.Kind == QueryKind.Search && _length <= 1.2f && (_agent.Owner.Position - _point).sqrMagnitude <= 1.44f && (corners[corners.Length - 1] - _point).sqrMagnitude <= 2.25f) // 必须先验证完整短路线，隔墙直线近不等于到达。
        { _agent.SearchArrived(request, now); return true; } // 零距离合法候选直接进入驻留，不重复提交零长度移动。
        if (corners.Length < 2) return NextCandidate(now, request, "path-corners"); // 其他单角点不能作为路径。
        if (request.Kind == QueryKind.Search) // 搜索按完整导航路线缓存，交给执行器按路径长度切段。
        {
            Span<System.Numerics.Vector3> values = stackalloc System.Numerics.Vector3[corners.Length]; // 固定上限栈空间，不建立每次转换的托管数组。
            for (int index = 0; index < corners.Length; index++) values[index] = GameAdapter.Snapshot(corners[index]); // 保留全部转角和坡度。
            bool accepted = _agent.SearchRoute.Load(values, GameAdapter.Snapshot(_agent.Owner.Position), GameAdapter.Snapshot(_point), true, now, out string reason); // 独立核对起终点与有限绕路长度。
            _length = _agent.SearchRoute.Length; // 成功和后续失败诊断能看到完整路线成本。
            if (!accepted) return NextCandidate(now, request, reason); // 失败也保留长度再尝试下一个候选。
            _agent.QuerySucceeded(request, _point, corners, now); // 下一步沿缓存消费，不再按直线求中间点。
            return true;
        }
        float limit = request.Kind == QueryKind.Cover ? 40 : _agent.Role == BotRole.Scav ? 35 : 80; // 各类移动有明确距离上限。
        if (_length > limit) return NextCandidate(now, request, "path-length"); // 路径过长单独归类。
        if ((corners[corners.Length - 1] - _point).sqrMagnitude > 2.25f) return NextCandidate(now, request, "path-endpoint"); // 终点偏离单独归类。
        _agent.QuerySucceeded(request, _point, corners, now); // 写入前再次由 Agent 核对动作代次。
        return true;
    }

    /// <summary>仅日志额度通过后格式化查询快照，坐标用于区分坡地、源点和终点问题。</summary>
    internal string Details()
    {
        return FormattableString.Invariant($"kind={_kind} candidate={_candidate} source={_origin.x:F1},{_origin.y:F1},{_origin.z:F1} requested={_requested.x:F1},{_requested.y:F1},{_requested.z:F1} projected={_point.x:F1},{_point.y:F1},{_point.z:F1} pathLength={_length:F1} navStatus={_path.status}");
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
        return _candidate >= Math.Min(_count, request.Kind == QueryKind.Cover ? 2 : request.Kind == QueryKind.Search ? 4 : 1) && Fail(now); // 保留搜索替代候选的处理机会。
    }

    /// <summary>统一处理无掩体、不可达或路径不完整的回退。</summary>
    private bool Fail(double now)
    {
        _agent.QueryFailed(now, _lastFailure);
        return true;
    }
}
