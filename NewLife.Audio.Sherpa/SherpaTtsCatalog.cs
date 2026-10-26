namespace NewLife.Audio.Tts.Sherpa;

/// <summary>sherpa-onnx 模型目录扫描</summary>
/// <remarks>
/// 把官方 release 包解压出来的目录翻译成 <see cref="SherpaTtsModel"/>，不硬编码文件名。
/// 目录不存在或缺少关键文件时返回空结果，不抛异常。
/// </remarks>
/// <example>
/// <code>
/// var models = SherpaTtsCatalog.Discover(SherpaTtsCatalog.DefaultRoot);
/// var model = SherpaTtsCatalog.Find("chaowen", SherpaTtsCatalog.DefaultRoot);
/// using var engine = new SherpaTtsEngine(model);
/// </code>
/// </example>
public static class SherpaTtsCatalog
{
    private static readonly Dictionary<String, String> _alias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chaowen"] = "vits-piper-zh_CN-chaowen-medium",
        ["chaowen-int8"] = "vits-piper-zh_CN-chaowen-medium-int8",
        ["huayan"] = "huayan",
        ["xiaoya"] = "xiao_ya",
        ["baker"] = "matcha-icefall-zh-baker",
        ["zhen"] = "matcha-icefall-zh-en",
        ["aishell3"] = "aishell3",
        ["kokoro"] = "kokoro",
    };

    /// <summary>默认模型根目录。可用环境变量 TTS_MODELS 覆盖</summary>
    public static String DefaultRoot
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("TTS_MODELS");
            if (!dir.IsNullOrEmpty()) return dir;

            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "newlife", "tts-models");

            return "/root/audio/tts-models";
        }
    }

    /// <summary>扫描模型根目录下所有可用档案</summary>
    /// <param name="root">模型根目录</param>
    /// <returns>按目录名排序的档案列表；目录不存在时返回空列表</returns>
    public static IReadOnlyList<SherpaTtsModel> Discover(String root)
    {
        var list = new List<SherpaTtsModel>();
        if (root.IsNullOrEmpty() || !Directory.Exists(root)) return list;

        foreach (var dir in Directory.GetDirectories(root))
        {
            var model = Create(Path.GetFileName(dir), dir, root);
            if (model != null) list.Add(model);
        }

        return list.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>按名称或别名查找档案</summary>
    /// <param name="name">目录名或别名（chaowen/baker/kokoro…）</param>
    /// <param name="root">模型根目录</param>
    /// <param name="all">已扫描的档案集合，传 null 则现场扫描</param>
    /// <returns>命中的档案，未找到时为 null</returns>
    /// <remarks>模糊匹配可能命中多个（如 int8 与 fp32 两套），优先取可加载的 fp32 版本。</remarks>
    public static SherpaTtsModel Find(String name, String root, IReadOnlyList<SherpaTtsModel> all = null)
    {
        if (name.IsNullOrEmpty()) return null;

        all ??= Discover(root);

        var exact = all.FirstOrDefault(e => String.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        var token = _alias.TryGetValue(name, out var alias) ? alias : name;
        var hits = all.Where(e => e.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        if (hits.Count == 0) return null;
        if (hits.Count == 1) return hits[0];

        // 多个命中时优先可加载的，其次 fp32——实测 A2 上 int8 反而慢一倍
        var usable = hits.Where(e => e.CanLoad).ToList();
        if (usable.Count > 0) hits = usable;

        return hits.FirstOrDefault(e => e.Name.IndexOf("int8", StringComparison.OrdinalIgnoreCase) < 0) ?? hits[0];
    }

    private static SherpaTtsModel Create(String name, String dir, String root)
    {
        var onnx = Directory.GetFiles(dir, "*.onnx", SearchOption.TopDirectoryOnly);
        if (onnx.Length == 0) return null;

        var tokens = First(dir, "tokens.txt");
        var lexicons = Directory.GetFiles(dir, "lexicon*.txt", SearchOption.TopDirectoryOnly)
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();
        var lexicon = lexicons.Count > 0 ? String.Join(",", lexicons) : null;
        var dataDir = Directory.Exists(Path.Combine(dir, "espeak-ng-data")) ? Path.Combine(dir, "espeak-ng-data") : null;
        var dictDir = File.Exists(Path.Combine(dir, "dict", "jieba.dict.utf8")) ? Path.Combine(dir, "dict") : null;
        var ruleFsts = FindRules(dir);
        var ruleFars = First(dir, "rule.far");
        var (license, note) = Describe(name);
        var readsEnglish = Directory.GetFiles(dir, "lexicon-*-en.txt").Length > 0;

        // 目录内最大的 onnx 即主模型：fp32 与 int8 同时存在时取 fp32
        var model = onnx.OrderByDescending(e => new FileInfo(e).Length).First();

        if (name.StartsWith("matcha", StringComparison.OrdinalIgnoreCase))
        {
            var vocoder = FindVocoder(name, root);
            if (vocoder == null) return null;

            return new SherpaTtsModel
            {
                Name = name,
                Kind = SherpaTtsKind.Matcha,
                Dir = dir,
                Model = model,
                Vocoder = vocoder,
                Tokens = tokens,
                Lexicon = lexicon,
                DataDir = dataDir,
                DictDir = dictDir,
                RuleFsts = ruleFsts,
                RuleFars = ruleFars,
                License = license,
                Note = note,
                ReadsEnglish = readsEnglish,
            };
        }

        if (name.StartsWith("kokoro", StringComparison.OrdinalIgnoreCase))
        {
            var voices = First(dir, "voices.bin");
            if (voices == null) return null;

            return new SherpaTtsModel
            {
                Name = name,
                Kind = SherpaTtsKind.Kokoro,
                Dir = dir,
                Model = model,
                Voices = voices,
                Tokens = tokens,
                Lexicon = lexicon,
                DataDir = dataDir,
                DictDir = dictDir,
                RuleFsts = ruleFsts,
                RuleFars = ruleFars,
                License = license,
                Note = note,
                ReadsEnglish = readsEnglish,
                // sid 0~2 是英文音色，3~57 中文女声，58~102 中文男声；中文默认取 zf_001
                DefaultSid = 3,
            };
        }

        return new SherpaTtsModel
        {
            Name = name,
            Kind = SherpaTtsKind.Vits,
            Dir = dir,
            Model = model,
            Tokens = tokens,
            Lexicon = lexicon,
            DataDir = dataDir,
            DictDir = dictDir,
            RuleFsts = ruleFsts,
            RuleFars = ruleFars,
            License = license,
            Note = note,
            ReadsEnglish = readsEnglish,
            // sherpa-onnx 2.0 起不再内置 espeak 音素化，只有词典或 FST 前端的模型才能直接合成
            Unsupported = lexicon == null && ruleFsts == null
                ? "no lexicon or FST frontend; use the piper command line instead"
                : null,
        };
    }

    private static String FindVocoder(String modelName, String root)
    {
        String[] candidates = modelName.IndexOf("zh-en", StringComparison.OrdinalIgnoreCase) >= 0
            ? ["vocos-16khz-univ.onnx", "hifigan_v2.onnx", "vocos-22khz-univ.onnx"]
            : ["vocos-22khz-univ.onnx", "hifigan_v2.onnx", "vocos-16khz-univ.onnx"];

        foreach (var item in candidates)
        {
            var path = Path.Combine(root, item);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private static String FindRules(String dir)
    {
        String[] names =
        [
            "phone.fst", "date.fst", "number.fst",
            "phone-zh.fst", "date-zh.fst", "number-zh.fst",
            "new_heteronym.fst",
        ];
        var hits = names.Select(e => Path.Combine(dir, e)).Where(File.Exists).ToList();

        return hits.Count > 0 ? String.Join(",", hits) : null;
    }

    private static String First(String dir, String file)
    {
        var path = Path.Combine(dir, file);
        return File.Exists(path) ? path : null;
    }

    private static (String License, String Note) Describe(String name)
    {
        if (name.IndexOf("chaowen", StringComparison.OrdinalIgnoreCase) >= 0) return ("CC0", "piper 中文音色，体积最小");
        if (name.IndexOf("xiao_ya", StringComparison.OrdinalIgnoreCase) >= 0) return ("NonCommercial", "音色较好；g2pW 前端仅 Python 版 piper 可用");
        if (name.IndexOf("huayan", StringComparison.OrdinalIgnoreCase) >= 0) return ("Unknown", "由英文底模微调，音色偏硬");
        if (name.IndexOf("zh-baker", StringComparison.OrdinalIgnoreCase) >= 0) return ("NonCommercial", "22.05k 单女声，韵律自然");
        if (name.IndexOf("zh-en", StringComparison.OrdinalIgnoreCase) >= 0) return ("NonCommercial", "16k，中英混排 + 号码/日期规则");
        if (name.IndexOf("aishell3", StringComparison.OrdinalIgnoreCase) >= 0) return ("Apache-2.0", "8kHz 窄带");
        if (name.IndexOf("kokoro", StringComparison.OrdinalIgnoreCase) >= 0) return ("Apache-2.0", "24k，103 音色，CPU 较慢");

        return ("Unknown", "");
    }
}
