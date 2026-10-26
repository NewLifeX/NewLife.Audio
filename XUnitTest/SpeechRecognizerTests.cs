using System;
using System.Collections.Generic;
using NewLife;
using NewLife.Audio;
using NewLife.Audio.DSP;
using NewLife.Audio.Speech;
using Xunit;

namespace XUnitTest;

/// <summary>流式语音识别测试：端点切句、部分结果、时间戳、取消与冲刷</summary>
public class SpeechRecognizerTests
{
    private static readonly AudioFormat Format = new() { SampleRate = 16000, Channels = 1, BitsPerSample = 16, SamplesPerFrame = 160 };

    [Fact(DisplayName = "端点检测把两段语音切成两句")]
    public void Segmenter_SplitsTwoUtterances()
    {
        var options = new SpeechRecognitionOptions { EndpointSilenceMs = 300, MinUtteranceMs = 100 };
        var segmenter = new SpeechSegmenter(Format, options);

        var pcm = BuildPattern(0.4, 0.4, 0.6, 0.4);
        var segments = segmenter.Accept(pcm);
        var tail = segmenter.Flush();

        var all = new List<(Byte[] Pcm, TimeSpan Start, TimeSpan End, Boolean IsEndpoint)>(segments);
        if (tail.Pcm != null) all.Add(tail);

        Assert.Equal(2, all.Count);
        Assert.True(all[0].IsEndpoint);
        Assert.True(all[1].Start > all[0].End);
    }

    [Fact(DisplayName = "过短的语音段被当作噪声丢弃")]
    public void Segmenter_DropsShortSegment()
    {
        var options = new SpeechRecognitionOptions { EndpointSilenceMs = 200, MinUtteranceMs = 300 };
        var segmenter = new SpeechSegmenter(Format, options);

        var pcm = BuildPattern(0.05, 0.4, 0.05, 0.4);
        var segments = segmenter.Accept(pcm);
        var tail = segmenter.Flush();

        Assert.Empty(segments);
        Assert.Null(tail.Pcm);
    }

    [Fact(DisplayName = "流式识别输出最终结果与时间戳")]
    public void Recognizer_ProducesFinalResults()
    {
        var engine = new FakeEngine();
        using var recognizer = new SegmentedSpeechRecognizer(engine, Format);
        recognizer.Start(new SpeechRecognitionOptions { EndpointSilenceMs = 300, MinUtteranceMs = 100, PartialResults = false });

        var pcm = BuildPattern(0.4, 0.4, 0.6, 0.4);
        var results = new List<SpeechRecognitionResult>();
        results.AddRange(recognizer.Accept(pcm));
        results.AddRange(recognizer.Finish());

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.IsFinal));
        Assert.All(results, r => Assert.False(r.Text.IsNullOrEmpty()));
        Assert.True(results[0].End > results[0].Start);
        Assert.True(engine.Calls >= 2);
    }

    [Fact(DisplayName = "流式识别按间隔输出部分结果")]
    public void Recognizer_ProducesPartialResults()
    {
        var engine = new FakeEngine();
        using var recognizer = new SegmentedSpeechRecognizer(engine, Format);
        recognizer.Start(new SpeechRecognitionOptions
        {
            EndpointSilenceMs = 1000,
            MinUtteranceMs = 100,
            PartialResults = true,
            PartialIntervalMs = 100,
        });

        var results = recognizer.Accept(BuildPattern(1));
        recognizer.Finish();

        Assert.Contains(results, r => !r.IsFinal);
        Assert.NotNull(engine.LastPcm);
    }

    [Fact(DisplayName = "取消后不再产出结果")]
    public void Recognizer_Cancel_StopsResults()
    {
        var engine = new FakeEngine();
        using var recognizer = new SegmentedSpeechRecognizer(engine, Format);
        recognizer.Start();
        recognizer.Accept(BuildPattern(0.3));
        recognizer.Cancel();

        Assert.False(recognizer.IsRunning);
        Assert.Empty(recognizer.Accept(BuildPattern(0.3)));
        Assert.Empty(recognizer.Finish());
    }

    [Fact(DisplayName = "引擎不可用时仍给出端点与时间戳")]
    public void Recognizer_UnavailableEngine_StillSegments()
    {
        var engine = new FakeEngine { IsAvailable = false };
        using var recognizer = new SegmentedSpeechRecognizer(engine, Format);
        recognizer.Start(new SpeechRecognitionOptions { EndpointSilenceMs = 300, MinUtteranceMs = 100, PartialResults = false });

        var results = new List<SpeechRecognitionResult>();
        results.AddRange(recognizer.Accept(BuildPattern(0.4, 0.4, 0.6, 0.4)));
        results.AddRange(recognizer.Finish());

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.IsFinal));
        Assert.All(results, r => Assert.Null(r.Text));
        Assert.Equal(0, engine.Calls);
    }

    #region 辅助
    /// <summary>按「语音/静音」交替时长拼出测试信号</summary>
    private static Byte[] BuildPattern(params Double[] seconds)
    {
        var rate = Format.SampleRate;
        var total = 0;
        foreach (var item in seconds) total += (Int32)(rate * item);

        var data = new Byte[total * 2];
        var random = new Random(2024);
        var index = 0;
        for (var k = 0; k < seconds.Length; k++)
        {
            var speech = k % 2 == 0;
            var count = (Int32)(rate * seconds[k]);
            for (var i = 0; i < count && index < total; i++, index++)
            {
                var v = speech ? (Int16)((random.NextDouble() * 2 - 1) * 0.5 * 32000) : (Int16)0;
                data[index * 2] = (Byte)(v & 0xFF);
                data[index * 2 + 1] = (Byte)((v >> 8) & 0xFF);
            }
        }

        return data;
    }

    private sealed class FakeEngine : ISpeechRecognitionEngine
    {
        public Boolean IsAvailable { get; set; } = true;

        public Int32 Calls { get; private set; }

        public Byte[] LastPcm { get; private set; }

        public SpeechRecognitionResult Transcribe(Byte[] pcm, AudioFormat format, SpeechRecognitionOptions options)
        {
            Calls++;
            LastPcm = pcm;

            return new SpeechRecognitionResult { Text = "文字" + Calls, Confidence = 0.9 };
        }
    }
    #endregion
}


