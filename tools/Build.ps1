param(
    [string]$SdkRoot,
    [string]$GameRoot,
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
# 将所有输出与缓存限制在工作区，编译不直接部署进游戏。
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# 默认使用用户已安装的便携 SDK。
if (-not $SdkRoot) { $SdkRoot = Join-Path (Split-Path $projectRoot -Parent) 'net_sdk\dotnet-sdk-10.0.401-win-x64' }
# 游戏安装与 my_mod 位于同一级上层目录。
if (-not $GameRoot) { $GameRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '..\..\EFT v4.1')) }
# 验证工具存在后才执行构建。
$sdkExecutable = Join-Path $SdkRoot 'dotnet.exe'
if (-not (Test-Path -LiteralPath $sdkExecutable -PathType Leaf)) { throw "找不到 SDK：$sdkExecutable" }
# 缓存位置与已有构建探针保持一致，避免重复下载引用包。
$buildCache = Join-Path $projectRoot 'artifacts\build-check'
$buildEnvironment = @{
    DOTNET_ROOT = $SdkRoot
    DOTNET_CLI_HOME = (Join-Path $buildCache 'cli-home')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
    DOTNET_NOLOGO = '1'
    NUGET_PACKAGES = (Join-Path $buildCache 'packages')
    NUGET_HTTP_CACHE_PATH = (Join-Path $buildCache 'http-cache')
}
# 记录进程级旧值，结束后恢复调用者环境。
$previousEnvironment = @{}
foreach ($buildKey in $buildEnvironment.Keys) {
    $previousEnvironment[$buildKey] = [Environment]::GetEnvironmentVariable($buildKey, 'Process') # 只读取当前进程值。
    [Environment]::SetEnvironmentVariable($buildKey, $buildEnvironment[$buildKey], 'Process') # 不修改系统或用户环境。
}
try {
    & (Join-Path $PSScriptRoot 'Verify-GameApi.ps1') -GameRoot $GameRoot # 在编译与打包前验证目标版本的精确接口。
    $buildProjects = @('src\AiBehavior.Client\AiBehavior.Client.csproj', 'tests\AiBehavior.Core.Tests\AiBehavior.Core.Tests.csproj') # 完整客户端与独立行为检查都必须通过。
    foreach ($buildRelative in $buildProjects) {
        $buildProject = Join-Path $projectRoot $buildRelative # 使用绝对路径，不依赖调用目录。
        & $sdkExecutable restore $buildProject --configfile (Join-Path $projectRoot 'NuGet.Config') "-p:GameRoot=$GameRoot" --verbosity minimal # 显式使用本项目源配置。
        if ($LASTEXITCODE -ne 0) { throw "还原失败：$buildRelative" } # 还原失败时禁止使用旧输出打包。
        & $sdkExecutable build $buildProject -c Release --no-restore --disable-build-servers '-p:UseSharedCompilation=false' "-p:GameRoot=$GameRoot" --verbosity minimal # 构建真实自有工程。
        if ($LASTEXITCODE -ne 0) { throw "编译失败：$buildRelative" } # 所有警告按错误处理。
    }
    $testAssembly = Join-Path $projectRoot 'tests\AiBehavior.Core.Tests\bin\Release\net10.0\AiBehavior.Core.Tests.dll' # 定位独立回归程序。
    & $sdkExecutable $testAssembly # 运行边界和行为检查。
    if ($LASTEXITCODE -ne 0) { throw '行为回归检查失败，停止打包。' } # 断言失败禁止生成发布包。
    if ($Package) {
        $packageStage = Join-Path $projectRoot ('artifacts\package\' + [Guid]::NewGuid().ToString('N')) # 使用新目录避免旧文件混入包内。
        $packagePlugins = Join-Path $packageStage 'BepInEx\plugins\AiBehaviorOpt' # 用户解压时可直接识别游戏目录结构。
        New-Item -ItemType Directory -Path $packagePlugins -Force | Out-Null # 仅创建工作区下的暂存目录。
        $clientOutput = Join-Path $projectRoot 'src\AiBehavior.Client\bin\Release\netstandard2.1' # 发布仅包含自有程序集。
        foreach ($packageFile in @('AiBehavior.Client.dll', 'AiBehavior.Core.dll')) {
            Copy-Item -LiteralPath (Join-Path $clientOutput $packageFile) -Destination $packagePlugins # 不复制游戏、BepInEx 或 BigBrain DLL。
        }
        Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\v0.1-user-guide.md') -Destination (Join-Path $packageStage 'README.md') # 包内附带实际功能和安装限制。
        Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\playtest-logging.md') -Destination (Join-Path $packageStage 'playtest-logging.md') # 安装包内也能直接查阅日志字段与实测流程。
        Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\v0.1.2-player-only.md') -Destination (Join-Path $packageStage 'player-only.md') # 附带本版玩家行为边界与重点验收场景。
        Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\v0.1.3-posture-fix.md') -Destination (Join-Path $packageStage 'posture-fix.md') # 附带姿态修复、已知问题与本版复测重点。
        Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\v0.1.4-navigation-fixes.md') -Destination (Join-Path $packageStage 'navigation-fixes.md') # 本版以此说明导航、控制恢复与可选限流适配。
        Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\v0.1.5-tactical-response.md') -Destination (Join-Path $packageStage 'tactical-response.md') # 附带本版自主实现的行为反馈与限频诊断。
        Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\v0.1.6-adaptive-navigation.md') -Destination (Join-Path $packageStage 'adaptive-navigation.md') # 附带新导航反馈和复测步骤。
        $distributionRoot = Join-Path $projectRoot 'dist' # 发布文件保存在 Git 忽略目录。
        New-Item -ItemType Directory -Path $distributionRoot -Force | Out-Null # 不修改游戏安装。
        $packageArchive = Join-Path $distributionRoot 'AiBehaviorOpt-0.1.6.zip' # 固定版本产物便于安装。
        Compress-Archive -Path (Join-Path $packageStage '*') -DestinationPath $packageArchive -Force # 只压缩本次新建的暂存目录。
        Get-FileHash -LiteralPath $packageArchive -Algorithm SHA256 # 输出校验值供交付追溯。
    }
}
finally {
    foreach ($buildKey in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($buildKey, $previousEnvironment[$buildKey], 'Process') # 无论成功失败都恢复环境。
    }
}
