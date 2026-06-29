namespace NewLife.Audio.Writers;

/// <summary>音频流写入器抽象基类。定义流式音频输出的生命周期：写头 → 逐块写数据 → 写尾</summary>
/// <remarks>
/// 典型用法：
/// <code>
/// await using var writer = AudioStreamWriter.Create("opus");
/// await writer.WriteHeaderAsync(stream, ct);
/// await foreach (var chunk in audioChunks)
///     await writer.WriteChunkAsync(stream, chunk, ct);
/// await writer.WriteTrailerAsync(stream, ct);
/// </code>
/// </remarks>
public abstract class AudioStreamWriter : IAsyncDisposable
{
    /// <summary>HTTP Content-Type，如 audio/mpeg、audio/ogg、audio/wav</summary>
    public abstract String ContentType { get; }

    /// <summary>写入流头部（OGG 标识头+注释头 / WAV RIFF 头 / MP3 无操作）</summary>
    /// <param name="stream">输出流</param>
    /// <param name="cancellationToken">取消令牌</param>
    public abstract ValueTask WriteHeaderAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>写入一个音频数据块</summary>
    /// <param name="stream">输出流</param>
    /// <param name="chunk">音频字节数据</param>
    /// <param name="cancellationToken">取消令牌</param>
    public abstract ValueTask WriteChunkAsync(Stream stream, ReadOnlyMemory<Byte> chunk, CancellationToken cancellationToken = default);

    /// <summary>写入流尾部（OGG EOS 页 / WAV 无操作 / MP3 无操作）</summary>
    /// <param name="stream">输出流</param>
    /// <param name="cancellationToken">取消令牌</param>
    public abstract ValueTask WriteTrailerAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>释放资源</summary>
    public virtual ValueTask DisposeAsync() => default;

    /// <summary>根据格式和采样率创建对应的音频流写入器</summary>
    /// <param name="format">音频格式: mp3 / opus / wav / pcm。不区分大小写，默认 mp3</param>
    /// <param name="sampleRate">采样率（Hz），仅 WAV/PCM 使用，默认 24000</param>
    /// <returns>对应的音频流写入器实例</returns>
    public static AudioStreamWriter Create(String format, Int32 sampleRate = 24000)
    {
        return (format?.ToLower() ?? "mp3") switch
        {
            "opus" => new OggOpusStreamWriter(),
            "wav" or "pcm" => new WavStreamWriter(sampleRate),
            _ => new Mp3StreamWriter(),
        };
    }
}
