# 本机构建环境验证

验证日期：2026-09-22。结论：当前环境已通过客户端与服务端的最小工程实际编译，可用于后续 Bot 行为优化 Mod 的本机构建。

## 工具链

- SDK：`E:\Games\Escape From Tarkof\my_mod\net_sdk\dotnet-sdk-10.0.401-win-x64`。
- .NET SDK：`10.0.401`；MSBuild：`18.9.11`；SDK 自带运行时：`10.0.12`。
- 游戏目录：`E:\Games\Escape From Tarkof\EFT v4.1`。
- 依赖基准：SPT 4.1.5、SAIN 4.5.1、BigBrain 1.5.0。

系统 PATH 中的 `dotnet` 仍指向 `C:\Program Files\dotnet\dotnet.exe`，该目录仅有运行时。本验证脚本显式调用上述 SDK 中的 `dotnet.exe`，无需修改系统 PATH。

## 已通过的编译

| 工程 | 目标框架 | 实际验证内容 | 结果 |
| --- | --- | --- | --- |
| `Client/Client.BuildCheck.csproj` | `netstandard2.1` | 游戏 BotOwner、Unity/BepInEx 插件继承、Harmony、SPT 补丁类型、BigBrain 动作接口、SAIN 公开接口及共享程序集 | Release 构建成功，0 警告，0 错误 |
| `Server/Server.BuildCheck.csproj` | `net10.0` | 本地 SPT 服务端依赖注入、日志、IOnLoad 生命周期与 BotController 类型 | Release 构建成功，0 警告，0 错误 |

两者均开启 `TreatWarningsAsErrors`，依赖包还原也已成功。输出位于各自的 `bin/Release/<目标框架>/`，程序集不会复制进游戏目录。

## 复查命令

在项目根目录的 PowerShell 中运行：

```powershell
& '.\tools\build-check\Test-Build.ps1'
```

SDK 或游戏目录变更时，可传入 `-SdkRoot` 和 `-GameRoot`。脚本将进程级 CLI 与 NuGet 缓存定位到工作区 `artifacts/build-check/`；首次还原可能需要访问 NuGet。若工具沙箱限制读取用户 NuGet 配置或联网，应在正常用户权限的 PowerShell 中运行。

这两个工程用于构建环境检查，不是可安装的行为 Mod。结果确认编译工具和所测依赖可用；具体 Mod 开发完成后仍需编译其完整工程并做游戏内验证。参考仓库 SAIN/BigBrain 的原始工程相对路径未在本次检查中修改。
