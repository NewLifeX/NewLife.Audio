# NewLife.Audio - オーディオコーデックライブラリ

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
🌐 [English](README.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [Español](README.es.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md) | [한국어](README.ko.md) | [العربية](README.ar.md)
<!-- lang:end -->

[简体中文](README.zh-CN.md) | [English](README.md)

コーデック、デバイス I/O、DSP エフェクト、音声処理、コンテナ形式の入出力、ストリーミングを 1 つの NuGet パッケージで提供する、クロスプラットフォームかつ純マネージドの .NET オーディオ処理ライブラリです。

🎧 [AudioStudio](AudioStudio/) クロスプラットフォームデスクトップデモクライアント（Avalonia）を同梱。再生、変換、デバイス、録音、DSP、診断という 6 つのツールモジュールを提供します。

ソースコード：https://github.com/NewLifeX/NewLife.Audio  
NuGet：`NewLife.Audio`

> 📖 詳細ドキュメント（中国語）：[要件定義書](Doc/需求文档.md) | [機能モジュール一覧](Doc/功能模块清单.md) | [クライアント要件定義](Doc/需求文档-客户端.md) | [クライアント機能一覧](Doc/功能模块清单-客户端.md) | [競合分析](Doc/竞品分析报告.md) | [AOT 互換性](Doc/AOT兼容性分析.md) | [多言語ローカライズ](Doc/多语言本地化.md) | [各モジュールのアーキテクチャ設計](Doc/)

## 主な機能

### 🎵 コーデックエンジン（CODEC）
10 種類以上のオーディオコーデックに対する PCM 双方向変換を、すべて純マネージド C# で実装しています：
- **G.711** μ-law / A-law
- **G.722** / **G.726** サブバンド ADPCM
- **IMA ADPCM**（DVI4）
- **Opus**（純マネージド CELT 実装）
- **MP3**（ハフマン復号 + 逆量子化 + IMDCT + サブバンド合成）
- **AAC-LC**（ICS 解析 + ハフマン復号 + IMDCT + 窓関数オーバーラップ加算）
- **FLAC**（Fixed LPC + Rice 残差符号化 + MD5）
- **Ogg Vorbis**（3 つのヘッダーパケットのエンコード/デコード）
- **Speex**（純マネージド狭帯域 CELP）
- `CodecRegistry` + `ICodecInfo` メタデータによるプラグイン可能なファクトリ
- IoT チップのヘッダー適応（Hisilicon / Dahua / Uniview）

### 🔊 オーディオデバイス I/O（DEVIO）
クロスプラットフォームなオーディオデバイスの抽象化：
- デバイスの列挙と検出
- Windows 上の **WaveOut** / **WASAPI** / **ASIO** バックエンド
- 音声キャプチャとループバック録音
- `AudioDeviceManager` による複数デバイスの同時管理

### 🎛️ DSP パイプライン（DSP）
組み合わせ可能なオーディオ信号処理チェーン：
- リサンプリング（線形補間）
- チャンネル変換（モノラル ↔ ステレオ）
- ビット深度 / サンプル形式の変換（8/16/32 ビット ↔ float）
- 音量制御とゲイン（リニア / 対数）
- 正規化付きの複数音源ミキシング
- **FFT** スペクトル解析（Cooley-Tukey、Hann/Hamming/Blackman 窓）
- **イコライザー**（10 バンドグラフィック + 3 バンドトーン、BiQuad カスケード）
- **BiQuad フィルター**（RBJ 標準：LP/HP/BP/Notch/Peaking/LowShelf/HighShelf）
- ダイナミックレンジコンプレッサー（ソフトニー、RMS エンベロープ）
- フェードイン/アウトとクロスフェード（リニア/対数/コサイン）
- 速度変更（OLA）とピッチシフト
- 連鎖可能な `IAudioProcessor` + `AudioPipeline`

### 🗣️ 音声処理（VOP）
リアルタイム音声通信のフロントエンド：
- **VAD** — 音声区間検出（GMM モデル、6 サブバンド + ハングオーバー）
- **AEC** — 音響エコーキャンセレーション（NLMS 適応フィルター + ダブルトーク検出 + コンフォートノイズ）
- **AGC** — 自動ゲイン制御（スローアタック・ファストリリース、RMS エンベロープ）
- **NS** — ノイズサプレッション（スペクトル減算 + FFT/IFFT + OLA 合成）
- 音声プリプロセッサパイプライン：HPF → VAD → AGC

### 📦 コンテナ形式（CONT）
オーディオファイル形式の読み書き：
- **WAV** — RIFF の完全な解析と書き込み
- **Ogg** — ページ単位の読み書き（Opus/Vorbis）
- **MP4/M4A** — ISO BMFF：ftyp/moov/mdat ボックスの解析と生成
- **FLAC** — ネイティブな fLaC メタデータブロックの解析
- **Raw PCM** — 自動検出、または手動での形式指定

### 📡 ストリーミングと転送（STRM）
ネットワーク経由のオーディオストリーミング：
- **RTP** パケット化と非パケット化（RFC 3550）+ セッション管理
- **RTSP** クライアント（TCP：OPTIONS→DESCRIBE→SETUP→PLAY→TEARDOWN、SDP 解析）
- **HTTP** オーディオストリーミング（Icecast/SHOUTcast）
- **WebSocket** 双方向オーディオパイプ（5 バイトのフレームヘッダー）

## インストール

```bash
dotnet add package NewLife.Audio
```

## クイックスタート

```csharp
using NewLife.Audio;
using NewLife.Audio.Codecs;

// G.711 μ-law を PCM にデコードする
var codec = new G711UCodec();
var pcm = codec.ToPcm(encodedData);

// PCM を G.711 A-law にエンコードする
var g711a = new G711ACodec();
var encoded = g711a.FromPcm(pcmData);
```

## エコシステムマップ

NewLife は、独立したバージョン管理を行うオープンソースプロジェクトの階層型エコシステムです。以下のマップはすべての NewLife リポジトリで逐語的に同一の内容を共有しており、各プロジェクトはそのリポジトリと主要な NuGet パッケージにリンクしています。

| プロジェクト | 概要 | ダウンロード数 |
| --- | --- | --- |
| **基盤** | | |
| [NewLife.Core](https://github.com/NewLifeX/X) | ログ、構成、キャッシュ、ネットワーク、シリアライズ、スケジューリング、APM トレーシング。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Core?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Core) |
| [NewLife.XCode](https://github.com/NewLifeX/NewLife.XCode) | データミドルウェア：単一テーブルで数百億件級、自動シャーディング、読み書き分離、10 種類以上のデータベースに対応。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCode?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCode) |
| [NewLife.Net](https://github.com/NewLifeX/NewLife.Net) | 高性能ネットワークライブラリ：単一マシンで 177 万 pps / 14.5 Gbps（TCP 1KB、2026-10）、400 万の TCP 長接続。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Net?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Net) |
| [NewLife.Remoting](https://github.com/NewLifeX/NewLife.Remoting) | CS アプリケーションと IoT デバイス向けのプロトコル通信および RPC フレームワーク。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Remoting?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Remoting) |
| [NewLife.Agent](https://github.com/NewLifeX/NewLife.Agent) | .NET アプリケーションを OS デーモンとしてインストール：Windows サービスと Linux systemd。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Agent?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Agent) |
| [NewLife.Templates](https://github.com/NewLifeX/NewLife.Zero) | コンソール、Web、API、サービス、データ、IoT 向けの `dotnet new` プロジェクトテンプレート。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Templates?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Templates) |
| **データとメッセージング** | | |
| [NewLife.Redis](https://github.com/NewLifeX/NewLife.Redis) | マイクロ秒級の遅延、毎秒数百万 ops のスループット、メッセージキュー内蔵の Redis クライアント。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Redis?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Redis) |
| [NewLife.RocketMQ](https://github.com/NewLifeX/NewLife.RocketMQ) | Apache RocketMQ と Alibaba Cloud Message Queue に対応した純マネージド RocketMQ クライアント。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.RocketMQ?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.RocketMQ) |
| [NewLife.MQTT](https://github.com/NewLifeX/NewLife.MQTT) | MQTT クライアントとサーバー。Alibaba Cloud IoT への接続を含みます。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MQTT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MQTT) |
| [NewLife.NovaDb](https://github.com/NewLifeX/NewLife.NovaDb) | C# 製ハイブリッドデータベース：組み込み/ネットワークの両モード、リレーショナル、時系列、メッセージキュー、KV を 1 つに。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.NovaDb?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.NovaDb) |
| [NewLife.MySql](https://github.com/NewLifeX/NewLife.MySql) | NewLife データスタック上に構築された MySql ドライバーとヘルパー層。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.MySql?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.MySql) |
| **Web、AI、分散** | | |
| [NewLife.Cube](https://github.com/NewLifeX/NewLife.Cube) | 高速な管理プラットフォームフレームワーク：権限、SSO、OAuth サーバー、CRUD スキャフォールディング。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Cube?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Cube) |
| [Stardust](https://github.com/NewLifeX/Stardust) | 分散サービスプラットフォーム：ノード管理、APM、構成センター、登録センター、リリースセンター。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Stardust?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Stardust) |
| [AntJob](https://github.com/NewLifeX/AntJob) | シャーディング、リトライ、バックフィルを備えた分散タスクとリアルタイムデータのスケジューリングプラットフォーム。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AntJob?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AntJob) |
| [NewLife.AI](https://github.com/NewLifeX/NewLife.AI) | 46 社の LLM プロバイダーに統一接続するクライアントと、すぐに使える Web チャットアプリ ChatAI。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.AI?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.AI) |
| **産業と IoT** | | |
| [NewLife.IoT](https://github.com/NewLifeX/NewLife.IoT) | プロトコル契約とデバイス抽象を定義する IoT 標準ライブラリ。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IoT?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IoT) |
| [NewLife.Modbus](https://github.com/NewLifeX/NewLife.Modbus) | NewLife.IoT 上に実装された Modbus TCP/RTU/ASCII。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Modbus?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Modbus) |
| [NewLife.Siemens](https://github.com/NewLifeX/NewLife.Siemens) | NewLife.IoT 上に実装されたシーメンス PLC プロトコル。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Siemens?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Siemens) |
| [NewLife.Omron](https://github.com/NewLifeX/NewLife.Omron) | NewLife.IoT 上に実装されたオムロン PLC プロトコル。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Omron?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Omron) |
| [NewLife.Melsec](https://github.com/NewLifeX/NewLife.Melsec) | NewLife.IoT 上に実装された三菱 Melsec PLC プロトコル。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Melsec?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Melsec) |
| [NewLife.OPC](https://github.com/NewLifeX/NewLife.OPC) | 産業データアクセス向けの OPC クライアント。 | — |
| [NewLife.BACnet](https://github.com/NewLifeX/NewLife.BACnet) | BACnet ビル管理プロトコルスタック。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.BACnet?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.BACnet) |
| [ZeroIoT](https://github.com/NewLifeX/ZeroIoT) | ゲートウェイとデバイス管理を備えた軽量なオープンソース IoT プラットフォーム。 | — |
| **車載テレマティクス** | | |
| [NewLife.JT808](https://github.com/NewLifeX/NewLife.JT808) | JT/T 808 車両位置情報プロトコル：70 以上のメッセージボディ、2011/2013/2019 に対応。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT808?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT808) |
| [NewLife.JT809](https://github.com/NewLifeX/NewLife.JT809) | 運輸管理部門向けの JT/T 809 プラットフォーム間プロトコル。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT809?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT809) |
| [NewLife.JT1078](https://github.com/NewLifeX/NewLife.JT1078) | JT/T 1078 車載ビデオ：RTP 解析、FLV/fMP4 リマックス、ライブ配信。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.JT1078?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.JT1078) |
| **ドメインコンポーネント** | | |
| [NewLife.Office](https://github.com/NewLifeX/NewLife.Office) | Excel、Word、PPT、PDF など 15 以上のドキュメント形式を 1 パッケージで。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Office?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Office) |
| [NewLife.Audio](https://github.com/NewLifeX/NewLife.Audio) | クロスプラットフォームなオーディオ：コーデック、デバイス I/O、DSP、コンテナ、ストリーミング。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Audio?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Audio) |
| [NewLife.Video](https://github.com/NewLifeX/NewLife.Video) | ビデオコーデック、コンテナ形式、フレーム処理、ストリーミング。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Video?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Video) |
| [NewLife.Map](https://github.com/NewLifeX/NewLife.Map) | 百度地図、高徳地図、テンセント地図、天地図を統一インターフェイス化。ジオコーディングと座標変換に対応。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.Map?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.Map) |
| [NewLife.IP](https://github.com/NewLifeX/NewLife.IP) | IPv4 の位置情報：オフラインのメモリマップデータベースとオンライン API フォールバック。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.IP?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.IP) |
| **ツールと製品** | | |
| [CrazyCoder](https://github.com/NewLifeX/XCoder) | 開発者向けツールボックス：ネットワーク、シリアルポート、暗号化、正規表現、Modbus、MQTT。 | [![NuGet](https://img.shields.io/nuget/dt/NewLife.XCoder?style=flat-square&logo=nuget&label=downloads)](https://www.nuget.org/packages/NewLife.XCoder) |
| [GitCandy](https://github.com/NewLifeX/GitCandy) | セルフホスト型 Git ソースコード管理システム。 | — |
| [LuckyClover](https://github.com/NewLifeX/LuckyClover) | 古いホストから起動した際に、対応する .NET ランタイムを自動でインストールします。 | — |

> バッジは各プロジェクトの主要パッケージを示します。テーマ、拡張、サテライトパッケージは各 NuGet ページを参照してください。ここに記載していない 30 以上のパッケージ（FTP、LoRa、Office 拡張、各種プロトコルドライバーなど）は [NewLifeX 組織](https://github.com/NewLifeX) と [newlifex.com](https://newlifex.com) で公開しています。

## 新生命開発チーム

<img src="https://newlifex.com/logo.png" width="48" align="right" alt="NewLife" />

新生命チームは 2002 年に設立され、20 年以上にわたり .NET と IoT の技術エコシステムに注力してきました。100 近いオープンソースプロジェクトを維持・主導し（そのうち 34 がこのマップの中核プロジェクト）、電力、物流、医療、産業制御、インターネットなどの業界向けにソフトウェアとハードウェアを一体化したソリューションを提供しています。私たちは「学問に先後なし、達する者が師である」という信条を大切にしています。

オープンソースコンポーネントは NuGet で累計 <!--nuget-total:start-->6.48M<!--nuget-total:end--> 回ダウンロードされています（エコシステム全体、2026-10 時点）。

ウェブサイト：https://newlifex.com · オープンソース組織：https://github.com/NewLifeX

<details>
<summary>WeChat 公式アカウント：智能大石头（スキャンしてフォロー）</summary>

![智能大石头](https://newlifex.com/stone.jpg)

</details>

## ライセンス

MIT License © 2002-2026 NewLife チーム

[GitHub](https://github.com/NewLifeX/NewLife.Audio) | [ウェブサイト](https://newlifex.com)
