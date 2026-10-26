namespace NewLife.Audio.Codecs;

/// <summary>Opus 进程内托管编解码器（自研路线）</summary>
/// <remarks>
/// M1 仅提供门面与降级切换点；CELT/SILK 托管实现按 M2~M5 填充。<br/>
/// <see cref="IsAvailable"/> 在 M2 完成后返回 true，<see cref="OpusCodec"/> 将自动优先使用本实现。
/// </remarks>
public static class OpusManagedCodec
{
    /// <summary>进程内托管编解码器是否可用</summary>
    /// <remarks>M1 为 false；M2 完成 CELT 解码后按能力逐步开启。</remarks>
    public static Boolean IsAvailable => false;

    /// <summary>解码单个 Opus 包为 16 位小端交错 PCM</summary>
    /// <param name="packet">Opus 包（TOC + payload）</param>
    /// <param name="options">输出采样率与声道数</param>
    /// <returns>PCM 数据</returns>
    /// <exception cref="NotSupportedException">托管解码器尚未实现时抛出</exception>
    public static Byte[] DecodePacket(ReadOnlySpan<Byte> packet, OpusOptions options) =>
        throw new NotSupportedException(L.T("Managed Opus decoder is not implemented yet (M2)."));

    /// <summary>编码一帧 16 位小端交错 PCM 为单个 Opus 包</summary>
    /// <param name="pcm">PCM 数据</param>
    /// <param name="options">编码选项</param>
    /// <returns>Opus 包（TOC + payload）</returns>
    /// <exception cref="NotSupportedException">托管编码器尚未实现时抛出</exception>
    public static Byte[] EncodeFrame(ReadOnlySpan<Byte> pcm, OpusOptions options) =>
        throw new NotSupportedException(L.T("Managed Opus encoder is not implemented yet (M3/M5)."));
}
