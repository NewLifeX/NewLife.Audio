using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using AudioStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NewLife.Audio.DSP;
using NewLife.Audio.Tts;
using NewLife.Audio.Tts.Advanced;

namespace AudioStudio.ViewModels.Speech;

/// <summary>可测试的语音合成引擎种类</summary>
public enum TtsEngineKind
{
    /// <summary>Windows 系统语音（SAPI）</summary>
    System = 0,

    /// <summary>在线 HTTP 语音服务</summary>
    Http = 1,

    /// <summary>子进程合成器（piper 等）</summary>
    Process = 2,

    /// <summary>Linux/macOS 命令行 espeak-ng</summary>
    Espeak = 3,

    /// <summary>macOS 系统 say</summary>
    MacSay = 4,

    /// <summary>静音引擎，用于无模型/无声卡时验证调用链</summary>
    Null = 5,
}

/// <summary>引擎下拉项</summary>
public sealed class TtsEngineOption
{
    /// <summary>引擎种类</summary>
    public TtsEngineKind Kind { get; init; }

    /// <summary>显示名称</summary>
    public String Name { get; init; } = "";

    /// <summary>一句话说明</summary>
    public String Description { get; init; } = "";

    /// <summary>返回显示名称</summary>
    /// <returns>引擎名称</returns>
    public override String ToString() => Name;
}

/// <summary>语音合成（TTS）测试视图模型</summary>
/// <remarks>
/// 覆盖 <see cref="TtsClient"/> 的三种主用法（播报、保存、流式）、
/// 六种引擎（<see cref="SystemSpeechEngine"/>、<see cref="EspeakTtsEngine"/>、<see cref="MacSayTtsEngine"/>、
/// <see cref="HttpTtsEngine"/>、<see cref="ProcessTtsEngine"/>、<see cref="NullTtsEngine"/>）
/// 与三种高级能力（磁盘缓存、预生成、文本规范化），方便逐项手工验证。
/// </remarks>
public partial class TtsViewModel : ViewModelBase
{
    #region 属性
    private TtsClient? _client;
    private TtsCache? _cache;
    private AudioPlayerService? _player;
    private String? _engineSignature;
    private String? _lastSavedPath;

    /// <summary>默认测试文本</summary>
    public const String DefaultText = "设备A2温度过高，请检查";

    /// <summary>引擎候选</summary>
    public ObservableCollection<TtsEngineOption> EngineOptions { get; } = [];

    /// <summary>当前引擎可用音色</summary>
    public ObservableCollection<TtsVoice> Voices { get; } = [];

    /// <summary>测试日志</summary>
    public ObservableCollection<String> Log { get; } = [];

    /// <summary>HTTP 返回类型候选</summary>
    public IReadOnlyList<TtsResponseKind> ResponseKinds { get; } = Enum.GetValues<TtsResponseKind>();

    /// <summary>子进程输出类型候选</summary>
    public IReadOnlyList<TtsOutputKind> OutputKinds { get; } = Enum.GetValues<TtsOutputKind>();

    /// <summary>数字读法候选</summary>
    public IReadOnlyList<TtsNumberMode> NumberModes { get; } = Enum.GetValues<TtsNumberMode>();

    /// <summary>输出目录，保存与流式落盘都在这里</summary>
    public String OutputDirectory { get; } = Path.Combine(Path.GetTempPath(), "AudioStudio-TTS", "output");

    /// <summary>缓存目录，用于缓存与预生成</summary>
    public String CacheDirectory { get; } = Path.Combine(Path.GetTempPath(), "AudioStudio-TTS", "cache");

    /// <summary>当前选中的引擎</summary>
    [ObservableProperty]
    private TtsEngineOption? _selectedEngine;

    /// <summary>引擎状态（名称与可用性）</summary>
    [ObservableProperty]
    private String _engineStatus = "尚未初始化";

    /// <summary>待合成文本</summary>
    [ObservableProperty]
    private String _text = DefaultText;

    /// <summary>音色标识，留空表示引擎默认音色</summary>
    [ObservableProperty]
    private String _voice = "";

    /// <summary>选中音色，选中后自动回填到 <see cref="Voice"/></summary>
    [ObservableProperty]
    private TtsVoice? _selectedVoice;

    /// <summary>语速倍率，1 为正常</summary>
    [ObservableProperty]
    private Double _rate = 1;

    /// <summary>音量，0~1</summary>
    [ObservableProperty]
    private Double _volume = 1;

    /// <summary>是否给引擎挂载磁盘缓存</summary>
    [ObservableProperty]
    private Boolean _useCache;

    /// <summary>是否使用客户端默认选项。为 true 时单次调用不传选项，验证 <see cref="TtsClient.Options"/> 生效</summary>
    [ObservableProperty]
    private Boolean _useClientDefault;

    /// <summary>缓存统计</summary>
    [ObservableProperty]
    private String _cacheInfo = "";

    /// <summary>最近一次合成结果明细</summary>
    [ObservableProperty]
    private String _resultText = "";

    /// <summary>运行状态</summary>
    [ObservableProperty]
    private String _status = "就绪";

    /// <summary>是否有操作正在执行</summary>
    [ObservableProperty]
    private Boolean _isBusy;

    /// <summary>最近一次保存的文件路径</summary>
    [ObservableProperty]
    private String? _lastSavedPathDisplay;

    // ─── HTTP 引擎参数 ───
    /// <summary>HTTP 服务地址，支持 {text} {voice} {speed} 占位</summary>
    [ObservableProperty]
    private String _httpUrl = "http://127.0.0.1:8080/tts";

    /// <summary>HTTP 方法</summary>
    [ObservableProperty]
    private String _httpMethod = "POST";

    /// <summary>请求体类型</summary>
    [ObservableProperty]
    private String _httpContentType = "application/json";

    /// <summary>请求体模板，支持 {text} {text_json} {voice} {speed} 占位</summary>
    [ObservableProperty]
    private String _httpBodyTemplate = "{\"text\":\"{text}\",\"voice\":\"{voice}\"}";

    /// <summary>返回音频类型</summary>
    [ObservableProperty]
    private TtsResponseKind _httpResponseKind = TtsResponseKind.Auto;

    /// <summary>返回裸 PCM 时的采样率</summary>
    [ObservableProperty]
    private Int32 _httpSampleRate = 24000;

    /// <summary>HTTP 超时时间（秒）</summary>
    [ObservableProperty]
    private Int32 _httpTimeout = 30;

    /// <summary>自定义请求头名称，用于携带鉴权</summary>
    [ObservableProperty]
    private String _httpHeaderKey = "";

    /// <summary>自定义请求头值</summary>
    [ObservableProperty]
    private String _httpHeaderValue = "";

    // ─── 子进程引擎参数 ───
    /// <summary>可执行文件路径或名称</summary>
    [ObservableProperty]
    private String _processFileName = "piper";

    /// <summary>命令行参数模板，支持 {text} {voice} {speed} {rate} {length_scale} 占位</summary>
    [ObservableProperty]
    private String _processArguments = "--model zh_CN-huayan-medium.onnx --output-raw";

    /// <summary>是否把文本写入标准输入</summary>
    [ObservableProperty]
    private Boolean _processWriteToStdin = true;

    /// <summary>子进程输出类型</summary>
    [ObservableProperty]
    private TtsOutputKind _processOutputKind = TtsOutputKind.RawPcm;

    /// <summary>子进程输出采样率</summary>
    [ObservableProperty]
    private Int32 _processSampleRate = 22050;

    /// <summary>子进程输出声道数</summary>
    [ObservableProperty]
    private Int32 _processChannels = 1;

    /// <summary>是否在合成前做文本规范化</summary>
    [ObservableProperty]
    private Boolean _processNormalize = true;

    /// <summary>是否把逗号类标点统一成句号</summary>
    [ObservableProperty]
    private Boolean _processUnifyPunctuation = true;

    /// <summary>单次合成超时时间（秒）</summary>
    [ObservableProperty]
    private Int32 _processTimeout = 60;

    // ─── espeak 参数 ───
    /// <summary>espeak 可执行文件，留空自动查找</summary>
    [ObservableProperty]
    private String _espeakFileName = "";

    /// <summary>espeak 语速（词/分钟）</summary>
    [ObservableProperty]
    private Int32 _espeakWordsPerMinute = 175;

    /// <summary>espeak 音量（0~200）</summary>
    [ObservableProperty]
    private Int32 _espeakAmplitude = 100;

    // ─── macOS say 参数 ───
    /// <summary>say 语速（词/分钟）</summary>
    [ObservableProperty]
    private Int32 _sayWordsPerMinute = 175;

    /// <summary>say 输出采样率</summary>
    [ObservableProperty]
    private Int32 _saySampleRate = 22050;

    // ─── 静音引擎参数 ───
    /// <summary>静音引擎每字符时长（秒）</summary>
    [ObservableProperty]
    private Double _nullSecondsPerChar = 0.18;

    /// <summary>静音引擎分块时长（毫秒）</summary>
    [ObservableProperty]
    private Int32 _nullChunkMilliseconds = 40;

    /// <summary>静音引擎采样率</summary>
    [ObservableProperty]
    private Int32 _nullSampleRate = 16000;

    // ─── 高级能力 ───
    /// <summary>文本规范化输入</summary>
    [ObservableProperty]
    private String _normalizeInput = "设备型号 A2 温度 35 度，2024年10月25日";

    /// <summary>文本规范化输出</summary>
    [ObservableProperty]
    private String _normalizeOutput = "";

    /// <summary>数字读法</summary>
    [ObservableProperty]
    private TtsNumberMode _numberMode = TtsNumberMode.Auto;

    /// <summary>预生成文本，每行一句</summary>
    [ObservableProperty]
    private String _pregenerateTexts = "设备离线\n温度过高\n请检查网络";

    /// <summary>预生成结果</summary>
    [ObservableProperty]
    private String _pregenerateInfo = "";

    /// <summary>是否选中 HTTP 引擎</summary>
    public Boolean IsHttpEngine => SelectedEngine?.Kind == TtsEngineKind.Http;

    /// <summary>是否选中子进程引擎</summary>
    public Boolean IsProcessEngine => SelectedEngine?.Kind == TtsEngineKind.Process;

    /// <summary>是否选中 espeak 引擎</summary>
    public Boolean IsEspeakEngine => SelectedEngine?.Kind == TtsEngineKind.Espeak;

    /// <summary>是否选中 macOS say 引擎</summary>
    public Boolean IsMacSayEngine => SelectedEngine?.Kind == TtsEngineKind.MacSay;

    /// <summary>是否选中静音引擎</summary>
    public Boolean IsNullEngine => SelectedEngine?.Kind == TtsEngineKind.Null;

    /// <summary>是否选中系统语音引擎</summary>
    public Boolean IsSystemEngine => SelectedEngine?.Kind == TtsEngineKind.System;

    private TtsCache Cache => _cache ??= new TtsCache(CacheDirectory);
    #endregion

    #region 构造
    /// <summary>初始化视图模型，装载引擎候选并创建首个引擎</summary>
    public TtsViewModel()
    {
        EngineOptions.Add(new TtsEngineOption
        {
            Kind = TtsEngineKind.System,
            Name = "系统语音（Windows SAPI）",
            Description = "SystemSpeechEngine，直接播报，延迟最低",
        });
        EngineOptions.Add(new TtsEngineOption
        {
            Kind = TtsEngineKind.Http,
            Name = "在线服务（HTTP）",
            Description = "HttpTtsEngine，地址/方法/请求体/请求头可配",
        });
        EngineOptions.Add(new TtsEngineOption
        {
            Kind = TtsEngineKind.Process,
            Name = "子进程合成器（piper 等）",
            Description = "ProcessTtsEngine，零原生依赖，真流式",
        });
        EngineOptions.Add(new TtsEngineOption
        {
            Kind = TtsEngineKind.Espeak,
            Name = "espeak-ng（Linux/macOS）",
            Description = "EspeakTtsEngine，需系统安装 espeak-ng",
        });
        EngineOptions.Add(new TtsEngineOption
        {
            Kind = TtsEngineKind.MacSay,
            Name = "macOS say",
            Description = "MacSayTtsEngine，系统自带 /usr/bin/say",
        });
        EngineOptions.Add(new TtsEngineOption
        {
            Kind = TtsEngineKind.Null,
            Name = "静音引擎（Null）",
            Description = "NullTtsEngine，无模型/无声卡时验证调用链",
        });

        SelectedEngine = EngineOptions[0];

        try { Directory.CreateDirectory(OutputDirectory); } catch { /* 目录不可用时按失败处理即可 */ }
        UpdateCacheInfo();
        AddLog("TTS 测试面板就绪：先确认引擎可用，再逐项测试播报/保存/流式与高级能力");
    }
    #endregion

    #region 引擎
    /// <summary>重建引擎并枚举音色</summary>
    [RelayCommand]
    private async Task RefreshEngineAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        try
        {
            _engineSignature = null;
            var client = EnsureClient();
            UpdateCacheInfo();

            Voices.Clear();
            var voices = await Task.Run(() => client.GetVoices());
            foreach (var voice in voices) Voices.Add(voice);

            Status = $"引擎 {client.Engine.Name}｜可用 {client.Engine.IsAvailable}｜音色 {Voices.Count} 个";
            AddLog(Status);

            if (String.IsNullOrWhiteSpace(Voice) && Voices.Count > 0) Voice = Voices[0].Name ?? "";
        }
        catch (Exception ex)
        {
            Status = "引擎初始化失败";
            AddLog($"枚举音色失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedEngineChanged(TtsEngineOption? value)
    {
        OnPropertyChanged(nameof(IsHttpEngine));
        OnPropertyChanged(nameof(IsProcessEngine));
        OnPropertyChanged(nameof(IsEspeakEngine));
        OnPropertyChanged(nameof(IsMacSayEngine));
        OnPropertyChanged(nameof(IsNullEngine));
        OnPropertyChanged(nameof(IsSystemEngine));

        _engineSignature = null;
        AddLog($"切换引擎：{value?.Name}");
        _ = RefreshEngineAsync();
    }

    partial void OnSelectedVoiceChanged(TtsVoice? value)
    {
        if (value?.Name != null) Voice = value.Name;
    }

    partial void OnUseCacheChanged(Boolean value)
    {
        _engineSignature = null;
        AddLog(value ? "已启用磁盘缓存装饰" : "已关闭磁盘缓存装饰");
    }

    private ITtsEngine CreateEngine()
    {
        var kind = SelectedEngine?.Kind ?? TtsEngineKind.System;
        return kind switch
        {
            TtsEngineKind.System => new SystemSpeechEngine(),
            TtsEngineKind.Espeak => CreateEspeak(),
            TtsEngineKind.MacSay => new MacSayTtsEngine
            {
                WordsPerMinute = SayWordsPerMinute,
                SampleRate = SaySampleRate,
            },
            TtsEngineKind.Http => CreateHttp(),
            TtsEngineKind.Process => CreateProcess(),
            _ => new NullTtsEngine
            {
                SecondsPerChar = NullSecondsPerChar,
                ChunkMilliseconds = NullChunkMilliseconds,
                SampleRate = NullSampleRate,
            },
        };
    }

    private EspeakTtsEngine CreateEspeak()
    {
        var engine = new EspeakTtsEngine
        {
            WordsPerMinute = EspeakWordsPerMinute,
            Amplitude = EspeakAmplitude,
        };
        if (!String.IsNullOrWhiteSpace(EspeakFileName)) engine.FileName = EspeakFileName.Trim();

        return engine;
    }

    private HttpTtsEngine CreateHttp()
    {
        var engine = new HttpTtsEngine
        {
            Url = String.IsNullOrWhiteSpace(HttpUrl) ? null : HttpUrl.Trim(),
            Method = String.IsNullOrWhiteSpace(HttpMethod) ? "POST" : HttpMethod.Trim(),
            ContentType = String.IsNullOrWhiteSpace(HttpContentType) ? "application/json" : HttpContentType.Trim(),
            BodyTemplate = HttpBodyTemplate,
            ResponseKind = HttpResponseKind,
            SampleRate = HttpSampleRate,
            TimeoutSeconds = HttpTimeout,
        };
        if (!String.IsNullOrWhiteSpace(HttpHeaderKey) && !String.IsNullOrWhiteSpace(HttpHeaderValue))
            engine.Headers[HttpHeaderKey.Trim()] = HttpHeaderValue.Trim();

        return engine;
    }

    private ProcessTtsEngine CreateProcess() => new()
    {
        FileName = String.IsNullOrWhiteSpace(ProcessFileName) ? null : ProcessFileName.Trim(),
        Arguments = ProcessArguments ?? "",
        WriteTextToStdin = ProcessWriteToStdin,
        OutputKind = ProcessOutputKind,
        SampleRate = ProcessSampleRate,
        Channels = ProcessChannels,
        Normalize = ProcessNormalize,
        UnifyPunctuation = ProcessUnifyPunctuation,
        TimeoutSeconds = ProcessTimeout,
    };

    /// <summary>影响引擎实例的配置指纹，变化时重建客户端</summary>
    private String BuildEngineSignature() => String.Join("|",
        SelectedEngine?.Kind,
        UseCache,
        HttpUrl, HttpMethod, HttpContentType, HttpBodyTemplate, HttpResponseKind, HttpSampleRate, HttpTimeout, HttpHeaderKey, HttpHeaderValue,
        ProcessFileName, ProcessArguments, ProcessWriteToStdin, ProcessOutputKind, ProcessSampleRate, ProcessChannels, ProcessNormalize, ProcessUnifyPunctuation, ProcessTimeout,
        EspeakFileName, EspeakWordsPerMinute, EspeakAmplitude,
        SayWordsPerMinute, SaySampleRate,
        NullSecondsPerChar, NullChunkMilliseconds, NullSampleRate);

    private TtsClient EnsureClient()
    {
        var signature = BuildEngineSignature();
        if (_client != null && _engineSignature == signature) return _client;

        _client?.Dispose();

        var engine = CreateEngine();
        if (UseCache) engine = new CachedTtsEngine(engine, Cache);

        _client = new TtsClient(engine);
        _engineSignature = signature;
        EngineStatus = $"当前引擎 {engine.Name}｜可用 {engine.IsAvailable}";
        AddLog($"已创建引擎 {engine.Name}（可用={engine.IsAvailable}{(UseCache ? "，已挂载磁盘缓存" : "")}）");

        return _client;
    }

    private TtsOptions BuildOptions() => new()
    {
        Voice = String.IsNullOrWhiteSpace(Voice) ? null : Voice.Trim(),
        Rate = (Single)Math.Clamp(Rate, 0.25, 4.0),
        Volume = (Single)Math.Clamp(Volume, 0, 1),
    };

    /// <summary>按当前模式给出单次调用选项：使用客户端默认值时返回 null，并同步刷新默认值</summary>
    /// <param name="client">目标客户端</param>
    /// <returns>单次调用选项，或 null 表示沿用客户端默认</returns>
    private TtsOptions? CallOptions(TtsClient client)
    {
        if (!UseClientDefault) return BuildOptions();

        client.Options = BuildOptions();
        return null;
    }
    #endregion

    #region 主用法
    /// <summary>直接播报</summary>
    [RelayCommand]
    private async Task SpeakAsync()
    {
        if (!BeginOperation("正在播报...")) return;

        try
        {
            var client = EnsureClient();
            var watch = Stopwatch.StartNew();
            await client.SayAsync(Text, CallOptions(client));
            watch.Stop();

            Status = $"播报完成，用时 {watch.Elapsed.TotalSeconds:F2}s";
            ResultText = Describe(client.Engine.Name, "SayAsync（直接播报）", watch.Elapsed.TotalSeconds, null);
            AddLog($"SayAsync ✅ {Status}");
        }
        catch (Exception ex)
        {
            Status = "播报失败";
            AddLog($"SayAsync ❌ {ex.Message}");
            ResultText = ex.ToString();
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>停止播报</summary>
    [RelayCommand]
    private void StopSpeak()
    {
        _client?.Stop();
        Status = "已停止播报";
        AddLog("已请求停止播报");
    }

    /// <summary>保存为 WAV</summary>
    [RelayCommand]
    private Task SaveWavAsync() => SaveCoreAsync(".wav");

    /// <summary>保存为裸 PCM</summary>
    [RelayCommand]
    private Task SavePcmAsync() => SaveCoreAsync(".pcm");

    /// <summary>保存为 MP3（走压缩编码管线）</summary>
    [RelayCommand]
    private Task SaveMp3Async() => SaveCoreAsync(".mp3");

    /// <summary>保存为 FLAC（无损压缩）</summary>
    [RelayCommand]
    private Task SaveFlacAsync() => SaveCoreAsync(".flac");

    private async Task SaveCoreAsync(String extension)
    {
        if (!BeginOperation("正在合成到文件...")) return;

        try
        {
            var client = EnsureClient();
            Directory.CreateDirectory(OutputDirectory);
            var path = Path.Combine(OutputDirectory, $"tts-{DateTime.Now:HHmmss-fff}{extension}");

            var result = await client.SaveAsync(Text, path, CallOptions(client));
            _lastSavedPath = path;
            LastSavedPathDisplay = path;

            var label = extension switch
            {
                ".pcm" => "裸 PCM",
                ".mp3" => "MP3",
                ".flac" => "FLAC",
                _ => "WAV",
            };
            Status = $"已保存 {Path.GetFileName(path)}";
            ResultText = Describe(result.Engine, $"SaveAsync（{label}）", result.ElapsedTime, result) + $"\n输出路径：{path}";
            AddLog($"SaveAsync {label} ✅ {result}");
        }
        catch (Exception ex)
        {
            Status = "保存失败";
            AddLog($"SaveAsync ❌ {ex.Message}");
            ResultText = ex.ToString();
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>流式合成并统计首包与分块，同时把裸 PCM 落盘</summary>
    [RelayCommand]
    private async Task StreamProbeAsync()
    {
        if (!BeginOperation("正在流式合成...")) return;

        try
        {
            var client = EnsureClient();
            Directory.CreateDirectory(OutputDirectory);
            var path = Path.Combine(OutputDirectory, $"stream-{DateTime.Now:HHmmss-fff}.pcm");

            var watch = Stopwatch.StartNew();
            var chunks = 0;
            var bytes = 0L;
            var firstChunk = 0D;
            AudioFormat? format = null;

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                await foreach (var chunk in client.StreamAsync(Text, CallOptions(client)))
                {
                    format ??= chunk.Format;
                    if (firstChunk <= 0) firstChunk = watch.Elapsed.TotalSeconds;

                    stream.Write(chunk.Data, 0, chunk.Data.Length);
                    chunks++;
                    bytes += chunk.Data.Length;
                }
            }

            watch.Stop();
            _lastSavedPath = path;
            LastSavedPathDisplay = path;

            var audio = format != null && format.ByteRate > 0 ? (Double)bytes / format.ByteRate : 0;
            Status = $"流式合成完成：{chunks} 块 / {bytes} 字节";
            ResultText = $"用法：StreamAsync（裸 PCM 流）\n引擎：{client.Engine.Name}" +
                         $"\n分块：{chunks} 块｜字节：{bytes}" +
                         $"\n首包：{firstChunk:F3}s｜总耗时：{watch.Elapsed.TotalSeconds:F3}s" +
                         $"\n格式：{(format == null ? "-" : $"{format.SampleRate}Hz {format.Channels}ch {format.BitsPerSample}bit")}" +
                         $"\n音频时长：{audio:F2}s｜RTF：{(audio > 0 ? watch.Elapsed.TotalSeconds / audio : 0):F2}" +
                         $"\n输出路径：{path}";
            AddLog($"StreamAsync ✅ {Status}，首包 {firstChunk:F3}s");
        }
        catch (Exception ex)
        {
            Status = "流式合成失败";
            AddLog($"StreamAsync ❌ {ex.Message}");
            ResultText = ex.ToString();
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>用播放器回放最近一次保存的 WAV</summary>
    [RelayCommand]
    private void PlayLastFile()
    {
        if (_lastSavedPath == null || !File.Exists(_lastSavedPath))
        {
            AddLog("还没有可回放的文件，请先保存 WAV");
            return;
        }

        if (!_lastSavedPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            AddLog("裸 PCM 没有容器头，无法直接回放，请改用『保存 WAV』");
            return;
        }

        try
        {
            _player ??= new AudioPlayerService();
            _player.Open(_lastSavedPath);
            _player.Play();
            Status = $"正在回放 {Path.GetFileName(_lastSavedPath)}";
            AddLog(Status);
        }
        catch (Exception ex)
        {
            AddLog($"回放失败：{ex.Message}");
        }
    }

    /// <summary>一键跑完全部用法，逐项输出结果</summary>
    [RelayCommand]
    private async Task RunAllAsync()
    {
        if (!BeginOperation("正在跑全部用法...")) return;

        var lines = new List<String>();
        var pass = 0;
        var fail = 0;

        void Record(String name, Boolean ok, String detail)
        {
            if (ok) pass++; else fail++;
            lines.Add($"{(ok ? "✅" : "❌")} {name}：{detail}");
            AddLog(lines[^1]);
        }

        try
        {
            var client = EnsureClient();
            var options = CallOptions(client);

            Record("引擎可用", client.Engine.IsAvailable, $"{client.Engine.Name} 可用={client.Engine.IsAvailable}");

            var voices = client.GetVoices();
            Record("枚举音色", true, $"{voices.Count} 个音色");

            Directory.CreateDirectory(OutputDirectory);
            var stamp = DateTime.Now.ToString("HHmmss-fff");

            try
            {
                var wav = Path.Combine(OutputDirectory, $"all-{stamp}.wav");
                var result = await client.SaveAsync(Text, wav, options);
                _lastSavedPath = wav;
                LastSavedPathDisplay = wav;
                Record("SaveAsync WAV", result.Bytes > 0, $"{result.Bytes} 字节 / {result.AudioTime:F2}s / RTF={result.Rtf:F2}");
            }
            catch (Exception ex)
            {
                Record("SaveAsync WAV", false, ex.Message);
            }

            try
            {
                var pcmPath = Path.Combine(OutputDirectory, $"all-{stamp}.pcm");
                var result = await client.SaveAsync(Text, pcmPath, options);
                Record("SaveAsync PCM", result.Bytes > 0, $"{result.Bytes} 字节 / {result.AudioTime:F2}s");
            }
            catch (Exception ex)
            {
                Record("SaveAsync PCM", false, ex.Message);
            }

            try
            {
                var chunks = 0;
                var bytes = 0L;
                var first = 0D;
                var watch = Stopwatch.StartNew();
                await foreach (var chunk in client.StreamAsync(Text, options))
                {
                    if (first <= 0) first = watch.Elapsed.TotalSeconds;
                    chunks++;
                    bytes += chunk.Data.Length;
                }
                watch.Stop();
                Record("StreamAsync", bytes > 0, $"{chunks} 块 / {bytes} 字节 / 首包 {first:F3}s / 总 {watch.Elapsed.TotalSeconds:F3}s");
            }
            catch (Exception ex)
            {
                Record("StreamAsync", false, ex.Message);
            }

            try
            {
                var normalized = TtsText.Default.Normalize(NormalizeInput, NumberMode);
                Record("文本规范化", !String.IsNullOrEmpty(normalized), normalized);
            }
            catch (Exception ex)
            {
                Record("文本规范化", false, ex.Message);
            }

            if (UseCache)
            {
                try
                {
                    var before = Cache.Hits;
                    var watch = Stopwatch.StartNew();
                    await client.SaveAsync(Text, Path.Combine(OutputDirectory, $"all-{stamp}-cached.wav"), options);
                    watch.Stop();
                    Record("缓存二次命中", Cache.Hits > before, $"累计命中 {Cache.Hits} 次，第二次耗时 {watch.Elapsed.TotalSeconds:F3}s");
                }
                catch (Exception ex)
                {
                    Record("缓存二次命中", false, ex.Message);
                }
            }

            Status = $"全部用法完成：通过 {pass}，失败 {fail}";
            ResultText = String.Join("\n", lines);
            AddLog($"RunAll 完成：通过 {pass}，失败 {fail}");
        }
        finally
        {
            EndOperation();
        }
    }
    #endregion

    #region 高级能力
    /// <summary>文本规范化（中文数字、英文读名、去空白、标点统一、分句）</summary>
    [RelayCommand]
    private void NormalizeText()
    {
        try
        {
            var input = NormalizeInput ?? "";
            var lines = new List<String>
            {
                $"原文：{input}",
                $"规范化（{NumberMode}）：{TtsText.Default.Normalize(input, NumberMode)}",
                $"标点统一：{TtsText.UnifyPunctuation(input)}",
                $"分句：{String.Join(" ｜ ", TtsText.SplitClauses(input))}",
            };

            var numbers = Regex.Matches(input, @"\d+(?:\.\d+)?")
                .Select(m => m.Value)
                .Distinct()
                .OrderByDescending(p => p.Length)
                .Take(3)
                .ToList();
            foreach (var value in numbers)
            {
                lines.Add($"数字 {value}｜逐位读 {ChineseNumber.ToDigits(value)}｜基数读 {ChineseNumber.ToChinese(value, TtsNumberMode.Cardinal)}｜自动 {ChineseNumber.ToChinese(value)}");
            }

            NormalizeOutput = String.Join("\n", lines);
            AddLog("文本规范化 ✅ 结果已更新");
        }
        catch (Exception ex)
        {
            NormalizeOutput = ex.ToString();
            AddLog($"文本规范化 ❌ {ex.Message}");
        }
    }

    /// <summary>清空磁盘缓存</summary>
    [RelayCommand]
    private void ClearCache()
    {
        try
        {
            var count = Cache.Clear();
            UpdateCacheInfo();
            AddLog($"已清空缓存，删除 {count} 个文件");
        }
        catch (Exception ex)
        {
            AddLog($"清空缓存失败：{ex.Message}");
        }
    }

    /// <summary>刷新缓存统计</summary>
    [RelayCommand]
    private void RefreshCacheInfo()
    {
        UpdateCacheInfo();
        AddLog($"缓存统计：{CacheInfo}");
    }

    /// <summary>预生成固定语句</summary>
    [RelayCommand]
    private async Task PregenerateAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        Status = "正在预生成...";
        try
        {
            var texts = (PregenerateTexts ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();
            if (texts.Count == 0)
            {
                PregenerateInfo = "没有可预生成的文本";
                return;
            }

            var options = BuildOptions();
            var pregen = new TtsPreGenerated(CreateEngine(), Cache);
            Int32 count;
            var lines = new List<String>();
            try
            {
                count = await pregen.GenerateAsync(texts, options);
                lines.Add($"预生成 {count}/{texts.Count} 句，缓存目录 {Cache.Root}");
                foreach (var item in texts)
                {
                    lines.Add($"{(pregen.Contains(item, options) ? "✅" : "❌")} {item}");
                }
            }
            finally
            {
                pregen.Dispose();
            }

            PregenerateInfo = String.Join("\n", lines);
            UpdateCacheInfo();
            Status = $"预生成完成：{count} 句";
            AddLog(Status);
        }
        catch (Exception ex)
        {
            Status = "预生成失败";
            PregenerateInfo = ex.ToString();
            AddLog($"预生成 ❌ {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>打开输出目录</summary>
    [RelayCommand]
    private void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            Process.Start(new ProcessStartInfo(OutputDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AddLog($"打开目录失败：{ex.Message} {OutputDirectory}");
        }
    }
    #endregion

    #region 辅助
    /// <summary>套用示例文本</summary>
    /// <param name="preset">示例名称：alert/english/number/long/mixed</param>
    [RelayCommand]
    private void ApplyPreset(String? preset)
    {
        Text = preset switch
        {
            "english" => "The temperature of device A2 is too high, please check it.",
            "number" => "订单号 20241025001，金额 1288.50 元，2024年10月25日发货",
            "long" => "一号线三号机组轴承温度连续三分钟超过阈值，系统已自动降载并通知值班工程师，" +
                      "请立即前往现场确认冷却水循环与润滑油位，处理完成后在工单系统回填处置结果。",
            "mixed" => "设备A2 温度 35℃，CPU 占用 92%，请检查 fan speed 与 power supply。",
            _ => DefaultText,
        };
        AddLog($"已套用示例文本：{preset ?? "alert"}");
    }

    /// <summary>清空日志</summary>
    [RelayCommand]
    private void ClearLog() => Log.Clear();

    private Boolean BeginOperation(String status)
    {
        if (IsBusy)
        {
            AddLog("上一次操作尚未结束，已忽略本次请求");
            return false;
        }

        IsBusy = true;
        Status = status;
        return true;
    }

    private void EndOperation()
    {
        IsBusy = false;
        UpdateCacheInfo();
    }

    private void UpdateCacheInfo()
    {
        try
        {
            CacheInfo = $"{Cache.Root}｜命中 {Cache.Hits}｜未命中 {Cache.Misses}｜占用 {Cache.GetSize() / 1024.0:F1} KB";
        }
        catch (Exception ex)
        {
            CacheInfo = ex.Message;
        }
    }

    private static String Describe(String engine, String usage, Double elapsed, TtsResult? result)
    {
        var lines = new List<String>
        {
            $"用法：{usage}",
            $"引擎：{engine}",
            $"耗时：{elapsed:F3}s",
        };
        if (result != null)
        {
            var format = result.Format;
            lines.Add($"格式：{(format == null ? "-" : $"{format.SampleRate}Hz {format.Channels}ch {format.BitsPerSample}bit")}");
            lines.Add($"字节：{result.Bytes}｜音频时长：{result.AudioTime:F2}s");
            lines.Add($"首包：{result.FirstChunkTime:F3}s｜RTF：{result.Rtf:F2}");
        }

        return String.Join("\n", lines);
    }

    private void AddLog(String message)
    {
        Log.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (Log.Count > 500) Log.RemoveAt(0);
    }
    #endregion
}
