using NewLife;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Devices;

/// <summary>音频驱动工厂。根据运行时平台自动选择最优驱动</summary>
/// <remarks>
/// Windows 上默认使用 <see cref="WaveOutNativeDriver"/>（winmm），可选 <see cref="WasapiDriver"/>；
/// Linux 上在 libasound 可用时使用 <see cref="AlsaDriver"/>。<br/>
/// 平台没有内置原生后端时，<see cref="Create()"/> 返回的驱动在创建播放器/录制器时明确抛出 <see cref="PlatformNotSupportedException"/>，
/// 不再伪造设备或返回空录制器。<br/>
/// 驱动选择一律按运行时平台判断，与编译目标框架无关（net461/netstandard/net8.0 行为一致）。
/// </remarks>
/// <example>
/// <code>
/// if (AudioDriverFactory.IsSupported)
/// {
///     using var player = AudioDriverFactory.Create().CreatePlayer();
///     player.Init(AudioFormat.Default);
///     player.Write(pcm);
/// }
/// </code>
/// </example>
public static class AudioDriverFactory
{
    /// <summary>是否运行在 Windows</summary>
    public static Boolean IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;

    /// <summary>当前平台是否有可用的音频设备后端</summary>
    /// <remarks>无可用后端时，<see cref="Create()"/> 返回的驱动在创建播放器/录制器时抛出 <see cref="PlatformNotSupportedException"/>。</remarks>
    public static Boolean IsSupported => IsWindows || AlsaDriver.IsAvailable;

    /// <summary>枚举当前平台可用的后端名称</summary>
    /// <returns>后端名称列表（小写）；无可用后端时返回空列表</returns>
    public static IReadOnlyList<String> GetAvailableBackends()
    {
        if (IsWindows) return ["waveout", "wasapi", "asio"];
        if (AlsaDriver.IsAvailable) return ["alsa"];

        return [];
    }

    /// <summary>创建当前平台最优驱动</summary>
    /// <returns>可用驱动；当前平台无后端时返回不可用驱动</returns>
    public static IAudioDriver Create()
    {
        if (IsWindows) return new WaveOutNativeDriver();
        if (AlsaDriver.IsAvailable) return new AlsaDriver();

        return new UnsupportedDriver();
    }

    /// <summary>创建指定名称的驱动</summary>
    /// <param name="name">驱动名称：waveout / wasapi / asio（Windows），alsa（Linux）；未知名称按平台最优驱动处理</param>
    /// <returns>驱动实例；当前平台无后端时返回不可用驱动</returns>
    public static IAudioDriver Create(String name)
    {
        if (IsWindows)
        {
            return (name?.ToLower()) switch
            {
                "wasapi" => new WasapiDriver(),
                "asio" => new AsioDriver(),
                _ => new WaveOutNativeDriver(),
            };
        }

        if (AlsaDriver.IsAvailable) return new AlsaDriver();

        return new UnsupportedDriver();
    }
}

/// <summary>当前平台不支持的驱动。明确报告不可用，不再伪造设备或静默回退</summary>
internal class UnsupportedDriver : IAudioDriver
{
    public String Name => "Unsupported";

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices() => [];

    public AudioDeviceInfo GetDefaultOutputDevice() => null;

    public AudioDeviceInfo GetDefaultInputDevice() => null;

    public IAudioPlayer CreatePlayer() => throw new PlatformNotSupportedException(L.T("No audio backend is available."));

    public IAudioRecorder CreateRecorder() => throw new PlatformNotSupportedException(L.T("No audio backend is available."));
}
