namespace NewLife.Audio.DSP;

/// <summary>音频处理器统一接口。读模型：从 Source 拉取数据，处理后输出</summary>
/// <remarks>
/// 所有 DSP 处理器实现此接口，内部统一使用 32-bit 浮点处理（-1.0 ~ 1.0）。
/// 通过 <see cref="Source"/> 属性实现链式组合：每个处理器只有一个上游源。
/// </remarks>
public interface IAudioProcessor
{
    /// <summary>输入音频格式</summary>
    AudioFormat InputFormat { get; }

    /// <summary>输出音频格式（处理后）</summary>
    AudioFormat OutputFormat { get; }

    /// <summary>上游数据源。设为 null 表示这是管线起点</summary>
    IAudioProcessor Source { get; set; }

    /// <summary>从处理器拉取处理后的浮点采样数据</summary>
    /// <param name="buffer">输出缓冲区</param>
    /// <param name="offset">缓冲区起始偏移</param>
    /// <param name="count">要读取的采样数（每声道）</param>
    /// <returns>实际读取的采样数，0 表示结束</returns>
    Int32 Read(Single[] buffer, Int32 offset, Int32 count);

    /// <summary>重置处理器状态</summary>
    void Reset();
}
