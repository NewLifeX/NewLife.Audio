using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts;

/// <summary>音频输出格式</summary>
public enum TtsFormat
{
    /// <summary>WAV 容器（RIFF/WAVE，16 位 PCM）</summary>
    Wav = 1,

    /// <summary>裸 PCM（16 位有符号整数，小端）</summary>
    Pcm = 2,

    /// <summary>MP3 文件（ID3v2 标签 + MPEG 音频帧）</summary>
    Mp3 = 3,

    /// <summary>FLAC 文件（无损压缩）</summary>
    Flac = 4,
}

/// <summary>语音合成选项</summary>
/// <remarks>作为 <see cref="TtsClient.Options"/> 的默认值，或在单次调用中覆盖。</remarks>
public sealed class TtsOptions
{
    /// <summary>音色标识。离线引擎为模型/说话人，在线引擎为服务端 voice 参数</summary>
    public String Voice { get; set; }

    /// <summary>语速倍率，1 为正常</summary>
    public Single Rate { get; set; } = 1;

    /// <summary>音量，0~1</summary>
    public Single Volume { get; set; } = 1;

    /// <summary>复制选项</summary>
    /// <returns>字段相同的副本</returns>
    public TtsOptions Clone() => new()
    {
        Voice = Voice,
        Rate = Rate,
        Volume = Volume,
    };
}

/// <summary>流式音频块</summary>
public sealed class TtsChunk
{
    /// <summary>音频格式。每个块都携带，方便消费者直接初始化播放器</summary>
    public AudioFormat Format { get; set; }

    /// <summary>裸 PCM 数据（16 位有符号整数，小端）</summary>
    public Byte[] Data { get; set; }
}

/// <summary>语音使用场景</summary>
/// <remarks>用于跨引擎挑选音色：同一段播报逻辑在告警、通知、长文本与对话场景下需要不同的声音气质。</remarks>
public enum TtsScenario
{
    /// <summary>通用，不限定场景</summary>
    General = 0,

    /// <summary>告警播报，要求吐字清楚、辨识度高</summary>
    Alert = 1,

    /// <summary>通知提示，多为简短消息</summary>
    Notification = 2,

    /// <summary>长文本朗读，强调自然耐听</summary>
    Narration = 3,

    /// <summary>对话交互，强调亲和力</summary>
    Conversation = 4,
}

/// <summary>可用音色</summary>
/// <remarks>
/// 一份描述跨引擎通用：<see cref="Id"/> 是引擎原生音色标识，直接赋给 <see cref="TtsOptions.Voice"/>；
/// <see cref="Culture"/> 描述语言（可只写语种，如 <c>en</c>）；<see cref="Scenarios"/> 描述适用场景。
/// 跨引擎汇总与挑选由 <see cref="TtsVoiceCatalog"/> 完成。
/// </remarks>
public sealed class TtsVoice
{
    private String _id;

    /// <summary>音色名称，用于展示与日志</summary>
    public String Name { get; set; }

    /// <summary>引擎原生音色标识。为空时回落到 <see cref="Name"/></summary>
    public String Id
    {
        get => _id.IsNullOrEmpty() ? Name : _id;
        set => _id = value;
    }

    /// <summary>区域，如 zh-CN、en-US；只支持特定语种时可只写 zh、en</summary>
    public String Culture { get; set; }

    /// <summary>性别，如 Female、Male；未知时为空</summary>
    public String Gender { get; set; }

    /// <summary>适用场景。为空表示通用</summary>
    public IList<TtsScenario> Scenarios { get; set; } = [];

    /// <summary>是否为引擎默认音色。每个引擎最多一个</summary>
    public Boolean IsDefault { get; set; }

    /// <summary>返回便于日志阅读的描述</summary>
    /// <returns>名称与区域</returns>
    public override String ToString() => $"{Name}({Culture})";
}

/// <summary>语音合成结果</summary>
/// <remarks>由 <see cref="TtsClient.SaveAsync(String, Stream, TtsFormat, TtsOptions, CancellationToken)"/> 统计并返回。</remarks>
public class TtsResult
{
    /// <summary>实际产出音频的引擎名称</summary>
    public String Engine { get; set; }

    /// <summary>音频格式</summary>
    public AudioFormat Format { get; set; }

    /// <summary>累计输出的 PCM 字节数</summary>
    public Int64 Bytes { get; set; }

    /// <summary>合成耗时（秒）</summary>
    public Double ElapsedTime { get; set; }

    /// <summary>首包耗时（秒）。非流式引擎等于总耗时</summary>
    public Double FirstChunkTime { get; set; }

    /// <summary>保存到文件时的输出路径</summary>
    public String OutputPath { get; set; }

    /// <summary>音频时长（秒）</summary>
    public Double AudioTime
    {
        get
        {
            var rate = Format?.ByteRate ?? 0;
            if (rate <= 0 || Bytes <= 0) return 0;
            return (Double)Bytes / rate;
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
        return $"{Engine} {rate}Hz {AudioTime:F2}s 用时{ElapsedTime:F2}s RTF={Rtf:F2}";
    }
}
