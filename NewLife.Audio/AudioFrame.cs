using System.Buffers;
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
public sealed class AudioFrame : IDisposable
{
    private Byte[] _rented;

    /// <summary>音频格式</summary>
    public AudioFormat Format { get; set; }

    /// <summary>帧时间戳（相对流起点）</summary>
    public TimeSpan Timestamp { get; set; }

    /// <summary>PCM 缓冲</summary>
    public Byte[] Data { get; set; } = [];

    /// <summary>有效数据在 <see cref="Data"/> 中的起始偏移</summary>
    public Int32 Offset { get; set; }

    /// <summary>有效字节数。0 表示 <see cref="Data"/> 全长</summary>
    public Int32 Count { get; set; }

    /// <summary>是否拥有缓冲所有权。true 表示帧创建者负责释放或归还 <see cref="Data"/></summary>
    public Boolean OwnsBuffer { get; set; } = true;

    /// <summary>缓冲字节数（已计入 <see cref="Offset"/> 与 <see cref="Count"/>）</summary>
    public Int32 Length
    {
        get
        {
            var data = Data;
            if (data == null || data.Length == 0) return 0;

            var offset = Offset > 0 ? Offset : 0;
            if (offset >= data.Length) return 0;

            var count = Count > 0 ? Count : data.Length - offset;
            return count > data.Length - offset ? data.Length - offset : count;
        }
    }

    /// <summary>取帧数据视图</summary>
    /// <returns>当前帧的只读字节视图</returns>
    public ReadOnlySpan<Byte> GetSpan() => Data == null ? default : new ReadOnlySpan<Byte>(Data, Offset > 0 ? Offset : 0, Length);

    /// <summary>取帧数据视图（可写）</summary>
    /// <returns>当前帧的可写内存视图，供采集器直接填充</returns>
    public Memory<Byte> GetMemory() => Data == null ? default : new Memory<Byte>(Data, Offset > 0 ? Offset : 0, Length);

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

    /// <summary>初始化音频帧（借用缓冲中的一段）</summary>
    /// <param name="format">音频格式</param>
    /// <param name="timestamp">帧时间戳</param>
    /// <param name="data">PCM 缓冲</param>
    /// <param name="offset">有效数据起始偏移</param>
    /// <param name="count">有效字节数</param>
    /// <param name="ownsBuffer">是否拥有缓冲所有权，借用时为 false</param>
    public AudioFrame(AudioFormat format, TimeSpan timestamp, Byte[] data, Int32 offset, Int32 count, Boolean ownsBuffer = false)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (offset < 0 || offset > data.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 0 || offset + count > data.Length) throw new ArgumentOutOfRangeException(nameof(count));

        Format = format;
        Timestamp = timestamp;
        Data = data;
        Offset = offset;
        Count = count;
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

    /// <summary>是否借用自共享数组池。为 true 时必须调用 <see cref="Dispose"/> 归还</summary>
    public Boolean IsPooled => _rented != null;

    /// <summary>从共享数组池租用缓冲创建帧</summary>
    /// <param name="format">音频格式</param>
    /// <param name="count">需要的字节数</param>
    /// <param name="timestamp">帧时间戳</param>
    /// <returns>租用帧，用完必须 <see cref="Dispose"/> 归还；<see cref="Data"/> 实际容量可能大于 <paramref name="count"/></returns>
    public static AudioFrame Rent(AudioFormat format, Int32 count, TimeSpan timestamp = default)
    {
        if (format == null) throw new ArgumentNullException(nameof(format));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

        var buffer = count > 0 ? ArrayPool<Byte>.Shared.Rent(count) : [];
        var frame = new AudioFrame(format, timestamp, buffer, 0, count, true) { _rented = count > 0 ? buffer : null };

        return frame;
    }

    /// <summary>归还租用的缓冲。借用帧与整段帧不持有池内缓冲，调用无副作用且可重复调用</summary>
    public void Dispose()
    {
        var rented = _rented;
        _rented = null;
        if (rented == null) return;

        Data = [];
        Offset = 0;
        Count = 0;

        ArrayPool<Byte>.Shared.Return(rented);
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
