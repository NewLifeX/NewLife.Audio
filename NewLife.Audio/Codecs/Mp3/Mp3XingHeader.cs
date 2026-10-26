using System;
using System.Collections.Generic;

namespace NewLife.Audio.Codecs;

/// <summary>MP3 信息帧（Xing/Info）构造器</summary>
/// <remarks>
/// 信息帧是一个本身合法的静音 MPEG 帧，紧跟在首个音频帧之前，用于告知播放器总帧数、总字节数与时长/seek 索引。
/// VBR 码流写 <c>Xing</c>，CBR 码流写 <c>Info</c>，两者结构一致（LAME 约定）。
/// <para>本实现写入 FRAMES / BYTES / TOC / QUALITY 四项，不写 LAME 扩展标签（编码延迟与 gapless 由上层另行处理）。</para>
/// </remarks>
internal static class Mp3XingHeader
{
    #region 常量
    private static readonly Int32[] BitratesMpeg1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    private static readonly Int32[] BitratesLsf = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];

    /// <summary>信息帧固定开销：帧头 4 + 标签 8 + 字段 12 + TOC 100 + 质量 4</summary>
    private const Int32 OverheadBytes = 4 + 8 + 12 + 100 + 4;
    #endregion

    #region 方法
    /// <summary>构造信息帧</summary>
    /// <param name="sampleRate">采样率</param>
    /// <param name="channels">声道数</param>
    /// <param name="frameCount">音频帧数（不含信息帧本身）</param>
    /// <param name="frameSizes">各音频帧字节数，用于生成 TOC 与总字节数</param>
    /// <param name="isVbr">是否为 VBR 码流（true 写 Xing，false 写 Info）</param>
    /// <param name="quality">VBR 质量标记 0~100，写入 QUALITY 字段</param>
    /// <returns>信息帧字节数组</returns>
    public static Byte[] Build(Int32 sampleRate, Int32 channels, Int32 frameCount, IReadOnlyList<Int32> frameSizes, Boolean isVbr, Int32 quality)
    {
        var (version, rateIndex) = sampleRate switch
        {
            44100 or 48000 or 32000 => (3, Array.IndexOf([44100, 48000, 32000], sampleRate)),
            22050 or 24000 or 16000 => (2, Array.IndexOf([22050, 24000, 16000], sampleRate)),
            11025 or 12000 or 8000 => (0, Array.IndexOf([11025, 12000, 8000], sampleRate)),
            _ => throw new ArgumentOutOfRangeException(nameof(sampleRate), "Unsupported MP3 sample rate: " + sampleRate),
        };

        var mpeg1 = version == 3;
        var bitrates = mpeg1 ? BitratesMpeg1 : BitratesLsf;
        var coef = mpeg1 ? 144 : 72;
        var sideInfoBytes = mpeg1
            ? (channels == 1 ? 17 : 32)
            : (channels == 1 ? 9 : 17);

        // 选一个能容纳信息帧的最小标准码率，避免播放器因帧太短而解析失败
        var bitrateIndex = 1;
        for (var i = 1; i < 15; i++)
        {
            if (coef * bitrates[i] * 1000 / sampleRate >= OverheadBytes + sideInfoBytes + 8)
            {
                bitrateIndex = i;
                break;
            }
        }
        var frameBytes = coef * bitrates[bitrateIndex] * 1000 / sampleRate;

        var frame = new Byte[frameBytes];
        var pos = 0;

        // 帧头：同步字 + MPEG 版本 + Layer III + 不保护 + 码率/采样率索引 + 无填充 + 非私有 + 立体声模式
        var padding = 0;
        frame[pos++] = 0xFF;
        frame[pos++] = (Byte)(0xE0 | (version << 3) | (1 << 1) | 0x01);
        frame[pos++] = (Byte)((bitrateIndex << 4) | (rateIndex << 2) | (padding << 1));
        frame[pos++] = channels == 1 ? (Byte)0xC0 : (Byte)0x00;

        // 边信息全零（静音帧）
        pos += sideInfoBytes;

        // 标签
        var tag = isVbr ? "Xing" : "Info";
        for (var i = 0; i < 4; i++) frame[pos++] = (Byte)tag[i];
        var flags = 0x0F;
        WriteInt32(frame, ref pos, flags);
        WriteInt32(frame, ref pos, frameCount);
        WriteInt32(frame, ref pos, (Int32)(frameCount == 0 ? frameBytes : frameBytes + Sum(frameSizes, frameCount)));

        // TOC：按时间进度记录字节进度，供播放器估算 seek 位置
        var totalAudio = Sum(frameSizes, frameCount);
        for (var i = 0; i < 100; i++)
        {
            if (frameCount == 0 || totalAudio <= 0)
            {
                frame[pos++] = 0;
                continue;
            }

            var target = (Int64)totalAudio * i / 100;
            var index = 0;
            var running = 0L;
            while (index < frameCount && running + frameSizes[index] <= target)
            {
                running += frameSizes[index];
                index++;
            }

            // 字节位置换算到包含信息帧之后的百分比
            var offsetBytes = frameBytes + running;
            var totalBytes = frameBytes + totalAudio;
            var value = totalBytes > 0 ? (Int32)(offsetBytes * 256L / totalBytes) : 0;
            frame[pos++] = (Byte)(value > 255 ? 255 : value);
        }

        WriteInt32(frame, ref pos, quality);

        return frame;
    }
    #endregion

    #region 辅助
    private static Int64 Sum(IReadOnlyList<Int32> values, Int32 count)
    {
        var total = 0L;
        for (var i = 0; i < count && i < values.Count; i++)
        {
            total += values[i];
        }

        return total;
    }

    private static void WriteInt32(Byte[] buffer, ref Int32 pos, Int32 value)
    {
        buffer[pos++] = (Byte)(value >> 24);
        buffer[pos++] = (Byte)(value >> 16);
        buffer[pos++] = (Byte)(value >> 8);
        buffer[pos++] = (Byte)value;
    }
    #endregion
}
