using System;
using System.Collections.Generic;
using System.Text;
using NewLife;

namespace NewLife.Audio.Containers;
/// <summary>FLAC 元数据块构造与就地重写</summary>
/// <remarks>
/// 供写入器与"仅改标签不重编码"路径复用：构造标准 VORBIS_COMMENT 块体，
/// 以及在保留音频帧的前提下替换文件中的注释块。
/// </remarks>
internal static class FlacMetadataBlocks
{
    #region 常量
    private const Byte BlockTypeStreamInfo = 0;
    private const Byte BlockTypePadding = 1;
    private const Byte BlockTypeVorbisComment = 4;

    /// <summary>默认厂商字符串</summary>
    public const String VendorString = "NewLife.Audio";
    #endregion

    #region 构造
    /// <summary>构造 VORBIS_COMMENT 块体（不含 4 字节块头）</summary>
    /// <param name="metadata">元数据，可为 null</param>
    /// <param name="vendor">厂商字符串，空则使用默认值</param>
    /// <returns>块体字节</returns>
    public static Byte[] BuildVorbisComment(AudioMetadata metadata, String vendor = null)
    {
        var comments = new List<String>();
        if (metadata != null)
        {
            AddComment(comments, "TITLE", metadata.Title);
            AddComment(comments, "ARTIST", metadata.Artist);
            AddComment(comments, "ALBUM", metadata.Album);
            AddComment(comments, "GENRE", metadata.Genre);
            if (metadata.TrackNumber > 0) comments.Add("TRACKNUMBER=" + metadata.TrackNumber);
            if (metadata.Year > 0) comments.Add("DATE=" + metadata.Year);
            AddComment(comments, "COMMENT", metadata.Comment);
        }

        var vendorBytes = Encoding.UTF8.GetBytes(vendor.IsNullOrEmpty() ? VendorString : vendor);
        var body = new List<Byte>();
        WriteLE32(body, vendorBytes.Length);
        body.AddRange(vendorBytes);
        WriteLE32(body, comments.Count);
        foreach (var comment in comments)
        {
            var bytes = Encoding.UTF8.GetBytes(comment);
            WriteLE32(body, bytes.Length);
            body.AddRange(bytes);
        }

        return body.ToArray();
    }

    /// <summary>把元数据写为 FLAC 块头 + VORBIS_COMMENT 块体</summary>
    /// <param name="metadata">元数据</param>
    /// <param name="vendor">厂商字符串</param>
    /// <returns>完整块字节（含 4 字节块头）</returns>
    public static Byte[] BuildVorbisCommentBlock(AudioMetadata metadata, String vendor = null)
    {
        var body = BuildVorbisComment(metadata, vendor);
        var block = new Byte[4 + body.Length];
        block[0] = BlockTypeVorbisComment;
        block[1] = (Byte)(body.Length >> 16);
        block[2] = (Byte)(body.Length >> 8);
        block[3] = (Byte)body.Length;
        Array.Copy(body, 0, block, 4, body.Length);

        return block;
    }
    #endregion

    #region 重写
    /// <summary>替换 FLAC 文件中的 VORBIS_COMMENT 块，音频帧保持原样</summary>
    /// <param name="data">完整 FLAC 文件字节</param>
    /// <param name="metadata">新元数据</param>
    /// <returns>重写后的文件字节；格式不合法时返回 null</returns>
    /// <remarks>已有注释块被替换，缺失时插入到 STREAMINFO 之后；其余块（含 PADDING/SEEKTABLE）按原顺序保留。</remarks>
    public static Byte[] RewriteTag(Byte[] data, AudioMetadata metadata)
    {
        if (data == null || data.Length < 8) return null;
        if (data[0] != 'f' || data[1] != 'L' || data[2] != 'a' || data[3] != 'C') return null;

        var blocks = new List<(Byte Type, Byte[] Data)>();
        var offset = 4;
        var last = false;
        while (!last)
        {
            if (offset + 4 > data.Length) return null;

            var header = data[offset];
            last = (header & 0x80) != 0;
            var type = (Byte)(header & 0x7F);
            var length = data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3];
            offset += 4;
            if (offset + length > data.Length) return null;

            var body = new Byte[length];
            Array.Copy(data, offset, body, 0, length);
            offset += length;

            if (type == BlockTypeVorbisComment) continue;
            if (type == BlockTypePadding) continue;

            blocks.Add((type, body));
        }

        // STREAMINFO 必须存在且位于首位
        if (blocks.Count == 0 || blocks[0].Type != BlockTypeStreamInfo) return null;

        var comment = BuildVorbisComment(metadata);
        var output = new List<Byte>(data.Length + comment.Length + 32);
        output.AddRange([(Byte)'f', (Byte)'L', (Byte)'a', (Byte)'C']);

        void WriteBlock(Byte type, Byte[] body, Boolean isLast)
        {
            output.Add((Byte)((isLast ? 0x80 : 0) | type));
            output.Add((Byte)(body.Length >> 16));
            output.Add((Byte)(body.Length >> 8));
            output.Add((Byte)body.Length);
            output.AddRange(body);
        }

        WriteBlock(BlockTypeStreamInfo, blocks[0].Data, false);
        WriteBlock(BlockTypeVorbisComment, comment, blocks.Count == 1);

        for (var i = 1; i < blocks.Count; i++)
        {
            WriteBlock(blocks[i].Type, blocks[i].Data, i == blocks.Count - 1);
        }

        // 追加剩余音频帧
        for (var i = offset; i < data.Length; i++) output.Add(data[i]);

        return output.ToArray();
    }
    #endregion

    #region 辅助
    private static void AddComment(List<String> comments, String key, String value)
    {
        if (!value.IsNullOrEmpty()) comments.Add(key + "=" + value);
    }

    private static void WriteLE32(List<Byte> buffer, Int32 value)
    {
        buffer.Add((Byte)value);
        buffer.Add((Byte)(value >> 8));
        buffer.Add((Byte)(value >> 16));
        buffer.Add((Byte)(value >> 24));
    }
    #endregion
}

