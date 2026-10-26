using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NewLife.Audio.Speech.Sherpa;

/// <summary>sherpa-onnx 识别模型目录扫描</summary>
/// <remarks>
/// 把官方 release 包解压出来的目录翻译成 <see cref="SherpaAsrModel"/>，按目录内容判断架构，不硬编码文件名。
/// 目录不存在或缺少关键文件时返回空结果，不抛异常。
/// </remarks>
/// <example>
/// <code>
/// var models = SherpaAsrCatalog.Discover(SherpaAsrCatalog.DefaultRoot);
/// using var engine = new SherpaSpeechRecognitionEngine(models[0]);
/// var result = engine.Transcribe(pcm, format, new SpeechRecognitionOptions { Language = "zh-CN" });
/// </code>
/// </example>
public static class SherpaAsrCatalog
{
    private static readonly Dictionary<String, String> _alias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sensevoice"] = "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17",
        ["paraformer"] = "sherpa-onnx-paraformer-zh-2024-03-09",
        ["whisper-tiny"] = "sherpa-onnx-whisper-tiny",
        ["zipformer-zh"] = "sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23",
    };

    /// <summary>默认模型根目录。可用环境变量 ASR_MODELS 覆盖</summary>
    public static String DefaultRoot
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("ASR_MODELS");
            if (!dir.IsNullOrEmpty()) return dir;

            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "newlife", "asr-models");

            return "/root/audio/asr-models";
        }
    }

    /// <summary>扫描模型根目录下所有可用档案</summary>
    /// <param name="root">模型根目录</param>
    /// <returns>按目录名排序的档案列表；目录不存在时返回空列表</returns>
    public static IReadOnlyList<SherpaAsrModel> Discover(String root)
    {
        var list = new List<SherpaAsrModel>();
        if (root.IsNullOrEmpty() || !Directory.Exists(root)) return list;

        foreach (var dir in Directory.GetDirectories(root).OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
        {
            SherpaAsrModel model = null;
            try
            {
                model = Inspect(dir);
            }
            catch
            {
                // 单个目录无权限或结构异常时跳过
            }
            if (model != null) list.Add(model);
        }

        return list;
    }

    /// <summary>按名称查找模型；名称可为目录名或别名</summary>
    /// <param name="name">模型名或别名</param>
    /// <param name="root">模型根目录，为空时用 <see cref="DefaultRoot"/></param>
    /// <returns>模型档案；未找到返回 null</returns>
    public static SherpaAsrModel Find(String name, String root = null)
    {
        if (name.IsNullOrEmpty()) return null;

        root ??= DefaultRoot;
        if (_alias.TryGetValue(name, out var dirName)) name = dirName;

        var dir = Path.Combine(root, name);
        var model = Inspect(dir);
        if (model != null) return model;

        // 回退到模糊匹配：目录名包含关键字
        foreach (var item in Discover(root))
        {
            if (item.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) return item;
        }

        return null;
    }

    /// <summary>检查单个目录并生成档案</summary>
    /// <param name="dir">模型目录</param>
    /// <returns>模型档案；目录不合格时返回 null</returns>
    public static SherpaAsrModel Inspect(String dir)
    {
        if (dir.IsNullOrEmpty() || !Directory.Exists(dir)) return null;

        String tokens;
        List<String> onnx;
        try
        {
            tokens = Directory.GetFiles(dir, "tokens.txt", SearchOption.AllDirectories).FirstOrDefault();
            if (tokens.IsNullOrEmpty()) return null;

            onnx = Directory.GetFiles(dir, "*.onnx", SearchOption.AllDirectories)
                .Where(e => !e.Contains("int8", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => new FileInfo(e).Length)
                .ToList();
            if (onnx.Count == 0)
            {
                // 退回 int8 量化模型
                onnx = Directory.GetFiles(dir, "*.onnx", SearchOption.AllDirectories)
                    .OrderByDescending(e => new FileInfo(e).Length)
                    .ToList();
            }
        }
        catch
        {
            return null;
        }

        if (onnx.Count == 0) return null;

        var model = new SherpaAsrModel
        {
            Name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            Dir = dir,
            Tokens = tokens,
        };

        var encoder = onnx.FirstOrDefault(e => Path.GetFileName(e).StartsWith("encoder", StringComparison.OrdinalIgnoreCase));
        var decoder = onnx.FirstOrDefault(e => Path.GetFileName(e).StartsWith("decoder", StringComparison.OrdinalIgnoreCase));
        var joiner = onnx.FirstOrDefault(e => Path.GetFileName(e).StartsWith("joiner", StringComparison.OrdinalIgnoreCase));

        if (!encoder.IsNullOrEmpty() && !joiner.IsNullOrEmpty())
        {
            model.Kind = SherpaAsrKind.Transducer;
            model.Encoder = encoder;
            model.Decoder = decoder;
            model.Joiner = joiner;
        }
        else if (model.Name.Contains("paraformer", StringComparison.OrdinalIgnoreCase))
        {
            model.Kind = SherpaAsrKind.Paraformer;
            model.Model = onnx[0];
        }
        else if (model.Name.Contains("sense", StringComparison.OrdinalIgnoreCase))
        {
            model.Kind = SherpaAsrKind.SenseVoice;
            model.Model = onnx[0];
        }
        else if (model.Name.Contains("whisper", StringComparison.OrdinalIgnoreCase))
        {
            model.Kind = SherpaAsrKind.Whisper;
            // Whisper 需要 encoder/decoder 两个文件，按大小区分
            var sorted = onnx.OrderByDescending(e => new FileInfo(e).Length).ToList();
            model.Encoder = sorted.FirstOrDefault(e => Path.GetFileName(e).Contains("encoder", StringComparison.OrdinalIgnoreCase)) ?? sorted[0];
            model.Decoder = sorted.FirstOrDefault(e => Path.GetFileName(e).Contains("decoder", StringComparison.OrdinalIgnoreCase)) ?? sorted[^1];
            model.Model = model.Encoder;
        }
        else
        {
            model.Kind = SherpaAsrKind.Ctc;
            model.Model = onnx[0];
        }

        return model;
    }
}
