using System;
using NewLife.Audio;
using NewLife.Data;
using Xunit;

namespace XUnitTest.Codecs;

public class AudioCodecFactoryTests
{
    private readonly AudioCodecFactory _factory = new();

    [Fact(DisplayName = "工厂ToPcm路由ADPCMA→PCM成功")]
    public void ToPcm_ADPCMA_ReturnsPcm()
    {
        // 生成带ADPCM头的编码数据
        var adpcmCodec = new NewLife.Audio.Codecs.ADPCMCodec();
        var pcm = new Byte[160 * 2];
        var encoded = adpcmCodec.FromPcm(pcm, null);

        var result = _factory.ToPcm(AVTypes.ADPCMA, encoded);
        Assert.NotNull(result);
        Assert.True(result.Total > 0);
    }

    [Fact(DisplayName = "工厂ToPcm路由G711A→PCM成功")]
    public void ToPcm_G711A_ReturnsPcm()
    {
        var g711aCodec = new NewLife.Audio.Codecs.G711ACodec();
        var pcm = new Byte[160 * 2];
        var encoded = g711aCodec.FromPcm(pcm, null);

        var result = _factory.ToPcm(AVTypes.G711A, encoded);
        Assert.NotNull(result);
        Assert.Equal(encoded.Total * 2, result.Total);
    }

    [Fact(DisplayName = "工厂ToPcm路由G711U→PCM成功")]
    public void ToPcm_G711U_ReturnsPcm()
    {
        var g711uCodec = new NewLife.Audio.Codecs.G711UCodec();
        var pcm = new Byte[160 * 2];
        var encoded = g711uCodec.FromPcm(pcm, null);

        var result = _factory.ToPcm(AVTypes.G711U, encoded);
        Assert.NotNull(result);
        Assert.Equal(encoded.Total * 2, result.Total);
    }

    [Fact(DisplayName = "工厂FromPcm路由ADPCMA→编码成功")]
    public void FromPcm_ADPCMA_ReturnsEncoded()
    {
        var pcm = new Byte[160 * 2];
        var result = _factory.FromPcm(AVTypes.ADPCMA, pcm);
        Assert.NotNull(result);
        Assert.True(result.Total > 0);
        Assert.True(result.Total < pcm.Length); // ADPCM 4:1压缩
    }

    [Fact(DisplayName = "工厂FromPcm路由G711A→编码成功")]
    public void FromPcm_G711A_ReturnsEncoded()
    {
        var pcm = new Byte[160 * 2];
        var result = _factory.FromPcm(AVTypes.G711A, pcm);
        Assert.NotNull(result);
        Assert.Equal(pcm.Length / 2, result.Total); // G.711 2:1压缩
    }

    [Fact(DisplayName = "工厂FromPcm路由G711U→编码成功")]
    public void FromPcm_G711U_ReturnsEncoded()
    {
        var pcm = new Byte[160 * 2];
        var result = _factory.FromPcm(AVTypes.G711U, pcm);
        Assert.NotNull(result);
        Assert.Equal(pcm.Length / 2, result.Total);
    }

    [Fact(DisplayName = "工厂不支持的编码类型抛NotSupportedException")]
    public void ToPcm_UnsupportedType_ThrowsNotSupported()
    {
        var data = new Byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        Assert.Throws<NotSupportedException>(() => _factory.ToPcm(AVTypes.H264, data));
        Assert.Throws<NotSupportedException>(() => _factory.ToPcm(AVTypes.SVAC, data));
        Assert.Throws<NotSupportedException>(() => _factory.FromPcm(AVTypes.H264, data));
    }

    [Fact(DisplayName = "海思头去除：正确格式的4字节头被剥离")]
    public void TrimHI_ValidHeader_StripsHeader()
    {
        // 构造海思头：[0x00, 0x01, dataLen/2, 0x00]
        var payload = new Byte[100];
        for (var i = 0; i < 100; i++) payload[i] = 0xAB;

        var buf = new Byte[4 + 100];
        buf[0] = 0x00;
        buf[1] = 0x01;
        buf[2] = (Byte)(100 / 2); // dataLen/2
        buf[3] = 0x00;
        Array.Copy(payload, 0, buf, 4, 100);

        var result = _factory.TrimHI(buf, out var trimmed);
        Assert.True(trimmed);
        Assert.Equal(100, result.Total);
        Assert.Equal(0xAB, result[0]);
    }

    [Fact(DisplayName = "海思头不去除：非海思头数据原样返回")]
    public void TrimHI_InvalidHeader_ReturnsOriginal()
    {
        var data = new Byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var result = _factory.TrimHI(data, out var trimmed);
        Assert.False(trimmed);
        Assert.Equal(4, result.Total);
    }

    [Fact(DisplayName = "海思头添加后数据长度增加4")]
    public void AddHI_ReturnsDataPlus4()
    {
        var data = new Byte[100];
        var result = _factory.AddHI(data);
        Assert.Equal(104, result.Total);
        Assert.Equal(0x00, result[0]);
        Assert.Equal(0x01, result[1]);
        Assert.Equal((Byte)(100 / 2), result[2]);
        Assert.Equal(0x00, result[3]);
    }

    [Fact(DisplayName = "海思头往返：添加→去除→数据一致")]
    public void HI_RoundTrip_DataPreserved()
    {
        var original = new Byte[50];
        for (var i = 0; i < 50; i++) original[i] = (Byte)(i + 1);

        // 添加海思头后去除
        var withHI = _factory.AddHI(original);
        Assert.Equal(54, withHI.Total);

        var afterStrip = _factory.TrimHI(withHI, out var stripped);
        Assert.True(stripped);
        Assert.Equal(original.Length, afterStrip.Total);
        for (var i = 0; i < original.Length; i++)
            Assert.Equal(original[i], afterStrip[i]);
    }
}
