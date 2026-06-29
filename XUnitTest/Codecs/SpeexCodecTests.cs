using System;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using Xunit;

namespace XUnitTest.Codecs;

public class SpeexCodecTests
{
    private readonly SpeexCodec _codec = new();

    [Fact(DisplayName = "Speex编解码器注册信息正确")]
    public void CodecInfo_IsCorrect()
    {
        Assert.Equal("Speex (Narrowband)", _codec.Name);
        Assert.Equal("1.0", _codec.Version);
        Assert.True(_codec.IsStateful);
        Assert.NotEmpty(_codec.SupportedTypes);
    }

    [Fact(DisplayName = "Speex解码空数据返回空包")]
    public void ToPcm_EmptyData_ReturnsEmpty()
    {
        var result = _codec.ToPcm(Array.Empty<Byte>(), null);
        Assert.NotNull(result);
        Assert.True(result.Total == 0);
    }

    [Fact(DisplayName = "Speex解码有效帧产生非空PCM")]
    public void ToPcm_ValidFrame_ProducesPcm()
    {
        // 构造最小 Speex 帧（LSP 全零 + 子帧参数）
        var frame = new Byte[16];
        var result = _codec.ToPcm(frame, null);
        Assert.NotNull(result);
        Assert.True(result.Total == 320); // 160 samples × 2 bytes
    }

    [Fact(DisplayName = "Speex编码静音PCM产生非空输出")]
    public void FromPcm_Silence_ProducesOutput()
    {
        var pcm = new Byte[160 * 2]; // 160 samples @ 16-bit
        var result = _codec.FromPcm(pcm, null);
        Assert.NotNull(result);
        Assert.True(result.Total > 0);
    }

    [Fact(DisplayName = "Speex编解码往返不抛异常")]
    public void RoundTrip_DoesNotThrow()
    {
        var pcm = new Byte[160 * 2 * 10]; // 10 frames
        // 生成简单正弦波
        for (var i = 0; i < pcm.Length / 2; i++)
        {
            var s = (Int16)(Math.Sin(2 * Math.PI * 400 * i / 8000) * 16000);
            pcm[i * 2] = (Byte)(s & 0xFF);
            pcm[i * 2 + 1] = (Byte)((s >> 8) & 0xFF);
        }

        var encoded = _codec.FromPcm(pcm, null);
        Assert.NotNull(encoded);
        var decoded = _codec.ToPcm(encoded.GetSpan(), null);
        Assert.NotNull(decoded);
        Assert.True(decoded.Total > 0);
    }
}
