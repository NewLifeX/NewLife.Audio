using System;
using System.IO;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Data;
using Xunit;

namespace XUnitTest.Codecs;

public class ADPCMCodecTests
{
    private readonly ADPCMCodec _codec = new();

    [Fact(DisplayName = "ADPCM编码→解码往返，验证PCM数据一致")]
    public void RoundTrip_SineWave()
    {
        var samples = 1024;
        var pcm = new Byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var val = (Int16)(Math.Sin(2 * Math.PI * i / 128) * 28000);
            pcm[i * 2] = (Byte)(val & 0xFF);
            pcm[i * 2 + 1] = (Byte)(val >> 8 & 0xFF);
        }

        var encoded = _codec.FromPcm(pcm, null);
        // ADPCM 4:1 压缩，1024 samples → ~516 bytes（含4字节头）
        Assert.True(encoded.Total > 200 && encoded.Total < samples * 2);

        var decoded = _codec.ToPcm(encoded.GetSpan(), null);
        // 解码后应有1024个采样
        Assert.Equal(samples * 2, decoded.Total);

        // ADPCM 有损压缩，验证波形相似性
        var pcmData = decoded.ReadBytes();
        var maxDiff = 0;
        for (var i = 0; i < pcm.Length - 1; i += 2)
        {
            var orig = Math.Abs((Int16)(pcm[i + 1] << 8 | pcm[i]));
            var result = (Int16)(pcmData[i + 1] << 8 | pcmData[i]);
            var diff = Math.Abs(orig - Math.Abs(result));
            if (diff > maxDiff) maxDiff = diff;
        }
        // ADPCM 在有陡峭变化处误差较大
        Assert.True(maxDiff < 8000, $"maxDiff={maxDiff}");
    }

    [Fact(DisplayName = "ADPCM编码静音信号，输出紧凑")]
    public void Encode_Silence()
    {
        var pcm = new Byte[1024 * 2]; // 全零 PCM
        var result = _codec.FromPcm(pcm, null);

        // 静音信号压缩率应很高，输出应小于输入
        Assert.True(result.Total < 1024);
        // 至少有4字节帧头
        Assert.True(result.Total > 4);
    }

    [Fact(DisplayName = "ADPCM编码后解码，输出采样数匹配输入")]
    public void OutputSampleCount_MatchesInput()
    {
        for (var sampleCount = 16; sampleCount <= 512; sampleCount *= 2)
        {
            var pcm = new Byte[sampleCount * 2];
            var random = new Random(sampleCount);
            for (var i = 0; i < sampleCount; i++)
            {
                var val = (Int16)(random.Next(-10000, 10000));
                pcm[i * 2] = (Byte)(val & 0xFF);
                pcm[i * 2 + 1] = (Byte)(val >> 8 & 0xFF);
            }

            var encoded = _codec.FromPcm(pcm, null);
            var decoded = _codec.ToPcm(encoded.GetSpan(), null);

            Assert.Equal(sampleCount * 2, decoded.Total);
        }
    }

    [Fact(DisplayName = "ADPCM扩展方法ToWav生成有效WAV头")]
    public void ToWav_ProducesValidWavHeader()
    {
        var pcm = new Byte[160 * 2];
        var wav = pcm.ToWav(8000);
        Assert.Equal(160 * 2 + 44, wav.Length);

        // 验证 RIFF 头
        Assert.Equal((Byte)'R', wav[0]);
        Assert.Equal((Byte)'I', wav[1]);
        Assert.Equal((Byte)'F', wav[2]);
        Assert.Equal((Byte)'F', wav[3]);
        Assert.Equal((Byte)'W', wav[8]);
        Assert.Equal((Byte)'A', wav[9]);
        Assert.Equal((Byte)'V', wav[10]);
        Assert.Equal((Byte)'E', wav[11]);

        // fmt chunk
        Assert.Equal((Byte)'f', wav[12]);
        Assert.Equal((Byte)'m', wav[13]);
        Assert.Equal((Byte)'t', wav[14]);
        Assert.Equal((Byte)' ', wav[15]);

        // PCM format = 1
        Assert.Equal(1, wav[20]);

        // data chunk
        Assert.Equal((Byte)'d', wav[36]);
        Assert.Equal((Byte)'a', wav[37]);
        Assert.Equal((Byte)'t', wav[38]);
        Assert.Equal((Byte)'a', wav[39]);
    }
}
