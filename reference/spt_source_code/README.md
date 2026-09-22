# SPT Mod 开发参考源码

来源：[SP-Tushonka](https://github.com/SP-Tushonka)。整理日期：2026-09-22。

## 仓库用途

| 本地目录 | 上游仓库 | 主要用途 | 当前版本 |
| --- | --- | --- | --- |
| `server-csharp/` | [server-csharp](https://github.com/SP-Tushonka/server-csharp) | C# 服务端、Bot 生成、难度配置、数据库、路由与 Mod 加载 | `reference/spt-4.1.5` / `7d7add5` |
| `modules/` | [modules](https://github.com/SP-Tushonka/modules) | BepInEx 客户端插件、Harmony 补丁、AI 调整、反射工具 | `reference/spt-4.1.5` / `d52d9c9` |
| `server-mod-examples/` | [server-mod-examples](https://github.com/SP-Tushonka/server-mod-examples) | 服务端 Mod 示例：依赖注入、配置修改、生命周期、方法补丁 | `main` / `d1f077e` |
| `wiki/` | [wiki](https://github.com/SP-Tushonka/wiki) | Mod 开发指南、4.1 迁移说明、Bot 参数说明 | `main` / `6413655` |
| `assembly-tool/` | [assembly-tool](https://github.com/SP-Tushonka/assembly-tool) | 游戏程序集重映射工具与类名映射，辅助定位客户端类型 | `master` / `a59b810` |

每个目录都是独立 Git 仓库，保留了上游 `origin` 和许可证。完整提交号、分支、LFS 状态见 [sources.lock.json](sources.lock.json)。

## 本地版本基准

从相邻游戏目录 `E:\Games\Escape From Tarkof\EFT v4.1` 的 EXE/DLL 版本信息读取：

- SPT：**4.1.5**。
- EFT：**0.16.9.5.40743**（EXE 产品版本为 `0.16.9.5-40743-f137e819`）。
- 服务端构建提交：`7d7add556a6f781e9a531fa3b0cf4cc925986e03`。
- 客户端模块构建提交：`d52d9c99836b6d7dc5ad93852cd8032158df0f9c`。

`server-csharp` 和 `modules` 已检出这些准确提交。默认分支 `main` / `master` 仍保留，后续针对本地安装开发时优先使用 `reference/spt-4.1.5`。

服务端源码的目标框架是 `net10.0`；客户端模块共享配置为 `netstandard2.1`。示例项目当前引用 `SPTushonka.*` 4.1.3 包，而本地服务端 DLL 文件名为 `SPTarkov.*`；后续建立 Mod 工程时，需要以实际安装的程序集/API 为准核对引用，不能仅凭示例版本推断兼容。

## AI 行为相关入口

客户端行为与补丁：

- `modules/SPT.Custom/CustomAI/`：AI 辅助逻辑、脑类型权重等。
- `modules/SPT.Custom/Patches/CustomAiPatch.cs`：自定义 AI 补丁入口。
- `modules/SPT.Custom/Patches/BotDifficultyPatch.cs`：Bot 难度补丁。
- `modules/SPT.SinglePlayer/Patches/RaidFix/`：离线战局相关修复。
- `modules/SPT.Reflection/Patching/`：补丁基础设施。

服务端路径以 `server-csharp/Libraries/` 为起点：

- `SPTushonka.Server.Core/Controllers/BotController.cs`：Bot 请求处理。
- `SPTushonka.Server.Core/Helpers/Bot/BotDifficultyHelper.cs`：难度数据处理。
- `SPTushonka.Server.Core/Generators/Bot/`：Bot、装备和物品生成。
- `SPTushonka.Server.Core/Generators/PmcWaveGenerator.cs`：PMC 波次生成。
- `SPTushonka.Server.Assets/SPT_Data/configs/bot.json`、`pmc.json`：服务端配置。
- `SPTushonka.Server.Assets/SPT_Data/database/bots/`：Bot 类型与难度数据，已包含在普通 Git 文件中。

示例与文档：

- `server-mod-examples/3EditSptConfig/`、`6OverrideMethodHarmony/`、`14AfterDBLoadHook/`。
- `wiki/modding/tutorials/Client_Modding_Quick_Guide.md`。
- `wiki/modding/SPT_41_Modding.md` 及同名目录中的版本迁移说明。
- `wiki/SPT_4x/Bot_Difficulties.md`。
- `assembly-tool/Assets/Json/Mappings/`：程序集类型映射。

## 下载范围与后续维护

本次使用 `--depth 1 --single-branch` 浅克隆，另为服务端抓取了准确的 4.1.5 构建提交。源码文件已检出，完整历史未下载。需要历史时，可在对应仓库执行 `git fetch --unshallow origin`。

克隆时设置了进程级 `GIT_LFS_SKIP_SMUDGE=1`。服务端当前提交的 14 个 LFS 文件仍为指针：13 个地图 `looseLoot.json` 和物品模板 `items.json`，合计约 **566.8 MiB**。这不影响阅读 C# 源码及已下载的 Bot 配置，但不能把这些指针当作实际数据库 JSON 使用。需要完整数据库或构建运行服务端时，在 `server-csharp` 目录执行：

```powershell
git lfs pull origin
```

上述命令使用仓库 `.lfsconfig` 指定的上游 LFS 服务，尚未在本次任务中下载或验证这些对象。其他 4 个仓库没有 LFS 文件。

本次未安装构建依赖、构建程序或修改游戏目录。各仓库均已通过 `git fsck --full --no-reflogs`，工作树干净。客户端构建需要的实际游戏程序集应在后续建立工程时按对应版本配置。

归档的 TypeScript `server`、启动器、安装器、网站与发行流水线暂未克隆；当前这组参考覆盖 C# 服务端和客户端 Mod 开发。
