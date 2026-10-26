# DEVIO-音频设备IO

> 版本：v1.3 | 日期：2026-10-26 | [← 返回 README](../README.zh-CN.md) | [English README](../README.md)
> 需求对应：[需求文档](需求文档.md) 第 3 章 | 功能清单：[功能模块清单](功能模块清单.md)

---

## 1. 模块职责

| 职责 | 说明 |
|---|---|
| 设备发现 | 枚举系统音频输入/输出设备，获取名称、通道数、格式支持等元数据 |
| 音频播放 | 将 PCM/编码数据推送到音频输出设备 |
| 音频录制 | 从音频输入设备采集原始 PCM 数据 |
| 驱动适配 | 目标平台：Windows（WaveOut/WASAPI/ASIO）、Linux（ALSA）、macOS（CoreAudio）、Android（AAudio）、iOS（AVAudioEngine）；**当前仅 Windows WaveOut/WaveIn（WinMM）为完整实现**，其余为骨架或未开始 |
| 生命周期管理 | 播放/录制会话的启停、暂停、恢复、缓冲区管理 |

---

## 2. 核心组件

| 组件 | 说明 |
|---|---|
| `AudioDeviceManager` | 设备管理器：驱动注册、设备枚举、默认设备选择、播放器/录制器创建 |
| `AudioDriverFactory` | 驱动工厂：`Create()` 按编译符号选择（net461 → WaveOut，其余 → `FallbackDriver`）；`Create(name)` 可显式指定 waveout / wasapi / asio。**注意**：主库无 `net8.0-windows` 目标，故 .NET 8/10 默认走回退驱动（见[架构设计](架构设计.md) FIX-005） |
| `IAudioPlayer` | 播放器接口：`Play()`/`Pause()`/`Stop()`/`Init(AudioFormat)` |
| `IAudioRecorder` | 录音器接口：`StartRecording()`/`StopRecording()`/`DataAvailable` 事件 |
| `IAudioDriver` | 音频驱动统一接口：枚举设备、创建播放器/录制器 |
| `AudioFormat` | 音频格式描述：采样率、位深、声道数、编码类型 |
| `WaveOutNativeDriver` | Windows WaveOut API 驱动实现（WaveOut 播放 + WaveIn 录制，真实 P/Invoke） |
| `WasapiDriver` | **骨架**：播放/录制器方法体为空，设备枚举返回两条硬编码条目（未走 `IMMDeviceEnumerator`） |
| `AsioDriver` | **骨架**：仅做注册表检测，无任何原生调用 |
| `LoopbackRecorder` | 系统音频回路录制：当前为后台线程 + 正弦波模拟，真实 WASAPI Loopback 未实现 |
| `AudioDeviceInfo` | 音频设备元数据：名称、ID、输入/输出、支持格式 |

---

## 3. 关键流程

### 3.1 播放流程

```mermaid
sequenceDiagram
    participant App as 调用方
    participant Player as IAudioPlayer
    participant Driver as IAudioDriver
    participant Device as 音频设备
    App->>Player: Init(AudioFormat)
    Player->>Driver: Open(format)
    Driver->>Device: 打开设备
    loop 播放循环
        App->>Player: Write(pcmData)
        Player->>Driver: 写入缓冲区
        Driver->>Device: 播放
    end
    App->>Player: Stop()
    Player->>Driver: Close()
    Driver->>Device: 关闭设备
```

### 3.2 录制流程

```mermaid
sequenceDiagram
    participant App as 调用方
    participant Recorder as IAudioRecorder
    participant Driver as IAudioDriver
    participant Device as 音频设备
    App->>Recorder: StartRecording(format)
    Recorder->>Driver: StartCapture(format)
    Driver->>Device: 开始采集
    loop 录制循环
        Device-->>Driver: 音频数据
        Driver-->>Recorder: DataAvailable(Byte[])
        Recorder-->>App: DataAvailable(Byte[])
    end
    App->>Recorder: StopRecording()
    Recorder->>Driver: StopCapture()
    Driver->>Device: 停止采集
```

### 3.3 播放器 + 信号链整合（与 DSP 协同）

```mermaid
flowchart LR
    Source[数据源] --> Pipeline[音频处理管线]
    Pipeline --> Player[IAudioPlayer]
    Player --> Driver[平台驱动]
    Driver --> Device[音频设备]
```

---

## 4. 接口/数据结构

### 4.1 核心接口

```csharp
/// <summary>音频格式描述</summary>
public class AudioFormat
{
    public Int32 SampleRate { get; set; } = 8000;
    public Int32 Channels { get; set; } = 1;
    public Int32 BitsPerSample { get; set; } = 16;
    public AVTypes Encoding { get; set; } = AVTypes.LPCM;
    public Int32 SamplesPerFrame { get; set; } = 160;
    public Int32 ByteRate => SampleRate * Channels * BitsPerSample / 8;
    public Int32 BytesPerSample => BitsPerSample / 8;
    public Int32 BytesPerFrame => SamplesPerFrame * Channels * BytesPerSample;
}

/// <summary>音频设备信息</summary>
public class AudioDeviceInfo
{
    public String Id { get; set; }
    public String Name { get; set; }
    public Boolean IsInput { get; set; }
    public Boolean IsDefault { get; set; }
    public List<AudioFormat> SupportedFormats { get; set; } = [];
}

/// <summary>音频播放器接口</summary>
public interface IAudioPlayer : IDisposable
{
    void Init(AudioFormat format, String deviceId = null);
    void Play();
    void Pause();
    void Stop();
    void Write(Byte[] data);
    Int32 BufferedBytes { get; }
    event EventHandler PlaybackStopped;
}

/// <summary>音频录制器接口</summary>
public interface IAudioRecorder : IDisposable
{
    void Init(AudioFormat format, String deviceId = null);
    void StartRecording();
    void StopRecording();
    event EventHandler<Byte[]> DataAvailable;
}
```

### 4.2 音频驱动

```csharp
/// <summary>音频驱动接口。一个驱动对应一套平台 API</summary>
public interface IAudioDriver
{
    String Name { get; }
    IReadOnlyList<AudioDeviceInfo> EnumerateDevices();
    AudioDeviceInfo GetDefaultOutputDevice();
    AudioDeviceInfo GetDefaultInputDevice();
    IAudioPlayer CreatePlayer();
    IAudioRecorder CreateRecorder();
}
```

---

## 5. 设计决策

| 决策 | 理由 |
|---|---|
| 驱动适配器模式 | 不同平台/API 差异巨大，适配器模式隔离变化，核心接口保持稳定 |
| 同步 `Write()` + 异步事件 `DataAvailable` | 播放是调用方主动推送（同步写），录制是被动接收（事件回调），符合各自语义 |
| `AudioFormat` 包含 `AVTypes` | 支持编码音频直接播放（如 G.711 设备输出），不只是原始 PCM |
| 驱动按编译符号选择（待改） | 当前用 `#if NETFRAMEWORK \|\| WINDOWS` 选择，主库无 `net8.0-windows` 目标，导致 .NET 8/10 落到 `FallbackDriver`；计划改为运行时平台判断（FIX-005） |
| 优先平台原生 API | 不用第三方跨平台抽象库（增加原生依赖），直接调用 OS API，零依赖 |
| 跨平台原生驱动进入路线图 | 原"Linux/macOS 暂缓"的结论已调整：五平台（含 Android/iOS）设备 I/O 纳入阶段一/二，见[架构设计](架构设计.md) 路线图 |
| 驱动命名（v1.2 破坏性） | `IAudioBackend`/`AudioBackendFactory` 与三个 Backend 实现直接改名为 Driver，不保留 Obsolete 别名；旧调用方需替换类型名，API 兼容门禁会报告这些删除 |

---

（完）
