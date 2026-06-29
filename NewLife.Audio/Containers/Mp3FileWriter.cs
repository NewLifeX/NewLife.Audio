using NewLife.Audio.DSP;
using NewLife.Audio.Writers;

namespace NewLife.Audio.Containers;

/// <summary>MP3 文件写入器适配器。包装 <see cref="Mp3StreamWriter"/> 实现 <see cref="IAudioContainerWriter"/> 接口</summary>
/// <remarks>MP3 帧天然可分片，无需容器封装，直接透传原始 MP3 帧数据</remarks>
public class Mp3FileWriter : IAudioContainerWriter
{
    private readonly Stream _stream;
    private readonly AudioFormat _format;
    private readonly Mp3StreamWriter _writer;
    private Boolean _completed;

    /// <summary>音频格式</summary>
    public AudioFormat Format => _format;

    /// <summary>初始化 MP3 文件写入器</summary>
    /// <param name="stream">输出流</param>
    /// <param name="format">音频格式</param>
    public Mp3FileWriter(Stream stream, AudioFormat format)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _format = format ?? throw new ArgumentNullException(nameof(format));
        _writer = new Mp3StreamWriter();

        _writer.WriteHeaderAsync(stream).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>写入元数据（MP3 容器无原生元数据块，空操作）</summary>
    /// <param name="metadata">音频元数据</param>
    public void WriteMetadata(AudioMetadata metadata)
    {
        // MP3 无容器级元数据，如需 ID3v2 标签可在此扩展
    }

    /// <summary>写入一帧 MP3 编码数据</summary>
    /// <param name="frame">MP3 编码帧</param>
    public void WriteFrame(ReadOnlySpan<Byte> frame)
    {
        if (_completed) return;

        _writer.WriteChunkAsync(_stream, frame.ToArray(), default).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>完成写入并刷新缓冲区</summary>
    public void Flush()
    {
        if (_completed) return;
        _completed = true;

        _writer.WriteTrailerAsync(_stream, default).AsTask().GetAwaiter().GetResult();
        _stream.Flush();
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        if (!_completed)
        {
            try { Flush(); } catch { /* 流可能已关闭 */ }
        }
        _stream?.Dispose();
    }
}
