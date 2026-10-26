using NewLife.Audio.DSP;

namespace NewLife.Audio;

/// <summary>音频帧：格式、时间戳与缓冲所有权</summary>
/// <remarks>
/// 作为设备、DSP、VOP 与文件门面之间的公共数据载体，避免各层自定义“格式 + 数据 + 时间”三元组。<br/>
/// <see cref="OwnsBuffer"/> 只是所有权标记：为 true 时由帧创建者负责释放或归还 <see cref="Data"/>；
/// 为 false 时缓冲由调用方管理。帧本身不强制释放内存，池化策略仍由创建者决定。
/// </remarks>
/// <example>
/// <code>
/// var frame = AudioFrame.FromPcm(pcm, TimeSpan.FromSeconds(1.5));
/// Console.WriteLine($"{frame.Format.SampleRate}Hz {frame.Duration.TotalMilliseconds}ms");
/// </code>
/// </example>
public sealed class AudioFrame
{
    /// <summary>音频格式</summary>
    public AudioFormat Format { get; set; }

    /// <summary>帧时间戳（相对流起点）</summary>
    public TimeSpan Timestamp { get; set; }

    /// <summary>PCM 缓冲</summary>
    public Byte[] Data { get; set; } = [];

    /// <summary>是否拥有缓冲所有权。true 表示帧创建者负责释放或归还 <see cref="Data"/></summary>
    public Boolean OwnsBuffer { get; set; } = true;

    /// <summary>缓冲字节数</summary>
    public Int32 Length => Data?.Length ?? 0;

    /// <summary>帧时长（按采样率、声道数与位深计算）</summary>
    public TimeSpan Duration
    {
        get
        {
            var format = Format;
            if (format == null || format.SampleRate <= 0 || format.Channels <= 0 || format.BitsPerSample <= 0) return TimeSpan.Zero;

            var bytesPerFrame = format.Channels * format.BytesPerSample;
            if (bytesPerFrame <= 0) return TimeSpan.Zero;

            return TimeSpan.FromSeconds((Double)Length / bytesPerFrame / format.SampleRate);
        }
    }

    /// <summary>初始化空帧</summary>
    public AudioFrame() { }

    /// <summary>初始化音频帧</summary>
    /// <param name="format">音频格式</param>
    /// <param name="timestamp">帧时间戳</param>
    /// <param name="data">PCM 缓冲</param>
    /// <param name="ownsBuffer">是否拥有缓冲所有权</param>
    public AudioFrame(AudioFormat format, TimeSpan timestamp, Byte[] data, Boolean ownsBuffer = true)
    {
        Format = format;
        Timestamp = timestamp;
        Data = data ?? [];
        OwnsBuffer = ownsBuffer;
    }

    /// <summary>从 PCM 视图创建音频帧</summary>
    /// <param name="pcm">PCM 数据</param>
    /// <param name="timestamp">帧时间戳</param>
    /// <returns>音频帧，格式为副本，数据沿用原缓冲引用</returns>
    public static AudioFrame FromPcm(AudioPcm pcm, TimeSpan timestamp = default)
    {
        if (pcm == null) throw new ArgumentNullException(nameof(pcm));

        return new AudioFrame(pcm.Format?.Clone(), timestamp, pcm.Data, true);
    }

    /// <summary>转换为 PCM 视图</summary>
    /// <returns>PCM 数据，格式为副本</returns>
    public AudioPcm ToPcm() => new()
    {
        Format = Format?.Clone(),
        Codec = Format?.Encoding ?? AVTypes.LPCM,
        Data = Data ?? [],
    };
}
