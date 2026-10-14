param(
    [Parameter(Mandatory=$true)][string]$ServerPath,
    [string]$OutputPath = (Join-Path $PSScriptRoot '../.validate/movespeed'),
    [string]$DotNetRoot = (Join-Path $env:ProgramFiles 'dotnet')
)
$ErrorActionPreference = 'Stop'
$taskSource = $PSScriptRoot
$taskCache = Join-Path $taskSource '1014_构建缓存_v6'
New-Item -ItemType Directory -Path $taskCache,$OutputPath -Force | Out-Null
$env:DOTNET_CLI_HOME = $taskCache
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$taskCompiler = Get-ChildItem -LiteralPath (Join-Path $DotNetRoot 'sdk') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$taskCsc = Join-Path $taskCompiler.FullName 'Roslyn\bincore\csc.dll'
$taskRuntime = Get-ChildItem -LiteralPath (Join-Path $DotNetRoot 'shared\Microsoft.NETCore.App') -Directory | Where-Object Name -Like '6.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$taskRuntime -or !(Test-Path -LiteralPath $taskCsc)) { throw '需要已有 .NET SDK 和 .NET 6 runtime；本脚本不自动下载安装。' }
$taskRefs = @()
$taskRefs += Get-ChildItem -LiteralPath $taskRuntime.FullName -Filter '*.dll' -File
$taskRefs += Get-ChildItem -LiteralPath (Join-Path $ServerPath 'BepInEx\core') -Filter '*.dll' -File
$taskRefs += Get-ChildItem -LiteralPath (Join-Path $ServerPath 'BepInEx\interop') -Filter '*.dll' -File
$taskRefs = $taskRefs | Where-Object {
    try { [System.Reflection.AssemblyName]::GetAssemblyName($_.FullName) | Out-Null; $true }
    catch [System.BadImageFormatException] { $false }
}
$taskOut = Join-Path $OutputPath '1014_VRisingMoveSpeed_v6.dll'
$taskArgs = @('-nologo','-target:library','-langversion:10','-nullable:disable','-optimize+','-deterministic+','-unsafe+','-nostdlib+',('-out:"' + $taskOut + '"'))
$taskArgs += $taskRefs | ForEach-Object { '-reference:"' + $_.FullName + '"' }
$taskArgs += @('1014_SpeedMath_v6.cs','1014_MoveSpeedPlugin_v6.cs','1014_RegistryInterop_v6.cs','1014_PersistentOwnership_v6.cs','1014_LegacyMigration_v6.cs') | ForEach-Object { '"' + (Join-Path $taskSource $_) + '"' }
$taskArgs += '"' + (Join-Path $taskSource '1014_FormDiagnostics_v6.cs') + '"'
$taskResponse = Join-Path $taskCache '1014_编译参数_v6.rsp'
$taskArgs | Set-Content -LiteralPath $taskResponse -Encoding utf8
& (Join-Path $DotNetRoot 'dotnet.exe') $taskCsc ('@' + $taskResponse)
if ($LASTEXITCODE -ne 0) { throw 'MOD 编译失败。' }
Get-Item -LiteralPath $taskOut | Select-Object FullName,Length
$taskTestOut = Join-Path $taskCache '1014_SpeedMathTests_v6.dll'
$taskTestArgs = @('-nologo','-target:exe','-langversion:10','-optimize+','-nostdlib+',('-out:"' + $taskTestOut + '"'))
$taskTestArgs += $taskRefs | Where-Object DirectoryName -EQ $taskRuntime.FullName | ForEach-Object { '-reference:"' + $_.FullName + '"' }
$taskTestArgs += @('1014_SpeedMath_v6.cs','1014_SpeedMathTests_v6.cs') | ForEach-Object { '"' + (Join-Path $taskSource $_) + '"' }
$taskTestResponse = Join-Path $taskCache '1014_测试编译参数_v6.rsp'
$taskTestArgs | Set-Content -LiteralPath $taskTestResponse -Encoding utf8
& (Join-Path $DotNetRoot 'dotnet.exe') $taskCsc ('@' + $taskTestResponse)
if ($LASTEXITCODE -ne 0) { throw '测试编译失败。' }
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.36"}}}' | Set-Content -LiteralPath (Join-Path $taskCache '1014_SpeedMathTests_v6.runtimeconfig.json') -Encoding utf8
& (Join-Path $DotNetRoot 'dotnet.exe') $taskTestOut
if ($LASTEXITCODE -ne 0) { throw '速度算法测试失败。' }
