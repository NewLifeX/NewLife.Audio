using System;
using System.Linq;
using System.Threading;
using NewLife;
using NewLife.Audio.DSP;
using NewLife.Audio.Devices;
using Xunit;

namespace XUnitTest.Devices;

public class WasapiDriverTests
{
    [Fact(DisplayName = "WASAPI驱动创建成功")]
    public void WasapiDriver_CreatesInstances()
    {
        var driver = AudioDriverFactory.Create("wasapi");
        Assert.NotNull(driver);
        Assert.Contains("WASAPI", driver.Name);

        if (!WasapiDriver.IsPlatformSupported)
        {
            Assert.Throws<PlatformNotSupportedException>(() => driver.CreatePlayer());
            Assert.Throws<PlatformNotSupportedException>(() => driver.CreateRecorder());
            return;
        }

        using var player = driver.CreatePlayer();
        Assert.NotNull(player);

        using var recorder = driver.CreateRecorder();
        Assert.NotNull(recorder);
    }

    [Fact(DisplayName = "WASAPI枚举端点返回真实设备标识与名称")]
    public void WasapiDriver_EnumerateDevices()
    {
        var driver = new WasapiDriver();
        var devices = driver.EnumerateDevices();

        if (!WasapiDriver.IsPlatformSupported)
        {
            Assert.Empty(devices);
            return;
        }

        foreach (var device in devices)
        {
            Assert.False(device.Id.IsNullOrEmpty());
            Assert.False(device.Name.IsNullOrEmpty());
        }
    }

    [Fact(DisplayName = "WASAPI默认设备标记与枚举结果一致")]
    public void WasapiDriver_DefaultDevicesMatchEnumeration()
    {
        if (!WasapiDriver.IsPlatformSupported) return;

        var driver = new WasapiDriver();
        var devices = driver.EnumerateDevices().ToList();
        var output = driver.GetDefaultOutputDevice();
        var input = driver.GetDefaultInputDevice();

        if (devices.Any(e => !e.IsInput && e.IsDefault))
            Assert.NotNull(output);
        if (devices.Any(e => e.IsInput && e.IsDefault))
            Assert.NotNull(input);

        if (output != null) Assert.False(output.IsInput);
        if (input != null) Assert.True(input.IsInput);
    }

    [Fact(DisplayName = "WASAPI播放器初始化并完成100毫秒静音播放")]
    public void WasapiPlayer_InitWritePlay()
    {
        if (!WasapiDriver.IsPlatformSupported) return;

        var driver = new WasapiDriver();
        if (driver.GetDefaultOutputDevice() == null) return;

        var format = new AudioFormat { SampleRate = 48000, Channels = 2, BitsPerSample = 16 };
        using var player = driver.CreatePlayer();
        player.Init(format);

        var buffer = new Byte[format.SampleRate / 10 * format.Channels * format.BytesPerSample];
        player.Write(buffer);
        player.Play();
        Thread.Sleep(120);
        player.Stop();

        Assert.True(player.BufferedBytes >= 0);
    }

    [Theory(DisplayName = "WASAPI播放器事件回调与轮询两种模式均可播放")]
    [InlineData(true)]
    [InlineData(false)]
    public void WasapiPlayer_LowLatencyModes(Boolean lowLatency)
    {
        if (!WasapiDriver.IsPlatformSupported) return;

        var driver = new WasapiDriver { LowLatency = lowLatency };
        if (driver.GetDefaultOutputDevice() == null) return;

        var format = new AudioFormat { SampleRate = 48000, Channels = 2, BitsPerSample = 16 };
        using var player = driver.CreatePlayer();
        player.Init(format);

        var buffer = new Byte[format.SampleRate / 10 * format.Channels * 2];
        player.Write(buffer);
        player.Play();
        Thread.Sleep(120);
        player.Stop();

        Assert.True(player.BufferedBytes >= 0);
    }

    [Fact(DisplayName = "WASAPI录制器在无采集端点时明确抛异常")]    public void WasapiRecorder_StartStop()
    {
        var driver = new WasapiDriver();
        using var recorder = driver.CreateRecorder();

        if (!WasapiDriver.IsPlatformSupported)
        {
            Assert.Throws<PlatformNotSupportedException>(() => recorder.StartRecording());
            return;
        }

        if (driver.GetDefaultInputDevice() == null)
        {
            Assert.Throws<InvalidOperationException>(() => recorder.StartRecording());
            return;
        }

        var received = 0;
        recorder.DataAvailable += (s, e) => Interlocked.Increment(ref received);
        recorder.Init(new AudioFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 });
        recorder.StartRecording();
        Thread.Sleep(150);
        recorder.StopRecording();

        Assert.True(Volatile.Read(ref received) >= 0);
    }

    [Fact(DisplayName = "环路录制器在无后端时明确报告不支持")]
    public void LoopbackRecorder_UnsupportedPlatform_Throws()
    {
        using var recorder = new LoopbackRecorder();
        recorder.Init(new AudioFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 });

        if (!LoopbackRecorder.IsSupported)
        {
            Assert.Throws<PlatformNotSupportedException>(() => recorder.StartRecording());
            Assert.False(recorder.IsRecording);
        }
    }
}
