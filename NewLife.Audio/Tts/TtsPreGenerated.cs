using System.Text;

namespace NewLife.Audio.Tts;

/// <summary>预生成语音库</summary>
/// <remarks>
/// 嵌入式设备的神经声学模型合成一句 20 字的话往往要 1 秒以上，而反复播放的告警语只有那么几十句。
/// 把这些固定语句提前合成好落盘，运行时就是一次文件读取，首包可以压到几十毫秒。
/// <para>
/// 目录里每条音频是“键.pcm + 键.meta”，键由 <see cref="TtsCache.BuildKey"/> 统一生成，
/// 因此预生成库与运行时缓存天然互通：预生成过的句子，运行时按缓存命中处理。
/// </para>
/// <para>预生成按引擎分别生成，换引擎需要重新生成。</para>
/// </remarks>
/// <example>
/// <code>
/// var pregen = new TtsPreGenerated("/data/tts-pregen");
/// if (pregen.TryGet(request, "chaowen", out var audio)) player.Play(audio.Pcm);
///
/// // 打包进镜像前批量生成
/// await pregen.BuildAsync(engine, ["设备离线", "温度过高", "请检查网络"]);
/// </code>
/// </example>
public class TtsPreGenerated
{
    private readonly TtsCache _store;

    /// <summary>预生成库目录</summary>
    public String Root => _store.Root;

    /// <summary>是否启用</summary>
    public Boolean Enabled
    {
        get => _store.Enabled;
        set => _store.Enabled = value;
    }

    /// <summary>文本规范化器。必须与运行时保持一致，否则键对不上</summary>
    public TtsText Text { get; set; } = TtsText.Default;

    /// <summary>默认预生成目录</summary>
    public static String DefaultRoot => Path.Combine(Path.GetTempPath(), "NewLife.Audio.tts.pregen");

    /// <summary>按指定目录初始化预生成库</summary>
    /// <param name="root">预生成库目录</param>
    public TtsPreGenerated(String root) => _store = new TtsCache(root.IsNullOrEmpty() ? DefaultRoot : root);

    /// <summary>条目数</summary>
    public Int32 Count => Directory.Exists(Root) ? Directory.GetFiles(Root, "*.pcm").Length : 0;

    /// <summary>规范化文本，供外部与运行时对齐键</summary>
    /// <param name="request">合成请求</param>
    /// <returns>规范化后的文本</returns>
    public String Normalize(TtsRequest request)
    {
        var text = request?.Text ?? "";
        return request == null || !request.Normalize ? text : Text.Normalize(text, request.NumberMode);
    }

    /// <summary>查找预生成音频</summary>
    /// <param name="request">合成请求</param>
    /// <param name="engine">引擎名</param>
    /// <param name="result">命中的音频</param>
    /// <returns>是否命中</returns>
    public Boolean TryGet(TtsRequest request, String engine, out TtsResult result)
    {
        var text = Normalize(request);
        var key = TtsCache.BuildKey(request, engine, text);
        return _store.TryGet(key, out result, TtsSource.PreGenerated);
    }

    /// <summary>批量生成预生成音频</summary>
    /// <param name="engine">用于合成的引擎</param>
    /// <param name="texts">待生成文本</param>
    /// <param name="template">请求模板，用于携带音色与语速，可为 null</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>新生成的条目数，已存在的会被跳过</returns>
    public async Task<Int32> BuildAsync(ITtsEngine engine, IEnumerable<String> texts, TtsRequest template = null, CancellationToken cancellationToken = default)
    {
        if (engine == null) throw new ArgumentNullException(nameof(engine));
        if (texts == null) throw new ArgumentNullException(nameof(texts));

        var count = 0;
        foreach (var item in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.IsNullOrEmpty()) continue;

            var request = template?.Clone() ?? new TtsRequest();
            request.Text = item;
            request.Scenario = TtsScenario.Complete;
            request.UseCache = false;

            var text = Normalize(request);
            if (text.IsNullOrEmpty()) continue;

            var key = TtsCache.BuildKey(request, engine.Name, text);
            if (_store.TryGet(key, out _, TtsSource.PreGenerated)) continue;

            var synth = request.Clone();
            synth.Text = text;

            var result = await engine.SynthesizeAsync(synth, null, cancellationToken).ConfigureAwait(false);
            if (result?.Pcm == null || result.Pcm.Length == 0) continue;

            result.Text = text;
            if (_store.Put(key, result)) count++;
        }

        return count;
    }

    /// <summary>列出预生成库里保存的文本，便于核对与去重</summary>
    /// <returns>文本列表</returns>
    public IList<String> ListTexts()
    {
        var list = new List<String>();
        if (!Directory.Exists(Root)) return list;

        foreach (var file in Directory.GetFiles(Root, "*.meta"))
        {
            try
            {
                foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    if (line.StartsWith("Text=")) list.Add(line.Substring(5));
                }
            }
            catch
            {
                // 单个元信息损坏不影响整体枚举
            }
        }

        return list;
    }

    /// <summary>清空预生成库</summary>
    /// <returns>删除的条目数</returns>
    public Int32 Clear() => _store.Clear();

    /// <summary>返回预生成库描述</summary>
    /// <returns>目录与条目数</returns>
    public override String ToString() => $"{Root} entries={Count}";
}
