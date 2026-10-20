<!-- generated from README.md by sync-readme-package.ps1; do not edit -->

# NewLife.Audio - Audio Codec Library

![GitHub top language](https://img.shields.io/github/languages/top/newlifex/NewLife.Audio?logo=github)


![GitHub License](https://img.shields.io/github/license/NewLifeX/NewLife.Audio?logo=github)
![NuGet Downloads](https://img.shields.io/nuget/dt/NewLife.Audio?logo=nuget)
![NuGet](https://img.shields.io/nuget/v/NewLife.Audio?logo=nuget)
![NuGet prerelease](https://img.shields.io/nuget/vpre/NewLife.Audio?label=dev%20nuget&logo=nuget)
[![test](https://github.com/NewLifeX/NewLife.Audio/actions/workflows/test.yml/badge.svg?branch=master)](https://github.com/NewLifeX/NewLife.Audio/actions/workflows/test.yml)
[![NativeAOT](https://github.com/NewLifeX/NewLife.Audio/actions/workflows/aot.yml/badge.svg?branch=master)](https://github.com/NewLifeX/NewLife.Audio/actions/workflows/aot.yml)
<!-- metrics:start -->
[![AOT smoke](https://img.shields.io/badge/AOT%20smoke-19%2F19-brightgreen?logo=dotnet)](https://github.com/NewLifeX/NewLife.Audio/blob/master/Doc/AOT兼容性分析.md)
[![TFM](https://img.shields.io/badge/TFM-3%20%28net461%20%E2%86%92%20netstandard2.1%29-512BD4?logo=dotnet)](https://www.nuget.org/packages/NewLife.Audio)
[![locales](https://img.shields.io/badge/locales-31-2ea44f?logo=dotnet)](https://github.com/NewLifeX/NewLife.Audio/blob/master/Doc/多语言本地化.md)
<!-- metrics:end -->
![platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS%20%7C%20Android%20%7C%20iOS-blue)
![arch](https://img.shields.io/badge/arch-x64%20%7C%20x86%20%7C%20arm64%20%7C%20arm%20%7C%20loongarch64%20%7C%20riscv64%20%7C%20s390x-orange)
[![contributors](https://img.shields.io/github/contributors/NewLifeX/NewLife.Audio?logo=github)](https://github.com/NewLifeX/NewLife.Audio/graphs/contributors)

<!-- lang:start -->
🌐 [简体中文](https://github.com/NewLifeX/NewLife.Audio/blob/master/README.zh-CN.md) | [繁體中文](https://github.com/NewLifeX/NewLife.Audio/blob/master/README.zh-TW.md) | [Español](https://github.com/NewLifeX/NewLife.Audio/blob/master/README.es.md) | [Deutsch](https://github.com/NewLifeX/NewLife.Audio/blob/master/README.de.md) | [日本語](https://github.com/NewLifeX/NewLife.Audio/blob/master/README.ja.md) | [Français](https://github.com/NewLifeX/NewLife.Audio/blob/master/README.fr.md) | [Português](https://github.com/NewLifeX/NewLife.Audio/blob/master/README.pt.md) | [Русский](https://github.com/NewLifeX/NewLife.Audio/blob/master/README.ru.md) | [한국어](https://github.com/NewLifeX/NewLife.Audio/blob/master/README.ko.md) | [العربية](https://github.com/NewLifeX/NewLife.Audio/blob/master/README.ar.md)
<!-- lang:end -->

[简体中文](https://github.com/NewLifeX/NewLife.Audio/blob/master/README.zh-CN.md) | English

A cross-platform, pure-managed .NET audio processing library supporting codec, device I/O, DSP effects, speech processing, container format I/O, and streaming — all in one NuGet package.

🎧 Bundled [AudioStudio](https://github.com/NewLifeX/NewLife.Audio/blob/master/AudioStudio/) cross-platform desktop demo client (Avalonia) with six tool modules: playback, conversion, devices, recording, DSP, and diagnostics.

Source: https://github.com/NewLifeX/NewLife.Audio  
NuGet: `NewLife.Audio`

> 📖 Detailed documentation (Chinese): [Requirements](https://github.com/NewLifeX/NewLife.Audio/blob/master/Doc/需求文档.md) | [Feature module list](https://github.com/NewLifeX/NewLife.Audio/blob/master/Doc/功能模块清单.md) | [Client requirements](https://github.com/NewLifeX/NewLife.Audio/blob/master/Doc/需求文档-客户端.md) | [Client feature list](https://github.com/NewLifeX/NewLife.Audio/blob/master/Doc/功能模块清单-客户端.md) | [Competitive analysis](https://github.com/NewLifeX/NewLife.Audio/blob/master/Doc/竞品分析报告.md) | [AOT compatibility](https://github.com/NewLifeX/NewLife.Audio/blob/master/Doc/AOT兼容性分析.md) | [Localization](https://github.com/NewLifeX/NewLife.Audio/blob/master/Doc/多语言本地化.md) | [Module architecture designs](https://github.com/NewLifeX/NewLife.Audio/blob/master/Doc/)

## Features

### 🎵 Codec Engine (CODEC)
Bidirectional PCM conversion for 10+ audio codecs, all in pure managed C#:
- **G.711** μ-law / A-law
- **G.722** / **G.726** sub-band ADPCM
- **IMA ADPCM** (DVI4)
- **Opus** (pure managed CELT implementation)
- **MP3** (Huffman decoding + inverse quantization + IMDCT + subband synthesis)
- **AAC-LC** (ICS parsing + Huffman decoding + IMDCT + window overlap-add)
- **FLAC** (Fixed LPC + Rice residual coding + MD5)
- **Ogg Vorbis** (3 header packets encode/decode)
- **Speex** (pure managed narrowband CELP)
- Pluggable factory via `CodecRegistry` + `ICodecInfo` metadata
- IoT chip header adaptation (Hisilicon / Dahua / Uniview)

### 🔊 Audio Device I/O (DEVIO)
Cross-platform audio device abstraction:
- Device enumeration & discovery
- **WaveOut** / **WASAPI** / **ASIO** backends on Windows
- Audio capture & loopback recording
- Multi-device concurrent management via `AudioDeviceManager`

### 🎛️ DSP Pipeline (DSP)
Composable audio signal processing chain:
- Resampling (linear interpolation)
- Channel conversion (mono ↔ stereo)
- Bit depth / sample format conversion (8/16/32-bit ↔ float)
- Volume control & gain (linear / logarithmic)
- Multi-source mixing with normalization
- **FFT** spectrum analysis (Cooley-Tukey, Hann/Hamming/Blackman windows)
- **Equalizer** (10-band graphic + 3-band tone, BiQuad cascade)
- **BiQuad filter** (RBJ standard: LP/HP/BP/Notch/Peaking/LowShelf/HighShelf)
- Dynamic range compressor (soft-knee, RMS envelope)
- Fade in/out & crossfade (linear/log/cosine)
- Speed change (OLA) & pitch shift
- Chainable `IAudioProcessor` + `AudioPipeline`

### 🗣️ Speech Processing (VOP)
Real-time voice communication front-end:
- **VAD** — Voice Activity Detection (GMM model, 6 sub-bands + hangover)
- **AEC** — Acoustic Echo Cancellation (NLMS adaptive filter + double-talk detection + comfort noise)
- **AGC** — Automatic Gain Control (slow-attack fast-release, RMS envelope)
- **NS** — Noise Suppression (spectral subtraction + FFT/IFFT + OLA synthesis)
- Voice preprocessor pipeline: HPF → VAD → AGC

### 📦 Container Formats (CONT)
Audio file format read/write:
- **WAV** — Full RIFF parsing & writing
- **Ogg** — Page-level read/write (Opus/Vorbis)
- **MP4/M4A** — ISO BMFF: ftyp/moov/mdat box parsing & generation
- **FLAC** — Native fLaC metadata block parsing
- **Raw PCM** — Auto-detect or manual format specification

### 📡 Streaming & Transport (STRM)
Network audio streaming:
- **RTP** packetization & depacketization (RFC 3550) + session management
- **RTSP** client (TCP: OPTIONS→DESCRIBE→SETUP→PLAY→TEARDOWN, SDP parsing)
- **HTTP** audio streaming (Icecast/SHOUTcast)
- **WebSocket** bidirectional audio pipe (5-byte frame header)

## Installation

```bash
dotnet add package NewLife.Audio
```

## Quick Start

```csharp
using NewLife.Audio;
using NewLife.Audio.Codecs;

// Decode G.711 μ-law to PCM
var codec = new G711UCodec();
var pcm = codec.ToPcm(encodedData);

// Encode PCM to G.711 A-law
var g711a = new G711ACodec();
var encoded = g711a.FromPcm(pcmData);
```

## Ecosystem Map

NewLife is a layered ecosystem of independently versioned open-source projects. The map below is shared verbatim by every NewLife repository; each project links to its repository and its primary NuGet package.

| Project | What it does | Downloads |
| --- | --- | --- |
| **Foundation** | | |
| [NewLife.Core](https://github.com/NewLifeX/X) | Logging, configuration, caching, networking, serialization, scheduling and APM tracing. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Core?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Core) |
| [NewLife.XCode](https://github.com/NewLifeX/NewLife.XCode) | Data middleware: single tables at the tens-of-billions scale, automatic sharding, read-write splitting, 10+ databases. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCode?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCode) |
| [NewLife.Net](https://github.com/NewLifeX/NewLife.Net) | High-performance network library: 1.77M pps / 14.5 Gbps on a single machine (TCP 1KB, 2026-10), 4M long-lived TCP connections. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Net?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Net) |
| [NewLife.Remoting](https://github.com/NewLifeX/NewLife.Remoting) | Protocol communication and RPC framework for client/server applications and IoT devices. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Remoting?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Remoting) |
| [NewLife.Agent](https://github.com/NewLifeX/NewLife.Agent) | Installs .NET applications as OS daemons: Windows services and Linux systemd. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Agent?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Agent) |
| [NewLife.Templates](https://github.com/NewLifeX/NewLife.Zero) | `dotnet new` project templates for console, web, API, service, data and IoT projects. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Templates?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Templates) |
| **Data & Messaging** | | |
| [NewLife.Redis](https://github.com/NewLifeX/NewLife.Redis) | Redis client with microsecond latency, millions of ops/s and built-in message queues. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Redis?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Redis) |
| [NewLife.RocketMQ](https://github.com/NewLifeX/NewLife.RocketMQ) | Pure managed RocketMQ client for Apache RocketMQ and Alibaba Cloud Message Queue. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.RocketMQ?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.RocketMQ) |
| [NewLife.MQTT](https://github.com/NewLifeX/NewLife.MQTT) | MQTT client and server, including Alibaba Cloud IoT connectivity. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MQTT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MQTT) |
| [NewLife.NovaDb](https://github.com/NewLifeX/NewLife.NovaDb) | Hybrid database in C#: embedded or networked, relational, time-series, message queue and KV in one. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.NovaDb?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.NovaDb) |
| [NewLife.MySql](https://github.com/NewLifeX/NewLife.MySql) | MySql driver and helper layer built on the NewLife data stack. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MySql?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MySql) |
| **Web, AI & Distributed** | | |
| [NewLife.Cube](https://github.com/NewLifeX/NewLife.Cube) | Rapid admin-platform framework: permissions, SSO, OAuth server and CRUD scaffolding. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Cube?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Cube) |
| [Stardust](https://github.com/NewLifeX/Stardust) | Distributed service platform: node management, APM, configuration center, registry and release center. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Stardust?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Stardust) |
| [AntJob](https://github.com/NewLifeX/AntJob) | Distributed task and real-time data scheduling platform with sharding, retries and backfill. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AntJob?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AntJob) |
| [NewLife.AI](https://github.com/NewLifeX/NewLife.AI) | Unified client for 46 LLM providers plus ChatAI, a ready-to-run web chat application. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AI?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AI) |
| **Industrial & IoT** | | |
| [NewLife.IoT](https://github.com/NewLifeX/NewLife.IoT) | IoT standard library defining protocol contracts and device abstractions. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IoT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IoT) |
| [NewLife.Modbus](https://github.com/NewLifeX/NewLife.Modbus) | Modbus TCP/RTU/ASCII implementation on top of NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Modbus?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Modbus) |
| [NewLife.Siemens](https://github.com/NewLifeX/NewLife.Siemens) | Siemens PLC protocol on top of NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Siemens?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Siemens) |
| [NewLife.Omron](https://github.com/NewLifeX/NewLife.Omron) | Omron PLC protocol on top of NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Omron?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Omron) |
| [NewLife.Melsec](https://github.com/NewLifeX/NewLife.Melsec) | Mitsubishi Melsec PLC protocol on top of NewLife.IoT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Melsec?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Melsec) |
| [NewLife.OPC](https://github.com/NewLifeX/NewLife.OPC) | OPC client for industrial data access. | — |
| [NewLife.BACnet](https://github.com/NewLifeX/NewLife.BACnet) | BACnet building-automation protocol stack. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.BACnet?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.BACnet) |
| [ZeroIoT](https://github.com/NewLifeX/ZeroIoT) | Lightweight open-source IoT platform with gateway and device management. | — |
| **Vehicle Telematics** | | |
| [NewLife.JT808](https://github.com/NewLifeX/NewLife.JT808) | JT/T 808 vehicle positioning protocol: 70+ message bodies, 2011/2013/2019. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT808?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT808) |
| [NewLife.JT809](https://github.com/NewLifeX/NewLife.JT809) | JT/T 809 platform-to-platform protocol for transport authorities. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT809?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT809) |
| [NewLife.JT1078](https://github.com/NewLifeX/NewLife.JT1078) | JT/T 1078 in-vehicle video: RTP parsing, FLV/fMP4 remuxing and live streaming. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT1078?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT1078) |
| **Domain Components** | | |
| [NewLife.Office](https://github.com/NewLifeX/NewLife.Office) | One package for Excel, Word, PPT, PDF and 15+ document formats. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Office?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Office) |
| [NewLife.Audio](https://github.com/NewLifeX/NewLife.Audio) | Cross-platform audio: codecs, device I/O, DSP, containers and streaming. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Audio?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Audio) |
| [NewLife.Video](https://github.com/NewLifeX/NewLife.Video) | Video codecs, container formats, frame processing and streaming. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Video?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Video) |
| [NewLife.Map](https://github.com/NewLifeX/NewLife.Map) | Unified interface over Baidu, AMap, Tencent and Tianditu with geocoding and coordinate conversion. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Map?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Map) |
| [NewLife.IP](https://github.com/NewLifeX/NewLife.IP) | IPv4 geolocation with an offline memory-mapped database and an online API fallback. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IP?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IP) |
| **Tools & Products** | | |
| [CrazyCoder](https://github.com/NewLifeX/XCoder) | Developer toolbox: network, serial port, crypto, regex, Modbus and MQTT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCoder?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCoder) |
| [GitCandy](https://github.com/NewLifeX/GitCandy) | Self-hosted Git source code management system. | — |
| [LuckyClover](https://github.com/NewLifeX/LuckyClover) | Installs the matching .NET runtime automatically when launched from an older host. | — |

> Badges show the primary package of each project; theme, extension and satellite packages are listed on their NuGet pages.The 30+ packages not listed here (FTP, LoRa, Office extensions, protocol drivers and more) are available under the [NewLifeX organization](https://github.com/NewLifeX) and on [newlifex.com](https://newlifex.com).

## Team

<img src="https://newlifex.com/logo.png" width="48" align="right" alt="NewLife" />

Founded in 2002, the NewLife team has spent more than two decades on the .NET and IoT ecosystem, maintains nearly 100 open-source projects (34 of them core to this map), and delivers integrated software and hardware solutions for power, logistics, healthcare, industrial control and internet industries. We live by the motto "learning has no seniority — mastery earns respect".

Open-source components have been downloaded <!--nuget-total:start-->6.48M<!--nuget-total:end--> times from NuGet (ecosystem-wide, as of 2026-10).

Website: https://newlifex.com · Open source organization: https://github.com/NewLifeX

<details>
<summary>WeChat Official Account: 智能大石头 (scan to follow)</summary>

![智能大石头](https://newlifex.com/stone.jpg)

</details>
