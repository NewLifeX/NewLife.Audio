using System;
using System.IO;
using NewLife.Audio.Codecs;
using NewLife.Audio.Containers;
using Xunit;

namespace XUnitTest.Codecs;

/// <summary>MP3 gapless（无缝连播）测试：延迟/填充标注与精确裁剪</summary>
public class Mp3GaplessTests
{
    private const Int32 SampleRate = 44100;

    [Theory(DisplayName = "MP3 gapless：解码样本数等于原始样本数")]
    [InlineData(44100)]
    [InlineData(12345)]
    [InlineData(1000)]
    public void ToPcmGapless_ExactSampleCount(Int32 samples)
    {
        var pcm = BuildNoise(samples);
        var codec = new Mp3Codec();
        var encoded = codec.FromPcm(pcm, new Mp3EncoderOptions { SampleRate = SampleRate, Channels = 1, Mode = Mp3RateMode.Vbr }).GetSpan().ToArray();

        var gapless = codec.ToPcmGapless(encoded, out var rate, out var channels);

        Assert.Equal(SampleRate, rate);
        Assert.Equal(1, channels);
        Assert.Equal(samples * 2, gapless.Length);
    }

    [Fact(DisplayName = "MP3 gapless：裁剪后与源信号逐样本对齐")]
    public void ToPcmGapless_AlignsToSource()
    {
        var samples = 20000;
        var pcm = BuildNoise(samples);
        var codec = new Mp3Codec();
        var encoded = codec.FromPcm(pcm, new Mp3EncoderOptions { SampleRate = SampleRate, Channels = 1, Mode = Mp3RateMode.Vbr, VbrQuality = 0 }).GetSpan().ToArray();

        var gapless = codec.ToPcmGapless(encoded, out _, out _);

        // 无需任何位移即可对齐：说明延迟与填充被精确扣除
        var snr = Snr(pcm, gapless, 0);
        Assert.True(snr > 20, $"gapless 输出未与源信号对齐：{snr:F1} dB");
    }

    [Fact(DisplayName = "MP3 gapless：两段解码后可直接拼接")]
    public void ToPcmGapless_ConcatWithoutGap()
    {
        var first = BuildNoise(10000);
        var second = BuildNoise(5000);
        var codec = new Mp3Codec();
        var options = new Mp3EncoderOptions { SampleRate = SampleRate, Channels = 1, Mode = Mp3RateMode.Vbr };

        var encodedA = codec.FromPcm(first, options).GetSpan().ToArray();
        var encodedB = codec.FromPcm(second, options).GetSpan().ToArray();

        var a = codec.ToPcmGapless(encodedA, out _, out _);
        var b = codec.ToPcmGapless(encodedB, out _, out _);

        Assert.Equal(10000 * 2, a.Length);
        Assert.Equal(5000 * 2, b.Length);

        // 拼接处连续：前段末尾与后段开头各自与源信号一致
        Assert.True(Snr(first, a, 0) > 20);
        Assert.True(Snr(second, b, 0) > 20);
    }

    [Fact(DisplayName = "MP3 gapless：CBR 打开信息帧同样可裁剪")]
    public void ToPcmGapless_CbrWithInfoFrame()
    {
        var samples = 8000;
        var pcm = BuildNoise(samples);
        var codec = new Mp3Codec();
        var encoded = codec.FromPcm(pcm, new Mp3EncoderOptions
        {
            SampleRate = SampleRate,
            Channels = 1,
            Bitrate = 128,
            WriteInfoFrame = true,
        }).GetSpan().ToArray();

        var gapless = codec.ToPcmGapless(encoded, out _, out _);
        Assert.Equal(samples * 2, gapless.Length);

        using var ms = new MemoryStream(encoded);
        var reader = new Mp3FileReader(ms);
        Assert.True(reader.EncoderDelay > 0);
        Assert.True(reader.EncoderPadding >= 0);
    }

    [Fact(DisplayName = "MP3 gapless：无信息帧时退化为普通解码")]
    public void ToPcmGapless_NoInfoFrame_ReturnsRaw()
    {
        var samples = 6000;
        var pcm = BuildNoise(samples);
        var codec = new Mp3Codec();
        var encoded = codec.FromPcm(pcm, new Mp3EncoderOptions { SampleRate = SampleRate, Channels = 1, Bitrate = 128 }).GetSpan().ToArray();

        var gapless = codec.ToPcmGapless(encoded, out _, out _);
        var raw = codec.ToPcm(encoded, null).GetSpan().ToArray();

        Assert.Null(Mp3GaplessInfo.TryParse(encoded, out _));
        Assert.Equal(raw, gapless);
    }

    #region 辅助
    private static Byte[] BuildNoise(Int32 samples)
    {
        var random = new Random(samples);
        var data = new Byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (Int16)((random.NextDouble() * 2 - 1) * 0.4 * 32000);
            data[i * 2] = (Byte)(v & 0xFF);
            data[i * 2 + 1] = (Byte)((v >> 8) & 0xFF);
        }

        return data;
    }

    private static Double Snr(Byte[] source, Byte[] actual, Int32 shift)
    {
        var count = Math.Min(source.Length / 2, actual.Length / 2 - shift);
        var signal = 0.0;
        var error = 0.0;
        for (var i = 0; i < count; i++)
        {
            var x = (Int16)(source[i * 2] | source[i * 2 + 1] << 8);
            var y = (Int16)(actual[(i + shift) * 2] | actual[(i + shift) * 2 + 1] << 8);
            signal += (Double)x * x;
            var e = x - y;
            error += (Double)e * e;
        }

        return 10 * Math.Log10(signal / Math.Max(error, 1e-9));
    }
    #endregion
}
