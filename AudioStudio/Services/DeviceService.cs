using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NewLife.Audio;
using NewLife.Audio.Devices;
using NewLife.Audio.DSP;

namespace AudioStudio.Services;

/// <summary>音频设备管理服务</summary>
public class DeviceService : IDisposable
{
    private readonly AudioDeviceManager _manager;
    private IAudioBackend? _backend;
    private IAudioPlayer? _player;
    private IAudioRecorder? _recorder;
    private Boolean _disposed;

    /// <summary>设备列表</summary>
    public IReadOnlyList<AudioDeviceInfo> Devices => _devices;
    private List<AudioDeviceInfo> _devices = [];

    /// <summary>输出设备列表</summary>
    public IReadOnlyList<AudioDeviceInfo> OutputDevices { get; private set; } = [];

    /// <summary>输入设备列表</summary>
    public IReadOnlyList<AudioDeviceInfo> InputDevices { get; private set; } = [];

    /// <summary>当前后端名称</summary>
    public String BackendName => _backend?.Name ?? "未初始化";

    /// <summary>是否在播放测试音</summary>
    public Boolean IsPlaying { get; private set; }

    /// <summary>是否在录制</summary>
    public Boolean IsRecording { get; private set; }

    /// <summary>录制数据可用事件</summary>
    public event EventHandler<Byte[]>? RecordingDataAvailable;

    public DeviceService()
    {
        _manager = new AudioDeviceManager();
    }

    /// <summary>初始化并枚举设备</summary>
    public void Initialize(String? backendName = null)
    {
        try
        {
            _backend = backendName != null
                ? AudioBackendFactory.Create(backendName)
                : AudioBackendFactory.Create();

            _manager.RegisterBackend(_backend);
            RefreshDevices();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"设备初始化失败: {ex.Message}");
        }
    }

    /// <summary>刷新设备列表</summary>
    public void RefreshDevices()
    {
        try
        {
            _devices = _manager.EnumerateDevices().ToList();
            OutputDevices = _devices.Where(d => !d.IsInput).ToList();
            InputDevices = _devices.Where(d => d.IsInput).ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"枚举设备失败: {ex.Message}");
            _devices = [];
            OutputDevices = [];
            InputDevices = [];
        }
    }

    /// <summary>获取默认输出设备</summary>
    public AudioDeviceInfo? GetDefaultOutput() =>
        _backend?.GetDefaultOutputDevice();

    /// <summary>获取默认输入设备</summary>
    public AudioDeviceInfo? GetDefaultInput() =>
        _backend?.GetDefaultInputDevice();

    /// <summary>播放测试音（正弦波）</summary>
    /// <param name="deviceId">设备 ID，null 用默认</param>
    /// <param name="frequency">频率 Hz</param>
    /// <param name="durationMs">持续时间毫秒</param>
    /// <param name="volume">音量 0.0~1.0</param>
    public void PlayTestTone(String? deviceId = null,
        Double frequency = 440, Int32 durationMs = 1000, Double volume = 0.3)
    {
        StopTestTone();

        try
        {
            _player = _manager.CreatePlayer();
            var format = new AudioFormat
            {
                SampleRate = 44100,
                Channels = 2,
                BitsPerSample = 16,
            };
            _player.Init(format, deviceId);

            var sampleCount = (Int32)(format.SampleRate * durationMs / 1000.0);
            var buffer = new Byte[sampleCount * format.Channels * 2]; // 16-bit

            for (var i = 0; i < sampleCount; i++)
            {
                var value = (Int16)(Math.Sin(2 * Math.PI * frequency * i / format.SampleRate)
                    * Int16.MaxValue * volume);
                var offset = i * format.Channels * 2;
                buffer[offset] = (Byte)(value & 0xFF);
                buffer[offset + 1] = (Byte)((value >> 8) & 0xFF);
                if (format.Channels >= 2)
                {
                    buffer[offset + 2] = buffer[offset];
                    buffer[offset + 3] = buffer[offset + 1];
                }
            }

            _player.Write(buffer);
            _player.Play();
            IsPlaying = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"播放测试音失败: {ex.Message}");
        }
    }

    /// <summary>停止测试音</summary>
    public void StopTestTone()
    {
        IsPlaying = false;
        if (_player != null)
        {
            try { _player.Stop(); } catch { }
            try { _player.Dispose(); } catch { }
            _player = null;
        }
    }

    /// <summary>开始录制</summary>
    /// <param name="deviceId">设备 ID</param>
    /// <param name="format">录制格式</param>
    public void StartRecording(String? deviceId = null, AudioFormat? format = null)
    {
        StopRecording();

        try
        {
            _recorder = _manager.CreateRecorder();
            var fmt = format ?? new AudioFormat
            {
                SampleRate = 16000,
                Channels = 1,
                BitsPerSample = 16,
            };
            _recorder.Init(fmt, deviceId);
            _recorder.DataAvailable += OnDataAvailable;
            _recorder.StartRecording();
            IsRecording = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"开始录制失败: {ex.Message}");
        }
    }

    /// <summary>停止录制</summary>
    public void StopRecording()
    {
        IsRecording = false;

        if (_loopbackRecorder != null)
        {
            try { _loopbackRecorder.StopRecording(); } catch { }
            try { _loopbackRecorder.Dispose(); } catch { }
            _loopbackRecorder = null;
        }

        if (_recorder != null)
        {
            try { _recorder.StopRecording(); } catch { }
            try { _recorder.Dispose(); } catch { }
            _recorder = null;
        }
    }

    private LoopbackRecorder? _loopbackRecorder;

    /// <summary>开始环路录制（系统音频输出捕获）</summary>
    /// <param name="format">音频格式</param>
    public void StartLoopbackRecording(AudioFormat format)
    {
        StopRecording();

        try
        {
            _loopbackRecorder = new LoopbackRecorder();
            _loopbackRecorder.Init(format);
            _loopbackRecorder.DataAvailable += OnDataAvailable;
            _loopbackRecorder.StartRecording();
            IsRecording = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"开始环路录制失败: {ex.Message}");
        }
    }

    private void OnDataAvailable(Object? sender, Byte[] data)
    {
        RecordingDataAvailable?.Invoke(this, data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTestTone();
        StopRecording();
        GC.SuppressFinalize(this);
    }
}
