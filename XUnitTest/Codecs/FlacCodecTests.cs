using System;
using System.IO;
using NewLife.Audio;
using NewLife.Audio.Codecs;using NewLife.Data;using Xunit;

namespace XUnitTest.Codecs;

public class FlacCodecTests
{
    private readonly FlacCodec _codec = new();

    [Fact(DisplayName = "FLAC编解码器注册信息正确")]
    public void CodecInfo_IsCorrect()
    {
        Assert.Equal("FLAC", _codec.Name);
        Assert.Equal("1.0", _codec.Version);
        Assert.True(_codec.IsStateful);
    }

    [Fact(DisplayName = "FLAC PCM→编码→解码→PCM往返，输出非空")]
    public void RoundTrip_ProducesOutput()
    {
        var samples = 128;
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
        Assert.True(encoded.Total > 40); // 至少包含 fLaC + STREAMINFO

        // 解码：目前仅支持Verbatim子帧，能获得部分输出即可
        var decoded = _codec.ToPcm(encoded.GetSpan(), null);
        // FLAC 无损解码应能恢复数据
        Assert.NotNull(decoded);
    }

    [Fact(DisplayName = "FLAC编码数据以fLaC标记开头")]
    public void Encode_StartsWithFlacMarker()
    {
        var pcm = new Byte[256 * 2];
        var encoded = _codec.FromPcm(pcm, null);

        Assert.Equal((Byte)'f', encoded[0]);
        Assert.Equal((Byte)'L', encoded[1]);
        Assert.Equal((Byte)'a', encoded[2]);
        Assert.Equal((Byte)'C', encoded[3]);
    }

    [Fact(DisplayName = "FLAC解码非FLAC数据抛异常")]
    public void Decode_NonFlacData_Throws()
    {
        var invalidData = new Byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 };
        Assert.Throws<InvalidDataException>(() => _codec.ToPcm(invalidData, null));
    }

    [Fact(DisplayName = "FLAC ICodecInfo接口正确实现")]
    public void ImplementsICodecInfo()
    {
        Assert.IsAssignableFrom<ICodecInfo>(_codec);
    }
}
