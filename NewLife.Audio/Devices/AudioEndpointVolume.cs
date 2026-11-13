using NewLife;

namespace NewLife.Audio.Devices;

/// <summary>音频端点音量控制</summary>
/// <remarks>
/// 通过 Windows Core Audio 的 <c>IAudioEndpointVolume</c> 读取/设置端点主音量与静音，
/// 效果等同于系统音量合成器里对该设备的调整，作用于送达该端点的全部声音，
/// 与只缩放本库播放数据的软件音量（<see cref="AudioPlayer.Volume"/>）不同。<br/>
/// 仅 Windows WASAPI 端点支持；端点 ID 与 <see cref="WasapiDriver.EnumerateDevices"/> 返回的一致。
/// </remarks>
/// <example>
/// <code>
/// using var volume = AudioEndpointVolume.Open(deviceId);
/// volume.Volume = 0.6f;
/// volume.Mute = false;
/// </code>
/// </example>
public sealed class AudioEndpointVolume : IDisposable
{
    #region 属性
    private IAudioEndpointVolume _volume;
    private Boolean _disposed;

    /// <summary>当前平台是否支持端点音量控制</summary>
    public static Boolean IsSupported => WasapiDriver.IsPlatformSupported;

    /// <summary>主音量标量，0~1</summary>
    public Single Volume
    {
        get
        {
            ThrowIfDisposed();

            var hr = _volume.GetMasterVolumeLevelScalar(out var value);
            if (hr != 0) throw new InvalidOperationException($"Failed to read the endpoint volume: {hr}");

            return value;
        }
        set
        {
            ThrowIfDisposed();

            var level = value;
            if (level < 0) level = 0;
            if (level > 1) level = 1;

            var hr = _volume.SetMasterVolumeLevelScalar(level, IntPtr.Zero);
            if (hr != 0) throw new InvalidOperationException($"Failed to set the endpoint volume: {hr}");
        }
    }

    /// <summary>是否静音</summary>
    public Boolean Mute
    {
        get
        {
            ThrowIfDisposed();

            var hr = _volume.GetMute(out var mute);
            if (hr != 0) throw new InvalidOperationException($"Failed to read the endpoint mute state: {hr}");

            return mute;
        }
        set
        {
            ThrowIfDisposed();

            var hr = _volume.SetMute(value, IntPtr.Zero);
            if (hr != 0) throw new InvalidOperationException($"Failed to set the endpoint mute state: {hr}");
        }
    }
    #endregion

    #region 构造
    /// <summary>打开指定端点的音量控制</summary>
    /// <param name="deviceId">端点 ID（WASAPI 端点 ID），为空时使用默认播放端点</param>
    /// <returns>端点音量控制实例</returns>
    /// <exception cref="PlatformNotSupportedException">非 Windows 平台</exception>
    /// <exception cref="InvalidOperationException">端点不存在或激活音量接口失败</exception>
    public static AudioEndpointVolume Open(String deviceId = null)
    {
        if (!IsSupported) throw new PlatformNotSupportedException(L.T("No audio backend is available."));

        var enumerator = WasapiInterop.CreateEnumerator();
        IMMDevice device = null;
        try
        {
            var hr = deviceId.IsNullOrEmpty()
                ? enumerator.GetDefaultAudioEndpoint(WasapiInterop.E_RENDER, WasapiInterop.E_CONSOLE, out device)
                : enumerator.GetDevice(deviceId, out device);

            if (hr != 0 || device == null) throw new InvalidOperationException($"Audio endpoint not found: {deviceId}");

            var iid = WasapiInterop.IID_IAudioEndpointVolume;
            hr = device.Activate(ref iid, WasapiInterop.CLSCTX_ALL, IntPtr.Zero, out var instance);
            if (hr != 0 || instance is not IAudioEndpointVolume volume)
                throw new InvalidOperationException($"Failed to activate the endpoint volume interface: {hr}");

            return new AudioEndpointVolume { _volume = volume };
        }
        finally
        {
            if (device != null) WasapiInterop.Release(device);
            WasapiInterop.Release(enumerator);
        }
    }
    #endregion

    #region 方法
    /// <summary>释放端点音量接口</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        WasapiInterop.Release(_volume);
        _volume = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AudioEndpointVolume));
    }
    #endregion
}
