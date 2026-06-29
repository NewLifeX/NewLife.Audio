namespace NewLife.Audio.Writers;

/// <summary>OGG 容器 CRC32 校验工具。采用 IEEE 802.3 多项式 0xEDB88320 查表法计算，与 OGG/RFC 3533 规范一致</summary>
public static class OggCrc32
{
    /// <summary>CRC32 查表法计算（IEEE 802.3 多项式 0xEDB88320），与 OGG 规范一致</summary>
    /// <param name="data">输入数据</param>
    /// <returns>CRC32 校验值</returns>
    public static UInt32 Compute(Byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = (crc >> 8) ^ Crc32Table[(crc ^ b) & 0xFF];
        }
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>CRC32 查表（IEEE 802.3 多项式 0xEDB88320）</summary>
    public static readonly UInt32[] Crc32Table = BuildTable();

    private static UInt32[] BuildTable()
    {
        var table = new UInt32[256];
        for (var i = 0u; i < 256; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            table[i] = crc;
        }
        return table;
    }
}
