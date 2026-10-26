using System;
using System.Collections.Generic;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Speech;

/// <summary>带端点检测的流式识别器</summary>
/// <remarks>
/// 把连续 PCM 交给 <see cref="SpeechSegmenter"/> 切句，每句调用底层 <see cref="ISpeechRecognitionEngine"/> 识别；
/// 期间按 <see cref="SpeechRecognitionOptions.PartialIntervalMs"/> 输出部分结果，句尾给出最终结果与时间戳。
/// 未接引擎时（引擎不可用）仍可用于端点检测与时间戳，只是文本为空。
/// </remarks>
/// <example>
/// <code>
/// using var recognizer = new SegmentedSpeechRecognizer(new MyEngine(), format);
/// recognizer.Start(new SpeechRecognitionOptions { Language = "zh-CN" });
/// foreach (var result in recognizer.Accept(pcm)) Console.WriteLine($"{result.Start}-{result.End} {(result.IsFinal ? "最终" : "部分")}: {result.Text}");
/// foreach (var result in recognizer.Finish()) Console.WriteLine(result.Text);
/// </code>
/// </example>
public class SegmentedSpeechRecognizer : ISpeechRecognizer
{
    #region 属性
    private readonly ISpeechRecognitionEngine _engine;
    private readonly AudioFormat _format;
    private readonly List<Byte> _pending = [];
    private SpeechSegmenter _segmenter;

    /// <summary>输入音频格式</summary>
    public AudioFormat Format => _format;

    /// <summary>底层引擎是否可用</summary>
    public Boolean IsAvailable => _engine is { IsAvailable: true };

    /// <summary>识别参数</summary>
    public SpeechRecognitionOptions Options { get; private set; } = new();

    /// <summary>当前是否处于识别会话中</summary>
    public Boolean IsRunning { get; private set; }
    #endregion

    #region 构造
    /// <summary>构造流式识别器</summary>
    /// <param name="engine">底层识别引擎，可为 null（仅做端点检测）</param>
    /// <param name="format">输入音频格式</param>
    public SegmentedSpeechRecognizer(ISpeechRecognitionEngine engine, AudioFormat format)
    {
        _engine = engine;
        _format = format ?? throw new ArgumentNullException(nameof(format));
    }
    #endregion

    #region 方法
    /// <summary>开始识别</summary>
    /// <param name="options">识别参数，为空时使用默认值</param>
    public void Start(SpeechRecognitionOptions options = null)
    {
        Options = options ?? new SpeechRecognitionOptions();
        _pending.Clear();
        IsRunning = true;
    }

    /// <summary>送入一段 PCM 并取回识别结果</summary>
    /// <param name="pcm">16 位小端交错 PCM</param>
    /// <returns>本次产出的部分/最终结果</returns>
    public IList<SpeechRecognitionResult> Accept(Byte[] pcm)
    {
        var results = new List<SpeechRecognitionResult>();
        if (!IsRunning || pcm == null || pcm.Length == 0) return results;

        var frameSamples = _format.SamplesPerFrame > 0 ? _format.SamplesPerFrame : 160;
        var frameBytes = frameSamples * _format.Channels * 2;
        if (frameBytes <= 0) frameBytes = 320;

        _pending.AddRange(pcm);

        // 按帧喂给分段器，凑满部分结果间隔时产出部分结果
        var segmenter = GetSegmenter();
        var intervalFrames = Math.Max(1, Options.PartialIntervalMs * Math.Max(1, _format.SampleRate) / 1000 / frameSamples);
        var frames = 0;

        while (_pending.Count >= frameBytes)
        {
            var frame = new Byte[frameBytes];
            _pending.CopyTo(0, frame, 0, frameBytes);
            _pending.RemoveRange(0, frameBytes);
            frames++;

            foreach (var segment in segmenter.Accept(frame))
            {
                var final = Recognize(segment.Pcm, segment.Start, segment.End, true);
                final.IsEndpoint = segment.IsEndpoint;
                if (final != null) results.Add(final);
            }

            if (Options.PartialResults && frames % intervalFrames == 0)
            {
                var partial = BuildPartial(segmenter);
                if (partial != null) results.Add(partial);
            }
        }

        return results;
    }

    /// <summary>结束输入并冲刷最后一段语音</summary>
    /// <returns>剩余的最终结果</returns>
    public IList<SpeechRecognitionResult> Finish()
    {
        var results = new List<SpeechRecognitionResult>();
        if (!IsRunning) return results;

        var segmenter = GetSegmenter();

        // 把不足一帧的尾巴补齐后再冲刷
        var frameSamples = _format.SamplesPerFrame > 0 ? _format.SamplesPerFrame : 160;
        var frameBytes = frameSamples * _format.Channels * 2;
        if (frameBytes <= 0) frameBytes = 320;
        if (_pending.Count > 0)
        {
            var tail = _pending.ToArray();
            Array.Resize(ref tail, frameBytes);
            foreach (var segment in segmenter.Accept(tail))
            {
                var final = Recognize(segment.Pcm, segment.Start, segment.End, true);
                final.IsEndpoint = segment.IsEndpoint;
                if (final != null) results.Add(final);
            }
            _pending.Clear();
        }

        var last = segmenter.Flush();
        if (last.Pcm != null)
        {
            var final = Recognize(last.Pcm, last.Start, last.End, true);
            if (final != null) results.Add(final);
        }

        IsRunning = false;
        _segmenter = null;

        return results;
    }

    /// <summary>取消识别，丢弃未完成的语音段</summary>
    public void Cancel()
    {
        _pending.Clear();
        _segmenter = null;
        IsRunning = false;
    }

    /// <summary>释放资源</summary>
    public void Dispose() => Cancel();

    private SpeechSegmenter GetSegmenter() => _segmenter ??= new SpeechSegmenter(_format, Options);

    private SpeechRecognitionResult Recognize(Byte[] pcm, TimeSpan start, TimeSpan end, Boolean isFinal)
    {
        if (!IsAvailable) return new SpeechRecognitionResult { Text = null, IsFinal = isFinal, Start = start, End = end };

        var result = _engine.Transcribe(pcm, _format, Options);
        if (result == null) return new SpeechRecognitionResult { IsFinal = isFinal, Start = start, End = end };

        result.IsFinal = isFinal;
        result.Start = start;
        result.End = end;

        return result;
    }

    private SpeechRecognitionResult BuildPartial(SpeechSegmenter segmenter)
    {
        var buffered = segmenter.BufferedPcm;
        if (buffered == null || buffered.Length == 0) return null;

        var end = TimeSpan.FromSeconds(segmenter.TotalSamples / Math.Max(1, _format.SampleRate));
        var start = end - TimeSpan.FromSeconds((Double)buffered.Length / 2 / Math.Max(1, _format.Channels) / Math.Max(1, _format.SampleRate));

        return Recognize(buffered, start, end, false);
    }
    #endregion
}
