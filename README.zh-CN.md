# NewLife.Audio - 音频编解码库

![GitHub top language](https://img.shields.io/github/languages/top/newlifex/NewLife.Audio?logo=github)


![GitHub License](https://img.shields.io/github/license/NewLifeX/NewLife.Audio?logo=github)
![NuGet Downloads](https://img.shields.io/nuget/dt/NewLife.Audio?logo=nuget)
![NuGet](https://img.shields.io/nuget/v/NewLife.Audio?logo=nuget)
![NuGet prerelease](https://img.shields.io/nuget/vpre/NewLife.Audio?label=dev%20nuget&logo=nuget)
[![test](https://github.com/NewLifeX/NewLife.Audio/actions/workflows/test.yml/badge.svg?branch=master)](https://github.com/NewLifeX/NewLife.Audio/actions/workflows/test.yml)
[![NativeAOT](https://github.com/NewLifeX/NewLife.Audio/actions/workflows/aot.yml/badge.svg?branch=master)](https://github.com/NewLifeX/NewLife.Audio/actions/workflows/aot.yml)
<!-- metrics:start -->
[![AOT smoke](https://img.shields.io/badge/AOT%20smoke-19%2F19-brightgreen?logo=dotnet)](Doc/AOT兼容性分析.md)
[![TFM](https://img.shields.io/badge/TFM-3%20%28net461%20%E2%86%92%20netstandard2.1%29-512BD4?logo=dotnet)](https://www.nuget.org/packages/NewLife.Audio)
[![locales](https://img.shields.io/badge/locales-31-2ea44f?logo=dotnet)](Doc/多语言本地化.md)
<!-- metrics:end -->
![platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS%20%7C%20Android%20%7C%20iOS-blue)
![arch](https://img.shields.io/badge/arch-x64%20%7C%20x86%20%7C%20arm64%20%7C%20arm%20%7C%20loongarch64%20%7C%20riscv64%20%7C%20s390x-orange)
[![contributors](https://img.shields.io/github/contributors/NewLifeX/NewLife.Audio?logo=github)](https://github.com/NewLifeX/NewLife.Audio/graphs/contributors)

<!-- lang:start -->
🌐 [English](README.md) | [繁體中文](README.zh-TW.md) | [Español](README.es.md) | [Deutsch](README.de.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md) | [한국어](README.ko.md) | [العربية](README.ar.md)
<!-- lang:end -->

[English](README.md) | 简体中文

跨平台 .NET 全功能音频处理库，纯托管优先，提供编解码（G.711/ADPCM/AAC/Opus/MP3/FLAC/Vorbis/Speex/G.722/G.726）、设备 I/O（WaveOut/WASAPI/ASIO）、DSP 信号链处理、语音处理（VAD/AEC/AGC/NS）、容器格式读写（WAV/Ogg/MP4/FLAC）、流媒体传输（RTP/RTSP/HTTP/WebSocket）一站式音频能力。

🎧 附带 [AudioStudio](AudioStudio/) 跨平台桌面演示客户端（Avalonia），提供播放、转换、设备、录制、DSP、诊断六大工具模块。

源码： https://github.com/NewLifeX/NewLife.Audio  
Nuget：NewLife.Audio  

📖 详细文档：[需求文档](Doc/需求文档.md) | [功能模块清单](Doc/功能模块清单.md) | [客户端需求文档](Doc/需求文档-客户端.md) | [客户端功能清单](Doc/功能模块清单-客户端.md) | [竞品分析](Doc/竞品分析报告.md) | [AOT 兼容性](Doc/AOT兼容性分析.md) | [多语言本地化](Doc/多语言本地化.md) | [各模块架构设计](Doc/)

> ℹ️ 本中文版与 [English README](README.md) 同批维护；若两者内容不一致，以英文版为对外口径。

## 功能特性

### 🎵 编解码引擎（CODEC）
纯托管 C# 实现 10+ 音频编解码器的 PCM 双向转换：
- **G.711** μ-law / A-law
- **G.722** / **G.726** 子带 ADPCM
- **IMA ADPCM**（DVI4）
- **Opus**（纯托管 CELT 实现）
- **MP3**（Huffman 解码 + 反量化 + IMDCT + 子带合成）
- **AAC-LC**（ICS 解析 + Huffman 解码 + IMDCT + 窗函数重叠相加）
- **FLAC**（Fixed LPC + Rice 残差编码 + MD5）
- **Ogg Vorbis**（3 个头部包编码/解码）
- **Speex**（纯托管窄带 CELP）
- 基于 `CodecRegistry` + `ICodecInfo` 元数据的可插拔工厂
- IoT 芯片头适配（海思 / 大华 / 宇视）

### 🔊 音频设备 I/O（DEVIO）
跨平台音频设备抽象：
- 设备枚举与发现
- Windows 下的 **WaveOut** / **WASAPI** / **ASIO** 后端
- 音频采集与环回录制
- 通过 `AudioDeviceManager` 进行多设备并发管理

### 🎛️ DSP 处理链（DSP）
可组合的音频信号处理链：
- 重采样（线性插值）
- 声道转换（单声道 ↔ 立体声）
- 位深 / 采样格式转换（8/16/32 位 ↔ 浮点）
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

### 📦 容器格式（CONT）
音频文件格式读写：
- **WAV** — 完整的 RIFF 解析与写入
- **Ogg** — 页级读写（Opus/Vorbis）
- **MP4/M4A** — ISO BMFF：ftyp/moov/mdat box 解析与生成
- **FLAC** — 原生 fLaC 元数据块解析
- **Raw PCM** — 自动探测或手动指定格式

### 📡 流媒体与传输（STRM）
网络音频流传输：
- **RTP** 打包与解包（RFC 3550）+ 会话管理
- **RTSP** 客户端（TCP：OPTIONS→DESCRIBE→SETUP→PLAY→TEARDOWN，SDP 解析）
- **HTTP** 音频流（Icecast/SHOUTcast）
- **WebSocket** 双向音频管道（5 字节帧头）

## 安装

```bash
dotnet add package NewLife.Audio
```

## 快速开始

```csharp
using NewLife.Audio;
using NewLife.Audio.Codecs;

// 将 G.711 μ-law 解码为 PCM
var codec = new G711UCodec();
var pcm = codec.ToPcm(encodedData);

// 将 PCM 编码为 G.711 A-law
var g711a = new G711ACodec();
var encoded = g711a.FromPcm(pcmData);
```

## 新生命项目矩阵
各项目默认支持net9.0/netstandard2.1/netstandard2.0/net462/net45，旧版（2024.0801）支持net4.0/net2.0  

|                               项目                               | 年份  | 说明                                                                                        |
| :--------------------------------------------------------------: | :---: | ------------------------------------------------------------------------------------------- |
|                             基础组件                             |       | 支撑其它中间件以及产品项目                                                                  |
|          [NewLife.Core](https://github.com/NewLifeX/X)           | 2002  | 核心库，日志、配置、缓存、网络、序列化、APM性能追踪                                         |
|    [NewLife.XCode](https://github.com/NewLifeX/NewLife.XCode)    | 2005  | 大数据中间件，单表百亿级，MySql/SQLite/SqlServer/Oracle/PostgreSql/达梦，自动分表，读写分离 |
|      [NewLife.Net](https://github.com/NewLifeX/NewLife.Net)      | 2005  | 网络库，单机千万级吞吐率（2266万tps），单机百万级连接（400万Tcp长连接）                     |
| [NewLife.Remoting](https://github.com/NewLifeX/NewLife.Remoting) | 2011  | 协议通信库，提供CS应用通信框架，支持Http/RPC通信框架，高吞吐，物联网设备低开销易接入        |
|     [NewLife.Cube](https://github.com/NewLifeX/NewLife.Cube)     | 2010  | 魔方快速开发平台，集成了用户权限、SSO登录、OAuth服务端等，单表100亿级项目验证               |
|    [NewLife.Agent](https://github.com/NewLifeX/NewLife.Agent)    | 2008  | 服务管理组件，把应用安装成为操作系统守护进程，Windows服务、Linux的Systemd                   |
|     [NewLife.Zero](https://github.com/NewLifeX/NewLife.Zero)     | 2020  | Zero零代脚手架，基于NewLife组件生态的项目模板NewLife.Templates，Web、WebApi、Service        |
|                              中间件                              |       | 对接知名中间件平台                                                                          |
|    [NewLife.Redis](https://github.com/NewLifeX/NewLife.Redis)    | 2017  | Redis客户端，微秒级延迟，百万级吞吐，丰富的消息队列，百亿级数据量项目验证                   |
| [NewLife.RocketMQ](https://github.com/NewLifeX/NewLife.RocketMQ) | 2018  | RocketMQ纯托管客户端，支持Apache RocketMQ和阿里云消息队列，十亿级项目验                     |
|     [NewLife.MQTT](https://github.com/NewLifeX/NewLife.MQTT)     | 2019  | 物联网消息协议，MqttClient/MqttServer，客户端支持阿里云物联网                               |
|      [NewLife.IoT](https://github.com/NewLifeX/NewLife.IoT)      | 2022  | IoT标准库，定义物联网领域的各种通信协议标准规范                                             |
|   [NewLife.Modbus](https://github.com/NewLifeX/NewLife.Modbus)   | 2022  | ModbusTcp/ModbusRTU/ModbusASCII，基于IoT标准库实现，支持ZeroIoT平台和IoTEdge网关            |
|  [NewLife.Siemens](https://github.com/NewLifeX/NewLife.Siemens)  | 2022  | 西门子PLC协议，基于IoT标准库实现，支持IoT平台和IoTEdge                                      |
|      [NewLife.Map](https://github.com/NewLifeX/NewLife.Map)      | 2022  | 地图组件库，封装百度地图、高德地图、腾讯地图、天地图                                        |
|    [NewLife.Audio](https://github.com/NewLifeX/NewLife.Audio)    | 2023  | 音频编解码库，G.711/G.722/G.726/ADPCM/Opus/MP3/AAC/FLAC/Vorbis/Speex，DSP/语音处理/流媒体     |
|                             产品平台                             |       | 产品平台级，编译部署即用，个性化自定义                                                      |
|         [Stardust](https://github.com/NewLifeX/Stardust)         | 2018  | 星尘，分布式服务平台，节点管理、APM监控中心、配置中心、注册中心、发布中心                   |
|           [AntJob](https://github.com/NewLifeX/AntJob)           | 2019  | 蚂蚁调度，分布式大数据计算平台（实时/离线），蚂蚁搬家分片思想，万亿级数据量项目验证         |
|      [NewLife.ERP](https://github.com/NewLifeX/NewLife.ERP)      | 2021  | 企业ERP，产品管理、客户管理、销售管理、供应商管理                                           |
|         [CrazyCoder](https://github.com/NewLifeX/XCoder)         | 2006  | 码神工具，众多开发者工具，网络、串口、加解密、正则表达式、Modbus、MQTT                      |
|           [EasyIO](https://github.com/NewLifeX/EasyIO)           | 2023  | 简易文件存储，支持分布式系统中文件集中存储。                                                |
|           [XProxy](https://github.com/NewLifeX/XProxy)           | 2005  | 产品级反向代理，NAT代理、Http代理                                                           |
|        [HttpMeter](https://github.com/NewLifeX/HttpMeter)        | 2022  | Http压力测试工具                                                                            |
|         [GitCandy](https://github.com/NewLifeX/GitCandy)         | 2015  | Git源代码管理系统                                                                           |
|          [SmartOS](https://github.com/NewLifeX/SmartOS)          | 2014  | 嵌入式操作系统，完全独立自主，支持ARM Cortex-M芯片架构                                      |
|          [SmartA2](https://github.com/NewLifeX/SmartA2)          | 2019  | 嵌入式工业计算机，物联网边缘网关，高性能.NET8主机，应用于工业、农业、交通、医疗             |
|                          FIoT物联网平台                          | 2020  | 物联网整体解决方案，建筑、环保、农业，软硬件及大数据分析一体化，单机十万级点位项目验证      |
|                        UWB高精度室内定位                         | 2020  | 厘米级（10~20cm）高精度室内定位，软硬件一体化，与其它系统联动，大型展厅项目验证             |

## 新生命开发团队
![XCode](https://newlifex.com/logo.png)  

新生命团队（NewLife）成立于2002年，是新时代物联网行业解决方案提供者，致力于提供软硬件应用方案咨询、系统架构规划与开发服务。  
团队主导的80多个开源项目已被广泛应用于各行业，Nuget累计下载量高达400余万次。  
团队开发的大数据中间件NewLife.XCode、蚂蚁调度计算平台AntJob、星尘分布式平台Stardust、缓存队列组件NewLife.Redis以及物联网平台FIoT，均成功应用于电力、高校、互联网、电信、交通、物流、工控、医疗、文博等行业，为客户提供了大量先进、可靠、安全、高质量、易扩展的产品和系统集成服务。  

我们将不断通过服务的持续改进，成为客户长期信赖的合作伙伴，通过不断的创新和发展，成为国内优秀的IoT服务供应商。  

`新生命团队始于2002年，部分开源项目具有20年以上漫长历史，源码库保留有2010年以来所有修改记录`  
网站：https://newlifex.com  
开源：https://github.com/newlifex  
QQ群：1600800/1600838  
微信公众号：  
![智能大石头](https://newlifex.com/stone.jpg)  
