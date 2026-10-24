# NuGet 包消费级验证
#
# 验证"下游只加 PackageReference 就能用"：
#   1. 打包 NewLife.Audio；
#   2. 检查 nupkg 必须包含 lib 资产、README 与图标；
#   3. 创建临时项目，仅用 PackageReference 引用刚打的包；
#   4. 运行 G.711 编解码与中文词表查表，断言包内嵌多语言表可用。
#
# 用法：
#   pwsh -File tools/check-package-consumer.ps1
#   pwsh -File tools/check-package-consumer.ps1 -Keep     # 保留临时目录便于排查
param(
    [String]$Configuration = "Release",
    [String]$Framework = "net10.0",
    [String]$Version,
    [Switch]$Keep
)

$ErrorActionPreference = "Stop"
# 脚本位于 tools 子目录，仓库根为上一级
$root = Split-Path $PSScriptRoot -Parent
$work = Join-Path $root ".tmp\package-consumer"
$out = Join-Path $work "out"
$consumer = Join-Path $work "Consumer"

if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

Write-Host "1/4 打包 NewLife.Audio ..."
# 每次用唯一版本号，避免 NuGet 全局缓存命中上一次同版本包
if (!$Version) {
    $prefix = ([xml](Get-Content -LiteralPath (Join-Path $root "NewLife.Audio\NewLife.Audio.csproj"))).Project.PropertyGroup.VersionPrefix |
        Where-Object { $_ } | Select-Object -First 1
    $Version = "$prefix." + (Get-Date -Format "yyyy.MMdd") + "-beta" + (Get-Date -Format "HHmmss")
}
& dotnet pack (Join-Path $root "NewLife.Audio\NewLife.Audio.csproj") -c $Configuration -o $out -p:Version=$Version --nologo
if ($LASTEXITCODE -ne 0) { throw "打包失败，退出码 $LASTEXITCODE" }

$package = Get-ChildItem -LiteralPath $out -Filter "NewLife.Audio.$Version.nupkg" | Select-Object -First 1
if (!$package) { throw "未找到 NewLife.Audio nupkg" }

Write-Host "   包：$($package.Name)（版本 $Version）"

Write-Host "2/4 检查 nupkg 内容 ..."
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $entries = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    $required = @(
        "lib/net461/NewLife.Audio.dll",
        "lib/netstandard2.0/NewLife.Audio.dll",
        "lib/netstandard2.1/NewLife.Audio.dll",
        "README.nuget.md",
        "leaf.png"
    )
    foreach ($item in $required) {
        if ($entries -notcontains $item) { throw "nupkg 缺少 $item" }
    }
    Write-Host "   必需项齐全（三 TFM / README / 图标）"
}
finally {
    $zip.Dispose()
}

Write-Host "3/4 创建消费项目（仅 PackageReference）..."
& dotnet new console -n Consumer -o $consumer -f $Framework --no-restore | Out-Null
if ($LASTEXITCODE -ne 0) { throw "创建临时项目失败" }

$nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$out" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@
[IO.File]::WriteAllText((Join-Path $consumer "NuGet.config"), $nugetConfig, (New-Object System.Text.UTF8Encoding($false)))

& dotnet add (Join-Path $consumer "Consumer.csproj") package NewLife.Audio --version $Version --no-restore | Out-Null
if ($LASTEXITCODE -ne 0) { throw "添加 NewLife.Audio 包引用失败" }

$program = @"
using System;
using System.Globalization;
using NewLife;
using NewLife.Audio.Codecs;

Localization.Culture = CultureInfo.GetCultureInfo("zh-Hans");
if (Localization.Translate("Default device") != "默认设备")
{
    Console.Error.WriteLine("词表未生效: " + Localization.Translate("Default device"));
    return 1;
}

var codec = new G711ACodec();
var pcm = new Byte[320];
var encoded = codec.FromPcm(pcm, null);
var decoded = codec.ToPcm(encoded.GetSpan(), null);
if (encoded.Total != pcm.Length / 2 || decoded.Total != pcm.Length)
{
    Console.Error.WriteLine(`$"编解码长度异常: {encoded.Total}/{decoded.Total}");
    return 1;
}

Console.WriteLine("PASS");
return 0;
"@
[IO.File]::WriteAllText((Join-Path $consumer "Program.cs"), $program, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "4/4 编译并运行消费项目 ..."
$output = & dotnet run --project (Join-Path $consumer "Consumer.csproj") -c $Configuration 2>&1
$exit = $LASTEXITCODE
$output | ForEach-Object { Write-Host "   $_" }
if ($exit -ne 0) { throw "消费项目运行失败，退出码 $exit" }
if (($output -join "`n") -notmatch "PASS") { throw "消费项目未输出 PASS" }

Write-Host "通过：下游仅用 PackageReference 即可使用包内嵌词表与编解码" -ForegroundColor Green

if (!$Keep) { Remove-Item -LiteralPath $work -Recurse -Force }
else { Write-Host "临时目录已保留：$work" }
