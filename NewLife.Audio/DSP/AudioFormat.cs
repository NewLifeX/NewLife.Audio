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

    /// <summary>是否为 IEEE 浮点采样（32/64 位 float）</summary>
    public Boolean IsFloat { get; set; }

    /// <summary>采样格式（由 <see cref="BitsPerSample"/> 与 <see cref="IsFloat"/> 推导）</summary>
    public SampleFormat SampleFormat => PcmConverter.FromBits(BitsPerSample, IsFloat);

    /// <summary>是否为平面布局（各声道分开存放）。false 表示交错布局</summary>
    public Boolean IsPlanar { get; set; }

    /// <summary>有效位数（容器位深中实际使用的位数）。0 表示等于 <see cref="BitsPerSample"/></summary>
    public Int32 ValidBitsPerSample { get; set; }

    /// <summary>声道掩码（如 WAVEFORMATEXTENSIBLE 的 dwChannelMask）。0 表示未指定</summary>
    public Int32 ChannelMask { get; set; }

    /// <summary>实际有效位数。未指定或超出容器位深时回退到 <see cref="BitsPerSample"/></summary>
    public Int32 EffectiveValidBitsPerSample => ValidBitsPerSample > 0 && ValidBitsPerSample <= BitsPerSample ? ValidBitsPerSample : BitsPerSample;

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
        IsFloat = IsFloat,
        IsPlanar = IsPlanar,
        ValidBitsPerSample = ValidBitsPerSample,
        ChannelMask = ChannelMask,
        SamplesPerFrame = SamplesPerFrame,
    };
}
