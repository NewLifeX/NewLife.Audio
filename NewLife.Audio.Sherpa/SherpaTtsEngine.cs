using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NewLife.Audio.DSP;
using SherpaOnnx;

namespace NewLife.Audio.Tts.Sherpa;

/// <summary>sherpa-onnx 常驻离线合成引擎</summary>
/// <remarks>
/// <see cref="OfflineTts"/> 构造一次即持有全部模型权重，后续每次合成只做推理；
/// piper 命令行每次调用都要重新加载模型，在 ARM 设备上固定开销可达数秒，因此务必复用引擎实例。
/// <para>支持 VITS / Matcha / Kokoro 三种架构，<see cref="TtsOptions.Rate"/> 直接作为语速倍率，
/// <see cref="TtsOptions.Voice"/> 为说话人编号（Kokoro 可用 <see cref="KokoroVoices"/> 把名称换算成编号）。</para>
/// <para>合成在后台线程逐块回调，块到达即产出，首包延迟由模型决定。</para>
/// </remarks>
/// <example>
/// <code>
/// var model = SherpaTtsCatalog.Find("chaowen", SherpaTtsCatalog.DefaultRoot);
/// using var tts = new TtsClient(new SherpaTtsEngine(model, threads: 3));
/// await tts.SaveAsync("设备A2温度过高", "alert.wav");
/// </code>
/// </example>
public sealed class SherpaTtsEngine : TtsEngineBase
{
    private readonly Object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineTts _tts;

    /// <summary>模型档案</summary>
    public SherpaTtsModel Model { get; }

    /// <summary>推理线程数。1GB 内存设备建议 2~3</summary>
    public Int32 Threads { get; set; }

    /// <summary>是否输出 sherpa-onnx 调试日志</summary>
    public Boolean Debug { get; set; }

    /// <summary>推理后端，默认 cpu</summary>
    public String Provider { get; set; } = "cpu";

    /// <summary>默认说话人编号，-1 表示用模型档案的默认值</summary>
    public Int32 DefaultSpeaker { get; set; } = -1;

    /// <summary>模型加载耗时（秒），未加载时为 0</summary>
    public Double LoadTime { get; private set; }

    /// <summary>模型是否已加载</summary>
    public Boolean Loaded => _tts != null;

    /// <summary>初始化常驻引擎</summary>
    /// <param name="model">模型档案</param>
    /// <param name="threads">推理线程数</param>
    public SherpaTtsEngine(SherpaTtsModel model, Int32 threads = 3) : base("sherpa-" + (model?.Name ?? "none"))
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Threads = threads;
    }

    /// <summary>是否可用。模型文件存在且具备文本前端</summary>
    public override Boolean IsAvailable => Model != null && Model.CanLoad && File.Exists(Model.Model);

    /// <summary>枚举音色。Kokoro 返回命名音色，其他架构加载模型后按编号返回</summary>
    /// <returns>音色列表</returns>
    public override IReadOnlyList<TtsVoice> GetVoices()
    {
        if (!IsAvailable) return Array.Empty<TtsVoice>();
        if (Model.Kind == SherpaTtsKind.Kokoro) return KokoroVoices.ToVoices();

        var tts = EnsureLoaded();
        var list = new List<TtsVoice>(tts.NumSpeakers);
        for (var i = 0; i < tts.NumSpeakers; i++)
        {
            list.Add(new TtsVoice { Name = i.ToString() });
        }

        return list;
    }

    /// <summary>合成语音，逐块产出 PCM</summary>
    /// <param name="text">待合成文本</param>
    /// <param name="options">合成选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>PCM 分块异步序列</returns>
    public override async IAsyncEnumerable<TtsChunk> SynthesizeAsync(String text, TtsOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            throw new NotSupportedException($"Sherpa TTS model {Model?.Name} is not available: {Model?.Unsupported ?? "model file missing"}");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var queue = new BlockingCollection<Byte[]>(4);
        try
        {
            var tts = EnsureLoaded();
            var speed = options != null && options.Rate > 0 ? options.Rate : 1f;
            var sid = ResolveSpeaker(options);
            var format = new AudioFormat { SampleRate = tts.SampleRate, Channels = 1, BitsPerSample = 16 };

            Exception error = null;
            var task = Task.Run(() =>
            {
                try
                {
                    var callback = new OfflineTtsCallback((samples, count) =>
                    {
                        try
                        {
                            queue.Add(ToPcm16(samples, count), cancellationToken);
                            return 1;
                        }
                        catch
                        {
                            // 取消或队列关闭时让原生侧停止生成
                            return 0;
                        }
                    });
                    tts.GenerateWithCallback(text, speed, sid, callback);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    try { queue.CompleteAdding(); } catch { /* 已关闭 */ }
                }
            });

            foreach (var pcm in queue.GetConsumingEnumerable(cancellationToken))
                yield return new TtsChunk { Format = format, Data = pcm };

            await task.ConfigureAwait(false);
            if (error != null) throw new InvalidOperationException($"Sherpa TTS synthesis failed: {error.Message}", error);
        }
        finally
        {
            try { queue.CompleteAdding(); } catch { /* 已关闭 */ }
            _gate.Release();
        }
    }

    /// <summary>把引擎输出的 float 采样转换成 16 位小端 PCM</summary>
    /// <param name="samples">float 采样指针（范围 -1~1）</param>
    /// <param name="count">采样点数量</param>
    /// <returns>S16_LE 单声道字节流</returns>
    public static Byte[] ToPcm16(IntPtr samples, Int32 count)
    {
        if (samples == IntPtr.Zero || count <= 0) return [];

        var buffer = new Single[count];
        Marshal.Copy(samples, buffer, 0, count);

        return ToPcm16(buffer);
    }

    /// <summary>把引擎输出的 float 采样转换成 16 位小端 PCM</summary>
    /// <param name="samples">float 采样（范围 -1~1）</param>
    /// <returns>S16_LE 单声道字节流</returns>
    public static Byte[] ToPcm16(Single[] samples)
    {
        if (samples == null || samples.Length == 0) return [];

        var buffer = new Byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = samples[i];
            if (value > 1f) value = 1f;
            else if (value < -1f) value = -1f;

            var sample = (Int16)(value * 32767f);
            buffer[i * 2] = (Byte)(sample & 0xFF);
            buffer[i * 2 + 1] = (Byte)((sample >> 8) & 0xFF);
        }

        return buffer;
    }

    /// <summary>释放模型与原生资源</summary>
    public override void Dispose()
    {
        OfflineTts tts;
        lock (_sync)
        {
            tts = _tts;
            _tts = null;
        }

        try { tts?.Dispose(); } catch { /* 原生侧可能已释放 */ }
        try { _gate.Dispose(); } catch { /* 仍有合成在进行 */ }
    }

    private Int32 ResolveSpeaker(TtsOptions options)
    {
        var sid = DefaultSpeaker >= 0 ? DefaultSpeaker : Model.DefaultSid;
        if (options != null && !options.Voice.IsNullOrEmpty() && Int32.TryParse(options.Voice, out var value)) sid = value;

        return sid;
    }

    private OfflineTts EnsureLoaded()
    {
        if (_tts != null) return _tts;

        lock (_sync)
        {
            if (_tts != null) return _tts;
            if (!IsAvailable)
                throw new NotSupportedException($"Sherpa TTS model {Model.Name} is not available: {Model.Unsupported ?? "model file missing"}");

            var watch = Stopwatch.StartNew();

            var config = new OfflineTtsConfig();
            config.Model.NumThreads = Threads > 0 ? Threads : 3;
            config.Model.Debug = Debug ? 1 : 0;
            config.Model.Provider = Provider.IsNullOrEmpty() ? "cpu" : Provider;
            config.MaxNumSentences = 1;
            config.SilenceScale = 0.2f;
            config.RuleFsts = Model.RuleFsts ?? "";
            config.RuleFars = Model.RuleFars ?? "";

            switch (Model.Kind)
            {
                case SherpaTtsKind.Vits:
                    config.Model.Vits.Model = Model.Model;
                    config.Model.Vits.Tokens = Model.Tokens ?? "";
                    config.Model.Vits.Lexicon = Model.Lexicon ?? "";
                    config.Model.Vits.DataDir = Model.DataDir ?? "";
                    config.Model.Vits.DictDir = Model.DictDir ?? "";
                    break;

                case SherpaTtsKind.Matcha:
                    config.Model.Matcha.AcousticModel = Model.Model;
                    config.Model.Matcha.Vocoder = Model.Vocoder ?? "";
                    config.Model.Matcha.Tokens = Model.Tokens ?? "";
                    config.Model.Matcha.Lexicon = Model.Lexicon ?? "";
                    config.Model.Matcha.DataDir = Model.DataDir ?? "";
                    config.Model.Matcha.DictDir = Model.DictDir ?? "";
                    break;

                case SherpaTtsKind.Kokoro:
                    config.Model.Kokoro.Model = Model.Model;
                    config.Model.Kokoro.Voices = Model.Voices ?? "";
                    config.Model.Kokoro.Tokens = Model.Tokens ?? "";
                    config.Model.Kokoro.Lexicon = Model.Lexicon ?? "";
                    config.Model.Kokoro.DataDir = Model.DataDir ?? "";
                    config.Model.Kokoro.DictDir = Model.DictDir ?? "";
                    break;
            }

            _tts = new OfflineTts(config);
            watch.Stop();
            LoadTime = watch.Elapsed.TotalSeconds;

            return _tts;
        }
    }
}
