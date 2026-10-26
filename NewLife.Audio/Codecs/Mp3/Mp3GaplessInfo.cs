using System;

namespace NewLife.Audio.Codecs;

/// <summary>MP3 信息帧中的 gapless 裁剪信息</summary>
/// <remarks>
/// 编码器在 Xing/Info 信息帧里写入 LAME 兼容标签：开头 <see cref="EncoderDelay"/> 个样本与
/// 结尾 <see cref="EncoderPadding"/> 个样本是编解码器引入的填充，解码后应被丢弃，才能让多段音频无缝连播。
/// </remarks>
public sealed class Mp3GaplessInfo
{
    /// <summary>编码器延迟（样本数），解码结果开头需要丢弃的样本数</summary>
    public Int32 EncoderDelay { get; set; }

    /// <summary>尾部填充（样本数），解码结果结尾需要丢弃的样本数</summary>
    public Int32 EncoderPadding { get; set; }

    /// <summary>解析码流首个信息帧中的 gapless 标签</summary>
    /// <param name="data">MP3 码流</param>
    /// <param name="infoSamples">信息帧自身解码出的静音样本数（每声道），无信息帧时为 0</param>
    /// <returns>裁剪信息；无信息帧或缺少 LAME 标签时返回 null</returns>
    public static Mp3GaplessInfo TryParse(ReadOnlySpan<Byte> data, out Int32 infoSamples) => TryParse(data, 0, out infoSamples);

    /// <summary>解析码流首个信息帧中的 gapless 标签</summary>
    /// <param name="data">MP3 码流</param>
    /// <param name="offset">首个帧的起始偏移（需已跳过 ID3v2 标签）</param>
    /// <param name="infoSamples">信息帧自身解码出的静音样本数（每声道），无信息帧时为 0</param>
    /// <returns>裁剪信息；无信息帧或缺少 LAME 标签时返回 null</returns>
    public static Mp3GaplessInfo TryParse(ReadOnlySpan<Byte> data, Int32 offset, out Int32 infoSamples)
    {
        infoSamples = 0;
        if (offset < 0 || offset + 4 > data.Length) return null;
        if (data[offset] != 0xFF || (data[offset + 1] & 0xE0) != 0xE0) return null;

        var b1 = data[offset + 1];
        var b3 = data[offset + 3];
        var version = (b1 >> 3) & 0x03;
        if (version == 1) return null;
        if (((b1 >> 1) & 0x03) != 0x01) return null;   // 仅 Layer III 有本格式的信息帧
        var mpeg1 = version == 3;
        var mono = (b3 >> 6) == 3;
        var hasCrc = (b1 & 1) == 0;

        var sideInfo = mpeg1 ? (mono ? 17 : 32) : (mono ? 9 : 17);
        var mark = offset + 4 + (hasCrc ? 2 : 0) + sideInfo;
        if (mark + 8 > data.Length) return null;
        if (!IsTag(data, mark, "Xing") && !IsTag(data, mark, "Info")) return null;

        infoSamples = (mpeg1 ? 2 : 1) * 576;

        var flags = data[mark + 4] << 24 | data[mark + 5] << 16 | data[mark + 6] << 8 | data[mark + 7];
        var pos = mark + 8;
        if ((flags & 1) != 0) pos += 4;
        if ((flags & 2) != 0) pos += 4;
        if ((flags & 4) != 0) pos += 100;
        if ((flags & 8) != 0) pos += 4;

        // LAME 兼容标签：前 4 字节为编码器标识，延迟与填充在第 21~23 字节
        if (pos + 24 > data.Length) return null;
        if (!IsTag(data, pos, "LAME") && !IsTag(data, pos, "Lavf") && !IsTag(data, pos, "Lavc")) return null;

        var delay = data[pos + 21] << 4 | data[pos + 22] >> 4;
        var padding = (data[pos + 22] & 0x0F) << 8 | data[pos + 23];

        return new Mp3GaplessInfo { EncoderDelay = delay, EncoderPadding = padding };
    }

    private static Boolean IsTag(ReadOnlySpan<Byte> data, Int32 offset, String tag)
    {
        if (offset + tag.Length > data.Length) return false;
        for (var i = 0; i < tag.Length; i++)
        {
            if (data[offset + i] != tag[i]) return false;
        }

        return true;
    }
}
