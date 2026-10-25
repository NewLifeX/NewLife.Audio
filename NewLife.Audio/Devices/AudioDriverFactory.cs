using NewLife;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Devices;

/// <summary>音频驱动工厂。根据运行时平台自动选择最优驱动</summary>
public static class AudioDriverFactory
{
    /// <summary>创建当前平台最优驱动</summary>
    public static IAudioDriver Create()
    {
#if NETFRAMEWORK || WINDOWS
        return new WaveOutNativeDriver();
#else
        return new FallbackDriver();
#endif
    }

    /// <summary>创建指定名称的驱动</summary>
    public static IAudioDriver Create(String name)
    {
        return (name?.ToLower()) switch
        {
            "waveout" => new WaveOutNativeDriver(),
            "wasapi" => new WasapiDriver(),
            "asio" => new AsioDriver(),
            _ => Create(),
        };
    }
}

/// <summary>跨平台回退驱动（无硬件加速）</summary>
internal class FallbackDriver : IAudioDriver
{
    public String Name => "Fallback";

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices() =>
        new List<AudioDeviceInfo> { new() { Id = "0", Name = L.T("Default device"), IsDefault = true } };

    public AudioDeviceInfo GetDefaultOutputDevice() =>
        new() { Id = "0", Name = L.T("Default device"), IsDefault = true };

    public AudioDeviceInfo GetDefaultInputDevice() =>
        new() { Id = "0", Name = L.T("Default device"), IsInput = true, IsDefault = true };

    public IAudioPlayer CreatePlayer() => new WaveOutPlayer();

    public IAudioRecorder CreateRecorder() => new NullRecorder();
}
