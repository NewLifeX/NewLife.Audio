namespace NewLife.Audio.DSP;

/// <summary>音频格式描述</summary>
public class AudioFormat
{
    /// <summary>采样率（Hz），如 8000、16000、44100、48000</summary>
    public Int32 SampleRate { get; set; } = 8000;

    /// <summary>声道数</summary>
    public Int32 Channels { get; set; } = 1;

    /// <summary>每样本位数（8/16/24/32）</summary>
    public Int32 BitsPerSample { get; set; } = 16;

    /// <summary>编码类型</summary>
    public AVTypes Encoding { get; set; } = AVTypes.LPCM;

    /// <summary>每帧样本数</summary>
    public Int32 SamplesPerFrame { get; set; } = 160;

    /// <summary>计算每秒字节数</summary>
    public Int32 ByteRate => SampleRate * Channels * BitsPerSample / 8;

    /// <summary>每样本字节数</summary>
    public Int32 BytesPerSample => BitsPerSample / 8;

    /// <summary>每帧字节数</summary>
    public Int32 BytesPerFrame => SamplesPerFrame * Channels * BytesPerSample;

    /// <summary>创建默认 8kHz 单声道 16-bit PCM 格式</summary>
    public static AudioFormat Default => new()
    {
        SampleRate = 8000,
        Channels = 1,
        BitsPerSample = 16,
    };

    /// <summary>克隆格式</summary>
    public AudioFormat Clone() => new()
    {
        SampleRate = SampleRate,
        Channels = Channels,
        BitsPerSample = BitsPerSample,
        Encoding = Encoding,
        SamplesPerFrame = SamplesPerFrame,
    };
}
