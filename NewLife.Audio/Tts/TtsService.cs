using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts;

/// <summary>语音合成服务。把规范化、预生成、缓存、路由与降级串成一条流水线</summary>
/// <remarks>
/// 业务代码只需要调用两个方法，对应两种场景：
/// <list type="bullet">
/// <item><see cref="SynthesizeAsync"/>：完整合成，返回整段 PCM，结果会写入缓存供下次复用。</item>
/// <item><see cref="SynthesizeStreamAsync"/>：流式合成，音频块边到达边写入 <see cref="ITtsSink"/>，首包延迟最低。</item>
/// </list>
/// <para>查询顺序固定为：预生成库 → 磁盘缓存 → 引擎实时合成 → 按画像降级下一个引擎。</para>
/// </remarks>
/// <example>
/// <code>
/// using var service = new TtsService();
/// service.Router.Profile = TtsRuntimeProfile.Embedded;
/// service.Router.Register(new ProcessTtsEngine { FileName = "piper", Arguments = "..." });
///
/// // 一句告警，边合成边播
/// await service.SynthesizeStreamAsync(
///     new TtsRequest { Text = "设备A2温度35度，请检查" },
///     new TtsDelegateSink(null, (buf, off, len) =&gt; player.Write(buf, off, len)));
/// </code>
/// </example>
public class TtsService : IDisposable
{
    private Int32 _synthCount;

    /// <summary>引擎路由</summary>
    public TtsRouter Router { get; set; }

    /// <summary>合成结果磁盘缓存</summary>
    public TtsCache Cache { get; set; }

    /// <summary>预生成语音库</summary>
    public TtsPreGenerated PreGenerated { get; set; }

    /// <summary>文本规范化器</summary>
    public TtsText Text { get; set; } = TtsText.Default;

    /// <summary>是否在缓存超限时自动清理，默认启用</summary>
    public Boolean AutoCleanCache { get; set; } = true;

    /// <summary>初始化语音合成服务</summary>
    public TtsService() : this(null) { }

    /// <summary>初始化语音合成服务</summary>
    /// <param name="router">引擎路由，为 null 时新建</param>
    public TtsService(TtsRouter router)
    {
        Router = router ?? new TtsRouter();
        Cache = new TtsCache();
        PreGenerated = new TtsPreGenerated(TtsPreGenerated.DefaultRoot);
    }

    /// <summary>完整合成。返回整段 PCM，并写入缓存</summary>
    /// <param name="request">合成请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>合成结果</returns>
    public Task<TtsResult> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken = default) =>
        RunAsync(request, null, cancellationToken);

    /// <summary>流式合成。音频块边到达边写入输出目标</summary>
    /// <param name="request">合成请求</param>
    /// <param name="sink">分块输出目标</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>合成结果，<see cref="TtsResult.Pcm"/> 为空但统计字段有效</returns>
    public Task<TtsResult> SynthesizeStreamAsync(TtsRequest request, ITtsSink sink, CancellationToken cancellationToken = default)
    {
        if (sink == null) throw new ArgumentNullException(nameof(sink));
        return RunAsync(request, sink, cancellationToken);
    }

    /// <summary>把合成结果的整段 PCM 写到输出目标</summary>
    /// <remarks>用于缓存命中后的直接播放，或把结果转交给播放器。</remarks>
    /// <param name="result">合成结果</param>
    /// <param name="sink">输出目标</param>
    public static void PlayTo(TtsResult result, ITtsSink sink)
    {
        if (result == null) throw new ArgumentNullException(nameof(result));
        if (sink == null) throw new ArgumentNullException(nameof(sink));
        if (result.Pcm == null || result.Pcm.Length == 0) return;

        sink.Open(result.Format ?? AudioFormat.Default);
        sink.Write(result.Pcm, 0, result.Pcm.Length);
        sink.Close();
    }

    /// <summary>释放路由与其中的引擎</summary>
    public void Dispose()
    {
        Router?.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<TtsResult> RunAsync(TtsRequest request, ITtsSink sink, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        var raw = request.Text ?? "";
        var text = request.Normalize ? Text.Normalize(raw, request.NumberMode) : raw;
        if (text.IsNullOrEmpty()) throw new ArgumentException(L.T("The TTS text is empty."), nameof(request));

        var inner = request.Clone();
        inner.Text = text;
        inner.Scenario = sink == null ? TtsScenario.Complete : TtsScenario.Streaming;

        var wrapper = (Func<ITtsEngine, ITtsEngine>)(engine => new CachedEngine(
            engine,
            inner.UseCache ? Cache : null,
            PreGenerated,
            sink == null && inner.UseCache));

        var result = await Router.SynthesizeAsync(inner, sink, cancellationToken, wrapper).ConfigureAwait(false);

        // 每答 32 次检查一次容量，避免每句都扫目录
        if (sink == null && AutoCleanCache && result != null && result.Source == TtsSource.Engine)
        {
            if (Interlocked.Increment(ref _synthCount) % 32 == 0) Cache.Clean();
        }

        return result;
    }

    /// <summary>给引擎套上预生成与缓存查询</summary>
    private sealed class CachedEngine : TtsEngineBase
    {
        private readonly ITtsEngine _inner;
        private readonly TtsCache _cache;
        private readonly TtsPreGenerated _preGenerated;
        private readonly Boolean _allowStore;

        public CachedEngine(ITtsEngine inner, TtsCache cache, TtsPreGenerated preGenerated, Boolean allowStore)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _cache = cache;
            _preGenerated = preGenerated;
            _allowStore = allowStore;

            Name = inner.Name;
            Capabilities = inner.Capabilities;
            Quality = inner.Quality;
            RequiredMemoryBytes = inner.RequiredMemoryBytes;
        }

        public override Boolean IsAvailable => _inner.IsAvailable;

        public override async Task<TtsResult> SynthesizeAsync(TtsRequest request, ITtsSink sink, CancellationToken cancellationToken)
        {
            var key = TtsCache.BuildKey(request, _inner.Name, request.Text);

            // 预生成与缓存的命中都是整段 PCM，流式场景一次性写出即可，远快于实时合成
            if (_preGenerated != null && _preGenerated.TryGet(request, _inner.Name, out var preGenerated)) return Deliver(preGenerated, sink);
            if (_cache != null && _cache.TryGet(key, out var cached)) return Deliver(cached, sink);

            var result = await _inner.SynthesizeAsync(request, sink, cancellationToken).ConfigureAwait(false);
            if (_allowStore && result?.Pcm != null && result.Pcm.Length > 0)
            {
                result.Text = request.Text;
                _cache.Put(key, result);
            }

            return result;
        }

        private static TtsResult Deliver(TtsResult source, ITtsSink sink)
        {
            WriteAll(sink, source.Pcm, source.Format);
            source.PcmBytes = source.Pcm?.Length ?? 0;
            return source;
        }

        public override void Dispose()
        {
            // 内层引擎由路由统一释放，装饰器只释放自己
        }
    }
}
