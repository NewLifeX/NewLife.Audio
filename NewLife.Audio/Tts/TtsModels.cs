using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts;

/// <summary>音频输出格式</summary>
public enum TtsFormat
{
    /// <summary>WAV 容器（RIFF/WAVE，16 位 PCM）</summary>
    Wav = 1,

    /// <summary>裸 PCM（16 位有符号整数，小端）</summary>
    Pcm = 2,
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

/// <summary>可用音色</summary>
public sealed class TtsVoice
{
    /// <summary>音色名称</summary>
    public String Name { get; set; }

    /// <summary>区域，如 zh-CN、en-US</summary>
    public String Culture { get; set; }

    /// <summary>性别</summary>
    public String Gender { get; set; }

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
