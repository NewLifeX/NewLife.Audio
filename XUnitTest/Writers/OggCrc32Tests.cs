using System;
using NewLife.Audio.Writers;
using Xunit;

namespace XUnitTest.Writers;

public class OggCrc32Tests
{
    [Fact(DisplayName = "OggCrc32空数据CRC32为0")]
    public void Compute_Empty_ReturnsZero()
    {
        var crc = OggCrc32.Compute(Array.Empty<Byte>());
        Assert.Equal(0u, crc);
    }

    [Fact(DisplayName = "OggCrc32已知向量CRC32正确")]
    public void Compute_KnownVector()
    {
        // Ogg 规范 CRC32("123456789") = 0x89A1897F
        var data = new Byte[] { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39 };
        var crc = OggCrc32.Compute(data);
        Assert.Equal(0x89A1897Fu, crc);
    }

    [Fact(DisplayName = "OggCrc32单字节CRC32正确")]
    public void Compute_SingleByte()
    {
        Assert.Equal(0u, OggCrc32.Compute([0x00]));
        Assert.Equal(0x04C11DB7u, OggCrc32.Compute([0x01]));
    }

    [Fact(DisplayName = "OggCrc32 真实Ogg页向量正确")]
    public void Compute_RealOggPage()
    {
        // 取自 opus-codec.org 示例 Ogg Opus 文件首字节页（OpusHead，BOS）
        var page = new Byte[]
        {
            0x4F, 0x67, 0x67, 0x53, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x46, 0x5B, 0xF2, 0x35, 0x00, 0x00, 0x00, 0x00, 0xC2, 0x44, 0x10, 0x52, 0x01,
            0x13, 0x4F, 0x70, 0x75, 0x73, 0x48, 0x65, 0x61, 0x64, 0x01, 0x02, 0x64, 0x01,
            0x44, 0xAC, 0x00, 0x00, 0x00, 0x00, 0x00,
        };
        page[22] = page[23] = page[24] = page[25] = 0;

        Assert.Equal(0x521044C2u, OggCrc32.Compute(page));
    }

    [Fact(DisplayName = "OggCrc32 Crc32Table大小为256")]
    public void Crc32Table_Has256Entries()
    {
        Assert.Equal(256, OggCrc32.Crc32Table.Length);
    }

    [Fact(DisplayName = "OggCrc32相同输入产生相同结果")]
    public void Compute_SameInput_SameResult()
    {
        var data = new Byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };
        var crc1 = OggCrc32.Compute(data);
        var crc2 = OggCrc32.Compute(data);
        Assert.Equal(crc1, crc2);
    }

    [Fact(DisplayName = "OggCrc32不同输入产生不同结果")]
    public void Compute_DifferentInput_DifferentResult()
    {
        var crc1 = OggCrc32.Compute([0x01, 0x02, 0x03]);
        var crc2 = OggCrc32.Compute([0x01, 0x02, 0x04]);
        Assert.NotEqual(crc1, crc2);
    }
}
