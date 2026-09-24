using BepInEx.Configuration;

namespace AiBehavior.Client;

/// <summary>启动时读取的配置快照，战局中不会改变已注册的行为层。</summary>
public sealed class PluginOptions
{
    public readonly bool Enabled;
    public readonly bool ManageScavs;
    public readonly bool CoverFailureMemory;
    public readonly bool SuppressionResponse;
    public readonly bool SearchObservation;
    public readonly bool CoverCommitment;
    public readonly bool CombatFootwork;
    public readonly bool CombatRangeControl;
    public readonly bool CombatTemperament;
    public readonly bool CoordinatedAdvance;
    public readonly bool AdaptiveFireMode;
    public readonly bool FastRepeekFire;
    public readonly bool ExposedSelfDefense;
    public readonly bool ShortRush;
    public readonly bool FootstepAmbush;
    public readonly bool CoverShift;
    public readonly bool TacticalGrenades;
    public readonly bool AdaptiveAim;
    public readonly int MinimumLevel;
    public readonly int MaximumLevel;
    public readonly double MainThreadMilliseconds;
    public readonly double RayRate;
    public readonly double PathRate;
    public readonly double SampleRate;
    public readonly double OverlapRate;
    public readonly float SummarySeconds;
    public readonly bool TraceBots;
    public readonly bool EventLogging;
    public readonly int EventLogsPerSecond;

    /// <summary>绑定中文配置说明，结构性参数重启游戏后生效。</summary>
    public PluginOptions(ConfigFile config)
    {
        Enabled = config.Bind("General", "Enabled", true, "启用拟真行为。更改后重启游戏。").Value;
        ManageScavs = config.Bind("General", "ManageScavs", true, "普通和狙击 Scav 使用固定模板；Boss、护卫与其他特殊角色保持原生。").Value;
        CoverFailureMemory = config.Bind("Tactics", "CoverFailureMemory", true, "在已到达的掩体处被玩家命中后，十二秒内跳过该位置两米内的候选；固定四项。重启生效。").Value;
        SuppressionResponse = config.Bind("Tactics", "SuppressionResponse", true, "玩家命中与近弹形成衰减压力；到达掩体、压力回落且满足视线和武器条件时可还击。关闭则保留旧版五秒内避险停火。重启生效。").Value;
        SearchObservation = config.Bind("Tactics", "SearchObservation", true, "沿已验证路线进入线索十八米路径范围时短暂停看，每区域最多两次，不延长线索。重启生效。").Value;
        CoverCommitment = config.Bind("Tactics", "CoverCommitment", true, "普通视线波动不打断已验证的掩体移动；危险、恢复、失效与原生抢占仍可中断。重启生效。").Value;
        CombatFootwork = config.Bind("Tactics", "CombatFootwork", true, "本插件实际控制的普通 Scav 与 PMC 在六至二十五米目视交战中，无已验证掩体且稳定目视后至多主动侧移一次；复用原导航预算。重启生效。").Value; // 可单独关闭原有主动侧移以便实测对照。
        CombatRangeControl = config.Bind("Tactics", "CombatRangeControl", true, "一至六米贴脸交战中，稳定目视后至多后撤三米一次；与主动侧移共用一次名额和导航预算。重启生效。").Value; // 独立开关便于验证贴脸控距是否自然。
        CombatTemperament = config.Bind("Tactics", "CombatTemperament", true, "PMC 出生时固定谨慎、均衡或主动风格，仅微调掩体倾向；等级能力仍线性成长。重启生效。").Value; // 只在交战情境形成时改变一次概率比较。
        CoordinatedAdvance = config.Bind("Tactics", "CoordinatedAdvance", true, "玩家直接命中同组 Bot 后，近邻先避险，再由一名合格成员有限推进；无合格队友时受害者沿用原规则。重启生效。").Value; // 复用已有受击单跳告警，不新增逐帧组扫描。
        AdaptiveFireMode = config.Bind("Tactics", "AdaptiveFireMode", true, "只在本插件实际控制且亲眼看见玩家时，按距离与武器支持模式有限切换射击模式；交还控制时恢复。重启生效。").Value; // 单独开关便于局内排查武器兼容性。
        FastRepeekFire = config.Bind("Tactics", "FastRepeekFire", true, "已完成反应准备的 Bot 在八秒内亲眼看见玩家从原区域再次露头时，不重复等待模组反应计时；原生瞄准和枪线检查仍生效。重启生效。").Value; // 新目标或未完成准备的短暂目击没有快速射击资格。
        ExposedSelfDefense = config.Bind("Tactics", "ExposedSelfDefense", true, "玩家枪弹危险中确认无可用掩体时，仍亲眼看见玩家的低压 Bot 可在短暂受惊后自卫；主动趴伏仅限本人中弹且失视。重启生效。").Value; // 避险路线、恢复、高压和射击安全仍优先。
        ShortRush = config.Bind("Tactics", "ShortRush", true, "PMC 失去近距亲眼目视后可在短暂守点结束时沿旧位置突击，仍需完整导航路线。重启生效。").Value; // 等级仅影响机会，不提高查询频率。
        FootstepAmbush = config.Bind("Tactics", "FootstepAmbush", true, "近距听到本机玩家脚步时短暂停看声音估计区域，不凭声音开枪。重启生效。").Value; // 普通 Scav 使用固定行为模板。
        CoverShift = config.Bind("Tactics", "CoverShift", true, "在掩体内失去玩家视线后有限尝试一次局部换位，复用掩体和路径预算。重启生效。").Value; // 危险和原生恢复可抢占。
        TacticalGrenades = config.Bind("Tactics", "TacticalGrenades", true, "仅向近期亲眼目击的玩家旧位置尝试投掷，检查同组友军并受全局每秒一次轨迹额度限制。重启生效。").Value; // 投掷仍由游戏原生接口执行。
        AdaptiveAim = config.Bind("Tactics", "AdaptiveAim", true, "只在亲眼看见本机玩家的自有交战层中，贴脸腰射、远距举枪；交还控制时恢复原生瞄准状态。重启生效。").Value; // 使用距离迟滞和每四分之一秒检查一次。
        MinimumLevel = config.Bind("PMC", "MinimumLevel", 1, new ConfigDescription("PMC 成长起点。", new AcceptableValueRange<int>(1, 999))).Value;
        MaximumLevel = config.Bind("PMC", "MaximumLevel", 60, new ConfigDescription("PMC 成长饱和点，并非游戏等级上限。", new AcceptableValueRange<int>(2, 1000))).Value;
        MainThreadMilliseconds = config.Bind("Performance", "DecisionBudgetMs", 0.5f, new ConfigDescription("每帧高层决策和查询的软时间片，不含原生运动与动画。", new AcceptableValueRange<float>(0.1f, 5f))).Value;
        RayRate = config.Bind("Performance", "RaysPerSecond", 120, new ConfigDescription("全局新增射线每秒补充量，单帧上限八次。", new AcceptableValueRange<int>(20, 600))).Value;
        PathRate = config.Bind("Performance", "PathsPerSecond", 12, new ConfigDescription("全局显式路径计算每秒补充量，单帧上限一次。", new AcceptableValueRange<int>(1, 60))).Value;
        SampleRate = config.Bind("Performance", "SamplesPerSecond", 36, new ConfigDescription("全局导航位置采样每秒补充量，单帧上限三次。", new AcceptableValueRange<int>(3, 180))).Value;
        OverlapRate = config.Bind("Performance", "OverlapsPerSecond", 6, new ConfigDescription("全局局部掩体扫描每秒补充量，单帧上限一次。", new AcceptableValueRange<int>(1, 30))).Value;
        SummarySeconds = config.Bind("Diagnostics", "SummarySeconds", 30f, new ConfigDescription("汇总日志间隔，零表示关闭。", new AcceptableValueRange<float>(0f, 300f))).Value;
        TraceBots = config.Bind("Diagnostics", "TraceBots", false, "额外输出每个 Bot 的注册和能力明细，不受关键事件限频限制，通常保持关闭。").Value;
        EventLogging = config.Bind("Diagnostics", "EventLogging", true, "记录限频的关键行为事件；关闭后仍保留累计计数和战局最终汇总。").Value;
        EventLogsPerSecond = config.Bind("Diagnostics", "EventLogsPerSecond", 8, new ConfigDescription("所有 Bot 共享的明细日志每秒补充量及突发上限；同 Bot 同事件至少间隔两秒。", new AcceptableValueRange<int>(1, 30))).Value;
    }
}
