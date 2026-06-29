using System;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Data;
using Xunit;

namespace XUnitTest.Codecs;

public class G711UCodecTests
{
    private readonly G711UCodec _codec = new();

    [Fact(DisplayName = "G711U编码→解码往返，验证PCM数据一致")]
    public void RoundTrip_SineWave()
    {
        // 使用中等幅度正弦波，减少削波影响
        var samples = 160;
        var pcm = new Byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var val = (Int16)(Math.Sin(2 * Math.PI * i / 40) * 16000);
            pcm[i * 2] = (Byte)(val & 0xFF);
            pcm[i * 2 + 1] = (Byte)(val >> 8 & 0xFF);
        }

        var encoded = _codec.FromPcm(pcm, null);
        Assert.Equal(samples, encoded.Total); // G.711 2:1 压缩

        var decoded = _codec.ToPcm(encoded.GetSpan(), null);
        Assert.Equal(samples * 2, decoded.Total);

        // G.711 μ-law 有损压缩，检查峰值误差
        var pcmData = decoded.ReadBytes();
        var maxDiff = 0;
        for (var i = 0; i < pcm.Length - 1; i += 2)
        {
            var orig = (Int16)(pcm[i + 1] << 8 | pcm[i]);
            var result = (Int16)(pcmData[i + 1] << 8 | pcmData[i]);
            var diff = Math.Abs(orig - result);
            if (diff > maxDiff) maxDiff = diff;
        }
        // μ-law 中幅度信噪比约 30dB，容差 ±256
        Assert.True(maxDiff <= 300, $"maxDiff={maxDiff}");
    }

    [Fact(DisplayName = "G711U编码已知测试向量：0→0xFF")]
    public void Encode_Zero_ReturnsFF()
    {
        var pcm = new Byte[] { 0, 0 };
        var result = _codec.FromPcm(pcm, null);
        Assert.Equal(0xFF, result[0]);
    }

    [Fact(DisplayName = "G711U解码已知测试向量：0xFF→PCM0")]
    public void Decode_FF_ReturnsZero()
    {
        var encoded = new Byte[] { 0xFF };
        var result = _codec.ToPcm(encoded, null);
        var value = (Int16)(result[1] << 8 | result[0]);
        Assert.True(Math.Abs(value) <= 8, $"value={value}");
    }

    [Fact(DisplayName = "G711U编码静音缓冲区输出均为0xFF")]
    public void Encode_Silence_AllFF()
    {
        var pcm = new Byte[200];
        var result = _codec.FromPcm(pcm, null);
        for (var i = 0; i < result.Total; i++)
            Assert.Equal(0xFF, result[i]);
    }

    [Fact(DisplayName = "G711U编码所有16位值不抛异常")]
    public void Encode_All16BitValues_NoException()
    {
        var pcm = new Byte[65536 * 2];
        for (var i = 0; i < 65536; i++)
        {
            pcm[i * 2] = (Byte)(i & 0xFF);
            pcm[i * 2 + 1] = (Byte)(i >> 8 & 0xFF);
        }
        var result = _codec.FromPcm(pcm, null);
        Assert.Equal(65536, result.Total);
    }
}
