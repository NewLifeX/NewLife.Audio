namespace NewLife.Audio.Writers;

/// <summary>MP3 音频流写入器。MP3 帧天然可分片，无需额外容器封装，直接透传原始字节</summary>
public sealed class Mp3StreamWriter : AudioStreamWriter
{
    /// <summary>audio/mpeg</summary>
    public override String ContentType => "audio/mpeg";

    /// <summary>MP3 无需头部</summary>
    /// <inheritdoc />
    public override ValueTask WriteHeaderAsync(Stream stream, CancellationToken cancellationToken = default) => default;

    /// <summary>直接写入 MP3 帧数据</summary>
    /// <inheritdoc />
    public override async ValueTask WriteChunkAsync(Stream stream, ReadOnlyMemory<Byte> chunk, CancellationToken cancellationToken = default)
    {
        await stream.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>MP3 无需尾部</summary>
    /// <inheritdoc />
    public override ValueTask WriteTrailerAsync(Stream stream, CancellationToken cancellationToken = default) => default;
}
