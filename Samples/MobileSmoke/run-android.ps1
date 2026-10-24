#Requires -Version 7.0
[CmdletBinding()]
param(
    [String]$Configuration = "Release",
    [Int32]$TimeoutSec = 180,
    [ValidateSet("Jit", "Aot", "NativeAot")]
    [String]$Mode = "Jit",
    [String]$Device = "",
    [Switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$pack = Join-Path $PSScriptRoot "pack-android.ps1"
$package = "com.newlife.smoke.audio"
$apkDir = Join-Path $root "Bin/MobileSmoke/apk"
$artifactDir = Join-Path $root "Bin/MobileSmoke/artifacts"

# adb 不在 PATH 是常态（Windows / CI 均如此），依次探测 ANDROID_HOME、SDK 默认目录与 PATH
function Find-Adb {
    $sdks = @()
    if ($env:ANDROID_HOME) { $sdks += $env:ANDROID_HOME }
    if ($env:ANDROID_SDK_ROOT) { $sdks += $env:ANDROID_SDK_ROOT }
    if ($IsWindows -and $env:LOCALAPPDATA) { $sdks += (Join-Path $env:LOCALAPPDATA "Android/Sdk") }

    $exe = if ($IsWindows) { "adb.exe" } else { "adb" }
    foreach ($sdk in $sdks) {
        $path = Join-Path $sdk "platform-tools/$exe"
        if (Test-Path $path) { return $path }
    }

    $cmd = Get-Command adb -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    throw "adb not found. Install platform-tools or set ANDROID_HOME."
}

$adb = Find-Adb

$devices = @()
foreach ($line in (& $adb devices)) {
    if ($line -match "^\s*(\S+)\s+device\s*$") { $devices += $Matches[1] }
}
if (-not $Device) {
    if ($devices.Count -eq 0) { throw "No Android device/emulator online. Start an emulator or connect a phone with USB debugging." }
    $Device = $devices[0]
}
if ($devices -notcontains $Device) { throw "Device $Device is not online. Online: $($devices -join ', ')" }
Write-Host "Using device: $Device"

$abi = (& $adb -s $Device shell getprop ro.product.cpu.abi).Trim()
# 注意 switch -Regex 会匹配所有分支，必须 break，否则 x86_64 同时命中 x86 分支得到两个 RID
$rid = switch -Regex ($abi) {
    "arm64" { "android-arm64"; break }
    "armeabi" { "android-arm"; break }
    "x86_64" { "android-x64"; break }
    "x86" { "android-x86"; break }
    default { throw "Unsupported ABI: $abi" }
}
Write-Host "ABI=$abi RID=$rid Mode=$Mode"

# Jit 模式必须在打包阶段关闭 AOT（pack-android.ps1 的 Jit 分支），否则 Release 默认开启 AOT
if (-not $SkipBuild) {
    & pwsh -NoProfile -File $pack -Mode $Mode -Configuration $Configuration -RuntimeIdentifier $rid
    if ($LASTEXITCODE -ne 0) { throw "Android $Mode build failed" }
}

$apk = Join-Path $apkDir ("MobileSmoke-" + $Mode + ".apk")
if (-not (Test-Path $apk)) {
    $apk = Get-ChildItem (Join-Path $root "Bin/MobileSmoke") -Recurse -Filter *.apk -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $apk -or -not (Test-Path $apk)) { throw "APK not found. Run pack-android.ps1 first." }
Write-Host "Installing $apk ..."

& $adb -s $Device install -r $apk | Out-Host
if ($LASTEXITCODE -ne 0) { throw "adb install failed" }

& $adb -s $Device logcat -c | Out-Null
& $adb -s $Device shell am force-stop $package | Out-Null

# monkey 在装了多个 NewLife 冒烟包时会启动到别的包，按包名解析真正的 Launcher Activity 再显式启动
$activity = (& $adb -s $Device shell cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.LAUNCHER $package |
    Select-Object -Last 1).Trim()
if (-not $activity) { throw "Unable to resolve $package launcher activity." }
& $adb -s $Device shell am start -n $activity | Out-Null
Write-Host "Started $activity"

New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
$logFile = Join-Path $artifactDir ("android-" + $Mode.ToLower() + "-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".log")
$hasPidof = [bool]((& $adb -s $Device shell pidof logd 2>$null | Out-String).Trim())
$deadline = (Get-Date).AddSeconds($TimeoutSec)
$startedAt = Get-Date
$seen = $false
$hit = $null

while ((Get-Date) -lt $deadline) {
    $hit = & $adb -s $Device logcat -d -s MobileSmoke:* | Select-String "SMOKE_OK|SMOKE_FAIL" | Select-Object -Last 1
    if ($hit) { break }

    # 崩溃后不会再有总结行，立即失败并导出日志，而不是白等到超时
    if (-not $hasPidof) { Start-Sleep -Seconds 2; continue }
    $appPid = (& $adb -s $Device shell pidof $package 2>$null | Out-String).Trim()
    if ($appPid) {
        $seen = $true
    }
    elseif ($seen -or ((Get-Date) - $startedAt).TotalSeconds -gt 15) {
        $logs = & $adb -s $Device logcat -d -s MobileSmoke:* | Out-String
        $logs | Out-File -FilePath $logFile -Encoding utf8
        $logs | Select-Object -Last 80 | Out-Host
        throw "MobileSmoke process exited before reporting a result (crash?). log=$logFile"
    }
    Start-Sleep -Seconds 2
}

$logs = & $adb -s $Device logcat -d -s MobileSmoke:* | Out-String
$logs | Out-File -FilePath $logFile -Encoding utf8
$logs | Out-Host

if (-not $hit) {
    throw "Android smoke timed out after ${TimeoutSec}s. log=$logFile"
}

Write-Host $hit.Line
Write-Host "log=$logFile"
if ($hit.Line -match "SMOKE_OK") { exit 0 }
throw "Android smoke failed: $($hit.Line)"
