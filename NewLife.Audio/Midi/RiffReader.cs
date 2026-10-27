using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NewLife.Audio.Midi;

/// <summary>RIFF 块</summary>
internal sealed class RiffChunk
{
    public String Id { get; set; }

    public String Type { get; set; }

    public ArraySegment<Byte> Data { get; set; }

    public List<RiffChunk> Children { get; } = [];

    public RiffChunk Find(String id)
    {
        foreach (var child in Children)
        {
            if (String.Equals(child.Id, id, StringComparison.OrdinalIgnoreCase)) return child;
        }

        return null;
    }

    public RiffChunk FindList(String type)
    {
        foreach (var child in Children)
        {
            if (String.Equals(child.Id, "LIST", StringComparison.OrdinalIgnoreCase) &&
                String.Equals(child.Type, type, StringComparison.OrdinalIgnoreCase))
                return child;
        }

        return null;
    }
}

/// <summary>RIFF 读取辅助</summary>
internal static class RiffReader
{
    /// <summary>解析 RIFF 文件，保留块在原始字节数组中的位置，避免复制大块采样数据</summary>
    /// <param name="data">文件字节</param>
    /// <returns>根块</returns>
    public static RiffChunk Parse(Byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (data.Length < 12) throw new InvalidDataException("The RIFF file is too short.");

        var id = GetId(data, 0);
        if (!String.Equals(id, "RIFF", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The file is not a RIFF file.");

        var size = ReadUInt32(data, 4);
        var end = (Int64)8 + size;
        if (end > data.Length) end = data.Length;

        var root = new RiffChunk
        {
            Id = id,
            Type = GetId(data, 8),
            Data = new ArraySegment<Byte>(data, 12, 0),
        };
        ParseChunks(root, data, 12, (Int32)end);

        return root;
    }

    /// <summary>读取块内的 16 位无符号整数</summary>
    /// <param name="chunk">块</param>
    /// <param name="offset">块内偏移</param>
    /// <returns>数值</returns>
    public static UInt16 ReadUInt16(RiffChunk chunk, Int32 offset) => ReadUInt16(chunk.Data.Array, chunk.Data.Offset + offset);

    /// <summary>读取块内的 16 位有符号整数</summary>
    /// <param name="chunk">块</param>
    /// <param name="offset">块内偏移</param>
    /// <returns>数值</returns>
    public static Int16 ReadInt16(RiffChunk chunk, Int32 offset) => ReadInt16(chunk.Data.Array, chunk.Data.Offset + offset);

    /// <summary>读取块内的 32 位无符号整数</summary>
    /// <param name="chunk">块</param>
    /// <param name="offset">块内偏移</param>
    /// <returns>数值</returns>
    public static UInt32 ReadUInt32(RiffChunk chunk, Int32 offset) => ReadUInt32(chunk.Data.Array, chunk.Data.Offset + offset);

    /// <summary>读取块内的 32 位有符号整数</summary>
    /// <param name="chunk">块</param>
    /// <param name="offset">块内偏移</param>
    /// <returns>数值</returns>
    public static Int32 ReadInt32(RiffChunk chunk, Int32 offset) => ReadInt32(chunk.Data.Array, chunk.Data.Offset + offset);

    /// <summary>读取块内的定长字符串，去掉尾部 0</summary>
    /// <param name="chunk">块</param>
    /// <param name="offset">块内偏移</param>
    /// <param name="length">最大长度</param>
    /// <returns>字符串</returns>
    public static String ReadString(RiffChunk chunk, Int32 offset, Int32 length)
    {
        var array = chunk.Data.Array;
        var start = chunk.Data.Offset + offset;
        var end = start + length;
        if (array == null || start < 0 || end > array.Length) return String.Empty;

        var count = 0;
        while (count < length && start + count < array.Length && array[start + count] != 0) count++;

        return Encoding.UTF8.GetString(array, start, count);
    }

    internal static String GetId(Byte[] data, Int32 offset)
    {
        if (data == null || offset < 0 || offset + 4 > data.Length) return String.Empty;

        return Encoding.ASCII.GetString(data, offset, 4);
    }

    internal static UInt16 ReadUInt16(Byte[] data, Int32 offset)
    {
        if (data == null || offset < 0 || offset + 2 > data.Length) return 0;

        return (UInt16)(data[offset] | data[offset + 1] << 8);
    }

    internal static Int16 ReadInt16(Byte[] data, Int32 offset) => unchecked((Int16)ReadUInt16(data, offset));

    internal static UInt32 ReadUInt32(Byte[] data, Int32 offset)
    {
        if (data == null || offset < 0 || offset + 4 > data.Length) return 0;

        return (UInt32)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);
    }

    internal static Int32 ReadInt32(Byte[] data, Int32 offset) => unchecked((Int32)ReadUInt32(data, offset));

    private static void ParseChunks(RiffChunk parent, Byte[] data, Int32 start, Int32 end)
    {
        var pos = start;
        while (pos + 8 <= end)
        {
            var id = GetId(data, pos);
            var size = ReadUInt32(data, pos + 4);
            var dataStart = pos + 8;
            if (dataStart > end) break;

            var dataEnd = dataStart + (Int32)Math.Min(size, (UInt32)(end - dataStart));
            var chunk = new RiffChunk
            {
                Id = id,
                Data = new ArraySegment<Byte>(data, dataStart, dataEnd - dataStart),
            };

            if (String.Equals(id, "LIST", StringComparison.OrdinalIgnoreCase) && dataEnd - dataStart >= 4)
            {
                chunk.Type = GetId(data, dataStart);
                ParseChunks(chunk, data, dataStart + 4, dataEnd);
            }

            parent.Children.Add(chunk);

            var next = dataStart + (Int64)size;
            if ((size & 1) != 0) next++;
            if (next <= pos) break;
            pos = next > end ? end : (Int32)next;
        }
    }
}
