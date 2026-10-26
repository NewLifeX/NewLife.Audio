using NewLife;
using NewLife.Data;

namespace NewLife.Audio.Codecs;

/// <summary>Opus 编解码器</summary>
/// <remarks>
/// 纯托管 Opus 编解码器（自研路线）。包结构（TOC、帧数、帧长、VBR/CBR、padding）由 <see cref="OpusPacket"/> 解析；<br/>
/// 在 M2~M5 的 CELT/SILK 进程内实现完成之前，编码与解码通过 <see cref="OpusProcessCodec"/> 调用系统 ffmpeg（libopus）作为降级方案；<br/>
/// <see cref="OpusManagedCodec"/> 是 M2 起的进程内切换点。<br/>
/// Opus 内部固定使用 48 kHz 时间基；<see cref="FromPcm"/> 每次编码一帧，连续 PCM 请使用 <see cref="Encode(ReadOnlySpan{Byte}, Object, Boolean)"/> 获取多包输出。<br/>
/// 有状态编解码器，每个音频流应使用独立实例。
/// </remarks>
public class OpusCodec : IAudioCodec, ICodecInfo, IDisposable
{
    #region 常量
    /// <summary>Opus 基础采样率（固定 48000Hz）</summary>
    public const Int32 BaseSampleRate = 48000;

    /// <summary>编码器算法延迟对应的时长（微秒）</summary>
    private const Int32 LookaheadMicroseconds = 6500;
    #endregion

    #region 属性
    /// <summary>编解码器名称</summary>
    public String Name => "Opus";

    /// <summary>版本号</summary>
    public String Version => "3.0";

    /// <summary>当前是否有可用的 Opus 编解码后端</summary>
    /// <remarks>M1 阶段为系统 ffmpeg + libopus 进程回退；托管 CELT/SILK 完成后始终为 true。</remarks>
    public static Boolean IsAvailable => OpusManagedCodec.IsAvailable || OpusProcessCodec.IsAvailable;

    /// <summary>支持的编码类型</summary>
    public IReadOnlyCollection<AVTypes> SupportedTypes { get; } = [AVTypes.Opus, AVTypes.Transparent];

    /// <summary>有状态编解码器</summary>
    public Boolean IsStateful => true;

    /// <summary>当前编码选项。尚未编码时返回 null</summary>
    public OpusOptions EncoderOptions
    {
        get { lock (_sync) return _encoderOptions?.Clone(); }
    }

    /// <summary>当前解码选项。尚未解码时返回 null</summary>
    public OpusOptions DecoderOptions
    {
        get { lock (_sync) return _decoderOptions?.Clone(); }
    }

    /// <summary>编码采样率（Hz）</summary>
    public Int32 SampleRate
    {
        get { lock (_sync) return _encoderOptions?.SampleRate ?? BaseSampleRate; }
    }

    /// <summary>编码声道数</summary>
    public Int32 Channels
    {
        get { lock (_sync) return _encoderOptions?.Channels ?? 1; }
    }

    /// <summary>每帧每声道样本数</summary>
    public Int32 FrameSize
    {
        get { lock (_sync) return _encoderOptions?.SamplesPerFrame ?? 960; }
    }

    /// <summary>每帧字节数（16 位交错 PCM）</summary>
    public Int32 FrameBytes
    {
        get { lock (_sync) return _encoderOptions?.FrameBytes ?? 1920; }
    }

    /// <summary>编码器算法延迟（每声道样本数）</summary>
    public Int32 Lookahead
    {
        get { lock (_sync) return GetLookahead(_encoderOptions ?? new OpusOptions()); }
    }

    private readonly Object _sync = new();
    private OpusOptions _encoderOptions;
    private OpusOptions _decoderOptions;
    private Byte[] _pendingPcm = new Byte[4096];
    private Int32 _pendingCount;
    private Int32 _pendingFrameBytes;
    private Boolean _disposed;
    #endregion

    #region 解码
    /// <summary>Opus 数据转 PCM</summary>
    /// <param name="audio">单个 Opus 包（TOC + payload）</param>
    /// <param name="option"><see cref="OpusOptions"/> 或 null，用于配置输出采样率/声道</param>
    /// <returns>16-bit PCM interleaved，采样率与声道数由选项决定；非法包返回空包</returns>
    /// <exception cref="NotSupportedException">托管 CELT/SILK 未完成且未找到可用 ffmpeg 时抛出</exception>
    public IPacket ToPcm(ReadOnlySpan<Byte> audio, Object option)
    {
        ThrowIfDisposed();
        if (audio.Length == 0) return ArrayPacket.Empty;
        if (!OpusPacket.TryParse(audio, out _)) return ArrayPacket.Empty;

        var options = ResolveOptions(option, _decoderOptions);
        options.Validate();

        Byte[] pcm;
        try
        {
            pcm = OpusManagedCodec.IsAvailable
                ? OpusManagedCodec.DecodePacket(audio, options)
                : OpusProcessCodec.DecodePacket(audio, options);
        }
        catch (ArgumentException)
        {
            return ArrayPacket.Empty;
        }

        if (pcm.Length == 0) return ArrayPacket.Empty;

        lock (_sync) _decoderOptions = options.Clone();

        return new ArrayPacket(pcm);
    }

    /// <summary>丢包补偿（PLC），按上一帧状态合成缺失音频</summary>
    /// <param name="frameSize">缺失帧的每声道样本数；0 表示使用当前解码选项的帧长</param>
    /// <returns>16-bit PCM；M1 进程回退不支持 PLC，返回空包</returns>
    /// <remarks>进程内 SILK/CELT 解码器（M4）完成后，此方法将使用解码器历史状态生成舒适噪声。</remarks>
    public IPacket DecodePacketLoss(Int32 frameSize = 0)
    {
        ThrowIfDisposed();

        return ArrayPacket.Empty;
    }
    #endregion

    #region 编码
    /// <summary>PCM 转 Opus（编码一帧）</summary>
    /// <param name="pcm">16-bit 小端交错 PCM</param>
    /// <param name="option"><see cref="OpusOptions"/>、比特率（Int32）或 null</param>
    /// <returns>单个 Opus 包（TOC + payload）</returns>
    /// <remarks>输入不足一帧时补静音，超过一帧时只编码第一帧；连续 PCM 请使用 <see cref="Encode(ReadOnlySpan{Byte}, Object, Boolean)"/>。</remarks>
    /// <exception cref="NotSupportedException">托管 CELT/SILK 未完成且未找到可用 ffmpeg 时抛出</exception>
    public IPacket FromPcm(ReadOnlySpan<Byte> pcm, Object option)
    {
        ThrowIfDisposed();
        if (pcm.Length == 0) return ArrayPacket.Empty;

        var options = ResolveOptions(option, _encoderOptions);
        options.Validate();

        lock (_sync)
        {
            var frameBytes = options.FrameBytes;
            if (frameBytes <= 0) return ArrayPacket.Empty;

            ReadOnlySpan<Byte> frame;
            if (pcm.Length < frameBytes)
            {
                var padded = new Byte[frameBytes];
                pcm.CopyTo(padded);
                frame = padded;
            }
            else
                frame = pcm[..frameBytes];

            return EncodeFrame(frame, options);
        }
    }

    /// <summary>编码连续 PCM，返回全部完整帧对应的 Opus 包</summary>
    /// <param name="pcm">16-bit 小端交错 PCM</param>
    /// <param name="flush">是否在末尾把不足一帧的数据补静音后编码</param>
    /// <returns>Opus 包列表，每个包一帧</returns>
    public IReadOnlyList<IPacket> Encode(ReadOnlySpan<Byte> pcm, Boolean flush = false) => Encode(pcm, null, flush);

    /// <summary>编码连续 PCM，返回全部完整帧对应的 Opus 包</summary>
    /// <param name="pcm">16-bit 小端交错 PCM</param>
    /// <param name="option"><see cref="OpusOptions"/>、比特率（Int32）或 null</param>
    /// <param name="flush">是否在末尾把不足一帧的数据补静音后编码</param>
    /// <returns>Opus 包列表，每个包一帧</returns>
    /// <exception cref="NotSupportedException">托管 CELT/SILK 未完成且未找到可用 ffmpeg 时抛出</exception>
    public IReadOnlyList<IPacket> Encode(ReadOnlySpan<Byte> pcm, Object option, Boolean flush = false)
    {
        ThrowIfDisposed();
        var options = ResolveOptions(option, _encoderOptions);
        options.Validate();

        lock (_sync)
        {
            var frameBytes = options.FrameBytes;
            if (frameBytes <= 0) return [];

            if (_pendingFrameBytes != frameBytes)
            {
                _pendingFrameBytes = frameBytes;
                _pendingCount = 0;
            }

            var list = new List<IPacket>();
            AppendPending(pcm);

            while (_pendingCount >= frameBytes)
            {
                var packet = EncodeFrame(_pendingPcm.AsSpan(0, frameBytes), options);
                if (packet.Total > 0) list.Add(packet);
                ConsumePending(frameBytes);
            }

            if (flush && _pendingCount > 0)
            {
                var padded = new Byte[frameBytes];
                _pendingPcm.AsSpan(0, _pendingCount).CopyTo(padded);
                var packet = EncodeFrame(padded, options);
                if (packet.Total > 0) list.Add(packet);
                _pendingCount = 0;
            }

            return list;
        }
    }

    private IPacket EncodeFrame(ReadOnlySpan<Byte> pcm, OpusOptions options)
    {
        _encoderOptions = options.Clone();

        var packet = OpusManagedCodec.IsAvailable
            ? OpusManagedCodec.EncodeFrame(pcm, options)
            : OpusProcessCodec.EncodeFrame(pcm, options);

        return packet.Length > 0 ? new ArrayPacket(packet) : ArrayPacket.Empty;
    }
    #endregion

    #region 状态
    /// <summary>重置编解码器状态与待编码缓冲</summary>
    public void Reset()
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            _pendingCount = 0;
        }
    }

    /// <summary>释放底层编解码器</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;

            _encoderOptions = null;
            _decoderOptions = null;
            _pendingCount = 0;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpusCodec));
    }
    #endregion

    #region 选项与工厂
    private static OpusOptions ResolveOptions(Object option, OpusOptions fallback)
    {
        if (option is OpusOptions options) return options.Clone();

        if (option is Int32 bitrate)
        {
            var result = fallback?.Clone() ?? new OpusOptions();
            result.Bitrate = bitrate;
            return result;
        }

        if (option is OpusFrameDuration duration)
        {
            var result = fallback?.Clone() ?? new OpusOptions();
            result.FrameDuration = duration;
            return result;
        }

        if (option is String text && Int32.TryParse(text, out bitrate))
        {
            var result = fallback?.Clone() ?? new OpusOptions();
            result.Bitrate = bitrate;
            return result;
        }

        return fallback?.Clone() ?? new OpusOptions();
    }

    private void AppendPending(ReadOnlySpan<Byte> data)
    {
        if (data.Length == 0) return;

        var required = _pendingCount + data.Length;
        if (_pendingPcm.Length < required)
        {
            var size = _pendingPcm.Length;
            while (size < required) size *= 2;

            var buffer = new Byte[size];
            Array.Copy(_pendingPcm, buffer, _pendingCount);
            _pendingPcm = buffer;
        }

        data.CopyTo(_pendingPcm.AsSpan(_pendingCount));
        _pendingCount = required;
    }

    private void ConsumePending(Int32 count)
    {
        if (count >= _pendingCount)
        {
            _pendingCount = 0;
            return;
        }

        Array.Copy(_pendingPcm, count, _pendingPcm, 0, _pendingCount - count);
        _pendingCount -= count;
    }
    #endregion

    #region 辅助
    /// <summary>根据 Opus TOC 配置序号获取 48 kHz 下的每帧样本数</summary>
    /// <param name="mode">TOC 配置序号（兼容旧接口的简化映射）</param>
    /// <returns>每帧每声道样本数</returns>
    public static Int32 GetFrameSize(Int32 mode)
    {
        return mode switch
        {
            0 or 1 or 2 => 120,
            3 or 4 => 240,
            5 or 6 => 480,
            7 or 8 => 960,
            _ => 960,
        };
    }

    /// <summary>获取指定帧时长在 48 kHz 下的每帧样本数</summary>
    /// <param name="duration">帧时长</param>
    /// <returns>每帧每声道样本数</returns>
    public static Int32 GetFrameSize(OpusFrameDuration duration) => (Int32)((Int64)(Int32)duration * BaseSampleRate / 1_000_000);

    /// <summary>解析 Opus 包头部信息</summary>
    /// <param name="packet">Opus 包</param>
    /// <returns>包头部信息</returns>
    public static OpusPacketHeader GetPacketHeader(ReadOnlySpan<Byte> packet) => OpusPacketHeader.Parse(packet);

    /// <summary>获取指定选项下编码器的算法延迟（每声道样本数），用于 Ogg Opus pre-skip</summary>
    /// <param name="options">Opus 选项</param>
    /// <returns>算法延迟样本数；libopus 固定为 6.5 ms 对应的样本数</returns>
    public static Int32 GetLookahead(OpusOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        options.Validate();

        return (Int32)((Int64)options.SampleRate * LookaheadMicroseconds / 1_000_000);
    }
    #endregion
}