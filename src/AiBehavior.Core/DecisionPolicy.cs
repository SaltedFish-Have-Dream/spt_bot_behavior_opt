namespace AiBehavior.Core;

public enum BehaviorState { Native, Observe, Engage, Cover, Investigate, Search, Recover, Disengage, Evade, Advance }

/// <summary>传入决策的值快照，避免纯逻辑访问游戏世界。</summary>
public struct DecisionInput
{
    public bool HasClue;
    public bool Visible;
    public bool Reacted;
    public bool SoundOnly;
    public bool NeedsRecovery;
    public bool RecoveryRunning;
    public bool HasCover;
    public bool AtCover;
    public bool UnderFire;
    public bool LowHealth;
    public bool CanMove;
    public int ContextVersion;
    public bool PlayerDanger;
    public bool AdvanceAfterDanger;
    public bool MovingToCover;
    public bool WatchLastSeen;
}

/// <summary>带动作承诺时间的有限策略，相同情境不重复抽取战术概率。</summary>
public sealed class DecisionPolicy
{
    private int _lastContext = int.MinValue;
    private bool _preferTactical;
    private double _holdUntil;
    public BehaviorState State { get; private set; }

    /// <summary>停用或调查结束时同步清空动作承诺，避免旧搜索状态阻止原生接手。</summary>
    public void Reset(BehaviorState state = BehaviorState.Native)
    {
        State = state;
        _holdUntil = 0;
        _lastContext = int.MinValue;
        _preferTactical = false;
    }

    /// <summary>只在新情境重抽倾向，恢复锁、线索失效和视线改变可以及时覆盖旧动作。</summary>
    public BehaviorState Decide(in DecisionInput input, in SkillProfile profile, double now, float random)
    {
        if (_lastContext != input.ContextVersion) // 只对新敌情或动作结果抽取一次。
        {
            _lastContext = input.ContextVersion; // 记录本次情境代次。
            _preferTactical = random < profile.TacticalProbability; // 高等级提高合理选择概率，不增加计算量。
        }
        BehaviorState next; // 先计算合法动作，再应用普通动作持有时间。
        if (input.PlayerDanger) next = BehaviorState.Evade; // 玩家中弹或近弹先避险，执行器不取消已经开始的原生恢复。
        else if (input.RecoveryRunning) next = BehaviorState.Recover; // 已开始的原生恢复不得被普通决策打断。
        else if (!input.HasClue) next = input.NeedsRecovery ? BehaviorState.Recover : BehaviorState.Native; // 无线索时交还巡逻或完成自救。
        else if (input.MovingToCover && input.HasCover && !input.AtCover && input.CanMove) next = input.NeedsRecovery || input.LowHealth || State == BehaviorState.Disengage ? BehaviorState.Disengage : BehaviorState.Cover; // 先走完有效掩体路线，普通视线波动和新的战术抽签不能让 Bot 停在半路。
        else if (input.NeedsRecovery && (!input.Visible || input.AtCover)) next = BehaviorState.Recover; // 只在相对安全时开始恢复。
        else if (input.WatchLastSeen && !input.Visible && !input.LowHealth) next = BehaviorState.Observe; // 只守最后亲眼看见的出口片刻，不凭旧点开火。
        else if (input.AdvanceAfterDanger && input.NeedsRecovery) next = input.HasCover && input.CanMove && !input.AtCover ? BehaviorState.Disengage : BehaviorState.Recover; // 缺弹或需治疗时不空手前压，恢复执行仍检查安全条件。
        else if (input.AdvanceAfterDanger && input.LowHealth) next = input.NeedsRecovery ? BehaviorState.Recover : BehaviorState.Observe; // 伤势严重且无恢复条件时不强制冲锋。
        else if (input.AdvanceAfterDanger && input.CanMove) next = BehaviorState.Advance; // 安静五秒且自身状态允许时接近危险快照。
        else if (input.LowHealth && input.HasCover && input.CanMove && !input.AtCover) next = BehaviorState.Disengage; // 不利状态优先返回已验证位置。
        else if (input.Visible && !input.Reacted) next = BehaviorState.Observe; // 尚未反应时不直接开火。
        else if (input.Visible && input.CanMove && input.HasCover && !input.AtCover && (input.UnderFire || _preferTactical)) next = BehaviorState.Cover; // 有合法掩体才允许移动。
        else if (input.Visible) next = BehaviorState.Engage; // 其余可见交战维持当前射击窗口。
        else next = input.SoundOnly ? BehaviorState.Investigate : BehaviorState.Search; // 失去视觉后只搜索已有线索。
        bool urgent = input.PlayerDanger || State == BehaviorState.Evade || input.RecoveryRunning || !input.HasClue || State == BehaviorState.Native ||
            ((!input.HasCover || !input.CanMove) && (State == BehaviorState.Cover || State == BehaviorState.Disengage)) || // 掩体失效或失去移动资格必须立即解除承诺。
            input.Visible != (State == BehaviorState.Observe || State == BehaviorState.Engage || State == BehaviorState.Cover) ||
            (State == BehaviorState.Observe && input.Reacted) || input.AtCover || input.WatchLastSeen; // 新失视可立即守点，结束由下方的可见性边沿及时退出。
        if (next != State && (urgent || now >= _holdUntil)) // 普通战术切换需要满足最短持有时间。
        {
            State = next; // 提交新状态。
            _holdUntil = now + 0.6; // 防止左右横跳与频繁重建动作。
        }
        return State;
    }
}
