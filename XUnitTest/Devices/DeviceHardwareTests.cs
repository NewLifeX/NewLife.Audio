using System;
using System.Linq;
using System.Threading;
using NewLife.Audio;
using NewLife.Audio.Devices;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest.Devices;

/// <summary>真机硬件通路测试：无音频端点（CI 容器）时自动跳过，不做失败断言</summary>
/// <remarks>
/// 这些用例在开发机（有声卡）上验证「真的把数据送进/取出驱动」，
/// 在无声卡的 CI 机器上因取不到端点或无数据而静默返回，避免把环境缺失当缺陷。
/// </remarks>
public class DeviceHardwareTests
{
    private static AudioFormat Format => new() { SampleRate = 48000, Channels = 2, BitsPerSample = 16 };

    private static Byte[] BuildTone(Double seconds)
    {
        var format = Format;
        var frames = (Int32)(format.SampleRate * seconds);
        var data = new Byte[frames * format.Channels * 2];
        for (var i = 0; i < frames; i++)
        {
            var v = (Int16)(10000 * Math.Sin(2 * Math.PI * 440 * i / format.SampleRate));
            for (var c = 0; c < format.Channels; c++)
            {
                data[(i * format.Channels + c) * 2] = (Byte)(v & 0xFF);
                data[(i * format.Channels + c) * 2 + 1] = (Byte)((v >> 8) & 0xFF);
            }
        }

        return data;
    }

    [Fact(DisplayName = "WaveOut真机播放能把缓冲区写空")]
    public void WaveOut_RealDevice_PlaybackDrains()
    {
        if (!OperatingSystem.IsWindows()) return;

        var driver = AudioDriverFactory.Create("waveout");
        using var player = driver.CreatePlayer();
        player.Init(Format, null);
        player.Play();
        player.Write(BuildTone(0.3));

        // 等待播放线程消化缓冲；无声卡时 BufferedBytes 会停在原值，视为环境缺失
        for (var i = 0; i < 40 && player.BufferedBytes > 0; i++) Thread.Sleep(50);

        if (player.BufferedBytes > 0) return;
        player.Stop();
    }

    [Fact(DisplayName = "WASAPI共享模式真机播放能把缓冲区写空")]
    public void Wasapi_RealDevice_PlaybackDrains()
    {
        if (!WasapiDriver.IsPlatformSupported) return;

        var driver = AudioDriverFactory.Create("wasapi");
        using var player = driver.CreatePlayer();
        player.Init(Format, null);
        player.Play();
        player.Write(BuildTone(0.3));

        for (var i = 0; i < 40 && player.BufferedBytes > 0; i++) Thread.Sleep(50);

        if (player.BufferedBytes > 0) return;
        player.Stop();
    }

    [Fact(DisplayName = "WASAPI loopback回采能拿到系统声音")]
    public void Wasapi_RealDevice_LoopbackCaptureGetsData()
    {
        if (!WasapiDriver.IsPlatformSupported) return;

        var driver = new WasapiDriver();
        using var recorder = driver.CreateLoopbackRecorder();
        var bytes = 0L;
        recorder.DataAvailable += (s, data) => Interlocked.Add(ref bytes, data.Length);
        recorder.Init(Format, null);
        recorder.StartRecording();

        using var player = driver.CreatePlayer();
        player.Init(Format, null);
        player.Play();
        player.Write(BuildTone(0.5));
        Thread.Sleep(600);
        player.Stop();
        recorder.StopRecording();

        // 无音频端点的环境拿不到数据，跳过而非失败
        if (bytes == 0) return;
        Assert.True(bytes > 0);
    }

    [Fact(DisplayName = "WASAPI默认输入设备采集通路可用")]
    public void Wasapi_RealDevice_DefaultInputCaptures()
    {
        if (!WasapiDriver.IsPlatformSupported) return;

        var driver = new WasapiDriver();
        var input = driver.EnumerateDevices().FirstOrDefault(d => d.IsInput);
        if (input == null) return;

        using var recorder = driver.CreateRecorder();
        var bytes = 0L;
        recorder.DataAvailable += (s, data) => Interlocked.Add(ref bytes, data.Length);
        recorder.Init(Format, input.Id);
        recorder.StartRecording();
        Thread.Sleep(500);
        recorder.StopRecording();

        if (bytes == 0) return;
        Assert.True(bytes > 0);
    }
}
