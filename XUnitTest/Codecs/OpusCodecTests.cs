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
}
