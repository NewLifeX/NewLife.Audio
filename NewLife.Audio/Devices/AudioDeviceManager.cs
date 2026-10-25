using NewLife;

namespace NewLife.Audio.Devices;

/// <summary>音频设备管理器。统一管理设备枚举、默认设备选择、驱动工厂</summary>
public class AudioDeviceManager
{
    private readonly List<IAudioDriver> _drivers = [];
    private IAudioDriver _activeDriver;

    /// <summary>已注册的驱动列表</summary>
    public IReadOnlyList<IAudioDriver> Drivers => _drivers;

    /// <summary>当前活动驱动</summary>
    public IAudioDriver ActiveDriver => _activeDriver;

    /// <summary>注册驱动</summary>
    public void RegisterDriver(IAudioDriver driver)
    {
        if (driver == null) throw new ArgumentNullException(nameof(driver));
        _drivers.Add(driver);
        _activeDriver ??= driver;
    }

    /// <summary>枚举所有设备</summary>
    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        if (_activeDriver == null) return Array.Empty<AudioDeviceInfo>();
        return _activeDriver.EnumerateDevices();
    }

    /// <summary>创建播放器</summary>
    public IAudioPlayer CreatePlayer()
    {
        if (_activeDriver == null) throw new InvalidOperationException(L.T("No audio backend is available."));
        return _activeDriver.CreatePlayer();
    }

    /// <summary>创建录制器</summary>
    public IAudioRecorder CreateRecorder()
    {
        if (_activeDriver == null) throw new InvalidOperationException(L.T("No audio backend is available."));
        return _activeDriver.CreateRecorder();
    }
}
