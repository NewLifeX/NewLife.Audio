using NewLife.Audio.DSP;

namespace NewLife.Audio.Devices;

/// <summary>音频后端工厂。根据运行时平台自动选择最优后端</summary>
public static class AudioBackendFactory
{
    /// <summary>创建当前平台最优后端</summary>
    public static IAudioBackend Create()
    {
#if NETFRAMEWORK || WINDOWS
        return new WaveOutNativeBackend();
#else
        return new FallbackBackend();
#endif
    }

    /// <summary>创建指定名称的后端</summary>
    public static IAudioBackend Create(String name)
    {
        return (name?.ToLower()) switch
        {
            "waveout" => new WaveOutNativeBackend(),
            "wasapi" => new WasapiBackend(),
            "asio" => new AsioBackend(),
            _ => Create(),
        };
    }
}

/// <summary>跨平台回退后端（无硬件加速）</summary>
internal class FallbackBackend : IAudioBackend
{
    public String Name => "Fallback";

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices() =>
        new List<AudioDeviceInfo> { new() { Id = "0", Name = "默认设备", IsDefault = true } };

    public AudioDeviceInfo GetDefaultOutputDevice() =>
        new() { Id = "0", Name = "默认设备", IsDefault = true };

    public AudioDeviceInfo GetDefaultInputDevice() =>
        new() { Id = "0", Name = "默认设备", IsInput = true, IsDefault = true };

    public IAudioPlayer CreatePlayer() => new WaveOutPlayer();

    public IAudioRecorder CreateRecorder() => new NullRecorder();
}
