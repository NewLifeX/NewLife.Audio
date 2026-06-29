using System.Runtime.InteropServices;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Devices;

/// <summary>Windows WASAPI 后端（Windows Audio Session API）</summary>
public class WasapiBackend : IAudioBackend
{
    public String Name => "Windows WASAPI";

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        return new List<AudioDeviceInfo>
        {
            new() { Id = "wasapi_default", Name = "WASAPI 默认输出", IsDefault = true },
        };
    }

    public AudioDeviceInfo GetDefaultOutputDevice() =>
        new() { Id = "wasapi_default", Name = "WASAPI 默认输出", IsDefault = true };

    public AudioDeviceInfo GetDefaultInputDevice() =>
        new() { Id = "wasapi_capture", Name = "WASAPI 默认输入", IsInput = true, IsDefault = true };

    public IAudioPlayer CreatePlayer() => new WaveOutPlayer();
    public IAudioRecorder CreateRecorder() => new NullRecorder();
}
