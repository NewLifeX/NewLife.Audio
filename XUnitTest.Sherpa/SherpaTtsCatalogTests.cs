using System;
using System.IO;
using NewLife.Audio.Tts.Sherpa;
using Xunit;

namespace XUnitTest.Sherpa;

/// <summary>模型目录扫描</summary>
public class SherpaTtsCatalogTests
{
    private static String CreateRoot() => Path.Combine(Path.GetTempPath(), "sherpa-test-" + Guid.NewGuid().ToString("N"));

    private static String CreateVits(String root, String name = "vits-piper-zh_CN-chaowen-medium", Boolean withLexicon = true)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "model.onnx"), new Byte[1024]);
        File.WriteAllText(Path.Combine(dir, "tokens.txt"), "a 0");
        File.WriteAllText(Path.Combine(dir, "number.fst"), "fst");
        Directory.CreateDirectory(Path.Combine(dir, "espeak-ng-data"));
        if (withLexicon) File.WriteAllText(Path.Combine(dir, "lexicon.txt"), "a a");

        return dir;
    }

    private static String CreateMatcha(String root, Boolean withVocoder = true)
    {
        var dir = Path.Combine(root, "matcha-icefall-zh-baker");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "model.onnx"), new Byte[2048]);
        File.WriteAllText(Path.Combine(dir, "tokens.txt"), "a 0");
        File.WriteAllText(Path.Combine(dir, "lexicon.txt"), "a a");
        if (withVocoder) File.WriteAllBytes(Path.Combine(root, "vocos-22khz-univ.onnx"), new Byte[512]);

        return dir;
    }

    private static String CreateKokoro(String root, Boolean withVoices = true)
    {
        var dir = Path.Combine(root, "kokoro-int8-multi-lang-v1_1");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "model.onnx"), new Byte[4096]);
        File.WriteAllText(Path.Combine(dir, "tokens.txt"), "a 0");
        if (withVoices) File.WriteAllBytes(Path.Combine(dir, "voices.bin"), new Byte[128]);

        return dir;
    }

    private static void Cleanup(String root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    [Fact(DisplayName = "sherpa：目录不存在时返回空列表")]
    public void Discover_MissingRoot_ReturnsEmpty()
    {
        Assert.Empty(SherpaTtsCatalog.Discover(Path.Combine(Path.GetTempPath(), "sherpa-missing-" + Guid.NewGuid().ToString("N"))));
        Assert.Empty(SherpaTtsCatalog.Discover(""));
    }

    [Fact(DisplayName = "sherpa：VITS 档案识别词典与 FST 前端")]
    public void Discover_Vits_ReadsFrontend()
    {
        var root = CreateRoot();
        try
        {
            CreateVits(root);

            var model = Assert.Single(SherpaTtsCatalog.Discover(root));

            Assert.Equal(SherpaTtsKind.Vits, model.Kind);
            Assert.True(model.CanLoad);
            Assert.Contains("lexicon.txt", model.Lexicon);
            Assert.Contains("number.fst", model.RuleFsts);
            Assert.NotNull(model.DataDir);
            Assert.True(model.HasRules);
            Assert.Equal("CC0", model.License);
            Assert.Contains("Vits", model.ToString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact(DisplayName = "sherpa：VITS 缺少文本前端时标记不可用")]
    public void Discover_VitsWithoutFrontend_MarksUnsupported()
    {
        var root = CreateRoot();
        try
        {
            var dir = Path.Combine(root, "vits-test");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "model.onnx"), new Byte[256]);

            var model = Assert.Single(SherpaTtsCatalog.Discover(root));

            Assert.False(model.CanLoad);
            Assert.Contains("lexicon", model.Unsupported);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact(DisplayName = "sherpa：Matcha 档案需要声码器")]
    public void Discover_Matcha_RequiresVocoder()
    {
        var root = CreateRoot();
        try
        {
            CreateMatcha(root, withVocoder: false);
            Assert.Empty(SherpaTtsCatalog.Discover(root));

            CreateMatcha(root, withVocoder: true);
            var model = Assert.Single(SherpaTtsCatalog.Discover(root));
            Assert.Equal(SherpaTtsKind.Matcha, model.Kind);
            Assert.Contains("vocos-22khz-univ.onnx", model.Vocoder);
            Assert.True(model.CanLoad);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact(DisplayName = "sherpa：Kokoro 档案需要音色库并默认中文女声")]
    public void Discover_Kokoro_RequiresVoices()
    {
        var root = CreateRoot();
        try
        {
            CreateKokoro(root, withVoices: false);
            Assert.Empty(SherpaTtsCatalog.Discover(root));

            CreateKokoro(root, withVoices: true);
            var model = Assert.Single(SherpaTtsCatalog.Discover(root));
            Assert.Equal(SherpaTtsKind.Kokoro, model.Kind);
            Assert.Contains("voices.bin", model.Voices);
            Assert.Equal(3, model.DefaultSid);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact(DisplayName = "sherpa：按别名与模糊名查找，未知名称返回空")]
    public void Find_AliasAndFuzzy()
    {
        var root = CreateRoot();
        try
        {
            CreateVits(root);
            CreateMatcha(root);
            var all = SherpaTtsCatalog.Discover(root);

            Assert.Equal("vits-piper-zh_CN-chaowen-medium", SherpaTtsCatalog.Find("chaowen", root, all).Name);
            Assert.Equal("matcha-icefall-zh-baker", SherpaTtsCatalog.Find("baker", root, all).Name);
            Assert.Equal("matcha-icefall-zh-baker", SherpaTtsCatalog.Find("zh-baker", root, all).Name);
            Assert.Null(SherpaTtsCatalog.Find("not-exists", root, all));
            Assert.Null(SherpaTtsCatalog.Find("", root, all));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact(DisplayName = "sherpa：同名多档优先 fp32")]
    public void Find_PrefersFp32()
    {
        var root = CreateRoot();
        try
        {
            CreateVits(root);
            CreateVits(root, "vits-piper-zh_CN-chaowen-medium-int8");
            var all = SherpaTtsCatalog.Discover(root);

            var model = SherpaTtsCatalog.Find("chaowen", root, all);

            Assert.DoesNotContain("int8", model.Name);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact(DisplayName = "sherpa：Kokoro 音色表可按编号取名称与语言")]
    public void KokoroVoices_Mapping()
    {
        Assert.Equal(103, KokoroVoices.Count);
        Assert.Equal("af_maple", KokoroVoices.Name(0));
        Assert.Equal("bf_vale", KokoroVoices.Name(2));
        Assert.Equal("zf_001", KokoroVoices.Name(3));
        Assert.True(KokoroVoices.IsEnglish(0));
        Assert.True(KokoroVoices.IsEnglish(2));
        Assert.False(KokoroVoices.IsEnglish(3));

        var voices = KokoroVoices.ToVoices();
        Assert.Equal(103, voices.Count);
        Assert.Equal("en-US", voices[0].Culture);
        Assert.Equal("en-GB", voices[2].Culture);
        Assert.Equal("zh-CN", voices[3].Culture);
        Assert.Equal("Female", voices[3].Gender);
        Assert.Equal("Male", voices[58].Gender);
        Assert.Equal("3", voices[3].Id);
        Assert.Equal("58", voices[58].Id);
    }
}
