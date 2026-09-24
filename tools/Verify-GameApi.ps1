param([string]$GameRoot)

$ErrorActionPreference = 'Stop'
# 根据工作区发现目标安装，也允许构建时显式覆盖。
$apiProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $GameRoot) { $GameRoot = [IO.Path]::GetFullPath((Join-Path $apiProjectRoot '..\..\EFT v4.1')) }
# Cecil 只读取元数据，不加载或执行游戏程序集。
Add-Type -Path (Join-Path $GameRoot 'BepInEx\core\Mono.Cecil.dll')
$apiGamePath = Join-Path $GameRoot 'EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll'
$apiBrainPath = Join-Path $GameRoot 'BepInEx\plugins\DrakiaXYZ-BigBrain.dll'
$apiWaypointsPath = Join-Path $GameRoot 'BepInEx\plugins\DrakiaXYZ-Waypoints\DrakiaXYZ-Waypoints.dll' # 正式前置与运行时相同安装路径。
if (-not (Test-Path -LiteralPath $apiWaypointsPath -PathType Leaf) -or [Diagnostics.FileVersionInfo]::GetVersionInfo($apiWaypointsPath).FileVersion -ne '1.9.0') { throw '缺少已验证的 Waypoints 1.9.0 前置。' } # 缺少前置时不构建发布包。
$apiGame = [Mono.Cecil.ModuleDefinition]::ReadModule($apiGamePath)
$apiBrain = [Mono.Cecil.ModuleDefinition]::ReadModule($apiBrainPath)
$apiVerified = [Collections.Generic.List[string]]::new()

# 检查精确签名、可见性和实例属性，避免仅凭方法名确认补丁。
function Assert-ApiMethod {
    param($Module, [string]$Type, [string]$Name, [string]$Returns, [string[]]$Parameters, [switch]$NonPublic)
    $apiDefinition = $null # 使用完整类型名称避免同名类冲突。
    foreach ($apiCandidate in $Module.Types) { if ($apiCandidate.FullName -eq $Type) { $apiDefinition = $apiCandidate; break } } # 找到目标类型。
    if ($null -eq $apiDefinition) { throw "缺少类型：$Type" } # 缺少类型时禁止继续打包。
    $apiMatches = 0 # 检查是否存在意外重载。
    foreach ($apiMethod in $apiDefinition.Methods) {
        if ($apiMethod.Name -ne $Name) { continue } # 忽略其他方法。
        $apiMatches++ # 目标补丁入口应当唯一。
        if ($apiMethod.ReturnType.FullName -ne $Returns -or $apiMethod.IsStatic -or ($apiMethod.IsPublic -eq $NonPublic.IsPresent)) { throw "方法属性不匹配：$Type.$Name" } # 可选兼容接口明确要求非公开，其他调用仍要求公开实例方法。
        if ($apiMethod.Parameters.Count -ne $Parameters.Count) { throw "参数数量不匹配：$Type.$Name" } # 不接受隐式重载替换。
        for ($apiIndex = 0; $apiIndex -lt $Parameters.Count; $apiIndex++) {
            if ($apiMethod.Parameters[$apiIndex].ParameterType.FullName -ne $Parameters[$apiIndex]) { throw "参数类型不匹配：$Type.$Name" } # 逐项比对参数类型。
        }
        $apiVerified.Add($apiMethod.FullName) # 只记录签名，不导出实现。
    }
    if ($apiMatches -ne 1) { throw "方法不存在或有未处理重载：$Type.$Name" } # 阻止 Harmony 运行时歧义。
}

try {
    Assert-ApiMethod $apiGame 'EFT.BotOwner' 'method_10' 'System.Void' @() # Bot 完整激活。
    Assert-ApiMethod $apiGame 'EFT.BotOwner' 'Dispose' 'System.Void' @() # Bot 销毁。
    Assert-ApiMethod $apiGame 'EFT.GameWorld' 'Dispose' 'System.Void' @() # 战局清理。
    Assert-ApiMethod $apiGame 'EnemyInfo' 'CheckLookEnemy' 'System.Void' @('LookAllData', 'System.Single') # 个人视觉完成入口。
    Assert-ApiMethod $apiGame 'BotHearingSensor' 'SoundHeared' 'System.Void' @('EFT.IPlayer', 'UnityEngine.Vector3', 'System.Single', 'System.Boolean', 'AISoundType') # 原生听觉过滤后的分支。
    Assert-ApiMethod $apiGame 'EFT.BotMemory' 'set_GoalEnemy' 'System.Void' @('EnemyInfo') # 原生目标切换。
    Assert-ApiMethod $apiGame 'BotAimingData' 'SetTarget' 'System.Void' @('UnityEngine.Vector3') # 首次瞄准。
    Assert-ApiMethod $apiGame 'BotAimingData' 'UpdateTarget' 'System.Void' @('UnityEngine.Vector3') # 连续瞄准。
    Assert-ApiMethod $apiGame 'ShootData' 'Shoot' 'System.Boolean' @() # 扳机入口。
    Assert-ApiMethod $apiGame 'EFT.BotOwner' 'OnGetHit' 'System.Void' @('EFT.Ballistics.DamageInfo', 'EBodyPart', 'System.Single') # 核对真实伤害来源入口。
    Assert-ApiMethod $apiGame 'EFT.Ballistics.Shot' 'Update' 'System.Void' @('System.Single') # 碰撞处理完成后读取实际弹道终点。
    Assert-ApiMethod $apiGame 'EFT.IPlayer' 'get_IsAI' 'System.Boolean' @() # 阵营不能替代 AI 身份判定。
    Assert-ApiMethod $apiGame 'EFT.IPlayer' 'get_IsYourPlayer' 'System.Boolean' @() # 本版仅支持本机真人玩家。
    Assert-ApiMethod $apiGame 'BotLay' 'set_IsLay' 'System.Void' @('System.Boolean') # 原生卧姿状态同步入口。
    Assert-ApiMethod $apiGame 'EFT.MovementContext' 'get_CanProne' 'System.Boolean' @() # 原生环境与姿态合法性检查。
    Assert-ApiMethod $apiGame 'LookData' 'ResetUpdateTime' 'System.Void' @() # 警戒只唤醒真实视觉，不伪造可见性。
    Assert-ApiMethod $apiGame 'BotMover' 'GoToByWay' 'System.Void' @('UnityEngine.Vector3[]', 'System.Single') # 复用完整路径。
    Assert-ApiMethod $apiGame 'BotFirstAid' 'ApplyToSelf' 'System.Void' @('System.Nullable`1<System.Int32>', 'System.Action') # 原生治疗。
    Assert-ApiMethod $apiGame 'BotAimingData' 'get_Status' 'AimStatus' @() # 已清空瞄准时跳过重复原生调用。
    Assert-ApiMethod $apiGame 'BotMover' 'get_DistDestination' 'System.Single' @() # 到达必须同时验证导航剩余距离。
    Assert-ApiMethod $apiBrain 'DrakiaXYZ.BigBrain.Brains.CustomLayer' 'Start' 'System.Void' @() # 跟踪实际选中层而非候选激活条件。
    Assert-ApiMethod $apiBrain 'DrakiaXYZ.BigBrain.Brains.CustomLayer' 'Stop' 'System.Void' @() # 原生抢占后撤销选择标记。
    $apiLimitPath = Join-Path $GameRoot 'BepInEx\plugins\dvize.AILimit.dll' # 只验证已安装且明确匹配的可选依赖。
    if ((Test-Path -LiteralPath $apiLimitPath -PathType Leaf) -and [Diagnostics.FileVersionInfo]::GetVersionInfo($apiLimitPath).FileVersion -eq '1.9.2.0') {
        $apiLimit = [Mono.Cecil.ModuleDefinition]::ReadModule($apiLimitPath) # 仅读元数据，不执行第三方插件。
        try { Assert-ApiMethod $apiLimit 'AILimit.AILimitComponent' 'getMinDistanceToBot' 'System.Single' @('EFT.Player') -NonPublic } # 排序适配的精确目标。
        finally { $apiLimit.Dispose() } # 不持有游戏文件句柄。
    }
    $apiRoles = @{ pmcBEAR = 51; pmcUSEC = 52; pmcBot = 9; assault = 1; assaultGroup = 19; marksman = 0 } # 明确区分 Raider 与 PMC。
    $apiRoleType = $null # 读取角色定义。
    foreach ($apiType in $apiGame.Types) { if ($apiType.FullName -eq 'EFT.WildSpawnType') { $apiRoleType = $apiType; break } } # 只读枚举元数据。
    foreach ($apiRole in $apiRoles.Keys) {
        $apiRoleFound = $false # 每个白名单角色必须存在。
        foreach ($apiField in $apiRoleType.Fields) { if ($apiField.Name -eq $apiRole -and $apiField.HasConstant -and [int]$apiField.Constant -eq $apiRoles[$apiRole]) { $apiRoleFound = $true; break } } # 检查实际常量。
        if (-not $apiRoleFound) { throw "角色枚举不匹配：$apiRole" } # 版本不一致时明确失败。
    }
    $apiBrainOverload = $false # BigBrain 允许重载，单独检查四参数入口。
    foreach ($apiType in $apiBrain.Types) {
        if ($apiType.FullName -ne 'DrakiaXYZ.BigBrain.Brains.BrainManager') { continue } # 限定公开管理器。
        foreach ($apiMethod in $apiType.Methods) {
            if ($apiMethod.Name -eq 'AddCustomLayer' -and $apiMethod.Parameters.Count -eq 4 -and $apiMethod.IsPublic -and $apiMethod.IsStatic -and
                $apiMethod.Parameters[0].ParameterType.FullName -eq 'System.Type' -and
                $apiMethod.Parameters[1].ParameterType.FullName -eq 'System.Collections.Generic.List`1<System.String>' -and
                $apiMethod.Parameters[2].ParameterType.FullName -eq 'System.Int32' -and
                $apiMethod.Parameters[3].ParameterType.FullName -eq 'System.Collections.Generic.List`1<EFT.WildSpawnType>' -and
                $apiMethod.ReturnType.FullName -eq 'System.Int32') { $apiBrainOverload = $true; $apiVerified.Add($apiMethod.FullName) } # 禁止退化为全角色注册。
        }
    }
    if (-not $apiBrainOverload) { throw 'BigBrain 缺少匹配的角色过滤接口。' } # 验证依赖接口。
    $apiReportRoot = Join-Path $apiProjectRoot 'artifacts\verification' # 报告不进入源码库。
    New-Item -ItemType Directory -Path $apiReportRoot -Force | Out-Null # 不修改游戏文件。
    [ordered]@{
        checkedAt = [DateTimeOffset]::Now.ToString('o')
        gameProductVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $GameRoot 'EscapeFromTarkov.exe')).ProductVersion
        assemblySha256 = (Get-FileHash -LiteralPath $apiGamePath -Algorithm SHA256).Hash
        bigBrainProductVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($apiBrainPath).ProductVersion
        waypointsProductVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($apiWaypointsPath).ProductVersion # 只验证文件版本；实际加载由插件启动核对。
        methodSignatures = @($apiVerified.ToArray())
        roles = $apiRoles
        runtimeTested = $false
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $apiReportRoot 'game-api.json') -Encoding UTF8 # 只保存验证证据。
    Write-Output "PASS: $($apiVerified.Count) API signatures, $($apiRoles.Count) role constants. No game process executed."
}
finally {
    $apiGame.Dispose() # 释放只读文件句柄。
    $apiBrain.Dispose() # 释放依赖元数据句柄。
}
