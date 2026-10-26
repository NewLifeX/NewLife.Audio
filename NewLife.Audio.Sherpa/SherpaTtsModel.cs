namespace NewLife.Audio.Tts.Sherpa;

/// <summary>sherpa-onnx 支持的 TTS 模型架构</summary>
public enum SherpaTtsKind
{
    /// <summary>VITS（piper / icefall / MeloTTS 转换而来，声码器在模型内）</summary>
    Vits = 0,

    /// <summary>Matcha-TTS（声学模型 + 独立声码器）</summary>
    Matcha = 1,

    /// <summary>Kokoro（StyleTTS2，主模型 + voices.bin 音色库）</summary>
    Kokoro = 2,
}

/// <summary>一个可用的离线 TTS 模型档案</summary>
/// <remarks>
/// 字段全部由模型目录自动发现，不硬编码文件名：VITS 取目录内最大的 <c>*.onnx</c> 与 <c>tokens.txt</c>；
/// Matcha 取最大 <c>*.onnx</c> 作声学模型、声码器从模型根目录按名称挑选；Kokoro 另需 <c>voices.bin</c>。
/// </remarks>
public sealed class SherpaTtsModel
{
    /// <summary>档案名，默认取模型目录名</summary>
    public String Name { get; set; }

    /// <summary>架构类型</summary>
    public SherpaTtsKind Kind { get; set; }

    /// <summary>模型目录绝对路径</summary>
    public String Dir { get; set; }

    /// <summary>主模型文件</summary>
    public String Model { get; set; }

    /// <summary>Matcha 声码器，其他架构为 null</summary>
    public String Vocoder { get; set; }

    /// <summary>Kokoro 音色库 voices.bin，其他架构为 null</summary>
    public String Voices { get; set; }

    /// <summary>tokens.txt</summary>
    public String Tokens { get; set; }

    /// <summary>词典文件，多个用逗号分隔</summary>
    public String Lexicon { get; set; }

    /// <summary>espeak-ng 数据目录</summary>
    public String DataDir { get; set; }

    /// <summary>jieba 词典目录</summary>
    public String DictDir { get; set; }

    /// <summary>文本正则规则（phone/date/number 等 FST），多个用逗号分隔</summary>
    public String RuleFsts { get; set; }

    /// <summary>文本正则规则包（rule.far），多个用逗号分隔</summary>
    public String RuleFars { get; set; }

    /// <summary>默认说话人编号</summary>
    public Int32 DefaultSid { get; set; }

    /// <summary>许可标注，商用前必须核对</summary>
    public String License { get; set; } = "Unknown";

    /// <summary>补充说明</summary>
    public String Note { get; set; } = "";

    /// <summary>不可用原因；为 null 表示可直接加载</summary>
    public String Unsupported { get; set; }

    /// <summary>模型自带英文词典，可以直接读英文单词</summary>
    public Boolean ReadsEnglish { get; set; }

    /// <summary>是否具备数字/日期正则前端</summary>
    public Boolean HasRules => !RuleFsts.IsNullOrEmpty() || !RuleFars.IsNullOrEmpty();

    /// <summary>是否可被 sherpa-onnx 直接加载</summary>
    public Boolean CanLoad => Unsupported.IsNullOrEmpty();

    /// <summary>返回一行摘要</summary>
    /// <returns>形如 <c>matcha-icefall-zh-baker [Matcha] 规则=有 许可=非商用</c></returns>
    public override String ToString() => $"{Name} [{Kind}] {(HasRules ? "规则=有" : "规则=无")} 许可={License}";
}
