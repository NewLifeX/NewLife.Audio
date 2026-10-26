using System;
using System.Collections.Generic;
using NewLife.Audio.DSP;
using SherpaOnnx;

namespace NewLife.Audio.Speech.Sherpa;

/// <summary>sherpa-onnx 离线语音识别引擎</summary>
/// <remarks>
/// 把 <see cref="SherpaAsrModel"/> 装配成 <c>OfflineRecognizer</c>，整段 PCM 一次性识别（非流式）。
/// 端点切分、部分结果与时间戳由 <see cref="SegmentedSpeechRecognizer"/> 统一处理，核心库零推理依赖。<br/>
/// 模型文件缺失或加载失败时 <see cref="IsAvailable"/> 为 false，识别返回空文本而不会抛异常。
/// </remarks>
/// <example>
/// <code>
/// var model = SherpaAsrCatalog.Find("sensevoice");
/// using var engine = new SherpaSpeechRecognitionEngine(model);
/// using var recognizer = new SegmentedSpeechRecognizer(engine, format);
/// </code>
/// </example>
public sealed class SherpaSpeechRecognitionEngine : ISpeechRecognitionEngine, IDisposable
{
    private readonly Object _lock = new();
    private readonly String _language;
    private OfflineRecognizer _recognizer;
    private String _loadError;

    /// <summary>模型档案</summary>
    public SherpaAsrModel Model { get; }

    /// <summary>引擎是否可用（模型成功加载）</summary>
    public Boolean IsAvailable => _recognizer != null;

    /// <summary>加载失败原因；可用时为空</summary>
    public String LoadError => _loadError;

    /// <summary>构造引擎并尝试加载模型</summary>
    /// <param name="model">模型档案，可为 null（此时不可用）</param>
    public SherpaSpeechRecognitionEngine(SherpaAsrModel model)
    {
        Model = model;
        _language = model?.Language;
        if (model == null)
        {
            _loadError = "Model is null.";

            return;
        }

        try
        {
            _recognizer = Create(model);
        }
        catch (Exception ex)
        {
            _loadError = ex.Message;
            _recognizer = null;
        }
    }

    /// <summary>按路径直接加载模型</summary>
    /// <param name="modelDir">模型目录</param>
    public SherpaSpeechRecognitionEngine(String modelDir) : this(SherpaAsrCatalog.Inspect(modelDir)) { }

    /// <summary>识别一段语音</summary>
    /// <param name="pcm">16 位小端交错 PCM</param>
    /// <param name="format">音频格式</param>
    /// <param name="options">识别参数</param>
    /// <returns>识别文本；引擎不可用或无结果时文本为空</returns>
    public SpeechRecognitionResult Transcribe(Byte[] pcm, AudioFormat format, SpeechRecognitionOptions options)
    {
        var result = new SpeechRecognitionResult();
        if (_recognizer == null || pcm == null || pcm.Length < 2 || format == null) return result;

        lock (_lock)
        {
            try
            {
                var samples = PcmConverter.ToFloat(pcm, SampleFormat.Int16);
                var channels = Math.Max(1, format.Channels);
                if (channels > 1)
                {
                    // 取第一声道（识别模型均为单声道）
                    var mono = new Single[samples.Length / channels];
                    for (var i = 0; i < mono.Length; i++) mono[i] = samples[i * channels];
                    samples = mono;
                }

                using var stream = _recognizer.CreateStream();
                stream.AcceptWaveform(format.SampleRate, samples);
                _recognizer.Decode(stream);

                var text = stream.Result?.Text;
                result.Text = text.IsNullOrEmpty() ? null : text.Trim();
                result.Confidence = 0;
            }
            catch (Exception ex)
            {
                _loadError = ex.Message;
                result.Text = null;
            }
        }

        return result;
    }

    /// <summary>释放识别器</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }

    #region 辅助
    private static OfflineRecognizer Create(SherpaAsrModel model)
    {
        var config = new OfflineRecognizerConfig();
        var mc = config.ModelConfig;
        mc.Tokens = model.Tokens;
        mc.NumThreads = model.NumThreads > 0 ? model.NumThreads : 2;
        mc.Provider = model.Provider.IsNullOrEmpty() ? "cpu" : model.Provider;

        switch (model.Kind)
        {
            case SherpaAsrKind.Transducer:
                mc.Transducer.Encoder = model.Encoder;
                mc.Transducer.Decoder = model.Decoder ?? "";
                mc.Transducer.Joiner = model.Joiner;
                break;
            case SherpaAsrKind.Paraformer:
                mc.Paraformer.Model = model.Model;
                break;
            case SherpaAsrKind.SenseVoice:
                mc.SenseVoice.Model = model.Model;
                mc.SenseVoice.UseInverseTextNormalization = 1;
                break;
            case SherpaAsrKind.Whisper:
                mc.Whisper.Encoder = model.Encoder ?? model.Model;
                mc.Whisper.Decoder = model.Decoder ?? model.Model;
                break;
            default:
                mc.ZipformerCtc.Model = model.Model;
                mc.ModelType = "zipformer2_ctc";
                break;
        }

        return new OfflineRecognizer(config);
    }
    #endregion
}
