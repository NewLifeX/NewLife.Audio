using System;
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
