using System;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest.DSP;

/// <summary>EBU R128 响度测量与归一化测试</summary>
public class LoudnessTests
{
    private static readonly AudioFormat Mono48 = new() { SampleRate = 48000, Channels = 1, BitsPerSample = 16 };
    private static readonly AudioFormat Stereo48 = new() { SampleRate = 48000, Channels = 2, BitsPerSample = 16 };

    [Fact(DisplayName = "1kHz正弦响度符合BS.1770理论值")]
    public void Measure_Sine_MatchesTheory()
    {
        var pcm = BuildSine(Mono48, 1000, 0.5, 3.0);

        var lufs = LoudnessNormalizer.Measure(pcm, Mono48);

        // 幅度 0.5 → 均方 0.125 → -9.03 dB，K 加权在 1kHz 约 0dB
        Assert.InRange(lufs, -10.7, -8.7);
    }

    [Fact(DisplayName = "幅度十倍差异对应20dB响度差")]
    public void Measure_AmplitudeScaling()
    {
        var loud = LoudnessNormalizer.Measure(BuildSine(Mono48, 1000, 0.5, 2.0), Mono48);
        var quiet = LoudnessNormalizer.Measure(BuildSine(Mono48, 1000, 0.05, 2.0), Mono48);

        Assert.InRange(loud - quiet, 19.5, 20.5);
    }

    [Fact(DisplayName = "双声道同信号比单声道高约3dB")]
    public void Measure_StereoSum()
    {
        var mono = LoudnessNormalizer.Measure(BuildSine(Mono48, 1000, 0.5, 2.0), Mono48);
        var stereo = LoudnessNormalizer.Measure(BuildSine(Stereo48, 1000, 0.5, 2.0), Stereo48);

        Assert.InRange(stereo - mono, 2.5, 3.5);
    }

    [Fact(DisplayName = "归一化到目标响度误差小于1LU")]
    public void Normalize_ReachesTarget()
    {
        var pcm = BuildSine(Mono48, 1000, 0.5, 3.0);
        var output = LoudnessNormalizer.Normalize(pcm, Mono48, new LoudnessOptions { TargetLufs = -20, TruePeakCeiling = 0 });

        var lufs = LoudnessNormalizer.Measure(output, Mono48);
        Assert.InRange(lufs, -21, -19);
    }

    [Fact(DisplayName = "归一化遵守真峰值上限")]
    public void Normalize_RespectsTruePeak()
    {
        var pcm = BuildSine(Mono48, 1000, 0.99, 3.0);
        var output = LoudnessNormalizer.Normalize(pcm, Mono48, new LoudnessOptions { TargetLufs = -6, TruePeakCeiling = -1 });

        var peak = 0;
        for (var i = 0; i < output.Length / 2; i++)
        {
            var value = Math.Abs((Int32)(Int16)(output[i * 2] | output[i * 2 + 1] << 8));
            if (value > peak) peak = value;
        }

        Assert.True(peak <= (Int32)(32767 * Math.Pow(10, -1.0 / 20)), $"峰值超限：{peak}");
    }

    [Fact(DisplayName = "静音返回负无穷且归一化原样返回")]
    public void Silence_HandledGracefully()
    {
        var pcm = new Byte[48000 * 2];

        Assert.True(Double.IsNegativeInfinity(LoudnessNormalizer.Measure(pcm, Mono48)));

        var output = LoudnessNormalizer.Normalize(pcm, Mono48);
        Assert.Equal(pcm.Length, output.Length);
        Assert.All(output, b => Assert.Equal(0, b));
    }

    [Fact(DisplayName = "流式测量输出瞬时与短时响度")]
    public void Meter_StreamingReportsMomentaryAndShortTerm()
    {
        var pcm = BuildSine(Mono48, 1000, 0.5, 3.5);
        using var meter = new LoudnessMeter(Mono48);

        // 分块喂入，验证跨块累积
        for (var offset = 0; offset < pcm.Length; offset += 960 * 2)
        {
            var count = Math.Min(960 * 2, pcm.Length - offset);
            meter.Process(pcm.AsSpan(offset, count));
        }

        Assert.False(Double.IsNegativeInfinity(meter.Momentary));
        Assert.False(Double.IsNegativeInfinity(meter.ShortTerm));
        Assert.InRange(meter.ShortTerm, -10.7, -8.7);
        Assert.True(meter.TruePeak > -10);
        Assert.True(meter.SampleCount >= 48000 * 3);
    }

    #region 辅助
    private static Byte[] BuildSine(AudioFormat format, Double frequency, Double amplitude, Double seconds)
    {
        var samples = (Int32)(format.SampleRate * seconds);
        var data = new Byte[samples * format.Channels * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (Int16)(Math.Sin(2 * Math.PI * frequency * i / format.SampleRate) * amplitude * 32767);
            for (var ch = 0; ch < format.Channels; ch++)
            {
                var offset = (i * format.Channels + ch) * 2;
                data[offset] = (Byte)(v & 0xFF);
                data[offset + 1] = (Byte)((v >> 8) & 0xFF);
            }
        }

        return data;
    }
    #endregion
}
