using NewLife.Audio.Writers;
using NewLife.Data;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Containers;

/// <summary>WAV 文件写入器。写入标准 RIFF/WAV 格式</summary>
public class WaveFileWriter : IAudioContainerWriter
{
    private readonly Stream _stream;
    private readonly AudioFormat _format;
    private readonly WavStreamWriter _writer;
    private Int32 _dataSize;
    private Boolean _headerWritten;

    /// <summary>音频格式</summary>
    public AudioFormat Format => _format;

    /// <summary>从流创建 WAV 写入器</summary>
    /// <param name="stream">输出流</param>
    /// <param name="format">音频格式</param>
    /// <remarks>头部延迟到首次写入数据或 <see cref="Flush"/> 时写出，以便把 <see cref="WriteMetadata"/> 的标签块放进 fmt 与 data 之间。</remarks>
    public WaveFileWriter(Stream stream, AudioFormat format)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _format = format ?? throw new ArgumentNullException(nameof(format));
        _writer = new WavStreamWriter(format.SampleRate)
        {
            Channels = format.Channels,
            BitsPerSample = format.BitsPerSample,
            PatchSize = stream.CanSeek,
        };

        _writer.WriteHeaderAsync(stream).AsTask().GetAwaiter().GetResult();
        _headerWritten = true;
    }

    /// <summary>写入元数据（RIFF LIST/INFO 块）</summary>
    /// <param name="metadata">元数据；为 null 或全空时不写标签块</param>
    /// <remarks>需在写入音频数据之前调用，且要求流可定位；否则标签被忽略。</remarks>
    public void WriteMetadata(AudioMetadata metadata)
    {
        var chunk = WaveInfoChunk.Build(metadata);
        if (chunk == null) return;

        // 头部已写出：把标签块插到 data 之前；插入失败时留给下一次写头（流式不可定位则忽略）
        if (!_writer.InsertListInfo(_stream, chunk)) _writer.ListInfo = chunk;
    }

    /// <summary>写入 PCM 数据帧</summary>
    public void WriteFrame(ReadOnlySpan<Byte> frame)
    {
        EnsureHeader();
        _writer.WriteChunkAsync(_stream, frame.ToArray(), default).AsTask().GetAwaiter().GetResult();
        _dataSize += frame.Length;
    }

    /// <summary>完成写入</summary>
    public void Flush()
    {
        EnsureHeader();
        _writer.WriteTrailerAsync(_stream).AsTask().GetAwaiter().GetResult();
        _stream.Flush();
    }

    /// <summary>释放</summary>
    public void Dispose() => _stream?.Dispose();

    private void EnsureHeader()
    {
        if (_headerWritten) return;

        _writer.WriteHeaderAsync(_stream).AsTask().GetAwaiter().GetResult();
        _headerWritten = true;
    }
}
