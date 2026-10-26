# NewLife.Audio AOT 兼容性现状与边界

> 基线：NewLife.Audio 1.2｜评估方式：**NativeAOT 真实编译 + 运行实测**（.NET 10）｜冒烟工程：`AotSmoke/`

## 1. 现状摘要

- **核心链路实测全通**：20 场景冒烟 JIT/AOT 双通，覆盖编解码往返、容器读写、RTP 封包组帧、DSP 信号链、语音处理、芯片头、统一工厂入口，以及 **31 种语言词表查表与英文回退**。
- **原生互操作**：`FaacEncoder` 仅为兼容保留并已标记 `[Obsolete]`，AAC 编解码主链路不再引用 libfaac；AOT 分析器对该遗留可选路径的提示性告警仍在 `AotSmoke.csproj` 按设计豁免 `IL2091`，下一主版本移除后可一并删除。
- **纯托管 Opus**：Opus 包解析与范围编解码器为纯托管实现，无反射与动态代码，纳入 AOT 冒烟；托管 CELT/SILK 完成前可选用系统 ffmpeg 子进程降级，未找到时抛出清晰异常，不影响 AOT 编译。
- **纯托管 Vorbis**：解码覆盖 floor0/1、residue 0/1/2、声道耦合与长短块，编码为长块基线；无反射、无动态代码、无原生依赖，AOT 冒烟包含真实编解码往返。
- **不变全球化**：同一 AOT 二进制在 `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` 下运行，语言表注册被跳过，运行时按英文回退且不抛异常。
- **Android NativeAOT**：`Samples/MobileSmoke/pack-android.ps1` 支持 JIT/AOT/NativeAOT 打包；NativeAOT arm64 APK 本机实测 6.32 MB，`mobile.yml` 手动触发可选择 NativeAot 模式跑模拟器冒烟。
- **设备 I/O 不在冒烟范围**：AotSmoke 不调用 winmm/WASAPI/ASIO，避免 CI 无音频设备；设备后端由单元测试与各平台桌面客户端覆盖。

## 2. 覆盖矩阵

| 平台/RID | 运行方式 | CI 状态 |
|---|---|---|
| win-x64 | NativeAOT | 阻断门禁 |
| linux-x64 / linux-arm64 | NativeAOT | 阻断门禁 |
| win-x86 / win-arm64 / osx-arm64 / osx-x64 | NativeAOT | 观察期（continue-on-error） |
| linux-musl-x64 / linux-musl-arm64 | Alpine 容器内 NativeAOT | 观察期 |
| linux-loongarch64 | QEMU + 龙芯 SDK，JIT 冒烟 | 人工触发 |

工作流：[aot.yml](../.github/workflows/aot.yml)。arm32 / riscv64 / s390x 的 JIT 冒烟由 [arch.yml](../.github/workflows/arch.yml) 覆盖，不属于 NativeAOT 范围。

## 3. 已知边界

- **动态能力**：除 TTS 系统语音外，本库不使用反射发射、脚本或插件动态加载，AOT 裁剪下无相关回退路径。
- **TTS 系统语音**：`SystemSpeechEngine` 通过反射加载 `System.Speech`（Windows SAPI），NativeAOT/裁剪下不保证可用；AOT 场景请注入 `ProcessTtsEngine`、`HttpTtsEngine` 或自定义 `ITtsEngine`。AOT 冒烟不调用系统语音，因此不会触发音频设备或反射路径。
- **原生库**：`Audio/libfaac.dll` 仅服务于已标记 `[Obsolete]` 的 `FaacEncoder`，AAC 编解码主链路不再使用；下一主版本移除，不影响 AOT 编译与核心编解码。Opus 自研实现不使用 P/Invoke；系统 ffmpeg 仅为可选降级路径，缺失时由托管包解析与范围编解码器继续工作。
- **目标框架**：AOT 冒烟以 `net10.0` 运行；`net8.0` 通过 net10 冒烟间接覆盖；`net461` / `netstandard2.0` / `netstandard2.1` 目标不参与 AOT 编译，只验证常规构建。
- **音频设备**：winmm/WASAPI/ASIO 的真实设备调用需要宿主音频栈，CI 不覆盖；相关 P/Invoke 为静态绑定，AOT 分析器无新增告警。

## 4. 本地复现

```powershell
# JIT 对照
dotnet run -c Release --project AotSmoke/AotSmoke.csproj

# NativeAOT
dotnet publish AotSmoke/AotSmoke.csproj -c Release -r win-x64 -o AotSmoke/bin/aot-out
.\AotSmoke\bin\aot-out\AotSmoke.exe

# 不变全球化
$env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1'
.\AotSmoke\bin\aot-out\AotSmoke.exe
```
