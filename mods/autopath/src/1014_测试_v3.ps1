param([string]$DotNetRoot = (Join-Path $env:ProgramFiles 'dotnet'))
$ErrorActionPreference = 'Stop'
$taskCache = Join-Path $PSScriptRoot '1014_构建缓存_v3'
New-Item -ItemType Directory -Path $taskCache -Force | Out-Null
$taskSdk = Get-ChildItem -LiteralPath (Join-Path $DotNetRoot 'sdk') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$taskRuntime = Get-ChildItem -LiteralPath (Join-Path $DotNetRoot 'shared/Microsoft.NETCore.App') -Directory | Where-Object Name -Like '6.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$taskOut = Join-Path $taskCache '1014_PathFollowerTests_v3.dll'
$taskArgs = @('-nologo','-target:exe','-langversion:10','-optimize+','-nostdlib+',('-out:"' + $taskOut + '"'))
$taskArgs += Get-ChildItem -LiteralPath $taskRuntime.FullName -Filter '*.dll' | Where-Object {
    try { [Reflection.AssemblyName]::GetAssemblyName($_.FullName) | Out-Null; $true } catch [BadImageFormatException] { $false }
} | ForEach-Object { '-reference:"' + $_.FullName + '"' }
$taskArgs += @('1014_PathFollower_v3.cs','1014_WalkableLine_v3.cs','1014_LocalRoute_v3.cs','1014_FlightMotion_v3.cs','1014_PathFollowerTests_v3.cs') | ForEach-Object { '"' + (Join-Path $PSScriptRoot $_) + '"' }
$taskRsp = Join-Path $taskCache '1014_测试编译参数_v3.rsp'
$taskArgs | Set-Content -LiteralPath $taskRsp -Encoding utf8
& (Join-Path $DotNetRoot 'dotnet.exe') (Join-Path $taskSdk.FullName 'Roslyn\bincore\csc.dll') ('@' + $taskRsp)
if ($LASTEXITCODE -ne 0) { throw 'Test compile failed' }
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.36"}}}' | Set-Content -LiteralPath (Join-Path $taskCache '1014_PathFollowerTests_v3.runtimeconfig.json') -Encoding utf8
& (Join-Path $DotNetRoot 'dotnet.exe') $taskOut | Tee-Object -LiteralPath (Join-Path $taskCache '1014_算法验证_v3.log')
if ($LASTEXITCODE -ne 0) { throw 'Path follower tests failed' }
