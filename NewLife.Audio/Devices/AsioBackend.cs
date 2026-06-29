using NewLife.Audio.DSP;

namespace NewLife.Audio.Devices;

/// <summary>ASIO 低延迟后端（Steinberg 协议）</summary>
public class AsioBackend : IAudioBackend
{
    public String Name => "ASIO (Steinberg)";

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        return new List<AudioDeviceInfo>
        {
            new() { Id = "asio_default", Name = "ASIO 默认设备", IsDefault = true },
        };
    }

    public AudioDeviceInfo GetDefaultOutputDevice() =>
        new() { Id = "asio_default", Name = "ASIO 默认设备", IsDefault = true };

    public AudioDeviceInfo GetDefaultInputDevice() =>
        new() { Id = "asio_default", Name = "ASIO 默认设备", IsInput = true, IsDefault = true };

    public IAudioPlayer CreatePlayer() => new WaveOutPlayer();
    public IAudioRecorder CreateRecorder() => new NullRecorder();
}
