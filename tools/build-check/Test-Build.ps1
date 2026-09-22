param(
    [string]$SdkRoot,
    [string]$GameRoot = 'E:\Games\Escape From Tarkof\EFT v4.1'
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $SdkRoot) {
    $SdkRoot = Join-Path (Split-Path $workspaceRoot -Parent) 'net_sdk\dotnet-sdk-10.0.401-win-x64'
}
$dotnetPath = Join-Path $SdkRoot 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetPath)) { throw "SDK not found: $dotnetPath" }
if (-not (Test-Path -LiteralPath (Join-Path $GameRoot 'EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll'))) {
    throw "Game dependencies not found: $GameRoot"
}

# 将构建缓存保存在工作区，使用指定 SDK，不修改系统 PATH。
$cacheRoot = Join-Path $workspaceRoot 'artifacts\build-check'
$env:DOTNET_ROOT = $SdkRoot
$env:DOTNET_CLI_HOME = Join-Path $cacheRoot 'cli-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:DOTNET_NOLOGO = '1'
$env:NUGET_PACKAGES = Join-Path $cacheRoot 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $cacheRoot 'http-cache'
$nugetConfig = Join-Path $PSScriptRoot 'NuGet.Config'

$failedProjects = @()
foreach ($relativePath in @('Client\Client.BuildCheck.csproj', 'Server\Server.BuildCheck.csproj')) {
    $projectPath = Join-Path $PSScriptRoot $relativePath
    & $dotnetPath restore $projectPath --configfile $nugetConfig "-p:GameRoot=$GameRoot" --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        $failedProjects += "$relativePath (restore)"
        continue
    }
    & $dotnetPath build $projectPath -c Release --no-restore --nologo --disable-build-servers '-p:UseSharedCompilation=false' "-p:GameRoot=$GameRoot" --verbosity minimal
    if ($LASTEXITCODE -ne 0) { $failedProjects += "$relativePath (build)" }
}
if ($failedProjects.Count -gt 0) { throw "Build check failed: $($failedProjects -join ', ')" }
Write-Output 'Client and server compile checks passed. No deployment performed.'
