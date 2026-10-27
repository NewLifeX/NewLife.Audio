# NewLife.Audio - 音频编解码库

![GitHub top language](https://img.shields.io/github/languages/top/newlifex/NewLife.Audio?logo=github)


![GitHub License](https://img.shields.io/github/license/NewLifeX/NewLife.Audio?logo=github)
![NuGet Downloads](https://img.shields.io/nuget/dt/NewLife.Audio?logo=nuget)
![NuGet](https://img.shields.io/nuget/v/NewLife.Audio?logo=nuget)
![NuGet prerelease](https://img.shields.io/nuget/vpre/NewLife.Audio?label=dev%20nuget&logo=nuget)
[![test](https://github.com/NewLifeX/NewLife.Audio/actions/workflows/test.yml/badge.svg?branch=master)](https://github.com/NewLifeX/NewLife.Audio/actions/workflows/test.yml)
[![NativeAOT](https://github.com/NewLifeX/NewLife.Audio/actions/workflows/aot.yml/badge.svg?branch=master)](https://github.com/NewLifeX/NewLife.Audio/actions/workflows/aot.yml)
<!-- metrics:start -->
[![AOT smoke](https://img.shields.io/badge/AOT%20smoke-20%2F20-brightgreen?logo=dotnet)](Doc/AOT兼容性分析.md)
[![TFM](https://img.shields.io/badge/TFM-4%20%28net461%20%E2%86%92%20net8.0%29-512BD4?logo=dotnet)](https://www.nuget.org/packages/NewLife.Audio)
[![locales](https://img.shields.io/badge/locales-31-2ea44f?logo=dotnet)](Doc/多语言本地化.md)
<!-- metrics:end -->
![platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS%20%7C%20Android%20%7C%20iOS-blue)
![arch](https://img.shields.io/badge/arch-x64%20%7C%20x86%20%7C%20arm64%20%7C%20arm%20%7C%20loongarch64%20%7C%20riscv64%20%7C%20s390x-orange)
[![contributors](https://img.shields.io/github/contributors/NewLifeX/NewLife.Audio?logo=github)](https://github.com/NewLifeX/NewLife.Audio/graphs/contributors)

<!-- lang:start -->
🌐 [English](README.md) | [繁體中文](README.zh-TW.md) | [Español](README.es.md) | [Deutsch](README.de.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md) | [한국어](README.ko.md) | [العربية](README.ar.md)
<!-- lang:end -->

[English](README.md) | 简体中文

跨平台 .NET 全功能音频处理库，纯托管优先，提供编解码（G.711/ADPCM/AAC/Opus/MP3/FLAC/Vorbis/Speex/G.722/G.726）、设备 I/O（WaveOut/WASAPI/ASIO）、DSP 信号链处理、语音处理（VAD/AEC/AGC/NS）、TTS 语音合成、ASR 语音识别、音频文件门面（打开→解码→编辑→保存）、容器格式读写（WAV/Ogg/MP4/FLAC/MP3/AIFF/CAF）、流媒体传输（RTP/RTSP/HTTP/WebSocket）与 MIDI 音序渲染一站式音频能力。

🎧 附带 [AudioStudio](AudioStudio/) 跨平台桌面演示客户端（Avalonia），提供播放、MIDI 音序、转换、设备、录制、DSP、诊断、语音合成八大工具模块。

源码： https://github.com/NewLifeX/NewLife.Audio  
Nuget：NewLife.Audio  

📖 详细文档：[需求文档](Doc/需求文档.md) | [模块清单](Doc/模块清单.md) | [架构设计](Doc/架构设计.md) | [客户端需求文档](Doc/需求文档-客户端.md) | [客户端功能清单](Doc/功能模块清单-客户端.md) | [竞品分析](Doc/竞品分析报告.md) | [AOT 兼容性](Doc/AOT兼容性分析.md) | [多语言本地化](Doc/多语言本地化.md) | [各模块架构设计](Doc/)

> ℹ️ 本中文版与 [English README](README.md) 同批维护；若两者内容不一致，以英文版为对外口径。

## 功能特性

### 🎵 编解码引擎（CODEC）
纯托管 C# 实现 10+ 音频编解码器的 PCM 双向转换：
- **G.711** μ-law / A-law
- **G.722** / **G.726** 子带 ADPCM
- **IMA ADPCM**（DVI4）
- **Opus**（自研纯托管进行中：RFC 6716 包解析 + 范围编解码器，系统 ffmpeg 降级）
- **MP3**（解码：Huffman + 反量化 + IMDCT + 子带合成，比特池、坏帧错误隐藏与 CRC 校验；编码：多相分析 + MDCT + 量化 + Huffman，支持 M/S 联合立体声、CBR/VBR/ABR + Xing 信息帧、简化心理声学模型、跨帧比特池与可选 CRC；短块尚未实现）
- **AAC**（纯托管 AAC-LC/HE-AAC 解码；AAC-LC 编码；ADTS/raw/MP4/LOAS/LATM）
- **FLAC**（Fixed LPC + Rice 残差编码 + MD5）
- **Ogg Vorbis**（纯托管解码：floor 0/1、residue 0/1/2、声道耦合、MDCT；编码为长块基线）
- **Speex**（纯托管窄带，20ms 帧 + 4 bit 自适应差分量化；私有码流，不与 libspeex 互通）
- 基于 `CodecRegistry` + `ICodecInfo` 元数据的可插拔工厂
- IoT 芯片头适配（海思 / 大华 / 宇视）

### 🔊 音频设备 I/O（DEVIO）
跨平台音频设备抽象：
- 设备枚举与发现
- Windows 下的 **WaveOut** / **WASAPI** 后端（ASIO 规划中）
- 音频采集与环回录制
- 通过 `AudioDeviceManager` 进行多设备并发管理

> ⚠️ Windows 下 WaveOut/WaveIn（WinMM）与 WASAPI（共享模式播放/采集、loopback、事件回调低延迟流、设备丢失恢复、热插拔观察）均已完整实现；ASIO 与 Linux/macOS/Android/iOS 后端尚未实现，状态与排期见[架构设计](Doc/架构设计.md)。

### 🎛️ DSP 处理链（DSP）
可组合的音频信号处理链：
- 重采样（线性 + 加窗 Sinc）
- 声道转换（单声道 ↔ 立体声 ↔ 多声道）
- 位深 / 采样格式转换（8/16/24/32 位 ↔ 浮点/双精度）
- 音量控制与增益（线性 / 对数）
- 多源混音与归一化
- **FFT** 频谱分析（Cooley-Tukey，Hann/Hamming/Blackman 窗）
- **均衡器**（10 段图示均衡 + 3 段音调，BiQuad 级联）
- **BiQuad 滤波器**（RBJ 标准：LP/HP/BP/Notch/Peaking/LowShelf/HighShelf）
- 动态范围压缩器（软拐点，RMS 包络）
- 淡入/淡出与交叉淡化（线性/对数/余弦）
- 变速（OLA）与变调
- 可串联的 `IAudioProcessor` + `AudioPipeline`

### 🗣️ 语音处理（VOP）
实时语音通信前端：
- **VAD** — 语音活动检测（GMM 模型，6 个子带 + 挂起）
- **AEC** — 声学回声消除（NLMS 自适应滤波 + 双讲检测 + 舒适噪声）
- **AGC** — 自动增益控制（慢启动快释放，RMS 包络）
- **NS** — 噪声抑制（谱减法 + FFT/IFFT + OLA 合成）
- 语音预处理链：HPF → VAD → AGC
- `SpeechProcessingPipeline`：10 ms 帧对齐、参考延迟补偿、AEC → NS → AGC，输出 ERLE/双讲/收敛指标

### 📦 容器格式（CONT）
音频文件格式读写：
- **WAV** — 完整 RIFF 解析与写入（EXTENSIBLE/浮点/24 位/RF64/LIST-INFO）
- **Ogg** — 页级读写（Opus/Vorbis）
- **MP3** — 帧索引、ID3v2.2/v2.3/v2.4 + v1（反同步、压缩帧、原地改标签）、Xing/Info/VBRI、gapless
- **MP4/M4A** — ISO BMFF：ftyp/moov/mdat box 解析与生成，iTunes 标签
- **FLAC** — 原生 fLaC 元数据读写（STREAMINFO/SEEKTABLE/VORBIS_COMMENT）
- **AIFF/CAF** — AIFF 读写；CAF 读取
- **探测** — 魔术字节识别容器/编码/时长/码率/标签
- **Raw PCM** — 必须显式指定格式，不做启发式推断

### 🗂️ 音频文件门面（FILE）
一条调用链完成打开 → 解码 → 编辑 → 保存：
- `AudioFile.Open` 自动识别容器/编码，暴露格式、时长、标签与可定位性
- 无损 PCM 编辑：裁剪、拼接、增益、归一化、重采样
- 标签保留、仅改标签原子重写、临时文件 + 原子替换保存
- MP3/AAC/WAV/Ogg Opus 参数一致时帧级复制/重封装，否则明确回退重编码
- 批量转换，支持进度回调与取消
- `StreamPcm` 流式解码 16 位 PCM 分块：WAV/AIFF/CAF/M4A 逐帧解码，MP3/FLAC/Ogg 整段解码后分块；流式路径内存只与分块大小相关

### 📡 流媒体与传输（STRM）
网络音频流传输：
- **RTP** 打包与解包（RFC 3550）+ 会话管理
- **RTSP** 客户端（TCP：OPTIONS→DESCRIBE→SETUP→PLAY→TEARDOWN，SDP 解析）
- **HTTP** 音频流（Icecast/SHOUTcast）
- **WebSocket** 双向音频管道（5 字节帧头）

### 🎼 MIDI 音序（MIDI）
纯托管标准 MIDI 文件读写与波形渲染：
- SMF 1.0 读写（Format 0/1/2、PPQ/SMPTE、VLQ、running status、Meta/SysEx 透传）；Format 2 按独立序列渲染
- 纯托管合成器：GM 音色分组、带限波表、ADSR、力度/音量/表情/声像/弯音/延音、复音上限、软限幅
- 可选 SoundFont 2（`.sf2`）与 DLS Level 1/2（`.dls`）采样音源，通过 `MidiRenderOptions.SoundBank` 加载
- MPE 连续弯音/通道压力，MIDI 2.0 UMP 包与 SysEx7 解析
- Windows 实时 MIDI 输出（WinMM `midiOut`/`midiIn`、设备枚举、按 tempo 调度的 `MidiOutputSequencer`）
- 渲染为 16 位 PCM，复用 `AudioFile.WritePcm` 输出 WAV/MP3/FLAC
- AudioStudio **MIDI 音序**页：渐变 min/max 波形、RMS 响度线、节拍网格、发光播放头、只读迷你钢琴卷、采样音源加载、MIDI 输出面板
- 内置公有领域示例（欢乐颂、绿袖子、卡农、茉莉花），位于 `AudioStudio/Assets/Midi`
- 控制台示例 `Samples/MidiDemo`：`info` / `render` / `play` / `gen`，可附加 `.sf2`/`.dls` 音源
- MIDI 是乐谱/事件流，不是采样音频；USB-MIDI/BLE-MIDI 与 MIDI 2.0 原生设备传输不在范围内

### 🔈 语音合成（TTS）
把文字变成声音，普通用户三行代码可用：
- **直接播报** — `await new TtsClient().SayAsync("文字")`，Windows SAPI / macOS `say` / Linux `espeak-ng`（Windows .NET Core/.NET 5+ 应用需引用 `System.Speech` 包）
- **文件输出** — WAV / 裸 PCM / MP3 / FLAC，扩展名决定格式（压缩格式走重采样 → 编码 → 容器管线）
- **流式输出** — 逐块 PCM，适合 HTTP/WebSocket 边合成边发送
- **引擎可插拔** — 系统语音、`HttpTtsEngine`、`ProcessTtsEngine`、自定义 `ITtsEngine`
- **云端与离线** — 阿里云/腾讯云/百度/Azure 引擎，免密钥百度翻译发音；可选 `NewLife.Audio.Sherpa` 离线神经音色（VITS/Matcha/Kokoro）
- **可选高级** — 音色/语速与音色目录（`TtsVoiceCatalog` 跨引擎按语言与场景挑选）、磁盘缓存、预生成语音库、文本规范化（`NewLife.Audio.Tts.Advanced`）

### 🎙️ 语音识别（ASR）
低优先级适配层，不内置模型与推理运行时：
- `ISpeechRecognizer` 流式 PCM 输入，输出部分/最终结果、时间戳与端点检测
- Windows 系统识别（`System.Speech.Recognition`）
- 可选 `NewLife.Audio.Sherpa` 离线 sherpa-onnx 识别包
- Android/iOS 系统适配随平台 TFM 补齐

## 安装

```bash
dotnet add package NewLife.Audio
```

## 快速开始

```csharp
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Audio.Tts;

// 将 G.711 μ-law 解码为 PCM
var codec = new G711UCodec();
var pcm = codec.ToPcm(encodedData);

// 将 PCM 编码为 G.711 A-law
var g711a = new G711ACodec();
var encoded = g711a.FromPcm(pcmData);

// 文字转语音：直接播报 + 保存 WAV
var tts = new TtsClient();
await tts.SayAsync("你好，世界");
await tts.SaveAsync("你好，世界", "hello.wav");
```

## 生态版图

NewLife 生态按依赖分层，各项目独立版本、独立发版。下面的版图在所有 NewLife 仓库中逐字一致；每个项目链接到代码仓库与主 NuGet 包。

| 项目 | 一句话定位 | 下载量 |
| --- | --- | --- |
| **基础底座** | | |
| [NewLife.Core](https://github.com/NewLifeX/X) | 日志、配置、缓存、网络、序列化、定时调度与 APM 追踪。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Core?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Core) |
| [NewLife.XCode](https://github.com/NewLifeX/NewLife.XCode) | 数据中间件：单表百亿级、自动分表、读写分离，支持 10 多种数据库。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCode?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCode) |
| [NewLife.Net](https://github.com/NewLifeX/NewLife.Net) | 高性能网络库：单机 177 万 pps、14.5 Gbps（TCP 1KB，2026-10 实测），400 万 Tcp 长连接。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Net?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Net) |
| [NewLife.Remoting](https://github.com/NewLifeX/NewLife.Remoting) | 协议通信与 RPC 框架，面向 CS 应用与物联网设备。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Remoting?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Remoting) |
| [NewLife.Agent](https://github.com/NewLifeX/NewLife.Agent) | 把 .NET 应用安装为操作系统守护进程：Windows 服务与 Linux systemd。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Agent?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Agent) |
| [NewLife.Templates](https://github.com/NewLifeX/NewLife.Zero) | `dotnet new` 项目模板：控制台、Web、WebApi、服务、数据与物联网。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Templates?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Templates) |
| **数据存储与消息** | | |
| [NewLife.Redis](https://github.com/NewLifeX/NewLife.Redis) | Redis 客户端：微秒级延迟、百万级吞吐，内置消息队列。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Redis?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Redis) |
| [NewLife.RocketMQ](https://github.com/NewLifeX/NewLife.RocketMQ) | 纯托管 RocketMQ 客户端，支持 Apache RocketMQ 与阿里云消息队列。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.RocketMQ?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.RocketMQ) |
| [NewLife.MQTT](https://github.com/NewLifeX/NewLife.MQTT) | MQTT 客户端与服务端，客户端支持阿里云物联网。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MQTT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MQTT) |
| [NewLife.NovaDb](https://github.com/NewLifeX/NewLife.NovaDb) | C# 混合数据库：嵌入/网络双模，关系型、时序、消息队列与 KV 于一体。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.NovaDb?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.NovaDb) |
| [NewLife.MySql](https://github.com/NewLifeX/NewLife.MySql) | MySql 驱动与数据访问辅助层，基于 NewLife 数据栈实现。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MySql?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MySql) |
| **Web、AI 与分布式** | | |
| [NewLife.Cube](https://github.com/NewLifeX/NewLife.Cube) | 魔方快速开发平台：用户权限、SSO、OAuth 服务端与 CRUD 脚手架。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Cube?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Cube) |
| [Stardust](https://github.com/NewLifeX/Stardust) | 星尘分布式服务平台：节点管理、APM、配置中心、注册中心与发布中心。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Stardust?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Stardust) |
| [AntJob](https://github.com/NewLifeX/AntJob) | 蚂蚁调度：分布式任务与实时数据平台，支持分片、重试与回溯补算。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AntJob?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AntJob) |
| [NewLife.AI](https://github.com/NewLifeX/NewLife.AI) | 统一接入 46 家大模型服务商，附带开箱即用的 ChatAI Web 对话应用。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AI?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AI) |
| **工业与物联网** | | |
| [NewLife.IoT](https://github.com/NewLifeX/NewLife.IoT) | IoT 标准库，定义物联网通信协议契约与设备抽象。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IoT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IoT) |
| [NewLife.Modbus](https://github.com/NewLifeX/NewLife.Modbus) | Modbus TCP/RTU/ASCII，基于 NewLife.IoT 实现。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Modbus?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Modbus) |
| [NewLife.Siemens](https://github.com/NewLifeX/NewLife.Siemens) | 西门子 PLC 协议，基于 NewLife.IoT 实现。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Siemens?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Siemens) |
| [NewLife.Omron](https://github.com/NewLifeX/NewLife.Omron) | 欧姆龙 PLC 协议，基于 NewLife.IoT 实现。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Omron?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Omron) |
| [NewLife.Melsec](https://github.com/NewLifeX/NewLife.Melsec) | 三菱 Melsec PLC 协议，基于 NewLife.IoT 实现。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Melsec?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Melsec) |
| [NewLife.OPC](https://github.com/NewLifeX/NewLife.OPC) | OPC 客户端，用于工业数据采集。 | — |
| [NewLife.BACnet](https://github.com/NewLifeX/NewLife.BACnet) | BACnet 楼宇自控协议栈。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.BACnet?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.BACnet) |
| [ZeroIoT](https://github.com/NewLifeX/ZeroIoT) | 开源轻量级 IoT 平台，含网关与设备管理。 | — |
| **车联网** | | |
| [NewLife.JT808](https://github.com/NewLifeX/NewLife.JT808) | JT/T 808 车辆定位协议：70+ 消息体，覆盖 2011/2013/2019 三个版本。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT808?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT808) |
| [NewLife.JT809](https://github.com/NewLifeX/NewLife.JT809) | JT/T 809 部标平台互联协议，面向运输管理部门。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT809?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT809) |
| [NewLife.JT1078](https://github.com/NewLifeX/NewLife.JT1078) | JT/T 1078 车载视频：RTP 解析、FLV/fMP4 转封装与直播推送。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT1078?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT1078) |
| **领域组件** | | |
| [NewLife.Office](https://github.com/NewLifeX/NewLife.Office) | 一个包覆盖 Excel、Word、PPT、PDF 等 15+ 文档格式。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Office?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Office) |
| [NewLife.Audio](https://github.com/NewLifeX/NewLife.Audio) | 跨平台音频：编解码、设备 IO、DSP、容器与流式处理。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Audio?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Audio) |
| [NewLife.Video](https://github.com/NewLifeX/NewLife.Video) | 视频编解码、封装格式、帧处理与流媒体。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Video?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Video) |
| [NewLife.Map](https://github.com/NewLifeX/NewLife.Map) | 百度/高德/腾讯/天地图统一接口，支持地理编码与坐标转换。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Map?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Map) |
| [NewLife.IP](https://github.com/NewLifeX/NewLife.IP) | IPv4 归属地：离线内存映射库 + 在线 API 双源。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IP?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IP) |
| **工具与产品** | | |
| [CrazyCoder](https://github.com/NewLifeX/XCoder) | 码神工具：网络、串口、加解密、正则、Modbus 与 MQTT。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCoder?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCoder) |
| [GitCandy](https://github.com/NewLifeX/GitCandy) | 自托管 Git 源代码管理系统。 | — |
| [LuckyClover](https://github.com/NewLifeX/LuckyClover) | 从低版本宿主启动时，自动安装匹配的 .NET 运行时。 | — |

> 徽章取各项目主包；主题包、扩展包与卫星包见对应 NuGet 页面。未在表中列出的 30 多个包（FTP、LoRa、Office 扩展、各类协议驱动等）见 [NewLifeX 组织](https://github.com/NewLifeX) 与 [newlifex.com](https://newlifex.com)。

## 新生命开发团队

<img src="https://newlifex.com/logo.png" width="48" align="right" alt="NewLife" />

新生命团队成立于 2002 年，二十余年专注 .NET 与物联网技术生态，主导开源项目近百个（生态版图收录 34 个主干项目），为电力、物流、医疗、工控、互联网等行业提供软硬件一体化解决方案；以“学无先后达者为师”为信条。

开源组件在 NuGet 累计下载 <!--nuget-total:start-->6.48M<!--nuget-total:end--> 次（全生态口径，截至 2026-10）。

网站：https://newlifex.com · 开源组织：https://github.com/NewLifeX

<details>
<summary>微信公众号：智能大石头（扫码关注）</summary>

![智能大石头](https://newlifex.com/stone.jpg)

</details>

## 授权

MIT License © 2002-2026 新生命开发团队

第三方组件（SharpJaad.AAC、EggEncoder MDCT、minimp3 码表）声明见
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

[GitHub](https://github.com/NewLifeX/NewLife.Audio) | [网站](https://newlifex.com)
