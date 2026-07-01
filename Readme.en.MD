# NewLife.Audio - Audio Codec Library

![GitHub top language](https://img.shields.io/github/languages/top/newlifex/NewLife.Audio?logo=github)
![GitHub License](https://img.shields.io/github/license/newlifex/NewLife.Audio?logo=github)
![Nuget Downloads](https://img.shields.io/nuget/dt/NewLife.Audio?logo=nuget)
![Nuget](https://img.shields.io/nuget/v/NewLife.Audio?logo=nuget)
![Nuget (with prereleases)](https://img.shields.io/nuget/vpre/NewLife.Audio?label=dev%20nuget&logo=nuget)

[简体中文](Readme.MD) | English

A cross-platform, pure-managed .NET audio processing library supporting codec, device I/O, DSP effects, speech processing, container format I/O, and streaming — all in one NuGet package.

Source: https://github.com/NewLifeX/NewLife.Audio  
NuGet: `NewLife.Audio`

> 📖 Detailed design docs are available in the [Doc/](Doc/) directory (Chinese). See [功能模块清单](Doc/功能模块清单.md) for feature status.

## Features

### 🎵 Codec Engine (M1)
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

### 🔊 Audio Device I/O (M2)
Cross-platform audio device abstraction:
- Device enumeration & discovery
- **WaveOut** / **WASAPI** / **ASIO** backends on Windows
- Audio capture & loopback recording
- Multi-device concurrent management via `AudioDeviceManager`

### 🎛️ DSP Pipeline (M3)
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

### 🗣️ Speech Processing (M4)
Real-time voice communication front-end:
- **VAD** — Voice Activity Detection (GMM model, 6 sub-bands + hangover)
- **AEC** — Acoustic Echo Cancellation (NLMS adaptive filter + double-talk detection + comfort noise)
- **AGC** — Automatic Gain Control (slow-attack fast-release, RMS envelope)
- **NS** — Noise Suppression (spectral subtraction + FFT/IFFT + OLA synthesis)
- Voice preprocessor pipeline: HPF → VAD → AGC

### 📦 Container Formats (M5)
Audio file format read/write:
- **WAV** — Full RIFF parsing & writing
- **Ogg** — Page-level read/write (Opus/Vorbis)
- **MP4/M4A** — ISO BMFF: ftyp/moov/mdat box parsing & generation
- **FLAC** — Native fLaC metadata block parsing
- **Raw PCM** — Auto-detect or manual format specification

### 📡 Streaming & Transport (M6)
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

## NewLife Project Matrix

All projects target `net9.0` / `netstandard2.1` / `netstandard2.0` / `net462` / `net45` by default. Legacy builds (2024.0801) support `net40` / `net20`.

|                                   Project                                   | Year  | Description                                                                                       |
| :-------------------------------------------------------------------------: | :---: | ------------------------------------------------------------------------------------------------- |
|                               **Foundation**                                |       | Core components powering middleware and products                                                  |
|           [NewLife.Core](https://github.com/NewLifeX/X)                     | 2002  | Core library: logging, config, cache, networking, serialization, APM tracing                      |
|     [NewLife.XCode](https://github.com/NewLifeX/NewLife.XCode)              | 2005  | Big-data ORM, 10B+ rows per table, MySQL/SQLite/SQL Server/Oracle/PostgreSQL/DaMeng, auto-sharding |
|       [NewLife.Net](https://github.com/NewLifeX/NewLife.Net)                | 2005  | Networking library, 22.66M tps throughput, 4M concurrent TCP connections                          |
|  [NewLife.Remoting](https://github.com/NewLifeX/NewLife.Remoting)           | 2011  | RPC communication framework, Http/RPC, high throughput, IoT-friendly                              |
|      [NewLife.Cube](https://github.com/NewLifeX/NewLife.Cube)               | 2010  | Rapid admin platform: user management, SSO, OAuth, proven at 10B+ row scale                       |
|     [NewLife.Agent](https://github.com/NewLifeX/NewLife.Agent)              | 2008  | Service manager: install apps as OS daemons (Windows Service / Linux systemd)                     |
|      [NewLife.Zero](https://github.com/NewLifeX/NewLife.Zero)               | 2020  | Zero scaffolding: project templates for Web, WebApi, Service                                      |
|                               **Middleware**                                |       | Integrations with major middleware platforms                                                      |
|     [NewLife.Redis](https://github.com/NewLifeX/NewLife.Redis)              | 2017  | Redis client, microsecond latency, million-level throughput, rich message queues                  |
|  [NewLife.RocketMQ](https://github.com/NewLifeX/NewLife.RocketMQ)           | 2018  | Pure managed RocketMQ client, Apache + Alibaba Cloud, proven at 1B+ scale                         |
|      [NewLife.MQTT](https://github.com/NewLifeX/NewLife.MQTT)               | 2019  | IoT messaging: MqttClient / MqttServer, Alibaba Cloud IoT support                                 |
|       [NewLife.IoT](https://github.com/NewLifeX/NewLife.IoT)                | 2022  | IoT standard library: protocol specs for the IoT domain                                           |
|    [NewLife.Modbus](https://github.com/NewLifeX/NewLife.Modbus)             | 2022  | Modbus TCP/RTU/ASCII, built on IoT standard library                                               |
|   [NewLife.Siemens](https://github.com/NewLifeX/NewLife.Siemens)            | 2022  | Siemens PLC protocols, built on IoT standard library                                              |
|       [NewLife.Map](https://github.com/NewLifeX/NewLife.Map)                | 2022  | Map component library: Baidu, Amap, Tencent, Tianditu                                              |
|     [NewLife.Audio](https://github.com/NewLifeX/NewLife.Audio)              | 2023  | Audio codec library: G.711/G.722/G.726/ADPCM/Opus/MP3/AAC/FLAC/Vorbis/Speex, DSP/speech/streaming |
|                               **Platforms**                                 |       | Product platforms, deploy & run out of the box                                                    |
|         [Stardust](https://github.com/NewLifeX/Stardust)                    | 2018  | Distributed service platform: node management, APM, config/registry/release center                |
|           [AntJob](https://github.com/NewLifeX/AntJob)                      | 2019  | Distributed big-data computing platform (real-time/batch), proven at trillion-row scale            |
|       [NewLife.ERP](https://github.com/NewLifeX/NewLife.ERP)                | 2021  | Enterprise ERP: product, customer, sales, supplier management                                     |
|         [CrazyCoder](https://github.com/NewLifeX/XCoder)                    | 2006  | Developer toolset: network, serial, crypto, regex, Modbus, MQTT                                   |
|           [EasyIO](https://github.com/NewLifeX/EasyIO)                      | 2023  | Simple file storage for distributed systems                                                        |
|           [XProxy](https://github.com/NewLifeX/XProxy)                      | 2005  | Production reverse proxy: NAT, HTTP proxy                                                         |
|        [HttpMeter](https://github.com/NewLifeX/HttpMeter)                   | 2022  | HTTP stress testing tool                                                                          |
|         [GitCandy](https://github.com/NewLifeX/GitCandy)                    | 2015  | Git source code management system                                                                 |
|          [SmartOS](https://github.com/NewLifeX/SmartOS)                     | 2014  | Embedded OS, fully independent, ARM Cortex-M                                                      |
|          [SmartA2](https://github.com/NewLifeX/SmartA2)                     | 2019  | Embedded industrial computer, IoT edge gateway, high-performance .NET 8 host                      |
|                        **FIoT IoT Platform**                                | 2020  | End-to-end IoT solution: construction, environmental, agriculture, proven at 100K+ device scale   |
|                   **UWB High-Precision Indoor Positioning**                  | 2020  | Centimeter-level (10~20cm) indoor positioning, hardware + software integration                    |

## NewLife Development Team

![NewLife](https://newlifex.com/logo.png)

The NewLife team, founded in 2002, is a next-generation IoT solution provider specializing in hardware/software consulting, system architecture, and development services.

Over 80 open-source projects led by the team are widely adopted across industries, with 4M+ total NuGet downloads. Core products — NewLife.XCode big-data ORM, AntJob distributed computing platform, Stardust distributed service platform, NewLife.Redis caching & queue, and the FIoT IoT platform — have been successfully deployed in power utilities, education, internet, telecom, transportation, logistics, industrial control, healthcare, and cultural heritage sectors.

Website: https://newlifex.com  
Open Source: https://github.com/newlifex  
QQ Group: 1600800 / 1600838  
WeChat Official Account:  
![Smart Stone](https://newlifex.com/stone.jpg)
