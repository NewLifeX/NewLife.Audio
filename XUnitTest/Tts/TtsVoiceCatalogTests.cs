using System;
using System.Collections.Generic;
using System.Linq;
using NewLife.Audio.Tts;
using Xunit;

namespace XUnitTest.Tts;

/// <summary>音色目录与多音色管理</summary>
public class TtsVoiceCatalogTests
{
    private static TtsVoice CreateVoice(String name, String culture, String gender = null, String id = null, Boolean isDefault = false, params TtsScenario[] scenarios) => new()
    {
        Name = name,
        Id = id,
        Culture = culture,
        Gender = gender,
        IsDefault = isDefault,
        Scenarios = scenarios,
    };

    [Fact(DisplayName = "未指定标识时回落到音色名称")]
    public void Voice_Id_FallsBackToName()
    {
        var voice = new TtsVoice { Name = "Microsoft Huihui Desktop" };

        Assert.Equal("Microsoft Huihui Desktop", voice.Id);

        voice.Id = "101001";
        Assert.Equal("101001", voice.Id);
    }

    [Fact(DisplayName = "未声明场景的音色适用于任意场景")]
    public void Voice_Matches_EmptyScenariosMeansGeneral()
    {
        var general = new TtsVoiceEntry { Voice = new TtsVoice { Name = "v" } };
        var narration = new TtsVoiceEntry { Voice = new TtsVoice { Name = "v", Scenarios = [TtsScenario.Narration] } };

        Assert.True(general.Matches(TtsScenario.Alert));
        Assert.True(general.Matches(TtsScenario.General));
        Assert.True(narration.Matches(TtsScenario.Narration));
        Assert.True(narration.Matches(TtsScenario.General));
        Assert.False(narration.Matches(TtsScenario.Alert));
    }

    [Fact(DisplayName = "登记引擎后条目带上引擎名称与实例")]
    public void Catalog_AddEngine_MapsVoices()
    {
        var engine = new FakeTtsEngine("fake")
        {
            Voices = new List<TtsVoice> { CreateVoice("v1", "zh-CN"), CreateVoice("v2", "en-US") },
        };

        var catalog = new TtsVoiceCatalog(engine);

        Assert.Equal(2, catalog.Count);
        Assert.All(catalog.Entries, e => Assert.Equal("fake", e.EngineName));
        Assert.All(catalog.Entries, e => Assert.Same(engine, e.Engine));
        Assert.Equal("v1", catalog.Entries[0].Name);
    }

    [Fact(DisplayName = "引擎枚举音色失败时目录保持为空且不抛异常")]
    public void Catalog_AddEngine_SwallowsEnumerationFailure()
    {
        var engine = new FakeTtsEngine("broken") { VoicesThrow = true };

        var catalog = new TtsVoiceCatalog(engine);

        Assert.Equal(0, catalog.Count);
        Assert.Empty(catalog.Entries);
    }

    [Fact(DisplayName = "登记空引擎或空音色不产生条目")]
    public void Catalog_Add_IgnoresNulls()
    {
        var catalog = new TtsVoiceCatalog();

        catalog.Add((ITtsEngine)null);
        catalog.Add("cloud", (TtsVoice[])null);
        catalog.Add("cloud", null, CreateVoice("v1", "zh-CN"));
        catalog.Add((TtsVoiceEntry)null);
        catalog.Add(new TtsVoiceEntry());

        Assert.Equal(1, catalog.Count);
        Assert.Equal("cloud", catalog.Entries[0].EngineName);
        Assert.Null(catalog.Entries[0].Engine);

        catalog.Clear();
        Assert.Equal(0, catalog.Count);
    }

    [Fact(DisplayName = "按语言与场景挑选音色")]
    public void Select_ExactLanguageAndScenario()
    {
        var catalog = new TtsVoiceCatalog();
        catalog.Add("engine", CreateVoice("narration", "zh-CN", "Female", null, false, TtsScenario.Narration), CreateVoice("alert", "zh-CN", "Female", null, false, TtsScenario.Alert));

        var entry = catalog.Select("zh-CN", TtsScenario.Alert);

        Assert.NotNull(entry);
        Assert.Equal("alert", entry.Name);
    }

    [Fact(DisplayName = "同语言无场景匹配时退化为同语言任意场景")]
    public void Select_FallsBackToLanguageAnyScenario()
    {
        var catalog = new TtsVoiceCatalog();
        catalog.Add("engine", CreateVoice("narration", "zh-CN", "Female", null, false, TtsScenario.Narration));

        var entry = catalog.Select("zh-CN", TtsScenario.Alert);

        Assert.NotNull(entry);
        Assert.Equal("narration", entry.Name);
    }

    [Fact(DisplayName = "请求语种可命中区域音色，请求区域也可命中语种音色")]
    public void Select_PrimaryLanguageFallback()
    {
        var catalog = new TtsVoiceCatalog();
        catalog.Add("engine", CreateVoice("zh", "zh-CN"), CreateVoice("hk", "zh-HK"));
        catalog.Add("engine2", CreateVoice("english", "en"));

        Assert.Equal("zh", catalog.Select("zh").Name);
        Assert.Equal("zh", catalog.Select("zh-MO").Name);
        Assert.Equal("english", catalog.Select("en-US").Name);
    }

    [Fact(DisplayName = "语言不匹配时返回空而不猜一个音色")]
    public void Select_ReturnsNull_WhenNoLanguageMatch()
    {
        var catalog = new TtsVoiceCatalog();
        catalog.Add("engine", CreateVoice("zh", "zh-CN"));

        Assert.Null(catalog.Select("ja-JP"));
        Assert.Null(new TtsVoiceCatalog().Select("zh-CN"));
    }

    [Fact(DisplayName = "未指定语言时挑选默认音色")]
    public void Select_EmptyLanguage_ReturnsDefault()
    {
        var catalog = new TtsVoiceCatalog();
        catalog.Add("engine", CreateVoice("v1", "zh-CN"), CreateVoice("v2", "en-US", null, null, true));

        Assert.Equal("v2", catalog.Select(null).Name);
        Assert.Equal("v2", catalog.SelectDefault().Name);
        Assert.Equal("v2", catalog.SelectDefault("engine").Name);
    }

    [Fact(DisplayName = "默认音色按引擎名过滤，没有默认时取首个条目")]
    public void SelectDefault_RespectsEngineName()
    {
        var catalog = new TtsVoiceCatalog();
        catalog.Add("a", CreateVoice("a1", "zh-CN", null, null, true));
        catalog.Add("b", CreateVoice("b1", "en-US"));

        Assert.Equal("a1", catalog.SelectDefault().Name);
        Assert.Equal("b1", catalog.SelectDefault("b").Name);
        Assert.Null(catalog.SelectDefault("missing"));
    }

    [Fact(DisplayName = "按语言、性别与场景筛选音色")]
    public void Find_FiltersLanguageGenderScenario()
    {
        var catalog = new TtsVoiceCatalog();
        catalog.Add("engine",
            CreateVoice("f1", "zh-CN", "Female", null, false, TtsScenario.Alert),
            CreateVoice("m1", "zh-CN", "Male", null, false, TtsScenario.Narration),
            CreateVoice("e1", "en-US", "Female"));

        Assert.Equal(2, catalog.Find("zh-CN").Count);
        Assert.Equal(2, catalog.Find("zh").Count);
        Assert.Single(catalog.Find("zh-CN", gender: "male"));
        Assert.Single(catalog.Find("zh-CN", TtsScenario.Alert));
        Assert.Single(catalog.Find("zh-CN", TtsScenario.Narration));
        Assert.Equal(2, catalog.Find(null, null, "Female").Count);
        Assert.Empty(catalog.Find("ja-JP"));
    }

    [Fact(DisplayName = "挑选后写入选项，找不到时不改动选项")]
    public void Apply_WritesVoiceId()
    {
        var catalog = new TtsVoiceCatalog();
        catalog.Add("engine", CreateVoice("智瑜", "zh-CN", "Female", "101001", true, TtsScenario.Alert));

        var options = new TtsOptions { Voice = "old" };
        var entry = catalog.Apply(options, "zh-CN", TtsScenario.Alert);

        Assert.NotNull(entry);
        Assert.Equal("101001", options.Voice);

        var miss = catalog.Apply(options, "ja-JP");
        Assert.Null(miss);
        Assert.Equal("101001", options.Voice);
    }

    [Fact(DisplayName = "选项为空时抛参数异常")]
    public void Apply_NullOptions_Throws()
    {
        var catalog = new TtsVoiceCatalog();

        Assert.Throws<ArgumentNullException>(() => catalog.Apply(null, "zh-CN"));
    }

    [Fact(DisplayName = "云端引擎内置常用音色且有唯一默认音色")]
    public void CloudEngines_ProvideBuiltinVoices()
    {
        var aliyun = new AliyunTtsEngine();
        var tencent = new TencentTtsEngine();
        var baidu = new BaiduTtsEngine();
        var azure = new AzureTtsEngine();

        AssertVoiceCoversDefault(aliyun, aliyun.Voice);
        AssertVoiceCoversDefault(tencent, tencent.VoiceType.ToString());
        AssertVoiceCoversDefault(baidu, baidu.Person.ToString());
        AssertVoiceCoversDefault(azure, azure.Voice);
    }

    [Fact(DisplayName = "云端引擎音色可通过目录按语言与场景挑选")]
    public void CloudEngineVoices_WorkWithCatalog()
    {
        var azure = new AzureTtsEngine();
        var catalog = new TtsVoiceCatalog(azure);

        var narration = catalog.Select("zh-CN", TtsScenario.Narration);
        Assert.NotNull(narration);
        Assert.Equal(azure, narration.Engine);
        Assert.Equal(narration.Voice.Culture, "zh-CN");

        var options = new TtsOptions();
        catalog.Apply(options, "zh", TtsScenario.Notification);
        Assert.False(String.IsNullOrEmpty(options.Voice));
        Assert.Contains(catalog.Entries, e => e.Id == options.Voice);
    }

    [Fact(DisplayName = "引擎默认音色列表可通过客户端枚举")]
    public void Client_GetVoices_ReturnsBuiltinCatalog()
    {
        using var client = new TtsClient(new BaiduTtsEngine());

        var voices = client.GetVoices();

        Assert.Equal(4, voices.Count);
        Assert.Contains(voices, v => v.Id == "0" && v.IsDefault);
    }

    private static void AssertVoiceCoversDefault(CloudTtsEngine engine, String defaultId)
    {
        var voices = engine.GetVoices();

        Assert.NotEmpty(voices);
        Assert.Single(voices.Where(v => v.IsDefault));
        var voice = voices.First(v => v.IsDefault);
        Assert.Equal(defaultId, voice.Id);
        Assert.False(String.IsNullOrEmpty(voice.Culture));
        Assert.False(String.IsNullOrEmpty(voice.Gender));
        Assert.Contains(voices, v => v.Scenarios != null && v.Scenarios.Count > 0);
    }
}
