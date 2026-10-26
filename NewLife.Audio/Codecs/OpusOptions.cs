using NewLife;

namespace NewLife.Audio.Codecs;

/// <summary>Opus 应用模式</summary>
public enum OpusApplication
{
    /// <summary>语音通话，优先低码率与低延迟</summary>
    Voip = 0,

    /// <summary>通用音频/音乐，优先音质</summary>
    Audio = 1,

    /// <summary>受限低延迟，仅使用 CELT 模式</summary>
    RestrictedLowDelay = 2,
}

/// <summary>Opus 编码带宽</summary>
public enum OpusBandwidth
{
    /// <summary>自动选择</summary>
    Auto = 0,

    /// <summary>窄带 4 kHz</summary>
    Narrowband = 1,

    /// <summary>中带 6 kHz</summary>
    Mediumband = 2,

    /// <summary>宽带 8 kHz</summary>
    Wideband = 3,

    /// <summary>超宽带 12 kHz</summary>
    SuperWideband = 4,

    /// <summary>全带 20 kHz</summary>
    Fullband = 5,

    /// <summary>未知（仅用于解析 Opus 包）</summary>
    Unknown = 255,
}

/// <summary>Opus 信号类型</summary>
public enum OpusSignal
{
    /// <summary>自动判断</summary>
    Auto = 0,

    /// <summary>语音</summary>
    Voice = 1,

    /// <summary>音乐</summary>
    Music = 2,
}

/// <summary>Opus 帧时长</summary>
/// <remarks>枚举值为微秒，便于换算任意合法采样率下的每帧样本数。</remarks>
public enum OpusFrameDuration
{
    /// <summary>2.5 毫秒</summary>
    Ms2_5 = 2500,

    /// <summary>5 毫秒</summary>
    Ms5 = 5000,

    /// <summary>10 毫秒</summary>
    Ms10 = 10000,

    /// <summary>20 毫秒</summary>
    Ms20 = 20000,

    /// <summary>40 毫秒</summary>
    Ms40 = 40000,

    /// <summary>60 毫秒</summary>
    Ms60 = 60000,
}

/// <summary>Opus 编码模式（从 Opus 包 TOC 字节解析）</summary>
public enum OpusMode
{
    /// <summary>未知</summary>
    Unknown = 0,

    /// <summary>SILK 语音模式</summary>
    SilkOnly = 1,

    /// <summary>SILK + CELT 混合模式</summary>
    Hybrid = 2,

    /// <summary>CELT 音乐/低延迟模式</summary>
    CeltOnly = 3,
}

/// <summary>Opus 编解码选项</summary>
/// <remarks>
/// 默认 48 kHz 单声道 20 ms 帧、64 kbps、通用音频模式。<br/>
/// 同一编解码器实例的采样率与声道数应保持稳定；修改选项会影响后续帧的编码/解码参数。
/// </remarks>
public sealed class OpusOptions
{
    /// <summary>采样率（Hz），支持 8000/12000/16000/24000/48000</summary>
    public Int32 SampleRate { get; set; } = 48000;

    /// <summary>声道数，支持 1（单声道）或 2（立体声）</summary>
    public Int32 Channels { get; set; } = 1;

    /// <summary>目标比特率（bps），默认 64000</summary>
    public Int32 Bitrate { get; set; } = 64000;

    /// <summary>编码帧时长，默认 20 毫秒</summary>
    public OpusFrameDuration FrameDuration { get; set; } = OpusFrameDuration.Ms20;

    /// <summary>应用模式，默认通用音频</summary>
    public OpusApplication Application { get; set; } = OpusApplication.Audio;

    /// <summary>最大编码带宽，默认自动</summary>
    public OpusBandwidth Bandwidth { get; set; } = OpusBandwidth.Auto;

    /// <summary>信号类型，默认自动</summary>
    public OpusSignal Signal { get; set; } = OpusSignal.Auto;

    /// <summary>是否启用可变比特率（VBR），默认 true</summary>
    public Boolean UseVbr { get; set; } = true;

    /// <summary>是否启用受约束的 VBR</summary>
    public Boolean UseConstrainedVbr { get; set; }

    /// <summary>是否启用不连续传输（DTX，静音时停发）</summary>
    /// <remarks>M1 进程回退不生效，M5 托管 SILK 编码器启用。</remarks>
    public Boolean UseDtx { get; set; }

    /// <summary>是否启用带内前向纠错（FEC）</summary>
    /// <remarks>M1 进程回退不生效，M4/M5 托管 SILK 启用。</remarks>
    public Boolean UseInbandFec { get; set; }

    /// <summary>预期丢包率（0~100），用于 FEC 与码率决策</summary>
    public Int32 PacketLossPercent { get; set; }

    /// <summary>编码复杂度（0~10），越高音质越好但越慢，默认 10</summary>
    public Int32 Complexity { get; set; } = 10;

    /// <summary>输入 PCM 有效位深（8~24），默认 16</summary>
    public Int32 LsbDepth { get; set; } = 16;

    /// <summary>解码时是否使用带内 FEC 恢复上一帧</summary>
    /// <remarks>M1 进程回退不生效，M4 托管 SILK 解码器启用。</remarks>
    public Boolean DecodeFec { get; set; }

    /// <summary>每帧每声道样本数</summary>
    public Int32 SamplesPerFrame => (Int32)((Int64)SampleRate * (Int32)FrameDuration / 1_000_000);

    /// <summary>每帧字节数（16 位交错 PCM）</summary>
    public Int32 FrameBytes => SamplesPerFrame * Channels * 2;

    /// <summary>复制选项</summary>
    /// <returns>字段相同的副本</returns>
    public OpusOptions Clone() => new()
    {
        SampleRate = SampleRate,
        Channels = Channels,
        Bitrate = Bitrate,
        FrameDuration = FrameDuration,
        Application = Application,
        Bandwidth = Bandwidth,
        Signal = Signal,
        UseVbr = UseVbr,
        UseConstrainedVbr = UseConstrainedVbr,
        UseDtx = UseDtx,
        UseInbandFec = UseInbandFec,
        PacketLossPercent = PacketLossPercent,
        Complexity = Complexity,
        LsbDepth = LsbDepth,
        DecodeFec = DecodeFec,
    };

    /// <summary>校验选项合法性</summary>
    /// <exception cref="ArgumentOutOfRangeException">字段超出 Opus 规范范围时抛出</exception>
    public void Validate()
    {
        if (!IsSupportedSampleRate(SampleRate))
            throw new ArgumentOutOfRangeException(nameof(SampleRate), L.T("Opus sample rate must be 8000, 12000, 16000, 24000, or 48000."));
        if (Channels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(Channels), L.T("Opus supports 1 or 2 channels."));
        if (Bitrate < 500 || Bitrate > 512000)
            throw new ArgumentOutOfRangeException(nameof(Bitrate), L.T("Opus bitrate must be between 500 and 512000 bps."));
        if (FrameDuration is not (OpusFrameDuration.Ms2_5 or OpusFrameDuration.Ms5 or OpusFrameDuration.Ms10 or OpusFrameDuration.Ms20 or OpusFrameDuration.Ms40 or OpusFrameDuration.Ms60))
            throw new ArgumentOutOfRangeException(nameof(FrameDuration), L.T("Unsupported Opus frame duration."));
        if (PacketLossPercent < 0 || PacketLossPercent > 100)
            throw new ArgumentOutOfRangeException(nameof(PacketLossPercent), L.T("Packet loss percentage must be between 0 and 100."));
        if (Complexity < 0 || Complexity > 10)
            throw new ArgumentOutOfRangeException(nameof(Complexity), L.T("Opus complexity must be between 0 and 10."));
        if (LsbDepth < 8 || LsbDepth > 24)
            throw new ArgumentOutOfRangeException(nameof(LsbDepth), L.T("Opus PCM bit depth must be between 8 and 24."));
    }

    /// <summary>判断采样率是否为 Opus 支持的取值</summary>
    /// <param name="sampleRate">采样率（Hz）</param>
    /// <returns>是否支持</returns>
    public static Boolean IsSupportedSampleRate(Int32 sampleRate) =>
        sampleRate is 8000 or 12000 or 16000 or 24000 or 48000;

    /// <summary>获取最接近的 Opus 支持采样率</summary>
    /// <param name="sampleRate">期望采样率（Hz）</param>
    /// <returns>8000/12000/16000/24000/48000 中最接近的值</returns>
    public static Int32 GetNearestSampleRate(Int32 sampleRate)
    {
        if (IsSupportedSampleRate(sampleRate)) return sampleRate;

        var rates = new[] { 8000, 12000, 16000, 24000, 48000 };
        var best = rates[0];
        var distance = Math.Abs(sampleRate - best);
        for (var i = 1; i < rates.Length; i++)
        {
            var current = Math.Abs(sampleRate - rates[i]);
            if (current < distance)
            {
                best = rates[i];
                distance = current;
            }
        }

        return best;
    }
}
