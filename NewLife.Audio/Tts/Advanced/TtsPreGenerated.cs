using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts.Advanced;

/// <summary>预生成语音管理</summary>
/// <remarks>
/// 把固定话术提前合成进 <see cref="TtsCache"/>，运行时只读缓存，避免首次播报的合成延迟。
/// 典型用法是设备告警、导航提示这类文本集合固定的场景。
/// </remarks>
/// <example>
/// <code>
/// var pre = new TtsPreGenerated(new SystemSpeechEngine());
/// await pre.GenerateAsync(["设备已启动", "温度过高", "网络断开"]);
///
/// var tts = new TtsClient(new CachedTtsEngine(pre.Engine, pre.Cache));
/// await tts.SayAsync("设备已启动");
/// </code>
/// </example>
public class TtsPreGenerated
{
    /// <summary>合成引擎</summary>
    public ITtsEngine Engine { get; }

    /// <summary>缓存</summary>
    public TtsCache Cache { get; }

    /// <summary>默认合成选项</summary>
    public TtsOptions Options { get; set; } = new();

    /// <summary>初始化预生成管理</summary>
    /// <param name="engine">合成引擎</param>
    /// <param name="cache">缓存，为 null 时使用默认缓存</param>
    public TtsPreGenerated(ITtsEngine engine, TtsCache cache = null)
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Cache = cache ?? new TtsCache();
    }

    /// <summary>判断文本是否已生成</summary>
    /// <param name="text">待合成文本</param>
    /// <param name="options">合成选项，为 null 时使用 <see cref="Options"/></param>
    /// <returns>缓存是否存在</returns>
    public Boolean Contains(String text, TtsOptions options = null) => TryGet(text, options, out _, out _);

    /// <summary>尝试读取已生成的音频</summary>
    /// <param name="text">待合成文本</param>
    /// <param name="options">合成选项，为 null 时使用 <see cref="Options"/></param>
    /// <param name="pcm">裸 PCM 数据</param>
    /// <param name="format">音频格式</param>
    /// <returns>是否命中</returns>
    public Boolean TryGet(String text, TtsOptions options, out Byte[] pcm, out AudioFormat format)
    {
        var opt = options ?? Options ?? new TtsOptions();
        var key = TtsCache.BuildKey(Engine.Name, opt.Voice, opt.Rate, text);
        return Cache.TryGet(key, out pcm, out format);
    }

    /// <summary>批量生成语音</summary>
    /// <param name="texts">待生成文本集合</param>
    /// <param name="options">合成选项，为 null 时使用 <see cref="Options"/></param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>本次新生成的条数</returns>
    public async Task<Int32> GenerateAsync(IEnumerable<String> texts, TtsOptions options = null, CancellationToken cancellationToken = default)
    {
        if (texts == null) throw new ArgumentNullException(nameof(texts));
        if (!Engine.IsAvailable) throw new NotSupportedException(L.T("The TTS engine {0} is not available.", Engine.Name));

        var opt = options ?? Options ?? new TtsOptions();
        var count = 0;
        var done = new HashSet<String>(StringComparer.Ordinal);
        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (text.IsNullOrWhiteSpace()) continue;

            var key = TtsCache.BuildKey(Engine.Name, opt.Voice, opt.Rate, text);
            if (!done.Add(key)) continue;
            if (Cache.TryGet(key, out _, out _)) continue;

            using var buffer = new MemoryStream();
            AudioFormat format = null;
            await foreach (var chunk in Engine.SynthesizeAsync(text, opt, cancellationToken).ConfigureAwait(false))
            {
                if (chunk?.Data == null || chunk.Data.Length == 0) continue;

                format ??= chunk.Format;
                buffer.Write(chunk.Data, 0, chunk.Data.Length);
            }

            if (buffer.Length > 0 && Cache.Put(key, buffer.ToArray(), format, Engine.Name, text)) count++;
        }

        return count;
    }

    /// <summary>清空已生成的语音</summary>
    /// <returns>删除的文件数</returns>
    public Int32 Clear() => Cache.Clear();

    /// <summary>释放引擎</summary>
    public void Dispose() => Engine.Dispose();

    /// <summary>返回预生成状态描述</summary>
    /// <returns>引擎与缓存状态</returns>
    public override String ToString() => $"{Engine.Name} {Cache}";
}
