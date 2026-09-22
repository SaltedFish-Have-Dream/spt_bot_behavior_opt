param([string]$GameRoot)

$ErrorActionPreference = 'Stop'
# 只从游戏安装读取文件，归档写入当前项目的忽略目录。
$logProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $GameRoot) { $GameRoot = [IO.Path]::GetFullPath((Join-Path $logProjectRoot '..\..\EFT v4.1')) }
$sourceLog = Join-Path $GameRoot 'BepInEx\LogOutput.log'
if (-not (Test-Path -LiteralPath $sourceLog -PathType Leaf)) { throw "找不到游戏日志：$sourceLog" }
# 新目录包含时间和随机后缀，连续归档也不会覆盖已有实测证据。
$logArchive = Join-Path $logProjectRoot ('artifacts\playtests\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $logArchive -Force | Out-Null

# 使用共享读取归档仍在写入的日志；仅复制打开时已有长度，避免追随不断增长的文件。
function Copy-LogSnapshot {
    param([string]$Source, [string]$Destination)
    $logInput = [IO.File]::Open($Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete) # 不锁住游戏的日志写入。
    try {
        $logOutput = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read) # 不覆盖以前的归档。
        try {
            $remainingBytes = $logInput.Length # 捕获当前有限长度。
            $logBuffer = [byte[]]::new(65536) # 固定缓冲，不一次性读取整份日志到内存。
            while ($remainingBytes -gt 0) {
                $readBytes = $logInput.Read($logBuffer, 0, [int][Math]::Min($remainingBytes, $logBuffer.Length)) # 只读本次快照范围。
                if ($readBytes -eq 0) { break } # 文件被外部截断时结束读取。
                $logOutput.Write($logBuffer, 0, $readBytes) # 写入当前项目归档。
                $remainingBytes -= $readBytes # 确保即使游戏持续写日志也能结束。
            }
        }
        finally { $logOutput.Dispose() } # 释放归档句柄，允许后续读取分析。
    }
    finally { $logInput.Dispose() } # 任何失败都释放游戏日志句柄。
}

Copy-LogSnapshot $sourceLog (Join-Path $logArchive 'LogOutput.log') # 保留完整日志，以便结合 BigBrain、Harmony 和加载器错误分析。
foreach ($logConfig in @('BepInEx\config\local.aibehavior.opt.cfg', 'BepInEx\config\BepInEx.cfg')) {
    $configSource = Join-Path $GameRoot $logConfig # 仅读取与运行及日志级别有关的配置。
    if (Test-Path -LiteralPath $configSource -PathType Leaf) { Copy-LogSnapshot $configSource (Join-Path $logArchive (Split-Path $configSource -Leaf)) } # 尚未生成时跳过。
}
$installedAssemblies = @() # 同时列出重复安装，避免把旧 DLL 的运行结果归因于新版本。
$pluginDirectory = Join-Path $GameRoot 'BepInEx\plugins'
if (Test-Path -LiteralPath $pluginDirectory -PathType Container) {
    foreach ($installedAssembly in (Get-ChildItem -LiteralPath $pluginDirectory -Filter 'AiBehavior.*.dll' -Recurse -File)) {
        $installedAssemblies += [ordered]@{ path = $installedAssembly.FullName; sha256 = (Get-FileHash -LiteralPath $installedAssembly.FullName -Algorithm SHA256).Hash } # 只保存自有 DLL 的位置和校验值。
    }
}
[ordered]@{
    capturedAtUtc = [DateTime]::UtcNow.ToString('o')
    gameRoot = $GameRoot
    sourceLogLastWriteUtc = (Get-Item -LiteralPath $sourceLog).LastWriteTimeUtc.ToString('o')
    snapshotSha256 = (Get-FileHash -LiteralPath (Join-Path $logArchive 'LogOutput.log') -Algorithm SHA256).Hash
    installedAssemblies = $installedAssemblies
    note = '日志快照不等价于游戏测试通过；归档后仍可能有新的日志写入。'
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $logArchive 'capture.json') -Encoding UTF8 # 记录实测证据来源。
Write-Output "日志已归档：$logArchive" # 供实测后继续分析使用。
