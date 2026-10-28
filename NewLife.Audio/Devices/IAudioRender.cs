using System;

namespace NewLife.Audio.Devices;

/// <summary>音频渲染接口（推模式，支持 Span 与背压）</summary>
/// <remarks>
/// 这是播放器与门面之间的内部契约：支持 Span 写入与水位等待的播放器实现本接口，
/// <see cref="AudioPlayer"/> 优先使用它，未实现时回退到 <see cref="IAudioPlayer.Write(Byte[])"/> 与按积压轮询。
/// </remarks>
internal interface IAudioRender
{
    /// <summary>写入 PCM 数据</summary>
    /// <param name="data">16 位小端交错 PCM</param>
    void Write(ReadOnlySpan<Byte> data);

    /// <summary>缓冲区中待播放的字节数</summary>
    Int32 BufferedBytes { get; }

    /// <summary>等待缓冲回落到水位以下</summary>
    /// <param name="maxBufferedBytes">允许的最大积压字节数</param>
    /// <param name="timeoutMilliseconds">超时时间（毫秒），小于 0 表示一直等</param>
    /// <returns>是否已回到水位以下</returns>
    Boolean WaitForSpace(Int32 maxBufferedBytes, Int32 timeoutMilliseconds);
}
