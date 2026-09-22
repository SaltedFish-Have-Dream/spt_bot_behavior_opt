# Bot 行为优化 Mod：参考源码

本项目的开发方向是优化 Bot 行为。以下源码用于研究行为决策、感知、掩体、搜索、移动、小队协作，以及与现有 AI Mod 的集成。

## 目录与版本

| 目录 | 来源 | 本地参考版本 | 用途 |
| --- | --- | --- | --- |
| [SAIN](SAIN/) | [ArchangelWTF/SAIN](https://github.com/ArchangelWTF/SAIN) | `v4.5.1`，分支 `reference/4.5.1`，提交 `2d1d925` | 完整 AI 行为实现，含客户端、共享配置和服务端代码 |
| [BigBrain](BigBrain/) | [DrakiaXYZ/SPT-BigBrain](https://github.com/DrakiaXYZ/SPT-BigBrain) | `1.5.0`，分支 `reference/1.5.0`，提交 `4d26e8b` | 自定义行为层、动作逻辑及其与 EFT Bot Brain 的衔接 |
| [spt_source_code](spt_source_code/README.md) | [SP-Tushonka](https://github.com/SP-Tushonka) | 核心源码对应 SPT 4.1.5 | SPT 客户端、服务端、示例和文档 |

整理日期：2026-09-22。SAIN 与 BigBrain 分别是独立 Git 仓库；完整版本信息见 [bot-mod-sources.lock.json](bot-mod-sources.lock.json)。

从本地 `E:\Games\Escape From Tarkof\EFT v4.1\BepInEx\plugins` 的 DLL 产品版本读取并核对：

- `SAIN/SAIN.dll`：`4.5.1+2d1d9257dbb3af535e9189e6181311f6355aec27`。
- `DrakiaXYZ-BigBrain.dll`：`1.5.0+4d26e8b0441d845933941eb0509cbe4e04ed2788`。

两个仓库的 HEAD 均与上述构建提交一致。SAIN 选择 Solarint 原项目的后续维护仓库 `ArchangelWTF/SAIN`；它的 4.5.1 发布说明面向 SPT 4.1，上游目前已归档。其 `Directory.Build.props` 中 `SptVersion` 为 4.1.3，本地 SPT 为 4.1.5；这里确认的是源码与已安装 Mod 的构建提交一致，尚未进行编译和游戏内兼容验证。

## SAIN 阅读入口

以下路径相对于 `reference/SAIN`：

| 路径 | 关注内容 |
| --- | --- |
| `SAIN/SAINPlugin.cs` | BepInEx 插件入口、初始化与补丁启用 |
| `SAIN/Plugin/BigBrainHandler.cs` | 注册 SAIN 行为层，移除或恢复原有层，设置优先级 |
| `SAIN/Classes/Bot/Decision/` | 战斗、小队、自保决策；`BotDecisionManager.cs` 为决策调度入口 |
| `SAIN/Layers/SAINLayer.cs`、`SAIN/Layers/Combat/` | 决策转为动作、行为层切换、个人与小队战斗行为 |
| `SAIN/Classes/Bot/EnemyClasses/`、`EnemyControllers/`、`Sense/` | 敌人状态管理与感知逻辑 |
| `SAIN/Classes/Coverfinder/`、`SAIN/Components/CoverFinderComponent.cs` | 掩体搜索与评估 |
| `SAIN/Classes/Bot/Mover/`、`Search/`、`Doors/` | 移动、搜索和门交互 |
| `SAIN/Classes/BotManager/Jobs/` | 视觉等射线检测任务 |
| `SAIN/Interop/` | 对外集成接口 |
| `SAIN.Preset.Shared/` | 共享配置模型、人格和行为参数 |
| `SAINServerMod/`、`SAIN.ServerInterop/` | 服务端配置支持与服务端互操作接口 |

## BigBrain 阅读入口

以下路径相对于 `reference/BigBrain`：

| 路径 | 关注内容 |
| --- | --- |
| `Brains/BrainManager.cs` | 注册自定义层，按脑类型与角色移除、恢复层 |
| `Brains/CustomLayer.cs` | `IsActive`、`GetNextAction`、`IsCurrentActionEnding` 及层生命周期 |
| `Brains/CustomLogic.cs` | 动作的 `Start`、`Update`、`Stop` 生命周期 |
| `Internal/CustomLayerWrapper.cs`、`Internal/CustomLogicWrapper.cs` | 自定义层、动作与游戏原有类型的适配 |
| `Patches/` | Bot Brain 和 Agent 的激活、更新挂钩 |

后续确定具体行为需求时，可以沿着“感知 → 决策 → 行为层 → 动作执行”追踪代码。新增行为层时，应先核对 SAIN 已注册的层、优先级和移动控制交接，确定与现有行为的关系。

## 下载与验证

- 两个新仓库均按发行标签进行 `--depth 1 --single-branch` 浅克隆，源码已完整检出；完整提交历史未下载。
- SAIN 有 519 个跟踪文件（467 个 C# 文件），BigBrain 有 27 个跟踪文件（20 个 C# 文件）。两者均无 LFS 文件和 Git 子模块。
- 两者均通过 `git fsck --full --no-reflogs`，工作树干净，保留上游 MIT 许可证。
- 如需历史，可在对应仓库执行 `git fetch --unshallow origin`。
- 本次仅建立参考源码库。编译时还需要配置实际游戏、BepInEx、SPT 和其他依赖引用；BigBrain 项目当前的相对 DLL 路径并不对应本参考目录布局。
