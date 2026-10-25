using NewLife.Buffers;

namespace NewLife.Audio.Writers;

/// <summary>WAV 音频流写入器。写标准 RIFF/WAVE 头，后续透传 PCM 数据</summary>
/// <remarks>
/// 默认按流式场景写入未知长度（data chunk size = 0x7FFFFFFF），允许边合成边发送、无需事后回填。<br/>
/// 当输出流可 seek 且 <see cref="PatchSize"/> 为 true 时，在 <see cref="WriteTrailerAsync"/> 中回填真实长度，
/// 适合保存为本地文件。
/// </remarks>
public sealed class WavStreamWriter : AudioStreamWriter
{
    private readonly Int32 _sampleRate;
    private Int64 _dataSize;
    private Boolean _headerWritten;

    /// <summary>初始化 WAV 写入器</summary>
    /// <param name="sampleRate">采样率（Hz），默认 24000</param>
    public WavStreamWriter(Int32 sampleRate = 24000) => _sampleRate = sampleRate > 0 ? sampleRate : 24000;

    /// <summary>声道数，默认 1</summary>
    public Int32 Channels { get; set; } = 1;

    /// <summary>每样本位数，默认 16</summary>
    public Int32 BitsPerSample { get; set; } = 16;

    /// <summary>是否在结束时回填长度。仅当输出流可 seek 时生效</summary>
    public Boolean PatchSize { get; set; }

    /// <summary>audio/wav</summary>
    public override String ContentType => "audio/wav";

    /// <summary>写入 WAV 头（44 字节 RIFF + fmt + data chunk）</summary>
    /// <param name="stream">输出流</param>
    /// <param name="cancellationToken">取消令牌</param>
    public override async ValueTask WriteHeaderAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (_headerWritten) return;

        var channels = Channels > 0 ? Channels : 1;
        var bitsPerSample = BitsPerSample > 0 ? BitsPerSample : 16;
        var byteRate = _sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;

        // 流式场景无法预知总长，先用最大 Int32 正值占位
        var maxDataSize = 0x7FFFFFFF;
        var riffSize = maxDataSize - 8;

        var header = new Byte[44];
        var writer = new SpanWriter(header.AsSpan());

        // RIFF chunk
        writer.Write(0x46464952u);       // "RIFF"
        writer.Write(riffSize);
        writer.Write(0x45564157u);       // "WAVE"

        // fmt sub-chunk
        writer.Write(0x20746D66u);       // "fmt "
        writer.Write(16);                // sub-chunk size (PCM = 16)
        writer.Write((Int16)1);          // audio format (1 = PCM)
        writer.Write((Int16)channels);
        writer.Write(_sampleRate);
        writer.Write(byteRate);
        writer.Write((Int16)blockAlign);
        writer.Write((Int16)bitsPerSample);

        // data sub-chunk
        writer.Write(0x61746164u);       // "data"
        writer.Write(maxDataSize);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        _headerWritten = true;
    }

    /// <summary>直接写入 PCM 数据（16-bit 有符号整数小端）</summary>
    /// <param name="stream">输出流</param>
    /// <param name="chunk">PCM 数据块</param>
    /// <param name="cancellationToken">取消令牌</param>
    public override async ValueTask WriteChunkAsync(Stream stream, ReadOnlyMemory<Byte> chunk, CancellationToken cancellationToken = default)
    {
        await stream.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        _dataSize += chunk.Length;
    }

    /// <summary>写入尾部。可 seek 且启用 <see cref="PatchSize"/> 时回填 RIFF/data 长度</summary>
    /// <param name="stream">输出流</param>
    /// <param name="cancellationToken">取消令牌</param>
    public override async ValueTask WriteTrailerAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (!PatchSize || stream == null || !stream.CanSeek) return;

        var position = stream.Position;
        try
        {
            var dataSize = (Int32)Math.Min(_dataSize, 0x7FFFFFFF - 36);
            var riffSize = dataSize + 36;

            var buffer = new Byte[4];
            var writer = new SpanWriter(buffer.AsSpan());

            stream.Position = 4;
            writer.Write(riffSize);
            await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);

            writer = new SpanWriter(buffer.AsSpan());
            stream.Position = 40;
            writer.Write(dataSize);
            await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);

            stream.Position = position;
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 回填失败不影响已经写出的音频
            stream.Position = position;
        }
    }
}
