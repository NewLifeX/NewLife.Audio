using System.Security.Cryptography;
using System.Text;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts;

/// <summary>合成结果磁盘缓存</summary>
/// <remarks>
/// 语音合成是纯函数：同样的文本、音色、语速、采样率必定得到同样的音频。
/// 把结果按内容哈希落盘，第二次请求同一句就变成一次文件读取，实测可把 1.3 秒降到 0.07 秒。
/// <para>缓存键包含引擎名，避免离线与在线、不同模型之间串音。</para>
/// <para>存储为“裸 PCM + 元信息文本”，不带容器头，读写都不依赖容器解析。</para>
/// </remarks>
/// <example>
/// <code>
/// var cache = new TtsCache { Root = "/var/cache/tts", MaxSize = 256L * 1024 * 1024 };
/// var key = TtsCache.BuildKey(request, "chaowen");
/// if (cache.TryGet(key, out var cached)) player.Play(cached.Pcm);
/// </code>
/// </example>
public class TtsCache
{
    private readonly Object _lock = new();
    private Int32 _hits;
    private Int32 _misses;

    /// <summary>缓存根目录</summary>
    public String Root { get; set; }

    /// <summary>是否启用，默认启用</summary>
    public Boolean Enabled { get; set; } = true;

    /// <summary>容量上限（字节），超过后按最近最少使用淘汰</summary>
    public Int64 MaxSize { get; set; } = 128L * 1024 * 1024;

    /// <summary>条目数上限</summary>
    public Int32 MaxCount { get; set; } = 4096;

    /// <summary>命中次数</summary>
    public Int32 Hits => _hits;

    /// <summary>未命中次数</summary>
    public Int32 Misses => _misses;

    /// <summary>默认缓存目录</summary>
    public static String DefaultRoot => Path.Combine(Path.GetTempPath(), "NewLife.Audio.tts");

    /// <summary>初始化磁盘缓存</summary>
    public TtsCache() => Root = DefaultRoot;

    /// <summary>初始化磁盘缓存</summary>
    /// <param name="root">缓存根目录</param>
    public TtsCache(String root) => Root = root.IsNullOrEmpty() ? DefaultRoot : root;

    /// <summary>构造缓存键</summary>
    /// <remarks>
    /// 键由引擎名、音色、语速与实际参与合成的文本决定：这四项确定，音频就确定，采样率也是模型的固有属性。
    /// 缓存与预生成库共用同一套键，预生成出来的条目可以被运行时直接命中。
    /// </remarks>
    /// <param name="request">合成请求</param>
    /// <param name="engine">引擎名</param>
    /// <param name="text">规范化后的文本</param>
    /// <returns>十六进制哈希键</returns>
    public static String BuildKey(TtsRequest request, String engine, String text)
    {
        request ??= new TtsRequest();
        var raw = $"{engine}|{request.Voice}|{request.Speed:F2}|{text}";

        using var sha = SHA1.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>读取缓存</summary>
    /// <param name="key">缓存键</param>
    /// <param name="result">命中的结果</param>
    /// <param name="source">命中后写入结果的来源标记</param>
    /// <returns>是否命中</returns>
    public Boolean TryGet(String key, out TtsResult result, TtsSource source = TtsSource.Cache)
    {
        result = null;
        if (!Enabled || key.IsNullOrEmpty()) return false;

        lock (_lock)
        {
            try
            {
                var pcmFile = GetPcmFile(key);
                var metaFile = GetMetaFile(key);
                if (!File.Exists(pcmFile) || !File.Exists(metaFile))
                {
                    _misses++;
                    return false;
                }

                var meta = MetaInfo.Load(metaFile);
                var format = new AudioFormat
                {
                    SampleRate = meta.SampleRate,
                    Channels = meta.Channels,
                    BitsPerSample = meta.BitsPerSample > 0 ? meta.BitsPerSample : 16,
                };

                result = new TtsResult
                {
                    Source = source,
                    Engine = meta.Engine,
                    Text = meta.Text,
                    Format = format,
                    Pcm = File.ReadAllBytes(pcmFile),
                };

                // 触发 LRU：命中时刷新访问时间
                if (source == TtsSource.Cache) File.SetLastWriteTimeUtc(pcmFile, DateTime.UtcNow);
                _hits++;
                return true;
            }
            catch
            {
                _misses++;
                return false;
            }
        }
    }

    /// <summary>写入缓存</summary>
    /// <param name="key">缓存键</param>
    /// <param name="result">合成结果，Pcm 不可为空</param>
    /// <returns>是否写入成功</returns>
    public Boolean Put(String key, TtsResult result)
    {
        if (!Enabled || key.IsNullOrEmpty() || result?.Pcm == null || result.Pcm.Length == 0) return false;

        lock (_lock)
        {
            try
            {
                var dir = EnsureRoot();
                var format = result.Format ?? AudioFormat.Default;
                var pcmFile = Path.Combine(dir, key + ".pcm");
                var metaFile = Path.Combine(dir, key + ".meta");

                var tmp = pcmFile + ".tmp";
                File.WriteAllBytes(tmp, result.Pcm);
                File.WriteAllText(metaFile, BuildMeta(result, format), Encoding.UTF8);

                if (File.Exists(pcmFile)) File.Delete(pcmFile);
                File.Move(tmp, pcmFile);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>删除指定键</summary>
    /// <param name="key">缓存键</param>
    /// <returns>是否存在并删除成功</returns>
    public Boolean Remove(String key)
    {
        if (key.IsNullOrEmpty()) return false;

        lock (_lock)
        {
            var pcmFile = GetPcmFile(key);
            var metaFile = GetMetaFile(key);
            var exists = File.Exists(pcmFile);
            if (exists) TryDelete(pcmFile);
            TryDelete(metaFile);
            return exists;
        }
    }

    /// <summary>清空缓存</summary>
    /// <returns>删除的条目数</returns>
    public Int32 Clear()
    {
        lock (_lock)
        {
            if (!Directory.Exists(Root)) return 0;

            var count = 0;
            foreach (var file in Directory.GetFiles(Root, "*.pcm"))
            {
                if (TryDelete(file)) count++;
            }

            foreach (var file in Directory.GetFiles(Root, "*.meta")) TryDelete(file);
            foreach (var file in Directory.GetFiles(Root, "*.tmp")) TryDelete(file);

            return count;
        }
    }

    /// <summary>按最近最少使用顺序清理，直到容量与条目数回到上限以内</summary>
    /// <returns>删除的条目数</returns>
    public Int32 Clean()
    {
        lock (_lock)
        {
            if (!Directory.Exists(Root)) return 0;

            var files = new DirectoryInfo(Root).GetFiles("*.pcm");
            var total = 0L;
            foreach (var file in files) total += file.Length;

            var count = 0;
            if (files.Length <= MaxCount && (MaxSize <= 0 || total <= MaxSize)) return 0;

            Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            foreach (var file in files)
            {
                if (files.Length - count <= MaxCount && (MaxSize <= 0 || total <= MaxSize)) break;

                total -= file.Length;
                if (TryDelete(file.FullName)) count++;
                TryDelete(Path.ChangeExtension(file.FullName, ".meta"));
            }

            return count;
        }
    }

    /// <summary>统计当前缓存占用</summary>
    /// <returns>总字节数</returns>
    public Int64 GetSize()
    {
        lock (_lock)
        {
            if (!Directory.Exists(Root)) return 0;

            var total = 0L;
            foreach (var file in Directory.GetFiles(Root, "*.pcm")) total += new FileInfo(file).Length;
            return total;
        }
    }

    /// <summary>返回缓存统计描述</summary>
    /// <returns>命中率与占用</returns>
    public override String ToString()
    {
        var total = _hits + _misses;
        var rate = total > 0 ? (Double)_hits / total : 0;
        return $"{Root} hits={_hits}/{total}({rate:P0}) size={GetSize() / 1024}KB";
    }

    private String EnsureRoot()
    {
        if (Root.IsNullOrEmpty()) Root = DefaultRoot;
        if (!Directory.Exists(Root)) Directory.CreateDirectory(Root);
        return Root;
    }

    private String GetPcmFile(String key) => Path.Combine(Root, key + ".pcm");

    private String GetMetaFile(String key) => Path.Combine(Root, key + ".meta");

    private static Boolean TryDelete(String file)
    {
        try
        {
            if (!File.Exists(file)) return false;
            File.Delete(file);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static String BuildMeta(TtsResult result, AudioFormat format) => String.Join("\n", new[]
    {
        "SampleRate=" + format.SampleRate,
        "Channels=" + format.Channels,
        "BitsPerSample=" + format.BitsPerSample,
        "Engine=" + (result.Engine ?? ""),
        "Text=" + (result.Text ?? "").Replace("\n", " "),
    });

    /// <summary>缓存元信息</summary>
    private sealed class MetaInfo
    {
        public Int32 SampleRate;
        public Int32 Channels = 1;
        public Int32 BitsPerSample = 16;
        public String Engine = "";
        public String Text = "";

        public static MetaInfo Load(String file)
        {
            var meta = new MetaInfo();
            foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
            {
                var idx = line.IndexOf('=');
                if (idx <= 0) continue;

                var key = line.Substring(0, idx);
                var value = line.Substring(idx + 1);
                switch (key)
                {
                    case "SampleRate": meta.SampleRate = ParseInt(value); break;
                    case "Channels": meta.Channels = ParseInt(value); break;
                    case "BitsPerSample": meta.BitsPerSample = ParseInt(value); break;
                    case "Engine": meta.Engine = value; break;
                    case "Text": meta.Text = value; break;
                }
            }

            return meta;
        }

        private static Int32 ParseInt(String value) => Int32.TryParse(value, out var n) ? n : 0;
    }
}
