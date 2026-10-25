using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts;

/// <summary>合成音频的分块输出目标</summary>
/// <remarks>
/// 引擎每产出一块 PCM 就调用一次 <see cref="Write"/>，播放端可以立即送声卡，
/// 从而实现“边合成边播放”。引擎保证：<see cref="Open"/> 一次 → <see cref="Write"/> 若干次 → <see cref="Close"/> 一次。
/// </remarks>
public interface ITtsSink
{
    /// <summary>开始接收音频。引擎在写出第一块数据前调用</summary>
    /// <param name="format">音频格式</param>
    void Open(AudioFormat format);

    /// <summary>写入一块 PCM 数据</summary>
    /// <param name="buffer">数据缓冲区</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="count">字节数</param>
    void Write(Byte[] buffer, Int32 offset, Int32 count);

    /// <summary>合成结束</summary>
    void Close();
}

/// <summary>内存缓冲输出目标。把分块数据攒成整段 PCM，便于缓存与测试</summary>
public class TtsBufferSink : ITtsSink
{
    private readonly MemoryStream _stream = new();

    /// <summary>音频格式</summary>
    public AudioFormat Format { get; private set; }

    /// <summary>已写入字节数</summary>
    public Int64 Length => _stream.Length;

    /// <summary>开始接收音频</summary>
    /// <param name="format">音频格式</param>
    public void Open(AudioFormat format) => Format = format;

    /// <summary>写入一块 PCM 数据</summary>
    /// <param name="buffer">数据缓冲区</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="count">字节数</param>
    public void Write(Byte[] buffer, Int32 offset, Int32 count)
    {
        if (count > 0) _stream.Write(buffer, offset, count);
    }

    /// <summary>合成结束</summary>
    public void Close() { }

    /// <summary>取出整段 PCM 数据</summary>
    /// <returns>PCM 字节数组</returns>
    public Byte[] ToArray() => _stream.ToArray();

    /// <summary>清空缓冲，便于复用</summary>
    public void Reset()
    {
        _stream.SetLength(0);
        Format = null;
    }
}

/// <summary>回调输出目标。把分块数据直接交给播放器或网络发送</summary>
/// <remarks>
/// 用于流式场景：<c>OnWrite</c> 里把 PCM 交给声卡，即可实现“首包到达即出声”。
/// </remarks>
/// <example>
/// <code>
/// var sink = new TtsDelegateSink(
///     format =&gt; player.Open(format),
///     (buf, off, len) =&gt; player.Write(buf, off, len),
///     () =&gt; player.Drain());
/// </code>
/// </example>
public class TtsDelegateSink : ITtsSink
{
    private readonly Action<AudioFormat> _onOpen;
    private readonly Action<Byte[], Int32, Int32> _onWrite;
    private readonly Action _onClose;

    /// <summary>音频格式</summary>
    public AudioFormat Format { get; private set; }

    /// <summary>初始化回调输出目标</summary>
    /// <param name="onOpen">开始回调，可为 null</param>
    /// <param name="onWrite">数据回调，不可为 null</param>
    /// <param name="onClose">结束回调，可为 null</param>
    public TtsDelegateSink(Action<AudioFormat> onOpen, Action<Byte[], Int32, Int32> onWrite, Action onClose = null)
    {
        _onOpen = onOpen;
        _onWrite = onWrite ?? throw new ArgumentNullException(nameof(onWrite));
        _onClose = onClose;
    }

    /// <summary>开始接收音频</summary>
    /// <param name="format">音频格式</param>
    public void Open(AudioFormat format)
    {
        Format = format;
        _onOpen?.Invoke(format);
    }

    /// <summary>写入一块 PCM 数据</summary>
    /// <param name="buffer">数据缓冲区</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="count">字节数</param>
    public void Write(Byte[] buffer, Int32 offset, Int32 count) => _onWrite(buffer, offset, count);

    /// <summary>合成结束</summary>
    public void Close() => _onClose?.Invoke();
}
