# API-录音、转码、播放

> 版本：v1.0 | 日期：2026-10-28

面向“不懂音频”的使用者：录音、转码、播放各一行。本页只讲**怎么用**与**为什么这样设计**，细节以 XML 注释和测试为准。

## 1. 三行速查

| 场景 | 调用 |
|---|---|
| 录 5 秒存 WAV（边录边写盘，内存与时长无关） | `AudioRecorder.RecordToFile("record.wav", TimeSpan.FromSeconds(5))` |
| 录 5 秒拿 PCM | `var pcm = AudioRecorder.Capture(TimeSpan.FromSeconds(5))` |
| 任意格式转码 | `AudioConverter.Convert("record.wav", "record.m4a", new AudioEncodeOptions { BitrateKbps = 96000 })` |
| 播放任意格式（播完返回） | `AudioPlayer.PlayFile("record.m4a")` |
| 流式处理麦克风 | `await foreach (var frame in recorder.StreamAsync().WithCancellation(ct)) using (frame) { ... }` |

支持的写入容器：`.wav` `.flac` `.mp3` `.aiff` `.ogg`(Vorbis) `.opus` `.m4a`(AAC-LC) `.aac`(ADTS)。
读取覆盖：WAV/RF64、FLAC、MP3、ADTS AAC、MP4/M4A、Ogg(Opus/Vorbis)、AIFF、CAF。

## 2. 录音为什么不走事件

硬件差异全部藏在驱动实现里，对外只有一个录音接口：

```
硬件（回调/阻塞读） → 驱动采集线程 → 有界队列 → IAudioRecorder.Read/ReadAsync + DataAvailable → 门面
```

- **一个接口** `IAudioRecorder`：`Format`、`IsRecording`、`BufferSeconds`、`BufferedBytes`、`DroppedBytes`、`Overruns`、
  `Init/StartRecording/StopRecording`、`Read(Span, timeout, ct)`、`ReadAsync(Memory, ct)`、`DataAvailable`。
- 驱动继承 `AudioRecorderBase`，只写三件事：`Open` / `Close` / `ReadNative`（阻塞读硬件一块 PCM）。
  **推送式硬件**（厂商回调、SDK 主动推）构造时传 `pushMode: true`，在回调里调用 `Push(pcm)` 即可，基类不建线程。
- 采集线程按时把硬件数据取出放入有界队列（容量 = `BufferSeconds × ByteRate`，默认 10 秒）：
  消费方在这个时长内变慢**不会丢音频**；超出容量才丢最旧数据，并累加到 `DroppedBytes`。
- 主通道是 `Read`/`ReadAsync`（取走即消费）；`DataAvailable` 是旁路观察，入队时若无订阅者零开销，
  有订阅者给一份**独立副本**，随便存，不影响主读取。
- `ReadAsync` 等的是"队列有新数据"的信号（`SemaphoreSlim.WaitAsync`），**真异步、不占线程**；
  唯一常驻线程是每路采集的取数线程（硬件决定的，无法省略）。
- 队列**不做背压**：麦克风不会因为消费慢而放慢，对它背压只会把丢数据的现场挪回硬件缓冲且更难统计。

## 3. 播放为什么不需要调用方分块

`AudioPlayer` 内部完成：解码 → 按 100 毫秒切块 → 等播放器缓冲回落到水位（有信号时等信号，没有则按积压轮询）→ 写入 → 等缓冲播完。

`Write(ReadOnlySpan<Byte>)` 与水位等待都是内部实现细节，第三方播放器只需实现 `IAudioPlayer` 也能正常使用。

文件播放走可定位会话：逐帧解码 → 按水位写入；`Seek(秒)` 在解码锁内定位容器帧并清掉播放器积压，因此拖动进度不会与解码线程打架；
Ogg Opus 的编码器前导（pre-skip）与尾部填充自动裁剪。`PcmDataAvailable` 每块送播放器前触发（独立副本），供频谱/波形使用。

## 4. 转码为什么只有一个入口

`AudioConverter.Convert` 内部：探测 → 逐帧解码（`AudioFile.StreamPcm`）→ `AudioEncodePipeline`（重采样、声道转换、编码、封装）。
目标容器由扩展名决定，编码参数统一放在 `AudioEncodeOptions`；标签默认沿用源文件，可用 `PreserveMetadata=false` 关闭。
目标文件先写临时文件再原子替换，中途失败不留半成品。

`AudioFile.WritePcm` / `AudioFile.SaveAs` 也走同一条管线，因此单文件门面与转码门面的行为一致。

## 5. 缓冲与分配

| 位置 | 做法 |
|---|---|
| 采集驱动 | 复用中转缓冲，`Marshal.Copy` 直写；无每块分配 |
| 队列 | `AudioFrame.Rent` 从 `ArrayPool<Byte>` 租用，读取或丢弃时归还 |
| 流式枚举 | 直接从队列取帧，零拷贝；用完必须 `Dispose` |
| 落盘 | `RecordToFile` 直接把 PCM 喂编码管线，不驻留整段录音 |
| 播放 | 单一 chunk 缓冲复用；内部 Span 写入避免调用方 `ToArray` |
| 转码 | 逐块解码/编码，内存与文件时长无关 |

`OwnerPacket`/`IPacket` 只在需要跨层引用计数共享的场景使用（如管线接收上游 `IPacket`），采集链路用 `AudioFrame` 即可。

## 6. 已知边界

- 消费方落后超过 `BufferSeconds` 时会丢最旧音频，`DroppedBytes` 如实计数；硬件侧覆盖/欠载计入 `Overruns`。
  “完全不丢”只能靠无限内存，这是物理取舍，不是实现缺陷。
- `.opus` 编码依赖可用编码后端（托管未完成时需 ffmpeg），不可用时 `AudioConverter` 明确抛异常。
- 播放的 `Seek` 仅对文件播放有效（PCM/流播放无可定位容器）；拖动后位置以“跳转点 + 已写入样本”为准。
- **破坏性变更**：`IAudioRecorder` 合并了事件与读取能力（新增 Format/IsRecording/BufferSeconds/BufferedBytes/DroppedBytes/Overruns/Read/ReadAsync），
  自定义实现需改继承 `AudioRecorderBase` 或补齐成员；发版时按不兼容变更处理。
