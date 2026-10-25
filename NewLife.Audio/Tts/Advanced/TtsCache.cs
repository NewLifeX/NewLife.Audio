using System.Security.Cryptography;
using System.Text;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts.Advanced;

/// <summary>合成结果磁盘缓存</summary>
/// <remarks>
/// 语音合成是纯函数：同样的文本、音色、语速与采样率必定得到同样的音频。
/// 把结果按内容哈希落盘，第二次请求同一句就是一次文件读取。
/// <para>缓存键包含引擎名，避免离线与在线、不同模型之间串音。</para>
/// <para>存储为“裸 PCM + 元信息文本”，不带容器头，读写都不依赖容器解析。</para>
/// </remarks>
/// <example>
/// <code>
/// var cache = new TtsCache { MaxSize = 256L * 1024 * 1024 };
/// var engine = new CachedTtsEngine(new SystemSpeechEngine(), cache);
/// var tts = new TtsClient(engine);
/// </code>
/// </example>
public class TtsCache
{
    private readonly Object _lock = new();
    private Int32 _hits;
    private Int32 _misses;

    /// <summary>缓存根目录</summary>
    public String Root { get; set; }

    /// <summary>是否启用缓存</summary>
    public Boolean Enabled { get; set; } = true;

    /// <summary>最大占用字节数，超出后按最久未使用清理。0 表示不限制</summary>
    public Int64 MaxSize { get; set; } = 128L * 1024 * 1024;

    /// <summary>最大文件数，超出后按最久未使用清理。0 表示不限制</summary>
    public Int32 MaxCount { get; set; } = 4096;

    /// <summary>命中次数</summary>
    public Int32 Hits => _hits;

    /// <summary>未命中次数</summary>
    public Int32 Misses => _misses;

    /// <summary>默认缓存目录</summary>
    public static String DefaultRoot => Path.Combine(Path.GetTempPath(), "NewLife.Audio.tts");

    /// <summary>初始化缓存，使用默认目录</summary>
    public TtsCache() => Root = DefaultRoot;

    /// <summary>初始化缓存</summary>
    /// <param name="root">缓存目录，为空时使用默认目录</param>
    public TtsCache(String root) => Root = root.IsNullOrEmpty() ? DefaultRoot : root;

    /// <summary>计算缓存键</summary>
    /// <param name="engine">引擎名</param>
    /// <param name="voice">音色</param>
    /// <param name="rate">语速倍率</param>
    /// <param name="text">待合成文本</param>
    /// <returns>十六进制内容哈希</returns>
    public static String BuildKey(String engine, String voice, Single rate, String text)
    {
        var raw = $"{engine}\n{voice}\n{rate:0.###}\n{text}";
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }

    /// <summary>尝试读取缓存</summary>
    /// <param name="key">缓存键</param>
    /// <param name="pcm">裸 PCM 数据</param>
    /// <param name="format">音频格式</param>
    /// <returns>是否命中</returns>
    public Boolean TryGet(String key, out Byte[] pcm, out AudioFormat format)
    {
        pcm = null;
        format = null;
        if (!Enabled || key.IsNullOrEmpty()) return false;

        var pcmFile = GetPcmFile(key);
        var metaFile = GetMetaFile(key);
        if (!File.Exists(pcmFile) || !File.Exists(metaFile))
        {
            Interlocked.Increment(ref _misses);
            return false;
        }

        try
        {
            var meta = ReadMeta(metaFile);
            pcm = File.ReadAllBytes(pcmFile);
            if (pcm.Length == 0) throw new InvalidDataException("empty pcm");

            try { File.SetLastWriteTimeUtc(pcmFile, DateTime.UtcNow); } catch { /* 只影响淘汰顺序 */ }

            format = new AudioFormat
            {
                SampleRate = meta.SampleRate > 0 ? meta.SampleRate : 22050,
                Channels = meta.Channels > 0 ? meta.Channels : 1,
                BitsPerSample = meta.BitsPerSample > 0 ? meta.BitsPerSample : 16,
            };
            Interlocked.Increment(ref _hits);
            return true;
        }
        catch
        {
            pcm = null;
            format = null;
            Interlocked.Increment(ref _misses);
            return false;
        }
    }

    /// <summary>写入缓存</summary>
    /// <param name="key">缓存键</param>
    /// <param name="pcm">裸 PCM 数据</param>
    /// <param name="format">音频格式</param>
    /// <param name="engine">引擎名，仅用于元信息</param>
    /// <param name="text">原始文本，仅用于元信息</param>
    /// <returns>是否写入成功</returns>
    public Boolean Put(String key, Byte[] pcm, AudioFormat format, String engine, String text)
    {
        if (!Enabled || key.IsNullOrEmpty() || pcm == null || pcm.Length == 0) return false;

        try
        {
            EnsureRoot();
            var pcmFile = GetPcmFile(key);
            var metaFile = GetMetaFile(key);
            var tmp = pcmFile + ".tmp";
            File.WriteAllBytes(tmp, pcm);
            if (File.Exists(pcmFile)) File.Delete(pcmFile);
            File.Move(tmp, pcmFile);

            var meta = String.Join("\n", new[]
            {
                "SampleRate=" + (format?.SampleRate > 0 ? format.SampleRate : 22050),
                "Channels=" + (format?.Channels > 0 ? format.Channels : 1),
                "BitsPerSample=" + (format?.BitsPerSample > 0 ? format.BitsPerSample : 16),
                "Engine=" + engine,
                "Text=" + text,
            });
            File.WriteAllText(metaFile, meta, Encoding.UTF8);

            if (MaxSize > 0 || MaxCount > 0) Clean();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>删除指定缓存项</summary>
    /// <param name="key">缓存键</param>
    /// <returns>是否删除成功</returns>
    public Boolean Remove(String key)
    {
        if (key.IsNullOrEmpty()) return false;

        var ok = TryDelete(GetPcmFile(key));
        ok |= TryDelete(GetMetaFile(key));
        return ok;
    }

    /// <summary>清空缓存</summary>
    /// <returns>删除的文件数</returns>
    public Int32 Clear()
    {
        var root = Root;
        if (root.IsNullOrEmpty() || !Directory.Exists(root)) return 0;

        var count = 0;
        foreach (var file in Directory.GetFiles(root))
        {
            if (TryDelete(file)) count++;
        }

        return count;
    }

    /// <summary>按最大容量与文件数清理最久未使用的缓存项</summary>
    /// <returns>删除的文件数</returns>
    public Int32 Clean()
    {
        var root = Root;
        if (root.IsNullOrEmpty() || !Directory.Exists(root)) return 0;

        lock (_lock)
        {
            var files = new DirectoryInfo(root).GetFiles("*.pcm");
            Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

            var total = 0L;
            foreach (var file in files) total += GetEntrySize(file);

            var count = files.Length;
            var deleted = 0;
            foreach (var file in files)
            {
                if ((MaxSize <= 0 || total <= MaxSize) && (MaxCount <= 0 || count <= MaxCount)) break;

                var size = GetEntrySize(file);
                var meta = GetMetaFile(Path.GetFileNameWithoutExtension(file.Name));
                if (TryDelete(file.FullName))
                {
                    TryDelete(meta);
                    total -= size;
                    count--;
                    deleted++;
                }
            }

            return deleted;
        }
    }

    /// <summary>统计缓存占用字节数</summary>
    /// <returns>所有缓存文件大小之和</returns>
    public Int64 GetSize()
    {
        var root = Root;
        if (root.IsNullOrEmpty() || !Directory.Exists(root)) return 0;

        var total = 0L;
        foreach (var file in Directory.GetFiles(root))
        {
            try { total += new FileInfo(file).Length; } catch { /* 文件可能已被清理 */ }
        }

        return total;
    }

    /// <summary>返回缓存状态描述</summary>
    /// <returns>命中、未命中与占用空间</returns>
    public override String ToString() => $"{Root} 命中{Hits} 未命中{Misses} 大小{GetSize()}";

    private String EnsureRoot()
    {
        var root = Root.IsNullOrEmpty() ? DefaultRoot : Root;
        if (!Directory.Exists(root)) Directory.CreateDirectory(root);
        return root;
    }

    private String GetPcmFile(String key) => Path.Combine(Root.IsNullOrEmpty() ? DefaultRoot : Root, key + ".pcm");

    private String GetMetaFile(String key) => Path.Combine(Root.IsNullOrEmpty() ? DefaultRoot : Root, key + ".meta");

    private static Int64 GetEntrySize(FileInfo pcm)
    {
        var size = pcm.Length;
        try
        {
            var meta = new FileInfo(Path.ChangeExtension(pcm.FullName, ".meta"));
            if (meta.Exists) size += meta.Length;
        }
        catch
        {
            // 元信息不可读时只按 PCM 计
        }

        return size;
    }

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

    private static MetaInfo ReadMeta(String file)
    {
        var meta = new MetaInfo();
        foreach (var line in File.ReadAllLines(file))
        {
            var p = line.IndexOf('=');
            if (p <= 0) continue;

            var key = line.Substring(0, p);
            var value = line.Substring(p + 1);
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

    private sealed class MetaInfo
    {
        public Int32 SampleRate;
        public Int32 Channels = 1;
        public Int32 BitsPerSample = 16;
        public String Engine = "";
        public String Text = "";
    }
}
