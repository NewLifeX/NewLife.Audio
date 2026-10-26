using System;
using System.IO;
using NewLife.Audio.Codecs;
using NVorbis;
using Xunit;

namespace XUnitTest.Codecs;

/// <summary>Vorbis 编码器与 NVorbis 解码器的互操作性测试</summary>
/// <remarks>NVorbis 仅作为测试用独立解码器，验证生成的 Ogg Vorbis 流可被第三方实现正常解码。</remarks>
public class VorbisEncoderInteropTests
{
    [Theory(DisplayName = "NVorbis 可解码本库生成的 Vorbis 流")]
    [InlineData(1, 5, 25.0)]
    [InlineData(2, 5, 25.0)]
    [InlineData(1, 10, 40.0)]
    public void NVorbis_DecodesGeneratedStream(Int32 channels, Int32 quality, Double threshold)
    {
        var sampleRate = 44100;
        var samples = 22050;
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

        var codec = new VorbisCodec();
        var encoded = codec.FromPcm(pcm, new VorbisOptions { SampleRate = sampleRate, Channels = channels, Quality = quality }).GetSpan().ToArray();

        var decoded = DecodeWithNVorbis(encoded, out var nvSampleRate, out var nvChannels, out var nvTotal);

        Assert.Equal(sampleRate, nvSampleRate);
        Assert.Equal(channels, nvChannels);
        Assert.Equal(samples, nvTotal);
        Assert.Equal(pcm.Length, decoded.Length);

        var reference = new Int16[pcm.Length / 2];
        var actual = new Int16[decoded.Length / 2];
        Buffer.BlockCopy(pcm, 0, reference, 0, pcm.Length);
        Buffer.BlockCopy(decoded, 0, actual, 0, decoded.Length);

        var snr = Snr(reference, actual, reference.Length);
        Assert.True(snr > threshold, $"q={quality} ch={channels} NVorbis 信噪比过低：{snr:F1} dB");
    }

    private static Byte[] DecodeWithNVorbis(Byte[] encoded, out Int32 sampleRate, out Int32 channels, out Int32 totalSamples)
    {
        using var stream = new MemoryStream(encoded);
        using var reader = new VorbisReader(stream);

        sampleRate = reader.SampleRate;
        channels = reader.Channels;
        totalSamples = (Int32)reader.TotalSamples;

        var expected = totalSamples * channels * 2;
        var output = new Byte[expected];
        var written = 0;
        var buffer = new Single[channels * 4096];
        Int32 count;
        while (written < expected && (count = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < count && written < expected; i++)
            {
                var value = (Int16)Math.Round(Math.Clamp(buffer[i], -1f, 1f) * 32767f);
                output[written++] = (Byte)(value & 0xFF);
                output[written++] = (Byte)((value >> 8) & 0xFF);
            }
        }

        return output;
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
}
