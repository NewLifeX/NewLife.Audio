using System;

namespace NewLife.Audio.Codecs;

/// <summary>MP3 Huffman 编码表</summary>
/// <remarks>
/// 编码表由解码表 <see cref="Mp3Huffman"/> 的树形结构反推得到，保证编码与解码使用同一份
/// ISO/IEC 11172-3 Table B.7 码字定义，避免维护两份容易不一致的常量表。<br/>
/// 每个表项以 (x &lt;&lt; 4 | y) 为键，值为码字与码长；码长为 0 表示该表无法表示该组合。<br/>
/// 表 16~23 支持 ESC 扩展（值为 15 时附加 linbits 位），最大可编码幅值为 14 + 2^linbits。
/// </remarks>
internal static class Mp3HuffmanEncoder
{
    #region 常量
    /// <summary>可用于长块的 table_select 候选（0 表示全零，4/14 未定义）</summary>
    public static readonly Int32[] LongTables = [1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 16, 17, 18, 19, 20, 21, 22, 23];

    /// <summary>Huffman 码字直接表示的最大幅值（超过后使用 ESC 扩展）</summary>
    public const Int32 MaxValue = 15;

    /// <summary>全表统一的最大可编码幅值（表 23 的 linbits=13）</summary>
    public const Int32 MaxEscapeValue = 14 + (1 << 13);
    #endregion

    #region 字段
    private static readonly Int32[,] _codes = new Int32[32, 256];
    private static readonly Byte[,] _lens = new Byte[32, 256];
    #endregion

    #region 构造
    static Mp3HuffmanEncoder()
    {
        for (var table = 1; table < 24; table++)
        {
            var offset = Mp3Huffman.GetOffset(table);
            if (offset < 0) continue;

            Build(table, offset, offset, 5, 0, 0);
        }
    }
    #endregion

    #region 方法
    /// <summary>获取指定表中一对系数的编码位数</summary>
    /// <param name="table">table_select 值</param>
    /// <param name="x">第一个系数的绝对值</param>
    /// <param name="y">第二个系数的绝对值</param>
    /// <returns>码字位数（不含符号位），无法编码返回 -1</returns>
    public static Int32 GetBits(Int32 table, Int32 x, Int32 y)
    {
        if (table <= 0 || table >= 32) return x == 0 && y == 0 ? 0 : -1;

        var linbits = (Int32)Mp3Huffman.LinBits[table];
        var limit = linbits > 0 ? 14 + (1 << linbits) : MaxValue;
        if (x > limit || y > limit) return -1;

        var len = _lens[table, (Math.Min(x, MaxValue) << 4) | Math.Min(y, MaxValue)];
        if (len == 0) return -1;

        var total = (Int32)len;
        if (x >= MaxValue && linbits > 0) total += linbits;
        if (y >= MaxValue && linbits > 0) total += linbits;

        return total;
    }

    /// <summary>写入一对系数（含 ESC 扩展与符号位）</summary>
    /// <param name="writer">比特写入器</param>
    /// <param name="table">table_select 值</param>
    /// <param name="x">第一个系数的值</param>
    /// <param name="y">第二个系数的值</param>
    /// <returns>是否写入成功</returns>
    public static Boolean TryWrite(Mp3BitWriter writer, Int32 table, Int32 x, Int32 y)
    {
        var ax = x < 0 ? -x : x;
        var ay = y < 0 ? -y : y;

        var linbits = table > 0 && table < 32 ? (Int32)Mp3Huffman.LinBits[table] : 0;
        var limit = linbits > 0 ? 14 + (1 << linbits) : MaxValue;
        var key = (Math.Min(ax, MaxValue) << 4) | Math.Min(ay, MaxValue);
        var len = table > 0 && table < 32 && ax <= limit && ay <= limit ? _lens[table, key] : (Byte)0;
        if (len == 0)
        {
            if (ax != 0 || ay != 0) return false;

            return true;
        }

        writer.Write(_codes[table, key], len);
        // 与解码端逐值交错：ESC 扩展位与其符号位成对写入
        if (ax >= MaxValue && linbits > 0) writer.Write(ax - MaxValue, linbits);
        if (ax != 0) writer.WriteBit(x < 0);
        if (ay >= MaxValue && linbits > 0) writer.Write(ay - MaxValue, linbits);
        if (ay != 0) writer.WriteBit(y < 0);

        return true;
    }

    /// <summary>递归展开解码表，生成码字映射</summary>
    /// <param name="table">表号</param>
    /// <param name="rootOffset">表根节点在解码表中的偏移（子表偏移均相对根节点）</param>
    /// <param name="nodeOffset">当前节点在解码表中的偏移</param>
    /// <param name="width">当前节点的索引位宽</param>
    /// <param name="prefix">已累积的码字前缀</param>
    /// <param name="prefixLen">已累积的码字长度</param>
    private static void Build(Int32 table, Int32 rootOffset, Int32 nodeOffset, Int32 width, Int32 prefix, Int32 prefixLen)
    {
        var count = 1 << width;
        for (var i = 0; i < count; i++)
        {
            var entry = Mp3Huffman.Tabs[nodeOffset + i];
            var code = (prefix << width) | i;
            if (entry < 0)
            {
                // 负表项：低 3 位为子表索引位宽，其余位为相对根节点的子表偏移
                var childWidth = entry & 7;
                var childBase = rootOffset - (entry >> 3);
                Build(table, rootOffset, childBase, childWidth, code, prefixLen + width);
            }
            else
            {
                var len = entry >> 8;
                if (len <= 0) continue;

                var x = entry & 0xF;
                var y = (entry >> 4) & 0xF;
                if (x > MaxValue || y > MaxValue) continue;

                var key = (x << 4) | y;
                _codes[table, key] = code >> (width - len);
                _lens[table, key] = (Byte)(prefixLen + len);
            }
        }
    }
    #endregion
}
