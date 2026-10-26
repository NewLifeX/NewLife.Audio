namespace NewLife.Audio.Speech.Sherpa;

/// <summary>sherpa-onnx 支持的离线识别模型架构</summary>
public enum SherpaAsrKind
{
    /// <summary>Transducer（zipformer/conformer + joiner）</summary>
    Transducer = 0,

    /// <summary>Paraformer（中文常用）</summary>
    Paraformer = 1,

    /// <summary>SenseVoice（多语种 + 情感/事件标签）</summary>
    SenseVoice = 2,

    /// <summary>Whisper（多语种）</summary>
    Whisper = 3,

    /// <summary>Zipformer CTC / wenet CTC 等单模型 CTC</summary>
    Ctc = 4,
}

/// <summary>一个可用的离线识别模型档案</summary>
/// <remarks>
/// 字段由模型目录自动发现，不硬编码具体文件名：Transducer 取 <c>encoder*.onnx</c>/<c>decoder*.onnx</c>/<c>joiner*.onnx</c>，
/// Paraformer/SenseVoice/Whisper 取目录内最大的 <c>*.onnx</c>，CTC 取单个 <c>model*.onnx</c>；均需 <c>tokens.txt</c>。
/// </remarks>
public sealed class SherpaAsrModel
{
    /// <summary>档案名，默认取模型目录名</summary>
    public String Name { get; set; }

    /// <summary>架构类型</summary>
    public SherpaAsrKind Kind { get; set; }

    /// <summary>模型目录绝对路径</summary>
    public String Dir { get; set; }

    /// <summary>主模型（CTC/Paraformer/SenseVoice/Whisper 单文件）</summary>
    public String Model { get; set; }

    /// <summary>Transducer 编码器</summary>
    public String Encoder { get; set; }

    /// <summary>Transducer 解码器</summary>
    public String Decoder { get; set; }

    /// <summary>Transducer 连接器</summary>
    public String Joiner { get; set; }

    /// <summary>tokens.txt</summary>
    public String Tokens { get; set; }

    /// <summary>模型语种标签（如 zh/en），供上层筛选，默认空</summary>
    public String Language { get; set; }

    /// <summary>推理线程数，默认 2</summary>
    public Int32 NumThreads { get; set; } = 2;

    /// <summary>推理后端，默认 cpu</summary>
    public String Provider { get; set; } = "cpu";
}
