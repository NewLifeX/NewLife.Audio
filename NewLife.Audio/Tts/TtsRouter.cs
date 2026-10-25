using System.Diagnostics;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts;

/// <summary>语音合成引擎路由</summary>
/// <remarks>
/// 引擎的可用性取决于场景与运行环境，业务代码不该关心“现在该用哪个模型”。
/// 路由做三件事：
/// <list type="number">
/// <item>按场景排序：流式场景优先支持分块输出的引擎，完整场景优先音质高的引擎。</item>
/// <item>按画像过滤：内存不够的引擎不参与，禁止在线的引擎不参与。</item>
/// <item>按顺序降级：前一个失败自动换下一个，并把尝试链记在 <see cref="TtsResult.Attempts"/> 里。</item>
/// </list>
/// <para>硬约束只有“是否允许在线”（合规问题），内存与流式能力都按软约束处理：优先满足，实在没有就退化，总比彻底没声音好。</para>
/// </remarks>
/// <example>
/// <code>
/// var router = new TtsRouter { Profile = TtsRuntimeProfile.Embedded };
/// router.Register(new ProcessTtsEngine { Name = "piper", FileName = "piper" })
///       .Register(new NullTtsEngine());
///
/// var result = await router.SynthesizeAsync(request, sink, cancellationToken);
/// </code>
/// </example>
public class TtsRouter : IDisposable
{
    private readonly List<ITtsEngine> _engines = [];

    /// <summary>运行画像</summary>
    public TtsRuntimeProfile Profile { get; set; } = new();

    /// <summary>已注册引擎</summary>
    public IList<ITtsEngine> Engines => _engines;

    /// <summary>是否在全部候选失败时抛出异常。为 false 时返回空结果</summary>
    public Boolean ThrowOnFailure { get; set; } = true;

    /// <summary>注册引擎</summary>
    /// <param name="engine">引擎实例，由路由负责释放</param>
    /// <returns>当前实例，便于链式调用</returns>
    public TtsRouter Register(ITtsEngine engine)
    {
        if (engine == null) throw new ArgumentNullException(nameof(engine));
        _engines.Add(engine);
        return this;
    }

    /// <summary>按名称移除引擎</summary>
    /// <param name="name">引擎名</param>
    /// <returns>是否移除成功</returns>
    public Boolean Unregister(String name)
    {
        for (var i = 0; i < _engines.Count; i++)
        {
            if (String.Equals(_engines[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                _engines[i].Dispose();
                _engines.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>查找引擎</summary>
    /// <param name="name">引擎名</param>
    /// <returns>引擎实例，不存在时返回 null</returns>
    public ITtsEngine Find(String name)
    {
        foreach (var engine in _engines)
        {
            if (String.Equals(engine.Name, name, StringComparison.OrdinalIgnoreCase)) return engine;
        }

        return null;
    }

    /// <summary>选出候选引擎，按优先级排序</summary>
    /// <param name="scenario">合成场景</param>
    /// <returns>候选引擎列表，第一个为优先，其余为兜底</returns>
    public IList<ITtsEngine> Select(TtsScenario scenario)
    {
        var primary = new List<ITtsEngine>();
        var relaxed = new List<ITtsEngine>();

        foreach (var engine in _engines)
        {
            if (!Profile.Allows(engine, scenario)) continue;

            if (scenario == TtsScenario.Streaming && !TtsRuntimeProfile.SupportsStreaming(engine))
                relaxed.Add(engine);
            else
                primary.Add(engine);
        }

        Sort(primary);
        Sort(relaxed);

        // 没有能流式的引擎时退化为“整段合成后播放”，仍然给出音频
        primary.AddRange(relaxed);
        return primary;
    }

    /// <summary>合成语音，自动按候选顺序降级</summary>
    /// <param name="request">合成请求，文本应已完成规范化</param>
    /// <param name="sink">分块输出目标，可为 null</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>合成结果。全部候选失败且 <see cref="ThrowOnFailure"/> 为 false 时返回空结果</returns>
    public Task<TtsResult> SynthesizeAsync(TtsRequest request, ITtsSink sink, CancellationToken cancellationToken) =>
        SynthesizeAsync(request, sink, cancellationToken, null);

    /// <summary>合成语音，自动按候选顺序降级</summary>
    /// <remarks>
    /// <paramref name="wrapper"/> 用于在调用前包装引擎，典型用途是插入缓存与预生成查询，
    /// 这样缓存逻辑不必写进每个引擎，也不必在路由里硬编码。
    /// </remarks>
    /// <param name="request">合成请求，文本应已完成规范化</param>
    /// <param name="sink">分块输出目标，可为 null</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <param name="wrapper">引擎包装钩子，可为 null</param>
    /// <returns>合成结果。全部候选失败且 <see cref="ThrowOnFailure"/> 为 false 时返回空结果</returns>
    public virtual async Task<TtsResult> SynthesizeAsync(TtsRequest request, ITtsSink sink, CancellationToken cancellationToken, Func<ITtsEngine, ITtsEngine> wrapper)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        var engines = Select(request.Scenario);
        var attempts = new List<String>();
        var probe = new ProbeSink(sink);
        var lastError = (Exception)null;

        foreach (var candidate in engines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts.Add(candidate.Name);

            var watch = Stopwatch.StartNew();
            try
            {
                var engine = wrapper?.Invoke(candidate) ?? candidate;
                var result = await engine.SynthesizeAsync(request, sink == null ? null : probe, cancellationToken).ConfigureAwait(false);
                watch.Stop();

                if (result == null)
                {
                    lastError = new InvalidOperationException(L.T("TTS engine {0} returned no result.", candidate.Name));
                    continue;
                }

                result.Engine = candidate.Name;
                if (result.Source == TtsSource.None) result.Source = TtsSource.Engine;
                if (result.ElapsedTime <= 0) result.ElapsedTime = watch.Elapsed.TotalSeconds;
                if (result.FirstChunkTime <= 0) result.FirstChunkTime = probe.FirstChunkTime;
                if (result.PcmBytes <= 0) result.PcmBytes = probe.Bytes;
                result.Text ??= request.Text;
                foreach (var item in attempts) result.Attempts.Add(item);

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;

                // 已经吐出去半截音频就不能换引擎，否则用户会听到两段拼接的怪声
                if (probe.Bytes > 0) throw;
            }
        }

        if (ThrowOnFailure)
        {
            var names = String.Join(" → ", attempts);
            throw new InvalidOperationException(L.T("No TTS engine is available. Tried: {0}. Last error: {1}", names, lastError?.Message), lastError);
        }

        return new TtsResult { Source = TtsSource.None, Format = request.Format, Text = request.Text };
    }

    /// <summary>释放全部引擎</summary>
    public void Dispose()
    {
        foreach (var engine in _engines)
        {
            try
            {
                engine.Dispose();
            }
            catch
            {
                // 释放失败不影响其他引擎
            }
        }

        _engines.Clear();
    }

    private static void Sort(List<ITtsEngine> list)
    {
        // 音质优先；同分时内存小的优先，方便小设备尽早拿到可用引擎
        list.Sort((a, b) =>
        {
            var q = b.Quality.CompareTo(a.Quality);
            return q != 0 ? q : a.RequiredMemoryBytes.CompareTo(b.RequiredMemoryBytes);
        });
    }

    /// <summary>记录已写出字节数与首包时间的输出代理</summary>
    private sealed class ProbeSink(ITtsSink inner) : ITtsSink
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();

        public ITtsSink Inner { get; } = inner;

        public Int64 Bytes { get; private set; }

        public Double FirstChunkTime { get; private set; }

        public void Open(AudioFormat format) => Inner.Open(format);

        public void Write(Byte[] buffer, Int32 offset, Int32 count)
        {
            if (FirstChunkTime <= 0 && count > 0) FirstChunkTime = _watch.Elapsed.TotalSeconds;
            Bytes += count;
            Inner.Write(buffer, offset, count);
        }

        public void Close() => Inner.Close();
    }
}
