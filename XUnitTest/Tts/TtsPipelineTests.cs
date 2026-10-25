using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio.DSP;
using NewLife.Audio.Tts;
using Xunit;

namespace XUnitTest.Tts;

/// <summary>可控的测试引擎</summary>
internal class FakeTtsEngine : TtsEngineBase
{
    public Int32 Calls;

    public Boolean Fail;

    public Boolean Available = true;

    public Byte[] Payload = new Byte[320];

    public FakeTtsEngine(String name, Int32 quality, TtsCapability capabilities = TtsCapability.Offline)
    {
        Name = name;
        Quality = quality;
        Capabilities = capabilities;
    }

    public override Boolean IsAvailable => Available;

    public override Task<TtsResult> SynthesizeAsync(TtsRequest request, ITtsSink sink, CancellationToken cancellationToken)
    {
        Calls++;
        if (Fail) throw new InvalidOperationException("fake failure");

        var format = new AudioFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 };
        var result = new TtsResult { Text = request.Text, Format = format, Pcm = Payload, PcmBytes = Payload.Length };

        if (sink != null)
        {
            sink.Open(format);
            sink.Write(Payload, 0, Payload.Length);
            sink.Close();
            result.Pcm = null;
        }

        return Task.FromResult(result);
    }
}

/// <summary>先吐数据再失败的引擎，用于验证不可中途降级</summary>
internal class PartialFailEngine : TtsEngineBase
{
    public PartialFailEngine(String name, Int32 quality)
    {
        Name = name;
        Quality = quality;
        Capabilities = TtsCapability.Offline | TtsCapability.Streaming;
    }

    public override Task<TtsResult> SynthesizeAsync(TtsRequest request, ITtsSink sink, CancellationToken cancellationToken)
    {
        if (sink == null) throw new InvalidOperationException("partial failure");

        sink.Open(new AudioFormat { SampleRate = 16000 });
        sink.Write(new Byte[320], 0, 320);
        throw new InvalidOperationException("partial failure");
    }
}

public class TtsCacheTests : IDisposable
{
    private readonly String _root = Path.Combine(Path.GetTempPath(), "tts-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    [Fact(DisplayName = "写入后按同一键能读回等长 PCM")]
    public void Put_Then_Get_ReturnsSamePcm()
    {
        var cache = new TtsCache(_root);
        var request = new TtsRequest { Text = "设备离线", Voice = "chaowen" };
        var format = new AudioFormat { SampleRate = 22050, Channels = 1, BitsPerSample = 16 };
        var pcm = new Byte[1024];
        for (var i = 0; i < pcm.Length; i++) pcm[i] = (Byte)(i % 251);

        var key = TtsCache.BuildKey(request, "chaowen", request.Text);
        Assert.True(cache.Put(key, new TtsResult { Pcm = pcm, Format = format, Engine = "chaowen", Text = request.Text }));

        Assert.True(cache.TryGet(key, out var result));
        Assert.Equal(pcm.Length, result.Pcm.Length);
        Assert.Equal(pcm[500], result.Pcm[500]);
        Assert.Equal(22050, result.Format.SampleRate);
        Assert.Equal(TtsSource.Cache, result.Source);
        Assert.Equal("chaowen", result.Engine);
    }

    [Fact(DisplayName = "键包含引擎、音色与文本，任一不同都不命中")]
    public void Key_DiffersByEngineVoiceText()
    {
        var request = new TtsRequest { Text = "温度过高", Voice = "v1" };

        var k1 = TtsCache.BuildKey(request, "engineA", request.Text);
        var k2 = TtsCache.BuildKey(request, "engineB", request.Text);

        var other = request.Clone();
        other.Voice = "v2";
        var k3 = TtsCache.BuildKey(other, "engineA", other.Text);

        var k4 = TtsCache.BuildKey(request, "engineA", "温度正常");

        Assert.NotEqual(k1, k2);
        Assert.NotEqual(k1, k3);
        Assert.NotEqual(k1, k4);
        Assert.Equal(k1, TtsCache.BuildKey(request, "engineA", request.Text));
    }

    [Fact(DisplayName = "删除与清空能回收条目")]
    public void Remove_And_Clear()
    {
        var cache = new TtsCache(_root);
        var request = new TtsRequest { Text = "a" };
        var format = AudioFormat.Default;

        var key = TtsCache.BuildKey(request, "e", request.Text);
        cache.Put(key, new TtsResult { Pcm = new Byte[64], Format = format });

        Assert.True(cache.Remove(key));
        Assert.False(cache.TryGet(key, out _));

        cache.Put(key, new TtsResult { Pcm = new Byte[64], Format = format });
        Assert.Equal(1, cache.Clear());
        Assert.False(cache.TryGet(key, out _));
    }

    [Fact(DisplayName = "禁用后既不读也不写")]
    public void Disabled_DoesNothing()
    {
        var cache = new TtsCache(_root) { Enabled = false };
        var key = TtsCache.BuildKey(new TtsRequest { Text = "x" }, "e", "x");

        Assert.False(cache.Put(key, new TtsResult { Pcm = new Byte[32], Format = AudioFormat.Default }));
        Assert.False(cache.TryGet(key, out _));
    }
}

public class TtsRouterTests
{
    [Fact(DisplayName = "完整场景按质量分排序，高分优先")]
    public void Select_Complete_OrdersByQuality()
    {
        using var router = new TtsRouter();
        router.Register(new FakeTtsEngine("low", 50));
        router.Register(new FakeTtsEngine("high", 90));

        var engines = router.Select(TtsScenario.Complete);

        Assert.Equal(2, engines.Count);
        Assert.Equal("high", engines[0].Name);
        Assert.Equal("low", engines[1].Name);
    }

    [Fact(DisplayName = "流式场景优先选支持流式的引擎，其余退居兜底")]
    public void Select_Streaming_PrefersStreamingEngine()
    {
        using var router = new TtsRouter();
        router.Register(new FakeTtsEngine("completeOnly", 95));
        router.Register(new FakeTtsEngine("streaming", 50, TtsCapability.Offline | TtsCapability.Streaming));

        var engines = router.Select(TtsScenario.Streaming);

        Assert.Equal(2, engines.Count);
        Assert.Equal("streaming", engines[0].Name);
        Assert.Equal("completeOnly", engines[1].Name);
    }

    [Fact(DisplayName = "禁止在线时不选在线引擎，断开网络同样不选")]
    public void Select_Online_GatedByProfile()
    {
        using var router = new TtsRouter();
        router.Register(new FakeTtsEngine("http", 90, TtsCapability.Online));
        router.Register(new FakeTtsEngine("local", 40));

        router.Profile.AllowOnline = false;
        Assert.Single(router.Select(TtsScenario.Complete));
        Assert.Equal("local", router.Select(TtsScenario.Complete)[0].Name);

        router.Profile.AllowOnline = true;
        router.Profile.NetworkAvailable = false;
        Assert.Single(router.Select(TtsScenario.Complete));

        router.Profile.NetworkAvailable = true;
        Assert.Equal(2, router.Select(TtsScenario.Complete).Count);
    }

    [Fact(DisplayName = "内存上限低于引擎需求时该引擎不参与选路")]
    public void Select_MemoryGate()
    {
        using var router = new TtsRouter { Profile = TtsRuntimeProfile.Embedded };
        var big = new FakeTtsEngine("big", 90) { RequiredMemoryBytes = 800L * 1024 * 1024 };
        var small = new FakeTtsEngine("small", 40) { RequiredMemoryBytes = 100L * 1024 * 1024 };
        router.Register(big);
        router.Register(small);

        var engines = router.Select(TtsScenario.Complete);

        Assert.Single(engines);
        Assert.Equal("small", engines[0].Name);
    }

    [Fact(DisplayName = "首选引擎失败后自动降级，并记录尝试链")]
    public async Task Synthesize_FallsBack_And_RecordsAttempts()
    {
        using var router = new TtsRouter();
        router.Register(new FakeTtsEngine("broken", 90) { Fail = true });
        router.Register(new FakeTtsEngine("backup", 50));

        var result = await router.SynthesizeAsync(new TtsRequest { Text = "测试" }, null, TestContext.Current.CancellationToken);

        Assert.Equal("backup", result.Engine);
        Assert.Equal(2, result.Attempts.Count);
        Assert.Equal("broken", result.Attempts[0]);
        Assert.Equal("backup", result.Attempts[1]);
        Assert.Equal(320, result.PcmBytes);
    }

    [Fact(DisplayName = "已经输出音频后再失败不降级，直接抛出")]
    public async Task Synthesize_PartialOutput_ThrowsInsteadOfFallback()
    {
        using var router = new TtsRouter();
        router.Register(new PartialFailEngine("partial", 90));
        router.Register(new FakeTtsEngine("backup", 50));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.SynthesizeAsync(new TtsRequest { Text = "测试" }, new TtsBufferSink(), TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "全部失败且不要求抛异常时返回空结果")]
    public async Task Synthesize_AllFailed_ReturnsEmpty()
    {
        using var router = new TtsRouter { ThrowOnFailure = false };
        router.Register(new FakeTtsEngine("broken", 90) { Fail = true });

        var result = await router.SynthesizeAsync(new TtsRequest { Text = "测试" }, null, TestContext.Current.CancellationToken);

        Assert.Equal(TtsSource.None, result.Source);
        Assert.Null(result.Pcm);
    }
}

public class TtsServiceTests : IDisposable
{
    private readonly String _cache = Path.Combine(Path.GetTempPath(), "tts-svc-cache-" + Guid.NewGuid().ToString("N"));
    private readonly String _pregen = Path.Combine(Path.GetTempPath(), "tts-svc-pre-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var dir in new[] { _cache, _pregen })
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // 临时目录清理失败不影响测试结论
            }
        }
    }

    private TtsService CreateService(FakeTtsEngine engine)
    {
        var router = new TtsRouter();
        router.Register(engine);

        return new TtsService(router)
        {
            Cache = new TtsCache(_cache),
            PreGenerated = new TtsPreGenerated(_pregen),
        };
    }

    [Fact(DisplayName = "第二次同样的句子直接命中缓存，不再调用引擎")]
    public async Task Synthesize_SecondCall_HitsCache()
    {
        var engine = new FakeTtsEngine("fake", 80);
        using var service = CreateService(engine);
        var request = new TtsRequest { Text = "温度过高" };

        var first = await service.SynthesizeAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(TtsSource.Engine, first.Source);
        Assert.Equal(320, first.Pcm.Length);

        var second = await service.SynthesizeAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(TtsSource.Cache, second.Source);
        Assert.Equal(320, second.Pcm.Length);
        Assert.Equal(1, engine.Calls);
    }

    [Fact(DisplayName = "流式合成把音频块写进输出目标")]
    public async Task SynthesizeStream_WritesToSink()
    {
        var engine = new FakeTtsEngine("fake", 80, TtsCapability.Offline | TtsCapability.Streaming);
        using var service = CreateService(engine);
        var sink = new TtsBufferSink();

        var result = await service.SynthesizeStreamAsync(new TtsRequest { Text = "温度过高" }, sink, TestContext.Current.CancellationToken);

        Assert.Equal(320, (Int32)sink.Length);
        Assert.Equal(320, (Int32)result.PcmBytes);
        Assert.Null(result.Pcm);
        Assert.Equal(TtsScenario.Streaming, TtsScenario.Streaming);
        Assert.Equal(16000, sink.Format.SampleRate);
    }

    [Fact(DisplayName = "预生成库命中时直接返回，不调用引擎")]
    public async Task Synthesize_PreGenerated_Hits()
    {
        var engine = new FakeTtsEngine("fake", 80);
        using var service = CreateService(engine);
        var request = new TtsRequest { Text = "设备离线" };

        var built = await service.PreGenerated.BuildAsync(engine, ["设备离线"], null, TestContext.Current.CancellationToken);
        Assert.Equal(1, built);
        Assert.Equal(1, engine.Calls);

        var result = await service.SynthesizeAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(TtsSource.PreGenerated, result.Source);
        // 预生成阶段已消耗一次调用，命中后不应再调用引擎
        Assert.Equal(1, engine.Calls);
    }

    [Fact(DisplayName = "文本规范化后送引擎，字母与数字都转成中文")]
    public async Task Synthesize_NormalizesBeforeEngine()
    {
        var engine = new FakeTtsEngine("fake", 80);
        using var service = CreateService(engine);

        var result = await service.SynthesizeAsync(new TtsRequest { Text = "A2 温度 35 度" }, TestContext.Current.CancellationToken);

        Assert.Equal("诶二温度三十五度", result.Text);
    }

    [Fact(DisplayName = "空文本直接拒绝")]
    public async Task Synthesize_EmptyText_Throws()
    {
        var engine = new FakeTtsEngine("fake", 80);
        using var service = CreateService(engine);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SynthesizeAsync(new TtsRequest { Text = "  " }, TestContext.Current.CancellationToken));
    }
}
