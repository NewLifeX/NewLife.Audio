namespace NewLife.Audio.Codecs;

/// <summary>MP3 编码参数</summary>
/// <remarks>
/// 采样率决定 MPEG 版本：32/44.1/48 kHz 为 MPEG-1，16/22.05/24 kHz 为 MPEG-2 LSF，
/// 8/11.025/12 kHz 为 MPEG-2.5；比特率会自动吸附到对应版本的标准档位。
/// </remarks>
public class Mp3EncoderOptions
{
    /// <summary>采样率（Hz），支持 8000/11025/12000/16000/22050/24000/32000/44100/48000</summary>
    public Int32 SampleRate { get; set; } = 44100;

    /// <summary>声道数，1 为单声道，2 为立体声</summary>
    public Int32 Channels { get; set; } = 1;

    /// <summary>目标比特率（kbps），CBR 为固定值，ABR 为平均值</summary>
    public Int32 Bitrate { get; set; } = 128;

    /// <summary>码率模式，默认 CBR</summary>
    public Mp3RateMode Mode { get; set; } = Mp3RateMode.Cbr;

    /// <summary>VBR 质量档位，0 最高质量、9 最小体积，默认 2</summary>
    /// <remarks>仅在 <see cref="Mode"/> 为 <see cref="Mp3RateMode.Vbr"/> 时生效</remarks>
    public Int32 VbrQuality { get; set; } = 2;

    /// <summary>VBR/ABR 的最低比特率（kbps），0 表示使用当前 MPEG 版本的最低档</summary>
    public Int32 MinBitrate { get; set; }

    /// <summary>VBR/ABR 的最高比特率（kbps），0 表示使用当前 MPEG 版本的最高档</summary>
    public Int32 MaxBitrate { get; set; }
}
