namespace NewLife.Audio.Codecs;

/// <summary>Vorbis 编码参数</summary>
/// <remarks>
/// Vorbis 为可变码率编码，质量档位 0~10 对应从低到高的目标质量；
/// 采样率支持 8k~192k，声道数支持 1~8。
/// </remarks>
public class VorbisOptions
{
    /// <summary>采样率（Hz），默认 44100</summary>
    public Int32 SampleRate { get; set; } = 44100;

    /// <summary>声道数，默认 1</summary>
    public Int32 Channels { get; set; } = 1;

    /// <summary>质量档位 0~10，默认 5</summary>
    public Int32 Quality { get; set; } = 5;

    /// <summary>短块大小，默认 256</summary>
    public Int32 BlockSize0 { get; set; } = 256;

    /// <summary>长块大小，默认 2048</summary>
    public Int32 BlockSize1 { get; set; } = 2048;

    /// <summary>校验参数并返回规范化选项</summary>
    /// <returns>规范化后的选项</returns>
    public VorbisOptions Validate()
    {
        if (SampleRate < 8000 || SampleRate > 192000) throw new ArgumentOutOfRangeException(nameof(SampleRate));
        if (Channels < 1 || Channels > 8) throw new ArgumentOutOfRangeException(nameof(Channels));
        if (Quality < 0 || Quality > 10) throw new ArgumentOutOfRangeException(nameof(Quality));
        if (BlockSize0 < 64 || BlockSize0 > 8192 || (BlockSize0 & (BlockSize0 - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(BlockSize0));
        if (BlockSize1 < BlockSize0 || BlockSize1 > 8192 || (BlockSize1 & (BlockSize1 - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(BlockSize1));

        return this;
    }
}
