using System.Runtime.InteropServices;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Devices;

#if NETFRAMEWORK || WINDOWS
/// <summary>Windows WaveOut 后端（P/Invoke winmm.dll）</summary>
public class WaveOutBackend : IAudioBackend
{
    /// <summary>后端名称</summary>
    public String Name => "Windows WaveOut";

    /// <summary>枚举音频设备</summary>
    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        var devices = new List<AudioDeviceInfo>
        {
            new() { Id = "default", Name = "默认音频设备", IsInput = false, IsDefault = true },
        };
        return devices;
    }

    /// <summary>获取默认输出设备</summary>
    public AudioDeviceInfo GetDefaultOutputDevice() =>
        new() { Id = "default", Name = "默认音频设备", IsInput = false, IsDefault = true };

    /// <summary>获取默认输入设备</summary>
    public AudioDeviceInfo GetDefaultInputDevice() =>
        new() { Id = "capture", Name = "默认录音设备", IsInput = true, IsDefault = true };

    /// <summary>创建播放器</summary>
    public IAudioPlayer CreatePlayer() => new DummyPlayer();

    /// <summary>创建录制器</summary>
    public IAudioRecorder CreateRecorder() => new DummyRecorder();
}
#else
/// <summary>跨平台默认后端（ALSA/CoreAudio 通过条件编译适配）</summary>
public class DefaultBackend : IAudioBackend
{
    /// <summary>后端名称</summary>
    public String Name
    {
        get
        {
#if NETFRAMEWORK || WINDOWS
            return "Windows WaveOut";
#else
            return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux ALSA" :
                   RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS CoreAudio" : "Unknown";
#endif
        }
    }

    /// <summary>枚举音频设备</summary>
    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        return new List<AudioDeviceInfo>
        {
            new() { Id = "default", Name = "默认音频设备", IsInput = false, IsDefault = true },
        };
    }

    /// <summary>获取默认输出设备</summary>
    public AudioDeviceInfo GetDefaultOutputDevice() =>
        new() { Id = "default", Name = "默认音频设备", IsInput = false, IsDefault = true };

    /// <summary>获取默认输入设备</summary>
    public AudioDeviceInfo GetDefaultInputDevice() =>
        new() { Id = "capture", Name = "默认录音设备", IsInput = true, IsDefault = true };

    /// <summary>创建播放器</summary>
    public IAudioPlayer CreatePlayer() => new DummyPlayer();

    /// <summary>创建录制器</summary>
    public IAudioRecorder CreateRecorder() => new DummyRecorder();
}
#endif

/// <summary>虚拟播放器（测试/无设备环境）</summary>
internal class DummyPlayer : IAudioPlayer
{
    public event EventHandler PlaybackStopped;

    public Int32 BufferedBytes => 0;

    public void Init(AudioFormat format, String deviceId = null) { }

    public void Play() { }

    public void Pause() { }

    public void Stop() { PlaybackStopped?.Invoke(this, EventArgs.Empty); }

    public void Write(Byte[] data) { }

    public void Dispose() { }
}

/// <summary>虚拟录制器（测试/无设备环境）</summary>
internal class DummyRecorder : IAudioRecorder
{
    public event EventHandler<Byte[]> DataAvailable;

    public void Init(AudioFormat format, String deviceId = null) { }

    public void StartRecording() { }

    public void StopRecording() { }

    public void Dispose() { }
}
