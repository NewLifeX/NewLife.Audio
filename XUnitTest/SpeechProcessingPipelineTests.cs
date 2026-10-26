using System;
using System.Collections.Generic;
using NewLife;
using NewLife.Audio.DSP;
using NewLife.Audio.Speech;
using Xunit;

namespace XUnitTest;

/// <summary>语音前处理工程化测试：帧对齐、指标、双讲与失锁恢复</summary>
public class SpeechProcessingPipelineTests
{
    private static readonly AudioFormat Format = new() { SampleRate = 16000, Channels = 1, BitsPerSample = 16 };

    [Fact(DisplayName = "前处理按10ms帧对齐切分输入")]
    public void Push_AlignsTo10msFrames()
    {
        using var pipeline = new SpeechProcessingPipeline(Format);

        Assert.Equal(160, pipeline.FrameSamples);
        Assert.Equal(320, pipeline.FrameBytes);

        var frames = pipeline.Push(new Byte[1000]);

        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.Equal(320, f.Length));

        var tail = pipeline.Flush();
        Assert.Single(tail);
        Assert.Equal(320, tail[0].Length);
    }

    [Fact(DisplayName = "前处理统计帧数与能量指标")]
    public void Push_UpdatesMetrics()
    {
        using var pipeline = new SpeechProcessingPipeline(Format, new SpeechPreprocessOptions
        {
            EnableAec = false,
            EnableNoiseSuppression = false,
            EnableAgc = false,
        });

        var frames = pipeline.Push(BuildSine(0.1, 440, 0.5));

        Assert.Equal(10, frames.Count);
        Assert.Equal(10, pipeline.Metrics.Frames);
        Assert.True(pipeline.Metrics.NearEndLevelDb > -20, $"近端电平异常：{pipeline.Metrics.NearEndLevelDb:F1} dB");
        Assert.True(pipeline.Metrics.OutputLevelDb > -20);
    }

    [Fact(DisplayName = "远端回声被抑制：ERLE 为正且输出能量下降")]
    public void Aec_SuppressesEcho()
    {
        var far = BuildSine(0.5, 300, 0.5);

        // 近端 = 远端回声（幅度 0.3，无近端语音）
        var near = new Byte[far.Length];
        for (var i = 0; i < far.Length; i += 2)
        {
            var v = (Int16)(far[i] | far[i + 1] << 8);
            var echo = (Int16)(v * 0.6);
            near[i] = (Byte)(echo & 0xFF);
            near[i + 1] = (Byte)((echo >> 8) & 0xFF);
        }

        using var pipeline = new SpeechProcessingPipeline(Format, new SpeechPreprocessOptions
        {
            EnableNoiseSuppression = false,
            EnableAgc = false,
            FilterLength = 64,
            StepSize = 0.5f,
        });

        var farOffset = 0;
        var nearOffset = 0;
        var inputLevel = 0.0;
        var outputLevel = 0.0;
        while (nearOffset < near.Length)
        {
            var nearChunk = new Byte[320];
            Array.Copy(near, nearOffset, nearChunk, 0, 320);
            var farChunk = new Byte[320];
            Array.Copy(far, farOffset, farChunk, 0, 320);

            pipeline.PushFarEnd(farChunk);
            var processed = pipeline.Push(nearChunk);

            inputLevel += Level(nearChunk);
            if (processed.Count > 0) outputLevel += Level(processed[0]);

            nearOffset += 320;
            farOffset += 320;
        }

        var frames = pipeline.Metrics.Frames;
        Assert.Equal(50, frames);
        Assert.True(outputLevel / frames < inputLevel / frames - 3, $"回声未被抑制：输入 {inputLevel / frames:F1} dB，输出 {outputLevel / frames:F1} dB");
        Assert.True(pipeline.Metrics.ErleDb > 0, $"ERLE 应为正：{pipeline.Metrics.ErleDb:F1} dB");
    }

    [Fact(DisplayName = "双讲帧被识别并计入占比")]
    public void DoubleTalk_Detected()
    {
        var far = BuildSine(0.3, 300, 0.2);
        var near = BuildSine(0.3, 900, 0.8);

        using var pipeline = new SpeechProcessingPipeline(Format, new SpeechPreprocessOptions
        {
            EnableNoiseSuppression = false,
            EnableAgc = false,
        });

        for (var offset = 0; offset < near.Length; offset += 320)
        {
            pipeline.PushFarEnd(far.AsSpan(offset, 320).ToArray());
            pipeline.Push(near.AsSpan(offset, 320).ToArray());
        }

        Assert.True(pipeline.Metrics.DoubleTalkFrames > 0);
        Assert.InRange(pipeline.Metrics.DoubleTalkRatio, 0.01, 1.0);
    }

    [Fact(DisplayName = "远端静音时不触发失锁重置")]
    public void Silence_NoReset()
    {
        using var pipeline = new SpeechProcessingPipeline(Format);

        for (var i = 0; i < 40; i++) pipeline.Push(new Byte[320]);

        Assert.Equal(0, pipeline.Metrics.ResetCount);
    }

    [Fact(DisplayName = "重置后指标与缓冲归零")]
    public void Reset_ClearsState()
    {
        using var pipeline = new SpeechProcessingPipeline(Format);
        pipeline.Push(BuildSine(0.1, 440, 0.5));
        pipeline.PushFarEnd(BuildSine(0.1, 440, 0.5));

        pipeline.Reset();

        Assert.Empty(pipeline.Flush());
        Assert.True(Math.Abs(pipeline.Metrics.ErleDb) < 0.001);
    }

    #region 辅助
    private static Byte[] BuildSine(Double seconds, Double frequency, Double amplitude)
    {
        var samples = (Int32)(Format.SampleRate * seconds);
        var data = new Byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (Int16)(Math.Sin(2 * Math.PI * frequency * i / Format.SampleRate) * amplitude * 32000);
            data[i * 2] = (Byte)(v & 0xFF);
            data[i * 2 + 1] = (Byte)((v >> 8) & 0xFF);
        }

        return data;
    }

    private static Double Level(Byte[] pcm)
    {
        if (pcm == null || pcm.Length < 2) return -120;

        var sum = 0.0;
        var count = pcm.Length / 2;
        for (var i = 0; i < count; i++)
        {
            var v = (Int16)(pcm[i * 2] | pcm[i * 2 + 1] << 8) / 32768.0;
            sum += v * v;
        }
        var rms = Math.Sqrt(sum / count);

        return rms > 0 ? 20 * Math.Log10(rms) : -120;
    }
    #endregion
}
