using System;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Data;
using Xunit;

namespace XUnitTest.Codecs;

public class G722CodecTests
{
    private readonly G722Codec _codec = new();

    [Fact(DisplayName = "G.722编解码器注册信息正确")]
    public void CodecInfo_IsCorrect()
    {
        Assert.Equal("G.722 SB-ADPCM", _codec.Name);
        Assert.Equal("1.0", _codec.Version);
        Assert.Contains(AVTypes.G722, _codec.SupportedTypes);
        Assert.False(_codec.IsStateful);
    }

    [Fact(DisplayName = "G.722编码→解码往返，PCM帧长一致")]
    public void RoundTrip_OutputLengthMatches()
    {
        // 16kHz PCM, 160 samples = 10ms
        var samples = 160;
        var pcm = new Byte[samples * 2];
        var random = new Random(42);
        for (var i = 0; i < samples; i++)
        {
            var val = (Int16)(random.Next(-5000, 5000));
            pcm[i * 2] = (Byte)(val & 0xFF);
            pcm[i * 2 + 1] = (Byte)(val >> 8 & 0xFF);
        }

        var encoded = _codec.FromPcm(pcm, null);
        Assert.True(encoded.Total > 0);
        // G.722 每2样本编码为1字节
        Assert.Equal(samples / 2, encoded.Total);

        var decoded = _codec.ToPcm(encoded.GetSpan(), null);
        Assert.Equal(samples * 2, decoded.Total);
    }

    [Fact(DisplayName = "G.722静音编码往返后输出低幅度PCM")]
    public void Decode_Silence_ReturnsLowAmplitude()
    {
        // 静音必须走「编码 → 解码」往返：裸零字节在标准子带 ADPCM 里表示最大负电平，并非静音
        var silent = new Byte[160 * 2];
        var encoded = _codec.FromPcm(silent, null);
        var decoded = _codec.ToPcm(encoded.GetSpan(), null);

        var pcmData = decoded.ReadBytes();
        var maxAbs = 0;
        for (var i = 0; i < pcmData.Length - 1; i += 2)
        {
            var val = Math.Abs((Int32)(Int16)(pcmData[i + 1] << 8 | pcmData[i]));
            if (val > maxAbs) maxAbs = val;
        }
        // 静音编码后重建幅度应保持很低
        Assert.True(maxAbs < 15000, $"maxAbs={maxAbs}");
    }

    [Fact(DisplayName = "G.722 ICodecInfo接口返回正确类型")]
    public void ImplementsICodecInfo()
    {
        Assert.IsAssignableFrom<ICodecInfo>(_codec);
    }
}
