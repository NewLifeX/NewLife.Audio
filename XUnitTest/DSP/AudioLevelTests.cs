using System;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest.DSP;

public class AudioLevelTests
{
    [Fact(DisplayName = "静音样例的峰值与均方根为零")]
    public void Measure_Silence_ReturnsZero()
    {
        var pcm = new Byte[1600]; // 16 位静音
        var level = AudioLevel.Measure(pcm);

        Assert.Equal(0, level.Peak);
        Assert.Equal(0, level.Rms);
        Assert.True(level.IsSilent);
        Assert.True(Double.IsNegativeInfinity(level.PeakDb));
    }

    [Fact(DisplayName = "满量程正弦的峰值接近 1、均方根约 0.707")]
    public void Measure_FullScaleSine_MatchesTheory()
    {
        const Int32 count = 1000;
        var pcm = new Byte[count * 2];
        for (var i = 0; i < count; i++)
        {
            var v = (Int16)(Math.Sin(2 * Math.PI * i / 100) * 32767);
            pcm[i * 2] = (Byte)(v & 0xFF);
            pcm[i * 2 + 1] = (Byte)(v >> 8 & 0xFF);
        }

        var level = AudioLevel.Measure(pcm);
        Assert.InRange(level.Peak, 0.99, 1.0);
        Assert.InRange(level.Rms, 0.69, 0.72);
        Assert.False(level.IsSilent);
        Assert.InRange(level.RmsDb, -3.5, -2.9);
    }

    [Theory(DisplayName = "各采样格式的静音与满量程识别一致")]
    [InlineData(SampleFormat.UInt8, 0x80, 0xFF)]
    [InlineData(SampleFormat.Int16, 0x00, 0xFF)]
    [InlineData(SampleFormat.Int24, 0x00, 0xFF)]
    [InlineData(SampleFormat.Int32, 0x00, 0xFF)]
    public void Measure_Formats_HandleSilenceAndFullScale(SampleFormat format, Byte silentByte, Byte loudByte)
    {
        var bytes = PcmConverter.GetBytesPerSample(format);

        var silence = new Byte[bytes * 16];
        if (format == SampleFormat.UInt8)
            for (var i = 0; i < 16; i++) silence[i] = silentByte;
        Assert.True(AudioLevel.Measure(silence, format).IsSilent);

        // 构造正满量程附近的值：有符号整数取 0x7F..，无符号取 0xFF
        var loud = new Byte[bytes * 16];
        for (var i = 0; i < 16; i++)
        {
            for (var b = 0; b < bytes; b++) loud[i * bytes + b] = b == bytes - 1 && format != SampleFormat.UInt8 ? (Byte)0x7F : loudByte;
        }
        Assert.True(AudioLevel.Measure(loud, format).Peak > 0.9);
    }

    [Fact(DisplayName = "空数据返回全零电平")]
    public void Measure_Empty_ReturnsDefault()
    {
        var level = AudioLevel.Measure(ReadOnlySpan<Byte>.Empty);
        Assert.Equal(0, level.Peak);
        Assert.True(Double.IsNegativeInfinity(level.RmsDb));
    }

    [Fact(DisplayName = "dBFS 换算是标准的 20log10")]
    public void ToDecibels_IsStandard()
    {
        Assert.Equal(0, AudioLevel.ToDecibels(1.0), 6);
        Assert.Equal(-6.0206, AudioLevel.ToDecibels(0.5), 3);
        Assert.True(Double.IsNegativeInfinity(AudioLevel.ToDecibels(0)));
    }
}
