# NewLife.Audio - Audio Codec Library

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
🌐 [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [Español](README.es.md) | [Deutsch](README.de.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md) | [한국어](README.ko.md) | [العربية](README.ar.md)
<!-- lang:end -->

[简体中文](README.zh-CN.md) | English

A cross-platform, pure-managed .NET audio processing library supporting codecs, device I/O, DSP effects, speech processing, text-to-speech, speech recognition, audio file read/edit/write, container format I/O, streaming, and MIDI sequencing/rendering — all in one NuGet package.

🎧 Bundled [AudioStudio](AudioStudio/) cross-platform desktop demo client (Avalonia) with eight tool modules: playback, MIDI sequencing, conversion, devices, recording, DSP, diagnostics, and speech synthesis.

Source: https://github.com/NewLifeX/NewLife.Audio  
NuGet: `NewLife.Audio`

> 📖 Detailed documentation (Chinese): [Requirements](Doc/需求文档.md) | [Feature module list](Doc/模块清单.md) | [Architecture](Doc/架构设计.md) | [Client requirements](Doc/需求文档-客户端.md) | [Client feature list](Doc/功能模块清单-客户端.md) | [Competitive analysis](Doc/竞品分析报告.md) | [AOT compatibility](Doc/AOT兼容性分析.md) | [Localization](Doc/多语言本地化.md) | [Module architecture designs](Doc/)

## Features

### 🎵 Codec Engine (CODEC)
Bidirectional PCM conversion for 10+ audio codecs, all in pure managed C#:
- **G.711** μ-law / A-law
- **G.722** / **G.726** sub-band ADPCM
- **IMA ADPCM** (DVI4)
- **Opus** (self-hosted pure-managed implementation in progress: RFC 6716 packet parser + range coder, with ffmpeg fallback)
- **MP3** (decode: Huffman + inverse quantization + IMDCT + subband synthesis, bit reservoir, bad-frame concealment and CRC validation; encode: polyphase analysis + MDCT + quantization + Huffman, with joint stereo M/S, CBR/VBR/ABR + Xing info frame, a simplified psychoacoustic model, cross-frame bit reservoir and optional CRC; short blocks not yet implemented)
- **AAC** (pure-managed AAC-LC/HE-AAC decoding; AAC-LC encoding; ADTS/raw/MP4/LOAS/LATM)
- **FLAC** (Fixed LPC + Rice residual coding + MD5)
- **Ogg Vorbis** (pure-managed decode: floor 0/1, residue 0/1/2, channel coupling, MDCT; baseline long-block encoder)
- **Speex** (pure managed narrowband, 20 ms frames + 4-bit adaptive differential coding; private bitstream, not libspeex-compatible)
- Pluggable factory via `CodecRegistry` + `ICodecInfo` metadata
- IoT chip header adaptation (Hisilicon / Dahua / Uniview)

### 🔊 Audio Device I/O (DEVIO)
Cross-platform audio device abstraction:
- Device enumeration & discovery
- **WaveOut** / **WASAPI** backends on Windows (ASIO planned)
- Audio capture & loopback recording
- Multi-device concurrent management via `AudioDeviceManager`

> ⚠️ On Windows, both WinMM (WaveOut/WaveIn) and WASAPI (shared-mode playback/capture, loopback, event-driven low-latency streams, device-loss recovery, hot-plug watcher) are fully implemented. ASIO and the Linux/macOS/Android/iOS backends are not; their status and roadmap are documented in the [architecture design](Doc/架构设计.md).

### 🎛️ DSP Pipeline (DSP)
Composable audio signal processing chain:
- Resampling (linear + windowed-sinc)
- Channel conversion (mono ↔ stereo ↔ multichannel)
- Bit depth / sample format conversion (8/16/24/32-bit ↔ float/double)
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
- `SpeechProcessingPipeline`: 10 ms frame alignment, reference delay compensation, AEC → NS → AGC with ERLE/double-talk/convergence metrics

### 📦 Container Formats (CONT)
Audio file format read/write:
- **WAV** — Full RIFF parsing & writing (EXTENSIBLE/float/24-bit/RF64/LIST-INFO)
- **Ogg** — Page-level read/write (Opus/Vorbis)
- **MP3** — Frame index, ID3v2.2/v2.3/v2.4 + v1 (unsync, compressed frames, in-place tag rewrite), Xing/Info/VBRI, gapless
- **MP4/M4A** — ISO BMFF: ftyp/moov/mdat box parsing & generation, iTunes tags
- **FLAC** — Native fLaC metadata read/write (STREAMINFO/SEEKTABLE/VORBIS_COMMENT)
- **AIFF/CAF** — AIFF read/write; CAF read
- **Probe** — Magic-byte detection with container/codec/duration/bitrate/tags
- **Raw PCM** — Explicit format required; no heuristic auto-detection

### 🗂️ Audio File Facade (FILE)
One call chain for open → decode → edit → save:
- `AudioFile.Open` auto-detects container/codec and exposes format, duration, tags and seekability
- Lossless PCM editing: trim, concat, gain, normalize, resample
- Metadata preservation, tag-only atomic rewrite, atomic save via temp file
- Frame-level copy/remux for MP3/AAC/WAV/Ogg Opus when parameters match; explicit re-encode fallback
- Batch conversion with progress callbacks and cancellation
- `StreamPcm` streams decoded 16-bit PCM chunks: frame-by-frame for WAV/AIFF/CAF/M4A, whole-file decode then chunking for MP3/FLAC/Ogg (memory scales with `chunkBytes`, not file size, for the streaming path)

### 📡 Streaming & Transport (STRM)
Network audio streaming:
- **RTP** packetization & depacketization (RFC 3550) + session management
- **RTSP** client (TCP: OPTIONS→DESCRIBE→SETUP→PLAY→TEARDOWN, SDP parsing)
- **HTTP** audio streaming (Icecast/SHOUTcast)
- **WebSocket** bidirectional audio pipe (5-byte frame header)

### 🎼 MIDI Sequencing (MIDI)
Pure-managed Standard MIDI File read/write and waveform rendering:
- Parse/write SMF 1.0 (Format 0/1/2, PPQ/SMPTE, VLQ, running status, Meta/SysEx passthrough); Format 2 sequences render independently
- Pure-managed synthesizer: GM program groups, band-limited wavetables, ADSR, velocity/volume/expression/pan/pitch bend/sustain, polyphony limit, soft limiting
- Optional SoundFont 2 (`.sf2`) and DLS Level 1/2 (`.dls`) sampling via `MidiRenderOptions.SoundBank`
- MPE continuous pitch bend/channel pressure and MIDI 2.0 UMP packet/SysEx7 parsing
- Windows real-time MIDI output (WinMM `midiOut`/`midiIn`, device enumeration, tempo-based `MidiOutputSequencer`)
- Render to 16-bit PCM and reuse `AudioFile.WritePcm` for WAV/MP3/FLAC
- AudioStudio **MIDI Sequencing** page: gradient min/max waveform, RMS line, beat grid, glowing playhead, read-only mini piano roll, sound-bank loading, MIDI output panel
- Built-in public-domain demos (`Ode to Joy`, `Greensleeves`, `Canon in D`, `Jasmine Flower`) under `AudioStudio/Assets/Midi`
- Console demo `Samples/MidiDemo`: `info` / `render` / `play` / `gen`, with optional `.sf2`/`.dls` source
- MIDI is a score/event stream, not sampled audio; USB-MIDI/BLE-MIDI and native MIDI 2.0 transport are out of scope

### 🔈 Text-to-Speech (TTS)
Turn text into sound with three lines of code:
- **Speak directly** — `await new TtsClient().SayAsync("text")` via Windows SAPI / macOS `say` / Linux `espeak-ng` (on Windows .NET Core/.NET 5+, reference the `System.Speech` package)
- **Save to file** — WAV / raw PCM / MP3 / FLAC, chosen by file extension (compressed formats go through the resample → encode → container pipeline)
- **Stream out** — PCM chunks for HTTP/WebSocket generate-as-you-send
- **Pluggable engines** — system speech, `HttpTtsEngine`, `ProcessTtsEngine`, custom `ITtsEngine`
- **Cloud providers** — `AliyunTtsEngine` / `TencentTtsEngine` / `BaiduTtsEngine` / `AzureTtsEngine`, plus key-free `BaiduTranslateTtsEngine`
- **Offline neural voices** — optional `NewLife.Audio.Sherpa` package (sherpa-onnx: VITS / Matcha / Kokoro, streaming)
- **Optional advanced** — voice/rate plus a voice catalog (`TtsVoiceCatalog`: pick voices across engines by language and scenario), disk cache, pre-generated phrases, text normalization (`NewLife.Audio.Tts.Advanced`)

### 🎙️ Speech Recognition (ASR)
Low-priority adapter layer — no bundled model or inference runtime:
- `ISpeechRecognizer` streaming PCM input with partial/final results, timestamps and endpoint detection
- Windows system recognition via `System.Speech.Recognition`
- Optional `NewLife.Audio.Sherpa` package for offline sherpa-onnx recognition
- Android/iOS system adapters planned with platform TFMs

## Installation

```bash
dotnet add package NewLife.Audio
```

## Quick Start

```csharp
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Audio.Tts;

// Decode G.711 μ-law to PCM
var codec = new G711UCodec();
var pcm = codec.ToPcm(encodedData);

// Encode PCM to G.711 A-law
var g711a = new G711ACodec();
var encoded = g711a.FromPcm(pcmData);

// Text-to-speech: speak and save to WAV
var tts = new TtsClient();
await tts.SayAsync("Hello, world");
await tts.SaveAsync("Hello, world", "hello.wav");
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

## License

MIT License © 2002-2026 NewLife Team

Third-party components (SharpJaad.AAC, EggEncoder MDCT, minimp3 tables) are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

[GitHub](https://github.com/NewLifeX/NewLife.Audio) | [Website](https://newlifex.com)
