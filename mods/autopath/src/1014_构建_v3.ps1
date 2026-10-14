param([Parameter(Mandatory=$true)][string]$GamePath, [string]$OutputPath = (Join-Path $PSScriptRoot '../.validate/autopath'), [string]$DotNetRoot = (Join-Path $env:ProgramFiles 'dotnet'))
$ErrorActionPreference = 'Stop'
$taskCache = Join-Path $PSScriptRoot '1014_构建缓存_v3'
$taskOutput = $OutputPath
New-Item -ItemType Directory -Path $taskCache,$taskOutput -Force | Out-Null
$env:DOTNET_CLI_HOME = $taskCache
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$taskSdk = Get-ChildItem -LiteralPath (Join-Path $DotNetRoot 'sdk') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$taskRuntime = Get-ChildItem -LiteralPath (Join-Path $DotNetRoot 'shared/Microsoft.NETCore.App') -Directory | Where-Object Name -Like '6.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$taskRefs = @()
$taskRefs += Get-ChildItem -LiteralPath $taskRuntime.FullName -Filter '*.dll' -File
$taskRefs += Get-ChildItem -LiteralPath (Join-Path $GamePath 'BepInEx\core') -Filter '*.dll' -File
$taskRefs += Get-ChildItem -LiteralPath (Join-Path $GamePath 'BepInEx\interop') -Filter '*.dll' -File
$taskRefs = $taskRefs | Where-Object { try { [Reflection.AssemblyName]::GetAssemblyName($_.FullName) | Out-Null; $true } catch [BadImageFormatException] { $false } }
$taskName = '1014_VRisingAutoPath_v3'
$taskSources = @('1014_AutoPathPlugin_v3.cs','1014_PathFollower_v3.cs','1014_WalkableLine_v3.cs','1014_LocalRoute_v3.cs','1014_BatFlightState_v3.cs','1014_FlightMotion_v3.cs')
$taskArgs = @('-nologo','-target:library','-langversion:10','-nullable:disable','-optimize+','-deterministic+','-unsafe+','-nostdlib+',('-out:"' + (Join-Path $taskOutput ($taskName + '.dll')) + '"'))
$taskArgs += $taskRefs | ForEach-Object { '-reference:"' + $_.FullName + '"' }
$taskArgs += $taskSources | ForEach-Object { '"' + (Join-Path $PSScriptRoot $_) + '"' }
$taskRsp = Join-Path $taskCache ($taskName + '.rsp')
$taskArgs | Set-Content -LiteralPath $taskRsp -Encoding utf8
& (Join-Path $DotNetRoot 'dotnet.exe') (Join-Path $taskSdk.FullName 'Roslyn\bincore\csc.dll') ('@' + $taskRsp)
if ($LASTEXITCODE -ne 0) { throw 'Compile failed' }
Get-Item -LiteralPath (Join-Path $taskOutput ($taskName + '.dll')) | Select-Object FullName,Length
