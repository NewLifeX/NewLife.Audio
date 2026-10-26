using System;
using System.Runtime.InteropServices;

namespace NewLife.Audio.Devices;

/// <summary>音频设备变化类型</summary>
public enum AudioDeviceChange
{
    /// <summary>设备新增</summary>
    Added = 1,

    /// <summary>设备移除</summary>
    Removed = 2,

    /// <summary>设备状态变化（启用/禁用/拔出）</summary>
    StateChanged = 3,

    /// <summary>默认设备变化</summary>
    DefaultChanged = 4,
}

/// <summary>音频设备变化事件参数</summary>
public class AudioDeviceChangeEventArgs : EventArgs
{
    /// <summary>变化类型</summary>
    public AudioDeviceChange Change { get; init; }

    /// <summary>设备 ID（默认设备变化时为新默认设备）</summary>
    public String DeviceId { get; init; }

    /// <summary>是否为采集（输入）方向的默认设备变化</summary>
    public Boolean IsInput { get; init; }

    /// <summary>是否与默认设备相关</summary>
    public Boolean IsDefault { get; init; }

    /// <summary>新状态（<see cref="AudioDeviceChange.StateChanged"/> 时有值，1=活动、2=禁用、4=未插入、8=已拔出）</summary>
    public UInt32 NewState { get; init; }
}

/// <summary>音频设备热插拔观察器（Windows WASAPI 端点通知）</summary>
/// <remarks>
/// 通过 <c>IMMDeviceEnumerator.RegisterEndpointNotificationCallback</c> 注册 <c>IMMNotificationClient</c>，
/// 在设备新增/移除、状态变化与默认设备切换时触发 <see cref="Changed"/>。<br/>
/// 非 Windows 平台 <see cref="IsSupported"/> 为 false，构造与释放均为空操作，调用方可按能力分支处理。
/// 回调在 COM 线程上触发，事件处理器应尽快返回，避免阻塞系统音频通知。
/// </remarks>
/// <example>
/// <code>
/// using var watcher = new AudioDeviceWatcher();
/// watcher.Changed += (s, e) => Console.WriteLine($"{e.Change}: {e.DeviceId} 默认={e.IsDefault}");
/// </code>
/// </example>
public sealed class AudioDeviceWatcher : IDisposable
{
    private readonly Object _lock = new();
    private IMMDeviceEnumerator _enumerator;
    private NotificationClient _client;
    private IntPtr _callback;
    private Boolean _disposed;

    /// <summary>是否支持热插拔通知（仅 Windows）</summary>
    public Boolean IsSupported { get; }

    /// <summary>是否已注册成功</summary>
    public Boolean IsRegistered => _callback != IntPtr.Zero;

    /// <summary>设备变化事件（COM 线程触发）</summary>
    public event EventHandler<AudioDeviceChangeEventArgs> Changed;

    /// <summary>构造并注册设备通知</summary>
    public AudioDeviceWatcher()
    {
        IsSupported = AudioDriverFactory.IsWindows;
        if (!IsSupported) return;

        try
        {
            _enumerator = WasapiInterop.CreateEnumerator();
            _client = new NotificationClient(this);
            _callback = Marshal.GetComInterfaceForObject(_client, typeof(IMMNotificationClient));
            if (_enumerator.RegisterEndpointNotificationCallback(_callback) != 0) DisposeCallback();
        }
        catch
        {
            // 注册失败时退化为不支持，不影响调用方
            IsSupported = false;
            DisposeCallback();
        }
    }

    /// <summary>当前活动的渲染（输出）设备 ID 列表</summary>
    /// <returns>设备 ID 集合</returns>
    public String[] GetRenderDeviceIds() => WasapiInterop.GetActiveDeviceIds(input: false);

    /// <summary>当前活动的采集（输入）设备 ID 列表</summary>
    /// <returns>设备 ID 集合</returns>
    public String[] GetCaptureDeviceIds() => WasapiInterop.GetActiveDeviceIds(input: true);

    /// <summary>释放注册</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        if (_enumerator != null && _callback != IntPtr.Zero)
        {
            try
            {
                _enumerator.UnregisterEndpointNotificationCallback(_callback);
            }
            catch
            {
                // 忽略反注册失败
            }
        }

        DisposeCallback();
        Changed = null;
    }

    private void DisposeCallback()
    {
        if (_callback != IntPtr.Zero)
        {
            try
            {
                Marshal.Release(_callback);
            }
            catch
            {
                // 忽略释放失败
            }
            _callback = IntPtr.Zero;
        }

        _client = null;
        _enumerator = null;
    }

    private void Raise(AudioDeviceChangeEventArgs args)
    {
        if (_disposed) return;

        try
        {
            Changed?.Invoke(this, args);
        }
        catch
        {
            // 订阅方异常不影响系统通知线程
        }
    }

    /// <summary>IMMNotificationClient 托管实现（作为 COM 回调包装对象）</summary>
    [ComVisible(true)]
    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly AudioDeviceWatcher _owner;

        public NotificationClient(AudioDeviceWatcher owner) => _owner = owner;

        public Int32 OnDeviceStateChanged(String deviceId, UInt32 newState)
        {
            _owner.Raise(new AudioDeviceChangeEventArgs
            {
                Change = AudioDeviceChange.StateChanged,
                DeviceId = deviceId,
                NewState = newState,
            });

            return 0;
        }

        public Int32 OnDeviceAdded(String deviceId)
        {
            _owner.Raise(new AudioDeviceChangeEventArgs { Change = AudioDeviceChange.Added, DeviceId = deviceId });

            return 0;
        }

        public Int32 OnDeviceRemoved(String deviceId)
        {
            _owner.Raise(new AudioDeviceChangeEventArgs { Change = AudioDeviceChange.Removed, DeviceId = deviceId });

            return 0;
        }

        public Int32 OnDefaultDeviceChanged(Int32 flow, Int32 role, String defaultDeviceId)
        {
            _owner.Raise(new AudioDeviceChangeEventArgs
            {
                Change = AudioDeviceChange.DefaultChanged,
                DeviceId = defaultDeviceId,
                IsDefault = true,
                IsInput = flow == 1,
            });

            return 0;
        }

        public Int32 OnPropertyValueChanged(String deviceId, PropertyKey key)
        {
            // 属性变化（如友好名称）不单独上报，避免噪音
            return 0;
        }
    }
}


