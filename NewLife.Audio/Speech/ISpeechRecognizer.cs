using System;
using System.Collections.Generic;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Speech;

/// <summary>语音识别结果</summary>
public class SpeechRecognitionResult
{
    /// <summary>识别文本</summary>
    public String Text { get; set; }

    /// <summary>是否为最终结果（false 为部分结果，可能被后续结果替换）</summary>
    public Boolean IsFinal { get; set; }

    /// <summary>置信度 0~1，未知时为 0</summary>
    public Double Confidence { get; set; }

    /// <summary>该段语音在输入流中的起始时间</summary>
    public TimeSpan Start { get; set; }

    /// <summary>该段语音在输入流中的结束时间</summary>
    public TimeSpan End { get; set; }

    /// <summary>是否由端点检测判定语句结束</summary>
    public Boolean IsEndpoint { get; set; }
}

/// <summary>语音识别参数</summary>
public class SpeechRecognitionOptions
{
    /// <summary>识别语言（BCP-47，如 zh-CN），为空时使用引擎默认</summary>
    public String Language { get; set; }

    /// <summary>是否输出部分结果，默认 true</summary>
    public Boolean PartialResults { get; set; } = true;

    /// <summary>部分结果的间隔（毫秒），默认 500</summary>
    public Int32 PartialIntervalMs { get; set; } = 500;

    /// <summary>端点判定所需的静音时长（毫秒），默认 700</summary>
    public Int32 EndpointSilenceMs { get; set; } = 700;

    /// <summary>最短语句时长（毫秒），短于此长度的能量段被视为噪声丢弃，默认 200</summary>
    public Int32 MinUtteranceMs { get; set; } = 200;

    /// <summary>最长语句时长（毫秒），超过后强制切分，默认 20000</summary>
    public Int32 MaxUtteranceMs { get; set; } = 20000;

    /// <summary>语音判定能量阈值（相对满量程的比例），默认 0.01</summary>
    public Double EnergyThreshold { get; set; } = 0.01;
}

/// <summary>语音识别引擎（不自带端点检测的底层能力）</summary>
/// <remarks>
/// 适配层只负责"一段 PCM → 文本"，端点检测、部分结果与时间戳由 <see cref="SegmentedSpeechRecognizer"/> 统一处理，
/// 因此可复用于系统识别、云服务与离线包。
/// </remarks>
public interface ISpeechRecognitionEngine
{
    /// <summary>引擎是否可用（依赖缺失或平台不支持时为 false）</summary>
    Boolean IsAvailable { get; }

    /// <summary>识别一段完整语音</summary>
    /// <param name="pcm">16 位小端交错 PCM</param>
    /// <param name="format">音频格式</param>
    /// <param name="options">识别参数</param>
    /// <returns>识别文本与置信度；无法识别时文本为空</returns>
    SpeechRecognitionResult Transcribe(Byte[] pcm, AudioFormat format, SpeechRecognitionOptions options);
}

/// <summary>语音识别器</summary>
/// <remarks>流式 PCM 输入，中间/最终结果输出，支持取消；可与 VOP 前处理（降噪、AEC、AGC）串联。</remarks>
public interface ISpeechRecognizer : IDisposable
{
    /// <summary>输入音频格式</summary>
    AudioFormat Format { get; }

    /// <summary>底层引擎是否可用</summary>
    Boolean IsAvailable { get; }

    /// <summary>识别参数</summary>
    SpeechRecognitionOptions Options { get; }

    /// <summary>开始识别（重新开始会清空内部缓冲）</summary>
    /// <param name="options">识别参数，为空时使用默认值</param>
    void Start(SpeechRecognitionOptions options = null);

    /// <summary>送入一段 PCM 并取回识别结果</summary>
    /// <param name="pcm">16 位小端交错 PCM，长度应为帧长整数倍</param>
    /// <returns>本次产出的结果（部分与最终，可能为空列表）</returns>
    IList<SpeechRecognitionResult> Accept(Byte[] pcm);

    /// <summary>结束输入并冲刷最后一段语音</summary>
    /// <returns>剩余的最终结果</returns>
    IList<SpeechRecognitionResult> Finish();

    /// <summary>取消本次识别，丢弃未完成的语音段</summary>
    void Cancel();
}
