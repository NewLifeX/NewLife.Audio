using System;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using Xunit;

namespace XUnitTest.Codecs;

/// <summary>MP3 Layer III 编码器测试</summary>
public class Mp3EncoderTests
{
    [Theory(DisplayName = "MP3 编码帧头与版本随采样率变化")]
    [InlineData(44100, 0xFB, 44100)]
    [InlineData(48000, 0xFB, 48000)]
    [InlineData(32000, 0xFB, 32000)]
    [InlineData(22050, 0xF3, 22050)]
    [InlineData(16000, 0xF3, 16000)]
    [InlineData(8000, 0xE3, 8000)]
    public void Encode_ProducesExpectedHeader(Int32 sampleRate, Int32 secondByte, Int32 expectedRate)
    {
        var pcm = BuildTone(sampleRate, 1, sampleRate / 2, 1000, 0.5);
        var codec = new Mp3Codec();
        var encoded = codec.FromPcm(pcm, new Mp3EncoderOptions { SampleRate = sampleRate, Channels = 1, Bitrate = 128 }).GetSpan().ToArray();

        Assert.True(encoded.Length >= 4);
        Assert.Equal(0xFF, encoded[0]);
        Assert.Equal(secondByte, encoded[1]);

        var header = Mp3Codec.ParseFrameHeader(encoded, 0);
        Assert.NotNull(header);
        Assert.Equal(expectedRate, header.SampleRate);
        Assert.Equal(1, header.ChannelMode == 3 ? 1 : 2);
    }

    [Fact(DisplayName = "MP3 编码结果是完整的帧序列")]
    public void Encode_ProducesFrameSequence()
    {
        var sampleRate = 44100;
        var pcm = BuildTone(sampleRate, 2, sampleRate, 1000, 0.5);
        var codec = new Mp3Codec();
        var encoded = codec.FromPcm(pcm, new Mp3EncoderOptions { SampleRate = sampleRate, Channels = 2, Bitrate = 128 }).GetSpan().ToArray();

        var offset = 0;
        var frames = 0;
        while (offset + 4 <= encoded.Length)
        {
            var header = Mp3Codec.ParseFrameHeader(encoded, offset);
            Assert.NotNull(header);
            Assert.True(header.FrameSize > 0);
            offset += header.FrameSize;
            frames++;
        }

        Assert.Equal(0, offset - encoded.Length);
        Assert.Equal(39, frames);
        Assert.InRange(encoded.Length, 39 * 417, 39 * 418);
    }

    [Fact(DisplayName = "MP3 编码后解码波形与原始信号一致")]
    public void EncodeDecode_RoundTrip()
    {
        var sampleRate = 44100;
        var pcm = BuildTone(sampleRate, 1, sampleRate, 1000, 0.3);
        var codec = new Mp3Codec();
        var options = new Mp3EncoderOptions { SampleRate = sampleRate, Channels = 1, Bitrate = 192 };

        var encoded = codec.FromPcm(pcm, options).GetSpan().ToArray();
        var decoded = codec.ToPcm(encoded, null).GetSpan().ToArray();

        var source = ToInt16(pcm);
        var actual = ToInt16(decoded);

        var snr = BestSnr(source, actual);
        Assert.True(snr > 10, $"往返信噪比过低：{snr:F1} dB");
    }

    [Fact(DisplayName = "MP3 编码兼容 Int32 比特率参数")]
    public void Encode_AcceptsLegacyBitrate()
    {
        var pcm = BuildTone(44100, 1, 44100 / 4, 1000, 0.5);
        var codec = new Mp3Codec();

        var encoded = codec.FromPcm(pcm, 96).GetSpan().ToArray();

        var header = Mp3Codec.ParseFrameHeader(encoded, 0);
        Assert.NotNull(header);
        Assert.Equal(96000, header.Bitrate);
    }

    [Fact(DisplayName = "MP3 编码静音得到全零谱线")]
    public void Encode_Silence_ProducesSilence()
    {
        var sampleRate = 44100;
        var pcm = new Byte[sampleRate];
        var codec = new Mp3Codec();
        var encoded = codec.FromPcm(pcm, new Mp3EncoderOptions { SampleRate = sampleRate, Channels = 1, Bitrate = 128 }).GetSpan().ToArray();

        var header = Mp3Codec.ParseFrameHeader(encoded, 0);
        Assert.NotNull(header);

        var decoded = codec.ToPcm(encoded, null).GetSpan().ToArray();
        for (var i = 0; i < decoded.Length; i++)
        {
            Assert.Equal(0, decoded[i]);
        }
    }

    [Fact(DisplayName = "MP3 编码对不足一帧的 PCM 补零成帧")]
    public void Encode_PartialFrame_PadsZeros()
    {
        var sampleRate = 44100;
        var pcm = BuildTone(sampleRate, 1, 100, 1000, 0.5);
        var codec = new Mp3Codec();
        var encoded = codec.FromPcm(pcm, new Mp3EncoderOptions { SampleRate = sampleRate, Channels = 1, Bitrate = 128 }).GetSpan().ToArray();

        var header = Mp3Codec.ParseFrameHeader(encoded, 0);
        Assert.NotNull(header);
        Assert.Equal(header.FrameSize, encoded.Length);
    }

    [Fact(DisplayName = "MP3 编码器拒绝非法采样率")]
    public void Encode_InvalidSampleRate_Throws()
    {
        var codec = new Mp3Codec();
        var pcm = new Byte[1024];

        Assert.Throws<ArgumentOutOfRangeException>(() => codec.FromPcm(pcm, new Mp3EncoderOptions { SampleRate = 44000, Channels = 1 }));
    }

    [Fact(DisplayName = "MP3 编码客观质量不低于当前基线")]
    public void Encode_QualityBaseline()
    {
        const Int32 rate = 44100;

        // IMP-001 修复后基线：320kbps 单声道正弦 ≥40 dB、白噪 ≥25 dB
        var sine = BuildTone(rate, 1, rate, 1000, 0.3);
        var sineSnr = MeasureSnr(sine, rate, 320);
        Assert.True(sineSnr > 40, $"1 kHz 正弦 SNR 低于基线：{sineSnr:F1} dB");

        var noise = BuildNoise(rate, 1, rate, 0.3);
        var noiseSnr = MeasureSnr(noise, rate, 320);
        Assert.True(noiseSnr > 25, $"白噪 SNR 低于基线：{noiseSnr:F1} dB");
    }

    [Fact(DisplayName = "MP3 编码在大幅值信号下仍满足颗粒比特预算")]
    public void Encode_LoudSignal_KeepsGranuleBudget()
    {
        var rate = 44100;
        var pcm = BuildTone(rate, 1, rate, 1000, 0.95);
        var encoded = new Mp3Codec().FromPcm(pcm, new Mp3EncoderOptions { SampleRate = rate, Channels = 1, Bitrate = 320 }).GetSpan().ToArray();

        var offset = 0;
        var frames = 0;
        while (offset + 4 <= encoded.Length)
        {
            var header = Mp3Codec.ParseFrameHeader(encoded, offset);
            Assert.NotNull(header);
            Assert.True(header.FrameSize > 0);
            offset += header.FrameSize;
            frames++;
        }

        Assert.True(frames > 0);
        Assert.Equal(encoded.Length, offset);
    }

    [Fact(DisplayName = "MP3 联合立体声：相关声道启用M/S")]
    public void Encode_Stereo_Correlated_UsesMs()
    {
        var sampleRate = 44100;
        var pcm = BuildTone(sampleRate, 2, sampleRate, 1000, 0.4);

        var codec = new Mp3Codec();
        var encoded = codec.FromPcm(pcm, new Mp3EncoderOptions { SampleRate = sampleRate, Channels = 2, Bitrate = 96 }).GetSpan().ToArray();

        var header = Mp3Codec.ParseFrameHeader(encoded, 0);
        Assert.NotNull(header);
        Assert.Equal(2, header.ModeExtension);

        var decoded = codec.ToPcm(encoded, null).GetSpan().ToArray();
        var snr = BestStereoSnr(ToInt16(pcm), ToInt16(decoded));
        Assert.True(snr > 25, $"M/S 往返信噪比过低：{snr:F1} dB");
    }

    [Fact(DisplayName = "MP3 联合立体声：不相关声道使用L/R")]
    public void Encode_Stereo_Uncorrelated_UsesLr()
    {
        var sampleRate = 44100;
        var samples = sampleRate;
        var pcm = new Byte[samples * 2 * 2];
        for (var i = 0; i < samples; i++)
        {
            var l = (Int16)(Math.Sin(2 * Math.PI * 1000 * i / sampleRate) * 0.4 * 32000);
            var r = (Int16)(Math.Sin(2 * Math.PI * 3200 * i / sampleRate) * 0.4 * 32000);
            pcm[i * 4] = (Byte)(l & 0xFF);
            pcm[i * 4 + 1] = (Byte)((l >> 8) & 0xFF);
            pcm[i * 4 + 2] = (Byte)(r & 0xFF);
            pcm[i * 4 + 3] = (Byte)((r >> 8) & 0xFF);
        }

        var codec = new Mp3Codec();
        var encoded = codec.FromPcm(pcm, new Mp3EncoderOptions { SampleRate = sampleRate, Channels = 2, Bitrate = 96 }).GetSpan().ToArray();

        var header = Mp3Codec.ParseFrameHeader(encoded, 0);
        Assert.NotNull(header);
        Assert.Equal(0, header.ModeExtension);

        var decoded = codec.ToPcm(encoded, null).GetSpan().ToArray();
        var snr = BestStereoSnr(ToInt16(pcm), ToInt16(decoded));
        Assert.True(snr > 25, $"L/R 往返信噪比过低：{snr:F1} dB");
    }

    [Fact(DisplayName = "MP3 联合立体声：相关立体声质量达标")]
    public void Encode_Stereo_QualityBaseline()
    {
        var sampleRate = 44100;
        var pcm = BuildTone(sampleRate, 2, sampleRate, 1000, 0.3);

        var encoded = new Mp3Codec().FromPcm(pcm, new Mp3EncoderOptions { SampleRate = sampleRate, Channels = 2, Bitrate = 192 }).GetSpan().ToArray();
        var decoded = new Mp3Codec().ToPcm(encoded, null).GetSpan().ToArray();

        var snr = BestStereoSnr(ToInt16(pcm), ToInt16(decoded));
        Assert.True(snr > 40, $"立体声质量基线未达标：{snr:F1} dB");
    }

    [Fact(DisplayName = "MP3 VBR：写入 Xing 信息帧并携带帧数与 TOC")]
    public void Encode_Vbr_WritesXingHeader()
    {
        var sampleRate = 44100;
        var pcm = BuildTone(sampleRate, 1, sampleRate, 1000, 0.4);

        var encoded = new Mp3Codec().FromPcm(pcm, new Mp3EncoderOptions { SampleRate = sampleRate, Channels = 1, Mode = Mp3RateMode.Vbr }).GetSpan().ToArray();

        var header = Mp3Codec.ParseFrameHeader(encoded, 0);
        Assert.NotNull(header);

        // 信息帧紧跟 17 字节单声道边信息
        var mark = 4 + 17;
        Assert.Equal((Byte)'X', encoded[mark]);
        Assert.Equal((Byte)'i', encoded[mark + 1]);
        Assert.Equal((Byte)'n', encoded[mark + 2]);
        Assert.Equal((Byte)'g', encoded[mark + 3]);

        var flags = ReadInt32(encoded, mark + 4);
        Assert.True((flags & 0x07) == 0x07, $"Xing 标记位不完整：0x{flags:X}");

        var frames = ReadInt32(encoded, mark + 8);
        var bytes = ReadInt32(encoded, mark + 12);
        Assert.True(frames > 0);
        Assert.Equal(encoded.Length, bytes);

        // 帧数与实际可解析的音频帧数一致
        var counted = 0;
        for (var offset = header.FrameSize; offset + 4 <= encoded.Length;)
        {
            var h = Mp3Codec.ParseFrameHeader(encoded, offset);
            Assert.NotNull(h);
            counted++;
            offset += h.FrameSize;
        }
        Assert.Equal(frames, counted);
    }

    [Fact(DisplayName = "MP3 VBR：按信号复杂度分配码率，噪声段高于纯音段")]
    public void Encode_Vbr_AllocatesByComplexity()
    {
        var sampleRate = 44100;
        var samples = sampleRate;
        var pcm = new Byte[samples * 2];
        var random = new Random(20261026);
        for (var i = 0; i < samples; i++)
        {
            // 前半段为纯音（频谱简单），后半段为白噪声（频谱复杂）
            var value = i < samples / 2
                ? Math.Sin(2 * Math.PI * 1000 * i / sampleRate) * 0.4
                : (random.NextDouble() * 2 - 1) * 0.4;
            var v = (Int16)(value * 32000);
            pcm[i * 2] = (Byte)(v & 0xFF);
            pcm[i * 2 + 1] = (Byte)((v >> 8) & 0xFF);
        }

        var encoded = new Mp3Codec().FromPcm(pcm, new Mp3EncoderOptions { SampleRate = sampleRate, Channels = 1, Mode = Mp3RateMode.Vbr, VbrQuality = 2 }).GetSpan().ToArray();

        var rates = new System.Collections.Generic.List<Int32>();
        var header = Mp3Codec.ParseFrameHeader(encoded, 0);
        for (var offset = header.FrameSize; offset + 4 <= encoded.Length;)
        {
            var h = Mp3Codec.ParseFrameHeader(encoded, offset);
            rates.Add(h.Bitrate);
            offset += h.FrameSize;
        }

        Assert.True(rates.Count > 8);
        // 跳过起始两帧（滤波器组未稳定），比较两段的平均码率
        var half = rates.Count / 2;
        var tone = 0.0;
        for (var i = 2; i < half; i++) tone += rates[i];
        tone /= half - 2;
        var noise = 0.0;
        for (var i = half + 2; i < rates.Count; i++) noise += rates[i];
        noise /= rates.Count - half - 2;

        Assert.True(noise > tone, $"噪声段码率未高于纯音段：{tone / 1000:F0} kbps vs {noise / 1000:F0} kbps");
    }

    [Fact(DisplayName = "MP3 VBR：往返质量达标")]
    public void Encode_Vbr_RoundTripQuality()
    {
        var sampleRate = 44100;
        var pcm = BuildTone(sampleRate, 1, sampleRate, 1000, 0.4);

        var encoded = new Mp3Codec().FromPcm(pcm, new Mp3EncoderOptions { SampleRate = sampleRate, Channels = 1, Mode = Mp3RateMode.Vbr, VbrQuality = 2 }).GetSpan().ToArray();
        var decoded = new Mp3Codec().ToPcm(encoded, null).GetSpan().ToArray();

        var snr = BestSnr(ToInt16(pcm), ToInt16(decoded));
        Assert.True(snr > 40, $"VBR 往返信噪比过低：{snr:F1} dB");
    }

    [Fact(DisplayName = "MP3 ABR：平均码率贴近目标")]
    public void Encode_Abr_TracksTargetBitrate()
    {
        var sampleRate = 44100;
        var seconds = 2;
        var pcm = BuildNoise(sampleRate, 1, sampleRate * seconds, 0.4);

        var encoded = new Mp3Codec().FromPcm(pcm, new Mp3EncoderOptions
        {
            SampleRate = sampleRate,
            Channels = 1,
            Bitrate = 96,
            Mode = Mp3RateMode.Abr,
        }).GetSpan().ToArray();

        var average = encoded.Length * 8.0 / seconds;
        Assert.InRange(average, 96 * 1000 * 0.7, 96 * 1000 * 1.3);
    }

    [Fact(DisplayName = "MP3 CBR：不写信息帧")]
    public void Encode_Cbr_NoInfoFrame()
    {
        var sampleRate = 44100;
        var pcm = BuildTone(sampleRate, 1, sampleRate, 1000, 0.4);

        var encoded = new Mp3Codec().FromPcm(pcm, new Mp3EncoderOptions { SampleRate = sampleRate, Channels = 1, Bitrate = 128 }).GetSpan().ToArray();

        var mark = 4 + 17;
        var tag = (Char)encoded[mark];
        Assert.NotEqual('X', tag);
        Assert.NotEqual('I', tag);
    }

    private static Int32 ReadInt32(Byte[] data, Int32 offset) =>
        data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3];

    private static Double MeasureSnr(Byte[] pcm, Int32 sampleRate, Int32 bitrate)
    {
        var encoded = new Mp3Codec().FromPcm(pcm, new Mp3EncoderOptions { SampleRate = sampleRate, Channels = 1, Bitrate = bitrate });
        var decoded = new Mp3Codec().ToPcm(encoded.GetSpan().ToArray(), null).GetSpan().ToArray();

        return BestSnr(ToInt16(pcm), ToInt16(decoded));
    }

    private static Byte[] BuildNoise(Int32 sampleRate, Int32 channels, Int32 samples, Double amplitude)
    {
        var random = new Random(20261026);
        var data = new Byte[samples * channels * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (Int16)((random.NextDouble() * 2 - 1) * amplitude * 32000);
            for (var ch = 0; ch < channels; ch++)
            {
                var p = (i * channels + ch) * 2;
                data[p] = (Byte)(v & 0xFF);
                data[p + 1] = (Byte)((v >> 8) & 0xFF);
            }
        }

        return data;
    }

    private static Byte[] BuildTone(Int32 sampleRate, Int32 channels, Int32 samples, Double frequency, Double amplitude)
    {
        var data = new Byte[samples * channels * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (Int16)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * amplitude * 32000);
            for (var ch = 0; ch < channels; ch++)
            {
                var p = (i * channels + ch) * 2;
                data[p] = (Byte)(v & 0xFF);
                data[p + 1] = (Byte)((v >> 8) & 0xFF);
            }
        }

        return data;
    }

    private static Int16[] ToInt16(Byte[] data)
    {
        var pcm = new Int16[data.Length / 2];
        for (var i = 0; i < pcm.Length; i++) pcm[i] = (Int16)(data[i * 2] | data[i * 2 + 1] << 8);

        return pcm;
    }

    /// <summary>在编解码器固有延迟范围内搜索最佳对齐，并拟合增益后的信噪比</summary>
    private static Double BestSnr(Int16[] source, Int16[] actual)
    {
        var best = -999.0;
        for (var shift = 0; shift < Math.Min(4096, actual.Length - 4096); shift++)
        {
            var sig = 0.0;
            var dot = 0.0;
            var pow = 0.0;
            var count = Math.Min(source.Length, actual.Length - shift) - shift;
            for (var i = 0; i < count; i++)
            {
                var x = (Double)source[i];
                var y = (Double)actual[i + shift];
                sig += x * x;
                dot += x * y;
                pow += y * y;
            }
            if (sig <= 0 || pow <= 0) continue;

            var gain = dot / pow;
            var error = 0.0;
            for (var i = 0; i < count; i++)
            {
                var e = source[i] - gain * actual[i + shift];
                error += e * e;
            }
            var snr = 10 * Math.Log10(sig / Math.Max(error, 1e-9));
            if (snr > best) best = snr;
        }

        return best;
    }

    /// <summary>立体声信噪比：拆分左右声道分别对齐后取均值（交错数据不能用单一增益评估）</summary>
    private static Double BestStereoSnr(Int16[] source, Int16[] actual)
    {
        var srcL = new Int16[source.Length / 2];
        var srcR = new Int16[source.Length / 2];
        for (var i = 0; i < srcL.Length; i++)
        {
            srcL[i] = source[i * 2];
            srcR[i] = source[i * 2 + 1];
        }

        var actL = new Int16[actual.Length / 2];
        var actR = new Int16[actual.Length / 2];
        for (var i = 0; i < actL.Length; i++)
        {
            actL[i] = actual[i * 2];
            actR[i] = actual[i * 2 + 1];
        }

        return Math.Min(BestSnr(srcL, actL), BestSnr(srcR, actR));
    }
}

