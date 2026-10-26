using System;
using NewLife.Audio;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest;

/// <summary>PCM 无损内容操作测试：裁剪、拼接、增益、重采样</summary>
public class AudioEditTests
{
    private static readonly AudioFormat Mono = new() { SampleRate = 16000, Channels = 1, BitsPerSample = 16 };
    private static readonly AudioFormat Stereo = new() { SampleRate = 16000, Channels = 2, BitsPerSample = 16 };

    [Fact(DisplayName = "裁剪按采样点取子区间")]
    public void Trim_Samples()
    {
        var audio = Build(Mono, 1000, 100);

        var trimmed = audio.Trim(100, 200);

        Assert.Equal(200 * 2, trimmed.Data.Length);
        Assert.Equal(200, Sample(trimmed, 0));
        Assert.Equal(399, Sample(trimmed, 199));
        Assert.Equal(1000, audio.Data.Length / 2);
    }

    [Fact(DisplayName = "裁剪按时间取子区间并夹取越界")]
    public void Trim_Time()
    {
        var audio = Build(Mono, 16000, 100);

        var trimmed = audio.Trim(TimeSpan.FromSeconds(0.25), TimeSpan.FromSeconds(0.5));
        Assert.Equal(8000 * 2, trimmed.Data.Length);
        Assert.Equal(4100, Sample(trimmed, 0));

        var overrun = audio.Trim(TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(1));
        Assert.Equal(1600 * 2, overrun.Data.Length);
    }

    [Fact(DisplayName = "裁剪越界参数被夹取")]
    public void Trim_OutOfRange_Clamped()
    {
        var audio = Build(Mono, 100, 0);

        Assert.Equal(50 * 2, audio.Trim(50, 1000).Data.Length);
        Assert.Empty(audio.Trim(500, 10).Data);
        Assert.Equal(200, audio.Trim(-10, 0).Data.Length);
    }

    [Fact(DisplayName = "拼接保持顺序与数据")]
    public void Concat_Appends()
    {
        var first = Build(Mono, 100, 0);
        var second = Build(Mono, 50, 500);

        var result = first.Concat(second);

        Assert.Equal(300, result.Data.Length);
        Assert.Equal(0, Sample(result, 0));
        Assert.Equal(99, Sample(result, 99));
        Assert.Equal(500, Sample(result, 100));
    }

    [Fact(DisplayName = "拼接格式不一致明确报错")]
    public void Concat_FormatMismatch_Throws()
    {
        var mono = Build(Mono, 10, 0);
        var stereo = Build(Stereo, 10, 0);

        Assert.Throws<ArgumentException>(() => mono.Concat(stereo));
    }

    [Fact(DisplayName = "增益按分贝缩放并限幅")]
    public void ApplyGain_ScalesAndClips()
    {
        var audio = Build(Mono, 4, 1000);

        var louder = audio.ApplyGain(6);
        Assert.Equal((Int32)Math.Round(1001 * Math.Pow(10, 6 / 20.0)), Sample(louder, 1));

        var quieter = audio.ApplyGain(-6);
        Assert.Equal((Int32)Math.Round(1001 * Math.Pow(10, -6 / 20.0)), Sample(quieter, 1));

        var clipped = Build(Mono, 2, 30000).ApplyGain(20);
        Assert.Equal(Int16.MaxValue, Sample(clipped, 0));
    }

    [Fact(DisplayName = "峰值归一化把最大样本拉到目标比例")]
    public void NormalizePeak_Scales()
    {
        var audio = Build(Mono, 3, 5000);

        var normalized = audio.NormalizePeak(0.5);

        var peak = 0;
        for (var i = 0; i < normalized.Data.Length / 2; i++)
        {
            var v = Math.Abs((Int32)Sample(normalized, i));
            if (v > peak) peak = v;
        }
        Assert.InRange(peak, (Int32)(Int16.MaxValue * 0.48), (Int32)(Int16.MaxValue * 0.52));
    }

    [Fact(DisplayName = "全静音归一化原样返回")]
    public void NormalizePeak_Silence_ReturnsSame()
    {
        var audio = new AudioPcm { Format = Mono, Data = new Byte[20] };

        Assert.Equal(audio.Data, audio.NormalizePeak().Data);
    }

    [Fact(DisplayName = "重采样保持时长与声道数")]
    public void Resample_PreservesDurationAndChannels()
    {
        var audio = BuildSine(Stereo, 0.5, 440, 0.6);

        var up = audio.Resample(32000, ResamplerQuality.Sinc);

        Assert.Equal(32000, up.Format.SampleRate);
        Assert.Equal(2, up.Format.Channels);
        Assert.InRange(up.Duration, 0.45, 0.55);
    }

    [Fact(DisplayName = "重采样保持波形质量")]
    public void Resample_KeepsQuality()
    {
        var audio = BuildSine(Mono, 0.5, 440, 0.6);

        var up = audio.Resample(32000, ResamplerQuality.Sinc);
        var back = up.Resample(16000, ResamplerQuality.Sinc);

        Assert.InRange(back.Duration, 0.45, 0.55);

        // 与原始信号比较相关性（允许少量首尾瞬态）
        var count = Math.Min(audio.Data.Length / 2, back.Data.Length / 2) - 400;
        var dot = 0.0;
        var energy = 0.0;
        for (var i = 400; i < count; i++)
        {
            var x = (Double)Sample(audio, i);
            var y = (Double)Sample(back, i);
            dot += x * y;
            energy += y * y;
        }
        var gain = dot / energy;
        var error = 0.0;
        var signal = 0.0;
        for (var i = 400; i < count; i++)
        {
            var x = (Double)Sample(audio, i);
            var e = x - gain * Sample(back, i);
            error += e * e;
            signal += x * x;
        }
        var snr = 10 * Math.Log10(signal / Math.Max(error, 1e-9));
        Assert.True(snr > 30, $"重采样往返信噪比过低：{snr:F1} dB");
    }

    [Fact(DisplayName = "采样率未变化时重采样原样返回")]
    public void Resample_SameRate_ReturnsCopy()
    {
        var audio = Build(Mono, 100, 10);

        var same = audio.Resample(Mono.SampleRate);

        Assert.Equal(audio.Data, same.Data);
        Assert.NotSame(audio.Data, same.Data);
    }

    #region 辅助
    private static AudioPcm Build(AudioFormat format, Int32 samples, Int16 start)
    {
        var data = new Byte[samples * format.Channels * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (Int16)(start + i);
            for (var ch = 0; ch < format.Channels; ch++)
            {
                var p = (i * format.Channels + ch) * 2;
                data[p] = (Byte)(v & 0xFF);
                data[p + 1] = (Byte)((v >> 8) & 0xFF);
            }
        }

        return new AudioPcm { Format = format, Data = data };
    }

    private static AudioPcm BuildSine(AudioFormat format, Double seconds, Double frequency, Double amplitude)
    {
        var samples = (Int32)(format.SampleRate * seconds);
        var data = new Byte[samples * format.Channels * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (Int16)(Math.Sin(2 * Math.PI * frequency * i / format.SampleRate) * amplitude * 32000);
            for (var ch = 0; ch < format.Channels; ch++)
            {
                var p = (i * format.Channels + ch) * 2;
                data[p] = (Byte)(v & 0xFF);
                data[p + 1] = (Byte)((v >> 8) & 0xFF);
            }
        }

        return new AudioPcm { Format = format, Data = data };
    }

    private static Int16 Sample(AudioPcm audio, Int32 index) =>
        (Int16)(audio.Data[index * 2] | audio.Data[index * 2 + 1] << 8);
    #endregion
}


