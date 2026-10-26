using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio.Tts;
using NewLife.Audio.Tts.Advanced;
using Xunit;

namespace XUnitTest.Tts;

/// <summary>缓存装饰器</summary>
public class CachedTtsEngineTests
{
    [Fact(DisplayName = "包装空引擎直接拒绝")]
    public void Ctor_NullInner_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CachedTtsEngine(null));
    }

    [Fact(DisplayName = "缓存引擎名带 cached 前缀")]
    public void Name_Prefixed()
    {
        using var engine = new CachedTtsEngine(new FakeTtsEngine("fake"));

        Assert.Equal("cached-fake", engine.Name);
        Assert.NotNull(engine.Cache);
    }

    [Fact(DisplayName = "可用性与音色透传被包装引擎")]
    public void AvailabilityAndVoices_MirrorInner()
    {
        var inner = new FakeTtsEngine("fake")
        {
            Available = false,
            Voices = new List<TtsVoice> { new() { Name = "v1" } },
        };
        using var engine = new CachedTtsEngine(inner);

        Assert.False(engine.IsAvailable);
        Assert.Single(engine.GetVoices());

        inner.Available = true;
        Assert.True(engine.IsAvailable);
    }

    [Fact(DisplayName = "释放缓存引擎同时释放被包装引擎")]
    public void Dispose_DisposesInner()
    {
        var inner = new FakeTtsEngine("fake");
        new CachedTtsEngine(inner).Dispose();

        Assert.True(inner.Disposed);
    }

    [Fact(DisplayName = "第二次相同请求命中缓存")]
    public async Task MissThenHit()
    {
        using var dir = new TempDirectory();
        var inner = new FakeTtsEngine("fake");
        using var engine = new CachedTtsEngine(inner, new TtsCache(dir.Path));

        var first = await CollectAsync(engine, "温度过高");
        var second = await CollectAsync(engine, "温度过高");

        Assert.Equal(1, inner.Calls);
        Assert.Equal(320, first.Length);
        Assert.Equal(first, second);
        Assert.Equal(1, engine.Cache.Hits);
    }

    [Fact(DisplayName = "语速不同视为不同缓存项")]
    public async Task RateChange_NewEntry()
    {
        using var dir = new TempDirectory();
        var inner = new FakeTtsEngine("fake");
        using var engine = new CachedTtsEngine(inner, new TtsCache(dir.Path));

        await CollectAsync(engine, "温度过高", new TtsOptions { Rate = 1 });
        await CollectAsync(engine, "温度过高", new TtsOptions { Rate = 1.5f });
        await CollectAsync(engine, "温度过高", new TtsOptions { Rate = 1 });

        Assert.Equal(2, inner.Calls);
    }

    [Fact(DisplayName = "没有音频时不写缓存")]
    public async Task EmptyAudio_NotCached()
    {
        using var dir = new TempDirectory();
        var inner = new FakeTtsEngine("fake") { Payload = [] };
        using var engine = new CachedTtsEngine(inner, new TtsCache(dir.Path));

        Assert.Empty(await CollectAsync(engine, "温度过高"));
        Assert.Empty(await CollectAsync(engine, "温度过高"));

        Assert.Equal(2, inner.Calls);
    }

    [Fact(DisplayName = "多块结果合并成一条缓存并在下次整块返回")]
    public async Task MultiChunk_CombinedIntoOneEntry()
    {
        using var dir = new TempDirectory();
        var inner = new FakeTtsEngine("fake")
        {
            Chunks = new List<Byte[]> { new Byte[320], new Byte[480] },
        };
        using var engine = new CachedTtsEngine(inner, new TtsCache(dir.Path));

        var first = await CollectAsync(engine, "温度过高");
        var count = await CollectCountAsync(engine, "温度过高");
        var second = await CollectAsync(engine, "温度过高");

        Assert.Equal(800, first.Length);
        Assert.Equal(1, inner.Calls);
        Assert.Equal(1, count);
        Assert.Equal(first, second);
    }

    [Fact(DisplayName = "关闭缓存时每次都调用被包装引擎")]
    public async Task CacheDisabled_AlwaysCallsInner()
    {
        using var dir = new TempDirectory();
        var inner = new FakeTtsEngine("fake");
        using var engine = new CachedTtsEngine(inner, new TtsCache(dir.Path) { Enabled = false });

        await CollectAsync(engine, "温度过高");
        await CollectAsync(engine, "温度过高");

        Assert.Equal(2, inner.Calls);
        Assert.Equal(0, engine.Cache.Hits);
    }

    private static async Task<Byte[]> CollectAsync(ITtsEngine engine, String text, TtsOptions options = null)
    {
        using var stream = new System.IO.MemoryStream();
        await foreach (var chunk in engine.SynthesizeAsync(text, options, TestContext.Current.CancellationToken))
            stream.Write(chunk.Data, 0, chunk.Data.Length);

        return stream.ToArray();
    }

    private static async Task<Int32> CollectCountAsync(ITtsEngine engine, String text, TtsOptions options = null)
    {
        var count = 0;
        await foreach (var _ in engine.SynthesizeAsync(text, options, TestContext.Current.CancellationToken))
            count++;

        return count;
    }
}

/// <summary>预生成语音库</summary>
public class TtsPreGeneratedTests
{
    private static (TtsPreGenerated Pre, FakeTtsEngine Engine, TempDirectory Dir) Create(String prefix = "tts-pregen-")
    {
        var dir = new TempDirectory(prefix);
        var engine = new FakeTtsEngine("fake");
        var pre = new TtsPreGenerated(engine, new TtsCache(dir.Path));

        return (pre, engine, dir);
    }

    [Fact(DisplayName = "批量生成统计新增条数并跳过空文本")]
    public async Task Generate_CountsNewEntries()
    {
        var (pre, engine, dir) = Create();
        using (dir)
        {
            var count = await pre.GenerateAsync(["设备离线", " ", "温度过高"], cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, count);
            Assert.Equal(2, engine.Calls);
            Assert.True(pre.Contains("设备离线"));
            Assert.True(pre.Contains("温度过高"));
        }
    }

    [Fact(DisplayName = "重复文本只合成一次")]
    public async Task Generate_Deduplicates()
    {
        var (pre, engine, dir) = Create();
        using (dir)
        {
            var count = await pre.GenerateAsync(["设备离线", "设备离线"], cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, count);
            Assert.Equal(1, engine.Calls);
        }
    }

    [Fact(DisplayName = "已生成条目可直接读回 PCM 与格式")]
    public async Task TryGet_ReturnsAudio()
    {
        var (pre, engine, dir) = Create();
        using (dir)
        {
            await pre.GenerateAsync(["设备离线"], cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(pre.TryGet("设备离线", null, out var pcm, out var format));
            Assert.Equal(320, pcm.Length);
            Assert.Equal(16000, format.SampleRate);
            Assert.False(pre.Contains("未生成"));
        }
    }

    [Fact(DisplayName = "音色不同视为不同条目")]
    public async Task Options_ChangeKey()
    {
        var (pre, engine, dir) = Create();
        using (dir)
        {
            pre.Options = new TtsOptions { Voice = "v1" };
            await pre.GenerateAsync(["设备离线"], cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(pre.Contains("设备离线"));
            Assert.False(pre.TryGet("设备离线", new TtsOptions(), out _, out _));
            Assert.True(pre.TryGet("设备离线", new TtsOptions { Voice = "v1" }, out _, out _));
            Assert.Equal(1, engine.Calls);
        }
    }

    [Fact(DisplayName = "引擎不可用时拒绝生成")]
    public async Task UnavailableEngine_Throws()
    {
        var (pre, engine, dir) = Create();
        using (dir)
        {
            engine.Available = false;

            await Assert.ThrowsAsync<NotSupportedException>(
                () => pre.GenerateAsync(["设备离线"], cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Fact(DisplayName = "文本集合为空引用时拒绝生成")]
    public async Task NullTexts_Throws()
    {
        var (pre, _, dir) = Create();
        using (dir)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => pre.GenerateAsync(null, cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Fact(DisplayName = "生成响应取消")]
    public async Task Cancelled_Throws()
    {
        var (pre, _, dir) = Create();
        using (dir)
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => pre.GenerateAsync(["设备离线"], cancellationToken: cts.Token));
        }
    }

    [Fact(DisplayName = "没有音频的文本不计入生成数")]
    public async Task EmptyAudio_NotCounted()
    {
        var (pre, engine, dir) = Create();
        using (dir)
        {
            engine.Payload = [];

            var count = await pre.GenerateAsync(["设备离线"], cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, count);
            Assert.False(pre.Contains("设备离线"));
        }
    }

    [Fact(DisplayName = "清空预生成库删除全部文件")]
    public async Task Clear_RemovesAll()
    {
        var (pre, _, dir) = Create();
        using (dir)
        {
            await pre.GenerateAsync(["设备离线", "温度过高"], cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(4, pre.Clear());
            Assert.False(pre.Contains("设备离线"));
        }
    }

    [Fact(DisplayName = "释放预生成库同时释放引擎")]
    public async Task Dispose_DisposesEngine()
    {
        var (pre, engine, dir) = Create();
        using (dir)
        {
            pre.Dispose();

            Assert.True(engine.Disposed);
            Assert.Contains("fake", pre.ToString());
        }
    }

    [Fact(DisplayName = "包装空引擎直接拒绝")]
    public void Ctor_NullEngine_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new TtsPreGenerated(null));
    }
}
