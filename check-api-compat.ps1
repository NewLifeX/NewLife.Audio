# 二进制兼容门禁本地检查（Microsoft Package Validation）
# 以「同一 1.2 线最新已发布版本」为基线，对当前源码打出的包做 API 兼容校验。
#
# 用法：
#   pwsh -File check-api-compat.ps1                        # 基线自动取最新 1.2.* 已发布版本
#   pwsh -File check-api-compat.ps1 -BaselineVersion 1.2.2026.1019-beta1203
#
# 结果：输出包到 .tmp\api-compat（已 gitignore），退出码即校验结果（0=通过）。
param([String]$BaselineVersion)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Set-Location $root

if (-not $BaselineVersion) {
    $v = (Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/newlife.audio/index.json").versions
    $BaselineVersion = $v | Where-Object { $_ -like "1.2.*" } | Select-Object -Last 1
    if (-not $BaselineVersion) { throw "未能从 NuGet 获取 1.2.* 基线版本" }
}
Write-Host "Package validation baseline = $BaselineVersion"

# 清理 ApiCompat 增量 semaphore，否则更换基线版本会被静默跳过
Get-ChildItem NewLife.Audio\obj -Recurse -Filter "Microsoft.NET.ApiCompat.ValidatePackage.semaphore" -ErrorAction SilentlyContinue | Remove-Item -Force

if (Test-Path .tmp\api-compat) { Remove-Item .tmp\api-compat -Recurse -Force }
dotnet pack NewLife.Audio\NewLife.Audio.csproj -c Release -o .tmp\api-compat `
  -p:EnablePackageValidation=true `
  -p:PackageValidationBaselineVersion=$BaselineVersion
exit $LASTEXITCODE
