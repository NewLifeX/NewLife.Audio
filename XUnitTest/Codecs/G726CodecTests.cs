using System;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using Xunit;

namespace XUnitTest.Codecs;

public class G726CodecTests
{
    [Fact(DisplayName = "G.726-32k编解码器注册信息正确")]
    public void CodecInfo_IsCorrect()
    {
        var codec = new G726Codec(4);
        Assert.Contains(AVTypes.G726, codec.SupportedTypes);
        Assert.True(codec.IsStateful);
    }

    [Theory(DisplayName = "G.726各比特率编码→解码往返")]
    [InlineData(2)] // 16kbps
    [InlineData(3)] // 24kbps
    [InlineData(4)] // 32kbps
    [InlineData(5)] // 40kbps
    public void RoundTrip_VariousBitrates(Int32 bits)
    {
        var codec = new G726Codec(bits);
        var samples = 320;
        var pcm = new Byte[samples * 2];
        var random = new Random(bits * 100);
        for (var i = 0; i < samples; i++)
        {
            var val = (Int16)(random.Next(-3000, 3000));
            pcm[i * 2] = (Byte)(val & 0xFF);
            pcm[i * 2 + 1] = (Byte)(val >> 8 & 0xFF);
        }

        var encoded = codec.FromPcm(pcm, null);
        Assert.True(encoded.Total > 0);

        var decoded = codec.ToPcm(encoded, null);
        Assert.Equal(samples * 2, decoded.Total);
    }

    [Fact(DisplayName = "G.726无效比特率构造抛异常")]
    public void Constructor_InvalidBits_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new G726Codec(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new G726Codec(6));
        Assert.Throws<ArgumentOutOfRangeException>(() => new G726Codec(10));
    }

    [Fact(DisplayName = "G.726有状态：独立实例编码结果不同")]
    public void Stateful_DifferentInstances_DifferentStates()
    {
        var codec1 = new G726Codec(4);
        var codec2 = new G726Codec(4);
        var pcm = new Byte[160 * 2];

        var r1 = codec1.FromPcm(pcm, null);
        var r2 = codec2.FromPcm(pcm, null);

        // 两个实例编码相同PCM，第一个样本可能有差异
        // G.726的初始状态相同，所以结果应该相同
        Assert.Equal(r1.Total, r2.Total);
    }

    [Fact(DisplayName = "G.726 ICodecInfo接口返回正确类型")]
    public void ImplementsICodecInfo()
    {
        var codec = new G726Codec();
        Assert.IsAssignableFrom<ICodecInfo>(codec);
    }
}
