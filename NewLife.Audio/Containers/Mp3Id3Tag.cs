using NewLife.Audio.DSP;

namespace NewLife.Audio.Containers;

/// <summary>ID3v2 标签读写</summary>
/// <remarks>
/// 支持 ID3v2.3 / ID3v2.4 的读取（文本编码 0~3 全部兼容），写入统一使用 v2.4 + UTF-8。
/// 仅处理常见文本帧与封面帧：TIT2/TPE1/TALB/TYER/TRCK/TCON/COMM/APIC。
/// </remarks>
internal static class Mp3Id3Tag
{
    #region 常量
    /// <summary>标签头长度</summary>
    public const Int32 HeaderSize = 10;

    private static readonly String[] GenreNames =
    [
        "Blues", "Classic Rock", "Country", "Dance", "Disco", "Funk", "Grunge", "Hip-Hop",
        "Jazz", "Metal", "New Age", "Oldies", "Other", "Pop", "R&B", "Rap", "Reggae", "Rock",
        "Techno", "Industrial", "Alternative", "Ska", "Death Metal", "Pranks", "Soundtrack",
        "Euro-Techno", "Ambient", "Trip-Hop", "Vocal", "Jazz+Funk", "Fusion", "Trance",
    ];
    #endregion

    #region 读取
    /// <summary>尝试从数据头部解析 ID3v2 标签</summary>
    /// <param name="data">文件数据</param>
    /// <param name="metadata">输出元数据</param>
    /// <param name="length">标签总长度（含头部）</param>
    /// <returns>是否存在有效的 ID3v2 标签</returns>
    public static Boolean TryRead(Byte[] data, AudioMetadata metadata, out Int32 length)
    {
        length = 0;
        if (data.Length < HeaderSize) return false;
        if (data[0] != 'I' || data[1] != 'D' || data[2] != '3') return false;

        var version = data[3];
        if (version < 3 || version > 4) return false;

        var size = (data[6] & 0x7F) << 21 | (data[7] & 0x7F) << 14 | (data[8] & 0x7F) << 7 | (data[9] & 0x7F);
        length = HeaderSize + size;
        if (length > data.Length) length = data.Length;

        var pos = HeaderSize;
        // 扩展头（v2.3 标志位 0x40）
        if ((data[5] & 0x40) != 0 && pos + 4 <= length)
        {
            var ext = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            if (version == 4) ext = (data[pos] & 0x7F) << 21 | (data[pos + 1] & 0x7F) << 14 | (data[pos + 2] & 0x7F) << 7 | (data[pos + 3] & 0x7F);
            pos += ext + 4;
        }

        while (pos + 10 <= length)
        {
            if (data[pos] == 0) break;

            var id = System.Text.Encoding.ASCII.GetString(data, pos, 4);
            var frameSize = version == 4
                ? (data[pos + 4] & 0x7F) << 21 | (data[pos + 5] & 0x7F) << 14 | (data[pos + 6] & 0x7F) << 7 | (data[pos + 7] & 0x7F)
                : (data[pos + 4] << 24) | (data[pos + 5] << 16) | (data[pos + 6] << 8) | data[pos + 7];
            var frameStart = pos + 10;
            if (frameSize <= 0 || frameStart + frameSize > length) break;

            ReadFrame(id, data, frameStart, frameSize, metadata);
            pos = frameStart + frameSize;
        }

        return true;
    }

    /// <summary>跳过 ID3v2 标签，返回首个音频帧的偏移</summary>
    /// <param name="data">文件数据</param>
    /// <returns>音频起始偏移</returns>
    public static Int32 Skip(Byte[] data)
    {
        if (data.Length < HeaderSize) return 0;
        if (data[0] != 'I' || data[1] != 'D' || data[2] != '3') return 0;

        var size = (data[6] & 0x7F) << 21 | (data[7] & 0x7F) << 14 | (data[8] & 0x7F) << 7 | (data[9] & 0x7F);
        var skip = HeaderSize + size;
        if ((data[5] & 0x10) != 0) skip += 10;

        return skip < data.Length ? skip : data.Length;
    }

    private static void ReadFrame(String id, Byte[] data, Int32 offset, Int32 size, AudioMetadata metadata)
    {
        switch (id)
        {
            case "TIT2":
                metadata.Title = ReadText(data, offset, size);
                break;
            case "TPE1":
                metadata.Artist = ReadText(data, offset, size);
                break;
            case "TALB":
                metadata.Album = ReadText(data, offset, size);
                break;
            case "TYER":
            case "TDRC":
                metadata.Year = ReadText(data, offset, size).ToInt();
                break;
            case "TRCK":
                metadata.TrackNumber = ReadText(data, offset, size).ToInt();
                break;
            case "TCON":
                metadata.Genre = ReadText(data, offset, size);
                break;
            case "COMM":
                metadata.Comment = ReadComment(data, offset, size);
                break;
            case "APIC":
                metadata.CoverImage = ReadPicture(data, offset, size);
                break;
        }
    }

    private static String ReadText(Byte[] data, Int32 offset, Int32 size)
    {
        if (size <= 1) return null;

        var encoding = data[offset];
        var text = Decode(data, offset + 1, size - 1, encoding);

        return text.TrimEnd('\0').Trim();
    }

    private static String ReadComment(Byte[] data, Int32 offset, Int32 size)
    {
        if (size <= 4) return null;

        var encoding = data[offset];
        // 跳过语言（3 字节）与简短描述
        var pos = offset + 4;
        var end = offset + size;
        if (encoding == 1 || encoding == 2)
        {
            while (pos + 1 < end && !(data[pos] == 0 && data[pos + 1] == 0)) pos += 2;
            pos += 2;
        }
        else
        {
            while (pos < end && data[pos] != 0) pos++;
            pos++;
        }

        return pos < end ? Decode(data, pos, end - pos, encoding).TrimEnd('\0').Trim() : null;
    }

    private static Byte[] ReadPicture(Byte[] data, Int32 offset, Int32 size)
    {
        if (size <= 4) return null;

        var encoding = data[offset];
        var pos = offset + 1;
        var end = offset + size;

        // MIME 类型（ASCII，以 0 结尾）
        while (pos < end && data[pos] != 0) pos++;
        pos++;

        // 图片类型 1 字节
        pos++;

        // 描述文本
        if (encoding == 1 || encoding == 2)
        {
            while (pos + 1 < end && !(data[pos] == 0 && data[pos + 1] == 0)) pos += 2;
            pos += 2;
        }
        else
        {
            while (pos < end && data[pos] != 0) pos++;
            pos++;
        }

        if (pos >= end) return null;

        var picture = new Byte[end - pos];
        Array.Copy(data, pos, picture, 0, picture.Length);

        return picture;
    }

    private static String Decode(Byte[] data, Int32 offset, Int32 size, Int32 encoding)
    {
        if (size <= 0) return null;

        return encoding switch
        {
            1 => System.Text.Encoding.Unicode.GetString(data, offset, size),
            2 => System.Text.Encoding.BigEndianUnicode.GetString(data, offset, size),
            3 => System.Text.Encoding.UTF8.GetString(data, offset, size),
            _ => System.Text.Encoding.GetEncoding(28591).GetString(data, offset, size),
        };
    }
    #endregion

    #region 写入
    /// <summary>构造 ID3v2.4 标签</summary>
    /// <param name="metadata">元数据</param>
    /// <param name="padding">尾部预留字节数，便于后续原地扩展</param>
    /// <returns>标签字节；无任何字段时返回空数组</returns>
    public static Byte[] Write(AudioMetadata metadata, Int32 padding = 0)
    {
        if (metadata == null) return [];

        var frames = new MemoryStream();
        AddText(frames, "TIT2", metadata.Title);
        AddText(frames, "TPE1", metadata.Artist);
        AddText(frames, "TALB", metadata.Album);
        AddText(frames, "TCON", metadata.Genre);
        if (metadata.Year > 0) AddText(frames, "TDRC", metadata.Year.ToString());
        if (metadata.TrackNumber > 0) AddText(frames, "TRCK", metadata.TrackNumber.ToString());
        if (!metadata.Comment.IsNullOrEmpty()) AddComment(frames, metadata.Comment);
        if (metadata.CoverImage != null && metadata.CoverImage.Length > 0) AddPicture(frames, metadata.CoverImage);

        var body = frames.ToArray();
        if (body.Length == 0 && padding <= 0) return [];

        var total = body.Length + padding;
        var tag = new Byte[HeaderSize + total];
        tag[0] = (Byte)'I';
        tag[1] = (Byte)'D';
        tag[2] = (Byte)'3';
        tag[3] = 4;
        tag[4] = 0;
        tag[5] = 0;
        tag[6] = (Byte)((total >> 21) & 0x7F);
        tag[7] = (Byte)((total >> 14) & 0x7F);
        tag[8] = (Byte)((total >> 7) & 0x7F);
        tag[9] = (Byte)(total & 0x7F);
        Array.Copy(body, 0, tag, HeaderSize, body.Length);

        return tag;
    }

    private static void AddText(MemoryStream frames, String id, String value)
    {
        if (value.IsNullOrEmpty()) return;

        var text = System.Text.Encoding.UTF8.GetBytes(value);
        WriteFrame(frames, id, [3, .. text]);
    }

    private static void AddComment(MemoryStream frames, String value)
    {
        var text = System.Text.Encoding.UTF8.GetBytes(value);
        var payload = new Byte[5 + text.Length];
        payload[0] = 3;
        // payload[1..3] 为语言（eng），payload[4] 为空的简短描述结束符
        payload[1] = (Byte)'e';
        payload[2] = (Byte)'n';
        payload[3] = (Byte)'g';
        payload[4] = 0;
        Array.Copy(text, 0, payload, 5, text.Length);
        WriteFrame(frames, "COMM", payload);
    }

    private static void AddPicture(MemoryStream frames, Byte[] picture)
    {
        var mime = System.Text.Encoding.ASCII.GetBytes("image/jpeg");
        var payload = new Byte[1 + mime.Length + 1 + 1 + 1 + picture.Length];
        var pos = 0;
        payload[pos++] = 3;
        Array.Copy(mime, 0, payload, pos, mime.Length);
        pos += mime.Length;
        payload[pos++] = 0;
        payload[pos++] = 3;      // 封面（front cover）
        payload[pos++] = 0;      // 空描述
        Array.Copy(picture, 0, payload, pos, picture.Length);
        WriteFrame(frames, "APIC", payload);
    }

    private static void WriteFrame(MemoryStream frames, String id, Byte[] payload)
    {
        var size = payload.Length;
        var header = new Byte[10];
        var ascii = System.Text.Encoding.ASCII.GetBytes(id);
        Array.Copy(ascii, 0, header, 0, 4);
        header[4] = (Byte)((size >> 21) & 0x7F);
        header[5] = (Byte)((size >> 14) & 0x7F);
        header[6] = (Byte)((size >> 7) & 0x7F);
        header[7] = (Byte)(size & 0x7F);

        frames.Write(header, 0, header.Length);
        frames.Write(payload, 0, payload.Length);
    }
    #endregion
}
