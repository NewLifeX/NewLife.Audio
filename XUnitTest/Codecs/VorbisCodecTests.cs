using System;
using System.IO;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Audio.Containers;
using NewLife.Data;
using Xunit;

namespace XUnitTest.Codecs;

/// <summary>Ogg Vorbis 编解码器测试</summary>
/// <remarks>
/// 参考样本由 OggVorbisEncoder 生成音频、NVorbis 解码为参考 PCM，
/// 用于验证纯托管解码器的 sample 级一致性；编码器另做自解码与 NVorbis 交叉验证。
/// </remarks>
public class VorbisCodecTests
{
    /// <summary>参考样本：文件名、采样率、声道数、总样本数（每声道）</summary>
    public static TheoryData<String, Int32, Int32, Int32> Vectors => new()
    {
        { "mono-44k-q5", 44100, 1, 25436 },
        { "stereo-44k-q5", 44100, 2, 25436 },
        { "stereo-48k-q8", 48000, 2, 27776 },
        { "mono-22k-q2", 22050, 1, 12718 },
        { "transient-44k-q5", 44100, 2, 25436 },
    };

    [Fact(DisplayName = "Vorbis 编解码器注册信息正确")]
    public void CodecInfo_IsCorrect()
    {
        var codec = new VorbisCodec();

        Assert.Equal("Vorbis I", codec.Name);
        Assert.True(codec.IsStateful);
        Assert.Contains(AVTypes.Vorbis, codec.SupportedTypes);
    }

    [Fact(DisplayName = "Vorbis 解码非 Vorbis 数据抛异常")]
    public void Decode_NonVorbis_Throws()
    {
        var codec = new VorbisCodec();

        Assert.Throws<InvalidDataException>(() => codec.ToPcm(new Byte[30], null));
    }

    [Theory(DisplayName = "Vorbis 解码结果与参考 PCM 一致")]
    [MemberData(nameof(Vectors))]
    public void Decode_MatchesReference(String name, Int32 sampleRate, Int32 channels, Int32 totalSamples)
    {
        var data = File.ReadAllBytes(AssetPath(name + ".ogg"));
        var reference = ReadPcm(AssetPath(name + ".ref.pcm"));

        var codec = new VorbisCodec();
        var pcm = codec.ToPcm(data, null).GetSpan().ToArray();

        Assert.Equal(totalSamples * channels * 2, pcm.Length);

        var actual = new Int16[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, actual, 0, pcm.Length);

        var count = Math.Min(reference.Length, actual.Length);
        var snr = Snr(reference, actual, count);

        Assert.True(snr > 80, $"{name} 解码信噪比过低：{snr:F1} dB");
        Assert.Equal(sampleRate, ReadSampleRate(data));
        Assert.Equal(channels, ReadChannels(data));
    }

    [Fact(DisplayName = "Vorbis 编码输出为合法 Ogg Vorbis 容器")]
    public void Encode_ProducesValidOgg()
    {
        var (pcm, samples, channels) = CreateSine(44100, 2, 22050);
        var codec = new VorbisCodec();

        var encoded = codec.FromPcm(pcm, new VorbisOptions { SampleRate = 44100, Channels = channels, Quality = 5 }).GetSpan().ToArray();

        using var stream = new MemoryStream(encoded);
        using var reader = new OggFileReader(stream);

        Assert.Equal(AVTypes.Vorbis, reader.CodecType);
        Assert.Equal(44100, reader.Format.SampleRate);
        Assert.Equal(channels, reader.Format.Channels);
        Assert.Equal(samples, reader.TotalSamples);
        Assert.NotNull(reader.VorbisIdentification);
        Assert.NotNull(reader.VorbisComment);
        Assert.NotNull(reader.VorbisSetup);
    }

    [Theory(DisplayName = "Vorbis 编码自解码往返达到目标质量")]
    [InlineData(1, 0, 15.0)]
    [InlineData(1, 5, 25.0)]
    [InlineData(1, 10, 40.0)]
    [InlineData(2, 5, 25.0)]
    public void Encode_RoundTrip(Int32 channels, Int32 quality, Double threshold)
    {
        var (pcm, samples, _) = CreateSine(44100, channels, 22050);
        var codec = new VorbisCodec();

        var encoded = codec.FromPcm(pcm, new VorbisOptions { SampleRate = 44100, Channels = channels, Quality = quality }).GetSpan().ToArray();
        var decoded = codec.ToPcm(encoded, null).GetSpan().ToArray();

        Assert.Equal(samples * channels * 2, decoded.Length);

        var reference = new Int16[pcm.Length / 2];
        var actual = new Int16[decoded.Length / 2];
        Buffer.BlockCopy(pcm, 0, reference, 0, pcm.Length);
        Buffer.BlockCopy(decoded, 0, actual, 0, decoded.Length);

        var snr = Snr(reference, actual, reference.Length);
        Assert.True(snr > threshold, $"q={quality} ch={channels} 往返信噪比过低：{snr:F1} dB");
    }

    [Fact(DisplayName = "Vorbis 编解码器可通过工厂按编码类型获取")]
    public void Factory_DecodesVorbis()
    {
        var (pcm, samples, channels) = CreateSine(44100, 1, 4096);
        var codec = new VorbisCodec();
        var encoded = codec.FromPcm(pcm, new VorbisOptions { SampleRate = 44100, Channels = channels, Quality = 5 });

        var factory = new AudioCodecFactory();
        var decoded = factory.ToPcm(AVTypes.Vorbis, encoded);

        Assert.Equal(samples * channels * 2, decoded.Length);
    }

    [Fact(DisplayName = "Vorbis 六声道往返覆盖非整除分区")]
    public void Encode_SixChannels_RoundTrip()
    {
        var (pcm, samples, channels) = CreateSine(44100, 6, 22050);
        var codec = new VorbisCodec();

        var encoded = codec.FromPcm(pcm, new VorbisOptions { SampleRate = 44100, Channels = channels, Quality = 5 }).GetSpan().ToArray();
        var decoded = codec.ToPcm(encoded, null).GetSpan().ToArray();

        Assert.Equal(samples * channels * 2, decoded.Length);

        var reference = new Int16[pcm.Length / 2];
        var actual = new Int16[decoded.Length / 2];
        Buffer.BlockCopy(pcm, 0, reference, 0, pcm.Length);
        Buffer.BlockCopy(decoded, 0, actual, 0, decoded.Length);

        var snr = Snr(reference, actual, reference.Length);
        Assert.True(snr > 25, $"6 声道往返信噪比过低：{snr:F1} dB");
    }

    [Fact(DisplayName = "Vorbis 空输入编码只输出头包")]
    public void Encode_EmptyInput_ProducesHeadersOnly()
    {
        var codec = new VorbisCodec();
        var encoded = codec.FromPcm([], new VorbisOptions { SampleRate = 44100, Channels = 1 }).GetSpan().ToArray();

        Assert.True(encoded.Length > 0);

        using var stream = new MemoryStream(encoded);
        using var reader = new OggFileReader(stream);

        Assert.Equal(AVTypes.Vorbis, reader.CodecType);
        Assert.Equal(0, reader.TotalSamples);
    }

    private static String AssetPath(String name) => Path.Combine(AppContext.BaseDirectory, "Assets", "vorbis", name);

    private static (Byte[] Pcm, Int32 Samples, Int32 Channels) CreateSine(Int32 sampleRate, Int32 channels, Int32 samples)
    {
        var pcm = new Byte[samples * channels * 2];
        for (var i = 0; i < samples; i++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var frequency = 440 + channel * 220;
                var value = (Int16)Math.Round(0.3 * 32767 * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
                var index = (i * channels + channel) * 2;
                pcm[index] = (Byte)(value & 0xFF);
                pcm[index + 1] = (Byte)((value >> 8) & 0xFF);
            }
        }

        return (pcm, samples, channels);
    }

    private static Int16[] ReadPcm(String path)
    {
        var bytes = File.ReadAllBytes(path);
        var samples = new Int16[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);

        return samples;
    }

    private static Double Snr(Int16[] reference, Int16[] actual, Int32 count)
    {
        Double signal = 0;
        Double error = 0;
        for (var i = 0; i < count; i++)
        {
            var r = reference[i];
            var a = actual[i];
            signal += r * (Double)r;
            error += (r - a) * (Double)(r - a);
        }

        return error > 0 ? 10 * Math.Log10(signal / error) : 999;
    }

    private static Int32 ReadSampleRate(Byte[] data)
    {
        using var stream = new MemoryStream(data);
        using var reader = new OggFileReader(stream);

        return reader.Format.SampleRate;
    }

    private static Int32 ReadChannels(Byte[] data)
    {
        using var stream = new MemoryStream(data);
        using var reader = new OggFileReader(stream);

        return reader.Format.Channels;
    }
}
