using System;
using System.Collections.Generic;
using System.IO;
using NewLife;
using NewLife.Audio;
using NewLife.Audio.DSP;
using NewLife.Audio.Speech;
using NewLife.Audio.Speech.Sherpa;
using Xunit;

namespace XUnitTest.Sherpa;

/// <summary>sherpa-onnx 离线识别适配测试：目录扫描与不可用分支</summary>
public class SherpaAsrTests
{
    private static readonly AudioFormat Format = new() { SampleRate = 16000, Channels = 1, BitsPerSample = 16 };

    [Fact(DisplayName = "识别模型扫描：目录不存在返回空")]
    public void Discover_MissingRoot_ReturnsEmpty()
    {
        Assert.Empty(SherpaAsrCatalog.Discover(Path.Combine(Path.GetTempPath(), "no-such-asr-" + Guid.NewGuid().ToString("N"))));
        Assert.Null(SherpaAsrCatalog.Find("whatever", Path.GetTempPath()));
    }

    [Fact(DisplayName = "识别模型扫描：按目录内容识别 Transducer 架构")]
    public void Inspect_DetectsTransducer()
    {
        var dir = CreateFakeModel("sherpa-onnx-streaming-zipformer-test", ("encoder.onnx", 4096), ("decoder.onnx", 2048), ("joiner.onnx", 1024), ("tokens.txt", 512));
        try
        {
            var model = SherpaAsrCatalog.Inspect(dir);

            Assert.NotNull(model);
            Assert.Equal(SherpaAsrKind.Transducer, model.Kind);
            Assert.True(File.Exists(model.Encoder));
            Assert.True(File.Exists(model.Decoder));
            Assert.True(File.Exists(model.Joiner));
            Assert.Equal("tokens.txt", Path.GetFileName(model.Tokens));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact(DisplayName = "识别模型扫描：按目录名识别 Paraformer/SenseVoice/Whisper")]
    public void Inspect_DetectsKinds()
    {
        var para = CreateFakeModel("sherpa-onnx-paraformer-zh-test", ("model.onnx", 4096), ("tokens.txt", 512));
        var sense = CreateFakeModel("sherpa-onnx-sense-voice-test", ("model.int8.onnx", 4096), ("tokens.txt", 512));
        var whisper = CreateFakeModel("sherpa-onnx-whisper-tiny", ("tiny-encoder.onnx", 8192), ("tiny-decoder.onnx", 4096), ("tokens.txt", 512));
        try
        {
            Assert.Equal(SherpaAsrKind.Paraformer, SherpaAsrCatalog.Inspect(para).Kind);
            Assert.Equal(SherpaAsrKind.SenseVoice, SherpaAsrCatalog.Inspect(sense).Kind);
            var w = SherpaAsrCatalog.Inspect(whisper);
            Assert.Equal(SherpaAsrKind.Whisper, w.Kind);
            Assert.Contains("encoder", Path.GetFileName(w.Encoder));
            Assert.Contains("decoder", Path.GetFileName(w.Decoder));
        }
        finally
        {
            TryDelete(para);
            TryDelete(sense);
            TryDelete(whisper);
        }
    }

    [Fact(DisplayName = "识别模型扫描：缺少 tokens 时判为不可用")]
    public void Inspect_WithoutTokens_ReturnsNull()
    {
        var dir = CreateFakeModel("broken-model", ("model.onnx", 1024));
        try
        {
            Assert.Null(SherpaAsrCatalog.Inspect(dir));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact(DisplayName = "识别引擎：模型为空时不可用且返回空结果")]
    public void Engine_NullModel_Graceful()
    {
        using var engine = new SherpaSpeechRecognitionEngine((SherpaAsrModel)null);

        Assert.False(engine.IsAvailable);
        Assert.False(engine.LoadError.IsNullOrEmpty());

        var result = engine.Transcribe(new Byte[320], Format, new SpeechRecognitionOptions());
        Assert.NotNull(result);
        Assert.True(result.Text.IsNullOrEmpty());
    }

    [Fact(DisplayName = "识别引擎：可接入流式识别管线")]
    public void Engine_WorksWithSegmentedRecognizer()
    {
        using var engine = new SherpaSpeechRecognitionEngine((SherpaAsrModel)null);
        using var recognizer = new SegmentedSpeechRecognizer(engine, Format);
        recognizer.Start(new SpeechRecognitionOptions { EndpointSilenceMs = 200, MinUtteranceMs = 50, PartialResults = false });

        var results = new List<SpeechRecognitionResult>(recognizer.Accept(BuildPattern(0.2, 0.3)));
        results.AddRange(recognizer.Finish());

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.True(r.IsFinal));
    }

    #region 辅助
    private static String CreateFakeModel(String name, params (String Name, Int32 Size)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "asr-" + Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(dir);
        foreach (var (file, size) in files)
        {
            File.WriteAllBytes(Path.Combine(dir, file), new Byte[size]);
        }

        return dir;
    }

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

    private static void TryDelete(String dir)
    {
        try
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent != null && Directory.Exists(parent)) Directory.Delete(parent, true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }
    #endregion
}

