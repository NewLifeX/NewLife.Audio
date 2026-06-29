using System;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using Xunit;

namespace XUnitTest.Codecs;

public class OpusCodecTests
{
    private readonly OpusCodec _codec = new();

    [Fact(DisplayName = "Opus编解码器注册信息正确")]
    public void CodecInfo_IsCorrect()
    {
        Assert.Contains("Opus", _codec.Name);
        Assert.True(_codec.IsStateful);
    }

    [Fact(DisplayName = "Opus编码输出含TOC字节")]
    public void Encode_ProducesTocByte()
    {
        var pcm = new Byte[960 * 2];
        var encoded = _codec.FromPcm(pcm, null);
        Assert.True(encoded.Total > 0);
        Assert.True(encoded[0] > 0);
    }

    [Fact(DisplayName = "Opus帧大小计算正确")]
    public void GetFrameSize_20ms()
    {
        Assert.Equal(960, OpusCodec.GetFrameSize(7));
        Assert.Equal(120, OpusCodec.GetFrameSize(0));
        Assert.Equal(480, OpusCodec.GetFrameSize(5));
    }

    [Fact(DisplayName = "Opus解码空数据返回空包")]
    public void ToPcm_EmptyData_ReturnsEmpty()
    {
        var result = _codec.ToPcm(Array.Empty<Byte>(), null);
        Assert.NotNull(result);
        Assert.True(result.Total == 0);
    }

    [Fact(DisplayName = "Opus编解码往返输出有效PCM")]
    public void RoundTrip_ProducesValidPcm()
    {
        var pcm = new Byte[960 * 2]; // 1 frame @ 20ms, 避免 MDCT O(N²) 过量计算
        for (var i = 0; i < 480; i++) // 用半帧正弦波减少纯静音
        {
            var s = (Int16)(Math.Sin(2 * Math.PI * 1000 * i / 48000) * 8000);
            pcm[i * 2] = (Byte)(s & 0xFF);
            pcm[i * 2 + 1] = (Byte)((s >> 8) & 0xFF);
        }

        var encoded = _codec.FromPcm(pcm, 64000);
        Assert.NotNull(encoded);
        Assert.True(encoded.Total > 0);

        var decoded = _codec.ToPcm(encoded.GetSpan(), null);
        Assert.NotNull(decoded);
        Assert.True(decoded.Total == 1920); // 960 samples × 2 bytes
    }
}
