using System;
using System.Collections.Generic;
using System.Text;

namespace NewLife.Audio.Containers;

/// <summary>WAV 的 RIFF LIST/INFO 标签块读写</summary>
/// <remarks>
/// INFO 列表用四个大写字母的块标识承载常见标签：INAM 标题、IART 艺术家、IPRD 专辑、
/// IGNR 流派、ICMT 注释、ICRD 年份、ITRK 音轨号。文本以 UTF-8 编码并以 0 结尾，块长度按偶数字节对齐。
/// </remarks>
internal static class WaveInfoChunk
{
    #region 常量
    private const UInt32 ListMagic = 0x5453494C;   // "LIST"
    private const UInt32 InfoMagic = 0x4F464E49;   // "INFO"
    #endregion

    #region 写入
    /// <summary>构造 LIST/INFO 块（含 8 字节块头）；元数据全空时返回 null</summary>
    /// <param name="metadata">元数据</param>
    /// <returns>块字节，无可用标签时返回 null</returns>
    public static Byte[] Build(AudioMetadata metadata)
    {
        if (metadata == null) return null;

        var body = new List<Byte>();
        AddTag(body, "INAM", metadata.Title);
        AddTag(body, "IART", metadata.Artist);
        AddTag(body, "IPRD", metadata.Album);
        AddTag(body, "IGNR", metadata.Genre);
        AddTag(body, "ICMT", metadata.Comment);
        if (metadata.Year > 0) AddTag(body, "ICRD", metadata.Year.ToString());
        if (metadata.TrackNumber > 0) AddTag(body, "ITRK", metadata.TrackNumber.ToString());

        if (body.Count == 0) return null;

        var chunk = new Byte[12 + body.Count];
        WriteUInt32(chunk, 0, ListMagic);
        WriteUInt32(chunk, 4, (UInt32)(4 + body.Count));
        WriteUInt32(chunk, 8, InfoMagic);
        body.CopyTo(chunk, 12);

        return chunk;
    }

    private static void AddTag(List<Byte> body, String id, String value)
    {
        if (value.IsNullOrEmpty()) return;

        var text = Encoding.UTF8.GetBytes(value);
        body.Add((Byte)id[0]);
        body.Add((Byte)id[1]);
        body.Add((Byte)id[2]);
        body.Add((Byte)id[3]);
        WriteUInt32(body, (UInt32)(text.Length + 1));
        body.AddRange(text);
        body.Add(0);
        if ((text.Length + 1) % 2 == 1) body.Add(0);
    }
    #endregion

    #region 读取
    /// <summary>解析 LIST/INFO 块体（不含 8 字节块头）</summary>
    /// <param name="data">块数据</param>
    /// <param name="offset">块体起始位置</param>
    /// <param name="length">块体长度</param>
    /// <param name="metadata">输出元数据</param>
    /// <returns>识别到 INFO 列表返回 true</returns>
    public static Boolean TryParse(Byte[] data, Int32 offset, Int32 length, AudioMetadata metadata)
    {
        if (data == null || metadata == null) return false;
        if (offset < 0 || length < 4 || offset + length > data.Length) return false;
        if (ReadUInt32(data, offset) != InfoMagic) return false;

        var end = offset + length;
        var pos = offset + 4;
        while (pos + 8 <= end)
        {
            var id = new String(new Char[] { (Char)data[pos], (Char)data[pos + 1], (Char)data[pos + 2], (Char)data[pos + 3] });
            var size = (Int32)ReadUInt32(data, pos + 4);
            pos += 8;
            if (size < 0 || pos + size > end) break;

            var value = ReadText(data, pos, size);
            switch (id)
            {
                case "INAM": metadata.Title = value; break;
                case "IART": metadata.Artist = value; break;
                case "IPRD": metadata.Album = value; break;
                case "IGNR": metadata.Genre = value; break;
                case "ICMT": metadata.Comment = value; break;
                case "ICRD":
                    if (Int32.TryParse(value.Length >= 4 ? value.Substring(0, 4) : value, out var year)) metadata.Year = year;
                    break;
                case "ITRK":
                    if (Int32.TryParse(value, out var track)) metadata.TrackNumber = track;
                    break;
            }

            pos += size;
            if ((size & 1) == 1) pos++;
        }

        return true;
    }

    private static String ReadText(Byte[] data, Int32 offset, Int32 size)
    {
        var length = size;
        while (length > 0 && data[offset + length - 1] == 0) length--;

        return length > 0 ? Encoding.UTF8.GetString(data, offset, length) : null;
    }
    #endregion

    #region 辅助
    private static UInt32 ReadUInt32(Byte[] data, Int32 offset) =>
        (UInt32)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);

    private static void WriteUInt32(Byte[] data, Int32 offset, UInt32 value)
    {
        data[offset] = (Byte)value;
        data[offset + 1] = (Byte)(value >> 8);
        data[offset + 2] = (Byte)(value >> 16);
        data[offset + 3] = (Byte)(value >> 24);
    }

    private static void WriteUInt32(List<Byte> body, UInt32 value)
    {
        body.Add((Byte)value);
        body.Add((Byte)(value >> 8));
        body.Add((Byte)(value >> 16));
        body.Add((Byte)(value >> 24));
    }
    #endregion
}

