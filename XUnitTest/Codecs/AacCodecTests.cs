using System;
using System.IO;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using Xunit;

namespace XUnitTest.Codecs;

public class AacCodecTests
{
    private readonly AacCodec _codec = new();

    [Fact(DisplayName = "AAC编解码器注册信息正确")]
    public void CodecInfo_IsCorrect()
    {
        Assert.Equal("AAC-LC", _codec.Name);
        Assert.Contains(AVTypes.AAC, _codec.SupportedTypes);
        Assert.Contains(AVTypes.AACLC, _codec.SupportedTypes);
        Assert.Contains(AVTypes.HEAAC, _codec.SupportedTypes);
    }

    [Fact(DisplayName = "AAC编码输出ADTS并可解码还原")]
    public void FromPcm_EncodesAdts_AndDecodes()
    {
        var pcm = MakeSine(44100, 1, 0.2, 440);
        var options = new AacEncoderOptions { SampleRate = 44100, Channels = 1, Bitrate = 64000 };

        var encoded = new MemoryStream();
        encoded.Write(_codec.FromPcm(pcm, options).GetSpan());
        encoded.Write(_codec.FromPcm(ReadOnlySpan<Byte>.Empty, options).GetSpan());
        var adts = encoded.ToArray();

        Assert.True(AacCodec.IsAdtsFormat(adts));
        Assert.True(adts.Length > 100);

        var decoder = new AacCodec();
        var decoded = decoder.ToPcm(adts, null);
        Assert.True(decoded.Total > 0);
        Assert.Equal(44100, decoder.SampleRate);
        Assert.Equal(1, decoder.Channels);
        Assert.Equal(16, decoder.BitsPerSample);

        // 编码器有 1024 样本延迟，按偏移对齐后应有可辨识的重建质量
        var source = ToShorts(pcm);
        var result = ToShorts(decoded.GetSpan().ToArray());
        var snr = Snr(source, result, 1, 1024);
        Assert.True(snr > 20, $"SNR={snr:F2}dB");
    }

    [Fact(DisplayName = "AAC ADTS带ID3标签时跳过标签解码")]
    public void ToPcm_WithId3Tag_Decodes()
    {
        var pcm = MakeSine(44100, 1, 0.1, 440);
        var options = new AacEncoderOptions { SampleRate = 44100, Channels = 1, Bitrate = 64000 };
        var encoded = new MemoryStream();
        encoded.Write(_codec.FromPcm(pcm, options).GetSpan());
        encoded.Write(_codec.FromPcm(ReadOnlySpan<Byte>.Empty, options).GetSpan());
        var adts = encoded.ToArray();

        // ID3v2 头 + 4 字节标签体
        var id3 = new Byte[] { (Byte)'I', (Byte)'D', (Byte)'3', 4, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0 };
        var tagged = new Byte[id3.Length + adts.Length];
        id3.CopyTo(tagged, 0);
        adts.CopyTo(tagged, id3.Length);

        var decoder = new AacCodec();
        var decoded = decoder.ToPcm(tagged, null);
        Assert.True(decoded.Total > 0);
        Assert.Equal(44100, decoder.SampleRate);
    }

    private static Byte[] MakeSine(Int32 sampleRate, Int32 channels, Double seconds, Double frequency)
    {
        var count = (Int32)(sampleRate * seconds);
        var pcm = new Byte[count * channels * 2];
        for (var i = 0; i < count; i++)
        {
            var v = (Int16)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * 12000);
            for (var ch = 0; ch < channels; ch++)
            {
                pcm[(i * channels + ch) * 2] = (Byte)(v & 0xFF);
                pcm[(i * channels + ch) * 2 + 1] = (Byte)((v >> 8) & 0xFF);
            }
        }
        return pcm;
    }

    private static Int16[] ToShorts(Byte[] pcm)
    {
        var samples = new Int16[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = (Int16)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
        return samples;
    }

    private static Double Snr(Int16[] source, Int16[] decoded, Int32 channels, Int32 offset)
    {
        Double signal = 0, noise = 0;
        var count = Math.Min(source.Length / channels, decoded.Length / channels - offset);
        for (var i = 0; i < count; i++)
        {
            for (var ch = 0; ch < channels; ch++)
            {
                var x = source[i * channels + ch];
                var y = decoded[(i + offset) * channels + ch];
                signal += (Double)x * x;
                var d = x - y;
                noise += (Double)d * d;
            }
        }
        return noise <= 0 ? 999 : 10 * Math.Log10(signal / noise);
    }

    [Fact(DisplayName = "AAC raw 输出可用 ASC 解码")]
    public void FromPcm_RawWithoutAdts_CanDecodeWithAsc()
    {
        var pcm = MakeSine(44100, 1, 0.1, 440);
        var options = new AacEncoderOptions { SampleRate = 44100, Channels = 1, Bitrate = 64000, Adts = false };
        var encoded = _codec.FromPcm(pcm, options);
        Assert.True(encoded.Total > 0);
        Assert.False(AacCodec.IsAdtsFormat(encoded.GetSpan()));

        var asc = AudioSpecificConfig.FromParameters(2, 44100, 1);
        var decoder = new AacCodec();
        var decoded = decoder.ToPcm(encoded.GetSpan(), asc);
        Assert.True(decoded.Total > 0);
        Assert.Equal(44100, decoder.SampleRate);
        Assert.Equal(1, decoder.Channels);
    }

    [Fact(DisplayName = "AAC raw 封装为 LOAS/LATM 后可解码")]
    public void Latm_RoundTrip_Decodes()
    {
        var pcm = MakeSine(44100, 1, 0.1, 440);
        var options = new AacEncoderOptions { SampleRate = 44100, Channels = 1, Bitrate = 64000, Adts = false };
        var raw = _codec.FromPcm(pcm, options).GetSpan().ToArray();
        Assert.True(raw.Length > 0);

        var asc = AudioSpecificConfig.FromParameters(2, 44100, 1).ToByteArray();
        var loas = AacLatmWriter.Write(asc, [raw]);
        Assert.True(AacCodec.IsLoasFormat(loas));

        var decoder = new AacCodec();
        var decoded = decoder.ToPcm(loas, null);
        Assert.True(decoded.Total > 0);
        Assert.Equal(44100, decoder.SampleRate);
        Assert.Equal(1, decoder.Channels);
    }

    [Fact(DisplayName = "AAC编码选项校验非法参数")]
    public void AacEncoderOptions_Validate_RejectsInvalid()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AacEncoderOptions { SampleRate = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AacEncoderOptions { Channels = 3 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AacEncoderOptions { Bitrate = -1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AacEncoderOptions { TargetMaxQuantizedMagnitude = 0 }.Validate());
    }

    [Fact(DisplayName = "Raw AAC缺少ASC时明确抛出NotSupportedException")]
    public void ToPcm_RawWithoutAsc_Throws()
    {
        // 非 ADTS 数据，且未提供 AudioSpecificConfig
        var raw = new Byte[] { 0x21, 0x10, 0x05, 0x00, 0x01, 0x02, 0x03, 0x04 };

        Assert.Throws<NotSupportedException>(() => _codec.ToPcm(raw, null));
    }

    [Fact(DisplayName = "AAC ADTS头解析采样率正确")]
    public void ParseAdts_ValidHeader()
    {
        // ADTS 帧头: 0xFFF9 开头 MPEG2 AAC-LC 44100Hz Stereo
        var adts = new Byte[] { 0xFF, 0xF9, 0x50, 0x80, 0x20, 0x1F, 0xFC };
        var info = AacCodec.ParseAdtsHeader(adts, 0);
        Assert.NotNull(info);
        Assert.True(info.SampleRate > 0);
    }

    [Fact(DisplayName = "AAC IsAdtsFormat检测ADTS格式")]
    public void IsAdtsFormat_ReturnsTrue()
    {
        var data = new Byte[] { 0xFF, 0xF1, 0x50, 0x80 };
        Assert.True(AacCodec.IsAdtsFormat(data));
    }

    [Fact(DisplayName = "AAC非ADTS数据检测返回false")]
    public void IsAdtsFormat_NonAdts_ReturnsFalse()
    {
        var data = new Byte[] { 0x00, 0x01, 0x02, 0x03 };
        Assert.False(AacCodec.IsAdtsFormat(data));
    }
}
