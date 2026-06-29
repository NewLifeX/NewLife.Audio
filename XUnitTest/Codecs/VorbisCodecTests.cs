using System;
using System.IO;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using Xunit;

namespace XUnitTest.Codecs;

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
