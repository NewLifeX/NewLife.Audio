using System;
using System.IO;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Containers;

/// <summary>AIFF 文件写入器。写出标准 FORM/AIFF（16 位大端 PCM）</summary>
/// <remarks>
/// 结构为 <c>FORM</c> + <c>COMM</c>（声道数、采样帧数、位深、80 位扩展浮点采样率）+ <c>SSND</c>（PCM 数据）。
/// 输入固定为 16 位小端交错 PCM，写出时转换为大端。可定位流在 <see cref="Flush"/> 时回填各处长度。
/// </remarks>
/// <example>
/// <code>
/// using var writer = new AiffFileWriter(stream, format);
/// writer.WriteFrame(pcm16LittleEndian);
/// writer.Flush();
/// </code>
/// </example>
public class AiffFileWriter : IAudioContainerWriter
{
    private readonly Stream _stream;
    private readonly AudioFormat _format;
    private Int64 _dataSize;
    private Int64 _frames;

    /// <summary>音频格式</summary>
    public AudioFormat Format => _format;

    /// <summary>写入的采样帧数</summary>
    public Int64 TotalFrames => _frames;

    /// <summary>从流创建 AIFF 写入器</summary>
    /// <param name="stream">输出流</param>
    /// <param name="format">音频格式，位深必须为 16</param>
    public AiffFileWriter(Stream stream, AudioFormat format)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _format = format ?? throw new ArgumentNullException(nameof(format));
        if (format.BitsPerSample != 16) throw new NotSupportedException("AIFF writer supports 16-bit PCM only.");

        WriteHeader();
    }

    /// <summary>写入元数据（AIFF 不承载标签，忽略）</summary>
    /// <param name="metadata">元数据</param>
    public void WriteMetadata(AudioMetadata metadata) { }

    /// <summary>写入 16 位小端交错 PCM</summary>
    /// <param name="frame">PCM 数据</param>
    public void WriteFrame(ReadOnlySpan<Byte> frame)
    {
        if (frame.Length < 2) return;

        var count = frame.Length / 2 * 2;
        var buffer = new Byte[count];
        for (var i = 0; i < count; i += 2)
        {
            buffer[i] = frame[i + 1];
            buffer[i + 1] = frame[i];
        }

        _stream.Write(buffer, 0, buffer.Length);
        _dataSize += buffer.Length;
        _frames += buffer.Length / 2 / Math.Max(1, _format.Channels);
    }

    /// <summary>完成写入并回填长度</summary>
    public void Flush()
    {
        if (!_stream.CanSeek)
        {
            _stream.Flush();
            return;
        }

        var end = _stream.Position;

        // FORM 长度 = 文件长度 - 8
        Patch(4, (Int32)Math.Min(end - 8, Int32.MaxValue));

        // COMM 中的采样帧数
        Patch(22, (Int32)Math.Min(_frames, Int32.MaxValue), 4);

        // SSND 长度 = 8 + 数据长度
        Patch(42, (Int32)Math.Min(_dataSize + 8, Int32.MaxValue));

        _stream.Seek(end, SeekOrigin.Begin);
        _stream.Flush();
    }

    /// <summary>释放</summary>
    public void Dispose() => _stream?.Dispose();

    #region 辅助
    private void WriteHeader()
    {
        var channels = _format.Channels > 0 ? _format.Channels : 1;
        var header = new Byte[54];
        var pos = 0;

        WriteTag(header, ref pos, "FORM");
        WriteInt32(header, ref pos, 0);            // 长度占位
        WriteTag(header, ref pos, "AIFF");

        WriteTag(header, ref pos, "COMM");
        WriteInt32(header, ref pos, 18);
        header[pos++] = (Byte)(channels >> 8);
        header[pos++] = (Byte)channels;
        WriteInt32(header, ref pos, 0);            // 采样帧数占位
        header[pos++] = 0;                          // 位深 16
        header[pos++] = 16;
        WriteExtended(header, ref pos, _format.SampleRate > 0 ? _format.SampleRate : 44100);

        WriteTag(header, ref pos, "SSND");
        WriteInt32(header, ref pos, 0);            // 长度占位
        WriteInt32(header, ref pos, 0);            // offset
        WriteInt32(header, ref pos, 0);            // blockSize

        _stream.Write(header, 0, pos);
    }

    private void Patch(Int32 offset, Int32 value, Int32 bytes = 4)
    {
        var buffer = new Byte[bytes];
        for (var i = 0; i < bytes; i++)
        {
            buffer[i] = (Byte)(value >> (8 * (bytes - 1 - i)));
        }

        _stream.Seek(offset, SeekOrigin.Begin);
        _stream.Write(buffer, 0, buffer.Length);
    }

    private static void WriteTag(Byte[] buffer, ref Int32 pos, String tag)
    {
        for (var i = 0; i < tag.Length; i++) buffer[pos++] = (Byte)tag[i];
    }

    private static void WriteInt32(Byte[] buffer, ref Int32 pos, Int32 value)
    {
        buffer[pos++] = (Byte)(value >> 24);
        buffer[pos++] = (Byte)(value >> 16);
        buffer[pos++] = (Byte)(value >> 8);
        buffer[pos++] = (Byte)value;
    }

    /// <summary>写 80 位扩展浮点（IEEE 754 extended，大端）</summary>
    private static void WriteExtended(Byte[] buffer, ref Int32 pos, Int32 value)
    {
        if (value <= 0)
        {
            pos += 10;
            return;
        }

        // 归一化尾数到 [1, 2)，指数按 16383 偏移
        var mantissa = (Double)value;
        var exponent = 16383;
        while (mantissa >= 2)
        {
            mantissa /= 2;
            exponent++;
        }
        while (mantissa < 1)
        {
            mantissa *= 2;
            exponent--;
        }

        buffer[pos++] = (Byte)(exponent >> 8);
        buffer[pos++] = (Byte)exponent;

        // 显式整数位包含在 64 位尾数中
        var bits = (UInt64)(mantissa * 9223372036854775808.0);
        for (var i = 0; i < 8; i++) buffer[pos++] = (Byte)(bits >> (8 * (7 - i)));
    }
    #endregion
}
