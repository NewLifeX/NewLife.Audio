using System;
using System.Threading;
using NewLife.Audio.DSP;
using NewLife.Audio.Devices;
using Xunit;

namespace XUnitTest.Devices;

public class LoopbackRecorderTests
{
    [Fact(DisplayName = "环路录制：播放正弦同时回采到非静音数据")]
    public void LoopbackRecorder_CapturesPlayback()
    {
        if (!WasapiDriver.IsPlatformSupported) return;

        var driver = new WasapiDriver();
        if (driver.GetDefaultOutputDevice() == null) return;

        var format = new AudioFormat { SampleRate = 48000, Channels = 2, BitsPerSample = 16 };

        // 构造 1 秒 1 kHz 正弦（双声道）
        var samples = format.SampleRate * 2;
        var pcm = new Byte[samples * format.Channels * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (Int16)(Math.Sin(2 * Math.PI * 1000 * i / format.SampleRate) * 0.4 * 32000);
            for (var ch = 0; ch < format.Channels; ch++)
            {
                var p = (i * format.Channels + ch) * 2;
                pcm[p] = (Byte)(v & 0xFF);
                pcm[p + 1] = (Byte)((v >> 8) & 0xFF);
            }
        }

        using var player = driver.CreatePlayer();
        using var loopback = driver.CreateLoopbackRecorder();

        var captured = 0L;
        var energy = 0.0;
        var count = 0L;
        loopback.DataAvailable += (s, data) =>
        {
            captured += data.Length;
            for (var i = 0; i + 1 < data.Length; i += 2)
            {
                var v = (Int16)(data[i] | data[i + 1] << 8) / 32768.0;
                energy += v * v;
                count++;
            }
        };

        player.Init(format);
        loopback.Init(format);
        loopback.StartRecording();

        player.Write(pcm);
        player.Play();
        Thread.Sleep(600);
        player.Stop();
        Thread.Sleep(200);
        loopback.StopRecording();

        if (captured == 0) return;   // 无渲染流量（远程会话静音）时不做断言

        var rms = Math.Sqrt(energy / Math.Max(1, count)) * 32768;
        Assert.True(rms > 100, $"回采信号过弱：rms={rms:F0}");
    }

    [Fact(DisplayName = "环路录制器初始化后处于未录制状态")]
    public void LoopbackRecorder_Init_NotRecording()
    {
        using var recorder = new LoopbackRecorder();
        recorder.Init(AudioFormat.Default);

        Assert.False(recorder.IsRecording);
        Assert.Equal(AudioFormat.Default.SampleRate, recorder.Format.SampleRate);
    }

    [Fact(DisplayName = "环路录制器启停：有渲染端点则可用，无后端则明确报不支持")]
    public void LoopbackRecorder_StartStop()
    {
        using var recorder = new LoopbackRecorder();
        recorder.Init(AudioFormat.Default);

        if (!LoopbackRecorder.IsSupported)
        {
            Assert.Throws<PlatformNotSupportedException>(() => recorder.StartRecording());
            return;
        }

        try
        {
            recorder.StartRecording();
        }
        catch (InvalidOperationException)
        {
            // 运行环境没有可用渲染端点，明确报错符合预期
            return;
        }

        Assert.True(recorder.IsRecording);
        recorder.StopRecording();
        Assert.False(recorder.IsRecording);
    }
}

