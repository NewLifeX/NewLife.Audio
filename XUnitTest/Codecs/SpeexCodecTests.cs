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
        Assert.Equal("Speex (Legacy)", _codec.Name);
        Assert.Equal("0.1-stub", _codec.Version);
        Assert.True(_codec.IsStateful);
        Assert.Empty(_codec.SupportedTypes);
    }

    [Fact(DisplayName = "Speex ToPcm抛NotSupportedException")]
    public void ToPcm_ThrowsNotSupported()
    {
        var ex = Assert.Throws<NotSupportedException>(() => _codec.ToPcm(new Byte[10], null));
        Assert.Contains("Opus", ex.Message);
    }

    [Fact(DisplayName = "Speex FromPcm抛NotSupportedException")]
    public void FromPcm_ThrowsNotSupported()
    {
        var ex = Assert.Throws<NotSupportedException>(() => _codec.FromPcm(new Byte[10], null));
        Assert.Contains("Opus", ex.Message);
    }

    [Fact(DisplayName = "Speex ICodecInfo接口返回正确类型")]
    public void ImplementsICodecInfo()
    {
        Assert.IsAssignableFrom<ICodecInfo>(_codec);
    }
}
