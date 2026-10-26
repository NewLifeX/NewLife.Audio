using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NewLife.Buffers;

namespace NewLife.Audio.Containers;

/// <summary>MP4/M4A 的 ilst 元数据构造与解析</summary>
/// <remarks>
/// 结构：<c>moov/udta/meta/ilst</c>，每个标签是一个以 4 字节标签名为类型的盒子，
/// 内含 <c>data</c> 子盒子（4 字节类型 + 4 字节语言 + 负载）。文本用类型 1（UTF-8），
/// 音轨号用类型 0（8 字节定长），封面用 13（JPEG）/14（PNG）。标签名含 0xA9 前缀，
/// 因此按字节而非 ASCII 字符串比较。
/// </remarks>
internal static class Mp4Metadata
{
    #region 标签名
    private static readonly Byte[] NameTitle = [0xA9, (Byte)'n', (Byte)'a', (Byte)'m'];
    private static readonly Byte[] NameArtist = [0xA9, (Byte)'A', (Byte)'R', (Byte)'T'];
    private static readonly Byte[] NameAlbum = [0xA9, (Byte)'a', (Byte)'l', (Byte)'b'];
    private static readonly Byte[] NameGenre = [0xA9, (Byte)'g', (Byte)'e', (Byte)'n'];
    private static readonly Byte[] NameYear = [0xA9, (Byte)'d', (Byte)'a', (Byte)'y'];
    private static readonly Byte[] NameComment = [0xA9, (Byte)'c', (Byte)'m', (Byte)'t'];
    private static readonly Byte[] NameTrack = [(Byte)'t', (Byte)'r', (Byte)'k', (Byte)'n'];
    private static readonly Byte[] NameCover = [(Byte)'c', (Byte)'o', (Byte)'v', (Byte)'r'];
    #endregion

    #region 构造
    /// <summary>构造 ilst 盒体（不含 8 字节盒头）</summary>
    /// <param name="metadata">元数据</param>
    /// <returns>ilst 内容；无可用标签时返回 null</returns>
    public static Byte[] BuildIlst(AudioMetadata metadata)
    {
        if (metadata == null) return null;

        using var ilst = new MemoryStream();
        AddText(ilst, NameTitle, metadata.Title);
        AddText(ilst, NameArtist, metadata.Artist);
        AddText(ilst, NameAlbum, metadata.Album);
        AddText(ilst, NameGenre, metadata.Genre);
        if (metadata.Year > 0) AddText(ilst, NameYear, metadata.Year.ToString("D4"));
        AddText(ilst, NameComment, metadata.Comment);

        if (metadata.TrackNumber > 0)
        {
            var payload = new Byte[8];
            payload[2] = (Byte)(metadata.TrackNumber >> 8);
            payload[3] = (Byte)metadata.TrackNumber;
            AddData(ilst, NameTrack, 0, payload);
        }

        if (metadata.CoverImage is { Length: > 0 } cover)
        {
            var type = cover.Length > 3 && cover[0] == 0x89 && cover[1] == 'P' ? 14 : 13;
            AddData(ilst, NameCover, type, cover);
        }

        return ilst.Length > 0 ? ilst.ToArray() : null;
    }

    private static void AddText(Stream ilst, Byte[] name, String value)
    {
        if (value.IsNullOrEmpty()) return;

        AddData(ilst, name, 1, Encoding.UTF8.GetBytes(value));
    }

    private static void AddData(Stream ilst, Byte[] name, Int32 dataType, Byte[] payload)
    {
        using var data = new MemoryStream();
        WriteUInt32BE(data, (UInt32)dataType);
        WriteUInt32BE(data, 0);                        // 语言/国家
        data.Write(payload, 0, payload.Length);
        var dataBox = BuildBox("data", data.ToArray());

        WriteUInt32BE(ilst, (UInt32)(dataBox.Length + 8));
        ilst.Write(name, 0, 4);
        ilst.Write(dataBox, 0, dataBox.Length);
    }

    /// <summary>构造一个以 ASCII 四字符码为类型的盒子</summary>
    /// <param name="type">盒类型（4 个 ASCII 字符）</param>
    /// <param name="payload">盒体</param>
    /// <returns>完整盒子字节</returns>
    public static Byte[] BuildBox(String type, Byte[] payload)
    {
        var box = new Byte[8 + (payload?.Length ?? 0)];
        var length = box.Length;
        box[0] = (Byte)(length >> 24);
        box[1] = (Byte)(length >> 16);
        box[2] = (Byte)(length >> 8);
        box[3] = (Byte)length;
        for (var i = 0; i < 4; i++) box[4 + i] = (Byte)type[i];
        if (payload != null && payload.Length > 0) Array.Copy(payload, 0, box, 8, payload.Length);

        return box;
    }

    private static void WriteUInt32BE(Stream stream, UInt32 value)
    {
        stream.WriteByte((Byte)(value >> 24));
        stream.WriteByte((Byte)(value >> 16));
        stream.WriteByte((Byte)(value >> 8));
        stream.WriteByte((Byte)value);
    }
    #endregion

    #region 解析
    /// <summary>解析 udta 盒体，把 ilst 标签填入 <paramref name="metadata"/></summary>
    /// <param name="udta">udta 盒体（不含盒头）</param>
    /// <param name="metadata">输出元数据</param>
    /// <returns>识别到 ilst 返回 true</returns>
    public static Boolean TryParseUdta(ReadOnlySpan<Byte> udta, AudioMetadata metadata)
    {
        var found = false;
        foreach (var (type, body) in Walk(udta))
        {
            if (!IsType(type, "meta")) continue;

            // meta 是 full box：跳过 4 字节 version/flags
            var inner = body.Length > 4 ? body.AsSpan(4) : ReadOnlySpan<Byte>.Empty;
            foreach (var (innerType, innerBody) in Walk(inner))
            {
                if (!IsType(innerType, "ilst")) continue;

                found = true;
                ParseIlst(innerBody, metadata);
            }
        }

        return found;
    }

    private static void ParseIlst(ReadOnlySpan<Byte> ilst, AudioMetadata metadata)
    {
        foreach (var (type, body) in Walk(ilst))
        {
            if (body.Length < 16) continue;

            // data 子盒：类型(4) + 语言(4) + 负载
            if (!IsType(body.AsSpan(4, 4), "data")) continue;

            var dataType = body[8] << 24 | body[9] << 16 | body[10] << 8 | body[11];
            var payload = body.AsSpan(16);

            if (Is(type, NameCover))
            {
                metadata.CoverImage = payload.ToArray();
                continue;
            }

            if (Is(type, NameTrack))
            {
                if (payload.Length >= 4) metadata.TrackNumber = payload[2] << 8 | payload[3];
                continue;
            }

            if (dataType != 1) continue;

            var text = Encoding.UTF8.GetString(payload.ToArray());
            if (Is(type, NameTitle)) metadata.Title = text;
            else if (Is(type, NameArtist)) metadata.Artist = text;
            else if (Is(type, NameAlbum)) metadata.Album = text;
            else if (Is(type, NameGenre)) metadata.Genre = text;
            else if (Is(type, NameComment)) metadata.Comment = text;
            else if (Is(type, NameYear) && Int32.TryParse(text.Length >= 4 ? text.Substring(0, 4) : text, out var year)) metadata.Year = year;
        }
    }

    private static Boolean Is(Byte[] type, Byte[] name)
    {
        if (type.Length != 4) return false;

        for (var i = 0; i < 4; i++)
        {
            if (type[i] != name[i]) return false;
        }

        return true;
    }

    private static Boolean IsType(ReadOnlySpan<Byte> type, String name)
    {
        if (type.Length != 4) return false;

        for (var i = 0; i < 4; i++)
        {
            if (type[i] != (Byte)name[i]) return false;
        }

        return true;
    }

    /// <summary>遍历盒子，返回类型字节与盒体</summary>
    private static List<(Byte[] Type, Byte[] Body)> Walk(ReadOnlySpan<Byte> data)
    {
        var list = new List<(Byte[], Byte[])>();
        var reader = new SpanReader(data.ToArray()) { IsLittleEndian = false };
        while (reader.Available >= 8)
        {
            var size = (Int32)reader.ReadUInt32();
            var code = reader.ReadBytes(4).ToArray();
            if (size < 8 || size - 8 > reader.Available) break;

            list.Add((code, reader.ReadBytes(size - 8).ToArray()));
        }

        return list;
    }
    #endregion
}
