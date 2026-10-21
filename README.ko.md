# NewLife.Audio - 오디오 코덱 라이브러리

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
🌐 [English](README.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [Español](README.es.md) | [Deutsch](README.de.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md) | [العربية](README.ar.md)
<!-- lang:end -->

[简体中文](README.zh-CN.md) | [English](README.md) | 한국어

코덱, 장치 I/O, DSP 효과, 음성 처리, 컨테이너 포맷 입출력, 스트리밍을 하나의 NuGet 패키지에서 모두 지원하는 크로스 플랫폼 순수 관리형 .NET 오디오 처리 라이브러리입니다.

🎧 [AudioStudio](AudioStudio/) 크로스 플랫폼 데스크톱 데모 클라이언트(Avalonia)가 포함되어 있으며, 재생, 변환, 장치, 녹음, DSP, 진단의 6가지 도구 모듈을 제공합니다.

소스: https://github.com/NewLifeX/NewLife.Audio  
NuGet: `NewLife.Audio`

> 📖 상세 문서(중국어): [요구사항 문서](Doc/需求文档.md) | [기능 모듈 목록](Doc/功能模块清单.md) | [클라이언트 요구사항](Doc/需求文档-客户端.md) | [클라이언트 기능 목록](Doc/功能模块清单-客户端.md) | [경쟁 분석](Doc/竞品分析报告.md) | [AOT 호환성](Doc/AOT兼容性分析.md) | [다국어 현지화](Doc/多语言本地化.md) | [모듈별 아키텍처 설계](Doc/)

## 기능

### 🎵 코덱 엔진(CODEC)
10종 이상의 오디오 코덱에 대한 양방향 PCM 변환을 모두 순수 관리형 C#으로 구현:
- **G.711** μ-law / A-law
- **G.722** / **G.726** 서브밴드 ADPCM
- **IMA ADPCM**(DVI4)
- **Opus**(순수 관리형 CELT 구현)
- **MP3**(허프만 디코딩 + 역양자화 + IMDCT + 서브밴드 합성)
- **AAC-LC**(ICS 파싱 + 허프만 디코딩 + IMDCT + 윈도 중첩 가산)
- **FLAC**(Fixed LPC + Rice 잔차 부호화 + MD5)
- **Ogg Vorbis**(3개 헤더 패킷 인코딩/디코딩)
- **Speex**(순수 관리형 협대역 CELP)
- `CodecRegistry` + `ICodecInfo` 메타데이터 기반의 플러그인 방식 팩토리
- IoT 칩 헤더 적응(Hisilicon / Dahua / Uniview)

### 🔊 오디오 장치 I/O(DEVIO)
크로스 플랫폼 오디오 장치 추상화:
- 장치 열거 및 검색
- Windows의 **WaveOut** / **WASAPI** / **ASIO** 백엔드
- 오디오 캡처 및 루프백 녹음
- `AudioDeviceManager`를 통한 다중 장치 동시 관리

### 🎛️ DSP 파이프라인(DSP)
조합 가능한 오디오 신호 처리 체인:
- 리샘플링(선형 보간)
- 채널 변환(모노 ↔ 스테레오)
- 비트 깊이 / 샘플 포맷 변환(8/16/32비트 ↔ float)
- 볼륨 제어 및 게인(선형 / 대수)
- 정규화를 포함한 다중 소스 믹싱
- **FFT** 스펙트럼 분석(Cooley-Tukey, Hann/Hamming/Blackman 윈도)
- **이퀄라이저**(10밴드 그래픽 + 3밴드 톤, BiQuad 캐스케이드)
- **BiQuad 필터**(RBJ 표준: LP/HP/BP/Notch/Peaking/LowShelf/HighShelf)
- 다이내믹 레인지 컴프레서(소프트 니, RMS 엔벨로프)
- 페이드 인/아웃 및 크로스페이드(선형/로그/코사인)
- 속도 변경(OLA) 및 피치 시프트
- 체이닝 가능한 `IAudioProcessor` + `AudioPipeline`

### 🗣️ 음성 처리(VOP)
실시간 음성 통신 프런트엔드:
- **VAD** — 음성 활동 검출(GMM 모델, 6개 서브밴드 + 행오버)
- **AEC** — 음향 에코 제거(NLMS 적응 필터 + 더블토크 검출 + 컴포트 노이즈)
- **AGC** — 자동 이득 제어(느린 어택·빠른 릴리즈, RMS 엔벨로프)
- **NS** — 잡음 억제(스펙트럼 차감 + FFT/IFFT + OLA 합성)
- 음성 전처리 파이프라인: HPF → VAD → AGC

### 📦 컨테이너 포맷(CONT)
오디오 파일 포맷 읽기/쓰기:
- **WAV** — RIFF 전체 파싱 및 쓰기
- **Ogg** — 페이지 단위 읽기/쓰기(Opus/Vorbis)
- **MP4/M4A** — ISO BMFF: ftyp/moov/mdat 박스 파싱 및 생성
- **FLAC** — 네이티브 fLaC 메타데이터 블록 파싱
- **Raw PCM** — 자동 감지 또는 수동 포맷 지정

### 📡 스트리밍 및 전송(STRM)
네트워크 오디오 스트리밍:
- **RTP** 패킷화 및 역패킷화(RFC 3550) + 세션 관리
- **RTSP** 클라이언트(TCP: OPTIONS→DESCRIBE→SETUP→PLAY→TEARDOWN, SDP 파싱)
- **HTTP** 오디오 스트리밍(Icecast/SHOUTcast)
- **WebSocket** 양방향 오디오 파이프(5바이트 프레임 헤더)

## 설치

```bash
dotnet add package NewLife.Audio
```

## 빠른 시작

```csharp
using NewLife.Audio;
using NewLife.Audio.Codecs;

// G.711 μ-law를 PCM으로 디코딩
var codec = new G711UCodec();
var pcm = codec.ToPcm(encodedData);

// PCM을 G.711 A-law로 인코딩
var g711a = new G711ACodec();
var encoded = g711a.FromPcm(pcmData);
```

## 생태계 지도

NewLife는 각 프로젝트가 독립적으로 버전 관리되고 배포되는 계층형 오픈 소스 생태계입니다. 아래 지도는 모든 NewLife 저장소에서 동일한 내용으로 사용되며, 각 프로젝트는 해당 저장소와 대표 NuGet 패키지로 연결됩니다.

| 프로젝트 | 기능 설명 | 다운로드 |
| --- | --- | --- |
| **기초 기반** | | |
| [NewLife.Core](https://github.com/NewLifeX/X) | 로깅, 구성, 캐싱, 네트워킹, 직렬화, 스케줄링 및 APM 추적. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Core?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Core) |
| [NewLife.XCode](https://github.com/NewLifeX/NewLife.XCode) | 데이터 미들웨어: 단일 테이블 수백억 건 규모, 자동 샤딩, 읽기/쓰기 분리, 10종 이상 데이터베이스 지원. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCode?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCode) |
| [NewLife.Net](https://github.com/NewLifeX/NewLife.Net) | 고성능 네트워크 라이브러리: 단일 머신에서 177만 pps / 14.5 Gbps(TCP 1KB, 2026-10), 400만 개 TCP 장기 연결. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Net?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Net) |
| [NewLife.Remoting](https://github.com/NewLifeX/NewLife.Remoting) | 클라이언트/서버 애플리케이션과 IoT 장치를 위한 프로토콜 통신 및 RPC 프레임워크. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Remoting?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Remoting) |
| [NewLife.Agent](https://github.com/NewLifeX/NewLife.Agent) | .NET 애플리케이션을 운영체제 데몬으로 설치: Windows 서비스 및 Linux systemd. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Agent?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Agent) |
| [NewLife.Templates](https://github.com/NewLifeX/NewLife.Zero) | 콘솔, 웹, API, 서비스, 데이터 및 IoT 프로젝트용 `dotnet new` 프로젝트 템플릿. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Templates?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Templates) |
| **데이터 및 메시징** | | |
| [NewLife.Redis](https://github.com/NewLifeX/NewLife.Redis) | 마이크로초 수준 지연, 초당 수백만 회 연산, 내장 메시지 큐를 갖춘 Redis 클라이언트. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Redis?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Redis) |
| [NewLife.RocketMQ](https://github.com/NewLifeX/NewLife.RocketMQ) | Apache RocketMQ와 Alibaba Cloud Message Queue를 위한 순수 관리형 RocketMQ 클라이언트. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.RocketMQ?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.RocketMQ) |
| [NewLife.MQTT](https://github.com/NewLifeX/NewLife.MQTT) | Alibaba Cloud IoT 연결을 포함한 MQTT 클라이언트 및 서버. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MQTT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MQTT) |
| [NewLife.NovaDb](https://github.com/NewLifeX/NewLife.NovaDb) | C# 기반 하이브리드 데이터베이스: 임베디드 또는 네트워크 모드, 관계형·시계열·메시지 큐·KV를 하나로 통합. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.NovaDb?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.NovaDb) |
| [NewLife.MySql](https://github.com/NewLifeX/NewLife.MySql) | NewLife 데이터 스택 위에 구축된 MySql 드라이버 및 도우미 계층. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MySql?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MySql) |
| **웹, AI 및 분산** | | |
| [NewLife.Cube](https://github.com/NewLifeX/NewLife.Cube) | 신속한 관리자 플랫폼 프레임워크: 권한, SSO, OAuth 서버 및 CRUD 스캐폴딩. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Cube?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Cube) |
| [Stardust](https://github.com/NewLifeX/Stardust) | 분산 서비스 플랫폼: 노드 관리, APM, 구성 센터, 레지스트리 및 릴리스 센터. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Stardust?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Stardust) |
| [AntJob](https://github.com/NewLifeX/AntJob) | 샤딩, 재시도, 백필을 지원하는 분산 작업 및 실시간 데이터 스케줄링 플랫폼. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AntJob?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AntJob) |
| [NewLife.AI](https://github.com/NewLifeX/NewLife.AI) | 46개 LLM 제공자를 위한 통합 클라이언트와 즉시 실행 가능한 웹 채팅 애플리케이션 ChatAI. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AI?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AI) |
| **산업 및 IoT** | | |
| [NewLife.IoT](https://github.com/NewLifeX/NewLife.IoT) | 프로토콜 계약과 장치 추상화를 정의하는 IoT 표준 라이브러리. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IoT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IoT) |
| [NewLife.Modbus](https://github.com/NewLifeX/NewLife.Modbus) | NewLife.IoT 기반의 Modbus TCP/RTU/ASCII 구현. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Modbus?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Modbus) |
| [NewLife.Siemens](https://github.com/NewLifeX/NewLife.Siemens) | NewLife.IoT 기반의 Siemens PLC 프로토콜. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Siemens?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Siemens) |
| [NewLife.Omron](https://github.com/NewLifeX/NewLife.Omron) | NewLife.IoT 기반의 Omron PLC 프로토콜. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Omron?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Omron) |
| [NewLife.Melsec](https://github.com/NewLifeX/NewLife.Melsec) | NewLife.IoT 기반의 Mitsubishi Melsec PLC 프로토콜. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Melsec?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Melsec) |
| [NewLife.OPC](https://github.com/NewLifeX/NewLife.OPC) | 산업 데이터 접근을 위한 OPC 클라이언트. | — |
| [NewLife.BACnet](https://github.com/NewLifeX/NewLife.BACnet) | BACnet 빌딩 자동화 프로토콜 스택. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.BACnet?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.BACnet) |
| [ZeroIoT](https://github.com/NewLifeX/ZeroIoT) | 게이트웨이와 장치 관리를 제공하는 경량 오픈 소스 IoT 플랫폼. | — |
| **차량 관제** | | |
| [NewLife.JT808](https://github.com/NewLifeX/NewLife.JT808) | JT/T 808 차량 위치 프로토콜: 70개 이상 메시지 바디, 2011/2013/2019 버전 지원. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT808?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT808) |
| [NewLife.JT809](https://github.com/NewLifeX/NewLife.JT809) | 운송 관리 부서를 위한 JT/T 809 플랫폼 간 연동 프로토콜. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT809?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT809) |
| [NewLife.JT1078](https://github.com/NewLifeX/NewLife.JT1078) | JT/T 1078 차량용 비디오: RTP 파싱, FLV/fMP4 리먹싱 및 라이브 스트리밍. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT1078?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT1078) |
| **도메인 컴포넌트** | | |
| [NewLife.Office](https://github.com/NewLifeX/NewLife.Office) | Excel, Word, PPT, PDF 및 15종 이상 문서 포맷을 하나의 패키지로 지원. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Office?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Office) |
| [NewLife.Audio](https://github.com/NewLifeX/NewLife.Audio) | 크로스 플랫폼 오디오: 코덱, 장치 I/O, DSP, 컨테이너 및 스트리밍. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Audio?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Audio) |
| [NewLife.Video](https://github.com/NewLifeX/NewLife.Video) | 비디오 코덱, 컨테이너 포맷, 프레임 처리 및 스트리밍. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Video?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Video) |
| [NewLife.Map](https://github.com/NewLifeX/NewLife.Map) | Baidu, AMap, Tencent, Tianditu 지도에 대한 통합 인터페이스와 지오코딩·좌표 변환 지원. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Map?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Map) |
| [NewLife.IP](https://github.com/NewLifeX/NewLife.IP) | 오프라인 메모리 매핑 데이터베이스와 온라인 API 폴백을 이용한 IPv4 위치 조회. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IP?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IP) |
| **도구 및 제품** | | |
| [CrazyCoder](https://github.com/NewLifeX/XCoder) | 개발자 도구 상자: 네트워크, 시리얼 포트, 암호화, 정규식, Modbus 및 MQTT. | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCoder?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCoder) |
| [GitCandy](https://github.com/NewLifeX/GitCandy) | 셀프 호스팅 Git 소스 코드 관리 시스템. | — |
| [LuckyClover](https://github.com/NewLifeX/LuckyClover) | 구버전 호스트에서 실행될 때 일치하는 .NET 런타임을 자동으로 설치. | — |

> 배지는 각 프로젝트의 대표 패키지를 표시하며, 테마·확장·위성 패키지는 해당 NuGet 페이지에서 확인할 수 있습니다. 여기에 나열되지 않은 30여 개 패키지(FTP, LoRa, Office 확장, 각종 프로토콜 드라이버 등)는 [NewLifeX 조직](https://github.com/NewLifeX)과 [newlifex.com](https://newlifex.com)에서 제공됩니다.

## 팀

<img src="https://newlifex.com/logo.png" width="48" align="right" alt="NewLife" />

2002년에 설립된 NewLife 팀은 20여 년간 .NET과 IoT 생태계에 전념해 왔으며, 약 100개의 오픈 소스 프로젝트(이 중 34개가 이 지도의 핵심 프로젝트)를 유지 관리하고, 전력, 물류, 의료, 산업 제어, 인터넷 산업에 소프트웨어와 하드웨어를 통합한 솔루션을 제공합니다. 우리는 "배움에 선후는 없으며, 뛰어난 사람이 스승이다"라는 신조를 따릅니다.

오픈 소스 구성 요소는 NuGet에서 <!--nuget-total:start-->6.48M<!--nuget-total:end-->회 다운로드되었습니다(전체 생태계 기준, 2026-10 기준).

웹사이트: https://newlifex.com · 오픈 소스 조직: https://github.com/NewLifeX

<details>
<summary>위챗 공식 계정: 智能大石头(스캔하여 팔로우)</summary>

![智能大石头](https://newlifex.com/stone.jpg)

</details>

## 라이선스

MIT License © 2002-2026 NewLife 팀

[GitHub](https://github.com/NewLifeX/NewLife.Audio) | [웹사이트](https://newlifex.com)
