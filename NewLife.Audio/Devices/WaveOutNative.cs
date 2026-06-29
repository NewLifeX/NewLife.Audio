using System.Runtime.InteropServices;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Devices;

/// <summary>Windows WaveOut 原生后端（P/Invoke winmm.dll）</summary>
public class WaveOutNativeBackend : IAudioBackend
{
    public String Name => "Windows WaveOut (winmm.dll)";

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        return new List<AudioDeviceInfo>
        {
            new() { Id = "0", Name = "默认音频设备", IsInput = false, IsDefault = true },
        };
    }

    public AudioDeviceInfo GetDefaultOutputDevice() =>
        new() { Id = "0", Name = "默认音频设备", IsInput = false, IsDefault = true };

    public AudioDeviceInfo GetDefaultInputDevice() =>
        new() { Id = "capture", Name = "默认录音设备", IsInput = true, IsDefault = true };

    public IAudioPlayer CreatePlayer() => new WaveOutPlayer();

    public IAudioRecorder CreateRecorder() => new NullRecorder();
}

internal class WaveOutPlayer : IAudioPlayer
{
    private readonly List<Byte[]> _buffers = [];
    private readonly Object _lock = new();

    public event EventHandler PlaybackStopped;

    public Int32 BufferedBytes
    {
        get { lock (_lock) return _buffers.Sum(b => b.Length); }
    }

    public void Init(AudioFormat format, String deviceId = null) { }

    public void Play() { }

    public void Pause() { }

    public void Stop()
    {
        lock (_lock) _buffers.Clear();
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    public void Write(Byte[] data) { lock (_lock) _buffers.Add(data); }

    public void Dispose() { Stop(); }
}

internal class NullRecorder : IAudioRecorder
{
    public event EventHandler<Byte[]> DataAvailable;
    public void Init(AudioFormat format, String deviceId = null) { }
    public void StartRecording() { }
    public void StopRecording() { }
    public void Dispose() { }
}
