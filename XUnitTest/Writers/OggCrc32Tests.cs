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
        // "123456789" CRC32 = 0xCBF43926
        var data = new Byte[] { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39 };
        var crc = OggCrc32.Compute(data);
        Assert.Equal(0xCBF43926u, crc);
    }

    [Fact(DisplayName = "OggCrc32单字节CRC32正确")]
    public void Compute_SingleByte()
    {
        var crc = OggCrc32.Compute(new Byte[] { 0x00 });
        Assert.NotEqual(0u, crc);
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
        var crc1 = OggCrc32.Compute(new Byte[] { 0x01, 0x02, 0x03 });
        var crc2 = OggCrc32.Compute(new Byte[] { 0x01, 0x02, 0x04 });
        Assert.NotEqual(crc1, crc2);
    }
}
