namespace NewLife.Audio.Writers;

/// <summary>OGG 容器 CRC32 校验工具。采用 Ogg 规范多项式 0x04C11DB7（初值 0、无最终异或、MSB 优先），与 RFC 3533 一致</summary>
public static class OggCrc32
{
    /// <summary>CRC32 查表法计算（Ogg 规范多项式 0x04C11DB7），与 RFC 3533 一致</summary>
    /// <param name="data">输入数据</param>
    /// <returns>CRC32 校验值</returns>
    public static UInt32 Compute(ReadOnlySpan<Byte> data)
    {
        var crc = 0u;
        foreach (var b in data)
        {
            crc = (crc << 8) ^ Crc32Table[((crc >> 24) & 0xFF) ^ b];
        }
        return crc;
    }

    /// <summary>CRC32 查表（Ogg 规范多项式 0x04C11DB7）</summary>
    public static readonly UInt32[] Crc32Table = BuildTable();

    private static UInt32[] BuildTable()
    {
        var table = new UInt32[256];
        for (var i = 0u; i < 256; i++)
        {
            var crc = i << 24;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
            }
            table[i] = crc;
        }
        return table;
    }
}
