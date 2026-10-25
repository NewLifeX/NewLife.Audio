using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts;

/// <summary>语音合成场景</summary>
/// <remarks>
/// 两种场景对应两种完全不同的工程取舍，选引擎时必须先明确场景：
/// <list type="bullet">
/// <item><see cref="Complete"/>：可以等待整段生成完毕，结果可落盘缓存、下次直接复用。</item>
/// <item><see cref="Streaming"/>：不允许长时间等待，必须边合成边播放，首包延迟优先于音质。</item>
/// </list>
/// </remarks>
public enum TtsScenario
{
    /// <summary>完整：接受较长等待，结果可缓存复用</summary>
    Complete = 1,

    /// <summary>流式：不接受长时间等待，必须边合成边播放</summary>
    Streaming = 2,
}

/// <summary>运行目标。用于按算力与内存挑选引擎</summary>
public enum TtsTarget
{
    /// <summary>桌面：开发机与 PC，算力中等、内存充裕</summary>
    Desktop = 1,

    /// <summary>嵌入式：内存小、算力弱、经常无外网</summary>
    Embedded = 2,

    /// <summary>服务器：算力与内存充裕，可承担批量与高并发</summary>
    Server = 3,
}

/// <summary>TTS 引擎能力标记</summary>
[Flags]
public enum TtsCapability
{
    /// <summary>无</summary>
    None = 0,

    /// <summary>离线：不依赖网络即可合成</summary>
    Offline = 1,

    /// <summary>在线：需要网络访问服务端</summary>
    Online = 2,

    /// <summary>流式：支持分块回调，可边合成边播放</summary>
    Streaming = 4,

    /// <summary>多音色</summary>
    MultiVoice = 8,

    /// <summary>中英混排：自带英文词典或同等能力</summary>
    MultiLanguage = 16,
}

/// <summary>音频来源</summary>
public enum TtsSource
{
    /// <summary>未知</summary>
    None = 0,

    /// <summary>预生成库命中</summary>
    PreGenerated = 1,

    /// <summary>磁盘缓存命中</summary>
    Cache = 2,

    /// <summary>引擎实时合成</summary>
    Engine = 3,
}

/// <summary>数字读法</summary>
public enum TtsNumberMode
{
    /// <summary>自动：四位年份与七位以上号码逐位读，其余按基数读</summary>
    Auto = 0,

    /// <summary>基数读，110 读作“一百一十”</summary>
    Cardinal = 1,

    /// <summary>逐位读，110 读作“一一零”</summary>
    Digit = 2,
}

/// <summary>语音合成请求</summary>
/// <remarks>
/// 请求只描述“要什么”，不描述“用哪个引擎”。路由与降级由 <see cref="TtsRouter"/> 负责。
/// </remarks>
/// <example>
/// <code>
/// var request = new TtsRequest
/// {
///     Text = "设备A2温度过高，请检查",
///     Scenario = TtsScenario.Streaming,
///     Voice = "chaowen",
/// };
/// </code>
/// </example>
public class TtsRequest
{
    /// <summary>待合成文本</summary>
    public String Text { get; set; }

    /// <summary>音色标识。离线引擎为模型名或说话人编号，在线引擎为 voice 参数</summary>
    public String Voice { get; set; }

    /// <summary>语速倍率，1 为正常</summary>
    public Single Speed { get; set; } = 1;

    /// <summary>合成场景</summary>
    public TtsScenario Scenario { get; set; } = TtsScenario.Complete;

    /// <summary>是否启用文本规范化（数字、英文、空白），默认启用</summary>
    public Boolean Normalize { get; set; } = true;

    /// <summary>数字读法</summary>
    public TtsNumberMode NumberMode { get; set; } = TtsNumberMode.Auto;

    /// <summary>是否允许读写缓存，默认启用</summary>
    public Boolean UseCache { get; set; } = true;

    /// <summary>期望输出格式。为 null 时由引擎决定</summary>
    public AudioFormat Format { get; set; }

    /// <summary>调用方透传状态，库内不使用</summary>
    public Object State { get; set; }

    /// <summary>复制请求</summary>
    /// <returns>字段相同的副本</returns>
    public TtsRequest Clone() => new()
    {
        Text = Text,
        Voice = Voice,
        Speed = Speed,
        Scenario = Scenario,
        Normalize = Normalize,
        NumberMode = NumberMode,
        UseCache = UseCache,
        Format = Format?.Clone(),
        State = State,
    };

    /// <summary>返回文本与音色的简短描述，便于日志</summary>
    /// <returns>描述字符串</returns>
    public override String ToString() => $"[{Scenario}]{Voice}@{Speed}x:{Text}";
}

/// <summary>语音合成结果</summary>
/// <remarks>
/// 流式合成时 <see cref="Pcm"/> 通常为空（数据已边合成边写入 <see cref="ITtsSink"/>），
/// 而 <see cref="AudioTime"/>、<see cref="Rtf"/> 等统计字段依然有效。
/// </remarks>
public class TtsResult
{
    /// <summary>音频来源</summary>
    public TtsSource Source { get; set; }

    /// <summary>实际产出音频的引擎名称</summary>
    public String Engine { get; set; }

    /// <summary>实际参与合成的文本（已规范化）</summary>
    public String Text { get; set; }

    /// <summary>音频格式</summary>
    public AudioFormat Format { get; set; }

    /// <summary>整段 PCM 数据（16 位有符号整数交错）。仅完整场景填充</summary>
    public Byte[] Pcm { get; set; }

    /// <summary>合成耗时（秒），不含缓存与排队</summary>
    public Double ElapsedTime { get; set; }

    /// <summary>首包耗时（秒）。非流式引擎等于总耗时</summary>
    public Double FirstChunkTime { get; set; }

    /// <summary>累计输出的 PCM 字节数，流式场景由服务统计</summary>
    public Int64 PcmBytes { get; set; }

    /// <summary>按顺序尝试过的引擎，用于诊断降级链</summary>
    public IList<String> Attempts { get; } = new List<String>();

    /// <summary>音频时长（秒）</summary>
    public Double AudioTime
    {
        get
        {
            var rate = Format?.ByteRate ?? 0;
            var bytes = Pcm != null ? Pcm.Length : PcmBytes;
            if (rate <= 0 || bytes <= 0) return 0;
            return (Double)bytes / rate;
        }
    }

    /// <summary>实时率。小于 1 表示合成快于播放</summary>
    public Double Rtf
    {
        get
        {
            var audio = AudioTime;
            return audio <= 0 ? 0 : ElapsedTime / audio;
        }
    }

    /// <summary>返回便于日志阅读的描述</summary>
    /// <returns>描述字符串</returns>
    public override String ToString()
    {
        var rate = Format?.SampleRate ?? 0;
        return $"{Engine}/{Source} {rate}Hz {AudioTime:F2}s 用时{ElapsedTime:F2}s RTF={Rtf:F2}";
    }
}
