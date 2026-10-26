namespace NewLife.Audio.Codecs;

/// <summary>AAC 编码选项</summary>
/// <remarks>
/// 纯托管 AAC-LC 编码器。首版只使用长窗（1024 样本/帧），支持单声道/立体声与常见采样率。
/// </remarks>
public sealed class AacEncoderOptions
{
    /// <summary>采样率（Hz）。默认 44100</summary>
    public Int32 SampleRate { get; set; } = 44100;

    /// <summary>声道数（1 或 2）。默认 1</summary>
    public Int32 Channels { get; set; } = 1;

    /// <summary>目标码率（bps）。默认 64000；设为 0 表示固定质量（VBR）</summary>
    public Int32 Bitrate { get; set; } = 64000;

    /// <summary>是否输出 ADTS 封装。默认 true；false 时输出 raw AAC access unit</summary>
    public Boolean Adts { get; set; } = true;

    /// <summary>每帧目标最大量化幅值。越大码率越高、音质越好。默认 8</summary>
    public Int32 TargetMaxQuantizedMagnitude { get; set; } = 8;

    /// <summary>校验选项</summary>
    /// <exception cref="ArgumentOutOfRangeException">参数非法</exception>
    public void Validate()
    {
        if (SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(SampleRate));
        if (Channels is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(Channels), "AAC encoder supports 1 or 2 channels.");
        if (Bitrate < 0) throw new ArgumentOutOfRangeException(nameof(Bitrate));
        if (TargetMaxQuantizedMagnitude < 1) throw new ArgumentOutOfRangeException(nameof(TargetMaxQuantizedMagnitude));
    }
}
