using System.Runtime.CompilerServices;

namespace NewLife.Audio.Tts.Advanced;

/// <summary>带磁盘缓存的合成引擎</summary>
/// <remarks>
/// 包装任意 <see cref="ITtsEngine"/>，相同文本、音色与语速第二次调用直接读缓存。
/// 未命中时边转发边缓冲，缓存只在完整合成成功后写入，避免半截音频污染缓存。
/// </remarks>
/// <example>
/// <code>
/// var engine = new CachedTtsEngine(new SystemSpeechEngine());
/// var tts = new TtsClient(engine);
/// </code>
/// </example>
public class CachedTtsEngine : TtsEngineBase
{
    /// <summary>被包装的引擎</summary>
    public ITtsEngine Inner { get; }

    /// <summary>缓存</summary>
    public TtsCache Cache { get; }

    /// <summary>初始化缓存引擎</summary>
    /// <param name="inner">被包装的引擎</param>
    /// <param name="cache">缓存，为 null 时使用默认缓存</param>
    public CachedTtsEngine(ITtsEngine inner, TtsCache cache = null) : base("cached-" + inner?.Name)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Cache = cache ?? new TtsCache();
    }

    /// <summary>当前是否可用</summary>
    public override Boolean IsAvailable => Inner.IsAvailable;

    /// <summary>枚举音色</summary>
    /// <returns>音色列表</returns>
    public override IReadOnlyList<TtsVoice> GetVoices() => Inner.GetVoices();

    /// <summary>合成语音，优先读缓存</summary>
    /// <param name="text">待合成文本</param>
    /// <param name="options">合成选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>PCM 分块异步序列</returns>
    public override async IAsyncEnumerable<TtsChunk> SynthesizeAsync(String text, TtsOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var key = TtsCache.BuildKey(Inner.Name, options?.Voice, options?.Rate ?? 1, text);
        if (Cache.TryGet(key, out var pcm, out var format))
        {
            yield return new TtsChunk { Format = format, Data = pcm };
            yield break;
        }

        using var buffer = new MemoryStream();
        format = null;
        await foreach (var chunk in Inner.SynthesizeAsync(text, options, cancellationToken).ConfigureAwait(false))
        {
            if (chunk?.Data == null || chunk.Data.Length == 0) continue;

            format ??= chunk.Format;
            buffer.Write(chunk.Data, 0, chunk.Data.Length);

            yield return chunk;
        }

        if (buffer.Length > 0) Cache.Put(key, buffer.ToArray(), format, Inner.Name, text);
    }

    /// <summary>释放被包装的引擎</summary>
    public override void Dispose() => Inner.Dispose();
}
