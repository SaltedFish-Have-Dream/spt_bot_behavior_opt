# spt_bot_behavior_opt

轻量化 SPT 拟真 Bot 行为模组。当前版本为 **0.1.6 开发验证版**：在 0.1.5 已验证功能上，补充无掩体且无法趴伏时的有限短距撤离、连续挡枪后的两侧换位、搜索失败点短期记忆，并将 Waypoints 1.9.0 设为正式前置。仍保留完整路径和射击安全校验；此版需重新游戏内实测，原生撤离层交接尚未解决。

## 首版功能

- PMC 的反应、瞄准稳定、听觉误差、记忆时长和战术倾向随等级线性变化；默认成长区间为 1～60 级。
- 普通 Scav 使用固定模板；狙击 Scav 保留定点定位；Boss、护卫、Raider 与其他特殊角色沿用原生逻辑。
- 保存个人视觉和带误差的听觉快照，限制丢失视线后的直接瞄准与开火；执行调查、搜索、交战、局部掩体和原生恢复动作。
- 共享物理与寻路预算、轮转查询、有限记忆、超时和卡住回退，提供低频汇总日志。
- 玩家枪声：听到后 0～30 米快速警戒，30～120 米按距离和能力估计区域搜索；超过 120 米未受枪弹威胁不额外响应。
- 玩家命中或 2.5 米内实际近弹：优先局部掩体，无验证可用掩体时尝试合法趴伏；安静五秒后，身体与弹药允许才有限推进。命中时还会限频通知 20 米内同组受管成员。
- 在自有掩体处被玩家命中后暂时排除该点；高压优先隐蔽，压力回落且在有效掩体、有真实视线时尝试还击。接近搜索点时短暂停看，同一区域最多两次；普通视线变化不再随意打断有效掩体移动。

依赖 **SPT 4.1.5 / EFT 0.16.9.5-40743 / BigBrain 1.5.0 / SPT-Waypoints 1.9.0**，不依赖 SAIN。SAIN 与 ORBIT 会接管相同的行为链路，插件启用冲突保护，安装前需要停用这两个模组。

## 本机构建

在项目目录执行：

```powershell
& '.\tools\Build.ps1' -Package
```

脚本默认使用上一级目录的 `net_sdk/dotnet-sdk-10.0.401-win-x64` 和本机 `EFT v4.1` 安装。其他位置可传入 `-SdkRoot` 与 `-GameRoot`。首次还原可能需要联网。

构建依次核对本地游戏接口及 Waypoints 安装、编译 Release 工程、运行独立行为检查，最后输出 `dist/AiBehaviorOpt-0.1.6.zip`。仅打包两个自有 DLL 与说明文件，不修改游戏安装，不修改全局 SDK 环境。

## 文档与目录

- [安装、配置与游戏内验证说明](docs/v0.1-user-guide.md)
- [首版实现范围与验证证据](docs/v0.1-implementation.md)
- [实测日志判读与归档](docs/playtest-logging.md)
- [海岸线首局实测结果与待修问题](docs/playtests/2026-09-22-shoreline.md)
- [0.1.1 修复内容与复测重点](docs/v0.1.1-fixes.md)
- [0.1.2 玩家专属行为与实测清单](docs/v0.1.2-player-only.md)
- [0.1.2 海岸线局中实测问题](docs/playtests/2026-09-23-shoreline-0.1.2.md)
- [0.1.3 交战蹲起修复与复测重点](docs/v0.1.3-posture-fix.md)
- [0.1.4 导航、控制恢复与限流兼容修复](docs/v0.1.4-navigation-fixes.md)
- [0.1.5 轻量战术反馈与复测清单](docs/v0.1.5-tactical-response.md)
- [0.1.5 海岸线战后排查与行为改进顺序](docs/playtests/2026-09-23-shoreline-0.1.5.md)
- [0.1.6 受阻后的有限导航反馈与复测](docs/v0.1.6-adaptive-navigation.md)
- [开发设计](docs/bot-behavior-design-v0.1.md)
- [SAIN 4.5.1 优秀行为设计与轻量化借鉴](docs/sain-behavior-reference-4.5.1.md)
- [独立行为核心](src/AiBehavior.Core)、[游戏适配插件](src/AiBehavior.Client)、[行为检查](tests/AiBehavior.Core.Tests)
- [构建脚本](tools/Build.ps1)、[游戏接口检查](tools/Verify-GameApi.ps1)

0.1.6 新增开销评为 **低（静态分析预估，未实测）**：新增状态为固定容量，短距查询沿用全局预算。大量 Bot 同时受玩家连射、掩体与卧姿均失败时，路径队列与同步寻路可能形成峰值；此前 0.1.5 海岸线数据仅作历史对照，不能代替新版测量。
