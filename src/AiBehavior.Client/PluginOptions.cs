using BepInEx.Configuration;

namespace AiBehavior.Client;

/// <summary>启动时读取的配置快照，战局中不会改变已注册的行为层。</summary>
public sealed class PluginOptions
{
    public readonly bool Enabled;
    public readonly bool ManageScavs;
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
