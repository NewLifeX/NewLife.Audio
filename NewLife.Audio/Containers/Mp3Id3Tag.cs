using System.IO.Compression;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Containers;

/// <summary>ID3v2 标签读写</summary>
/// <remarks>
/// 支持 ID3v2.2 / v2.3 / v2.4 的读取（文本编码 0~3 全部兼容，含反同步与 zlib 压缩帧），
/// 写入统一使用 v2.4 + UTF-8；标签变小时可在原标签区内原地重写，音频偏移保持不变。
/// 仅处理常见文本帧与封面帧：TIT2/TPE1/TALB/TYER/TRCK/TCON/COMM/APIC（v2.2 映射 TT2/TP1/TAL/TYE/TRK/TCO/COM/PIC）。
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
        "Classical", "Instrumental", "Acid", "House", "Game", "Sound Clip", "Gospel", "Noise",
        "AlternRock", "Bass", "Soul", "Punk", "Space", "Meditative", "Instrumental Pop",
        "Instrumental Rock", "Ethnic", "Gothic", "Darkwave", "Techno-Industrial", "Electronic",
        "Pop-Folk", "Eurodance", "Dream", "Southern Rock", "Comedy", "Cult", "Gangsta", "Top 40",
        "Christian Rap", "Pop/Funk", "Jungle", "Native American", "Cabaret", "New Wave",
        "Psychedelic", "Rave", "Showtunes", "Trailer", "Lo-Fi", "Tribal", "Acid Punk",
        "Acid Jazz", "Polka", "Retro", "Musical", "Rock & Roll", "Hard Rock",
    ];
    #endregion

    #region ID3v1
    /// <summary>ID3v1 标签长度</summary>
    public const Int32 V1Size = 128;

    /// <summary>尝试从数据尾部解析 ID3v1 标签</summary>
    /// <param name="data">包含标签的缓冲区</param>
    /// <param name="offset">标签起始偏移</param>
    /// <param name="metadata">目标元数据，已存在的字段不会被覆盖</param>
    /// <returns>是否为合法 ID3v1 标签</returns>
    /// <remarks>
    /// 只解析固定 128 字节布局；文本按 Latin-1 处理（ID3v1 无编码声明）。
    /// 已存在的字段不覆盖，因此 ID3v2 标签优先于 ID3v1。
    /// </remarks>
    public static Boolean TryReadV1(Byte[] data, Int32 offset, AudioMetadata metadata)
    {
        if (data == null || metadata == null) return false;
        if (offset < 0 || offset + V1Size > data.Length) return false;
        if (data[offset] != (Byte)'T' || data[offset + 1] != (Byte)'A' || data[offset + 2] != (Byte)'G') return false;

        if (metadata.Title.IsNullOrEmpty()) metadata.Title = ReadV1Text(data, offset + 3, 30);
        if (metadata.Artist.IsNullOrEmpty()) metadata.Artist = ReadV1Text(data, offset + 33, 30);
        if (metadata.Album.IsNullOrEmpty()) metadata.Album = ReadV1Text(data, offset + 63, 30);
        if (metadata.Year == 0) metadata.Year = ReadV1Text(data, offset + 93, 4).ToInt();

        // ID3v1.1：注释第 29 字节为 0 且第 30 字节非 0 时，末字节表示音轨号
        var track = 0;
        var commentSize = 30;
        if (data[offset + 125] == 0 && data[offset + 126] != 0)
        {
            track = data[offset + 126];
            commentSize = 28;
        }
        if (metadata.Comment.IsNullOrEmpty()) metadata.Comment = ReadV1Text(data, offset + 97, commentSize);
        if (metadata.TrackNumber == 0) metadata.TrackNumber = track;

        var genre = data[offset + 127];
        if (metadata.Genre.IsNullOrEmpty() && genre < GenreNames.Length) metadata.Genre = GenreNames[genre];

        return true;
    }

    /// <summary>构造 ID3v1 标签（128 字节，含音轨号的 v1.1 布局）</summary>
    /// <param name="metadata">音频元数据</param>
    /// <returns>128 字节标签；无任何可用字段时返回空数组</returns>
    /// <remarks>ID3v1 固定 Latin-1 单字节编码，无法表示的字符写入 '?'；中文等非拉丁字符请同时依赖 ID3v2 标签。</remarks>
    public static Byte[] WriteV1(AudioMetadata metadata)
    {
        if (metadata == null) return [];

        var hasValue = !metadata.Title.IsNullOrEmpty() || !metadata.Artist.IsNullOrEmpty() || !metadata.Album.IsNullOrEmpty()
            || metadata.Year > 0 || !metadata.Comment.IsNullOrEmpty() || metadata.TrackNumber > 0 || !metadata.Genre.IsNullOrEmpty();
        if (!hasValue) return [];

        var data = new Byte[V1Size];
        data[0] = (Byte)'T';
        data[1] = (Byte)'A';
        data[2] = (Byte)'G';

        WriteV1Text(data, 3, 30, metadata.Title);
        WriteV1Text(data, 33, 30, metadata.Artist);
        WriteV1Text(data, 63, 30, metadata.Album);
        if (metadata.Year > 0) WriteV1Text(data, 93, 4, metadata.Year.ToString());

        var track = metadata.TrackNumber is > 0 and < 256 ? metadata.TrackNumber : 0;
        WriteV1Text(data, 97, track > 0 ? 28 : 30, metadata.Comment);
        if (track > 0)
        {
            data[125] = 0;
            data[126] = (Byte)track;
        }

        var index = Array.IndexOf(GenreNames, metadata.Genre);
        data[127] = (Byte)(index >= 0 ? index : 255);

        return data;
    }

    private static String ReadV1Text(Byte[] data, Int32 offset, Int32 size)
    {
        var end = offset;
        while (end < offset + size && data[end] != 0) end++;

        var chars = new Char[end - offset];
        for (var i = 0; i < chars.Length; i++) chars[i] = (Char)data[offset + i];

        return new String(chars).Trim();
    }

    private static void WriteV1Text(Byte[] data, Int32 offset, Int32 size, String value)
    {
        if (value == null) return;

        // ID3v1 为 Latin-1 单字节，超出范围的字符降级为 '?'
        var length = Math.Min(size, value.Length);
        for (var i = 0; i < length; i++)
        {
            var ch = value[i];
            data[offset + i] = ch <= 0xFF ? (Byte)ch : (Byte)'?';
        }
    }
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
        if (version < 2 || version > 4) return false;
        var flags = data[5];

        var size = (data[6] & 0x7F) << 21 | (data[7] & 0x7F) << 14 | (data[8] & 0x7F) << 7 | (data[9] & 0x7F);
        length = HeaderSize + size;
        if (length > data.Length) length = data.Length;

        var pos = HeaderSize;
        // 扩展头（v2.3/v2.4 标志位 0x40）
        if ((flags & 0x40) != 0 && version >= 3 && pos + 4 <= length)
        {
            var ext = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            if (version == 4) ext = (data[pos] & 0x7F) << 21 | (data[pos + 1] & 0x7F) << 14 | (data[pos + 2] & 0x7F) << 7 | (data[pos + 3] & 0x7F);
            pos += ext + 4;
        }

        var frameHeaderSize = version == 2 ? 6 : 10;
        var tagUnsync = (flags & 0x80) != 0;
        while (pos + frameHeaderSize <= length)
        {
            if (data[pos] == 0) break;

            String id;
            Int32 frameSize;
            Int32 frameStart;
            if (version == 2)
            {
                id = System.Text.Encoding.ASCII.GetString(data, pos, 3);
                frameSize = (data[pos + 3] << 16) | (data[pos + 4] << 8) | data[pos + 5];
                frameStart = pos + 6;
            }
            else
            {
                id = System.Text.Encoding.ASCII.GetString(data, pos, 4);
                frameSize = version == 4
                    ? (data[pos + 4] & 0x7F) << 21 | (data[pos + 5] & 0x7F) << 14 | (data[pos + 6] & 0x7F) << 7 | (data[pos + 7] & 0x7F)
                    : (data[pos + 4] << 24) | (data[pos + 5] << 16) | (data[pos + 6] << 8) | data[pos + 7];
                frameStart = pos + 10;
            }

            if (frameSize <= 0 || frameSize > length - frameStart) break;
            var frameEnd = frameStart + frameSize;

            var payload = data;
            var payloadOffset = frameStart;
            var payloadSize = frameSize;

            if (version >= 3)
            {
                var format = data[pos + 9];
                if (version == 3)
                {
                    if ((format & 0x20) != 0) { payloadOffset++; payloadSize--; }
                    if ((format & 0x40) != 0) { pos = frameEnd; continue; }
                    if ((format & 0x80) != 0)
                    {
                        if (payloadSize < 4 || !TryDecompressZlib(data, payloadOffset + 4, payloadSize - 4, out var decompressed))
                        {
                            pos = frameEnd;
                            continue;
                        }
                        payload = decompressed;
                        payloadOffset = 0;
                        payloadSize = decompressed.Length;
                    }
                }
                else
                {
                    if ((format & 0x40) != 0) { payloadOffset++; payloadSize--; }
                    if ((format & 0x04) != 0) { pos = frameEnd; continue; }
                    if ((format & 0x01) != 0)
                    {
                        if (payloadSize < 4) { pos = frameEnd; continue; }
                        payloadOffset += 4;
                        payloadSize -= 4;
                    }
                    if ((format & 0x08) != 0)
                    {
                        if (!TryDecompressZlib(data, payloadOffset, payloadSize, out var decompressed))
                        {
                            pos = frameEnd;
                            continue;
                        }
                        payload = decompressed;
                        payloadOffset = 0;
                        payloadSize = decompressed.Length;
                    }
                    if ((format & 0x02) != 0)
                    {
                        payload = Deunsync(payload, payloadOffset, payloadSize);
                        payloadOffset = 0;
                        payloadSize = payload.Length;
                    }
                }
            }

            if (tagUnsync)
            {
                payload = Deunsync(payload, payloadOffset, payloadSize);
                payloadOffset = 0;
                payloadSize = payload.Length;
            }

            ReadFrame(NormalizeId(id), payload, payloadOffset, payloadSize, metadata);
            pos = frameEnd;
        }

        return true;
    }

    /// <summary>把 ID3v2.2 三字符帧标识映射到 v2.3/v2.4 标识</summary>
    /// <param name="id">原始帧标识</param>
    /// <returns>规范帧标识</returns>
    private static String NormalizeId(String id) => id switch
    {
        "TT2" => "TIT2",
        "TP1" => "TPE1",
        "TAL" => "TALB",
        "TYE" => "TYER",
        "TRK" => "TRCK",
        "TCO" => "TCON",
        "COM" => "COMM",
        "PIC" => "PIC",
        _ => id,
    };

    /// <summary>去除 ID3 反同步插入的 0x00</summary>
    /// <param name="data">源数据</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="size">长度</param>
    /// <returns>反同步后的数据</returns>
    private static Byte[] Deunsync(Byte[] data, Int32 offset, Int32 size)
    {
        if (size <= 0 || offset < 0 || offset + size > data.Length) return [];

        var result = new Byte[size];
        var pos = 0;
        var end = offset + size;
        for (var i = offset; i < end; i++)
        {
            result[pos++] = data[i];
            if (data[i] == 0xFF && i + 1 < end && data[i + 1] == 0) i++;
        }

        if (pos == size) return result;

        var trimmed = new Byte[pos];
        Array.Copy(result, trimmed, pos);

        return trimmed;
    }

    /// <summary>解压 zlib 压缩的帧数据，失败时返回 false</summary>
    /// <param name="data">源数据</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="size">长度</param>
    /// <param name="result">解压结果</param>
    /// <returns>是否成功解压</returns>
    private static Boolean TryDecompressZlib(Byte[] data, Int32 offset, Int32 size, out Byte[] result)
    {
        result = null;
        if (size <= 2 || offset < 0 || offset + size > data.Length) return false;

        try
        {
            // zlib 头 2 字节 + DEFLATE 数据 + Adler-32 尾 4 字节
            using var input = new MemoryStream(data, offset + 2, Math.Max(0, size - 6));
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            result = output.ToArray();

            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return false;
        }
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
        // 仅 v2.4 支持 10 字节 footer
        if (data[3] == 4 && (data[5] & 0x10) != 0) skip += 10;

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
            case "PIC":
                metadata.CoverImage = ReadPictureV22(data, offset, size);
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

    /// <summary>读取 ID3v2.2 PIC 帧（3 字节图片格式代替 MIME）</summary>
    /// <param name="data">源数据</param>
    /// <param name="offset">载荷起始偏移</param>
    /// <param name="size">载荷长度</param>
    /// <returns>图片数据；无效时返回 null</returns>
    private static Byte[] ReadPictureV22(Byte[] data, Int32 offset, Int32 size)
    {
        if (size <= 5) return null;

        var encoding = data[offset];
        var pos = offset + 4; // 编码 1 字节 + 图片格式 3 字节
        var end = offset + size;
        pos++; // 图片类型

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
    /// <summary>在原有 ID3v2 标签区内原地重写标签</summary>
    /// <param name="data">完整文件数据</param>
    /// <param name="metadata">新元数据</param>
    /// <param name="output">重写后的文件数据；无法原地重写时返回 null</param>
    /// <returns>是否成功原地重写</returns>
    /// <remarks>
    /// 仅当新标签不大于旧标签区且旧标签不带 footer 时可用。新标签用尾部填充补齐旧区域，
    /// 音频字节与起始偏移保持不变，避免整段音频搬移。
    /// </remarks>
    public static Boolean TryRewriteInPlace(Byte[] data, AudioMetadata metadata, out Byte[] output)
    {
        output = null;
        if (data == null || metadata == null || data.Length < HeaderSize) return false;
        if (data[0] != 'I' || data[1] != 'D' || data[2] != '3') return false;
        if (data[3] == 4 && (data[5] & 0x10) != 0) return false;

        var oldLength = Skip(data);
        if (oldLength <= HeaderSize || oldLength > data.Length) return false;

        var tag = Write(metadata, 0);
        if (tag.Length == 0 || tag.Length > oldLength) return false;

        var padded = Write(metadata, oldLength - tag.Length);
        if (padded.Length != oldLength) return false;

        // 原地更新仍丢弃尾部 ID3v1，避免旧标题继续被只读 v1 的播放器使用
        var end = data.Length;
        if (end - oldLength >= V1Size
            && data[end - V1Size] == 'T' && data[end - V1Size + 1] == 'A' && data[end - V1Size + 2] == 'G')
            end -= V1Size;

        output = new Byte[end];
        Array.Copy(data, output, end);
        Array.Copy(padded, 0, output, 0, padded.Length);

        return true;
    }

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
