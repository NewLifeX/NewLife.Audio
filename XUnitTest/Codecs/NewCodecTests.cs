using System;
using System.IO;
using NewLife.Audio;
using NewLife.Audio.Codecs;
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

public class VorbisCodecTests
{
    private readonly VorbisCodec _codec = new();

    [Fact(DisplayName = "Vorbis编解码器注册信息正确")]
    public void CodecInfo_IsCorrect()
    {
        Assert.Equal("Vorbis I", _codec.Name);
        Assert.True(_codec.IsStateful);
    }

    [Fact(DisplayName = "Vorbis解码非Vorbis数据抛异常")]
    public void Decode_NonVorbis_Throws()
    {
        var data = new Byte[30];
        Assert.Throws<InvalidDataException>(() => _codec.ToPcm(data, null));
    }

    [Fact(DisplayName = "Vorbis编码输出非空")]
    public void Encode_ProducesOutput()
    {
        var pcm = new Byte[1024 * 2];
        var encoded = _codec.FromPcm(pcm, null);
        Assert.True(encoded.Total > 0);
    }
}

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
