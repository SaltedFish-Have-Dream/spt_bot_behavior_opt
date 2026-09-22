using System;
using AiBehavior.Core;
using EFT;
using UnityEngine;

namespace AiBehavior.Client;

/// <summary>单个 Bot 的值记忆、状态机和动作控制，不创建独立 Update 组件。</summary>
internal sealed class BotAgent : IDisposable
{
    internal readonly RaidRuntime Runtime;
    internal readonly BotOwner Owner;
    internal readonly int Id;
    internal readonly BotRole Role;
    internal readonly SkillProfile Skill;
    internal readonly ThreatMemory Memory = new();
    internal readonly ReactionGate Gate = new();
    internal readonly BotQuery Query;
    private readonly BotRandom _random;
    private readonly DecisionPolicy _policy = new();
    private Observation _clue;
    private bool _hasClue;
    private bool _visible;
    private bool _needsRecovery;
    private bool _recoveryRunning;
    private Vector3 _aimPoint;
    private string? _selectedIdentity;
    private int _contextKey = -1;
    private int _contextVersion;
    private float _decisionRoll;
    private bool _hasCover;
    private Vector3 _cover;
    private Vector3 _coverThreat;
    private Vector3[]? _coverPath;
    private double _coverExpires;
    private bool _pending;
    private double _pendingDeadline;
    private double _retryAt;
    private int _queryFailures;
    private bool _moving;
    private Vector3 _destination;
    private Vector3 _progressPosition;
    private double _lastProgress;
    private float _savedPose;
    private float _savedSpeed;
    private double _nextRecovery;
    private string? _searchIdentity;
    private Vector3 _searchAnchor;
    private Vector3 _searchStart;
    private readonly Vector3[] _searchPoints = new Vector3[3];
    private int _searchIndex;
    private double _arrivedAt;
    private double _nextShotAttempt;
    private bool _shotPending;
    private bool _shotPermit;
    private string? _shotIdentity;
    private double _shotRequestedAt;
    private double _shotDeadline;
    private double _shotPermitUntil;
    private Vector3 _shotOrigin;
    private Vector3 _shotPoint;
    internal bool Controlled;
    internal bool Disposed;
    internal int Generation;
    internal double NextDecision;
    internal BehaviorState State;

    /// <summary>缓存能力和固定容量工作区，缺失等级回退到成长起点。</summary>
    internal BotAgent(RaidRuntime runtime, BotOwner owner, int id, BotRole role)
    {
        Runtime = runtime;
        Owner = owner;
        Id = id;
        Role = role;
        int level = owner.Profile.Info.Level;
        if (level < 1) { level = runtime.Options.MinimumLevel; runtime.Log.LogWarning($"Bot {id} 等级无效，已回退到成长起点。"); }
        Skill = SkillProfile.Create(role, level, runtime.Options.MinimumLevel, runtime.Options.MaximumLevel);
        _random = new BotRandom(owner.ProfileId);
        Query = new BotQuery(this);
        NextDecision = Time.time + id % 10 * 0.01;
    }

    /// <summary>只在已产生自有决策且原生未锁定投掷动作时申请控制。</summary>
    internal bool ShouldControl => !Disposed && Owner != null && !Owner.IsDead && Owner.BotState == EBotState.Active &&
        State != BehaviorState.Native && Owner.WeaponManager?.Grenades?.ThrowindNow != true;

    /// <summary>检查本地掩体到达条件，不依赖被原生寻路隐式更新的目标位置。</summary>
    private bool AtCover => _hasCover && (Owner.Position - _cover).sqrMagnitude <= 1.44f;

    /// <summary>真正视觉更新后保存个人位置快照，并立即唤醒普通决策。</summary>
    internal void ObserveVision(EnemyInfo enemy, double now)
    {
        if (Disposed || !GameAdapter.DirectlyVisible(enemy, now)) return; // 感知型可见和过期数据不进入精确记忆。
        bool known = Memory.TryGet(enemy.ProfileId, now, out _); // 区分新接敌与持续观察。
        var observation = new Observation(enemy.ProfileId, ObservationSource.Vision, GameAdapter.Snapshot(enemy.PersonalLastPos),
            enemy.PersonalLastSeenTime, enemy.PersonalLastSeenTime + Skill.MemorySeconds, 0, GameAdapter.Snapshot(enemy.GetPartToShoot())); // 瞄准坐标也只在真实视觉检查完成时生成快照。
        Memory.Observe(observation, now); // 只写固定四项记忆。
        if (!known || !_visible) NextDecision = Math.Min(NextDecision, now); // 新威胁不用等待空闲两秒周期。
    }

    /// <summary>接收原生听觉已判定听到的声音，只在事件到达时生成一次定位误差。</summary>
    internal void ObserveSound(string identity, Vector3 position, double now)
    {
        if (Disposed || Owner.BotState != EBotState.Active) return; // 失活 Bot 不积累声音队列。
        if (Memory.TryGet(identity, now, out Observation previous) && now - previous.ObservedAt < 1) return; // 同一声源一秒内合并。
        float distance = Vector3.Distance(Owner.Position, position); // 使用声音事件位置，不能追踪声源后续移动。
        float error = Skill.HearingError * Mathf.Clamp(distance / 30f, 0.25f, 2f); // 等级基线再乘固定距离修正。
        float angle = _random.Next01() * Mathf.PI * 2; // 每条新线索只抽样一次方向。
        float radius = Mathf.Sqrt(_random.Next01()) * error; // 圆盘内均匀抽样，避免偏向中心。
        position += new Vector3(Mathf.Cos(angle) * radius, 0, Mathf.Sin(angle) * radius); // 后续读取只看到带误差的位置。
        var observation = new Observation(identity, ObservationSource.Hearing, GameAdapter.Snapshot(position), now, now + Math.Min(Skill.MemorySeconds, 8), error); // 声音线索最多保留八秒。
        if (Memory.Observe(observation, now)) NextDecision = Math.Min(NextDecision, now); // 合法新声音唤醒调查。
    }

    /// <summary>逐帧处理视线、过期与正在射击的安全边界，不进行物理查询。</summary>
    internal void SafetyTick(double now)
    {
        if (Disposed) return; // 旧回调不访问已释放对象。
        if (Owner.BotState != EBotState.Active) { Release(); Gate.Reset(); return; } // 停用时交回动作并清空射击就绪。
        EnemyInfo? enemy = Owner.Memory.GoalEnemy; // 读取原生选定的单一敌人，不扫描全场。
        bool visible = TryVisibleObservation(enemy, now, out Observation sight); // 必须已有真实检查产生的坐标快照。
        if (enemy != null && enemy.Person?.HealthController?.IsAlive != true) // 死亡目标不继续参与搜索。
        {
            Memory.Forget(enemy.ProfileId); // 清除尸体记忆。
            Owner.Memory.GoalEnemy = null; // 让原生系统重选目标。
            enemy = null; // 防止本次继续使用旧对象。
            visible = false;
        }
        if (enemy != null && !visible && !Memory.TryGet(enemy.ProfileId, now, out _)) // 到期后不允许原生旧目标恢复隐藏追踪。
        {
            Owner.Memory.GoalEnemy = null; // 只清理当前目标，不修改全局敌对关系。
            enemy = null;
        }
        if (_visible != visible) NextDecision = Math.Min(NextDecision, now); // 视线改变及时改变高层状态。
        _visible = visible; // 记录本帧的个人视觉状态。
        bool ready = Owner.WeaponManager.IsWeaponReady && Owner.WeaponManager.HaveBullets && !Owner.WeaponManager.Reload.Reloading; // 原生武器条件不被能力曲线覆盖。
        Gate.Update(enemy?.ProfileId, visible, ready, now); // 并行推进察觉和稳定计时。
        if (visible) _aimPoint = GameAdapter.Position(sight.AimPosition); // 两次原生视觉检查之间不追踪实时身体坐标。
        if (!visible && Owner.ShootData.Shooting) Owner.ShootData.EndShoot(); // 遮挡后及时停止持续连射。
        _hasClue = enemy != null && Memory.TryGet(enemy.ProfileId, now, out _clue); // 优先保留当前敌人的合法观察。
        if (!_hasClue) _hasClue = Memory.TryGetLatest(now, out _clue); // 没有当前目标时使用最新声音或视觉线索。
        _recoveryRunning = Owner.Medecine.Using || Owner.Medecine.FirstAid.Using || Owner.WeaponManager.Reload.Reloading; // 已开始恢复由原生动作完成。
        if (_hasCover && (now >= _coverExpires || (_hasClue && (_coverThreat - GameAdapter.Position(_clue.Position)).sqrMagnitude > 25))) // 敌情方向明显改变时失效缓存。
        { _hasCover = false; _coverPath = null; NextDecision = Math.Min(NextDecision, now); }
        if (_pending && now >= _pendingDeadline) QueryFailed(now); // 队列丢弃过期请求后也要释放 Bot 的等待状态。
        if (_shotPending && now >= _shotDeadline) _shotPending = false; // 过期射击允许重新公平排队。
        if (_moving && (Owner.Position - _progressPosition).sqrMagnitude > 0.25f) { _progressPosition = Owner.Position; _lastProgress = now; } // 用实际位移判定进度。
        if (_moving && now - _lastProgress > 2.5 && (Owner.Position - _destination).sqrMagnitude > 1.44f) // 门口或不可达点不能无限保持移动意图。
        {
            StopMotion(); // 终止卡住路径。
            QueryFailed(now); // 两次失败后进入更长冷却。
        }
    }

    /// <summary>按战斗、警戒和空闲频率重选有界动作。</summary>
    internal void Decide(double now)
    {
        _needsRecovery = !Owner.WeaponManager.HaveBullets || Owner.Medecine.FirstAid.Have2Do; // 只检查原生恢复需求。
        bool underFire = Owner.Memory.LastTimeHit > 0 && now - Owner.Memory.LastTimeHit < 2; // 命中事件比全场威胁遍历便宜。
        var health = Owner.HealthController.GetBodyPartHealth(EBodyPart.Common); // 读取自身健康，不能读取隐藏敌人的状态。
        bool lowHealth = health.Maximum > 0 && health.Current / health.Maximum < 0.45f; // 低血量倾向脱离或自救。
        int key = (_visible ? 1 : 0) | (_needsRecovery ? 2 : 0) | (_hasCover ? 4 : 0) | (underFire ? 8 : 0) | (lowHealth ? 16 : 0); // 仅实质条件变化才形成新情境。
        string? identity = _hasClue ? _clue.Identity : null; // 不把每次视觉时间更新当成新情境。
        if (key != _contextKey || identity != _selectedIdentity) // 同一交火上下文不反复抽取概率。
        {
            _contextKey = key; // 保存本次布尔情境。
            _selectedIdentity = identity; // 目标改变允许重新选择。
            _contextVersion++; // 为纯逻辑提供稳定代次。
            _decisionRoll = _random.Next01(); // 与 Unity 随机数互不干扰。
        }
        var input = new DecisionInput // 构造不携带游戏对象的决策快照。
        {
            HasClue = _hasClue, Visible = _visible, Reacted = Gate.CanFire(Skill, now),
            SoundOnly = _hasClue && _clue.Source == ObservationSource.Hearing,
            NeedsRecovery = _needsRecovery, RecoveryRunning = _recoveryRunning,
            HasCover = _hasCover, AtCover = AtCover, UnderFire = underFire, LowHealth = lowHealth,
            CanMove = Role != BotRole.Marksman, ContextVersion = _contextVersion
        };
        BehaviorState next = _policy.Decide(input, Skill, now, _decisionRoll); // 状态候选数与等级无关。
        if (next != State) // 只在状态切换时失效旧请求。
        {
            if (Controlled) StopMotion(); // 新状态不能继续执行旧状态的移动目标。
            State = next; // 提交给 BigBrain 的缓存状态。
            Generation++; // 使旧动作路径结果无法复用。
            Runtime.Queue.Cancel(Id); // 清除尚未执行的旧任务。
            Runtime.ShotQueue.Cancel(Id); // 状态切换也撤销旧射击许可。
            _shotPending = _shotPermit = false; // 不复用上一个动作的验证结果。
            _pending = false; // 解除旧动作的等待锁。
            Runtime.StateChanges++; // 汇总诊断而不逐状态刷日志。
        }
        if (Controlled && _hasClue && Role != BotRole.Marksman && !_hasCover && !_pending && now >= _retryAt && (_visible || _needsRecovery))
            Request(QueryKind.Cover, Owner.Position, now); // 只在有实际需求时查找局部掩体。
        NextDecision = now + (_visible ? 0.2 : _hasClue ? 0.5 : 2); // 所有等级共用 5/2/0.5 Hz。
    }

    /// <summary>开始自有动作时保存原生姿态，避免沿用上一层的旧路径。</summary>
    internal void Claim()
    {
        if (Disposed || Controlled) return; // 取得控制是幂等操作。
        Controlled = true; // 后续结果只有持有控制权才能执行。
        _savedPose = Owner.Mover.TargetPose; // 保存被临时覆盖的姿态目标。
        _savedSpeed = Owner.Mover.DestMoveSpeed; // 保存原生移动速度目标。
        Owner.PatrollingData.Pause(); // 暂停原生巡逻，避免它重新提交旧目的地。
        Owner.Mover.Stop(); // 取消原生上一个动作的路径。
        Owner.Mover.MovementResume(); // 不继承上一动作的短期移动暂停。
        Owner.Mover.Sprint(false); // 不在未知路线中保持冲刺。
        NextDecision = Math.Min(NextDecision, Time.time); // 下次共享调度立即处理当前情境。
    }

    /// <summary>被抢占或层退出时停止自身控制，原生治疗与换弹不被主动取消。</summary>
    internal void Release()
    {
        Runtime.Queue.Cancel(Id); // 撤销所有未完成请求。
        Runtime.ShotQueue.Cancel(Id); // 层退出后旧射击结果不能生效。
        _shotPending = _shotPermit = false; // 清除排队与许可标志。
        Generation++; // 即使请求已被取出也不能再次应用。
        _pending = false; // 清理等待标志。
        _hasCover = false; // 不在交接后使用旧起点路径。
        _coverPath = null; // 释放路径角点数组。
        Gate.Reset(); // 重新接管后需要重新准备射击。
        if (!Controlled) return; // 不停止属于其他层的动作。
        Controlled = false; // 先交出令牌，异常路径也不会继续写入。
        if (Owner == null) return; // Unity 已销毁对象不能再访问。
        Owner.Mover?.Stop(); // 清除本模组留下的移动路径。
        _moving = false; // 同步本地移动状态。
        Owner.ShootData?.EndShoot(); // 停止本模组开始的连射。
        Owner.AimingManager?.CurrentAiming?.LoseTarget(); // 清除精确瞄准覆盖。
        Owner.SetPose(_savedPose); // 恢复交接前的姿态参数。
        Owner.SetTargetMoveSpeed(_savedSpeed); // 恢复交接前的速度参数。
    }

    /// <summary>逐帧执行缓存状态；物理、掩体和寻路不在这里展开扫描。</summary>
    internal void Execute(double now)
    {
        if (!Controlled || Disposed || Owner.BotState != EBotState.Active) return; // 原生抢占后停止动作写入。
        switch (State) // 每帧仅执行一个当前动作。
        {
            case BehaviorState.Observe:
            case BehaviorState.Engage:
                StopMotion(); // 射击窗口内避免无目的持续移动。
                AimAndFire(now); // 武器动画保持原生逐帧更新。
                break;
            case BehaviorState.Cover:
            case BehaviorState.Disengage:
                MoveToCover(now); // 使用已经验证的路径或排队重算。
                if (State == BehaviorState.Cover) AimAndFire(now); // 普通掩体移动允许原生检查后射击。
                else StopAim(); // 脱离时不对隐藏位置继续瞄准。
                break;
            case BehaviorState.Investigate:
            case BehaviorState.Search:
                StopAim(); // 调查只观察线索，不赋予射击权限。
                Search(now); // 最多三个位置，失败与到期都能结束。
                break;
            case BehaviorState.Recover:
                StopMotion(); // 换弹和治疗保持稳定位置。
                StopAim(); // 恢复过程不开新连射。
                Recover(now); // 原生接口处理物品与动画。
                break;
            default:
                StopMotion(); // 即将交还巡逻前停止自有移动。
                StopAim();
                break;
        }
    }

    /// <summary>保留原生散布、后坐力和武器就绪检查，只控制合法目标与开火时间。</summary>
    private void AimAndFire(double now)
    {
        EnemyInfo? enemy = Owner.Memory.GoalEnemy; // 每次开火前重新核对当前目标。
        if (!_visible || !TryVisibleObservation(enemy, now, out Observation sight) || enemy?.CanShoot != true || _recoveryRunning) { StopAim(); return; } // 同时验证目标身份、合法部位与最新观察。
        _aimPoint = GameAdapter.Position(sight.AimPosition); // 目标切换后不能沿用上一敌人的瞄准点。
        var aiming = Owner.AimingManager.CurrentAiming; // 使用原生瞄准系统。
        aiming.SetTarget(_aimPoint); // 输入仅来自本 Bot 可见部位的快照。
        aiming.NodeUpdate(); // 原生稳定、姿态和后坐力继续工作。
        if (Gate.CanFire(Skill, now) && aiming.IsReady && enemy!.CanShoot && !Owner.ShootData.Shooting && now >= _nextShotAttempt) // 曲线只是必要条件，不绕过原生条件。
        {
            _nextShotAttempt = now + 0.05; // 失败验证最多每秒二十次尝试，不每帧消耗射线。
            Owner.ShootData.Shoot(); // Harmony 射击入口统一做最终预算内验证。
        }
    }

    /// <summary>只消费新鲜且未使用的射击许可；需要验证时进入公平队列。</summary>
    internal bool AllowShot(double now, int frame)
    {
        EnemyInfo? enemy = Owner.Memory.GoalEnemy; // 不信任动作开始时的旧目标。
        bool visible = TryVisibleObservation(enemy, now, out _); // 必须有当前个人视觉快照。
        bool ready = Owner.WeaponManager.IsWeaponReady && !Owner.WeaponManager.Reload.Reloading && Owner.WeaponManager.HaveBullets; // 不修改原生限制。
        Gate.Update(enemy?.ProfileId, visible, ready, now); // 原生层调用射击时也遵守相同反应门槛。
        if (!visible || !enemy!.CanShoot || !ready || !Gate.CanFire(Skill, now)) return false; // 感知、反应与准备缺一不可。
        if (Owner.ShootData.Shooting) return true; // 已开始的连射由逐帧视线检查负责停止。
        if (now <= Owner.ShootData.nextFingerDownCan) return false; // 原生射击冷却期间不浪费验证额度。
        Vector3 currentOrigin = Owner.WeaponRoot.position; // 检查上次验证后武器是否明显移动。
        Vector3 currentPoint = Owner.AimingManager.CurrentAiming.EndTargetPoint; // 同时核对瞄准点变化。
        if (_shotPermit && now < _shotPermitUntil && _shotIdentity == enemy.ProfileId &&
            (currentOrigin - _shotOrigin).sqrMagnitude < 0.0625f && (currentPoint - _shotPoint).sqrMagnitude < 0.25f) // 不复用旧目标或大幅偏移的验证。
        {
            _shotPermit = false; // 一个许可最多启动一次连射。
            return true;
        }
        _shotPermit = false; // 过期或偏移后清除旧许可。
        if (_shotPending && now < _shotDeadline) return false; // 不因每帧调用而反复插队。
        _shotIdentity = enemy.ProfileId; // 记录请求对应的目标身份。
        _shotRequestedAt = now; // 用于测量真实排队等待时间。
        _shotDeadline = now + 0.2; // 射击确认采用短有效期。
        var request = new WorkRequest(Id, Generation, QueryKind.Shot, GameAdapter.Snapshot(currentPoint), GameAdapter.Snapshot(currentOrigin), _shotDeadline); // 队列只持有值快照。
        _shotPending = Runtime.EnqueueShot(request, now); // 所有 Bot 在共享队列中轮转。
        return false; // 由后续帧取得许可后再调用原生 Shoot。
    }

    /// <summary>轮到本 Bot 后进行两次有预算的物理验证，并发放短期单次许可。</summary>
    internal bool VerifyShot(in WorkRequest request, double now, int frame)
    {
        EnemyInfo? enemy = Owner.Memory.GoalEnemy; // 执行前再次确认目标。
        if (!_shotPending || request.Generation != Generation || enemy == null || enemy.ProfileId != _shotIdentity || !TryVisibleObservation(enemy, now, out _) || !enemy.CanShoot)
        { _shotPending = false; return true; } // 目标改变或失去视线后丢弃请求。
        if (!Runtime.Rays.TryTake(now, frame, 2)) return false; // 一次几何检查与一次人员检查作为一组领取额度。
        _shotPending = false; // 本次已有实际验证结果，不再无限等待。
        Runtime.MaxShotWait = Math.Max(Runtime.MaxShotWait, now - _shotRequestedAt); // 记录等待峰值，不能用静态延迟冒充实际响应。
        Vector3 from = Owner.WeaponRoot.position; // 从枪口附近检查实际射击线。
        Vector3 target = Owner.AimingManager.CurrentAiming.EndTargetPoint; // 使用原生最终瞄准点，包括散布和后坐力。
        if (!ThreatMemory.Finite(GameAdapter.Snapshot(target))) return true; // 无效坐标不能进入物理 API，也不发放许可。
        Runtime.RayCalls++; // 实际 API 次数与领取令牌数分别统计。
        if (Physics.Linecast(from, target, LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore)) return true; // 阻止对实心障碍开火。
        Vector3 direction = target - from; // 第二条检测只查询前方人员。
        float length = direction.magnitude; // 保存长度用于规范化和射线范围。
        if (length < 0.1f) return true; // 退化零长度方向不射击。
        direction /= length; // 避免归一化零向量。
        float offset = Mathf.Min(0.5f, length * 0.25f); // 尽量避开自己的身体碰撞体。
        Runtime.RayCalls++; // 只有实际执行第二条查询才计数。
        if (Physics.SphereCast(from + direction * offset, 0.12f, direction, out RaycastHit hit, length - offset, LayersMaskController.PlayerMask, QueryTriggerInteraction.Ignore)) // 小半径保护枪线附近队友。
        {
            Player? player = Owner.ShootData.GetPlayerByCollider(hit.collider); // 复用游戏的碰撞体映射。
            if (player != null && player.ProfileId != Owner.ProfileId && player.ProfileId != enemy.ProfileId) return true; // 不向任何夹在线路中的其他人开火。
        }
        _shotOrigin = from; // 保存本次实际验证起点。
        _shotPoint = target; // 保存本次实际验证终点。
        _shotPermitUntil = now + 0.1; // 结果只能在很短时间内使用。
        _shotPermit = true; // 正式发放一次新连射许可。
        return true;
    }

    /// <summary>停止当前连射和瞄准，观察方向由后续搜索动作单独设置。</summary>
    private void StopAim()
    {
        if (Owner.ShootData.Shooting) Owner.ShootData.EndShoot();
        Owner.AimingManager.CurrentAiming.LoseTarget();
    }

    /// <summary>只停止本模组自己开始的移动。</summary>
    private void StopMotion()
    {
        if (!_moving) return;
        Owner.Mover.Stop();
        _moving = false;
    }

    /// <summary>使用缓存路径去往已验证的掩体，起点变化后重新排队。</summary>
    private void MoveToCover(double now)
    {
        if (!_hasCover || AtCover) { StopMotion(); return; } // 掩体失效或已经到达时不继续追逐目的地。
        if (_moving) return; // 不重复向原生移动器提交相同路径。
        if (_coverPath != null && (Owner.Position - _coverPath[0]).sqrMagnitude < 2.25f) // 只复用仍接近起点的路线。
        {
            StartPath(_coverPath, _cover, now); // 路径已计算，不触发第二次完整寻路。
            _coverPath = null; // 原生路径对象继续持有数组，本地不再复用。
        }
        else if (!_pending && now >= _retryAt) Request(QueryKind.Move, _cover, now); // 按全局额度重建路径。
    }

    /// <summary>围绕固定线索搜索至多三个位置，狙击 Scav 只在岗位观察。</summary>
    private void Search(double now)
    {
        if (!_hasClue) return; // 到期后不生成目标位置。
        Vector3 anchor = GameAdapter.Position(_clue.Position); // 只能读观察快照。
        Owner.Steering.LookToPoint(anchor + Vector3.up); // 看向搜索区域而非隐藏目标的实时位置。
        if (Role == BotRole.Marksman) return; // 狙击模板不得离开岗位追击。
        if (_searchIdentity != _clue.Identity || (_searchAnchor - anchor).sqrMagnitude > 16) // 小幅新声音不反复重置完整搜索。
        {
            _searchIdentity = _clue.Identity; // 绑定本轮线索身份。
            _searchAnchor = anchor; // 固定本轮中心。
            _searchStart = Owner.Position; // 用于限制追击范围。
            _searchIndex = 0; // 新的合法区域才重新开始三个候选。
            _arrivedAt = 0; // 清理上一轮驻留计时。
            float angle = _random.Next01() * Mathf.PI * 2; // 一次生成后续候选方向。
            Vector3 lateral = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * Mathf.Clamp(_clue.Uncertainty, 2, 6); // 候选范围保持有界。
            _searchPoints[0] = anchor; // 首先检查线索中心。
            _searchPoints[1] = anchor + lateral; // 第二点检查一个侧面。
            _searchPoints[2] = anchor - lateral; // 第三点检查相反侧面。
            StopMotion(); // 不沿用上一线索的路径。
        }
        float limit = Role == BotRole.Scav ? 25 : 60; // 普通 Scav 追击范围固定且更小。
        if (_searchIndex >= 3 || (_searchStart - anchor).sqrMagnitude > limit * limit) // 搜索完成或超出局部职责就结束。
        {
            Memory.Forget(_clue.Identity); // 不能通过重新读取延长记忆。
            _hasClue = false; // 下次决策交还原生巡逻。
            NextDecision = Math.Min(NextDecision, now);
            StopMotion();
            return;
        }
        Vector3 point = _searchPoints[_searchIndex]; // 本轮最多只有三个候选。
        if ((Owner.Position - point).sqrMagnitude < 2.25f || (_moving && (Owner.Position - _destination).sqrMagnitude <= 1.44f)) // 使用导航投影后的实际到达点，避免卡在原始坐标旁。
        {
            StopMotion(); // 清理已完成的移动。
            if (_arrivedAt == 0) _arrivedAt = now; // 只在首次到达时开始驻留。
            if (now - _arrivedAt >= 1) { _searchIndex++; _arrivedAt = 0; } // 满足驻留时间后才继续搜索。
        }
        else if (!_moving && !_pending && now >= _retryAt) Request(QueryKind.Move, point, now); // 路径由共享调度器分配。
    }

    /// <summary>安全条件成立时调用原生恢复接口，失败重试有冷却。</summary>
    private void Recover(double now)
    {
        if (_recoveryRunning || now < _nextRecovery || (_visible && !AtCover)) return; // 不打断恢复，也不在暴露位置反复用药。
        _nextRecovery = now + 2; // 背包无药或弹药不足时限制尝试频率。
        if (!Owner.WeaponManager.HaveBullets) Owner.WeaponManager.Reload.TryReload(); // 由原生接口处理弹匣与手部状态。
        else if (Owner.Medecine.FirstAid.ShallStartUse()) Owner.Medecine.FirstAid.ApplyToSelf(); // 先验证可使用物品与身体部位。
    }

    /// <summary>为当前动作创建带截止时间的唯一查询。</summary>
    private void Request(QueryKind kind, Vector3 position, double now)
    {
        var request = new WorkRequest(Id, Generation, kind, GameAdapter.Snapshot(position), _hasClue ? _clue.Position : GameAdapter.Snapshot(Owner.Position), now + 1); // 查询不会无限等待。
        if (!Runtime.Enqueue(request, now)) { _retryAt = now + 0.5; return; } // 队列满时推迟可选工作。
        Query.Begin(request); // 初始化本次有界分步查询。
        _pending = true; // 同一 Bot 不重复提交昂贵查询。
        _pendingDeadline = request.Deadline; // 用于清理被队列超时丢弃的任务。
    }

    /// <summary>查询执行前检查生命周期、动作代次与控制权。</summary>
    internal bool Accepts(in WorkRequest request)
    {
        return !Disposed && Controlled && _pending && request.Owner == Id && request.Generation == Generation && Owner.BotState == EBotState.Active &&
            (request.Kind != QueryKind.Cover || (_hasClue && System.Numerics.Vector3.DistanceSquared(request.Threat, _clue.Position) <= 25));
    }

    /// <summary>验证观察身份、来源和时间，视觉检查之外不读取隐藏身体位置。</summary>
    internal bool TryVisibleObservation(EnemyInfo? enemy, double now, out Observation observation)
    {
        observation = default; // 失败时不给出伪造瞄准坐标。
        return GameAdapter.DirectlyVisible(enemy, now) && Memory.TryGet(enemy!.ProfileId, now, out observation) &&
            observation.Source == ObservationSource.Vision && observation.ObservedAt == enemy.PersonalLastSeenTime; // 观察必须对应原生最新的个人检查。
    }

    /// <summary>提交成功路径或掩体缓存，禁止使用已被原生层抢占的结果。</summary>
    internal void QuerySucceeded(in WorkRequest request, Vector3 point, Vector3[] path, double now)
    {
        if (!Accepts(request)) return; // 最终写入前再次检查控制权。
        _pending = false; // 解除查询等待。
        _queryFailures = 0; // 成功后清除连续失败次数。
        _retryAt = now + 0.8; // 避免同一敌情立即再次查找。
        Runtime.CompletedQueries++; // 记录真实完成而非仅排队的请求。
        if (request.Kind == QueryKind.Cover) // 掩体结果先交给决策层，不强制立即移动。
        {
            _cover = point; // 缓存已验证位置。
            _coverPath = path; // 缓存本次计算结果。
            _coverThreat = GameAdapter.Position(request.Threat); // 缓存对应的合法威胁方向。
            _coverExpires = now + 8; // 设置有限的缓存寿命。
            _hasCover = true; // 允许下次决策考虑掩体动作。
            NextDecision = Math.Min(NextDecision, now); // 有新候选后及时重新评分。
        }
        else StartPath(path, point, now); // 普通搜索或重规划可以直接执行。
    }

    /// <summary>失败最多连续重试两次，随后暂时放弃当前目标。</summary>
    internal void QueryFailed(double now)
    {
        Runtime.Queue.Cancel(Id); // 清除仍在队列中的同一请求。
        _pending = false; // 防止超时造成永久等待。
        _queryFailures++; // 统计当前目标的连续失败。
        _retryAt = now + (_queryFailures >= 2 ? 5 : 0.8); // 达到上限后使用更长冷却。
        Runtime.FailedQueries++; // 诊断失败和排队成功分开计算。
        if (_queryFailures >= 2) // 无法到达时允许行为继续而不是无限重算。
        {
            _hasCover = false; // 放弃不可用掩体。
            _coverPath = null; // 释放失败缓存。
            if (State == BehaviorState.Search || State == BehaviorState.Investigate) _searchIndex++; // 搜索跳过当前不可达点。
            _queryFailures = 0; // 冷却后下一目的地可重新验证。
        }
        NextDecision = Math.Min(NextDecision, now); // 让状态机考虑回退。
    }

    /// <summary>把完整路径直接交给原生移动器，避免 GoToPoint 的重复寻路。</summary>
    private void StartPath(Vector3[] path, Vector3 point, double now)
    {
        if (path.Length < 2 || !Controlled) return; // 退化路径和失去控制时禁止移动。
        Owner.SetPose(0.9f); // 使用可正常行走的姿态。
        Owner.SetTargetMoveSpeed(Role == BotRole.Scav ? 0.8f : 1f); // 角色固定速度，不随等级增加执行频率。
        Owner.Mover.GoToByWay(path, 0.6f); // 已计算路径直接设置到原生路径控制器。
        _destination = point; // 保存有界的移动目标。
        _progressPosition = Owner.Position; // 用真实位移检测卡住。
        _lastProgress = now; // 开始本次移动超时计时。
        _moving = true; // 标记此路径由本模组持有。
    }

    /// <summary>注销时撤销请求和动作，清理失败也不阻止释放索引。</summary>
    public void Dispose()
    {
        if (Disposed) return; // 保证重复销毁安全。
        try { Release(); } // 尽量恢复原生动作参数。
        catch (Exception exception) { Runtime.Log.LogWarning($"Bot {Id} 清理时对象已失效：{exception.Message}"); } // 销毁过程仅记录一次。
        finally { Disposed = true; Runtime.Queue.Cancel(Id); Runtime.ShotQueue.Cancel(Id); } // 无论清理结果如何都拒绝后续请求。
    }
}
