using System;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest;

/// <summary>采样格式模型与交错/平面转换测试</summary>
public class PcmConverterTests
{
    [Theory(DisplayName = "采样格式：字节数与浮点标记正确")]
    [InlineData(SampleFormat.UInt8, 1, false)]
    [InlineData(SampleFormat.Int16, 2, false)]
    [InlineData(SampleFormat.Int24, 3, false)]
    [InlineData(SampleFormat.Int32, 4, false)]
    [InlineData(SampleFormat.Float32, 4, true)]
    [InlineData(SampleFormat.Float64, 8, true)]
    public void GetBytesPerSample(SampleFormat format, Int32 bytes, Boolean isFloat)
    {
        Assert.Equal(bytes, PcmConverter.GetBytesPerSample(format));
        Assert.Equal(isFloat, PcmConverter.IsFloat(format));
    }

    [Fact(DisplayName = "位深与浮点标记推导采样格式")]
    public void FromBits()
    {
        Assert.Equal(SampleFormat.UInt8, PcmConverter.FromBits(8, false));
        Assert.Equal(SampleFormat.Int16, PcmConverter.FromBits(16, false));
        Assert.Equal(SampleFormat.Int24, PcmConverter.FromBits(24, false));
        Assert.Equal(SampleFormat.Int32, PcmConverter.FromBits(32, false));
        Assert.Equal(SampleFormat.Float32, PcmConverter.FromBits(32, true));
        Assert.Equal(SampleFormat.Float64, PcmConverter.FromBits(64, true));

        Assert.Equal(SampleFormat.Int24, new AudioFormat { BitsPerSample = 24 }.SampleFormat);
        Assert.Equal(SampleFormat.Float32, new AudioFormat { BitsPerSample = 32, IsFloat = true }.SampleFormat);
    }

    [Theory(DisplayName = "各格式往返误差在量化精度内")]
    [InlineData(SampleFormat.Int16, 1.0 / 32000)]
    [InlineData(SampleFormat.Int24, 1.0 / 8000000)]
    [InlineData(SampleFormat.Int32, 1e-8)]
    [InlineData(SampleFormat.Float32, 1e-6)]
    public void RoundTrip(SampleFormat format, Double tolerance)
    {
        Single[] samples = [0f, 0.25f, -0.25f, 0.5f, -0.9f, 0.999f];

        var pcm = PcmConverter.FromFloat(samples, format);
        var back = PcmConverter.ToFloat(pcm, format);

        Assert.Equal(samples.Length, back.Length);
        for (var i = 0; i < samples.Length; i++)
        {
            Assert.True(Math.Abs(samples[i] - back[i]) <= tolerance, $"{format} 第 {i} 个样本偏差过大：{samples[i]} vs {back[i]}");
        }
    }

    [Fact(DisplayName = "8 位无符号格式静音为 128")]
    public void UInt8_SilenceIs128()
    {
        var pcm = PcmConverter.FromFloat([0f], SampleFormat.UInt8);
        Assert.Equal(128, pcm[0]);

        var back = PcmConverter.ToFloat(pcm, SampleFormat.UInt8);
        Assert.Equal(0f, back[0]);
    }

    [Fact(DisplayName = "整型格式按目标范围限幅不翻转")]
    public void FromFloat_Clips()
    {
        var pcm16 = PcmConverter.FromFloat([2f, -2f], SampleFormat.Int16);
        Assert.Equal(Int16.MaxValue, (Int16)(pcm16[0] | pcm16[1] << 8));
        Assert.Equal(Int16.MinValue, (Int16)(pcm16[2] | pcm16[3] << 8));

        var pcm24 = PcmConverter.FromFloat([2f], SampleFormat.Int24);
        Assert.Equal(0xFF, pcm24[0]);
        Assert.Equal(0xFF, pcm24[1]);
        Assert.Equal(0x7F, pcm24[2]);
    }

    [Fact(DisplayName = "24 位样本符号扩展正确")]
    public void Int24_SignExtension()
    {
        Single[] samples = [-0.5f, 0.5f];
        var pcm = PcmConverter.FromFloat(samples, SampleFormat.Int24);
        var back = PcmConverter.ToFloat(pcm, SampleFormat.Int24);

        Assert.True(Math.Abs(back[0] + 0.5f) < 1e-5);
        Assert.True(Math.Abs(back[1] - 0.5f) < 1e-5);
    }

    [Fact(DisplayName = "交错与平面互转保持声道顺序")]
    public void Interleave_RoundTrip()
    {
        Single[] interleaved = [1f, 10f, 2f, 20f, 3f, 30f];

        var planes = PcmConverter.Deinterleave(interleaved, 2);

        Assert.Equal(2, planes.Length);
        Assert.Equal([1f, 2f, 3f], planes[0]);
        Assert.Equal([10f, 20f, 30f], planes[1]);

        Assert.Equal(interleaved, PcmConverter.Interleave(planes));
    }

    [Fact(DisplayName = "平面转交错按最短声道截齐")]
    public void Interleave_MismatchedLengths_Truncates()
    {
        Single[][] planes = [[1f, 2f, 3f], [10f, 20f]];

        var interleaved = PcmConverter.Interleave(planes);

        Assert.Equal([1f, 10f, 2f, 20f], interleaved);
    }

    [Fact(DisplayName = "非法采样格式明确报错")]
    public void InvalidFormat_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PcmConverter.ToFloat(new Byte[4], SampleFormat.Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => PcmConverter.FromFloat([0f], SampleFormat.Unknown));
    }
}
