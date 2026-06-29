using NewLife.Audio.DSP;

namespace NewLife.Audio.Devices;

/// <summary>音频播放器接口</summary>
public interface IAudioPlayer : IDisposable
{
    /// <summary>初始化播放器</summary>
    /// <param name="format">音频格式</param>
    /// <param name="deviceId">设备ID，null=默认设备</param>
    void Init(AudioFormat format, String deviceId = null);

    /// <summary>开始播放</summary>
    void Play();

    /// <summary>暂停播放</summary>
    void Pause();

    /// <summary>停止播放</summary>
    void Stop();

    /// <summary>写入音频数据到播放缓冲区</summary>
    /// <param name="data">PCM 音频数据</param>
    void Write(Byte[] data);

    /// <summary>缓冲区中待播放的字节数</summary>
    Int32 BufferedBytes { get; }

    /// <summary>播放停止事件</summary>
    event EventHandler PlaybackStopped;
}

/// <summary>音频录制器接口</summary>
public interface IAudioRecorder : IDisposable
{
    /// <summary>初始化录制器</summary>
    /// <param name="format">音频格式</param>
    /// <param name="deviceId">设备ID</param>
    void Init(AudioFormat format, String deviceId = null);

    /// <summary>开始录制</summary>
    void StartRecording();

    /// <summary>停止录制</summary>
    void StopRecording();

    /// <summary>数据可用事件（PCM 字节数据）</summary>
    event EventHandler<Byte[]> DataAvailable;
}

/// <summary>音频后端适配器接口</summary>
public interface IAudioBackend
{
    /// <summary>后端名称</summary>
    String Name { get; }

    /// <summary>枚举设备</summary>
    IReadOnlyList<AudioDeviceInfo> EnumerateDevices();

    /// <summary>获取默认输出设备</summary>
    AudioDeviceInfo GetDefaultOutputDevice();

    /// <summary>获取默认输入设备</summary>
    AudioDeviceInfo GetDefaultInputDevice();

    /// <summary>创建播放器</summary>
    IAudioPlayer CreatePlayer();

    /// <summary>创建录制器</summary>
    IAudioRecorder CreateRecorder();
}
