using System;
using System.IO;
using NewLife.Audio.DSP;
using NewLife.Audio.Tts.Advanced;
using Xunit;

namespace XUnitTest.Tts;

/// <summary>临时目录，测试结束自动清理</summary>
internal sealed class TempDirectory : IDisposable
{
    public String Path { get; }

    public TempDirectory(String prefix = "tts-test-")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }
}

/// <summary>合成结果磁盘缓存</summary>
public class TtsCacheTests
{
    private static readonly AudioFormat _format = new() { SampleRate = 16000, Channels = 1, BitsPerSample = 16 };

    private static Byte[] Pcm(Int32 size, Byte seed = 1)
    {
        var data = new Byte[size];
        for (var i = 0; i < size; i++) data[i] = (Byte)(seed + i);
        return data;
    }

    [Fact(DisplayName = "缓存键稳定且随引擎、音色、语速、文本变化")]
    public void BuildKey_StableAndSensitive()
    {
        var key = TtsCache.BuildKey("engine", "voice", 1.0f, "温度过高");

        Assert.Equal(64, key.Length);
        Assert.Equal(key, TtsCache.BuildKey("engine", "voice", 1f, "温度过高"));
        Assert.NotEqual(key, TtsCache.BuildKey("engine2", "voice", 1f, "温度过高"));
        Assert.NotEqual(key, TtsCache.BuildKey("engine", "voice2", 1f, "温度过高"));
        Assert.NotEqual(key, TtsCache.BuildKey("engine", "voice", 1.5f, "温度过高"));
        Assert.NotEqual(key, TtsCache.BuildKey("engine", "voice", 1f, "温度过低"));
    }

    [Fact(DisplayName = "缓存目录缺省为临时目录")]
    public void Root_Defaults()
    {
        Assert.Equal(TtsCache.DefaultRoot, new TtsCache().Root);
        Assert.Equal(TtsCache.DefaultRoot, new TtsCache("").Root);
        Assert.EndsWith("NewLife.Audio.tts", TtsCache.DefaultRoot);
    }

    [Fact(DisplayName = "写入后可读回 PCM 与格式")]
    public void PutThenGet_RoundTrips()
    {
        using var dir = new TempDirectory();
        var cache = new TtsCache(dir.Path);
        var pcm = Pcm(640);
        var format = new AudioFormat { SampleRate = 16000, Channels = 2, BitsPerSample = 16 };
        var key = TtsCache.BuildKey("fake", "v", 1f, "你好");

        Assert.True(cache.Put(key, pcm, format, "fake", "你好"));
        Assert.True(cache.TryGet(key, out var got, out var gotFormat));

        Assert.Equal(pcm, got);
        Assert.Equal(16000, gotFormat.SampleRate);
        Assert.Equal(2, gotFormat.Channels);
        Assert.Equal(16, gotFormat.BitsPerSample);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(0, cache.Misses);
    }

    [Fact(DisplayName = "未命中计入统计且不返回数据")]
    public void Get_Missing_CountsMiss()
    {
        using var dir = new TempDirectory();
        var cache = new TtsCache(dir.Path);

        Assert.False(cache.TryGet(TtsCache.BuildKey("fake", null, 1f, "你好"), out var pcm, out var format));
        Assert.Null(pcm);
        Assert.Null(format);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0, cache.Hits);
    }

    [Fact(DisplayName = "非法入参不写入缓存")]
    public void Put_RejectsInvalidInput()
    {
        using var dir = new TempDirectory();
        var cache = new TtsCache(dir.Path);
        var key = TtsCache.BuildKey("fake", null, 1f, "你好");

        Assert.False(cache.Put("", Pcm(320), _format, "fake", "你好"));
        Assert.False(cache.Put(key, null, _format, "fake", "你好"));
        Assert.False(cache.Put(key, [], _format, "fake", "你好"));

        cache.Enabled = false;
        Assert.False(cache.Put(key, Pcm(320), _format, "fake", "你好"));
        Assert.False(cache.TryGet(key, out _, out _));
        Assert.Equal(0, cache.Misses);
    }

    [Fact(DisplayName = "删除单个缓存项")]
    public void Remove_DeletesEntry()
    {
        using var dir = new TempDirectory();
        var cache = new TtsCache(dir.Path);
        var key = TtsCache.BuildKey("fake", null, 1f, "你好");
        cache.Put(key, Pcm(320), _format, "fake", "你好");

        Assert.True(cache.Remove(key));
        Assert.False(cache.TryGet(key, out _, out _));
        Assert.False(cache.Remove(key));
        Assert.False(cache.Remove(""));
    }

    [Fact(DisplayName = "清空缓存删除全部文件")]
    public void Clear_RemovesAll()
    {
        using var dir = new TempDirectory();
        var cache = new TtsCache(dir.Path);
        cache.Put(TtsCache.BuildKey("fake", null, 1f, "a"), Pcm(320), _format, "fake", "a");
        cache.Put(TtsCache.BuildKey("fake", null, 1f, "b"), Pcm(320), _format, "fake", "b");

        Assert.True(cache.GetSize() > 0);
        Assert.Equal(4, cache.Clear());
        Assert.Equal(0, cache.GetSize());

        cache.Root = System.IO.Path.Combine(dir.Path, "missing");
        Assert.Equal(0, cache.Clear());
    }

    [Fact(DisplayName = "按最大文件数淘汰最久未使用项")]
    public void Clean_ByMaxCount()
    {
        using var dir = new TempDirectory();
        var cache = new TtsCache(dir.Path) { MaxSize = 0, MaxCount = 0 };
        var keys = new String[3];
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = TtsCache.BuildKey("fake", null, 1f, "text" + i);
            cache.Put(keys[i], Pcm(320, (Byte)i), _format, "fake", "text" + i);
            File.SetLastWriteTimeUtc(System.IO.Path.Combine(dir.Path, keys[i] + ".pcm"), DateTime.UtcNow.AddMinutes(-30 + i));
        }

        cache.MaxCount = 1;

        Assert.Equal(2, cache.Clean());
        Assert.False(File.Exists(System.IO.Path.Combine(dir.Path, keys[0] + ".pcm")));
        Assert.False(File.Exists(System.IO.Path.Combine(dir.Path, keys[1] + ".pcm")));
        Assert.True(File.Exists(System.IO.Path.Combine(dir.Path, keys[2] + ".pcm")));
    }

    [Fact(DisplayName = "按最大容量淘汰到限额以内")]
    public void Clean_ByMaxSize()
    {
        using var dir = new TempDirectory();
        var cache = new TtsCache(dir.Path) { MaxSize = 0, MaxCount = 0 };
        for (var i = 0; i < 3; i++)
        {
            var key = TtsCache.BuildKey("fake", null, 1f, "text" + i);
            cache.Put(key, Pcm(320, (Byte)i), _format, "fake", "text" + i);
        }

        cache.MaxSize = 1;

        Assert.Equal(3, cache.Clean());
        Assert.Equal(0, cache.GetSize());
    }

    [Fact(DisplayName = "未设置限额时不淘汰")]
    public void Clean_NoLimit_Noop()
    {
        using var dir = new TempDirectory();
        var cache = new TtsCache(dir.Path) { MaxSize = 0, MaxCount = 0 };
        var key = TtsCache.BuildKey("fake", null, 1f, "text");
        cache.Put(key, Pcm(320), _format, "fake", "text");

        Assert.Equal(0, cache.Clean());
        Assert.True(cache.TryGet(key, out _, out _));
    }

    [Fact(DisplayName = "占用统计与状态描述")]
    public void GetSize_And_ToString()
    {
        using var dir = new TempDirectory();
        var cache = new TtsCache(dir.Path);
        var key = TtsCache.BuildKey("fake", null, 1f, "你好");
        cache.Put(key, Pcm(320), _format, "fake", "你好");
        cache.TryGet(key, out _, out _);

        Assert.True(cache.GetSize() > 0);
        var text = cache.ToString();
        Assert.Contains(dir.Path, text);
        Assert.Contains("命中1", text);

        cache.Root = System.IO.Path.Combine(dir.Path, "missing");
        Assert.Equal(0, cache.GetSize());
    }

    [Fact(DisplayName = "损坏的缓存项按未命中处理")]
    public void CorruptedEntry_Miss()
    {
        using var dir = new TempDirectory();
        var cache = new TtsCache(dir.Path);
        var key = TtsCache.BuildKey("fake", null, 1f, "你好");
        cache.Put(key, Pcm(320), _format, "fake", "你好");

        File.WriteAllBytes(System.IO.Path.Combine(dir.Path, key + ".pcm"), []);

        Assert.False(cache.TryGet(key, out var pcm, out var format));
        Assert.Null(pcm);
        Assert.Null(format);
        Assert.Equal(1, cache.Misses);
    }

    [Fact(DisplayName = "元信息缺失按未命中处理")]
    public void MissingMeta_Miss()
    {
        using var dir = new TempDirectory();
        var cache = new TtsCache(dir.Path);
        var key = TtsCache.BuildKey("fake", null, 1f, "你好");
        cache.Put(key, Pcm(320), _format, "fake", "你好");

        File.Delete(System.IO.Path.Combine(dir.Path, key + ".meta"));

        Assert.False(cache.TryGet(key, out _, out _));
    }

    [Fact(DisplayName = "格式缺省时按 22.05kHz 单声道 16 位读回")]
    public void MetaFallback_Defaults()
    {
        using var dir = new TempDirectory();
        var cache = new TtsCache(dir.Path);
        var key = TtsCache.BuildKey("fake", null, 1f, "你好");
        cache.Put(key, Pcm(320), null, "fake", "你好");

        Assert.True(cache.TryGet(key, out _, out var format));
        Assert.Equal(22050, format.SampleRate);
        Assert.Equal(1, format.Channels);
        Assert.Equal(16, format.BitsPerSample);
    }

    [Fact(DisplayName = "元信息数值非法时回落到缺省值")]
    public void MetaWithGarbage_FallsBack()
    {
        using var dir = new TempDirectory();
        var cache = new TtsCache(dir.Path);
        var key = TtsCache.BuildKey("fake", null, 1f, "你好");
        cache.Put(key, Pcm(320), _format, "fake", "你好");

        File.WriteAllText(System.IO.Path.Combine(dir.Path, key + ".meta"), "SampleRate=abc\nChannels=\nBitsPerSample=xyz\n");

        Assert.True(cache.TryGet(key, out _, out var format));
        Assert.Equal(22050, format.SampleRate);
        Assert.Equal(1, format.Channels);
        Assert.Equal(16, format.BitsPerSample);
    }

    [Fact(DisplayName = "缓存目录不存在时自动逐级创建")]
    public void Put_CreatesNestedRoot()
    {
        using var dir = new TempDirectory();
        var root = System.IO.Path.Combine(dir.Path, "a", "b");
        var cache = new TtsCache(root);
        var key = TtsCache.BuildKey("fake", null, 1f, "你好");

        Assert.True(cache.Put(key, Pcm(320), _format, "fake", "你好"));
        Assert.True(Directory.Exists(root));
        Assert.True(cache.TryGet(key, out _, out _));
    }

    [Fact(DisplayName = "缓存根目录不可写时写入返回失败")]
    public void Put_IOException_ReturnsFalse()
    {
        using var dir = new TempDirectory();
        var blocker = System.IO.Path.Combine(dir.Path, "blocker");
        File.WriteAllText(blocker, "x");
        var cache = new TtsCache(blocker);
        var key = TtsCache.BuildKey("fake", null, 1f, "你好");

        Assert.False(cache.Put(key, Pcm(320), _format, "fake", "你好"));
        Assert.False(cache.TryGet(key, out _, out _));
    }

    [Fact(DisplayName = "同一键重复写入以最后一次为准")]
    public void Put_OverwritesExisting()
    {
        using var dir = new TempDirectory();
        var cache = new TtsCache(dir.Path);
        var key = TtsCache.BuildKey("fake", null, 1f, "你好");

        cache.Put(key, Pcm(320, 1), _format, "fake", "你好");
        cache.Put(key, Pcm(480, 2), _format, "fake", "你好");

        Assert.True(cache.TryGet(key, out var pcm, out _));
        Assert.Equal(Pcm(480, 2), pcm);
    }
}
