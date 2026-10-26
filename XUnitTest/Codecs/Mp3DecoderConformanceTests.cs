using System;
using System.IO;
using NewLife.Audio.Codecs;
using Xunit;

namespace XUnitTest.Codecs;

/// <summary>MP3 Layer III 解码一致性测试</summary>
/// <remarks>
/// 样本由 LAME 编码自制正弦/噪声素材（无第三方版权内容），参考 PCM 为成熟解码器的解码结果，
/// 测试按信噪比判定一致性，允许不同实现之间的浮点舍入差异。
/// </remarks>
public class Mp3DecoderConformanceTests
{
    /// <summary>样本文件、采样率、声道数、总样本数（每声道）</summary>
    public static TheoryData<String, Int32, Int32, Int32> Vectors => new()
    {
        { "m1-mono-44k-320.mp3", 44100, 1, 25344 },
        { "m1-stereo-44k-128.mp3", 44100, 2, 28800 },
        { "m1-joint-vbr-44k.mp3", 44100, 2, 28800 },
        { "m1-transient-44k-64.mp3", 24000, 2, 16128 },
        { "m2-mono-16k-32.mp3", 16000, 1, 9216 },
        { "m25-mono-8k-16.mp3", 8000, 1, 4608 },
    };

    private static String AssetPath(String name) => Path.Combine(AppContext.BaseDirectory, "Assets", "mp3", name);

    [Theory(DisplayName = "MP3 解码结果与参考 PCM 一致")]
    [MemberData(nameof(Vectors))]
    public void Decode_MatchesReference(String name, Int32 sampleRate, Int32 channels, Int32 totalSamples)
    {
        var data = File.ReadAllBytes(AssetPath(name));
        var reference = ReadPcm(AssetPath(name.Replace(".mp3", ".ref.pcm")));

        var header = Mp3Codec.ParseFrameHeader(data, 0);
        Assert.NotNull(header);
        Assert.Equal(sampleRate, header.SampleRate);

        var codec = new Mp3Codec();
        var pcm = codec.ToPcm(data, null);

        Assert.Equal(totalSamples * channels * 2, pcm.Length);

        var actual = new Int16[pcm.Length / 2];
        Buffer.BlockCopy(pcm.GetSpan().ToArray(), 0, actual, 0, actual.Length * 2);

        var count = Math.Min(reference.Length, actual.Length);
        Assert.True(count > 1000);

        var snr = Snr(actual, reference, count);

        Assert.True(snr > 60, $"{name} 解码信噪比过低：{snr:F1} dB");
    }

    [Theory(DisplayName = "MP3 帧头解析出正确的采样率与声道")]
    [MemberData(nameof(Vectors))]
    public void ParseHeader_ReportsFormat(String name, Int32 sampleRate, Int32 channels, Int32 totalSamples)
    {
        var data = File.ReadAllBytes(AssetPath(name));
        var header = Mp3Codec.ParseFrameHeader(data, 0);

        Assert.NotNull(header);
        Assert.Equal(sampleRate, header.SampleRate);
        Assert.Equal(channels, header.ChannelMode == 3 ? 1 : 2);
        Assert.Equal(3, header.Layer);
        _ = totalSamples;
    }

    [Fact(DisplayName = "MP3 跳过 ID3v2 标签后再解码")]
    public void Decode_SkipsId3v2Tag()
    {
        var data = File.ReadAllBytes(AssetPath("m2-mono-16k-32.mp3"));
        var tagged = new Byte[10 + data.Length];
        tagged[0] = (Byte)'I';
        tagged[1] = (Byte)'D';
        tagged[2] = (Byte)'3';
        tagged[3] = 4;
        tagged[9] = 0; // 标签长度 0，仅占 10 字节头
        Buffer.BlockCopy(data, 0, tagged, 10, data.Length);

        var codec = new Mp3Codec();
        var plain = codec.ToPcm(data, null);
        var withTag = codec.ToPcm(tagged, null);

        Assert.Equal(plain.Length, withTag.Length);
    }

    [Fact(DisplayName = "MP3 尾部截断时保留已解码帧且不抛异常")]
    public void Decode_Truncated_KeepsDecodedFrames()
    {
        var data = File.ReadAllBytes(AssetPath("m1-stereo-44k-128.mp3"));
        var truncated = new Byte[data.Length - 600];
        Buffer.BlockCopy(data, 0, truncated, 0, truncated.Length);

        var codec = new Mp3Codec();
        var pcm = codec.ToPcm(truncated, null);

        Assert.True(pcm.Length > 0);
        Assert.True(pcm.Length < codec.ToPcm(data, null).Length);
    }

    [Fact(DisplayName = "MP3 非 Layer III 帧不被解码")]
    public void Decode_NonLayer3_ReturnsEmpty()
    {
        // 0xFF 0xFD：MPEG1 Layer II
        var data = new Byte[1024];
        data[0] = 0xFF;
        data[1] = 0xFD;
        data[2] = 0x90;
        data[3] = 0x00;

        var codec = new Mp3Codec();
        var pcm = codec.ToPcm(data, null);

        Assert.Equal(0, pcm.Length);
    }

    private static Int16[] ReadPcm(String path)
    {
        var bytes = File.ReadAllBytes(path);
        var pcm = new Int16[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, pcm, 0, pcm.Length * 2);

        return pcm;
    }

    private static Double Snr(Int16[] actual, Int16[] reference, Int32 count)
    {
        var signal = 0.0;
        var error = 0.0;
        for (var i = 0; i < count; i++)
        {
            var x = (Double)reference[i];
            var y = (Double)actual[i];
            signal += x * x;
            error += (x - y) * (x - y);
        }

        if (signal <= 0) return 0;
        if (error <= 0) return 999;

        return 10 * Math.Log10(signal / error);
    }
}
