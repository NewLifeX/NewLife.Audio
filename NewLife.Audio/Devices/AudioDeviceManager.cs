namespace NewLife.Audio.Devices;

/// <summary>音频设备管理器。统一管理设备枚举、默认设备选择、后端工厂</summary>
public class AudioDeviceManager
{
    private readonly List<IAudioBackend> _backends = [];
    private IAudioBackend _activeBackend;

    /// <summary>已注册的后端列表</summary>
    public IReadOnlyList<IAudioBackend> Backends => _backends;

    /// <summary>当前活动后端</summary>
    public IAudioBackend ActiveBackend => _activeBackend;

    /// <summary>注册后端</summary>
    public void RegisterBackend(IAudioBackend backend)
    {
        if (backend == null) throw new ArgumentNullException(nameof(backend));
        _backends.Add(backend);
        _activeBackend ??= backend;
    }

    /// <summary>枚举所有设备</summary>
    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        if (_activeBackend == null) return Array.Empty<AudioDeviceInfo>();
        return _activeBackend.EnumerateDevices();
    }

    /// <summary>创建播放器</summary>
    public IAudioPlayer CreatePlayer()
    {
        if (_activeBackend == null) throw new InvalidOperationException("没有可用的音频后端");
        return _activeBackend.CreatePlayer();
    }

    /// <summary>创建录制器</summary>
    public IAudioRecorder CreateRecorder()
    {
        if (_activeBackend == null) throw new InvalidOperationException("没有可用的音频后端");
        return _activeBackend.CreateRecorder();
    }
}
