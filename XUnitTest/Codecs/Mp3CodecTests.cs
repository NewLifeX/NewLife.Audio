using System;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Data;
using Xunit;

namespace XUnitTest.Codecs;

public class Mp3CodecTests
{
    private readonly Mp3Codec _codec = new();

    [Fact(DisplayName = "MP3编解码器注册信息正确")]
    public void CodecInfo_IsCorrect()
    {
        Assert.Equal("MP3 (MPEG Audio Layer III)", _codec.Name);
        Assert.Contains(AVTypes.MP3, _codec.SupportedTypes);
        Assert.Contains(AVTypes.MPEGAUDIO, _codec.SupportedTypes);
    }

    [Fact(DisplayName = "MP3编码数据以0xFF同步字开始")]
    public void Encode_StartsWithSyncWord()
    {
        var pcm = new Byte[1152 * 2];
        var encoded = _codec.FromPcm(pcm, null);
        Assert.True(encoded.Total >= 4);
        Assert.Equal(0xFF, encoded[0]);
        Assert.True((encoded[1] & 0xE0) == 0xE0);
    }

    [Fact(DisplayName = "MP3帧头解析MPEG1 Layer3 128kbps 44100Hz")]
    public void ParseHeader_DefaultFrame()
    {
        var header = new Byte[] { 0xFF, 0xFB, 0x90, 0x00 };
        var info = Mp3Codec.ParseFrameHeader(header, 0);
        Assert.NotNull(info);
        Assert.Equal(Mp3Codec.MpegVersion.Mpeg1, info.Version);
        Assert.Equal(3, info.Layer);
        Assert.Equal(44100, info.SampleRate);
    }

    [Fact(DisplayName = "MP3 IsMp3Frame有效帧返回true")]
    public void IsMp3Frame_ValidFrame_ReturnsTrue()
    {
        var frame = new Byte[] { 0xFF, 0xFB, 0x90, 0x00 };
        Assert.True(Mp3Codec.IsMp3Frame(frame, 0));
    }

    [Fact(DisplayName = "MP3非帧数据IsMp3Frame返回false")]
    public void IsMp3Frame_Invalid_ReturnsFalse()
    {
        var data = new Byte[] { 0x00, 0x01, 0x02, 0x03 };
        Assert.False(Mp3Codec.IsMp3Frame(data, 0));
    }
}
