using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio.DSP;
using NewLife.Audio.Tts;
using Xunit;

namespace XUnitTest.Tts;

/// <summary>最小可用的引擎实现，用于验证基类默认行为</summary>
internal sealed class MinimalTtsEngine : TtsEngineBase
{
    public override async IAsyncEnumerable<TtsChunk> SynthesizeAsync(String text, TtsOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield break;
    }
}

/// <summary>引擎基类与各平台引擎契约</summary>
public class TtsEngineContractTests
{
    [Fact(DisplayName = "引擎基类默认可用且不支持枚举音色")]
    public void Base_Defaults()
    {
        var engine = new MinimalTtsEngine();

        Assert.True(engine.IsAvailable);
        Assert.Empty(engine.GetVoices());
        Assert.Equal("MinimalTtsEngine", engine.ToString());

        engine.Dispose();
    }

    [Fact(DisplayName = "引擎基类命名后以名称描述")]
    public void Base_ToString_UsesName()
    {
        var engine = new NullTtsEngine { Name = "custom" };

        Assert.Equal("custom", engine.ToString());
    }

    [Fact(DisplayName = "静音引擎分块数量与总长符合估算")]
    public async Task NullEngine_Chunking()
    {
        var engine = new NullTtsEngine();
        var total = 0;
        var count = 0;
        var chunkSize = engine.SampleRate * 2 / 1000 * engine.ChunkMilliseconds;

        await foreach (var chunk in engine.SynthesizeAsync("测试", null, TestContext.Current.CancellationToken))
        {
            total += chunk.Data.Length;
            count++;
            Assert.Equal(16000, chunk.Format.SampleRate);
        }

        Assert.Equal(11520, total);
        Assert.Equal(11520 / chunkSize, count);
    }

    [Fact(DisplayName = "静音引擎采样率非法时回落到 16kHz")]
    public async Task NullEngine_SampleRateFallback()
    {
        var engine = new NullTtsEngine { SampleRate = 0 };

        var (pcm, format) = await TtsChunkHelper.CollectAsync(engine, "测", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(16000, format.SampleRate);
        Assert.True(pcm.Length > 0);
    }

    [Fact(DisplayName = "静音引擎按毫秒分块，0 毫秒取最小块")]
    public async Task NullEngine_MinimumChunk()
    {
        var engine = new NullTtsEngine { ChunkMilliseconds = 0 };
        var count = 0;

        await foreach (var chunk in engine.SynthesizeAsync("测", null, TestContext.Current.CancellationToken))
        {
            Assert.Equal(32, chunk.Data.Length);
            count++;
        }

        Assert.Equal(5760 / 32, count);
    }

    [Fact(DisplayName = "静音引擎在空文本或零时长时没有输出")]
    public async Task NullEngine_NoOutput()
    {
        var (nullText, _) = await TtsChunkHelper.CollectAsync(new NullTtsEngine(), null, cancellationToken: TestContext.Current.CancellationToken);
        var (zeroChar, _) = await TtsChunkHelper.CollectAsync(new NullTtsEngine { SecondsPerChar = 0 }, "测试", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(nullText);
        Assert.Empty(zeroChar);
    }

    [Fact(DisplayName = "静音引擎响应取消")]
    public async Task NullEngine_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TtsChunkHelper.CollectAsync(new NullTtsEngine(), "测试", cancellationToken: cts.Token));
    }

    [Fact(DisplayName = "macOS say 引擎在非 macOS 上拒绝合成")]
    public async Task MacSay_NonMac_Throws()
    {
        var engine = new MacSayTtsEngine();

        Assert.Equal("say", engine.Name);
        engine.Stop();

        if (engine.IsAvailable) return;

        Assert.Empty(engine.GetVoices());
        await Assert.ThrowsAsync<PlatformNotSupportedException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(
            () => engine.SpeakAsync("你好", null, TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "espeak 未安装时合成报错且停止安全")]
    public async Task Espeak_Unavailable_Throws()
    {
        var engine = new EspeakTtsEngine();

        Assert.Equal("espeak", engine.Name);
        engine.Stop();

        if (engine.IsAvailable) return;

        Assert.Empty(engine.GetVoices());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.SpeakAsync("你好", null, TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "系统语音引擎停止与释放可重复调用")]
    public void SystemSpeech_StopAndDispose_AreSafe()
    {
        var engine = new SystemSpeechEngine();

        Assert.Equal("system", engine.Name);
        engine.Stop();
        engine.Dispose();
        engine.Dispose();
    }

    [Fact(DisplayName = "系统语音引擎播报响应已取消的令牌")]
    public async Task SystemSpeech_Cancelled_Throws()
    {
        var engine = new SystemSpeechEngine();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.SpeakAsync("你好", null, cts.Token));
    }

    [Fact(DisplayName = "系统语音引擎合成后释放合成器")]
    public async Task SystemSpeech_DisposeAfterSynthesis()
    {
        var engine = new SystemSpeechEngine();
        if (!engine.IsAvailable) return;
        if (engine.GetVoices().Count == 0) return;

        var (pcm, _) = await TtsChunkHelper.CollectAsync(engine, "hello", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(pcm.Length > 0);

        engine.Dispose();
        engine.Dispose();
    }

    [Fact(DisplayName = "系统语音引擎在非 Windows 上拒绝合成")]
    public async Task SystemSpeech_NonWindows_Throws()
    {
        var engine = new SystemSpeechEngine();
        if (engine.IsAvailable) return;

        Assert.Empty(engine.GetVoices());
        await Assert.ThrowsAsync<NotSupportedException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => engine.SpeakAsync("你好", null, TestContext.Current.CancellationToken));
    }
}
