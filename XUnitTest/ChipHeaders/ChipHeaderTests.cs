using System;
using NewLife.Audio;
using NewLife.Audio.ChipHeaders;
using NewLife.Data;
using Xunit;

namespace XUnitTest.ChipHeaders;

public class HisiliconHeaderTests
{
    private readonly HisiliconHeader _header = new();

    [Fact(DisplayName = "海思头正确去除含标准头的4+50字节数据")]
    public void TryTrim_ValidHeader_ReturnsTrue()
    {
        var data = new Byte[54];
        data[0] = 0x00;
        data[1] = 0x01;
        data[2] = (Byte)(50 / 2); // 25
        data[3] = 0x00;
        for (var i = 4; i < 54; i++) data[i] = (Byte)(i - 4);

        var result = _header.TryTrim(data, out var trimmed);
        Assert.True(result);
        Assert.Equal(50, trimmed.Total);
        Assert.Equal(0, trimmed[0]);
    }

    [Fact(DisplayName = "海思头不去除非海思格式数据")]
    public void TryTrim_InvalidHeader_ReturnsFalse()
    {
        var data = new Byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var result = _header.TryTrim(data, out var trimmed);
        Assert.False(result);
        Assert.Equal(4, trimmed.Total);
    }

    [Fact(DisplayName = "海思头往返：添加→去除→数据一致")]
    public void RoundTrip_DataPreserved()
    {
        var original = new Byte[50];
        for (var i = 0; i < 50; i++) original[i] = (Byte)(i + 1);

        Assert.True(_header.TryAdd(original, out var withHI));
        Assert.Equal(54, withHI.Total);

        Assert.True(_header.TryTrim(withHI.GetSpan(), out var stripped));
        Assert.Equal(50, stripped.Total);

        for (var i = 0; i < 50; i++)
            Assert.Equal(original[i], stripped[i]);
    }
}

public class DahuaHeaderTests
{
    private readonly DahuaHeader _header = new();

    [Fact(DisplayName = "大华头正确去除含标准头的6+50字节数据")]
    public void TryTrim_ValidHeader_ReturnsTrue()
    {
        var data = new Byte[56];
        data[0] = 0x00;
        data[1] = 0x01;
        data[2] = 0x01;
        data[3] = 0x00;
        data[4] = (Byte)(50 & 0xFF);
        data[5] = (Byte)((50 >> 8) & 0xFF);
        for (var i = 6; i < 56; i++) data[i] = (Byte)(i - 6);

        var result = _header.TryTrim(data, out var trimmed);
        Assert.True(result);
        Assert.Equal(50, trimmed.Total);
    }

    [Fact(DisplayName = "大华头往返：添加→去除→数据一致")]
    public void RoundTrip_DataPreserved()
    {
        var original = new Byte[30];
        for (var i = 0; i < 30; i++) original[i] = (Byte)(i + 10);

        Assert.True(_header.TryAdd(original, out var withHeader));
        Assert.Equal(36, withHeader.Total);

        Assert.True(_header.TryTrim(withHeader.GetSpan(), out var stripped));
        Assert.Equal(30, stripped.Total);
        for (var i = 0; i < 30; i++)
            Assert.Equal(original[i], stripped[i]);
    }
}

public class UniviewHeaderTests
{
    private readonly UniviewHeader _header = new();

    [Fact(DisplayName = "宇视头正确去除含标准头的4+50字节数据")]
    public void TryTrim_ValidHeader_ReturnsTrue()
    {
        var data = new Byte[54];
        data[0] = 0x24;
        data[1] = 0x01;
        data[2] = 0x00;
        data[3] = 0x00;
        for (var i = 4; i < 54; i++) data[i] = (Byte)(i - 4);

        var result = _header.TryTrim(data, out var trimmed);
        Assert.True(result);
        Assert.Equal(50, trimmed.Total);
    }

    [Fact(DisplayName = "宇视头往返：添加→去除→数据一致")]
    public void RoundTrip_DataPreserved()
    {
        var original = new Byte[20];
        for (var i = 0; i < 20; i++) original[i] = (Byte)(i + 30);

        Assert.True(_header.TryAdd(original, out var withHeader));
        Assert.Equal(24, withHeader.Total);

        Assert.True(_header.TryTrim(withHeader.GetSpan(), out var stripped));
        Assert.Equal(20, stripped.Total);
        for (var i = 0; i < 20; i++)
            Assert.Equal(original[i], stripped[i]);
    }
}
