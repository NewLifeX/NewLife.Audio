using System;
using NewLife.Audio.Devices;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest.Devices;

public class AlsaDriverTests
{
    [Fact(DisplayName = "ALSA后端只在Linux且能加载libasound时可用")]
    public void Alsa_Availability_MatchesPlatform()
    {
        if (!AlsaDriver.IsLinux)
        {
            Assert.False(AlsaDriver.IsAvailable);

            var driver = new AlsaDriver();
            Assert.Equal("alsa", driver.Name);
            Assert.Empty(driver.EnumerateDevices());
            Assert.Null(driver.GetDefaultOutputDevice());
            return;
        }

        // Linux 上是否可用取决于运行环境是否安装 libasound，两种情况都不能抛异常
        var linux = new AlsaDriver();
        var devices = linux.EnumerateDevices();
        Assert.NotNull(devices);
        if (!AlsaDriver.IsAvailable) return;

        Assert.NotNull(linux.GetDefaultOutputDevice());
        Assert.NotNull(linux.GetDefaultInputDevice());
        Assert.Equal("default", linux.GetDefaultInputDevice().Id);
    }

    [Fact(DisplayName = "ALSA未在Linux运行时创建设备明确报告不支持")]
    public void Alsa_Init_OutsideLinux_Throws()
    {
        if (AlsaDriver.IsAvailable) return;

        using var player = new AlsaDriver().CreatePlayer();
        using var recorder = new AlsaDriver().CreateRecorder();

        var format = new AudioFormat { SampleRate = 48000, Channels = 2, BitsPerSample = 16 };

        Assert.ThrowsAny<NotSupportedException>(() => player.Init(format));
        Assert.ThrowsAny<NotSupportedException>(() => recorder.Init(format));
    }

    [Fact(DisplayName = "ALSA仅支持16位有符号PCM")]
    public void Alsa_Init_RejectsUnsupportedFormat()
    {
        using var player = new AlsaDriver().CreatePlayer();

        var format = new AudioFormat { SampleRate = 48000, Channels = 2, BitsPerSample = 24 };
        var ex = Assert.ThrowsAny<NotSupportedException>(() => player.Init(format));
        Assert.Contains("24", ex.Message);
    }

    [Fact(DisplayName = "无ALSA时工厂仍返回可诊断的驱动")]
    public void Factory_WithoutAlsa_StaysDiagnosable()
    {
        if (AudioDriverFactory.IsWindows || AlsaDriver.IsAvailable) return;

        Assert.False(AudioDriverFactory.IsSupported);
        Assert.Empty(AudioDriverFactory.GetAvailableBackends());

        var driver = AudioDriverFactory.Create("alsa");
        Assert.Throws<PlatformNotSupportedException>(() => driver.CreatePlayer());
    }

    [Fact(DisplayName = "ALSA null 设备能播放并把缓冲写空")]
    public void Alsa_NullDevice_PlaybackDrains()
    {
        Assert.SkipUnless(AlsaDriver.IsAvailable, "本机不可用 ALSA");

        var format = new AudioFormat { SampleRate = 48000, Channels = 2, BitsPerSample = 16 };
        var driver = new AlsaDriver();
        using var player = driver.CreatePlayer();
        player.Init(format, "null");
        player.Play();

        var frames = format.SampleRate / 4;
        var data = new Byte[frames * format.Channels * 2];
        for (var i = 0; i < frames; i++)
        {
            var v = (Int16)(8000 * Math.Sin(2 * Math.PI * 440 * i / format.SampleRate));
            for (var c = 0; c < format.Channels; c++)
            {
                data[(i * format.Channels + c) * 2] = (Byte)(v & 0xFF);
                data[(i * format.Channels + c) * 2 + 1] = (Byte)((v >> 8) & 0xFF);
            }
        }

        player.Write(data);
        for (var i = 0; i < 40 && player.BufferedBytes > 0; i++) System.Threading.Thread.Sleep(50);
        var left = player.BufferedBytes;
        player.Stop();

        Assert.Equal(0, left);
    }

    [Fact(DisplayName = "ALSA null 设备能走通采集读取与事件通路")]
    public void Alsa_NullDevice_CaptureRaisesData()
    {
        Assert.SkipUnless(AlsaDriver.IsAvailable, "本机不可用 ALSA");

        var format = new AudioFormat { SampleRate = 48000, Channels = 2, BitsPerSample = 16 };
        var driver = new AlsaDriver();
        using var recorder = driver.CreateRecorder();
        var bytes = 0L;
        recorder.DataAvailable += (s, data) => System.Threading.Interlocked.Add(ref bytes, data.Length);

        recorder.Init(format, "null");
        recorder.StartRecording();
        System.Threading.Thread.Sleep(200);
        recorder.StopRecording();

        Assert.True(bytes > 0, "null 设备应当产生静音数据流");
    }
}
