namespace NewLife.Audio.Writers;

/// <summary>二进制写入辅助方法。提供小端字节序的整数写入和 CRC32 计算，供 OGG/WAV 等容器格式使用</summary>
internal static class BinaryWriterHelper
{
    /// <summary>写入 4 字节 ASCII 标识（FourCC）</summary>
    /// <param name="buffer">目标缓冲区</param>
    /// <param name="offset">当前偏移，写入后自动递增 4</param>
    /// <param name="fourCC">4 字符标识字符串</param>
    public static void WriteFourCC(Byte[] buffer, ref Int32 offset, String fourCC)
    {
        buffer[offset] = (Byte)fourCC[0];
        buffer[offset + 1] = (Byte)fourCC[1];
        buffer[offset + 2] = (Byte)fourCC[2];
        buffer[offset + 3] = (Byte)fourCC[3];
        offset += 4;
    }

    /// <summary>写入 2 字节小端有符号整数</summary>
    /// <param name="buffer">目标缓冲区</param>
    /// <param name="offset">当前偏移，写入后自动递增 2</param>
    /// <param name="value">16 位有符号整数值</param>
    public static void WriteInt16LE(Byte[] buffer, ref Int32 offset, Int16 value)
    {
        buffer[offset] = (Byte)(value & 0xFF);
        buffer[offset + 1] = (Byte)((value >> 8) & 0xFF);
        offset += 2;
    }

    /// <summary>写入 4 字节小端有符号整数</summary>
    /// <param name="buffer">目标缓冲区</param>
    /// <param name="offset">当前偏移，写入后自动递增 4</param>
    /// <param name="value">32 位有符号整数值</param>
    public static void WriteInt32LE(Byte[] buffer, ref Int32 offset, Int32 value)
    {
        buffer[offset] = (Byte)(value & 0xFF);
        buffer[offset + 1] = (Byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (Byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (Byte)((value >> 24) & 0xFF);
        offset += 4;
    }

    /// <summary>写入 8 字节小端有符号整数</summary>
    /// <param name="buffer">目标缓冲区</param>
    /// <param name="offset">当前偏移，写入后自动递增 8</param>
    /// <param name="value">64 位有符号整数值</param>
    public static void WriteInt64LE(Byte[] buffer, ref Int32 offset, Int64 value)
    {
        buffer[offset] = (Byte)(value & 0xFF);
        buffer[offset + 1] = (Byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (Byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (Byte)((value >> 24) & 0xFF);
        buffer[offset + 4] = (Byte)((value >> 32) & 0xFF);
        buffer[offset + 5] = (Byte)((value >> 40) & 0xFF);
        buffer[offset + 6] = (Byte)((value >> 48) & 0xFF);
        buffer[offset + 7] = (Byte)((value >> 56) & 0xFF);
        offset += 8;
    }

    /// <summary>写入 4 字节小端无符号整数</summary>
    /// <param name="buffer">目标缓冲区</param>
    /// <param name="offset">当前偏移，写入后自动递增 4</param>
    /// <param name="value">32 位无符号整数值</param>
    public static void WriteUInt32LE(Byte[] buffer, ref Int32 offset, UInt32 value)
    {
        buffer[offset] = (Byte)(value & 0xFF);
        buffer[offset + 1] = (Byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (Byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (Byte)((value >> 24) & 0xFF);
        offset += 4;
    }

    /// <summary>CRC32 查表法计算（IEEE 802.3 多项式 0xEDB88320），与 OGG 规范一致</summary>
    /// <param name="data">输入数据</param>
    /// <returns>CRC32 校验值</returns>
    public static UInt32 Crc32Compute(Byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = (crc >> 8) ^ Crc32Table[(crc ^ b) & 0xFF];
        }
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>CRC32 查表（IEEE 802.3 多项式 0xEDB88320）</summary>
    public static readonly UInt32[] Crc32Table = BuildCrc32Table();

    private static UInt32[] BuildCrc32Table()
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
