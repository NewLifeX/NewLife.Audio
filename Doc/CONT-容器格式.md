# CONT-容器格式

> 版本：v1.3 | 日期：2026-10-26 | [← 返回 README](../README.zh-CN.md) | [English README](../README.md)
> 需求对应：[需求文档](需求文档.md) 第 6 章 | 功能清单：[功能模块清单](功能模块清单.md)

---

## 1. 模块职责

| 职责 | 说明 |
|---|---|
| 音频文件读取 | 解析容器格式（WAV / Ogg / MP4-M4A / FLAC / MP3 / AIFF / CAF / 裸 PCM），提取编码音频数据和格式元数据 |
| 音频文件写入 | 将编码音频数据封装为容器格式并写入头/数据块；当前覆盖 WAV / Ogg / MP4-M4A / MP3 / FLAC / AIFF |
| 流式输出 | `Writers` 命名空间的 `WavStreamWriter` / `OggOpusStreamWriter` / `Mp3StreamWriter` 面向不可 seek 的流（HTTP/WebSocket 边合成边写） |
| 格式探测 | `AudioProbe` 按魔术字节识别 WAV/RF64/Ogg/FLAC/MP3/ADTS/MP4/AIFF/CAF；扩展名只作提示，未知时明确返回 Unknown |
| 元数据管理 | WAV（LIST/INFO）、MP3（ID3v2.4/v1.1）、FLAC（VORBIS_COMMENT）、Ogg（OpusTags/Vorbis comment）、M4A（udta/meta/ilst）均可读写 |

> 已知限制：WAV 已支持 RIFF 块遍历、`fmt` 16/18/40、`WAVE_FORMAT_EXTENSIBLE`、IEEE float、24/32 bit、RF64 与 `LIST/INFO`；FLAC 读取器与 `FlacFileWriter` 均已提供；CAF 只有读取器，AMR/WMA 明确不做。`AudioContainerFactory.CreateReader(stream)` 只按魔术字节识别，未识别时抛 `NotSupportedException`（FIX-009 已修），需要判断未知流请用 `AudioProbe`，需要裸 PCM 请显式构造 `RawPcmReader` 并传入格式。

---

## 2. 核心组件

| 组件 | 说明 |
|---|---|
| `IAudioContainerReader` | 容器读取器接口：`Format` / `CodecType` / `TotalFrames` / `Duration` / `Metadata` 属性 + `ReadFrame()` / `SeekFrame()`，实现 `IDisposable` |
| `IAudioContainerWriter` | 容器写入器接口：`Format` 属性 + `WriteMetadata()` / `WriteFrame()` / `Flush()`，实现 `IDisposable` |
| `AudioContainerFactory` | 容器工厂：`CreateReader(path)` / `CreateReader(stream, hint)` / `CreateWriter(path, format)` / `CreateWriter(stream, hint, format)`；写出支持 `.wav` / `.ogg` / `.opus` / `.mp3` / `.mp4` / `.m4a` / `.flac`；读取按扩展名或魔术字节识别 WAV/Ogg/FLAC/MP3/MP4，未识别时抛 `NotSupportedException`（判断未知流用 `AudioProbe`，读裸 PCM 显式构造 `RawPcmReader`）；不可定位流会把已读取的头部字节还回读取器 |
| `WaveFileReader` / `WaveFileWriter` | WAV 容器读写器：RIFF 块遍历、`fmt` 16/18/40、EXTENSIBLE、浮点、24/32 bit、RF64、`LIST/INFO`；写入为流式头 + 结束回填 |
| `OggFileReader` / `OggFileWriter` | Ogg 容器读写器（Vorbis + Opus；OpusHead/OpusTags、lacing、granule/pre-skip 按 RFC 7845；Vorbis identification/comment/setup 独立头页由 `WriteVorbisHeaders` 写入） |
| `Mp4FileReader` / `Mp4FileWriter` | MP4/M4A 容器读写器（ISO BMFF 基础盒子 + `esds` + `udta/meta/ilst` 标签；无编辑列表/章节） |
| `Mp3FileReader` / `Mp3FileWriter` | MP3 文件读写器：ID3v2 标签 + 帧索引 + Xing/Info/VBRI 信息帧解析（帧数/字节数）；写入支持帧透传与 PCM 压缩 |
| `Mp3Id3Tag` | ID3v2.3/v2.4 标签解析与 v2.4（UTF-8）标签生成；ID3v1/v1.1 尾标签读写（含 80 项流派表），读取时 ID3v2 优先于 ID3v1 |
| `FlacContainerReader` / `FlacFileWriter` | FLAC 原生容器读写：读取 fLaC 元数据块（STREAMINFO/VORBIS_COMMENT）并按帧头 CRC-8 定位；写入 STREAMINFO/SEEKTABLE/VORBIS_COMMENT 与规范帧 |
| `AiffFileReader` / `AiffFileWriter` / `CafFileReader` | AIFF/AIFF-C 双向读写（FORM/COMM、80 位扩展浮点采样率、16 位大端 PCM、sowt 小端可读）；CAF 读取（desc+data、未压缩 lpcm） |
| `RawPcmReader` | 原始 PCM 解析器：按调用方给定的采样率/位深/声道/帧长切帧；不做启发式推断 |
| `AudioProbe` | 容器探测：按魔术字节识别 WAV/RF64/Ogg/FLAC/MP3/ADTS/MP4/AIFF/CAF，返回容器、编码、格式、时长、码率、标签、CanSeek 与置信度 |
| `AudioStreamFactory` / `CallbackStream` | 统一路径、`Stream`、字节数组与回调式数据源/目标；按回调声明读写与定位能力 |
| `AudioMetadata` | 音频元数据模型（标题、艺术家、专辑、时长、码率等） |
| `WavStreamWriter` / `OggOpusStreamWriter` / `Mp3StreamWriter` | 面向不可 seek 流的流式写入器（`Writers` 命名空间），由 TTS 与实时推流使用 |

---

## 3. 关键流程

### 3.1 文件读取流程

```mermaid
sequenceDiagram
    participant App as 调用方
    participant Factory as AudioContainerFactory
    participant Reader as IAudioContainerReader
    participant Codec as CODEC 编解码引擎
    App->>Factory: CreateReader("audio.ogg")
    Factory->>Factory: 扩展名/魔术字节检测 → Ogg
    Factory->>Reader: new OggFileReader(stream)
    Factory-->>App: Reader（Format / CodecType / Metadata）
    loop 每帧
        App->>Reader: ReadFrame()
        Reader-->>App: 编码帧 (IPacket)
        App->>Codec: ToPcm(encoded)
        Codec-->>App: PCM
    end
    App->>Reader: Dispose()
```

### 3.2 文件写入流程

```mermaid
sequenceDiagram
    participant App as 调用方
    participant Factory as AudioContainerFactory
    participant Writer as IAudioContainerWriter
    participant Codec as CODEC 编解码引擎
    App->>Factory: CreateWriter("output.wav", format)
    Factory-->>App: WaveFileWriter
    App->>Writer: WriteMetadata(metadata)
    loop 每帧
        App->>Codec: FromPcm(pcm)
        Codec-->>App: 编码帧
        App->>Writer: WriteFrame(encoded)
    end
    App->>Writer: Flush() / Dispose()
```

### 3.3 格式探测流程（AudioProbe）

```mermaid
flowchart TD
    A[读取文件头魔术字节] --> B{识别结果}
    B -->|"RIFF/RF64"| C[WAV]
    B -->|"OggS"| D[Ogg]
    B -->|"fLaC"| E[FLAC]
    B -->|"ID3 或 0xFFEx"| F[MP3]
    B -->|"ADTS 同步字"| G[AAC/ADTS]
    B -->|"ftyp"| H[MP4/M4A]
    B -->|"其他"| I[Unknown<br/>调用方明确报错，不回退裸 PCM]
```

---

## 4. 接口/数据结构

### 4.1 核心接口

```csharp
/// <summary>容器读取器接口</summary>
public interface IAudioContainerReader : IDisposable
{
    /// <summary>音频格式</summary>
    AudioFormat Format { get; }

    /// <summary>编码类型</summary>
    AVTypes CodecType { get; }

    /// <summary>总帧数（未知时为 0）</summary>
    Int64 TotalFrames { get; }

    /// <summary>总时长（秒）</summary>
    Double Duration { get; }

    /// <summary>音频元数据</summary>
    AudioMetadata Metadata { get; }

    /// <summary>读取下一帧编码数据</summary>
    /// <returns>编码后的音频帧，流结束返回 null</returns>
    IPacket ReadFrame();

    /// <summary>定位到指定帧</summary>
    void SeekFrame(Int64 frameIndex);
}

/// <summary>容器写入器接口</summary>
public interface IAudioContainerWriter : IDisposable
{
    /// <summary>音频格式</summary>
    AudioFormat Format { get; }

    /// <summary>写入元数据</summary>
    void WriteMetadata(AudioMetadata metadata);

    /// <summary>写入一帧编码数据</summary>
    void WriteFrame(ReadOnlySpan<Byte> encodedFrame);

    /// <summary>完成写入（写入尾部索引等）</summary>
    void Flush();
}
```

### 4.2 数据结构

```csharp
/// <summary>音频元数据</summary>
public class AudioMetadata
{
    public String Title { get; set; }
    public String Artist { get; set; }
    public String Album { get; set; }
    public String Genre { get; set; }
    public Int32 TrackNumber { get; set; }
    public Int32 Year { get; set; }
    public String Comment { get; set; }
    public Byte[] CoverImage { get; set; }
    public String CoverImageMimeType { get; set; }
}
```

### 4.3 WAV 文件结构

```
RIFF Header (12 bytes)
├── "RIFF" (4 bytes)
├── File Size - 8 (4 bytes, LE)
├── "WAVE" (4 bytes)
fmt  Chunk (24 bytes)
├── "fmt " (4 bytes)
├── Chunk Size = 16 (4 bytes, LE)
├── Audio Format = 1 (PCM) (2 bytes)
├── Channels (2 bytes)
├── Sample Rate (4 bytes)
├── Byte Rate (4 bytes)
├── Block Align (2 bytes)
├── Bits Per Sample (2 bytes)
data Chunk
├── "data" (4 bytes)
├── Data Size (4 bytes, LE)
└── PCM/编码数据...
```

---

## 5. 设计决策

| 决策 | 理由 |
|---|---|
| 容器与编解码分离 | 容器只负责解析/封装，编码数据交给 CODEC 处理，职责清晰 |
| 读取器不自动解码 | 调用方按需选择解码或不解码（如仅需转发编码数据），灵活性最高 |
| `RawPcmReader` 需要显式参数 | 无头 PCM 没有任何可靠的自描述信息，盲猜采样率/位深/声道极易出错；明确要求调用方给出参数，不做启发式推断（IMP-006） |
| 元数据与音频数据分离 | 写入时元数据可提前设置，不与帧数据混在一起 |
| Ogg 容器同时支持 Vorbis 和 Opus | Ogg 是 Opus 的标准容器（RFC 7845），一石二鸟 |
| Ogg Opus granule = pre-skip + 累计 48 kHz 样本 | 与 RFC 7845 的 PCM sample position 定义一致，浏览器 MSE 可正确 seek；包时长由 TOC 解析而非固定 20 ms |
| Ogg Opus pre-skip 默认 0，本地编码取 `OpusCodec.GetLookahead(options)` | 预跳应等于编码器延迟；外部已编码流未知时取 0 比固定 80 ms 更安全，避免丢弃真实音频 |
| Ogg Vorbis 头包按页拆分 | identification 单独一页，comment 与 setup 各自成页，避免多包同页导致部分解码器只读第一个头包 |
| Ogg Vorbis 首音频包 granule=0 | 首包只建立重叠状态，不产生 PCM；后续页按 hop 累计，读取器按最后 granule 裁剪尾部 padding |
| Ogg 读取器暴露 `PreSkip` / `TotalSamples` / `Duration` | 播放器可据此裁剪 Opus 预跳与尾部 padding，与 RFC 7845 的 PCM sample position 对齐 |

---

（完）
