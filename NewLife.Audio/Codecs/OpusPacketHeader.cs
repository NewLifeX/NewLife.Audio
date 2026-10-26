namespace NewLife.Audio.Codecs;

/// <summary>Opus 包头部信息（TOC 解析结果）</summary>
/// <remarks>
/// Opus 包的 TOC 字节包含编码模式、带宽与帧长，帧数由打包方式决定。<br/>
/// 样本数统一按 48 kHz 计算，这是 Ogg Opus 的 granule 时间基。
/// </remarks>
public readonly struct OpusPacketHeader
{
    /// <summary>TOC 字节</summary>
    public Byte Toc { get; }

    /// <summary>编码模式</summary>
    public OpusMode Mode { get; }

    /// <summary>编码带宽</summary>
    public OpusBandwidth Bandwidth { get; }

    /// <summary>编码声道数</summary>
    public Int32 Channels { get; }

    /// <summary>包内帧数</summary>
    public Int32 FrameCount { get; }

    /// <summary>每帧每声道样本数（48 kHz）</summary>
    public Int32 SamplesPerFrame { get; }

    /// <summary>整包每声道样本数（48 kHz）</summary>
    public Int32 TotalSamples { get; }

    /// <summary>整包时长（毫秒）</summary>
    public Double DurationMs => TotalSamples / 48.0;

    /// <summary>整包时长（秒）</summary>
    public Double Duration => TotalSamples / 48000.0;

    /// <summary>初始化 Opus 包头部信息</summary>
    /// <param name="toc">TOC 字节</param>
    /// <param name="mode">编码模式</param>
    /// <param name="bandwidth">编码带宽</param>
    /// <param name="channels">编码声道数</param>
    /// <param name="frameCount">帧数</param>
    /// <param name="samplesPerFrame">每帧每声道样本数（48 kHz）</param>
    /// <param name="totalSamples">整包每声道样本数（48 kHz）</param>
    public OpusPacketHeader(Byte toc, OpusMode mode, OpusBandwidth bandwidth, Int32 channels, Int32 frameCount, Int32 samplesPerFrame, Int32 totalSamples)
    {
        Toc = toc;
        Mode = mode;
        Bandwidth = bandwidth;
        Channels = channels;
        FrameCount = frameCount;
        SamplesPerFrame = samplesPerFrame;
        TotalSamples = totalSamples;
    }

    /// <summary>解析 Opus 包头部</summary>
    /// <param name="packet">Opus 包（TOC + payload）</param>
    /// <returns>包头部信息</returns>
    /// <exception cref="ArgumentException">包为空或格式非法时抛出</exception>
    public static OpusPacketHeader Parse(ReadOnlySpan<Byte> packet)
    {
        if (!TryParse(packet, out var header))
            throw new ArgumentException(L.T("Not a valid Opus packet."), nameof(packet));

        return header;
    }

    /// <summary>尝试解析 Opus 包头部</summary>
    /// <param name="packet">Opus 包（TOC + payload）</param>
    /// <param name="header">解析结果</param>
    /// <returns>是否解析成功</returns>
    public static Boolean TryParse(ReadOnlySpan<Byte> packet, out OpusPacketHeader header)
    {
        header = default;
        if (!OpusPacket.TryParse(packet, out var parsed)) return false;

        header = new OpusPacketHeader(parsed.Toc, parsed.Mode, parsed.Bandwidth, parsed.Channels, parsed.FrameCount, parsed.FrameSamplesPerChannel, parsed.TotalSamplesPerChannel);
        return true;
    }
}
