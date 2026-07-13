namespace NewLife.Audio.Codecs;

/// <summary>ADPCM 编解码器状态。存储编码/解码过程中的中间状态</summary>
/// <remarks>
/// IMA-ADPCM 是有状态编解码器，编码/解码过程需要跨帧跟踪预测值和步长索引。
/// 多帧连续处理时，调用方需保留此状态并逐帧传递给 <see cref="ADPCMCodec"/>。
/// </remarks>
public class AdpcmState
{
    /// <summary>上一个采样数据。当 <see cref="Index"/> 为 0 时，该值为未压缩的原数据</summary>
    public Int16 Valprev { get; set; }

    /// <summary>保留数据（未使用）</summary>
    public Byte Reserved { get; set; }

    /// <summary>上一个 block 最后一个 index，第一个 block 的 index = 0</summary>
    public Byte Index { get; set; }
}
