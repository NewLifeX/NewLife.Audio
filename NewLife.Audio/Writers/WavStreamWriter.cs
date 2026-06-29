namespace NewLife.Audio.Writers;

/// <summary>WAV 音频流写入器。写标准 RIFF/WAV 头（data chunk size 设为最大值表示未知长度），后续透传 PCM int16 单声道数据</summary>
/// <remarks>
/// 输出格式: PCM int16 单声道。<br/>
/// RIFF data chunk size 设为 0x7FFFFFFF（约 2GB），允许流式写入直到结束，无需事后回填。
/// </remarks>
public sealed class WavStreamWriter : AudioStreamWriter
{
    private readonly Int32 _sampleRate;
    private Boolean _headerWritten;

    /// <summary>初始化 WAV 写入器</summary>
    /// <param name="sampleRate">采样率（Hz），默认 24000</param>
    public WavStreamWriter(Int32 sampleRate = 24000)
    {
        _sampleRate = sampleRate > 0 ? sampleRate : 24000;
    }

    /// <summary>audio/wav</summary>
    public override String ContentType => "audio/wav";

    /// <summary>写入 WAV 头（44 字节 RIFF + fmt + data chunk）</summary>
    /// <inheritdoc />
    public override async ValueTask WriteHeaderAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (_headerWritten) return;

        const Int32 channels = 1;
        const Int32 bitsPerSample = 16;
        var byteRate = _sampleRate * channels * bitsPerSample / 8;
        const Int32 blockAlign = channels * bitsPerSample / 8;

        // 使用最大 Int32 正值表示未知长度（流式场景下无法预知总长）
        const Int32 maxDataSize = 0x7FFFFFFF;
        var riffSize = maxDataSize - 8; // RIFF chunk size = fileSize - 8

        var header = new Byte[44];
        var offset = 0;

        // RIFF chunk
        BinaryWriterHelper.WriteFourCC(header, ref offset, "RIFF");
        BinaryWriterHelper.WriteInt32LE(header, ref offset, riffSize);
        BinaryWriterHelper.WriteFourCC(header, ref offset, "WAVE");

        // fmt sub-chunk
        BinaryWriterHelper.WriteFourCC(header, ref offset, "fmt ");
        BinaryWriterHelper.WriteInt32LE(header, ref offset, 16);          // sub-chunk size (PCM = 16)
        BinaryWriterHelper.WriteInt16LE(header, ref offset, 1);           // audio format (1 = PCM)
        BinaryWriterHelper.WriteInt16LE(header, ref offset, (Int16)channels);
        BinaryWriterHelper.WriteInt32LE(header, ref offset, _sampleRate);
        BinaryWriterHelper.WriteInt32LE(header, ref offset, byteRate);
        BinaryWriterHelper.WriteInt16LE(header, ref offset, (Int16)blockAlign);
        BinaryWriterHelper.WriteInt16LE(header, ref offset, (Int16)bitsPerSample);

        // data sub-chunk
        BinaryWriterHelper.WriteFourCC(header, ref offset, "data");
        BinaryWriterHelper.WriteInt32LE(header, ref offset, maxDataSize);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        _headerWritten = true;
    }

    /// <summary>直接写入 PCM 数据（16-bit 有符号整数，单声道）</summary>
    /// <inheritdoc />
    public override async ValueTask WriteChunkAsync(Stream stream, ReadOnlyMemory<Byte> chunk, CancellationToken cancellationToken = default)
    {
        await stream.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>WAV 无需尾部</summary>
    /// <inheritdoc />
    public override ValueTask WriteTrailerAsync(Stream stream, CancellationToken cancellationToken = default) => default;
}
