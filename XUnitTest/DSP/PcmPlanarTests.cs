using System;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest.DSP;

/// <summary>字节级交错↔平面互转</summary>
public class PcmPlanarTests
{
    private static AudioFormat Format(Int32 bits, Int32 channels, Boolean isFloat = false) => new()
    {
        SampleRate = 48000,
        Channels = channels,
        BitsPerSample = bits,
        IsFloat = isFloat,
    };

    [Theory(DisplayName = "多档位深下交错与平面互转一致")]
    [InlineData(16, 2)]
    [InlineData(24, 2)]
    [InlineData(32, 2)]
    [InlineData(16, 6)]
    public void Deinterleave_Interleave_RoundTrip(Int32 bits, Int32 channels)
    {
        var format = Format(bits, channels);
        var bytesPerSample = bits / 8;
        var pcm = new Byte[channels * bytesPerSample * 97];
        new Random(bits * 100 + channels).NextBytes(pcm);

        var planes = PcmConverter.Deinterleave(pcm, format);

        Assert.Equal(channels, planes.Length);
        Assert.All(planes, p => Assert.Equal(97 * bytesPerSample, p.Length));
        Assert.Equal(pcm, PcmConverter.Interleave(planes, format));
    }

    [Fact(DisplayName = "单声道拆平面后与原数据相同")]
    public void Deinterleave_Mono_EqualsSource()
    {
        var format = Format(16, 1);
        var pcm = new Byte[] { 1, 2, 3, 4, 5, 6 };

        var planes = PcmConverter.Deinterleave(pcm, format);

        Assert.Single(planes);
        Assert.Equal(pcm, planes[0]);
        Assert.Equal(pcm, PcmConverter.Interleave(planes, format));
    }

    [Fact(DisplayName = "浮点 PCM 同样按字节搬运")]
    public void Deinterleave_Float_RoundTrip()
    {
        var format = Format(32, 2, true);
        var pcm = new Byte[2 * 4 * 64];
        new Random(7).NextBytes(pcm);

        var planes = PcmConverter.Deinterleave(pcm, format);

        Assert.Equal(pcm, PcmConverter.Interleave(planes, format));
    }

    [Fact(DisplayName = "尾部不足一个样本的字节被忽略")]
    public void Deinterleave_PartialSample_Truncated()
    {
        var format = Format(16, 2);
        var pcm = new Byte[4 * 3 + 2];

        var planes = PcmConverter.Deinterleave(pcm, format);

        Assert.All(planes, p => Assert.Equal(6, p.Length));
    }

    [Fact(DisplayName = "声道长度不一致时以最短为准且缺失声道补零")]
    public void Interleave_UnevenPlanes_UsesShortest()
    {
        var format = Format(16, 2);
        var left = new Byte[] { 1, 0, 2, 0, 3, 0 };
        var right = new Byte[] { 9, 0 };

        var pcm = PcmConverter.Interleave([left, right], format);

        Assert.Equal(4, pcm.Length);
        Assert.Equal(new Byte[] { 1, 0, 9, 0 }, pcm);

        var withHole = PcmConverter.Interleave([left, null], format);
        Assert.Equal(new Byte[] { 1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0 }, withHole);
    }

    [Fact(DisplayName = "格式非法或空输入返回空结果")]
    public void InvalidInput_ReturnsEmpty()
    {
        Assert.Empty(PcmConverter.Deinterleave(new Byte[8], Format(16, 0)));
        Assert.Empty(PcmConverter.Deinterleave(new Byte[1], Format(16, 2)));
        Assert.Empty(PcmConverter.Interleave([], Format(16, 2)));
        Assert.Empty(PcmConverter.Interleave([new Byte[0]], Format(16, 2)));
    }

    [Fact(DisplayName = "空参数抛异常")]
    public void NullArgs_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => PcmConverter.Deinterleave(new Byte[4], null));
        Assert.Throws<ArgumentNullException>(() => PcmConverter.Interleave(null, Format(16, 2)));
        Assert.Throws<ArgumentNullException>(() => PcmConverter.Interleave([new Byte[4]], null));
    }
}
