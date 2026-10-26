namespace NewLife.Audio.Tts;

/// <summary>音色条目：一个音色及其所属引擎</summary>
/// <remarks>
/// 同一个音色名在不同引擎里含义不同，跨引擎挑选音色必须同时知道“用哪个引擎”和“用哪个音色”，
/// 本类把两者绑在一起，避免调用方自己拼引擎与音色的对应关系。
/// </remarks>
public sealed class TtsVoiceEntry
{
    /// <summary>引擎名称</summary>
    public String EngineName { get; set; }

    /// <summary>引擎实例。只登记静态音色描述（不绑定实例）时为 null</summary>
    public ITtsEngine Engine { get; set; }

    /// <summary>音色</summary>
    public TtsVoice Voice { get; set; }

    /// <summary>音色标识，可直接赋给 <see cref="TtsOptions.Voice"/></summary>
    public String Id => Voice?.Id;

    /// <summary>音色名称</summary>
    public String Name => Voice?.Name;

    /// <summary>区域，如 zh-CN</summary>
    public String Culture => Voice?.Culture;

    /// <summary>性别</summary>
    public String Gender => Voice?.Gender;

    /// <summary>适用场景。为空表示通用</summary>
    public IList<TtsScenario> Scenarios => Voice?.Scenarios;

    /// <summary>是否引擎默认音色</summary>
    public Boolean IsDefault => Voice != null && Voice.IsDefault;

    /// <summary>判断音色是否适用于指定场景</summary>
    /// <param name="scenario">场景</param>
    /// <returns>未声明场景，或声明中已包含该场景（含通用场景）时返回 true</returns>
    public Boolean Matches(TtsScenario scenario)
    {
        var scenarios = Scenarios;
        if (scenarios == null || scenarios.Count <= 0) return true;
        if (scenario == TtsScenario.General) return true;

        for (var i = 0; i < scenarios.Count; i++)
        {
            if (scenarios[i] == scenario) return true;
        }

        return false;
    }

    /// <summary>返回便于日志阅读的描述</summary>
    /// <returns>引擎名与音色</returns>
    public override String ToString() => EngineName.IsNullOrEmpty() ? $"{Voice}" : $"{EngineName}:{Voice}";
}

/// <summary>音色目录：汇总多个引擎的音色，按语言、性别与场景查询和挑选</summary>
/// <remarks>
/// 解决“同一段播报逻辑要在不同引擎/平台上挑到合适音色”的问题：引擎负责上报自己的音色，
/// 目录负责统一描述（引擎、标识、语言、性别、场景）并提供带优先级的挑选。
/// <para>目录不持有引擎生命周期，也不修改引擎状态；<see cref="ITtsEngine.GetVoices"/> 枚举失败按无音色处理。</para>
/// <para>云端服务的音色由服务端维护，引擎只内置常用快照，可整体替换 <see cref="CloudTtsEngine.Voices"/> 或向目录追加。</para>
/// </remarks>
/// <example>
/// <code>
/// var catalog = new TtsVoiceCatalog();
/// catalog.Add(new SystemSpeechEngine()).Add(new AzureTtsEngine { SubscriptionKey = "your-key" });
///
/// foreach (var entry in catalog.Find("zh-CN", TtsScenario.Alert))
///     Console.WriteLine(entry);
///
/// var entry = catalog.Select("zh-CN", TtsScenario.Alert);
/// var tts = new TtsClient(entry?.Engine ?? new SystemSpeechEngine());
/// if (entry != null) tts.Options.Voice = entry.Id;
/// await tts.SayAsync("设备A2温度过高");
/// </code>
/// </example>
public sealed class TtsVoiceCatalog
{
    private readonly List<TtsVoiceEntry> _entries = [];

    /// <summary>全部音色条目，按登记顺序</summary>
    public IReadOnlyList<TtsVoiceEntry> Entries => _entries;

    /// <summary>条目数量</summary>
    public Int32 Count => _entries.Count;

    /// <summary>初始化空目录</summary>
    public TtsVoiceCatalog() { }

    /// <summary>初始化目录并登记一个引擎</summary>
    /// <param name="engine">合成引擎</param>
    public TtsVoiceCatalog(ITtsEngine engine) => Add(engine);

    /// <summary>登记引擎并枚举其音色</summary>
    /// <param name="engine">合成引擎，为 null 时忽略</param>
    /// <returns>目录本身，便于链式调用</returns>
    public TtsVoiceCatalog Add(ITtsEngine engine) => Add(null, engine);

    /// <summary>登记引擎并枚举其音色</summary>
    /// <param name="engineName">引擎名称，为空时取 <see cref="ITtsEngine.Name"/></param>
    /// <param name="engine">合成引擎，为 null 时忽略</param>
    /// <returns>目录本身，便于链式调用</returns>
    public TtsVoiceCatalog Add(String engineName, ITtsEngine engine)
    {
        if (engine == null) return this;

        var name = engineName.IsNullOrEmpty() ? engine.Name : engineName;
        IReadOnlyList<TtsVoice> voices = null;
        try { voices = engine.GetVoices(); } catch { /* 枚举失败按无音色处理 */ }
        if (voices == null) return this;

        foreach (var voice in voices)
        {
            if (voice == null) continue;

            _entries.Add(new TtsVoiceEntry { EngineName = name, Engine = engine, Voice = voice });
        }

        return this;
    }

    /// <summary>登记静态音色描述（不绑定引擎实例）</summary>
    /// <param name="engineName">引擎名称</param>
    /// <param name="voices">音色列表，null 项忽略</param>
    /// <returns>目录本身，便于链式调用</returns>
    public TtsVoiceCatalog Add(String engineName, params TtsVoice[] voices)
    {
        if (voices == null) return this;

        foreach (var voice in voices)
        {
            if (voice == null) continue;

            _entries.Add(new TtsVoiceEntry { EngineName = engineName, Voice = voice });
        }

        return this;
    }

    /// <summary>添加音色条目</summary>
    /// <param name="entry">条目，为 null 或缺少音色时忽略</param>
    /// <returns>目录本身，便于链式调用</returns>
    public TtsVoiceCatalog Add(TtsVoiceEntry entry)
    {
        if (entry?.Voice != null) _entries.Add(entry);

        return this;
    }

    /// <summary>清空目录</summary>
    public void Clear() => _entries.Clear();

    /// <summary>按条件筛选音色</summary>
    /// <param name="language">语言，如 zh-CN 或 zh；为空时不限</param>
    /// <param name="scenario">场景，为 null 时不限</param>
    /// <param name="gender">性别，如 Female、Male；为空时不限</param>
    /// <returns>匹配的条目，无匹配时返回空列表</returns>
    public IReadOnlyList<TtsVoiceEntry> Find(String language = null, TtsScenario? scenario = null, String gender = null)
    {
        var list = new List<TtsVoiceEntry>();
        foreach (var entry in _entries)
        {
            if (!language.IsNullOrEmpty() && !MatchLanguage(entry.Culture, language, false)) continue;
            if (scenario != null && !entry.Matches(scenario.Value)) continue;
            if (!gender.IsNullOrEmpty() && !gender.Equals(entry.Gender, StringComparison.OrdinalIgnoreCase)) continue;

            list.Add(entry);
        }

        return list;
    }

    /// <summary>按语言与场景挑选音色</summary>
    /// <remarks>
    /// 优先级：同区域且场景匹配 → 同区域任意场景 → 同语种且场景匹配 → 同语种任意场景，同档内优先默认音色。
    /// 语言为空时等价于 <see cref="SelectDefault"/>；找不到匹配音色时返回 null，由调用方决定是否回落到引擎默认值。
    /// </remarks>
    /// <param name="language">语言，如 zh-CN、zh；为空时返回默认音色</param>
    /// <param name="scenario">场景</param>
    /// <returns>选中的条目，没有匹配时返回 null</returns>
    public TtsVoiceEntry Select(String language, TtsScenario scenario = TtsScenario.General)
    {
        if (_entries.Count <= 0) return null;
        if (language.IsNullOrEmpty()) return SelectDefault();

        var wanted = scenario == TtsScenario.General ? null : (TtsScenario?)scenario;

        return Pick(language, true, wanted)
            ?? Pick(language, true, null)
            ?? Pick(language, false, wanted)
            ?? Pick(language, false, null);
    }

    /// <summary>挑选默认音色</summary>
    /// <param name="engineName">限定引擎名称，为空时不限</param>
    /// <returns>标记为默认的条目；没有标记时返回首个匹配条目；目录为空时返回 null</returns>
    public TtsVoiceEntry SelectDefault(String engineName = null)
    {
        TtsVoiceEntry first = null;
        foreach (var entry in _entries)
        {
            if (!engineName.IsNullOrEmpty() && !engineName.Equals(entry.EngineName, StringComparison.OrdinalIgnoreCase)) continue;

            if (entry.IsDefault) return entry;
            if (first == null) first = entry;
        }

        return first;
    }

    /// <summary>按语言与场景挑选音色并写入选项</summary>
    /// <param name="options">合成选项，选中音色时改写 <see cref="TtsOptions.Voice"/></param>
    /// <param name="language">语言，如 zh-CN</param>
    /// <param name="scenario">场景</param>
    /// <returns>选中的条目；未找到时返回 null 且不修改选项</returns>
    public TtsVoiceEntry Apply(TtsOptions options, String language, TtsScenario scenario = TtsScenario.General)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));

        var entry = Select(language, scenario);
        if (entry != null && !entry.Id.IsNullOrEmpty()) options.Voice = entry.Id;

        return entry;
    }

    private TtsVoiceEntry Pick(String language, Boolean exact, TtsScenario? scenario)
    {
        TtsVoiceEntry first = null;
        foreach (var entry in _entries)
        {
            if (!MatchLanguage(entry.Culture, language, exact)) continue;
            if (scenario != null && !entry.Matches(scenario.Value)) continue;

            if (entry.IsDefault) return entry;
            if (first == null) first = entry;
        }

        return first;
    }

    private static Boolean MatchLanguage(String culture, String language, Boolean exact)
    {
        if (culture.IsNullOrEmpty() || language.IsNullOrEmpty()) return false;

        if (culture.Equals(language, StringComparison.OrdinalIgnoreCase)) return true;

        // 请求语种，命中区域：zh 匹配 zh-CN
        if (culture.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase)) return true;

        // 放宽到同语种：zh-CN 与 zh-TW 视为同语种，仅作为兜底
        return !exact && String.Equals(PrimaryLanguage(culture), PrimaryLanguage(language), StringComparison.OrdinalIgnoreCase);
    }

    private static String PrimaryLanguage(String language)
    {
        var index = language.IndexOf('-');
        return index > 0 ? language.Substring(0, index) : language;
    }
}
