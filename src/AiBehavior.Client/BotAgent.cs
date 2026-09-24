using System;
using AiBehavior.Core;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;
using static EFT.InventoryLogic.Weapon;

namespace AiBehavior.Client;

/// <summary>单个 Bot 的值记忆、状态机和动作控制，不创建独立 Update 组件。</summary>
internal sealed class BotAgent : IDisposable
{
    private enum RepositionPurpose { BlockedShot, OpenFight, CloseRange }
    private enum FireWaitCause { None, Reaction, NativeAim, NoShootablePart, VisionStale, Recovery, Evasion }
    internal readonly RaidRuntime Runtime;
    internal readonly BotOwner Owner;
    internal readonly int Id;
    internal readonly BotRole Role;
    internal readonly SkillProfile Skill;
    internal readonly CombatTemperament Temperament;
    internal readonly ThreatMemory Memory = new();
    internal readonly ReactionGate Gate = new();
    internal readonly BotQuery Query;
    internal readonly SearchRoute SearchRoute = new();
    private readonly SearchProgress _searchProgress = new();
    internal readonly double[] NextDiagnosticAt = new double[(int)DiagnosticEvent.Count];
    private BehaviorState? _lastExecutedState;
    private readonly BotRandom _random;
    private readonly DecisionPolicy _policy = new();
    private readonly BotLifecycle _lifecycle = new();
    private readonly PlayerDanger _playerDanger = new();
    private readonly PosturePolicy _posture = new();
    private readonly CoverArrival _coverArrival = new();
    private readonly SuppressionPressure _pressure = new();
    private readonly FailedCoverMemory _failedCovers = new();
    private readonly SearchPacing _searchPacing = new();
    private readonly EscapeFallback _escapeFallback = new();
    private readonly BlockedShotFeedback _blockedShots = new();
    private readonly SearchFailureMemory _failedSearch = new();
    private readonly RecentSightAwareness _recentSight = new();
    private readonly EngagementFootwork _footwork;
    private bool _watchLookIssued;
    private bool _ambushLookIssued;
    private bool _coverShiftRequested;
    private bool _coverShiftCommitted;
    private double _coverShiftObservationAt = double.NegativeInfinity;
    private double _nextGrenadeAt;
    private double _grenadeObservationAt = double.NegativeInfinity;
    private double _visibleSince = double.NegativeInfinity;
    private FireWaitCause _reportedFireWait;
    private bool _fireReadyReported;
    private bool _exposedDefenseActive;
    private bool _squadMayAdvance = true;
    private Weapon? _fireModeWeapon;
    private EFireMode _originalFireMode;
    private EFireMode _appliedFireMode;
    private double _nextFireModeCheck;
    private object? _aimModeController;
    private BotAimingData? _aimModeData;
    private bool _originalAimMode;
    private bool _appliedAimMode;
    private bool _originalHardAim;
    private double _nextAimModeCheck;
    private bool _aimModeFailed;
    private RepositionPurpose _repositionPurpose;
    private PressureLevel _reportedPressure;
    private double _nextSoundSnapshot;
    private double _nextGunshotSnapshot;
    private double _nextNearBullet;
    private double _nextProneCheck;
    private double _combatMemoryUntil;
    private bool _coverUnavailable;
    private bool _ownsProne;
    private bool _savedProne;
    private Vector3 _dangerOrigin;
    internal bool Participating;
    private Observation _clue;
    private Observation _lastGunshot;
    private Vector3 _gunshotOrigin;
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
    private IBotAiming? _clearedAiming;
    private bool _aimDirty;
    private double _releasedAt;
    private double _nextControlCheck;
    internal bool LayerSelected;
    private double _arrivedAt;
    private bool _searchSegmentFinal;
    private bool _searchAreaHeld;
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
        Temperament = CombatAdaptation.Temperament(role, role == BotRole.Pmc && runtime.Options.CombatTemperament ? _random.Next01() : 0.5f); // 功能关闭时不消费旧决策随机流，普通 Scav 固定均衡。
        _footwork = new EngagementFootwork(id & 1); // 固定左右倾向，不随帧率或等级增加查询次数。
        Query = new BotQuery(this);
        NextDecision = Time.time + id % 10 * 0.01;
    }

    /// <summary>只在已产生自有决策且原生未锁定投掷动作时申请控制。</summary>
    internal bool ShouldControl => Participating && !Disposed && Owner != null && !Owner.IsDead && Owner.BotState == EBotState.Active &&
        State != BehaviorState.Native && Owner.WeaponManager?.Grenades?.ThrowindNow != true;

    /// <summary>活动限额仅优先已有玩家事件的 Bot，停用期间保留的有限快照也可在下一轮原生调度恢复。</summary>
    internal bool HasPlayerActivity(double now)
    {
        if (Disposed || Owner == null || Owner.IsDead || Runtime.LocalPlayer?.HealthController?.IsAlive != true) return false; // 死亡和结束战局不保留优先权。
        bool danger = _playerDanger.IsActive(now); // 只有真人危险能短暂打断 AI 互战。
        EnemyInfo? enemy = Owner.Memory.GoalEnemy; // 不扫描所有目标。
        if (enemy != null && !GameAdapter.IsPlayerTarget(enemy) && !danger) return false; // 原生 AI 互战排序不受本模组改变。
        return danger || _lastGunshot.ExpiresAt > now || Memory.TryGetLatest(now, out _); // 读取快照不会延长寿命。
    }

    /// <summary>近距枪声与已就绪再露头只豁免重复的模组等待，原生瞄准、枪线和武器条件仍独立验证。</summary>
    private bool ReadyToReact(double now)
    {
        EnemyInfo? enemy = Owner.Memory.GoalEnemy; // 熟悉优势只作用于当前真正可见的本机真人。
        bool primed = Controlled && LayerSelected && _visible && GameAdapter.IsPlayerTarget(enemy) && _recentSight.IsPrimed(enemy!.ProfileId); // 再发现优势只能用于本插件实际控制的当前真人目标。
        float factor = primed ? 0.8f : 1f; // 曾短暂看见却未完成准备的 Bot 仍遵守八成等级等待。
        bool readyOnRepeek = Runtime.Options.FastRepeekFire && primed && _recentSight.CanFireOnRepeek(enemy!.ProfileId); // 遮挡前已经就绪时才省略重复的模组等待。
        return now < _playerDanger.CloseAlertUntil || Gate.CanFire(Skill, now, factor, readyOnRepeek); // 原生瞄准和后续枪线验证仍单独生效。
    }

    /// <summary>紧急掩体使用最后危险方向，普通战斗使用合法观察快照。</summary>
    private Vector3 ThreatPoint(double now)
    {
        return _playerDanger.IsActive(now) ? GameAdapter.Position(_playerDanger.Position) : GameAdapter.Position(_clue.Position);
    }

    /// <summary>共用带距离缓冲的掩体到达判断，避免边缘位移反复触发走动和蹲起。</summary>
    private bool AtCover => _coverArrival.Update(_hasCover, _hasCover ? (Owner.Position - _cover).sqrMagnitude : 0);

    /// <summary>只有仍向同一有效掩体前进的自有路线可以保持，不能把调查移动当作掩体移动。</summary>
    private bool MovingToCover => Controlled && LayerSelected && _moving && _hasCover && !AtCover &&
        (_pendingKind == QueryKind.Cover || _pendingKind == QueryKind.Move) && (_destination - _cover).sqrMagnitude <= 0.01f;

    /// <summary>固定四项的失效点检查，采样前后与接收路径共用相同规则。</summary>
    internal bool RejectsCover(Vector3 point, double now)
    {
        if (_coverShiftRequested && _hasCover && (point - _cover).sqrMagnitude < 9) return true; // 换位候选必须实际离开当前旧掩体。
        return Runtime.Options.CoverFailureMemory && _failedCovers.Rejects(GameAdapter.Snapshot(point), now); // 玩家打穿的点仍使用原有短时失效记忆。
    }

    /// <summary>衰减个人压力并只记录等级边沿，不为每颗子弹写日志或改动原生属性。</summary>
    private void UpdatePressure(double now)
    {
        if (!Runtime.Options.SuppressionResponse) return; // 关闭时不执行新增压力更新。
        _pressure.Advance(now); // 常数时间，五秒安静窗口不被压力计时延长。
        if (_reportedPressure == _pressure.Level) return; // 连续相同压力只保留标量。
        PressureLevel previous = _reportedPressure; // 明细描述真正的状态变化。
        _reportedPressure = _pressure.Level; // 即使日志限频也提交边沿。
        if (Trace(DiagnosticEvent.PressureChanged, now)) WriteTrace("PRESSURE_CHANGED", now, FormattableString.Invariant($"from={previous} to={_pressure.Level} value={_pressure.Value:F2}")); // 通过全局额度后才格式化。
    }

    /// <summary>玩家在 Bot 已到达的掩体处命中时，封锁失效点并撤销通往它的旧路径。</summary>
    private void RejectHitCover(double now)
    {
        if (!Runtime.Options.CoverFailureMemory || !Controlled || !LayerSelected || !_hasCover || !AtCover) return; // 移动途中受击和原生控制期不能证明候选本身失效。
        if (!_failedCovers.ObserveHit(true, true, GameAdapter.Snapshot(_cover), now)) return; // 仅从已核验玩家命中的入口调用。
        _hasCover = false; // 先撤销到达与还击资格，不能继续将此点用于恢复。
        _coverPath = null; // 旧数组不再交给原生移动器。
        _coverArrival.Reset(); // 解除同位置的距离缓冲记忆。
        StopMotion(); // 只停止本模组实际持有的移动。
        if (_pending && (_pendingKind == QueryKind.Cover || _pendingKind == QueryKind.Move)) { Runtime.CancelQueries(Id); _pending = false; } // 失效点的排队任务不能迟到复活。
        _coverUnavailable = false; // 允许用原预算查找替代点。
        _queryFailures = 0; // 新失效原因不继承旧导航失败计数。
        _retryAt = now; // 直接命中的首次替代可以尽快排队，后续仍受共享令牌限制。
        if (Trace(DiagnosticEvent.CoverInvalidated, now)) WriteTrace("COVER_INVALIDATED", now, "reason=player-hit-at-cover rejectRadius=2 lifetimeSeconds=12"); // 失效只记事件，不添加持续扫描。
    }

    /// <summary>压力反馈仅允许在有效掩体处尝试还击，所有原生视线、瞄准与枪线检查仍保留。</summary>
    private bool CanDefendFromCover(double now)
    {
        return Runtime.Options.SuppressionResponse && TacticalActionPolicy.CanReturnFire(AtCover, _moving, _visible, _recoveryRunning, _pressure.Level, now, _playerDanger.LastDangerAt);
    }

    /// <summary>掩体已证实不可用且无需等待卧姿处理时，低压 Bot 才能对真实可见玩家短暂自卫。</summary>
    private bool CanExposedReturnFire(double now)
    {
        if (!Runtime.Options.ExposedSelfDefense || !Runtime.Options.SuppressionResponse || !_coverUnavailable || _hasCover || !_visible) return false; // 只在原有掩体查询失败后的玩家避险情境考虑自卫。
        bool proneRequired = TacticalActionPolicy.CanProneAfterHit(_playerDanger.HasRecentPersonalHit(now), _coverUnavailable, _hasCover, _pending, _visible, _moving); // 玩家仍在眼前时不为姿态动作阻断还击。
        bool postureResolved = !proneRequired || Owner.BotLay.IsLay || !Owner.GetPlayer.MovementContext.CanProne; // 只有符合本人受击且失去目标的场景才等待卧姿处理。
        bool weaponReady = Owner.WeaponManager.IsWeaponReady && Owner.WeaponManager.HaveBullets && !Owner.WeaponManager.Reload.Reloading; // 无弹、切枪或换弹仍不得开火。
        return TacticalActionPolicy.CanExposedReturnFire(true, _moving, _pending, _visible, _recoveryRunning, weaponReady, postureResolved, _pressure.Level, now, _playerDanger.LastDangerAt); // 不申请额外射线或寻路额度。
    }

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
        var seenPosition = GameAdapter.Snapshot(enemy.PersonalLastPos); // 延长近距同层交战时只读取这次合法视觉快照。
        bool sustained = _visible || State == BehaviorState.Engage || State == BehaviorState.Cover || State == BehaviorState.Evade; // 一次短暂发现不自动取得长期战斗记忆。
        double memorySeconds = CombatMemoryPolicy.VisionSeconds(Role, Skill, GameAdapter.Snapshot(Owner.Position), seenPosition, sustained); // 无须新增室内检测或隐藏玩家位置读取。
        var observation = new Observation(enemy.ProfileId, ObservationSource.Vision, seenPosition,
            enemy.PersonalLastSeenTime, enemy.PersonalLastSeenTime + memorySeconds, 0, GameAdapter.Snapshot(enemy.GetPartToShoot())); // 瞄准坐标仍只在真实视觉检查完成时生成快照。
        if (Memory.Observe(observation, now)) // 只有真正写入新个人视觉的快照才能延长战斗记忆。
        {
            if (memorySeconds > Skill.MemorySeconds) _combatMemoryUntil = Math.Max(_combatMemoryUntil, observation.ExpiresAt); // 由近距同层持续视觉建立搜完区域后的短期守点期限。
            Runtime.Diagnostics.Count(DiagnosticEvent.VisionSaved); // 只统计实际写入固定记忆的次数，不逐视觉回调刷日志。
        }
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
        Vector3 soundOrigin = position; // 保存本次事件位置，不能读取射手之后的隐藏坐标。
        if (gunshot && _lastGunshot.ExpiresAt > now && now - _lastGunshot.ObservedAt <= 3 && (soundOrigin - _gunshotOrigin).sqrMagnitude <= 25) position = GameAdapter.Position(_lastGunshot.Position); // 同一区域连射沿用一次定位误差，不能每颗子弹随机重置路线。
        else
        {
            float angle = _random.Next01() * Mathf.PI * 2; // 新区域才生成新的方位误差。
            float radius = Mathf.Sqrt(_random.Next01()) * error; // 圆盘内均匀抽样，避免偏向中心。
            position += new Vector3(Mathf.Cos(angle) * radius, 0, Mathf.Sin(angle) * radius); // 后续读取只看到带误差的位置。
        }
        if (!gunshot && !InvestigationPolicy.InRange(Role, GameAdapter.Snapshot(Owner.Position), GameAdapter.Snapshot(position))) // 脚步保持局部范围，枪声使用独立的一百二十米分段。
        {
            if (Trace(DiagnosticEvent.SoundOutOfRange, now)) WriteTrace("SOUND_OUT_OF_RANGE", now, $"role={Role} action=skip-investigation"); // 只拒绝本次声音，不删除已有视觉记忆。
            return;
        }
        var observation = new Observation(source.ProfileId, gunshot ? ObservationSource.Gunshot : ObservationSource.Hearing, GameAdapter.Snapshot(position), now,
            now + (gunshot ? PlayerThreatPolicy.SearchLifetime(distance) : Math.Min(Skill.MemorySeconds, 8)), error); // 枪声线索独立限时，允许分段调查。
        if (gunshot) { _lastGunshot = observation; _gunshotOrigin = soundOrigin; } // 真实枪声保留独立寿命，不能因新鲜视觉优先而完全丢失。
        if (Memory.TryGet(source.ProfileId, now, out Observation previous) && previous.Source == ObservationSource.Vision && now - previous.ObservedAt < 0.35)
        {
            if (gunshot && Trace(DiagnosticEvent.SoundSaved, now)) WriteTrace("SOUND_SAVED", now, FormattableString.Invariant($"source=local-player kind=gunshot band={band} distance={distance:F1} error={error:F1} ttl={observation.ExpiresAt - now:F1} retainedVision=True")); // 视觉继续用于瞄准，声音仅供失去视线后调查。
            return; // 不能覆盖当前视觉造成停火。
        }
        if (Memory.Observe(observation, now)) // 合法新声音唤醒调查。
        {
            if (!gunshot) _ambushLookIssued = false; // 只有新的有效脚步线索允许再次提交一次伏击转向。
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
        if (!_playerDanger.Observe(true, GameAdapter.Snapshot(estimate), now, PlayerThreatPolicy.SearchLifetime(distance), reason == "hit")) return; // 本人命中证据与近弹、队友报告分开保存。
        if (reason == "hit") _squadMayAdvance = true; // 直接受害者在没有合格队友时保留原有五秒后推进资格。
        else if (reason == "ally-hit") _squadMayAdvance = false; // 队友先共同避险，只有稍后被选中的一人推进。
        if (Runtime.Options.SuppressionResponse) _pressure.Observe(true, reason == "hit" ? PressureEvent.Hit : reason == "near-bullet" ? PressureEvent.NearBullet : PressureEvent.AllyWarning, now); // 友方报告只维持原有危险告警，不伪造本人被压制。
        UpdatePressure(now); // 新命中或连续近弹立即更新等级。
        if (reason == "hit") RejectHitCover(now); // 只有玩家直接命中能封锁已到达的掩体。
        _searchPacing.Cancel(); // 紧急危险不等待搜索停看结束。
        _recentSight.Clear(); // 受击避险立即放弃旧出口的守点与再发现优惠。
        _watchLookIssued = false; // 下次真实失视才能开始新的守点窗口。
        if (entering) { _coverUnavailable = false; _retryAt = now; _escapeFallback.StartDanger(); } // 新一轮危险允许尽快查询一次局部掩体和有限撤离。
        _blockedShots.ClearStreak(); // 避险期间不继承交战时的连续挡枪记录。
        Memory.Observe(new Observation(Runtime.LocalPlayer.ProfileId, ObservationSource.Danger, GameAdapter.Snapshot(estimate), now, _playerDanger.SearchUntil, error), now); // 保存有限的接近区域。
        Participating = true; // 受击和近弹不等待普通调度才激活行为层。
        _policy.Reset(BehaviorState.Evade); // 紧急行为绕过普通动作承诺。
        ChangeState(BehaviorState.Evade, now); // 取消原有搜索或射击请求。
        NextDecision = now; // 下一轮继续处理资源查询与恢复条件。
        Owner.LookData.ResetUpdateTime(); // 尽快检查真实视线。
        UpdatePosture(now); // 新危险可立即压低，连续近弹不会重复与移动路线争抢姿态。
        if (Trace(DiagnosticEvent.PlayerDanger, now)) WriteTrace("PLAYER_DANGER", now, $"kind={reason} source=local-player holdSeconds=5 generation={Generation}"); // 不传播实时坐标。
    }

    /// <summary>玩家直接命中后只给被选中的一名近邻恢复推进资格，不影响全组即时避险。</summary>
    internal void SelectSquadAdvance(bool selected, double now)
    {
        if (!Participating || Disposed) return; // 未接受玩家危险的对象不能凭小队标记进入增强。
        _squadMayAdvance = selected; // 只影响五秒危险结束后的普通决策。
        NextDecision = Math.Min(NextDecision, now); // 名额变化后及时刷新决策，不发起额外导航请求。
        if (Trace(DiagnosticEvent.SquadAdvanceSelected, now)) WriteTrace("SQUAD_ADVANCE_SELECTED", now, $"selected={selected} source=ally-hit"); // 可与同组避险和后续 Advance 对照。
    }

    /// <summary>只按本 Bot 自身武器与状态判断能否成为队友受击后的单名推进者。</summary>
    internal bool CanLeadSquadAdvance => Role != BotRole.Marksman && !Disposed && !Owner.IsDead && Owner.BotState == EBotState.Active && Owner.WeaponManager.HaveBullets && !Owner.WeaponManager.Reload.Reloading && !Owner.Medecine.Using;

    /// <summary>没有真人情境或转向 AI 交战时交回原生，不停止已经属于原生目标的射击。</summary>
    private void LeavePlayerContext(double now)
    {
        if (Participating || Controlled || _shotPending || _shotPermit) // 只在真正退出时清理资源。
        {
            Release(true, "player-context-left"); // 原生互战的瞄准与扳机保持正常。
            _policy.Reset(); // 清除玩家情境的动作承诺。
            if (State != BehaviorState.Native) Runtime.StateChanges++; // 汇总仍记录实际回退。
            State = BehaviorState.Native; // BigBrain 归还原生层。
            if (Trace(DiagnosticEvent.PlayerScopeLeft, now)) WriteTrace("PLAYER_SCOPE_LEFT", now, "action=native-control"); // 可对应混战切换。
        }
        Participating = _visible = _hasClue = false; // 不把旧情境状态显示为正在增强。
        NextDecision = double.PositiveInfinity; // 等待新的合法玩家事件唤醒。
        Gate.Reset(); // 新接敌不能继承上次玩家的射击就绪。
        _searchPacing.Cancel(); // 交回 AI 互战后不保留活动停看窗口，但同区域次数不会退款。
        _pressure.Clear(); // 玩家情境退出后不继续保留受压行为。
        _blockedShots.Clear(); // 下次独立接敌可以重新判断枪线。
        _recentSight.Clear(); // 旧玩家视觉不能带入新情境。
        _footwork.Clear(); // 交还原生后不保留主动侧移名额和目标。
        _visibleSince = double.NegativeInfinity; // 旧玩家情境的暴露计时不能带入新目标。
        _reportedFireWait = FireWaitCause.None; // 下一次目视从新原因开始记录。
        _fireReadyReported = _exposedDefenseActive = false; // 交还控制后释放诊断边沿。
        _squadMayAdvance = true; // 新玩家情境重新选择队友推进名额。
        _combatMemoryUntil = 0; // 交还玩家情境后不让旧的区域守点资格影响下一次接管。
        _searchAreaHeld = false; // 下一次独立搜索需重新证明候选已完成。
        _watchLookIssued = false; // 新情境的守点必须重新确认视觉边沿。
        _ambushLookIssued = _coverShiftRequested = _coverShiftCommitted = false; // 交回原生后不保留战术动作所有权。
        _coverShiftObservationAt = _grenadeObservationAt = double.NegativeInfinity; // 新接敌重新核对真实目击时间。
        _reportedPressure = PressureLevel.Calm; // 新玩家情境的第一次高压应重新形成日志边沿。
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
                Release(reason: "bot-inactive"); // 幂等撤销资源，不再逐帧扫描两条队列。
                Gate.Reset(); // 未接管但曾运行射击门控的实例也清除就绪。
                _visible = _hasClue = _recoveryRunning = false; // 汇总和恢复不能沿用停用前的缓存结论。
                Participating = false; // 停用 Bot 不参与增强调度。
                _searchIdentity = null; // 恢复后重新验证搜索起点。
                _recentSight.Clear(); // 停用期间不能保留短时再次探头优势。
                _visibleSince = double.NegativeInfinity; // 停用前的露头时刻不能用于恢复后的延迟诊断。
                _reportedFireWait = FireWaitCause.None; // 旧等待原因不跨活动生命周期保留。
                _fireReadyReported = _exposedDefenseActive = false; // 新活动窗口独立记录真实就绪和自卫边沿。
                _footwork.Clear(); // 恢复活动后等待新的真人视觉。
                _squadMayAdvance = true; // 停用期间不保留小队推进身份。
                _watchLookIssued = false; // 恢复后重新等待真实视觉。
                _ambushLookIssued = _coverShiftRequested = _coverShiftCommitted = false; // 停用期间不能继续占有旧掩体换位或伏击。
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
        bool playerClue = Memory.TryGetLatest(now, out _) || _lastGunshot.ExpiresAt > now || _playerDanger.CanAdvance(now); // 独立保留枪声原寿命，仍不访问隐藏坐标。
        if (!PlayerThreatPolicy.ShouldManage(active, playerAlive, enemy != null && !playerTarget, playerTarget, playerClue, _playerDanger.IsActive(now))) // AI 互战只有玩家紧急危险可短时打断。
        {
            if (!playerAlive && (playerClue || _playerDanger.SearchUntil > 0)) { Memory.Clear(); _lastGunshot = default; _playerDanger.Clear(); _combatMemoryUntil = 0; _searchAreaHeld = false; _failedCovers.Clear(); _failedSearch.Clear(); _blockedShots.Clear(); _recentSight.Clear(); _footwork.Clear(); _searchPacing.Clear(); _coverShiftRequested = _coverShiftCommitted = false; } // 玩家失效时一并清理事件值缓存。
            LeavePlayerContext(now); // 不清除原生 AI 目标、不阻止原生瞄准或射击。
            return; // 不读取医疗、健康、不更新决策、不搜索掩体。
        }
        if (!Participating) // 新的真人线索首次激活时立即进入轮转。
        {
            Participating = true; // 行为层现在可以消费缓存状态。
            NextDecision = now; // 从无穷等待恢复到本帧截止。
            if (Trace(DiagnosticEvent.PlayerScopeEntered, now)) WriteTrace("PLAYER_SCOPE_ENTERED", now, "source=local-player"); // 证明不是全场 Bot 同时进入新增逻辑。
        }
        UpdatePressure(now); // 只在玩家上下文推进标量衰减，不扫描其他 Bot。
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
            if (_visible && !visible) // 只有真正失视边沿可建立下一次再露头资格。
            {
                bool readyAtLoss = ReadyToReact(now); // 在清空当前反应门之前核对 Bot 是否真的已经完成准备。
                if (_recentSight.MarkLost(now, readyAtLoss)) // 最近个人视觉仍新鲜时才允许守旧出口。
                {
                    _watchLookIssued = false; // 此次守点尚未提交转向。
                    if (Trace(DiagnosticEvent.SightWatchArmed, now)) WriteTrace("SIGHT_WATCH_ARMED", now, "watchSeconds=1.25 repeekSeconds=8"); // 设定不等于已取得层控制。
                }
                else _recentSight.Clear(); // 视觉证据已经过期时连同快速开火资格一起作废。
            }
            if (!_visible && visible && _recentSight.TryReacquire(enemy?.ProfileId, sight.Position, now) && Trace(DiagnosticEvent.RepeekRecognized, now)) WriteTrace("REPEEK_RECOGNIZED", now, $"sameAreaMeters=6 readyAtLoss={_recentSight.CanFireOnRepeek(enemy?.ProfileId)}"); // 再次探头必须由新的个人视觉核实。
            _visibleSince = visible ? now : double.NegativeInfinity; // 每次真正重新露头都有独立的等待测量窗口。
            _reportedFireWait = FireWaitCause.None; // 旧遮挡期间的等待原因不跨窗口抑制。
            _fireReadyReported = false; // 再次可见后才允许记录本次准备完成。
            NextDecision = Math.Min(NextDecision, now); // 保持原来的唤醒时机。
            if (Trace(DiagnosticEvent.SightChanged, now)) WriteTrace("SIGHT_CHANGED", now, $"visible={visible} shooting={Owner.ShootData.Shooting} memoryLeft={((enemy != null && Memory.TryGet(enemy.ProfileId, now, out Observation lastSight)) ? Math.Max(0, lastSight.ExpiresAt - now) : 0):F1}"); // 边沿记录线索剩余寿命，便于实测房间内失视是否真已遗忘。
        }
        _visible = visible; // 记录本帧的个人视觉状态。
        if (visible && enemy != null) _recentSight.ObserveVisible(enemy.ProfileId, sight.Position, now); // 只用本帧核实的视觉快照刷新已知位置。
        if ((Runtime.Options.CombatFootwork || Runtime.Options.CombatRangeControl) && visible && enemy != null) _footwork.ObserveVisible(enemy.ProfileId, now); // 两种近战换位共用一次真实视觉窗口。
        bool ready = Owner.WeaponManager.IsWeaponReady && Owner.WeaponManager.HaveBullets && !Owner.WeaponManager.Reload.Reloading; // 原生武器条件不被能力曲线覆盖。
        Gate.Update(enemy?.ProfileId, visible, ready, now); // 并行推进察觉和稳定计时。
        if (visible) _aimPoint = GameAdapter.Position(sight.AimPosition); // 两次原生视觉检查之间不追踪实时身体坐标。
        if (!visible && GameAdapter.IsPlayerTarget(Owner.Memory.GoalEnemy) && Owner.ShootData.Shooting) Owner.ShootData.EndShoot(); // 只停止对玩家丢失视线后的连射，不误停 AI 互战。
        _hasClue = enemy != null && Memory.TryGet(enemy.ProfileId, now, out _clue); // 优先保留当前敌人的合法观察。
        if (!_hasClue) _hasClue = Memory.TryGetLatest(now, out _clue); // 没有当前目标时使用最新声音或视觉线索。
        if (!_visible && _lastGunshot.ExpiresAt > now && (!_hasClue || _clue.ObservedAt < _lastGunshot.ObservedAt)) { _clue = _lastGunshot; _hasClue = true; } // 过期短记忆可以回退到原有有效枪声，不续期也不跟踪真人位置。
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
        if (_moving && (Owner.Position - _progressPosition).sqrMagnitude > 0.25f) // 用真实位移证明移动请求已经执行。
        {
            _progressPosition = Owner.Position; _lastProgress = now; // 不依赖原生路径的名义进度。
            if (Trace(DiagnosticEvent.MoveProgress, now)) WriteTrace("MOVE_PROGRESS", now, FormattableString.Invariant($"state={State} stepRemaining={Vector3.Distance(Owner.Position, _destination):F1} clueRemaining={(_hasClue ? Vector3.Distance(Owner.Position, GameAdapter.Position(_clue.Position)) : -1):F1}")); // 限频后才计算日志字段。
        }
        if (_moving && now - _lastProgress > 2.5 && (Owner.Position - _destination).sqrMagnitude > 1.44f) // 门口或不可达点不能无限保持移动意图。
        {
            if (Trace(DiagnosticEvent.Stuck, now)) WriteTrace("STUCK", now, FormattableString.Invariant($"state={State} noProgressSeconds={now - _lastProgress:F1} distance={Vector3.Distance(Owner.Position, _destination):F1}")); // 记录回退前的真实剩余距离。
            StopMotion(); // 终止卡住路径。
            SearchRoute.Clear(); // 失效路径不能在冷却后继续复用。
            QueryFailed(now, "stuck"); // 两次失败后进入更长冷却。
        }
    }

    /// <summary>按战斗、警戒和空闲频率重选有界动作。</summary>
    internal void Decide(double now)
    {
        if (!Participating) return; // 无真人上下文时不执行自身恢复或查询，交给原生。
        if (_hasClue && !ExtendedSearch(now) && !_visible && !Controlled && !InvestigationPolicy.InRange(Role, GameAdapter.Snapshot(Owner.Position), _clue.Position)) // 仍有枪声寿命时不因短视觉覆盖回退到脚步半径。
        {
            Memory.Forget(_clue.Identity); // 已不可调查的旧快照不能反复触发接管。
            _hasClue = false; // 后续策略直接考虑原生巡逻或自身恢复。
            Runtime.Diagnostics.Count(DiagnosticEvent.ClueOutOfRange); // 与声音入库前的过滤分开计数。
        }
        _needsRecovery = !Owner.WeaponManager.HaveBullets || Owner.Medecine.FirstAid.Have2Do; // 只检查原生恢复需求。
        bool underFire = _playerDanger.IsActive(now); // AI 互射和环境伤害不刷新玩家危险。
        var health = Owner.HealthController.GetBodyPartHealth(EBodyPart.Common); // 读取自身健康，不能读取隐藏敌人的状态。
        bool lowHealth = health.Maximum > 0 && health.Current / health.Maximum < 0.45f; // 低血量倾向脱离或自救。
        bool atCover = AtCover; // 同一决策轮只读取一次带缓冲的到达状态。
        bool ambush = Runtime.Options.FootstepAmbush && _hasClue && CombatTactics.WaitOnFootstep(_clue, GameAdapter.Snapshot(Owner.Position), now, _visible, underFire, _recoveryRunning || _needsRecovery, Role != BotRole.Marksman); // 听觉只触发短暂停看。
        bool shiftPossible = Runtime.Options.CoverShift && _hasClue && _coverShiftObservationAt != _clue.ObservedAt && CombatTactics.ShiftCover(_clue, now, _visible, underFire, _recoveryRunning || _needsRecovery, _hasCover, atCover, Role != BotRole.Marksman); // 一个失视窗口最多尝试一次局部候选。
        if (_coverShiftCommitted && atCover && Trace(DiagnosticEvent.CoverShiftArrived, now)) WriteTrace("COVER_SHIFT_ARRIVED", now, "source=validated-cover-path"); // 决策轮先于动作执行时也保留实际到达证据。
        if (_coverShiftCommitted && (atCover || !_hasCover || underFire || now >= _coverExpires)) _coverShiftCommitted = false; // 到达、危险或旧路线失效立即结束换位承诺。
        int key = (_visible ? 1 : 0) | (_needsRecovery ? 2 : 0) | (_hasCover ? 4 : 0) | (underFire ? 8 : 0) | (lowHealth ? 16 : 0); // 仅实质条件变化才形成新情境。
        string? identity = _hasClue ? _clue.Identity : null; // 不把每次视觉时间更新当成新情境。
        if (key != _contextKey || identity != _selectedIdentity) // 同一交火上下文不反复抽取概率。
        {
            _contextKey = key; // 保存本次布尔情境。
            _selectedIdentity = identity; // 目标改变允许重新选择。
            _contextVersion++; // 为纯逻辑提供稳定代次。
            _decisionRoll = _random.Next01(); // 与 Unity 随机数互不干扰。
        }
        bool rush = Runtime.Options.ShortRush && _hasClue && CombatTactics.RushAfterSight(Role, Skill, Temperament, _clue, GameAdapter.Snapshot(Owner.Position), now, _decisionRoll, _visible, underFire, _recoveryRunning || _needsRecovery || lowHealth, Role != BotRole.Marksman, Owner.WeaponManager.HaveBullets); // 同一敌情固定抽签，PMC 等级线性提高机会。
        var input = new DecisionInput // 构造不携带游戏对象的决策快照。
        {
            HasClue = _hasClue, Visible = _visible, Reacted = ReadyToReact(now),
            SoundOnly = _hasClue && (_clue.Source == ObservationSource.Hearing || _clue.Source == ObservationSource.Gunshot),
            NeedsRecovery = _needsRecovery, RecoveryRunning = _recoveryRunning,
            HasCover = _hasCover, AtCover = atCover, UnderFire = underFire, LowHealth = lowHealth,
            CanMove = Role != BotRole.Marksman, ContextVersion = _contextVersion,
            PlayerDanger = underFire, AdvanceAfterDanger = _playerDanger.CanAdvance(now) && !underFire && (!Runtime.Options.CoordinatedAdvance || _squadMayAdvance), // 小队受击后只有一名被选中的成员执行危险区域推进。
            HoldAfterSquadDanger = Runtime.Options.CoordinatedAdvance && !_squadMayAdvance && _playerDanger.CanAdvance(now) && !underFire, // 未获名额者守点观察，真实看见玩家时仍可还击。
            WatchLastSeen = _hasClue && _clue.Source == ObservationSource.Vision && _recentSight.CanWatch(_clue.Identity, now, out _), // 新声源覆盖旧视觉后不再守原出口。
            AmbushFootstep = ambush, RushAfterSight = rush, ShiftCover = shiftPossible || _coverShiftRequested || _coverShiftCommitted, // 纯策略只接收已验证个人证据和动作承诺。
            MovingToCover = Runtime.Options.CoverCommitment && MovingToCover, // 明确记录执行中的合法路线，不能仅凭决策名称推断正在移动。
            Temperament = Runtime.Options.CombatTemperament ? Temperament : CombatTemperament.Balanced // 风格只影响战术掩体倾向，不更改能力成长与原生射击。
        };
        BehaviorState next = _policy.Decide(input, Skill, now, _decisionRoll); // 状态候选数与等级无关。
        ChangeState(next, now); // 决策与调查结束复用同一任务失效路径。
        if (State == BehaviorState.Advance && rush && _lastExecutedState != BehaviorState.Advance && Trace(DiagnosticEvent.RushSelected, now)) WriteTrace("RUSH_SELECTED", now, $"source=last-personal-vision age={now - _clue.ObservedAt:F1} distance={Vector3.Distance(Owner.Position, GameAdapter.Position(_clue.Position)):F1}"); // 只在机会边沿留下战术来源。
        if (State == BehaviorState.Observe && ambush && !_ambushLookIssued && Trace(DiagnosticEvent.AmbushSelected, now)) WriteTrace("AMBUSH_SELECTED", now, $"source=local-player-footstep age={now - _clue.ObservedAt:F1}"); // 伏击不能被误读为获得敌人当前坐标。
        if (State == BehaviorState.Cover && shiftPossible && Controlled && !_pending && now >= _retryAt) // 拥有当前层时才申请另一处掩体。
        {
            _coverShiftObservationAt = _clue.ObservedAt; // 即使失败也不在同一失视窗口反复扫描。
            _coverShiftRequested = true; // 粗选时排除当前旧掩体附近候选。
            Request(QueryKind.Cover, Owner.Position, now); // 复用已有全局碰撞、射线和路径额度。
            if (!_pending) _coverShiftRequested = false; // 队列已满时不保持伪承诺。
            if (Trace(DiagnosticEvent.CoverShiftRequested, now)) WriteTrace("COVER_SHIFT_REQUESTED", now, $"queued={_pending} source=last-personal-vision"); // 区分申请与实际换位。
        }
        if ((Runtime.Options.CombatFootwork || Runtime.Options.CombatRangeControl) && State == BehaviorState.Engage && _visible && TryVisibleObservation(Owner.Memory.GoalEnemy, now, out Observation combatSight)) TryCombatMovement(now, combatSight); // 已稳定交战时先考虑一次有界换位，避免普通掩体重试永久占队列。
        if (Controlled && _hasClue && Role != BotRole.Marksman && !_hasCover && !_pending && now >= _retryAt && (!_coverUnavailable || !underFire) && (_visible || _needsRecovery || underFire))
            Request(QueryKind.Cover, Owner.Position, now); // 只在有实际需求时查找局部掩体。
        NextDecision = now + (_visible ? 0.2 : _hasClue ? 0.5 : 2); // 所有等级共用 5/2/0.5 Hz。
        if (ShouldControl && !Controlled && now >= _nextControlCheck && now - _releasedAt >= 2) // 只记录持续缺少控制的情况，不把首次决策当作故障。
        {
            _nextControlCheck = now + 2; // 不按帧查询当前行为层。
            if (Trace(DiagnosticEvent.ControlWaiting, now)) WriteTrace("CONTROL_WAITING", now, $"state={State} selected={LayerSelected} nativeLayer={Owner.Brain?.BaseBrain?.CurLayerInfo?.Name() ?? "none"} grenade={Owner.WeaponManager.Grenades.ThrowindNow}"); // 原生抢占原因须保留证据，不能直接提优先级。
        }
    }

    /// <summary>状态改变时统一撤销旧任务，调用方负责同步纯逻辑策略。</summary>
    private void ChangeState(BehaviorState next, double now)
    {
        if (next != State) // 只在状态切换时失效旧请求。
        {
            if (!Controlled && State == BehaviorState.Native && next != BehaviorState.Native) { _releasedAt = now; _nextControlCheck = now + 2; } // 首次请求控制给 BigBrain 正常调度窗口，不立即报告等待故障。
            if (Trace(DiagnosticEvent.StateChanged, now)) WriteTrace("STATE_CHANGED", now, $"from={State} to={next} visible={_visible} clue={_hasClue} recovery={_needsRecovery} cover={_hasCover} controlled={Controlled}"); // 仅记录已缓存条件。
            bool keepCoverMove = Runtime.Options.CoverCommitment && TacticalActionPolicy.KeepCoverMove(State, next, MovingToCover); // 仅掩体族状态之间允许复用同一路线。
            if (Controlled && !keepCoverMove) StopMotion(); // 搜索、恢复、失效或换目标仍停止旧移动。
            if (keepCoverMove && Trace(DiagnosticEvent.CoverMoveKept, now)) WriteTrace("COVER_MOVE_KEPT", now, $"from={State} to={next}"); // 记录真实状态切换时保留路线，不按帧写日志。
            _searchPacing.Cancel(); // 切换动作不能把旧停看等待带入新状态。
            _blockedShots.ClearStreak(); // 不把上一个动作的阻挡次数拼进新交战窗口。
            SearchRoute.Clear(); // 旧路线不能跨避险、治疗等状态迁移。
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
    internal void Claim(string reason = "logic-start")
    {
        if (Controlled || !LayerSelected || !ShouldControl) return; // 只有实际选中的本模组层才能恢复，原生抢占时不能强夺控制。
        _lifecycle.OwnResources(); // 从此必须执行一次交接清理。
        Controlled = true; // 后续结果只有持有控制权才能执行。
        _clearedAiming = null; // 新的控制周期重新确认原生瞄准状态。
        _posture.Acquire(Owner.Mover.TargetPose, Time.time); // 保存目标姿态并建立本次控制周期的写入责任。
        _savedSpeed = Owner.Mover.DestMoveSpeed; // 保存原生移动速度目标。
        _savedProne = Owner.BotLay.IsLay; // 自有趴伏退出时不能改变原生原本的卧姿。
        Owner.PatrollingData.Pause(); // 暂停原生巡逻，避免它重新提交旧目的地。
        Owner.Mover.Stop(); // 取消原生上一个动作的路径。
        Owner.Mover.MovementResume(); // 不继承上一动作的短期移动暂停。
        Owner.Mover.Sprint(false); // 不在未知路线中保持冲刺。
        UpdatePosture(Time.time); // 初次受击立即压低，其他动作等待稳定意图。
        NextDecision = Math.Min(NextDecision, Time.time); // 下次共享调度立即处理当前情境。
        if (Trace(DiagnosticEvent.ControlClaimed, Time.time)) WriteTrace("CONTROL_CLAIMED", Time.time, $"state={State} generation={Generation} reason={reason}"); // 区分首次启动和同动作恢复。
    }

    /// <summary>被抢占或层退出时停止自身控制，原生治疗与换弹不被主动取消。</summary>
    internal void Release(bool preserveNativeCombat = false, string reason = "native-handoff")
    {
        _searchPacing.Cancel(); // 原生抢占优先，保留次数防止反复交接制造无限停看。
        _coverShiftRequested = _coverShiftCommitted = false; // 失去控制后旧换位任务不能重获执行资格。
        if (!_lifecycle.TryRelease()) return; // 已清理且未重新接管或排队时立即退出。
        Runtime.CancelQueries(Id); // 普通与紧急请求统一撤销。
        Runtime.ShotQueue.Cancel(Id); // 层退出后旧射击结果不能生效。
        _shotPending = _shotPermit = false; // 清除排队与许可标志。
        RestoreFireMode(Time.time); // 交还原生前恢复仍由本插件持有的武器模式。
        RestoreAimMode(Time.time); // 交还原生前恢复本插件改变的 ADS 与原生瞄准状态。
        Runtime.Diagnostics.Count(DiagnosticEvent.ResourceReleased); // 与停用次数及控制权交接分开计数。
        _pending = false; // 清理等待标志。
        _hasCover = false; // 不在交接后使用旧起点路径。
        _coverArrival.Reset(); // 不能把上一处掩体的到达状态传给新动作。
        _coverPath = null; // 释放路径角点数组。
        _searchIdentity = null; // 再次接管需要从实际位置重新建立搜索。
        SearchRoute.Clear(); // 交接后不能执行旧起点的导航折线。
        Gate.Reset(); // 重新接管后需要重新准备射击。
        if (!Controlled) return; // 不停止属于其他层的动作。
        Controlled = false; // 先交出令牌，异常路径也不会继续写入。
        _releasedAt = Time.time; // 等待诊断从实际交接时刻开始。
        _lastExecutedState = null; // 再次接管时重新记录实际动作入口。
        if (Trace(DiagnosticEvent.ControlReleased, Time.time)) WriteTrace("CONTROL_RELEASED", Time.time, $"state={State} generation={Generation} reason={reason} selected={LayerSelected}"); // 区分层停止、玩家退出、失活和正常搜索完成。
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
        ClearVisibleDuelProne(now); // 先清理不合条件的卧姿，避免本帧继续以卧姿处理瞄准或移动。
        switch (State) // 每帧仅执行一个当前动作。
        {
            case BehaviorState.Evade:
                Evade(now); // 紧急压低姿态、查询掩体或合法趴伏。
                break;
            case BehaviorState.Advance:
                RestoreProne(); // 前压前恢复本模组临时改变的姿态。
                if (TryTacticalGrenade(now)) break; // 推进途中的旧目击点允许让位给原生投掷动作。
                Search(now); // 只接近最后危险区域，分段推进。
                if (_visible && Controlled) AimAndFire(now); // 仍需真实视线，不能朝隐藏玩家盲射。
                break;
            case BehaviorState.Observe:
            case BehaviorState.Engage:
                if (State == BehaviorState.Observe && !_visible && _hasClue && Runtime.Options.FootstepAmbush && CombatTactics.WaitOnFootstep(_clue, GameAdapter.Snapshot(Owner.Position), now, false, _playerDanger.IsActive(now), _recoveryRunning || _needsRecovery, Role != BotRole.Marksman)) // 短时声音伏击仅转头，不沿估计点盲射。
                {
                    StopMotion(); // 原地停看最多一秒多，随后由下一次决策恢复搜索。
                    StopAim(); // 声音不提供射击许可。
                    if (!_ambushLookIssued) { Owner.Steering.LookToPoint(GameAdapter.Position(_clue.Position) + Vector3.up); _ambushLookIssued = true; } // 同一声源只提交一次原生转向。
                    break; // 不进入目视射击分支。
                }
                _ambushLookIssued = false; // 离开伏击后允许新脚步再次建立观察。
                if (!_visible && _hasClue && _clue.Source == ObservationSource.Vision && _recentSight.CanWatch(_clue.Identity, now, out System.Numerics.Vector3 watchPoint)) // 失视窗口只看已亲眼见过的固定位置。
                {
                    StopMotion(); // 守点短暂停留，不提交新寻路。
                    StopAim(); // 隐藏玩家没有直接瞄准与射击许可。
                    if (!_watchLookIssued) // 固定目标只需提交一次原生转向。
                    {
                        Owner.Steering.LookToPoint(GameAdapter.Position(watchPoint) + Vector3.up); // 看守旧出口，不读取玩家现在的坐标。
                        _watchLookIssued = true; // 本窗口剩余帧不重复调用原生转向接口。
                        if (Trace(DiagnosticEvent.SightWatchActive, now)) WriteTrace("SIGHT_WATCH_ACTIVE", now, "source=last-personal-vision"); // 区分已武装与实际动作。
                    }
                    break; // 守点时不进入 AimAndFire。
                }
                if (_moving && _pendingKind == QueryKind.Reposition && (Owner.Position - _destination).sqrMagnitude <= 1.44f && Owner.Mover.DistDestination <= 1.2f) // 换位必须走完短路线才能视为到达。
                {
                    StopMotion(); // 结束本模组的换位路线，后续仍走正常瞄准验证。
                    if (Trace(DiagnosticEvent.RepositionArrived, now)) WriteTrace("REPOSITION_ARRIVED", now, _repositionPurpose == RepositionPurpose.CloseRange ? "reason=close-range" : _repositionPurpose == RepositionPurpose.OpenFight ? "reason=open-fight" : "reason=world-obstacle"); // 区分贴脸后撤、主动侧移和挡枪换位的实际到达。
                }
                else if (!_moving || _pendingKind != QueryKind.Reposition) StopMotion(); // 普通射击窗口继续保持静止。
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
                if (TryTacticalGrenade(now)) break; // 合法手雷由原生执行，动作接管下一帧让出控制。
                Search(now); // 最多三个位置，失败与到期都能结束。
                break;
            case BehaviorState.Recover:
                StopMotion(); // 换弹和治疗保持稳定位置。
                ReportFireWait(FireWaitCause.Recovery, now); // 玩家露头而原生恢复仍占手时记录一次具体等待原因。
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

    /// <summary>对近期亲眼目击的旧点最多一次调用原生轨迹验证，投掷由游戏自身完成。</summary>
    private bool TryTacticalGrenade(double now)
    {
        if (!Runtime.Options.TacticalGrenades || Role == BotRole.Marksman || !Controlled || !LayerSelected || !_hasClue || now < _nextGrenadeAt || _grenadeObservationAt == _clue.ObservedAt) return false; // 层未持有或同一旧点已尝试过时不进入游戏接口。
        bool otherAction = (_moving || _pending) && _pendingKind != QueryKind.Search; // 搜索路线允许让位给更短时效的手雷机会。
        if (!CombatTactics.ThrowAtLastSeen(_clue, GameAdapter.Snapshot(Owner.Position), now, _visible, _playerDanger.IsActive(now), _recoveryRunning || _needsRecovery, otherAction)) return false; // 所有便宜的线索、时效和距离条件先行。
        _nextGrenadeAt = now + 0.5; // 轨迹额度不足或友军阻挡时，也最多每半秒复查一次。
        var weaponManager = Owner.WeaponManager; // 先固定本次原生武器管理器引用。
        var grenades = weaponManager?.Grenades; // 原生投掷器负责是否真的有可用手雷。
        if (grenades == null || !grenades.HaveGrenade || grenades.ThrowindNow || weaponManager!.Reload.Reloading) return false; // 不打断正在进行的原生武器和移动动作。
        Vector3 target = GameAdapter.Position(_clue.Position); // 投掷目标永远是个人视觉旧坐标。
        var group = Owner.BotsGroup; // 友军核对仅在准备实际验证轨迹时执行。
        if (group == null || group.MembersCount > 16) // 大型或未知队伍安全优先，且不产生无界遍历。
        {
            Runtime.Diagnostics.Count(DiagnosticEvent.GrenadeRejected); // 统计安全条件拒绝。
            _nextGrenadeAt = now + 3; // 三秒内不重复扫描同一队伍。
            return false; // 没有可靠友军信息时不能投掷。
        }
        for (int index = 0; index < group.MembersCount; index++) // 固定最多十六名同组成员。
        {
            var friend = group.Member(index); // 复用游戏已有小队列表，不扫描全场 Bot。
            if (friend != null && friend != Owner && !friend.IsDead && (friend.Position - target).sqrMagnitude < 100) // 旧点十米内有友军时直接取消。
            {
                Runtime.Diagnostics.Count(DiagnosticEvent.GrenadeRejected); // 只增加固定原因计数。
                _nextGrenadeAt = now + 3; // 队友移动前不反复检查。
                return false; // 不进入原生轨迹检查。
            }
        }
        if (!Runtime.GrenadeTrajectories.TryTake(now, Time.frameCount)) return false; // 全局每秒最多一次原生轨迹求解，同帧也只能一次。
        using var measurement = new ActionMeasurement(Runtime, this, WorkPhase.ActionGrenade); // 将原生轨迹检查的同步成本单独纳入战局峰值。
        _grenadeObservationAt = _clue.ObservedAt; // 无论轨迹是否有效，同一目击窗口不重试多角度。
        _nextGrenadeAt = now + 20; // 每个 Bot 的投掷尝试冷却独立于全局额度。
        Runtime.Diagnostics.Count(DiagnosticEvent.GrenadeAttempted); // 计数不分配日志字符串。
        Vector3 source = Owner.Position + Vector3.up * 1.4f; // EFT 40743 不公开 SAIN 封装的武器根位置，采用角色投掷高度。
        AIGreanageThrowData trajectory = AIGrenadeHelper.CanThrowGrenade2(source, target + Vector3.up * 0.25f, grenades.MaxPower * 0.9f, AIGreandeAng.ang15, -1f, 0.66f); // 仅试一个低弧度，失败不扩大物理工作。
        if (!trajectory.CanThrow) { if (Trace(DiagnosticEvent.GrenadeRejected, now)) WriteTrace("GRENADE_REJECTED", now, "reason=native-trajectory"); return false; } // 游戏轨迹拒绝时继续原有搜索。
        if (!grenades.SetThrowData(trajectory)) { if (Trace(DiagnosticEvent.GrenadeRejected, now)) WriteTrace("GRENADE_REJECTED", now, "reason=native-set-data"); return false; } // 原生控制器拒绝轨迹时不能继续投掷。
        if (!grenades.ReadyToThrow || !grenades.AIGreanageThrowData.IsUpToDate()) { if (Trace(DiagnosticEvent.GrenadeRejected, now)) WriteTrace("GRENADE_REJECTED", now, "reason=native-not-ready"); return false; } // 原生投掷器若尚未就绪则不抢占动作。
        if (_pending && _pendingKind == QueryKind.Search) { Runtime.CancelQueries(Id); _pending = false; SearchRoute.Clear(); _searchIdentity = null; } // 投掷开始后旧导航结果不能覆盖原生动画。
        StopMotion(); // 投掷前停下本模组的路线，避免手雷动画与移动相互覆盖。
        StopAim(); // 自有真人射击入口暂停，原生手雷动作取得优先权。
        bool thrown = grenades.DoThrow(); // 游戏自身处理装备、动画与弹道。
        if (thrown && Trace(DiagnosticEvent.GrenadeThrown, now)) WriteTrace("GRENADE_THROWN", now, $"source=last-personal-vision age={now - _clue.ObservedAt:F1} distance={Vector3.Distance(Owner.Position, target):F1}"); // 日志区分轨迹尝试和原生实际接受。
        if (!thrown && Trace(DiagnosticEvent.GrenadeRejected, now)) WriteTrace("GRENADE_REJECTED", now, "reason=native-do-throw"); // 原生拒绝仍遵守本次冷却。
        return thrown; // 原生已接管本帧投掷时不再启动搜索路线。
    }

    /// <summary>玩家仍在当前枪线内时清理本层持有期间新增的卧姿，保留接管前的原生状态。</summary>
    private void ClearVisibleDuelProne(double now)
    {
        if (!TacticalActionPolicy.ShouldClearVisibleProne(Controlled, LayerSelected, _visible, GameAdapter.IsPlayerTarget(Owner.Memory.GoalEnemy), _savedProne, Owner.BotLay.IsLay)) return; // 原生层、失视或原有卧姿都不由本模组改写。
        bool owned = _ownsProne; // 日志区分本模组较早设置与外部行为后来设置的卧姿。
        if (owned) RestoreProne(); // 自有卧姿通过原有归属恢复路径释放。
        else
        {
            Owner.BotLay.IsLay = false; // 对枪时及时撤销接管后出现的外部卧姿。
            _posture.ResumeAfterProne(Owner.Mover.TargetPose); // 同步原生卧姿退出后的姿态基准，避免蹲起抖动。
        }
        if (Trace(DiagnosticEvent.VisibleProneCleared, now)) WriteTrace("VISIBLE_PRONE_CLEARED", now, $"source={(owned ? "mod" : "external")} reason=player-visible"); // 边沿事件可核对局内观察到的趴伏来源。
    }

    /// <summary>所有自有普通姿态只从此处提交，原生卧姿和已交还的控制权不被干预。</summary>
    private void UpdatePosture(double now)
    {
        using var measurement = new ActionMeasurement(Runtime, this, WorkPhase.ActionPosture); // 姿态及同步动画调用独立归属。
        if (!Controlled || !LayerSelected || !Participating || Disposed || Owner == null || Owner.BotState != EBotState.Active || Owner.BotLay.IsLay) return; // 未被选中、非玩家情境、失活或卧姿均不争抢姿态。
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
        using var measurement = new ActionMeasurement(Runtime, this, WorkPhase.ActionAim); // 原生瞄准、转向与武器入口独立计时。
        EnemyInfo? enemy = Owner.Memory.GoalEnemy; // 每次开火前重新核对当前目标。
        if (!GameAdapter.IsPlayerTarget(enemy) || !_visible || !TryVisibleObservation(enemy, now, out Observation sight) || enemy?.CanShoot != true || _recoveryRunning) // 同时验证真人目标、合法部位与最新观察。
        {
            Runtime.Diagnostics.Count(DiagnosticEvent.AimBlocked); // 该计数也包含目标不能射击或恢复锁，并非都代表失去视线。
            if (_visible && GameAdapter.IsPlayerTarget(enemy)) ReportFireWait(_recoveryRunning ? FireWaitCause.Recovery : enemy?.CanShoot != true ? FireWaitCause.NoShootablePart : FireWaitCause.VisionStale, now); // 区分恢复锁、不可射击部位和已过时的视觉快照。
            StopAim(); // 无当前合法个人视觉或恢复锁时不能继续瞄准。
            return; // 本帧不能提交射击验证。
        }
        _aimPoint = GameAdapter.Position(sight.AimPosition); // 目标切换后不能沿用上一敌人的瞄准点。
        var aiming = Owner.AimingManager.CurrentAiming; // 使用原生瞄准系统。
        _aimDirty = true; // 只有重新提交瞄准后才需要再次撤销自有目标。
        aiming.SetTarget(_aimPoint); // 输入仅来自本 Bot 可见部位的快照。
        aiming.NodeUpdate(); // 原生稳定、姿态和后坐力继续工作。
        UpdateFireMode(now, sight); // 真实目视交战时才按武器支持集有限调整模式。
        UpdateAimMode(now, sight); // 仅在真实目视窗口按距离迟滞选择举枪或腰射。
        bool reacted = ReadyToReact(now); // 当前重新露头是否已完成模组反应门槛。
        bool nativeReady = aiming.IsReady; // 原生瞄准准备独立于本插件计时。
        if (!nativeReady) Runtime.Diagnostics.Count(DiagnosticEvent.AimNotReady); // 区分原生瞄准未完成与 Shoot 入口被拦截。
        if (!reacted) ReportFireWait(FireWaitCause.Reaction, now); // 等待超过短窗口时才留下本插件延迟证据。
        else if (!nativeReady) ReportFireWait(FireWaitCause.NativeAim, now); // 新版实测可区分原生稳定和自有等待。
        else ReportFireReady(now); // 只说明本插件与原生瞄准已就绪，尚未通过枪线验证。
        if (reacted && nativeReady && enemy!.CanShoot && !Owner.ShootData.Shooting && now >= _nextShotAttempt) // 近枪可跳过等级等待，但原生条件保持。
        {
            _nextShotAttempt = now + 0.05; // 失败验证最多每秒二十次尝试，不每帧消耗射线。
            Owner.ShootData.Shoot(); // Harmony 射击入口统一做最终预算内验证。
        }
        TryCombatMovement(now, sight); // 决策帧之间仍可在武器完成验证后抓住交战换位机会。
    }

    /// <summary>只在持续露头且等待原因首次确定或改变时记录一次，不逐帧增加计数与字符串。</summary>
    private void ReportFireWait(FireWaitCause cause, double now)
    {
        if (!_visible || double.IsNegativeInfinity(_visibleSince) || now - _visibleSince < 0.25 || cause == _reportedFireWait) return; // 短暂目视和同一原因不刷日志。
        _reportedFireWait = cause; // 同一可见窗口只在原因变化时重新记录。
        if (Trace(DiagnosticEvent.FireOpportunityWait, now)) WriteTrace("FIRE_OPPORTUNITY_WAIT", now, $"reason={cause} visibleMs={(int)((now - _visibleSince) * 1000)} state={State}"); // 留下可与原生射击和枪线验证对照的原因。
    }

    /// <summary>模组反应和原生瞄准均就绪时记录首个可验证射击窗口，不误报为已开枪。</summary>
    private void ReportFireReady(double now)
    {
        if (_fireReadyReported || double.IsNegativeInfinity(_visibleSince)) return; // 每次连续可见窗口最多一个就绪事件。
        _fireReadyReported = true; // 明细被全局限频合并也不能每帧重复计数。
        _reportedFireWait = FireWaitCause.None; // 下一次失视重新评估等待原因。
        if (Trace(DiagnosticEvent.FireOpportunityReady, now)) WriteTrace("FIRE_OPPORTUNITY_READY", now, $"visibleMs={(int)((now - _visibleSince) * 1000)} state={State} fastRepeek={_recentSight.CanFireOnRepeek(Owner.Memory.GoalEnemy?.ProfileId)}"); // 枪线遮挡和原生扳机结果仍需另查。
    }

    /// <summary>最多每半秒按真实视觉距离调整一次当前武器模式，不覆盖换弹、原生连射或其他层。</summary>
    private void UpdateFireMode(double now, in Observation sight)
    {
        if (!Runtime.Options.AdaptiveFireMode || !Controlled || !LayerSelected || now < _nextFireModeCheck || Owner.ShootData.Shooting || !Owner.WeaponManager.IsWeaponReady || Owner.WeaponManager.Reload.Reloading) return; // 热循环只做便宜门槛判断。
        _nextFireModeCheck = now + 0.5; // 限制武器属性与动画写入频率。
        Weapon? weapon = Owner.WeaponManager.CurrentWeapon; // 只检查当前真正装备的武器。
        if (weapon == null || weapon.FireMode == null || weapon.WeapFireType == null) return; // 无有效模式的武器交给原生。
        if (_fireModeWeapon != null && !ReferenceEquals(_fireModeWeapon, weapon)) RestoreFireMode(now); // 换枪时先归还上一把武器的模式。
        if (_fireModeWeapon != null && weapon.SelectedFireMode != _appliedFireMode) _fireModeWeapon = null; // 其他系统已改写模式时放弃所有权。
        EFireMode currentMode = weapon.SelectedFireMode; // 读取模式快照，避免重复切换。
        CombatFireMode current = currentMode == EFireMode.single ? CombatFireMode.Single : currentMode == EFireMode.burst ? CombatFireMode.Burst : currentMode == EFireMode.fullauto ? CombatFireMode.FullAuto : CombatFireMode.None; // 未知模式保持原生。
        float distance = Vector3.Distance(Owner.Position, GameAdapter.Position(sight.Position)); // 只用 Bot 自己的当前位置和最近一次个人视觉快照。
        EFireMode[] modes = weapon.WeapFireType; // 当前武器的有限支持集只读取一次。
        CombatFireMode selected = CombatAdaptation.SelectFireMode(distance, Array.IndexOf(modes, EFireMode.single) >= 0, Array.IndexOf(modes, EFireMode.burst) >= 0, Array.IndexOf(modes, EFireMode.fullauto) >= 0, current); // 三次无分配数组查找，无敌人遍历。
        if (selected == CombatFireMode.None || selected == current) return; // 无需切换时不写武器对象或动画。
        EFireMode target = selected == CombatFireMode.Single ? EFireMode.single : selected == CombatFireMode.Burst ? EFireMode.burst : EFireMode.fullauto; // 只映射已经确认支持的三种模式。
        weapon.FireMode.SetFireMode(target); // 已验证支持且当前未射击时才提交武器状态。
        if (_fireModeWeapon == null) { _fireModeWeapon = weapon; _originalFireMode = currentMode; } // 成功写入后才记录恢复责任。
        _appliedFireMode = target; // 后续交接仅恢复自己仍持有的写入。
        Owner.GetPlayer.HandsController?.FirearmsAnimator?.SetFireMode(target); // 同步当前持枪动画，避免视觉模式与物品状态不一致。
        if (Trace(DiagnosticEvent.FireModeChanged, now)) WriteTrace("FIRE_MODE_CHANGED", now, $"from={currentMode} to={target} distanceBand={(distance <= 22 ? "near" : distance >= 55 ? "far" : "middle")}"); // 不记录玩家位置或武器身份。
    }

    /// <summary>只在武器仍保持本插件写入的模式时归还原值，允许其他层接手后的修改。</summary>
    private void RestoreFireMode(double now)
    {
        Weapon? weapon = _fireModeWeapon; // 先保存旧物品并清除责任，异常时不会反复恢复。
        _fireModeWeapon = null; // 其他层后续可独立选择模式。
        if (weapon == null || weapon.FireMode == null || weapon.SelectedFireMode != _appliedFireMode) return; // 已被其他系统改写时不能覆盖它。
        try // 游戏销毁或换枪期间的对象异常只降级这一次恢复。
        {
            weapon.FireMode.SetFireMode(_originalFireMode); // 无论是否仍手持，都恢复本插件改过的物品数据。
            if (Owner != null && ReferenceEquals(Owner.WeaponManager.CurrentWeapon, weapon)) Owner.GetPlayer.HandsController?.FirearmsAnimator?.SetFireMode(_originalFireMode); // 仅当前手持武器需要同步动画。
            if (Trace(DiagnosticEvent.FireModeRestored, now)) WriteTrace("FIRE_MODE_RESTORED", now, $"to={_originalFireMode}"); // 控制交接证据与射击结果分开。
        }
        catch (Exception exception) { Runtime.Log.LogWarning($"[ABO] event=FIRE_MODE_RESTORE_FAILED bot={Id} reason={exception.Message}"); } // 一次性记录，避免销毁期刷屏。
    }

    /// <summary>每四分之一秒检查当前真实目视距离，只有自有交战层实际持有时才切换 ADS。</summary>
    private void UpdateAimMode(double now, in Observation sight)
    {
        if (!Runtime.Options.AdaptiveAim || _aimModeFailed || !Controlled || !LayerSelected || now < _nextAimModeCheck || Owner.ShootData.Shooting || _recoveryRunning) return; // 动画切换不进入逐帧热路径，也不打断连射。
        _nextAimModeCheck = now + 0.25; // 所有等级共用检查频率。
        var controller = Owner.WeaponManager?.ShootController; // 只读取当前装备的原生瞄准控制器。
        if (controller == null) return; // 缺少武器时交给原生。
        if (_aimModeController != null && !ReferenceEquals(_aimModeController, controller)) RestoreAimMode(now); // 换枪时先清理旧控制责任。
        if (_aimModeController != null && controller.IsAiming != _appliedAimMode) { _aimModeController = null; _aimModeData = null; } // 其他系统已改写时放弃旧恢复基线。
        float distance = Vector3.Distance(Owner.Position, GameAdapter.Position(sight.Position)); // 距离仅使用 Bot 本人和亲眼目击的目标快照。
        bool desired = CombatAdaptation.AimDownSights(distance, controller.IsAiming); // 近距腰射、远距举枪，中距保留当前状态。
        if (desired == controller.IsAiming) return; // 不重复写入已正确的原生状态。
        if (_aimModeController == null) { _aimModeController = controller; _originalAimMode = controller.IsAiming; _aimModeData = Owner.AimingManager.CurrentAiming as BotAimingData; _originalHardAim = _aimModeData?.HardAim ?? false; } // 首次写入才保存交还基线。
        _appliedAimMode = desired; // 交还时仅恢复仍等于本插件写入的值。
        try // 版本已核对，但销毁或切换武器的临界帧仍需单 Bot 回退。
        {
            controller.SetAim(desired); // 让游戏自身更新举枪动画与持枪状态。
            if (_aimModeData != null && ReferenceEquals(_aimModeData, Owner.AimingManager.CurrentAiming)) _aimModeData.HardAim = desired; // 只同步原先实际持有的瞄准实例。
        }
        catch (Exception exception) { _aimModeFailed = true; RestoreAimMode(now); Runtime.Log.LogWarning($"[ABO] event=AIM_MODE_FAILED bot={Id} reason={exception.Message}"); return; } // 该 Bot 此局禁用扩展 ADS，保持其余战斗链路。
        if (Trace(DiagnosticEvent.AimModeChanged, now)) WriteTrace("AIM_MODE_CHANGED", now, $"ads={desired} distanceBand={(distance <= 5 ? "close" : distance >= 10 ? "far" : "middle")}"); // 不记录玩家坐标。
    }

    /// <summary>控制权退出时仅恢复本插件仍持有的原生 ADS 状态，不覆盖其他层后续改写。</summary>
    private void RestoreAimMode(double now)
    {
        object? owned = _aimModeController; // 先清除责任，异常或重复释放不能反复切换。
        _aimModeController = null; // 下一次交战重新记录当前原生基线。
        BotAimingData? ownedAiming = _aimModeData; // 只处理本插件曾经改动的实例。
        _aimModeData = null; // 不跨层保留瞄准器引用。
        if (owned == null || Owner == null) return; // 从未改写时不触碰原生状态。
        try // 战局销毁时原生控制器可能提前失效，清理不得阻止其他资源释放。
        {
            var controller = Owner.WeaponManager?.ShootController; // 使用当前控制器核对是否仍为同一对象。
            if (controller != null && ReferenceEquals(owned, controller) && controller.IsAiming == _appliedAimMode) { controller.SetAim(_originalAimMode); if (Trace(DiagnosticEvent.AimModeRestored, now)) WriteTrace("AIM_MODE_RESTORED", now, $"ads={_originalAimMode}"); } // 他人已改写时不抢回控制。
            if (ownedAiming != null && ReferenceEquals(ownedAiming, Owner.AimingManager?.CurrentAiming) && ownedAiming.HardAim == _appliedAimMode) ownedAiming.HardAim = _originalHardAim; // 硬瞄准也按相同所有权规则恢复。
        }
        catch (Exception exception) { _aimModeFailed = true; Runtime.Log.LogWarning($"[ABO] event=AIM_MODE_RESTORE_FAILED bot={Id} reason={exception.Message}"); } // 一次失败后本 Bot 不再改写 ADS。
    }

    /// <summary>在交战状态中对真实可见的本机玩家提出一次预算内近战换位。</summary>
    private void TryCombatMovement(double now, in Observation sight)
    {
        bool canMove = Controlled && LayerSelected && State == BehaviorState.Engage && Role != BotRole.Marksman && !_hasCover && !_moving && !_pending && !_shotPending && !_recoveryRunning && !_playerDanger.IsActive(now) && now >= _retryAt; // 掩体、避险和恢复动作均优先于普通近战换位。
        if (!canMove || (!Runtime.Options.CombatFootwork && !Runtime.Options.CombatRangeControl)) return; // 功能关闭或控制权失效时不计算候选。
        System.Numerics.Vector3 origin = GameAdapter.Snapshot(Owner.Position); // 本次换位只使用当前 Bot 位置和已确认的玩家视觉快照。
        System.Numerics.Vector3 backPoint = default; // 功能关闭时不使用未初始化的候选。
        bool closeRange = Runtime.Options.CombatRangeControl && !Owner.BotLay.IsLay && !Owner.WeaponManager.IsMelee && _footwork.TryPlanCloseRetreat(true, origin, sight.Position, now, out backPoint); // 原生卧姿或近战武器不强制后撤，一至六米时才提出候选。
        System.Numerics.Vector3 footworkPoint = backPoint; // 成功的后撤候选直接进入共享路线验证。
        if (!closeRange && (!Runtime.Options.CombatFootwork || !_footwork.TryPlan(true, origin, sight.Position, now, out footworkPoint))) return; // 六至二十五米才考虑侧移，不生成第二条候选。
        Request(QueryKind.Reposition, GameAdapter.Position(footworkPoint), now); // Waypoints 网格和现有分帧额度验证完整短路线。
        if (_pending) // 队列拒绝时保留机会，仍受原有半秒重试冷却。
        {
            _repositionPurpose = closeRange ? RepositionPurpose.CloseRange : RepositionPurpose.OpenFight; // 实际到达日志保留动作原因。
            _footwork.MarkRequested(); // 本次交战只发起一次主动侧移或贴脸后撤。
            _blockedShots.CooldownAfterExternal(sight.Identity, now); // 挡枪反馈八秒内不能紧接着生成第二条侧移路线。
            DiagnosticEvent kind = closeRange ? DiagnosticEvent.CloseRangeRetreatRequested : DiagnosticEvent.CombatFootworkRequested; // 两类动作分别计数，便于局内对照。
            if (Trace(kind, now)) WriteTrace(closeRange ? "CLOSE_RANGE_RETREAT_REQUESTED" : "COMBAT_FOOTWORK_REQUESTED", now, closeRange ? "source=current-personal-vision distanceBand=1-6m stepMeters=3" : "source=current-personal-vision distanceBand=6-25m stepMeters=3"); // 排队不等于已移动或路线可达。
        }
    }

    /// <summary>只消费新鲜且未使用的射击许可；需要验证时进入公平队列。</summary>
    internal bool AllowShot(double now, int frame)
    {
        if (Disposed || Owner.BotState != EBotState.Active) return RejectShot(DiagnosticEvent.ShotInactive); // 停用后不能通过原生层回调重新排队。
        EnemyInfo? enemy = Owner.Memory.GoalEnemy; // 不信任动作开始时的旧目标。
        if (!GameAdapter.IsPlayerTarget(enemy)) return true; // 防御性旁路，AI 互射永远不领取本模组射击许可。
        if (Controlled && LayerSelected && State == BehaviorState.Evade && _playerDanger.IsActive(now) && !CanDefendFromCover(now) && !CanExposedReturnFire(now)) return RejectShot(DiagnosticEvent.ShotPressureBlocked); // 掩体还击或受限暴露自卫之外继续避险，原生层和 AI 互战不受压力门控。
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
        if (Controlled && LayerSelected && State == BehaviorState.Evade && _playerDanger.IsActive(now) && !CanDefendFromCover(now) && !CanExposedReturnFire(now)) { _shotPending = _shotPermit = false; Runtime.Diagnostics.Count(DiagnosticEvent.ShotPressureBlocked); return true; } // 高压、开始移动或新掩体出现时旧自卫许可立即作废。
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
            bool canReposition = Controlled && LayerSelected && Participating && (State == BehaviorState.Observe || State == BehaviorState.Engage) && !_moving && !_pending && !_recoveryRunning && Role != BotRole.Marksman && !_playerDanger.IsActive(now); // 只在自有真人交战层中换位，不抢撤离或紧急避险。
            if (_blockedShots.Observe(enemy.ProfileId, canReposition, now, out int attempt) && AdaptiveMovement.TrySideStep(GameAdapter.Snapshot(Owner.Position), GameAdapter.Snapshot(target), attempt, out System.Numerics.Vector3 candidate)) // 三次连续真实挡枪才花一次共享导航额度。
            {
                Request(QueryKind.Reposition, GameAdapter.Position(candidate), now); // 路径仍由原有完整路径和距离上限检查。
                if (_pending) { _repositionPurpose = RepositionPurpose.BlockedShot; _footwork.MarkOtherReposition(); } // 成功排队的挡枪换位占用本交战的主动换位名额。
                if (Trace(DiagnosticEvent.RepositionRequested, now)) WriteTrace("REPOSITION_REQUESTED", now, $"side={attempt} queued={_pending}"); // 排队不等于路线成功。
            }
            return true;
        }
        _blockedShots.ClearStreak(); // 已恢复世界枪线时取消累计阻挡。
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
        using var measurement = new ActionMeasurement(Runtime, this, WorkPhase.ActionAimClear); // 区分反复取消瞄准与正常瞄准更新。
        if (Owner.Memory.GoalEnemy != null && !GameAdapter.IsPlayerTarget(Owner.Memory.GoalEnemy)) return; // 不在混战交接时取消原生对 AI 的瞄准。
        RestoreAimMode(Time.time); // 玩家失视或搜索开始后及时归还本插件的举枪选择。
        if (Owner.ShootData.Shooting) Owner.ShootData.EndShoot();
        IBotAiming aiming = Owner.AimingManager.CurrentAiming; // 原生可能替换瞄准器，不能只记一个布尔值。
        bool nativeTarget = aiming is BotAimingData data && data.Status != AimStatus.NoTarget; // 外部重新设置目标时仍及时清理。
        if (!_aimDirty && ReferenceEquals(_clearedAiming, aiming) && !nativeTarget && !aiming.HardAim && !aiming.IsReady) return; // 不逐帧重复触发无目标的原生瞄准/动画清理。
        aiming.LoseTarget(); // 首次、实例变更或目标重新出现时才调用。
        _clearedAiming = aiming; _aimDirty = false; // 后续空闲帧只做常数检查。
    }

    /// <summary>只停止本模组自己开始的移动。</summary>
    private void StopMotion()
    {
        if (!_moving) return;
        using var measurement = new ActionMeasurement(Runtime, this, WorkPhase.ActionMove); // 只计真正提交的停止动作。
        Owner.Mover.Stop();
        _moving = false;
    }

    /// <summary>使用缓存路径去往已验证的掩体，起点变化后重新排队。</summary>
    private void MoveToCover(double now)
    {
        bool arrived = AtCover; // 单次动作只核对一次带缓冲的掩体到达状态。
        if (!_hasCover || arrived) // 掩体失效或已经到达时不继续追逐目的地。
        {
            if (_coverShiftCommitted && _hasCover && arrived && Trace(DiagnosticEvent.CoverShiftArrived, now)) WriteTrace("COVER_SHIFT_ARRIVED", now, "source=validated-cover-path"); // 动作帧先于下一决策时记录真实到达。
            _coverShiftCommitted = false; // 结束本次换位承诺。
            StopMotion(); // 已到达时停止本模组自己的路径。
            return; // 原生可以接续守掩体或搜索。
        }
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
        using var measurement = new ActionMeasurement(Runtime, this, WorkPhase.ActionSearch); // 包含转向和候选处理，子动作按独占阶段拆开。
        if (!_hasClue) { FinishInvestigation(now, "clue-expired"); return; } // 无线索立即停止自有搜索。
        Vector3 anchor = GameAdapter.Position(_clue.Position); // 只能读观察快照。
        if (!_searchAreaHeld) Owner.Steering.LookToPoint(anchor + Vector3.up); // 查完区域后保留已经提交的朝向，不再为延长的守点期限逐帧调用原生转向。
        if (Role == BotRole.Marksman) return; // 狙击模板不得离开岗位追击。
        if (_searchIdentity != _clue.Identity || (_searchAnchor - anchor).sqrMagnitude > 16) // 小幅新声音不反复重置完整搜索。
        {
            _searchIdentity = _clue.Identity; // 绑定本轮线索身份。
            _searchAnchor = anchor; // 固定本轮中心。
            _searchStart = Owner.Position; // 用于限制追击范围。
            _searchPacing.SetArea(_clue.Identity, _clue.Position); // 同区域重新接管不重置有限停看名额。
            _searchProgress.Reset(); // 新区域重新记录到达与失败，二者不可混淆。
            _searchAreaHeld = false; // 新目击区域必须重新走候选，不能继承上一处的守点边沿。
            SearchRoute.Clear(); // 新快照不能使用旧区域的缓存折线。
            if (_pending && _pendingKind == QueryKind.Search) { Runtime.CancelQueries(Id); _pending = false; } // 原候选仍在排队时必须撤销，不能把旧区域路线绑定到新候选。
            _arrivedAt = 0; // 清理上一轮驻留计时。
            _searchSegmentFinal = false; // 上一个区域的末段不能算作本区域已到达。
            float angle = _random.Next01() * Mathf.PI * 2; // 一次生成后续候选方向。
            Vector3 lateral = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * Mathf.Clamp(_clue.Uncertainty, 2, 6); // 候选范围保持有界。
            _searchPoints[0] = anchor; // 首先检查线索中心。
            _searchPoints[1] = anchor + lateral; // 第二点检查一个侧面。
            _searchPoints[2] = anchor - lateral; // 第三点检查相反侧面。
            StopMotion(); // 不沿用上一线索的路径。
        }
        bool extended = ExtendedSearch(now); // 枪声寿命不因随后短暂视觉或脚步而退回短调查半径。
        if (_searchProgress.Index >= 3 && _searchProgress.Retry(now, Math.Max(_clue.ExpiresAt, _lastGunshot.ExpiresAt))) // 未到达的候选最多再试一轮。
        {
            _retryAt = now + 3; // 给临时阻挡和导航状态留出恢复时间，不立即集中重算。
            SearchRoute.Clear(); // 替代尝试必须重新求解。
            if (Trace(DiagnosticEvent.SearchRetry, now)) WriteTrace("SEARCH_RETRY", now, $"reached={_searchProgress.Reached} failed={_searchProgress.Failed} delaySeconds=3"); // 明确这是失败重试而非搜索完成。
            return;
        }
        if (CombatMemoryPolicy.HoldCheckedArea(_searchProgress.Index >= 3, now, _combatMemoryUntil, _clue.ExpiresAt)) // 三点已查完时保留同层交战的最后区域，避免在记忆期限前直接失忆。
        {
            StopMotion(); // 不新增第四处寻路候选，也不在已经检查完的区域反复计算路径。
            if (!_searchAreaHeld) // 同一轮只记录一次静态守点边沿。
            {
                _searchAreaHeld = true; // 后续帧维持已有朝向并等待真实视觉或声音。
                if (Trace(DiagnosticEvent.SearchAreaHeld, now)) WriteTrace("SEARCH_AREA_HELD", now, $"secondsLeft={_clue.ExpiresAt - now:F1} reached={_searchProgress.Reached} failed={_searchProgress.Failed}"); // 可与记忆到期和新视觉对应。
            }
            return; // 仍有合法记忆时不调用 FinishInvestigation 清除目标。
        }
        if (_searchProgress.Index >= 3 || (!extended && !InvestigationPolicy.InRange(Role, GameAdapter.Snapshot(_searchStart), _clue.Position))) // 普通搜索继续保留固定半径。
        {
            Memory.Forget(_clue.Identity); // 不能通过重新读取延长记忆。
            _hasClue = false; // 结束当前线索的调查。
            FinishInvestigation(now, _searchProgress.Index >= 3 ? (_searchProgress.Failed > 0 ? "unreachable" : "area-checked") : "range-limit"); // 不把失败放弃记作已搜完区域。
            return;
        }
        Vector3 point = _searchPoints[_searchProgress.Index]; // 本轮最多只有三个候选。
        if (!_moving && !_pending && _failedSearch.Rejects(GameAdapter.Snapshot(point), now)) // 短期失败记忆只在真正需要新路径时跳过该点。
        {
            int index = _searchProgress.Index; // 日志使用跳过前的候选序号。
            _searchProgress.CompletePoint(false); // 不把不可达区域冒充实际搜到。
            if (Trace(DiagnosticEvent.SearchCandidateSkipped, now)) WriteTrace("SEARCH_CANDIDATE_SKIPPED", now, $"pointIndex={index} reason=recent-navigation-failure"); // 可据此验证没有重算同一断路。
            return;
        }
        bool reachedStep = _moving && (Owner.Position - _destination).sqrMagnitude <= 1.44f && Owner.Mover.DistDestination <= 1.2f; // 还必须走完原生路径，隔墙接近终点不能提前结束。
        if (reachedStep)
        {
            StopMotion(); // 完成一段后优先取缓存中的下一段。
            if (Trace(DiagnosticEvent.MoveArrived, now)) WriteTrace("MOVE_ARRIVED", now, $"state={State} final={_searchSegmentFinal} pointIndex={_searchProgress.Index}"); // 路径提交与实际到达分开证明。
        }
        if ((reachedStep && _searchSegmentFinal) || _arrivedAt > 0) // 只有走完末段或验证完整短路线后才能驻留。
        {
            StopMotion(); // 清理已完成的移动。
            if (_arrivedAt == 0) _arrivedAt = now; // 只在首次到达时开始驻留。
            if (now - _arrivedAt >= 1) { _searchProgress.CompletePoint(true); _arrivedAt = 0; _searchSegmentFinal = false; SearchRoute.Clear(); } // 实际到达并驻留后才算完成候选。
        }
        else if (!_moving && !_pending) // 已有路线可继续或完成停看，只有新增查询需要重试冷却。
        {
            if (StartSearchSegment(now)) return; // 缓存仍有效时不重复求解路径。
            if (now < _retryAt) return; // 停看结束不能绕过失败查询原有的冷却。
            _searchSegmentFinal = false; // 没有路线不能冒充已经到达末段。
            Request(QueryKind.Search, point, now); // 先求到完整候选的路线，再沿实际折线分段。
        }
    }

    /// <summary>玩家枪声和危险期间允许远距调查，不把当前观察来源当成整轮搜索的唯一授权。</summary>
    private bool ExtendedSearch(double now)
    {
        return now < _combatMemoryUntil || _clue.Source == ObservationSource.Gunshot || _clue.Source == ObservationSource.Danger || State == BehaviorState.Advance || _lastGunshot.ExpiresAt > now; // 同层持续交战的已见区域允许完成原有三点搜索，不增候选数。
    }

    /// <summary>复用有时效的导航折线，每次只交给原生最多十二米的实际路径。</summary>
    private bool StartSearchSegment(double now)
    {
        System.Numerics.Vector3 origin = GameAdapter.Snapshot(Owner.Position); // 与实际路线验证共用同一个位置快照。
        if (!SearchRoute.CanTake(origin, now)) { _searchPacing.Cancel(); return false; } // 过期或偏离不能先停看再假装还有有效路线。
        bool observe = Runtime.Options.SearchObservation && !_visible && !_playerDanger.IsActive(now) && !_recoveryRunning && !_needsRecovery; // 新危险、交战和恢复优先，不占用额外物理预算。
        if (_searchPacing.TryPause(observe, origin, SearchRoute.RemainingLength, Skill.TacticalProbability, now, Math.Max(_clue.ExpiresAt, _lastGunshot.ExpiresAt), out bool started)) // 只在剩余真实路线十八米内安排有限窗口。
        {
            if (started && Trace(DiagnosticEvent.SearchPaused, now)) WriteTrace("SEARCH_PAUSED", now, FormattableString.Invariant($"reason=approach-observation seconds={_searchPacing.PauseUntil - now:F2} remainingPath={SearchRoute.RemainingLength:F1} count={_searchPacing.Count}")); // 一次停看只记录一次，等待帧不重新计数。
            return true; // 已处理本帧等待，调用者不能另排一个搜索查询。
        }
        Span<System.Numerics.Vector3> points = stackalloc System.Numerics.Vector3[SearchRoute.Capacity + 2]; // 固定栈缓冲，不按帧分配工作数组。
        if (!SearchRoute.Take(origin, now, points, out int count, out bool final)) return false; // 消费路径前的停看不会丢失下一段。
        var segment = new Vector3[count]; // 原生移动器会持有数组，因此仅在真正提交一段时创建独立副本。
        for (int index = 0; index < count; index++) segment[index] = GameAdapter.Position(points[index]); // 所有转角都保留，不连直线穿墙。
        _searchSegmentFinal = final; // 中途段到达不能结束整个候选。
        _pendingKind = QueryKind.Search; // 后续卡住回退归属于调查路线。
        StartPath(segment, segment[count - 1], now); // 不发起二次 NavMesh 计算。
        Runtime.Diagnostics.Count(DiagnosticEvent.RouteSegment); // 可对照路径计算次数确认缓存复用。
        return true;
    }

    /// <summary>线索过期、超距或搜索结束时立即退出；原生已经开始的恢复继续执行。</summary>
    private void FinishInvestigation(double now, string reason)
    {
        if (State != BehaviorState.Investigate && State != BehaviorState.Search && State != BehaviorState.Advance) return; // 同帧重复回调不能再次清理。
        if (Trace(DiagnosticEvent.SearchFinished, now)) WriteTrace("SEARCH_FINISHED", now, $"reason={reason} pointIndex={_searchProgress.Index} reached={_searchProgress.Reached} failed={_searchProgress.Failed}"); // 分开实际完成、不可达、范围和记忆到期。
        _lastGunshot = default; // 本轮结束后不能用独立枪声快照立刻重启同一次调查。
        BehaviorState next = _recoveryRunning ? BehaviorState.Recover : BehaviorState.Native; // 不取消已有治疗或换弹。
        _policy.Reset(next); // 清除原搜索状态的最短承诺。
        _playerDanger.Clear(); // 完成推进后不能因旧危险寿命尚未结束而重新搜索。
        _pressure.Clear(); // 此次危险调查结束后不继承压制。
        _reportedPressure = PressureLevel.Calm; // 诊断基准随值状态一起重置。
        _searchPacing.Clear(); // 结束线索释放停看区域，真正的新调查可重新分配名额。
        ChangeState(next, now); // 立即取消查询和停止本模组的移动。
        if (next == BehaviorState.Native) Release(reason: "search-" + reason); // 本轮执行后就解除控制并记录原因。
        NextDecision = Math.Min(NextDecision, now); // 后续恢复需求和其他记忆仍可在共享轮转中处理。
    }

    /// <summary>玩家危险期间优先掩体；仅本人中弹、确认无掩体且看不到玩家时允许主动趴伏。</summary>
    private void Evade(double now)
    {
        using var measurement = new ActionMeasurement(Runtime, this, WorkPhase.ActionEvade); // 卧姿合法性与避险同步调用可单独定位。
        if (!_playerDanger.IsActive(now)) { NextDecision = Math.Min(NextDecision, now); return; } // 安静满五秒后由决策评估恢复或推进。
        if (AtCover && !_recoveryRunning) // 已验证且已到达的位置允许压力影响防守节奏。
        {
            _exposedDefenseActive = false; // 已有验证掩体时结束暴露自卫边沿。
            StopMotion(); // 到达掩体后先稳定位置，不能边离开边获得掩体还击资格。
            if (CanDefendFromCover(now)) AimAndFire(now); // 压力回落与短暂受惊结束后仍走完整射击验证。
            else { ReportFireWait(FireWaitCause.Evasion, now); StopAim(); } // 高压、缺少视觉或功能关闭时继续隐蔽。
            return; // 不在掩体内重复尝试趴伏或查询同一位置。
        }
        if (_recoveryRunning) { _exposedDefenseActive = false; ReportFireWait(FireWaitCause.Recovery, now); StopAim(); StopMotion(); return; } // 不取消已有医疗或换弹，姿态由统一入口处理。
        if (_hasCover && !AtCover) { _exposedDefenseActive = false; ReportFireWait(FireWaitCause.Evasion, now); StopAim(); MoveToCover(now); return; } // 已验证路径优先，不重复找掩体。
        if (_moving && _pendingKind == QueryKind.Escape) // 无掩体撤离路线不能被每帧 Evade 自己停掉。
        {
            if ((Owner.Position - _destination).sqrMagnitude > 1.44f || Owner.Mover.DistDestination > 1.2f) { _exposedDefenseActive = false; ReportFireWait(FireWaitCause.Evasion, now); StopAim(); return; } // 还未走完整路径就保持移动。
            StopMotion(); // 路线确实到达后才结束短距撤离。
            if (Trace(DiagnosticEvent.EscapeArrived, now)) WriteTrace("ESCAPE_ARRIVED", now, "reason=no-cover-no-prone"); // 分开查询成功与实际到达。
        }
        StopMotion(); // 评估原地防守或趴伏前不沿用旧搜索路线。
        if (AtCover) { _exposedDefenseActive = false; StopAim(); return; } // 已到掩体不额外趴下。
        if (Role != BotRole.Marksman && !_pending && !_coverUnavailable && now >= _retryAt) Request(QueryKind.Cover, Owner.Position, now); // 狙击模板不离岗，其他角色优先查询。
        if ((!_coverUnavailable && Role != BotRole.Marksman) || now < _nextProneCheck) { DefendOrStopAim(now); return; } // 掩体查询未结束时继续停火；卧姿检查间隔中仍可维持已合法的自卫瞄准。
        _nextProneCheck = now + 1; // 原生姿态合法性检查最多每秒一次。
        bool personallyHit = _playerDanger.HasRecentPersonalHit(now); // 近弹和队友中弹不能冒充本人受击。
        bool eligible = TacticalActionPolicy.CanProneAfterHit(personallyHit, _coverUnavailable, _hasCover, _pending, _visible, _moving); // 必须已经查明无掩体并失去真实视线。
        string? blocked = !eligible ? _visible ? "visible-target" : personallyHit ? "cover-or-route" : "no-personal-hit" : (Owner.Position - ThreatPoint(now)).sqrMagnitude < 64 ? "close-threat" : Owner.BotLay.IsLay ? "already-prone" : !Owner.GetPlayer.MovementContext.CanProne ? "native-posture-blocked" : null; // 合法性最多每秒检查一次。
        if (blocked != null)
        {
            if (Trace(DiagnosticEvent.DangerProneSkipped, now)) WriteTrace("DANGER_PRONE_SKIPPED", now, $"reason={blocked}"); // 区分没有执行与原生地形限制。
            bool canEscape = !_visible && blocked != "already-prone" && Role != BotRole.Marksman && Controlled && LayerSelected && !_pending && !_moving && !_hasCover && _coverUnavailable; // 对枪时不因不能趴伏而逃跑，失视且无掩体时仍可有限撤离。
            if (_escapeFallback.TryPlan(canEscape, GameAdapter.Snapshot(Owner.Position), GameAdapter.Snapshot(ThreatPoint(now)), now, out System.Numerics.Vector3 escapePoint)) // 每段危险最多两个有限近处候选。
            {
                Request(QueryKind.Escape, GameAdapter.Position(escapePoint), now); // 与其他紧急动作共享既有额度。
                if (Trace(DiagnosticEvent.EscapeRequested, now)) WriteTrace("ESCAPE_REQUESTED", now, $"posture={blocked} queued={_pending}"); // 未入队不能误记为已移动。
            }
            DefendOrStopAim(now); // 没有可行撤离且本人目视玩家时，才评估受限自卫。
            return;
        }
        Owner.BotLay.IsLay = true; // 通过原生卧姿控制器维护相关事件和动作状态。
        _ownsProne = !_savedProne; // 只对自己新增的姿态承担恢复责任。
        if (Trace(DiagnosticEvent.DangerProne, now)) WriteTrace("DANGER_PRONE", now, "reason=personal-hit-no-cover-no-visual"); // 日志证明三项必要条件均通过。
        DefendOrStopAim(now); // 完成卧姿后可在低压与原生瞄准就绪时自卫。
    }

    /// <summary>只在合法无掩体自卫窗口瞄准真人，其余避险状态立即撤销自有瞄准。</summary>
    private void DefendOrStopAim(double now)
    {
        if (!CanExposedReturnFire(now)) { _exposedDefenseActive = false; ReportFireWait(FireWaitCause.Evasion, now); StopAim(); return; } // 高压、移动、待查掩体或恢复中仍保留避险停火。
        if (!_exposedDefenseActive) // 窗口打开只记一次，不逐帧产生日志和计数。
        {
            _exposedDefenseActive = true; // 本次连续合法窗口已记录。
            if (Trace(DiagnosticEvent.ExposedDefense, now)) WriteTrace("EXPOSED_DEFENSE_READY", now, $"state={State} prone={Owner.BotLay.IsLay} pressure={_pressure.Level}"); // 仅表示允许尝试瞄准，实际射击仍看枪线与原生结果。
        }
        AimAndFire(now); // 原生瞄准就绪、反应门槛和射击验证均继续生效。
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
        using var measurement = new ActionMeasurement(Runtime, this, WorkPhase.ActionRecovery); // 首次用药或换弹慢调用不再淹没在整体动作阶段。
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
        var request = new WorkRequest(Id, Generation, kind, GameAdapter.Snapshot(position), GameAdapter.Snapshot(ThreatPoint(now)), now + (kind == QueryKind.Search ? 4 : State == BehaviorState.Evade ? 2.5 : 1)); // 搜索替代候选最多四秒，排队不延长截止。
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
        if (Disposed || Owner == null || !LayerSelected || !Controlled || !Participating) return false; // 层停止后迟到查询不能继续写动作。
        bool playerContext = Participating && (_playerDanger.IsActive(Time.time) || Owner.Memory.GoalEnemy == null || GameAdapter.IsPlayerTarget(Owner.Memory.GoalEnemy)); // 目标已切成 AI 时拒绝旧玩家任务。
        return !Disposed && playerContext && Controlled && _pending && request.Owner == Id && request.Generation == Generation && Owner.BotState == EBotState.Active &&
            (request.Kind != QueryKind.Escape || (State == BehaviorState.Evade && _playerDanger.IsActive(Time.time) && !_hasCover)) && // 迟到的撤离路线不能覆盖新掩体。
            (request.Kind != QueryKind.Reposition || ((State == BehaviorState.Observe || State == BehaviorState.Engage) && _visible && !_playerDanger.IsActive(Time.time))) && // 视线或交战状态失效后不再换位。
            (request.Kind != QueryKind.Cover || ((_hasClue || _playerDanger.IsActive(Time.time)) && System.Numerics.Vector3.DistanceSquared(request.Threat, GameAdapter.Snapshot(ThreatPoint(Time.time))) <= 25)); // 查询结果必须对应当前危险区域。
    }

    /// <summary>验证观察身份、来源和时间，视觉检查之外不读取隐藏身体位置。</summary>
    internal bool TryVisibleObservation(EnemyInfo? enemy, double now, out Observation observation)
    {
        observation = default; // 失败时不给出伪造瞄准坐标。
        return GameAdapter.IsPlayerTarget(enemy) && GameAdapter.DirectlyVisible(enemy, now) && Memory.TryGet(enemy!.ProfileId, now, out observation) &&
            observation.Source == ObservationSource.Vision && observation.ObservedAt == enemy.PersonalLastSeenTime; // 观察必须对应原生最新的个人检查。
    }

    /// <summary>完整短路线证明已经站在搜索候选附近时直接驻留，不提交零长度移动。</summary>
    internal void SearchArrived(in WorkRequest request, double now)
    {
        if (!Accepts(request) || request.Kind != QueryKind.Search) return; // 迟到查询不能推进新的搜索候选。
        _pending = false; // 释放当前请求。
        _queryFailures = 0; // 真实完成解除失败累计。
        _arrivedAt = now; // 下一动作帧按正常一秒驻留处理。
        _searchSegmentFinal = true; // 只代表这一候选的合法导航终点。
        SearchRoute.Clear(); // 不保留之前的长路线。
        Runtime.CompletedQueries++; // 完整短路验证也属于一次完成查询。
        Runtime.Diagnostics.Count(DiagnosticEvent.QuerySucceeded); // 与总量保持一致。
        if (Trace(DiagnosticEvent.MoveArrived, now)) WriteTrace("MOVE_ARRIVED", now, $"state={State} final=True pointIndex={_searchProgress.Index} reason=already-at-validated-point"); // 不把隔墙的欧氏距离当作到达证明。
    }

    /// <summary>提交成功路径或掩体缓存，禁止使用已被原生层抢占的结果。</summary>
    internal void QuerySucceeded(in WorkRequest request, Vector3 point, Vector3[] path, double now)
    {
        if (!Accepts(request)) return; // 最终写入前再次检查控制权。
        _pending = false; // 解除查询等待。
        _queryFailures = 0; // 成功后清除连续失败次数。
        _retryAt = now + 0.8; // 避免同一敌情立即再次查找。
        Runtime.CompletedQueries++; // 记录真实完成而非仅排队的请求。
        if (Trace(DiagnosticEvent.QuerySucceeded, now)) WriteTrace("QUERY_SUCCEEDED", now, $"corners={path.Length} generation={Generation} {Query.Details()}"); // 只有日志额度通过才格式化坐标和长度。
        if (request.Kind == QueryKind.Cover) // 掩体结果先交给决策层，不强制立即移动。
        {
            if (_coverShiftRequested) { _coverShiftRequested = false; _coverShiftCommitted = true; } // 新掩体验证后保持换位意图，直到实际到达或危险打断。
            if (!_hasCover || (_cover - point).sqrMagnitude > 0.01f) _coverArrival.Reset(); // 新位置不能继承旧掩体的到达缓冲。
            _cover = point; // 缓存已验证位置。
            _coverPath = path; // 缓存本次计算结果。
            _coverThreat = GameAdapter.Position(request.Threat); // 缓存对应的合法威胁方向。
            _coverExpires = now + 8; // 设置有限的缓存寿命。
            _hasCover = true; // 允许下次决策考虑掩体动作。
            NextDecision = Math.Min(NextDecision, now); // 有新候选后及时重新评分。
        }
        else if (request.Kind == QueryKind.Search) StartSearchSegment(now); // 完整路线按缓存折线分段推进。
        else StartPath(path, point, now); // 普通近处重规划直接执行。
    }

    /// <summary>失败最多连续重试两次，随后暂时放弃当前目标。</summary>
    internal void QueryFailed(double now, string reason)
    {
        bool failedShift = _coverShiftRequested; // 换位失败不能把已经使用的旧掩体误判为不可用。
        _coverShiftRequested = false; // 同一视觉线索的尝试记录仍阻止立即重扫。
        switch (reason) // 明细被限频时仍能根据累计原因判断瓶颈。
        {
            case "deadline": Runtime.Diagnostics.Count(DiagnosticEvent.QueryDeadline); break; // 排队或多阶段执行超过有效期。
            case "no-candidates": Runtime.Diagnostics.Count(DiagnosticEvent.QueryNoCandidates); break; // 局部场景没有合适候选。
            case "nav-sample": Runtime.Diagnostics.Count(DiagnosticEvent.QueryNavSample); break; // 候选无法投影到导航网格。
            case "no-occlusion": Runtime.Diagnostics.Count(DiagnosticEvent.QueryNoOcclusion); break; // 候选没有阻挡已知威胁。
            case "path-incomplete": Runtime.Diagnostics.Count(DiagnosticEvent.QueryPathIncomplete); break; // 路径不可达或只返回部分路径。
            case "path-corners": Runtime.Diagnostics.Count(DiagnosticEvent.QueryPathCorners); break; // 角点数量超出合理范围。
            case "path-bounds": Runtime.Diagnostics.Count(DiagnosticEvent.QueryPathBounds); break; // 路径绕行过长或终点偏离。
            case "nav-source": Runtime.Diagnostics.Count(DiagnosticEvent.QueryNavSource); break; // 起点不能投影，不能用扩目标半径掩盖。
            case "path-source": Runtime.Diagnostics.Count(DiagnosticEvent.QueryPathSource); break; // 查询期间起点移动或源点断层。
            case "path-length": Runtime.Diagnostics.Count(DiagnosticEvent.QueryPathLength); break; // 实际绕路超过有界上限。
            case "path-endpoint": Runtime.Diagnostics.Count(DiagnosticEvent.QueryPathEndpoint); break; // 完整路径末端与候选不一致。
        }
        Runtime.CancelQueries(Id); // 清除普通与紧急队列中的同一请求。
        if (State == BehaviorState.Evade && _pendingKind == QueryKind.Cover && reason != "deadline" && reason != "stuck") _coverUnavailable = true; // 实际候选验证失败才允许无掩体趴伏。
        _pending = false; // 防止超时造成永久等待。
        SearchRoute.Clear(); // 错误路线不能通过缓存反复提交。
        _queryFailures++; // 统计当前目标的连续失败。
        _retryAt = now + (_queryFailures >= 2 ? 5 : 0.8); // 达到上限后使用更长冷却。
        Runtime.FailedQueries++; // 诊断失败和排队成功分开计算。
        if (Trace(DiagnosticEvent.QueryFailed, now)) WriteTrace("QUERY_FAILED", now, FormattableString.Invariant($"reason={reason} state={State} failures={_queryFailures} retrySeconds={_retryAt - now:F1} ") + Query.Details()); // 限频后记录具体源点、候选和路径状态。
        if (_queryFailures >= 2) // 无法到达时允许行为继续而不是无限重算。
        {
            if (!failedShift) // 换位失败时保留原来已经验证的掩体。
            {
                _hasCover = false; // 普通连续失败则放弃不可用掩体。
                _coverPath = null; // 释放失败缓存。
            }
            if (_pendingKind == QueryKind.Search && (State == BehaviorState.Search || State == BehaviorState.Investigate || State == BehaviorState.Advance))
            {
                if ((reason == "nav-sample" || reason == "path-incomplete" || reason == "path-length") && _searchProgress.Index < 3) _failedSearch.Record(GameAdapter.Snapshot(_searchPoints[_searchProgress.Index]), now); // 连续导航失败才短期记住该搜索点。
                _searchProgress.CompletePoint(false); _arrivedAt = 0; _searchSegmentFinal = false; // 掩体和换位失败不能误耗搜索点。
            }
            _queryFailures = 0; // 冷却后下一目的地可重新验证。
        }
        NextDecision = Math.Min(NextDecision, now); // 让状态机考虑回退。
    }

    /// <summary>把完整路径直接交给原生移动器，避免 GoToPoint 的重复寻路。</summary>
    private void StartPath(Vector3[] path, Vector3 point, double now)
    {
        using var measurement = new ActionMeasurement(Runtime, this, WorkPhase.ActionMove); // 原生路径接收可能触发同步事件，单独计量。
        if (path.Length < 2 || !Controlled || !LayerSelected) return; // 退化路径和失去实际层选择时禁止移动。
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
        _failedSearch.Clear(); // 场景对象销毁时不保留旧搜索位置。
        _blockedShots.Clear(); // 不跨 Bot 生命周期复用目标与额度。
        _recentSight.Clear(); // 销毁时清空最后目击位置与短期资格。
        _footwork.Clear(); // 销毁时清空侧移窗口与目标身份。
        try { Release(reason: "dispose"); } // 尽量恢复原生动作参数。
        catch (Exception exception) { Runtime.Log.LogWarning($"Bot {Id} 清理时对象已失效：{exception.Message}"); } // 销毁过程仅记录一次。
        finally { Disposed = true; } // Release 已在游戏对象访问之前撤销队列，重复扫描没有必要。
    }
}
