# 0.1.6 实测日志说明

关键事件默认开启，日志位于游戏安装的 `BepInEx/LogOutput.log`。本机路径为 `E:\Games\Escape From Tarkof\EFT v4.1\BepInEx\LogOutput.log`。同时替换两个 DLL 并重启游戏，确认 `event=START version=0.1.6` 和 `NAVIGATION_RULES`；本版新增撤离换位、挡枪侧移及失败搜索点跳过的证据。此前的压力、掩体、姿态和控制交接日志继续保留。

## 实测步骤

1. 关闭游戏后更新插件；停用 SAIN、ORBIT，保留 BigBrain 1.5.0 和 Waypoints 1.9.0。默认 `EventLogging=true`、`EventLogsPerSecond=8`、`SummarySeconds=30` 即可。
2. 开始一局少量 Bot 的战局，尽量覆盖接敌、遮挡后搜索、掩体移动和换弹；记下地图、大致时间、Bot 数量及看到的异常。
3. 正常结束战局回到菜单，让战局清理日志写出。若发生崩溃，同样保留现有日志，缺少最终汇总本身也是证据。
4. **再次启动游戏前保留日志**，避免原日志被覆盖。可运行项目中的 `tools/Collect-Logs.ps1`，或告知助手“实测结束”，由助手读取并归档。

归档脚本只读游戏文件，在项目 `artifacts/playtests/时间-随机后缀/` 下保存完整 `LogOutput.log`、本插件配置、BepInEx 配置及 `capture.json`。它支持读取游戏仍在写入的日志，但快照不包含归档之后才产生的内容。使用其他测试安装时传入 `-GameRoot`。

## 如何判断执行链路

| 日志事件 | 能证明什么 | 判断边界 |
| --- | --- | --- |
| `START / CONFIG` | 插件进入初始化、实际 DLL 标识与配置 | 不代表初始化成功 |
| `PATCH_APPLIED / READY` | 基础十一个方法；AI Limit 适配成功时十二个 | 以 `expectedMethods` 核对；初始化不代表已执行行为 |
| `PLAYER_RULES` | 本版运行模式、距离、危险时长和 Boss 范围 | `mode=local-human-only` 是固定模式，没有默认扩大至 AI 或远程玩家 |
| `TACTICAL_RULES` | 四个战术开关及固定容量/时长配置 | 启动记录不代表对应行为已经执行 |
| `NAVIGATION_RULES` | Waypoints 实际加载并进入本版导航规则 | 不代表地图所有位置都有完整可达路径 |
| `ESCAPE_REQUESTED / ESCAPE_ARRIVED` | 卧姿受阻后的有限短距撤离申请及实际到达 | `queued=False` 或只有查询成功均不代表实际位移 |
| `REPOSITION_REQUESTED / REPOSITION_ARRIVED` | 连续世界障碍挡枪后申请侧移及到达 | 仍须核对目标真实可见、后续射击安全结果 |
| `SEARCH_CANDIDATE_SKIPPED` | 十五秒内跳过近期确认不可达的搜索点 | 表示失败候选，不计作搜到玩家位置 |
| `PRESSURE_CHANGED` | 个人压力等级发生变化，含前后等级与当前值 | 只接受玩家个人命中/近弹，队友告警不加个人压力；明细限频可能省略中间等级 |
| `COVER_INVALIDATED` | 到达自有掩体后遭玩家命中，失效点已撤销 | 不代表替代掩体已经找到，后续仍看查询与移动链路 |
| `SEARCH_PAUSED` | 已开始一次接近线索时的有限停看，含时长、剩余路线和次数 | 每区域最多两次；不代表卡住，也不延长线索或路线寿命 |
| `COVER_MOVE_KEPT` | 掩体族状态切换时保留同一有效移动路线 | 同状态持续移动不重复记录，不等于已到达 |
| `PLAYER_SCOPE_ENTERED / PLAYER_SCOPE_LEFT` | 从真人线索激活或交还原生的边沿 | 直接受击也可直接进入 `Evade`，不必先出现 ENTERED |
| `PLAYER_DANGER` | 玩家枪弹危险已保存且设置避险状态 | `kind=hit/near-bullet/ally-hit`；实际动作仍看 `ACTION_ENTERED` |
| `DANGER_PRONE` | 无验证可用掩体后已调用原生合法卧姿 | 不能仅靠此日志证明模型姿态完成或保证不会被命中 |
| `POSE_CHANGED` | 本模组真正提交了新蹲站目标，含前值、目标、状态、危险/移动/掩体条件 | 相同目标不重复写；不代表动画已完成，也不记录其他插件的姿态写入 |
| `POSE_RESTORED` | 行为层交还控制，恢复仍属于本模组的目标姿态 | 原生已改写目标时不覆盖；未改过姿态或重复释放不计数 |
| `PLAYER_SCOPE` | 玩家增强参与数、紧急队列、弹道积压/丢弃、告警合并 | 与活动数、地图交火情况及 `TIMING` 联合判断 |
| `PLAYER_EVENT_FAILED` | 当前局的命中或近弹入口异常并被停用 | 原生伤害/弹道保留；必须看异常栈，不能算本版测试通过 |
| `HEARTBEAT` | 插件仍在更新，目前未观察到 Bot 激活 | 在菜单正常；战局内长期如此需排查激活入口 |
| `RAID_OBSERVED` | 本次进程已收到某局首次 Bot 激活 | 时间不是地图加载起点 |
| `REGISTERED / NATIVE_SKIPPED / BRAIN_SKIPPED` | 受管上下文建立或明确旁路原因 | 普通注册明细可能限频；每局前 32 种未知角色/脑型组合首次必记，不消耗普通令牌 |
| `BRAIN_SKIPPED_OVERFLOW` | 未知组合已超过固定容量，后续仅保留总计数 | 每局最多一次，不代表停止兼容回退 |
| `DEACTIVATED / REACTIVATED` | 受管 Bot 活动状态发生真实边沿变化 | 重复停用帧不重复清理，不能单凭此日志认定是哪个模组切换状态 |
| `STATE_CHANGED` | 高层选择了新状态与当时条件 | 尚不能证明 BigBrain 采用了动作 |
| `CONTROL_CLAIMED / ACTION_ENTERED` | 实际取得控制权并进入对应动作分支 | 不代表移动已到达或治疗已完成 |
| `CONTROL_RELEASED` | 自有动作退出或被其他层抢占 | 结合状态判断正常交接还是反复切换 |
| `CONTROL_WAITING` | 有接管资格但持续未获得控制，包含 `selected/nativeLayer/grenade` | 原生高优先级行为可以正常阻止接管，不自动等于故障 |
| `AI_LIMIT_COMPAT / AI_LIMIT_PRIORITY` | 可选适配启用及玩家情境对象被提高活动排序 | 不改变数量上限，不证明已激活；激活仍受原插件检查周期影响 |
| `SIGHT_CHANGED / SOUND_SAVED / MEMORY_EXPIRED` | 感知边沿、声音入库与旧目标记忆失效 | 视觉回调总量见 `VisionSaved` |
| `SOUND_OUT_OF_RANGE / SEARCH_FINISHED` | 声音调查点超距被过滤，或调查因到期/完成/超距而结束 | 超距声音不删除已有视觉记忆；结束自有搜索不等于已经证明原生巡逻恢复 |
| `QUERY_SUCCEEDED / MOVE_STARTED` | 完整查询通过、路径已提交原生移动器 | 路径提交不等于最终到达 |
| `MOVE_PROGRESS / MOVE_ARRIVED` | 实际位置发生变化，或走到经导航验证的当前段终点 | `final=False` 只是中间段；观察完整线索距离变化及后续段 |
| `SEARCH_RETRY / SEARCH_FINISHED` | 有期限的失败重试，以及 `reached/failed` 最终计数 | `area-checked` 为完成，`unreachable` 为失败退出，不再混称候选耗尽 |
| `DANGER_PRONE_SKIPPED` | 已无验证掩体但卧姿检查未提交，含贴脸、已卧姿或原生阻止 | 不能把每条跳过记录都当成错误 |
| `ACTION_SLOW` | 两毫秒以上的动作子调用，含阶段、Bot、状态与耗时 | 墙钟时间含同步原生调用、GC 或调度停顿；明细仍限频 |
| `QUERY_FAILED / QUERY_REJECTED / STUCK` | 查询失败原因、入队拒绝或移动无进展 | 单次找不到掩体不一定是缺陷，应看重复次数及场景 |
| `SHOT_BLOCKED / SHOT_EXPIRED` | 实体/人员阻挡，或射击请求等待到期 | 不能只看 `blockedShots` 判定 Bot 无法射击 |
| `SHOT_NATIVE_RESULT` | 本插件放行后，原生新连射调用返回接受或拒绝 | 接受不等于命中、击杀，也不是逐发弹药计数 |
| `RECOVERY_REQUESTED` | 已调用原生换弹/急救，及调用后的运行标志 | 请求不等于恢复完成 |
| `SUMMARY / COUNTERS` | 当前受管状态、本局累计工作量和原因计数 | 未实测完整帧时间，不据此直接宣称 FPS 收益 |
| `TIMING` | 分阶段累计、调用峰值、最差帧阶段组成与局内时刻 | 独占阶段可以相加；含子调用的单次峰值不能相加 |
| `RAID_END` | 战局清理后剩余受管对象和任务数量 | 正常应为 `remainingBots=0 pending=0` |
| `INIT_FAILED / REGISTER_FAILED / FALLBACK` | 初始化、注册或运行降级及异常栈 | 结合完整日志中的 Harmony/BigBrain 错误定位 |

`SUMMARY.states` 的固定顺序为 `Native,Observe,Engage,Cover,Investigate,Search,Recover,Disengage,Evade,Advance`，最后两项分别是玩家危险避险、安全窗口后的有限推进；不要把状态编号当成角色编号。

## 计数判读

- `COUNTERS` 是本局累计值，周期日志之间取差值才是该段增量；重进战局会重置。最终汇总在清理前生成，清理结果由后面的 `RAID_END` 单独报告。
- `active/inactive` 是汇总时活动/停用受管数，两者之和为 `bots`，不是地图全部 AI 数。
- `PLAYER_SCOPE.participating` 是实际处于玩家情境的受管数；AI 单独互战时应不因 AI 事件增长。已有玩家记忆尚未失效则可能继续参与，不能把人数非零直接认定为串入 AI 事件。
- `urgentPending/urgentExpired` 为紧急查询；`SUMMARY.pending/expired` 仍是普通查询，`shotPending/shotExpired` 为射击队列，三者分别判读。
- `bulletChecks` 是近弹候选筛选总数，包含失活/非敌对候选的过滤；`bulletPending` 上限 64，`bulletDropped/bulletExpired` 增长意味着部分近弹通知被舍弃，直接命中不经过该队列。
- `allyAlertsSuppressed` 是合并的受击广播次数，不是漏判直接受害者的次数；`hitEnabled/bulletEnabled` 正常为 `True`。
- `PressureChanged/CoverInvalidated/SearchPaused/CoverMoveKept` 是上述真实行为边沿的累计数。短期压力清空只重置内部基准，不额外生成退出事件；后续新压力会从平静状态重新判读。
- `CoverCandidateRejected` 是候选被失效记忆过滤的次数，包含粗选和导航阶段，后续请求成功也会保留；不是唯一掩体数或请求失败数。`QUERY_FAILED reason=cover-rejected` 仅代表请求最终因此失败。
- `ShotPressureBlocked` 是实际接管的避险动作尚不满足掩体还击条件时的拒绝次数，包含高压、刚受击、无有效掩体/视线和功能关闭后的旧版停火规则。不是独立 Bot 数；还击仍须查看既有射击验证链路与 `SHOT_NATIVE_RESULT`。
- `PoseChanged/PoseRestored` 统计实际调用姿态接口的次数。连续近弹与路径重规划不应导致 `target=0.00/0.90` 高频交替；普通意图需稳定 0.75 秒，真实新危险可立即压低。判读时区分同一 Bot 与不同 Bot 的事件，结合 `CONTROL_RELEASED/CLAIMED` 判断控制交接。
- `CONTROL_CLAIMED reason=selected-action-resume` 和 `ControlResumed` 表示 BigBrain 实际执行本动作时恢复了租约；不能从 SafetyTick 或外部限流回调强行接管。
- `QUERY_FAILED` 新增 `nav-source/path-source/path-length/path-endpoint`，原 `path-bounds` 保留为旧版计数兼容。明细包含查询种类、候选序号、原始/投影/起点坐标、路径长度和 NavMesh 状态；失败时可能保留最后尝试的状态，需与原因及候选序号一起看。坐标均来自事件快照或本 Bot，不用于隐藏目标实时跟踪。
- `RouteSegment` 对照 `paths` 可检查缓存复用；`SearchRetry` 最多每个搜索区域一次额外轮次，新区域或重新接管会重建搜索会话。候选尝试及请求失败不是独立 Bot 数量。
- `TIMING` 新增 `ActionAim/ActionAimClear/ActionSearch/ActionMove/ActionEvade/ActionRecovery/ActionPosture`。现在 `ActionTotalMs` 是拆分后的父阶段剩余独占成本，不能单独与旧版整个 Action 阶段比较；累计总量或同一帧全部独占阶段可相加，独立峰值不能相加。
- `SOUND_SAVED` 的 `kind=gunshot/step`、`band=Close/Search`、`distance/error/ttl` 分别用于核对来源、枪声距离段、事件距离、定位半径与寿命；脚步的 `band` 字段不代表套用枪声分段。120 米外枪声会增加 `FarGunshotIgnored`。
- `Deactivated/Reactivated` 统计活动边沿；`ResourceReleased` 统计真正消费清理责任的次数，包括自有控制与原生层射击请求，因此不要求它与 `ControlReleased` 相等。稳定停用期不应持续增加这些计数。
- `DecisionServed` 为实际开始的到期决策数，`DecisionForced` 为超出 40% 软阈值后执行的保障次数，`DecisionDelayed` 为迟到超过 100 毫秒的执行次数。
- `decisionWaitAvgMs/decisionWaitMaxMs` 统计已开始执行的决策超过截止时刻的等待；`decisionDue/decisionOldestDueMs` 是汇总时尚待执行的活动项及最大迟到。二者需一起看，不能用“已服务等待很小”掩盖未服务项。均使用 Unity 局内时钟，不能当作 CPU 耗时或完整感知到动作延迟。
- `SoundOutOfRange` 为声音入库前的范围过滤，`ClueOutOfRange` 为接管前发现旧线索已超距；狙击模板只看向线索，不受移动调查半径限制。
- `Registered` 增长但 `ControlClaimed`、`ActionEntered` 长期为零：结合感知和状态判断是否没有敌情、角色/层优先级不匹配或动作未被选中。
- `AimBlocked` 包括视线/目标条件不合法及恢复锁；`AimNotReady` 统计原生瞄准尚未就绪的调用次数。两者不是独立 Bot 数。
- `ShotNoSight / ShotCannotShoot / ShotNotReady / ShotReacting / ShotCooldown` 区分进入扳机入口后的拒绝原因。同一次调用只记最先失败的条件；尚未调用扳机入口的等待不会出现在这些数中。
- `ShotQueued → ShotValidated → ShotAllowed → ShotNativeAccepted` 分别表示排队、几何验证、许可消费、原生接受。被取消、过期、失效、原生拒绝的分支不会走完整链路，不能要求四个数相等。
- `ShotBudgetWait` 是等待额度的检查次数；`ShotExpired` 是 Bot 等待超时，另一个小写 `shotExpired` 是队列移除过期项的次数，清理时机不同，不能直接相加当作独立请求数。
- `QueryDeadline / QueryNoCandidates / QueryNavSample / QueryNoOcclusion / QueryPathIncomplete / QueryPathCorners / QueryPathBounds` 给出普通查询的最终失败原因。`Stuck` 也计入 `QueryFailed`，故不是额外独立任务。
- `ShotPermitStale` 大量增长说明目标、枪口或瞄准变化使短期许可失效；`ShotNativeRejected` 增长则说明原生还有条件不满足，需要结合场景继续细化。
- `suppressed` 表示关键事件明细受单体或全局限频而合并，事件计数仍保留。默认令牌补充量八条/秒、突发容量八条，不等于任意滑动一秒窗口绝不超过八条。

## 分阶段耗时的口径

`TIMING` 包含 `Safety`（安全检查及其中的停用处理）、`Decision`（高层决策）、`Action`（动作执行）、`Perception`（玩家视觉/声音/受击回调及近弹队列）、`Shooting`（射击许可与枪线验证）、`Query`（紧急与普通掩体/导航查询）、`Lifecycle`（注册/销毁和 BigBrain 控制交接）、`Logging`（同步日志与周期汇总）。阶段内调用的原生方法耗时也归属该阶段；AI 来源提前退出的轻量判断、未插桩入口、区间之间的游戏工作和异步磁盘刷盘不在此口径内。

- `*TotalMs` 为已结算帧中该阶段的独占累计毫秒数，嵌套射击和日志从父阶段扣除；各阶段之和与 `workAvgMs × workFrames` 在舍入误差内一致。
- `*Calls` 为相同已结算帧中的区间调用次数。`*CallPeakMs` 为单次区间含子调用的峰值，便于定位一次原生同步调用或日志阻塞；不同阶段的单次峰值不可相加。
- `peakFrame/peakT` 标记累计最差插件工作帧及该帧局内时刻；`*AtPeakMs` 保存这个同一帧的阶段独占组成，求和对应 `workPeakMs`，不混用不同帧的阶段峰值。
- `stackOverflows` 正常应为零；极端同步重入超过固定栈时并入最深父阶段并计数，不扩容或打断行为。此时最深阶段的归因精度降低。

每帧在下一帧首次插件入口结算，包含 LateUpdate 之后的动作；周期汇总展示到上一完整帧，其自身格式化与写入在后续帧汇总中出现。退出战局会结算最后一帧；最终汇总与清场排除在战局工作计时之外。0.1.1 新增注册/交接和周期日志计时，修复晚回调漏计，因此与 0.1.0 的均值/峰值不完全同口径，更不能直接换算 FPS。

本次日志增量的性能影响暂评 **低（静态分析预估，未局内实测）**：使用固定容量阶段统计，普通明细限频后才格式化。大量 Bot 同时产生事件、提高日志额度、启用额外 `TraceBots` 或同步日志监听器阻塞时，会增加主线程格式化、写入和 GC 开销。日志不能替代同场景的外部帧时间和 GC 测量。
