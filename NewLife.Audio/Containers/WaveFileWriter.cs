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

    /// <summary>音频格式</summary>
    public AudioFormat Format => _format;

    /// <summary>从流创建 WAV 写入器</summary>
    /// <param name="stream">输出流</param>
    /// <param name="format">音频格式</param>
    public WaveFileWriter(Stream stream, AudioFormat format)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _format = format ?? throw new ArgumentNullException(nameof(format));
        _writer = new WavStreamWriter(format.SampleRate);

        _writer.WriteHeaderAsync(stream).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>写入元数据</summary>
    public void WriteMetadata(AudioMetadata metadata) { }

    /// <summary>写入 PCM 数据帧</summary>
    public void WriteFrame(ReadOnlySpan<Byte> frame)
    {
        _writer.WriteChunkAsync(_stream, frame.ToArray(), default).AsTask().GetAwaiter().GetResult();
        _dataSize += frame.Length;
    }

    /// <summary>完成写入</summary>
    public void Flush() => _stream.Flush();

    /// <summary>释放</summary>
    public void Dispose() => _stream?.Dispose();
}
