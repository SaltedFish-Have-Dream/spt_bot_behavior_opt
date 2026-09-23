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
    internal readonly double[] NextDiagnosticAt = new double[(int)DiagnosticEvent.Count];
    private BehaviorState? _lastExecutedState;
    private readonly BotRandom _random;
    private readonly DecisionPolicy _policy = new();
    private readonly BotLifecycle _lifecycle = new();
    private readonly PlayerDanger _playerDanger = new();
    private readonly PosturePolicy _posture = new();
    private readonly CoverArrival _coverArrival = new();
    private double _nextSoundSnapshot;
    private double _nextGunshotSnapshot;
    private double _nextNearBullet;
    private double _nextProneCheck;
    private bool _coverUnavailable;
    private bool _ownsProne;
    private bool _savedProne;
    private Vector3 _dangerOrigin;
    internal bool Participating;
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
    private QueryKind _pendingKind;
    private double _pendingDeadline;
    private double _retryAt;
    private int _queryFailures;
    private bool _moving;
    private Vector3 _destination;
    private Vector3 _progressPosition;
    private double _lastProgress;
    private float _savedSpeed;
    private double _nextRecovery;
    private string? _searchIdentity;
    private Vector3 _searchAnchor;
    private Vector3 _searchStart;
    private readonly Vector3[] _searchPoints = new Vector3[3];
    private int _searchIndex;
    private double _arrivedAt;
    private bool _searchSegmentFinal;
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
    /// <summary>诊断与查询共用实际生命周期代次，重复释放不会递增。</summary>
    internal int Generation => _lifecycle.Generation;
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
    internal bool ShouldControl => Participating && !Disposed && Owner != null && !Owner.IsDead && Owner.BotState == EBotState.Active &&
        State != BehaviorState.Native && Owner.WeaponManager?.Grenades?.ThrowindNow != true;

    /// <summary>真人当前目标才接受射击门控，近距枪声只豁免新增等待、不豁免原生条件。</summary>
    private bool ReadyToReact(double now)
    {
        return now < _playerDanger.CloseAlertUntil || Gate.CanFire(Skill, now);
    }

    /// <summary>紧急掩体使用最后危险方向，普通战斗使用合法观察快照。</summary>
    private Vector3 ThreatPoint(double now)
    {
        return _playerDanger.IsActive(now) ? GameAdapter.Position(_playerDanger.Position) : GameAdapter.Position(_clue.Position);
    }

    /// <summary>共用带距离缓冲的掩体到达判断，避免边缘位移反复触发走动和蹲起。</summary>
    private bool AtCover => _coverArrival.Update(_hasCover, _hasCover ? (Owner.Position - _cover).sqrMagnitude : 0);

    /// <summary>诊断只读取缓存可见状态，不触发新的视觉检查。</summary>
    internal bool HasVisibleTarget => _visible;

    /// <summary>记录事件并检查输出额度，调用方只在允许时构造日志字段。</summary>
    internal bool Trace(DiagnosticEvent kind, double now)
    {
        return Runtime.Diagnostics.Record(kind, this, now);
    }

    /// <summary>输出已通过限频检查的 Bot 明细，使用匿名序号关联事件。</summary>
    internal void WriteTrace(string kind, double now, string details)
    {
        Runtime.Diagnostics.Write(kind, Id, now, details);
    }

    /// <summary>高频射击拒绝只增加原因计数并保持原有返回语义。</summary>
    private bool RejectShot(DiagnosticEvent kind)
    {
        Runtime.Diagnostics.Count(kind);
        return false;
    }

    /// <summary>真正视觉更新后保存个人位置快照，并立即唤醒普通决策。</summary>
    internal void ObserveVision(EnemyInfo enemy, double now)
    {
        if (Disposed || Owner.BotState != EBotState.Active || !GameAdapter.IsPlayerTarget(enemy) || !GameAdapter.DirectlyVisible(enemy, now)) return; // AI 视觉不进入自有记忆。
        Runtime.RememberPlayer(enemy.Person); // 只在已核验真人的事件入口缓存对象。
        bool known = Memory.TryGet(enemy.ProfileId, now, out _); // 区分新接敌与持续观察。
        var observation = new Observation(enemy.ProfileId, ObservationSource.Vision, GameAdapter.Snapshot(enemy.PersonalLastPos),
            enemy.PersonalLastSeenTime, enemy.PersonalLastSeenTime + Skill.MemorySeconds, 0, GameAdapter.Snapshot(enemy.GetPartToShoot())); // 瞄准坐标也只在真实视觉检查完成时生成快照。
        if (Memory.Observe(observation, now)) Runtime.Diagnostics.Count(DiagnosticEvent.VisionSaved); // 只统计实际写入固定记忆的次数，不逐视觉回调刷日志。
        if (!known || !_visible) NextDecision = Math.Min(NextDecision, now); // 新威胁不用等待空闲两秒周期。
    }

    /// <summary>接收原生听觉已判定听到的声音，只在事件到达时生成一次定位误差。</summary>
    internal void ObserveSound(IPlayer source, Vector3 position, double now, bool gunshot)
    {
        if (Disposed || Owner.BotState != EBotState.Active || !GameAdapter.IsLocalPlayer(source)) return; // AI 和未知来源不积累事件。
        Runtime.RememberPlayer(source); // 保存真人身份供危险与范围核对。
        float distance = Vector3.Distance(Owner.Position, position); // 使用声音事件位置，不能追踪声源后续移动。
        GunshotBand band = PlayerThreatPolicy.ClassifyGunshot(true, true, distance); // 枪声感知范围独立于原来的调查步长。
        if (gunshot && band == GunshotBand.Ignore) { Runtime.Diagnostics.Count(DiagnosticEvent.FarGunshotIgnored); return; } // 一百二十米外未命中枪声不额外唤醒。
        if (gunshot && band == GunshotBand.Close) _playerDanger.AlertClose(now); // 每次近枪更新短暂快速警戒，但不能增加视觉能力。
        if (gunshot) NextDecision = Math.Min(NextDecision, now); // 合并事件仍可立即唤醒反应检查。
        if (now < (gunshot ? _nextGunshotSnapshot : _nextSoundSnapshot)) { Runtime.Diagnostics.Count(DiagnosticEvent.SoundMerged); return; } // 枪声与脚步独立限频，脚步不能延迟首次枪声。
        if (gunshot) _nextGunshotSnapshot = now + 0.25; // 连射每秒最多四次生成定位快照。
        else _nextSoundSnapshot = now + 1; // 普通脚步每秒最多一次。
        float error = gunshot ? PlayerThreatPolicy.GunshotError(Skill, distance) : Skill.HearingError * Mathf.Clamp(distance / 30f, 0.25f, 2f); // 距离和能力共同决定区域大小。
        float angle = _random.Next01() * Mathf.PI * 2; // 每条新线索只抽样一次方向。
        float radius = Mathf.Sqrt(_random.Next01()) * error; // 圆盘内均匀抽样，避免偏向中心。
        position += new Vector3(Mathf.Cos(angle) * radius, 0, Mathf.Sin(angle) * radius); // 后续读取只看到带误差的位置。
        if (!gunshot && !InvestigationPolicy.InRange(Role, GameAdapter.Snapshot(Owner.Position), GameAdapter.Snapshot(position))) // 脚步保持局部范围，枪声使用独立的一百二十米分段。
        {
            if (Trace(DiagnosticEvent.SoundOutOfRange, now)) WriteTrace("SOUND_OUT_OF_RANGE", now, $"role={Role} action=skip-investigation"); // 只拒绝本次声音，不删除已有视觉记忆。
            return;
        }
        if (Memory.TryGet(source.ProfileId, now, out Observation previous) && previous.Source == ObservationSource.Vision && now - previous.ObservedAt < 0.35) return; // 新鲜个人视觉不能被声音覆盖而导致停火。
        var observation = new Observation(source.ProfileId, gunshot ? ObservationSource.Gunshot : ObservationSource.Hearing, GameAdapter.Snapshot(position), now,
            now + (gunshot ? PlayerThreatPolicy.SearchLifetime(distance) : Math.Min(Skill.MemorySeconds, 8)), error); // 枪声线索独立限时，允许分段调查。
        if (Memory.Observe(observation, now)) // 合法新声音唤醒调查。
        {
            NextDecision = Math.Min(NextDecision, now); // 保持原有调度时机。
            Owner.LookData.ResetUpdateTime(); // 唤醒原生视觉扫描，不伪造看见敌人。
            if (Trace(DiagnosticEvent.SoundSaved, now)) WriteTrace("SOUND_SAVED", now, FormattableString.Invariant($"source=local-player kind={(gunshot ? "gunshot" : "step")} band={band} distance={distance:F1} error={error:F1} ttl={observation.ExpiresAt - now:F1}")); // 不输出玩家身份或坐标。
        }
    }

    /// <summary>玩家枪弹危险直接唤醒避险；同一近弹的相邻轨迹段合并，命中不受该冷却限制。</summary>
    internal void ObserveDanger(Vector3 origin, double now, string reason)
    {
        if (Disposed || Owner.IsDead || Owner.BotState != EBotState.Active || Runtime.LocalPlayer == null || !ThreatMemory.Finite(GameAdapter.Snapshot(origin))) return; // 未确认玩家来源或对象失活时不接管。
        if (reason == "near-bullet" && now < _nextNearBullet) return; // 合并一颗子弹在连续物理步中的重复接近。
        if (reason == "near-bullet") _nextNearBullet = now + 0.15; // 只限制几何通知，直接命中立即执行。
        float distance = Vector3.Distance(Owner.Position, origin); // 使用原射击时刻的坐标，不访问玩家实时位置。
        Vector3 estimate = origin; // 原点仅用于生成一次不精确的危险方向。
        float error = Skill.HearingError * Mathf.Clamp(distance / 30f, 0.5f, 2f); // 受击不意味着获得跨地图精确定位。
        float angle = _random.Next01() * Mathf.PI * 2; // 每个新危险生成一次固定误差。
        bool entering = !_playerDanger.IsActive(now); // 连续受压不每次取消正在寻找的掩体。
        if (!entering && (_dangerOrigin - origin).sqrMagnitude <= 25) estimate = GameAdapter.Position(_playerDanger.Position); // 同一射击区域沿用估计，避免随机抖动使掩体不断失效。
        else estimate += new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * (Mathf.Sqrt(_random.Next01()) * error); // 新危险区域才生成偏移。
        _dangerOrigin = origin; // 保存事件起点快照供下一次粗略比较。
        if (!_playerDanger.Observe(true, GameAdapter.Snapshot(estimate), now, PlayerThreatPolicy.SearchLifetime(distance))) return; // 只接受有效、非倒序快照。
        if (entering) { _coverUnavailable = false; _retryAt = now; } // 新一轮危险允许尽快查询一次局部掩体。
        Memory.Observe(new Observation(Runtime.LocalPlayer.ProfileId, ObservationSource.Danger, GameAdapter.Snapshot(estimate), now, _playerDanger.SearchUntil, error), now); // 保存有限的接近区域。
        Participating = true; // 受击和近弹不等待普通调度才激活行为层。
        _policy.Reset(BehaviorState.Evade); // 紧急行为绕过普通动作承诺。
        ChangeState(BehaviorState.Evade, now); // 取消原有搜索或射击请求。
        NextDecision = now; // 下一轮继续处理资源查询与恢复条件。
        Owner.LookData.ResetUpdateTime(); // 尽快检查真实视线。
        UpdatePosture(now); // 新危险可立即压低，连续近弹不会重复与移动路线争抢姿态。
        if (Trace(DiagnosticEvent.PlayerDanger, now)) WriteTrace("PLAYER_DANGER", now, $"kind={reason} source=local-player holdSeconds=5 generation={Generation}"); // 不传播实时坐标。
    }

    /// <summary>没有真人情境或转向 AI 交战时交回原生，不停止已经属于原生目标的射击。</summary>
    private void LeavePlayerContext(double now)
    {
        if (Participating || Controlled || _shotPending || _shotPermit) // 只在真正退出时清理资源。
        {
            Release(true); // 原生互战的瞄准与扳机保持正常。
            _policy.Reset(); // 清除玩家情境的动作承诺。
            if (State != BehaviorState.Native) Runtime.StateChanges++; // 汇总仍记录实际回退。
            State = BehaviorState.Native; // BigBrain 归还原生层。
            if (Trace(DiagnosticEvent.PlayerScopeLeft, now)) WriteTrace("PLAYER_SCOPE_LEFT", now, "action=native-control"); // 可对应混战切换。
        }
        Participating = _visible = _hasClue = false; // 不把旧情境状态显示为正在增强。
        NextDecision = double.PositiveInfinity; // 等待新的合法玩家事件唤醒。
        Gate.Reset(); // 新接敌不能继承上次玩家的射击就绪。
    }

    /// <summary>逐帧处理视线、过期与正在射击的安全边界，不进行物理查询。</summary>
    internal void SafetyTick(double now)
    {
        if (Disposed) return; // 旧回调不访问已释放对象。
        bool active = Owner.BotState == EBotState.Active; // AI Limit 等模组可能反复暂停和恢复同一实例。
        if (_lifecycle.SetActive(active)) // 只在激活边沿做一次交接或唤醒。
        {
            if (!active) // 进入停用状态后禁止继续持有旧动作。
            {
                Release(); // 幂等撤销资源，不再逐帧扫描两条队列。
                Gate.Reset(); // 未接管但曾运行射击门控的实例也清除就绪。
                _visible = _hasClue = _recoveryRunning = false; // 汇总和恢复不能沿用停用前的缓存结论。
                Participating = false; // 停用 Bot 不参与增强调度。
                _searchIdentity = null; // 恢复后重新验证搜索起点。
                _policy.Reset(); // 清除停用前的动作承诺。
                if (State != BehaviorState.Native) Runtime.StateChanges++; // 停用迁移也保留总量。
                State = BehaviorState.Native; // BigBrain 下一次层检查可以交还原生。
                if (Trace(DiagnosticEvent.Deactivated, now)) WriteTrace("DEACTIVATED", now, $"generation={Generation}"); // 日志只记录边沿。
            }
            else // 恢复活动后不等待此前的空闲决策周期。
            {
                NextDecision = now; // 进入本帧到期轮转。
                if (Trace(DiagnosticEvent.Reactivated, now)) WriteTrace("REACTIVATED", now, $"generation={Generation}"); // 与停用次数对应排查外部限流。
            }
        }
        if (!active) return; // 稳定停用期只做常数时间判断。
        EnemyInfo? enemy = Owner.Memory.GoalEnemy; // 读取原生选定的单一敌人，不扫描全场。
        bool playerTarget = GameAdapter.IsPlayerTarget(enemy); // 目标来源决定哪些补丁和安全边界可以生效。
        if (playerTarget) Runtime.RememberPlayer(enemy!.Person); // 原生先发现玩家时也能建立合法上下文。
        bool playerAlive = Runtime.LocalPlayer?.HealthController?.IsAlive == true; // 不持续围绕死亡或消失的玩家生成动作。
        bool playerClue = Memory.TryGetLatest(now, out _) || _playerDanger.CanAdvance(now); // 固定容量值记忆，不访问隐藏坐标。
        if (!PlayerThreatPolicy.ShouldManage(active, playerAlive, enemy != null && !playerTarget, playerTarget, playerClue, _playerDanger.IsActive(now))) // AI 互战只有玩家紧急危险可短时打断。
        {
            if (!playerAlive && (playerClue || _playerDanger.SearchUntil > 0)) { Memory.Clear(); _playerDanger.Clear(); } // 玩家失效时只清理已有数据，空闲 Bot 不反复清空数组。
            LeavePlayerContext(now); // 不清除原生 AI 目标、不阻止原生瞄准或射击。
            return; // 不读取医疗、健康、不更新决策、不搜索掩体。
        }
        if (!Participating) // 新的真人线索首次激活时立即进入轮转。
        {
            Participating = true; // 行为层现在可以消费缓存状态。
            NextDecision = now; // 从无穷等待恢复到本帧截止。
            if (Trace(DiagnosticEvent.PlayerScopeEntered, now)) WriteTrace("PLAYER_SCOPE_ENTERED", now, "source=local-player"); // 证明不是全场 Bot 同时进入新增逻辑。
        }
        if (!playerTarget) enemy = null; // 后续目标清理只针对真人，不能写入原生 AI 目标。
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
            if (Trace(DiagnosticEvent.MemoryExpired, now)) WriteTrace("MEMORY_EXPIRED", now, "action=clear-goal-enemy"); // 区分自然失忆和行为卡死。
            Owner.Memory.GoalEnemy = null; // 只清理当前目标，不修改全局敌对关系。
            enemy = null;
        }
        if (_visible != visible) // 视线改变及时改变高层状态。
        {
            NextDecision = Math.Min(NextDecision, now); // 保持原来的唤醒时机。
            if (Trace(DiagnosticEvent.SightChanged, now)) WriteTrace("SIGHT_CHANGED", now, $"visible={visible} shooting={Owner.ShootData.Shooting}"); // 只记录可见性边沿。
        }
        _visible = visible; // 记录本帧的个人视觉状态。
        bool ready = Owner.WeaponManager.IsWeaponReady && Owner.WeaponManager.HaveBullets && !Owner.WeaponManager.Reload.Reloading; // 原生武器条件不被能力曲线覆盖。
        Gate.Update(enemy?.ProfileId, visible, ready, now); // 并行推进察觉和稳定计时。
        if (visible) _aimPoint = GameAdapter.Position(sight.AimPosition); // 两次原生视觉检查之间不追踪实时身体坐标。
        if (!visible && GameAdapter.IsPlayerTarget(Owner.Memory.GoalEnemy) && Owner.ShootData.Shooting) Owner.ShootData.EndShoot(); // 只停止对玩家丢失视线后的连射，不误停 AI 互战。
        _hasClue = enemy != null && Memory.TryGet(enemy.ProfileId, now, out _clue); // 优先保留当前敌人的合法观察。
        if (!_hasClue) _hasClue = Memory.TryGetLatest(now, out _clue); // 没有当前目标时使用最新声音或视觉线索。
        if (!_hasClue && now < _playerDanger.SearchUntil && Runtime.LocalPlayer != null) // 视觉记忆过期后仍可完成有限的受击区域调查。
        {
            _clue = new Observation(Runtime.LocalPlayer.ProfileId, ObservationSource.Danger, _playerDanger.Position, _playerDanger.LastDangerAt, _playerDanger.SearchUntil, Skill.HearingError); // 仅恢复事件时保存的值快照。
            _hasClue = true; // 不续期原危险的寿命。
        }
        _recoveryRunning = Owner.Medecine.Using || Owner.Medecine.FirstAid.Using || Owner.WeaponManager.Reload.Reloading; // 已开始恢复由原生动作完成。
        if ((State == BehaviorState.Investigate || State == BehaviorState.Search) && !_hasClue) FinishInvestigation(now, "clue-expired"); // 关键退出不依赖普通决策预算。
        if (_hasCover && (now >= _coverExpires || (_hasClue && (_coverThreat - ThreatPoint(now)).sqrMagnitude > 25))) // 危险区域改变后重新验证掩体。
        { _hasCover = false; _coverPath = null; NextDecision = Math.Min(NextDecision, now); }
        if (_pending && now >= _pendingDeadline) QueryFailed(now, "deadline"); // 队列丢弃过期请求后也要释放 Bot 的等待状态。
        if (_shotPending && now >= _shotDeadline) // 过期射击允许重新公平排队。
        {
            _shotPending = false; // 原有超时语义保持不变。
            if (Trace(DiagnosticEvent.ShotExpired, now)) WriteTrace("SHOT_EXPIRED", now, FormattableString.Invariant($"waitMs={(now - _shotRequestedAt) * 1000:F1}")); // 区分没有机会开火与合法射击被拦截。
        }
        if (_moving && (Owner.Position - _progressPosition).sqrMagnitude > 0.25f) { _progressPosition = Owner.Position; _lastProgress = now; } // 用实际位移判定进度。
        if (_moving && now - _lastProgress > 2.5 && (Owner.Position - _destination).sqrMagnitude > 1.44f) // 门口或不可达点不能无限保持移动意图。
        {
            if (Trace(DiagnosticEvent.Stuck, now)) WriteTrace("STUCK", now, FormattableString.Invariant($"state={State} noProgressSeconds={now - _lastProgress:F1} distance={Vector3.Distance(Owner.Position, _destination):F1}")); // 记录回退前的真实剩余距离。
            StopMotion(); // 终止卡住路径。
            QueryFailed(now, "stuck"); // 两次失败后进入更长冷却。
        }
    }

    /// <summary>按战斗、警戒和空闲频率重选有界动作。</summary>
    internal void Decide(double now)
    {
        if (!Participating) return; // 无真人上下文时不执行自身恢复或查询，交给原生。
        if (_hasClue && _clue.Source != ObservationSource.Gunshot && _clue.Source != ObservationSource.Danger && !_visible && !Controlled && !InvestigationPolicy.InRange(Role, GameAdapter.Snapshot(Owner.Position), _clue.Position)) // 普通脚步和视觉搜索仍保持原范围。
        {
            Memory.Forget(_clue.Identity); // 已不可调查的旧快照不能反复触发接管。
            _hasClue = false; // 后续策略直接考虑原生巡逻或自身恢复。
            Runtime.Diagnostics.Count(DiagnosticEvent.ClueOutOfRange); // 与声音入库前的过滤分开计数。
        }
        _needsRecovery = !Owner.WeaponManager.HaveBullets || Owner.Medecine.FirstAid.Have2Do; // 只检查原生恢复需求。
        bool underFire = _playerDanger.IsActive(now); // AI 互射和环境伤害不刷新玩家危险。
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
            HasClue = _hasClue, Visible = _visible, Reacted = ReadyToReact(now),
            SoundOnly = _hasClue && (_clue.Source == ObservationSource.Hearing || _clue.Source == ObservationSource.Gunshot),
            NeedsRecovery = _needsRecovery, RecoveryRunning = _recoveryRunning,
            HasCover = _hasCover, AtCover = AtCover, UnderFire = underFire, LowHealth = lowHealth,
            CanMove = Role != BotRole.Marksman, ContextVersion = _contextVersion,
            PlayerDanger = underFire, AdvanceAfterDanger = _playerDanger.CanAdvance(now) && !underFire
        };
        BehaviorState next = _policy.Decide(input, Skill, now, _decisionRoll); // 状态候选数与等级无关。
        ChangeState(next, now); // 决策与调查结束复用同一任务失效路径。
        if (Controlled && _hasClue && Role != BotRole.Marksman && !_hasCover && !_pending && now >= _retryAt && (!_coverUnavailable || !underFire) && (_visible || _needsRecovery || underFire))
            Request(QueryKind.Cover, Owner.Position, now); // 只在有实际需求时查找局部掩体。
        NextDecision = now + (_visible ? 0.2 : _hasClue ? 0.5 : 2); // 所有等级共用 5/2/0.5 Hz。
    }

    /// <summary>状态改变时统一撤销旧任务，调用方负责同步纯逻辑策略。</summary>
    private void ChangeState(BehaviorState next, double now)
    {
        if (next != State) // 只在状态切换时失效旧请求。
        {
            if (Trace(DiagnosticEvent.StateChanged, now)) WriteTrace("STATE_CHANGED", now, $"from={State} to={next} visible={_visible} clue={_hasClue} recovery={_needsRecovery} cover={_hasCover} controlled={Controlled}"); // 仅记录已缓存条件。
            if (Controlled) StopMotion(); // 新状态不能继续执行旧状态的移动目标。
            State = next; // 提交给 BigBrain 的缓存状态。
            _lifecycle.InvalidateRequests(); // 使旧动作路径结果无法复用。
            Runtime.CancelQueries(Id); // 清除普通与紧急队列中的旧任务。
            Runtime.ShotQueue.Cancel(Id); // 状态切换也撤销旧射击许可。
            _shotPending = _shotPermit = false; // 不复用上一个动作的验证结果。
            _pending = false; // 解除旧动作的等待锁。
            Runtime.StateChanges++; // 汇总诊断而不逐状态刷日志。
        }
    }

    /// <summary>开始自有动作时保存原生姿态，避免沿用上一层的旧路径。</summary>
    internal void Claim()
    {
        if (!Participating || Disposed || Controlled || Owner.BotState != EBotState.Active) return; // 玩家情境退出、停用与重复接管都不能重新提交动作。
        _lifecycle.OwnResources(); // 从此必须执行一次交接清理。
        Controlled = true; // 后续结果只有持有控制权才能执行。
        _posture.Acquire(Owner.Mover.TargetPose, Time.time); // 保存目标姿态并建立本次控制周期的写入责任。
        _savedSpeed = Owner.Mover.DestMoveSpeed; // 保存原生移动速度目标。
        _savedProne = Owner.BotLay.IsLay; // 自有趴伏退出时不能改变原生原本的卧姿。
        Owner.PatrollingData.Pause(); // 暂停原生巡逻，避免它重新提交旧目的地。
        Owner.Mover.Stop(); // 取消原生上一个动作的路径。
        Owner.Mover.MovementResume(); // 不继承上一动作的短期移动暂停。
        Owner.Mover.Sprint(false); // 不在未知路线中保持冲刺。
        UpdatePosture(Time.time); // 初次受击立即压低，其他动作等待稳定意图。
        NextDecision = Math.Min(NextDecision, Time.time); // 下次共享调度立即处理当前情境。
        if (Trace(DiagnosticEvent.ControlClaimed, Time.time)) WriteTrace("CONTROL_CLAIMED", Time.time, $"state={State} generation={Generation}"); // 完成动作准备后才确认控制权已取得。
    }

    /// <summary>被抢占或层退出时停止自身控制，原生治疗与换弹不被主动取消。</summary>
    internal void Release(bool preserveNativeCombat = false)
    {
        if (!_lifecycle.TryRelease()) return; // 已清理且未重新接管或排队时立即退出。
        Runtime.CancelQueries(Id); // 普通与紧急请求统一撤销。
        Runtime.ShotQueue.Cancel(Id); // 层退出后旧射击结果不能生效。
        _shotPending = _shotPermit = false; // 清除排队与许可标志。
        Runtime.Diagnostics.Count(DiagnosticEvent.ResourceReleased); // 与停用次数及控制权交接分开计数。
        _pending = false; // 清理等待标志。
        _hasCover = false; // 不在交接后使用旧起点路径。
        _coverArrival.Reset(); // 不能把上一处掩体的到达状态传给新动作。
        _coverPath = null; // 释放路径角点数组。
        _searchIdentity = null; // 再次接管需要从实际位置重新建立搜索。
        Gate.Reset(); // 重新接管后需要重新准备射击。
        if (!Controlled) return; // 不停止属于其他层的动作。
        Controlled = false; // 先交出令牌，异常路径也不会继续写入。
        _lastExecutedState = null; // 再次接管时重新记录实际动作入口。
        if (Trace(DiagnosticEvent.ControlReleased, Time.time)) WriteTrace("CONTROL_RELEASED", Time.time, $"state={State} generation={Generation}"); // 原生抢占与正常退出都留下交接证据。
        if (Owner == null) return; // Unity 已销毁对象不能再访问。
        Owner.Mover?.Stop(); // 清除本模组留下的移动路径。
        _moving = false; // 同步本地移动状态。
        if (!preserveNativeCombat && GameAdapter.IsPlayerTarget(Owner.Memory.GoalEnemy)) // 已切换 AI 目标时不能误停其原生射击。
        {
            Owner.ShootData?.EndShoot(); // 只停止仍针对玩家的旧连射。
            Owner.AimingManager?.CurrentAiming?.LoseTarget(); // 不清除已经属于 AI 互战的瞄准。
        }
        bool restorePose = _posture.TryRelease(Owner.Mover?.TargetPose ?? float.NaN, out float savedPose); // 在取消卧姿前判断写入归属；移动器失效时只释放责任。
        RestoreProne(); // 仅撤销本模组主动设置的趴伏。
        if (restorePose) // 其他层已经改写姿态时不再覆盖它。
        {
            Owner.SetPose(savedPose); // 仅还原自己仍持有的目标姿态。
            if (Trace(DiagnosticEvent.PoseRestored, Time.time)) WriteTrace("POSE_RESTORED", Time.time, FormattableString.Invariant($"target={savedPose:F2} reason=control-released")); // 将正常控制交接和战斗内抖动分开。
        }
        Owner.SetTargetMoveSpeed(_savedSpeed); // 恢复交接前的速度参数。
    }

    /// <summary>逐帧执行缓存状态；物理、掩体和寻路不在这里展开扫描。</summary>
    internal void Execute(double now)
    {
        if (!Controlled || !Participating || Disposed || Owner.BotState != EBotState.Active) return; // 原生抢占或玩家情境结束后停止动作写入。
        if (Owner.Memory.GoalEnemy != null && !GameAdapter.IsPlayerTarget(Owner.Memory.GoalEnemy) && !_playerDanger.IsActive(now)) { LeavePlayerContext(now); return; } // 不等待下一帧才放行 AI 互战。
        if (_lastExecutedState != State) // 只在首次进入实际执行分支时记录。
        {
            _lastExecutedState = State; // 每帧持续动作不重复生成字符串。
            if (Trace(DiagnosticEvent.ActionEntered, now)) WriteTrace("ACTION_ENTERED", now, $"state={State} generation={Generation}"); // 与 STATE_CHANGED 分开证明决策已进入执行器。
        }
        switch (State) // 每帧仅执行一个当前动作。
        {
            case BehaviorState.Evade:
                Evade(now); // 紧急压低姿态、查询掩体或合法趴伏。
                break;
            case BehaviorState.Advance:
                RestoreProne(); // 前压前恢复本模组临时改变的姿态。
                Search(now); // 只接近最后危险区域，分段推进。
                if (_visible && Controlled) AimAndFire(now); // 仍需真实视线，不能朝隐藏玩家盲射。
                break;
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
        UpdatePosture(now); // 在本帧动作确定之后统一提交姿态，不由搜索、掩体和危险分支各自写入。
    }

    /// <summary>所有自有普通姿态只从此处提交，原生卧姿和已交还的控制权不被干预。</summary>
    private void UpdatePosture(double now)
    {
        if (!Controlled || !Participating || Disposed || Owner == null || Owner.BotState != EBotState.Active || Owner.BotLay.IsLay) return; // 不为非玩家情境、失活或卧姿对象争抢姿态。
        bool danger = _playerDanger.IsActive(now); // 近弹与命中共用稳定的五秒危险窗口。
        bool atCover = AtCover && !_moving; // 正在离开掩体时按当前移动状态处理，短暂停止仍有稳定窗口保护。
        float previous = Owner.Mover.TargetPose; // 比较目标姿态而非正在插值变化的实际身体高度。
        if (!_posture.TryApply(State, danger, _recoveryRunning, atCover, previous, now, out float target)) return; // 重复意图或尚未稳定时不调用原生接口。
        Owner.SetPose(target); // 危险期间移动与静止均维持同一低姿态。
        if (Trace(DiagnosticEvent.PoseChanged, now)) WriteTrace("POSE_CHANGED", now, FormattableString.Invariant($"from={previous:F2} target={target:F2} state={State} danger={danger} moving={_moving} atCover={atCover}")); // 只对真实目标写入计数，日志限频后才格式化。
    }

    /// <summary>保留原生散布、后坐力和武器就绪检查，只控制合法目标与开火时间。</summary>
    private void AimAndFire(double now)
    {
        EnemyInfo? enemy = Owner.Memory.GoalEnemy; // 每次开火前重新核对当前目标。
        if (!GameAdapter.IsPlayerTarget(enemy) || !_visible || !TryVisibleObservation(enemy, now, out Observation sight) || enemy?.CanShoot != true || _recoveryRunning) // 同时验证真人目标、合法部位与最新观察。
        { Runtime.Diagnostics.Count(DiagnosticEvent.AimBlocked); StopAim(); return; } // 该计数也包含目标不能射击或恢复锁，并非都代表失去视线。
        _aimPoint = GameAdapter.Position(sight.AimPosition); // 目标切换后不能沿用上一敌人的瞄准点。
        var aiming = Owner.AimingManager.CurrentAiming; // 使用原生瞄准系统。
        aiming.SetTarget(_aimPoint); // 输入仅来自本 Bot 可见部位的快照。
        aiming.NodeUpdate(); // 原生稳定、姿态和后坐力继续工作。
        if (!aiming.IsReady) Runtime.Diagnostics.Count(DiagnosticEvent.AimNotReady); // 区分原生瞄准未完成与 Shoot 入口被拦截。
        if (ReadyToReact(now) && aiming.IsReady && enemy!.CanShoot && !Owner.ShootData.Shooting && now >= _nextShotAttempt) // 近枪可跳过等级等待，但原生条件保持。
        {
            _nextShotAttempt = now + 0.05; // 失败验证最多每秒二十次尝试，不每帧消耗射线。
            Owner.ShootData.Shoot(); // Harmony 射击入口统一做最终预算内验证。
        }
    }

    /// <summary>只消费新鲜且未使用的射击许可；需要验证时进入公平队列。</summary>
    internal bool AllowShot(double now, int frame)
    {
        if (Disposed || Owner.BotState != EBotState.Active) return RejectShot(DiagnosticEvent.ShotInactive); // 停用后不能通过原生层回调重新排队。
        EnemyInfo? enemy = Owner.Memory.GoalEnemy; // 不信任动作开始时的旧目标。
        if (!GameAdapter.IsPlayerTarget(enemy)) return true; // 防御性旁路，AI 互射永远不领取本模组射击许可。
        bool visible = TryVisibleObservation(enemy, now, out _); // 必须有当前个人视觉快照。
        bool ready = Owner.WeaponManager.IsWeaponReady && !Owner.WeaponManager.Reload.Reloading && Owner.WeaponManager.HaveBullets; // 不修改原生限制。
        Gate.Update(enemy?.ProfileId, visible, ready, now); // 原生层调用射击时也遵守相同反应门槛。
        if (!visible) return RejectShot(DiagnosticEvent.ShotNoSight); // 每次拒绝只归入首个不满足条件。
        if (!enemy!.CanShoot) return RejectShot(DiagnosticEvent.ShotCannotShoot); // 保留原生合法部位检查的顺序。
        if (!ready) return RejectShot(DiagnosticEvent.ShotNotReady); // 不改变武器未就绪时的处理。
        if (!ReadyToReact(now)) return RejectShot(DiagnosticEvent.ShotReacting); // 玩家近枪豁免新增等待，其他情境仍使用等级曲线。
        if (Owner.ShootData.Shooting) return true; // 已开始的连射由逐帧视线检查负责停止。
        if (now <= Owner.ShootData.nextFingerDownCan) return RejectShot(DiagnosticEvent.ShotCooldown); // 原生射击冷却期间不浪费验证额度。
        Vector3 currentOrigin = Owner.WeaponRoot.position; // 检查上次验证后武器是否明显移动。
        Vector3 currentPoint = Owner.AimingManager.CurrentAiming.EndTargetPoint; // 同时核对瞄准点变化。
        if (_shotPermit && now < _shotPermitUntil && _shotIdentity == enemy.ProfileId &&
            (currentOrigin - _shotOrigin).sqrMagnitude < 0.0625f && (currentPoint - _shotPoint).sqrMagnitude < 0.25f) // 不复用旧目标或大幅偏移的验证。
        {
            _shotPermit = false; // 一个许可最多启动一次连射。
            Runtime.Diagnostics.Count(DiagnosticEvent.ShotAllowed); // 这里只表示本插件允许，原生结果由 Postfix 另记。
            return true;
        }
        if (_shotPermit) Runtime.Diagnostics.Count(DiagnosticEvent.ShotPermitStale); // 到期、位移过大或目标变化造成的许可失效。
        _shotPermit = false; // 过期或偏移后清除旧许可。
        if (_shotPending && now < _shotDeadline) return false; // 不因每帧调用而反复插队。
        _shotIdentity = enemy.ProfileId; // 记录请求对应的目标身份。
        _shotRequestedAt = now; // 用于测量真实排队等待时间。
        _shotDeadline = now + 0.2; // 射击确认采用短有效期。
        var request = new WorkRequest(Id, Generation, QueryKind.Shot, GameAdapter.Snapshot(currentPoint), GameAdapter.Snapshot(currentOrigin), _shotDeadline); // 队列只持有值快照。
        _shotPending = Runtime.EnqueueShot(request, now); // 所有 Bot 在共享队列中轮转。
        if (_shotPending) _lifecycle.OwnResources(); // 原生层没有取得自有控制权时也可能提交射击验证。
        Runtime.Diagnostics.Count(_shotPending ? DiagnosticEvent.ShotQueued : DiagnosticEvent.ShotQueueRejected); // 高频排队只累计，不输出每次扳机尝试。
        return false; // 由后续帧取得许可后再调用原生 Shoot。
    }

    /// <summary>轮到本 Bot 后进行两次有预算的物理验证，并发放短期单次许可。</summary>
    internal bool VerifyShot(in WorkRequest request, double now, int frame)
    {
        EnemyInfo? enemy = Owner.Memory.GoalEnemy; // 执行前再次确认目标。
        if (Owner.BotState != EBotState.Active || !GameAdapter.IsPlayerTarget(enemy) || !_shotPending || request.Generation != Generation || enemy == null || enemy.ProfileId != _shotIdentity || !TryVisibleObservation(enemy, now, out _) || !enemy.CanShoot)
        { Runtime.Diagnostics.Count(DiagnosticEvent.ShotInvalidated); _shotPending = false; return true; } // 目标改变或失去视线后丢弃请求。
        if (!Runtime.Rays.TryTake(now, frame, 2)) return RejectShot(DiagnosticEvent.ShotBudgetWait); // 记录等待调用次数，不误称唯一请求数量。
        _shotPending = false; // 本次已有实际验证结果，不再无限等待。
        Runtime.MaxShotWait = Math.Max(Runtime.MaxShotWait, now - _shotRequestedAt); // 记录等待峰值，不能用静态延迟冒充实际响应。
        Vector3 from = Owner.WeaponRoot.position; // 从枪口附近检查实际射击线。
        Vector3 target = Owner.AimingManager.CurrentAiming.EndTargetPoint; // 使用原生最终瞄准点，包括散布和后坐力。
        if (!ThreatMemory.Finite(GameAdapter.Snapshot(target))) { Runtime.Diagnostics.Count(DiagnosticEvent.ShotInvalidPoint); return true; } // 无效坐标不能进入物理 API，也不发放许可。
        Runtime.RayCalls++; // 实际 API 次数与领取令牌数分别统计。
        if (Physics.Linecast(from, target, LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore)) // 阻止对实心障碍开火。
        {
            if (Trace(DiagnosticEvent.ShotWorldBlocked, now)) WriteTrace("SHOT_BLOCKED", now, "reason=world-obstacle"); // 只输出限频的几何阻挡明细。
            return true;
        }
        Vector3 direction = target - from; // 第二条检测只查询前方人员。
        float length = direction.magnitude; // 保存长度用于规范化和射线范围。
        if (length < 0.1f) { Runtime.Diagnostics.Count(DiagnosticEvent.ShotInvalidPoint); return true; } // 退化零长度方向不射击。
        direction /= length; // 避免归一化零向量。
        float offset = Mathf.Min(0.5f, length * 0.25f); // 尽量避开自己的身体碰撞体。
        Runtime.RayCalls++; // 只有实际执行第二条查询才计数。
        if (Physics.SphereCast(from + direction * offset, 0.12f, direction, out RaycastHit hit, length - offset, LayersMaskController.PlayerMask, QueryTriggerInteraction.Ignore)) // 小半径保护枪线附近队友。
        {
            Player? player = Owner.ShootData.GetPlayerByCollider(hit.collider); // 复用游戏的碰撞体映射。
            if (player != null && player.ProfileId != Owner.ProfileId && player.ProfileId != enemy.ProfileId) // 不向任何夹在线路中的其他人开火。
            {
                if (Trace(DiagnosticEvent.ShotPlayerBlocked, now)) WriteTrace("SHOT_BLOCKED", now, "reason=other-player-in-line"); // 不输出其他玩家身份。
                return true;
            }
        }
        _shotOrigin = from; // 保存本次实际验证起点。
        _shotPoint = target; // 保存本次实际验证终点。
        _shotPermitUntil = now + 0.1; // 结果只能在很短时间内使用。
        _shotPermit = true; // 正式发放一次新连射许可。
        Runtime.Diagnostics.Count(DiagnosticEvent.ShotValidated); // 几何通过不代表原生已扣动扳机。
        return true;
    }

    /// <summary>停止当前连射和瞄准，观察方向由后续搜索动作单独设置。</summary>
    private void StopAim()
    {
        if (Owner.Memory.GoalEnemy != null && !GameAdapter.IsPlayerTarget(Owner.Memory.GoalEnemy)) return; // 不在混战交接时取消原生对 AI 的瞄准。
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
        RestoreProne(); // 有可达掩体后先解除本模组自己的临时卧姿。
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
        if (!_hasClue) { FinishInvestigation(now, "clue-expired"); return; } // 无线索立即停止自有搜索。
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
            _searchSegmentFinal = false; // 上一个区域的末段不能算作本区域已到达。
            float angle = _random.Next01() * Mathf.PI * 2; // 一次生成后续候选方向。
            Vector3 lateral = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * Mathf.Clamp(_clue.Uncertainty, 2, 6); // 候选范围保持有界。
            _searchPoints[0] = anchor; // 首先检查线索中心。
            _searchPoints[1] = anchor + lateral; // 第二点检查一个侧面。
            _searchPoints[2] = anchor - lateral; // 第三点检查相反侧面。
            StopMotion(); // 不沿用上一线索的路径。
        }
        bool extended = _clue.Source == ObservationSource.Gunshot || _clue.Source == ObservationSource.Danger || State == BehaviorState.Advance; // 枪声和受击调查不受原脚步半径截断。
        if (_searchIndex >= 3 || (!extended && !InvestigationPolicy.InRange(Role, GameAdapter.Snapshot(_searchStart), _clue.Position))) // 普通搜索继续保留固定半径。
        {
            Memory.Forget(_clue.Identity); // 不能通过重新读取延长记忆。
            _hasClue = false; // 结束当前线索的调查。
            FinishInvestigation(now, _searchIndex >= 3 ? "points-exhausted" : "range-limit"); // 本次就交还控制，不等待下一次普通决策。
            return;
        }
        Vector3 point = _searchPoints[_searchIndex]; // 本轮最多只有三个候选。
        bool reachedStep = _moving && (Owner.Position - _destination).sqrMagnitude <= 1.44f; // 分段路径到达不等于已经到达完整调查点。
        if (reachedStep) StopMotion(); // 完成一个有限步长后允许排队下一段。
        if ((Owner.Position - point).sqrMagnitude < 2.25f || (reachedStep && _searchSegmentFinal) || _arrivedAt > 0) // 末段合法导航投影也算到达，避免墙边重复提交相同路线。
        {
            StopMotion(); // 清理已完成的移动。
            if (_arrivedAt == 0) _arrivedAt = now; // 只在首次到达时开始驻留。
            if (now - _arrivedAt >= 1) { _searchIndex++; _arrivedAt = 0; _searchSegmentFinal = false; } // 满足驻留时间后才继续搜索。
        }
        else if (!_moving && !_pending && now >= _retryAt) // 每次只提交一个有限步长的请求。
        {
            Vector3 step = GameAdapter.Position(PlayerThreatPolicy.NextSearchStep(GameAdapter.Snapshot(Owner.Position), GameAdapter.Snapshot(point))); // 十二米以内直接进入最后一段。
            _searchSegmentFinal = (step - point).sqrMagnitude < 0.0001f; // 中间段到达不能跳过剩余距离。
            Request(QueryKind.Move, step, now); // 导航投影仍由共享查询负责。
        }
    }

    /// <summary>线索过期、超距或搜索结束时立即退出；原生已经开始的恢复继续执行。</summary>
    private void FinishInvestigation(double now, string reason)
    {
        if (State != BehaviorState.Investigate && State != BehaviorState.Search && State != BehaviorState.Advance) return; // 同帧重复回调不能再次清理。
        if (Trace(DiagnosticEvent.SearchFinished, now)) WriteTrace("SEARCH_FINISHED", now, $"reason={reason} pointIndex={_searchIndex}"); // 区分记忆过期与范围边界。
        BehaviorState next = _recoveryRunning ? BehaviorState.Recover : BehaviorState.Native; // 不取消已有治疗或换弹。
        _policy.Reset(next); // 清除原搜索状态的最短承诺。
        _playerDanger.Clear(); // 完成推进后不能因旧危险寿命尚未结束而重新搜索。
        ChangeState(next, now); // 立即取消查询和停止本模组的移动。
        if (next == BehaviorState.Native) Release(); // 本轮执行后就解除控制，不额外等待预算。
        NextDecision = Math.Min(NextDecision, now); // 后续恢复需求和其他记忆仍可在共享轮转中处理。
    }

    /// <summary>玩家危险期间先压低姿态，有掩体则移动；确认查询失败才尝试原生合法趴伏。</summary>
    private void Evade(double now)
    {
        if (!_playerDanger.IsActive(now)) { NextDecision = Math.Min(NextDecision, now); return; } // 安静满五秒后由决策评估恢复或推进。
        StopAim(); // 避险不继续对隐藏玩家精确瞄准。
        if (_recoveryRunning) { StopMotion(); return; } // 不取消已有医疗或换弹，姿态由统一入口处理。
        if (_hasCover && !AtCover) { MoveToCover(now); return; } // 已验证路径优先，不重复找掩体。
        StopMotion(); // 原地压低姿态时不沿用搜索路线。
        if (AtCover) return; // 已到掩体不额外趴下。
        if (Role != BotRole.Marksman && !_pending && !_coverUnavailable && now >= _retryAt) Request(QueryKind.Cover, Owner.Position, now); // 狙击模板不离岗，其他角色优先查询。
        if ((!_coverUnavailable && Role != BotRole.Marksman) || now < _nextProneCheck) return; // 额度等待或请求超时不能被误称为无掩体。
        _nextProneCheck = now + 1; // 原生姿态合法性检查最多每秒一次。
        if ((Owner.Position - ThreatPoint(now)).sqrMagnitude < 64 || Owner.BotLay.IsLay) return; // 贴脸时不强制趴伏，已有卧姿不重复设置。
        if (!Owner.GetPlayer.MovementContext.CanProne) return; // 使用原生地形和身体条件，不能强行穿入障碍。
        Owner.BotLay.IsLay = true; // 通过原生卧姿控制器维护相关事件和动作状态。
        _ownsProne = !_savedProne; // 只对自己新增的姿态承担恢复责任。
        if (Trace(DiagnosticEvent.DangerProne, now)) WriteTrace("DANGER_PRONE", now, "reason=no-validated-cover"); // 单独证明已执行合法趴伏。
    }

    /// <summary>退出危险或开始移动时撤销自有趴伏，不取消接管前就存在的原生卧姿。</summary>
    private void RestoreProne()
    {
        if (!_ownsProne) return; // 原生原有姿态不由本模组覆盖。
        _ownsProne = false; // 异常路径不重复改变身体状态。
        if (Owner != null && Owner.BotLay != null) // 对象失效时不能再调用姿态接口。
        {
            Owner.BotLay.IsLay = false; // 通过原生接口同步取消旋转限制订阅。
            _posture.ResumeAfterProne(Owner.Mover.TargetPose); // 原生取消卧姿会抬高目标，下一次统一姿态处理必须感知这一变化。
        }
    }

    /// <summary>安全条件成立时调用原生恢复接口，失败重试有冷却。</summary>
    private void Recover(double now)
    {
        if (_recoveryRunning || now < _nextRecovery || (_visible && !AtCover)) return; // 不打断恢复，也不在暴露位置反复用药。
        _nextRecovery = now + 2; // 背包无药或弹药不足时限制尝试频率。
        if (!Owner.WeaponManager.HaveBullets) // 由原生接口处理弹匣与手部状态。
        {
            Owner.WeaponManager.Reload.TryReload(); // 保留原调用与尝试频率。
            if (Trace(DiagnosticEvent.RecoveryRequested, now)) WriteTrace("RECOVERY_REQUESTED", now, $"kind=reload running={Owner.WeaponManager.Reload.Reloading}"); // 发起请求不等价于动作已完成。
        }
        else if (Owner.Medecine.FirstAid.ShallStartUse()) // 先验证可使用物品与身体部位。
        {
            Owner.Medecine.FirstAid.ApplyToSelf(); // 急救仍由原生执行。
            if (Trace(DiagnosticEvent.RecoveryRequested, now)) WriteTrace("RECOVERY_REQUESTED", now, $"kind=first-aid running={Owner.Medecine.FirstAid.Using}"); // 不宣称治疗结果已经成功。
        }
    }

    /// <summary>为当前动作创建带截止时间的唯一查询。</summary>
    private void Request(QueryKind kind, Vector3 position, double now)
    {
        if (!Participating) return; // 来源已交回原生时不能重新入队。
        var request = new WorkRequest(Id, Generation, kind, GameAdapter.Snapshot(position), GameAdapter.Snapshot(ThreatPoint(now)), now + (State == BehaviorState.Evade ? 2.5 : 1)); // 紧急多阶段查询最多等待二点五秒。
        if (!Runtime.Enqueue(request, now)) // 队列满时推迟可选工作。
        {
            _retryAt = now + 0.5; // 保持原重试间隔。
            if (Trace(DiagnosticEvent.QueryRejected, now)) WriteTrace("QUERY_REJECTED", now, $"kind={kind} reason=queue-rejected"); // 区分未入队和后续导航失败。
            return;
        }
        Query.Begin(request); // 初始化本次有界分步查询。
        _pending = true; // 同一 Bot 不重复提交昂贵查询。
        _pendingKind = kind; // 超时与无掩体回退需要知道具体任务种类。
        _pendingDeadline = request.Deadline; // 用于清理被队列超时丢弃的任务。
        Runtime.Diagnostics.Count(DiagnosticEvent.QueryQueued); // 普通排队也保留总量。
    }

    /// <summary>查询执行前检查生命周期、动作代次与控制权。</summary>
    internal bool Accepts(in WorkRequest request)
    {
        if (Disposed || Owner == null || !Controlled || !Participating) return false; // 对象销毁或已经交回控制时不再访问游戏记忆。
        bool playerContext = Participating && (_playerDanger.IsActive(Time.time) || Owner.Memory.GoalEnemy == null || GameAdapter.IsPlayerTarget(Owner.Memory.GoalEnemy)); // 目标已切成 AI 时拒绝旧玩家任务。
        return !Disposed && playerContext && Controlled && _pending && request.Owner == Id && request.Generation == Generation && Owner.BotState == EBotState.Active &&
            (request.Kind != QueryKind.Cover || ((_hasClue || _playerDanger.IsActive(Time.time)) && System.Numerics.Vector3.DistanceSquared(request.Threat, GameAdapter.Snapshot(ThreatPoint(Time.time))) <= 25)); // 查询结果必须对应当前危险区域。
    }

    /// <summary>验证观察身份、来源和时间，视觉检查之外不读取隐藏身体位置。</summary>
    internal bool TryVisibleObservation(EnemyInfo? enemy, double now, out Observation observation)
    {
        observation = default; // 失败时不给出伪造瞄准坐标。
        return GameAdapter.IsPlayerTarget(enemy) && GameAdapter.DirectlyVisible(enemy, now) && Memory.TryGet(enemy!.ProfileId, now, out observation) &&
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
        if (Trace(DiagnosticEvent.QuerySucceeded, now)) WriteTrace("QUERY_SUCCEEDED", now, $"kind={request.Kind} corners={path.Length} generation={Generation}"); // 完整路径通过才记录成功。
        if (request.Kind == QueryKind.Cover) // 掩体结果先交给决策层，不强制立即移动。
        {
            if (!_hasCover || (_cover - point).sqrMagnitude > 0.01f) _coverArrival.Reset(); // 新位置不能继承旧掩体的到达缓冲。
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
    internal void QueryFailed(double now, string reason)
    {
        switch (reason) // 明细被限频时仍能根据累计原因判断瓶颈。
        {
            case "deadline": Runtime.Diagnostics.Count(DiagnosticEvent.QueryDeadline); break; // 排队或多阶段执行超过有效期。
            case "no-candidates": Runtime.Diagnostics.Count(DiagnosticEvent.QueryNoCandidates); break; // 局部场景没有合适候选。
            case "nav-sample": Runtime.Diagnostics.Count(DiagnosticEvent.QueryNavSample); break; // 候选无法投影到导航网格。
            case "no-occlusion": Runtime.Diagnostics.Count(DiagnosticEvent.QueryNoOcclusion); break; // 候选没有阻挡已知威胁。
            case "path-incomplete": Runtime.Diagnostics.Count(DiagnosticEvent.QueryPathIncomplete); break; // 路径不可达或只返回部分路径。
            case "path-corners": Runtime.Diagnostics.Count(DiagnosticEvent.QueryPathCorners); break; // 角点数量超出合理范围。
            case "path-bounds": Runtime.Diagnostics.Count(DiagnosticEvent.QueryPathBounds); break; // 路径绕行过长或终点偏离。
        }
        Runtime.CancelQueries(Id); // 清除普通与紧急队列中的同一请求。
        if (State == BehaviorState.Evade && _pendingKind == QueryKind.Cover && reason != "deadline" && reason != "stuck") _coverUnavailable = true; // 实际候选验证失败才允许无掩体趴伏。
        _pending = false; // 防止超时造成永久等待。
        _queryFailures++; // 统计当前目标的连续失败。
        _retryAt = now + (_queryFailures >= 2 ? 5 : 0.8); // 达到上限后使用更长冷却。
        Runtime.FailedQueries++; // 诊断失败和排队成功分开计算。
        if (Trace(DiagnosticEvent.QueryFailed, now)) WriteTrace("QUERY_FAILED", now, FormattableString.Invariant($"reason={reason} state={State} failures={_queryFailures} retrySeconds={_retryAt - now:F1}")); // 限频前累计失败次数，明细给出本次原因。
        if (_queryFailures >= 2) // 无法到达时允许行为继续而不是无限重算。
        {
            _hasCover = false; // 放弃不可用掩体。
            _coverPath = null; // 释放失败缓存。
            if (State == BehaviorState.Search || State == BehaviorState.Investigate || State == BehaviorState.Advance) { _searchIndex++; _arrivedAt = 0; _searchSegmentFinal = false; } // 推进也必须能放弃不可达候选。
            _queryFailures = 0; // 冷却后下一目的地可重新验证。
        }
        NextDecision = Math.Min(NextDecision, now); // 让状态机考虑回退。
    }

    /// <summary>把完整路径直接交给原生移动器，避免 GoToPoint 的重复寻路。</summary>
    private void StartPath(Vector3[] path, Vector3 point, double now)
    {
        if (path.Length < 2 || !Controlled) return; // 退化路径和失去控制时禁止移动。
        RestoreProne(); // 使用新路径前恢复自己施加的卧姿。
        Owner.SetTargetMoveSpeed(Role == BotRole.Scav ? 0.8f : 1f); // 角色固定速度，不随等级增加执行频率。
        Owner.Mover.GoToByWay(path, 0.6f); // 已计算路径直接设置到原生路径控制器。
        _destination = point; // 保存有界的移动目标。
        _progressPosition = Owner.Position; // 用真实位移检测卡住。
        _lastProgress = now; // 开始本次移动超时计时。
        _moving = true; // 标记此路径由本模组持有。
        UpdatePosture(now); // 新路径不能无条件抬高姿态，避险中重规划保持蹲伏。
        if (Trace(DiagnosticEvent.MoveStarted, now)) WriteTrace("MOVE_STARTED", now, FormattableString.Invariant($"state={State} corners={path.Length} distance={Vector3.Distance(Owner.Position, point):F1}")); // 证明已向原生提交路径，不把查询完成等同于移动完成。
    }

    /// <summary>注销时撤销请求和动作，清理失败也不阻止释放索引。</summary>
    public void Dispose()
    {
        if (Disposed) return; // 保证重复销毁安全。
        try { Release(); } // 尽量恢复原生动作参数。
        catch (Exception exception) { Runtime.Log.LogWarning($"Bot {Id} 清理时对象已失效：{exception.Message}"); } // 销毁过程仅记录一次。
        finally { Disposed = true; } // Release 已在游戏对象访问之前撤销队列，重复扫描没有必要。
    }
}
