using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio.Tts;
using Xunit;

namespace XUnitTest.Tts;

/// <summary>TtsClient 边界、统计与生命周期</summary>
public class TtsClientEdgeCaseTests
{
    private static String TempFile(String ext) => Path.Combine(Path.GetTempPath(), "tts-" + Guid.NewGuid().ToString("N") + ext);

    private static async Task DrainAsync(IAsyncEnumerable<TtsChunk> source)
    {
        await foreach (var _ in source) { }
    }

    [Fact(DisplayName = "GetVoices 透传引擎音色")]
    public void GetVoices_ReturnsEngineVoices()
    {
        var engine = new FakeTtsEngine("fake")
        {
            Voices = new List<TtsVoice> { new() { Name = "Huihui", Culture = "zh-CN" } },
        };
        using var tts = new TtsClient(engine);

        var voices = tts.GetVoices();

        Assert.Single(voices);
        Assert.Equal("Huihui", voices[0].Name);
    }

    [Fact(DisplayName = "GetVoices 在引擎抛异常时返回空列表")]
    public void GetVoices_EngineThrow_ReturnsEmpty()
    {
        var engine = new FakeTtsEngine("fake") { VoicesThrow = true };
        using var tts = new TtsClient(engine);

        Assert.Empty(tts.GetVoices());
    }

    [Fact(DisplayName = "GetVoices 在未指定引擎时返回空列表")]
    public void GetVoices_NullEngine_ReturnsEmpty()
    {
        using var tts = new TtsClient(new FakeTtsEngine("fake")) { Engine = null };

        Assert.Empty(tts.GetVoices());
        Assert.Null(tts.Engine);
    }

    [Fact(DisplayName = "保存文件时路径为空直接拒绝")]
    public async Task SaveAsync_NullPath_Throws()
    {
        using var tts = new TtsClient(new FakeTtsEngine("fake"));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => tts.SaveAsync("你好", (String)null, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => tts.SaveAsync("你好", "", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "保存到空输出流直接拒绝")]
    public async Task SaveAsync_NullStream_Throws()
    {
        using var tts = new TtsClient(new FakeTtsEngine("fake"));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => tts.SaveAsync("你好", (Stream)null, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "保存文件时空文本直接拒绝")]
    public async Task SaveAsync_EmptyText_PathOverload_Throws()
    {
        using var tts = new TtsClient(new FakeTtsEngine("fake"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => tts.SaveAsync("", TempFile(".wav"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "引擎不可用时三个主入口都拒绝")]
    public async Task UnavailableEngine_AllEntries_Throw()
    {
        var engine = new FakeTtsEngine("fake") { Available = false };
        using var tts = new TtsClient(engine);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => tts.SaveAsync("你好", new MemoryStream(), cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => tts.SayAsync("你好", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => DrainAsync(tts.StreamAsync("你好", cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact(DisplayName = "引擎没有产出音频时报错")]
    public async Task SaveAsync_NoAudio_Throws()
    {
        var engine = new FakeTtsEngine("fake") { Payload = [] };
        using var tts = new TtsClient(engine);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tts.SaveAsync("你好", new MemoryStream(), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "空块被跳过，不计入字节数")]
    public async Task SaveAsync_SkipsEmptyChunks()
    {
        var engine = new FakeTtsEngine("fake")
        {
            Chunks = new List<Byte[]> { Array.Empty<Byte>(), null, new Byte[320] },
        };
        using var tts = new TtsClient(engine);
        using var stream = new MemoryStream();

        var result = await tts.SaveAsync("你好", stream, TtsFormat.Wav, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(320, result.Bytes);
        Assert.Equal(44 + 320, stream.ToArray().Length);
    }

    [Fact(DisplayName = "多块合成按总字节统计时长与实时率")]
    public async Task SaveAsync_MultiChunk_AggregatesStats()
    {
        var engine = new FakeTtsEngine("fake")
        {
            Chunks = new List<Byte[]> { new Byte[320], new Byte[480] },
        };
        using var tts = new TtsClient(engine);
        using var stream = new MemoryStream();

        var result = await tts.SaveAsync("你好", stream, TtsFormat.Pcm, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("fake", result.Engine);
        Assert.Equal(800, result.Bytes);
        Assert.Equal(16000, result.Format.SampleRate);
        Assert.Equal(0.025, result.AudioTime, 6);
        Assert.True(result.ElapsedTime > 0);
        Assert.True(result.FirstChunkTime > 0);
        Assert.True(result.FirstChunkTime <= result.ElapsedTime);
        Assert.True(result.Rtf >= 0);
        Assert.Contains("16000Hz", result.ToString());
        Assert.Null(result.OutputPath);
    }

    [Fact(DisplayName = "语速与音量越界时收敛到合法区间")]
    public async Task SaveAsync_ClampsOptions()
    {
        var engine = new FakeTtsEngine("fake");
        using var tts = new TtsClient(engine)
        {
            Options = new TtsOptions { Voice = "v", Rate = 0, Volume = -0.5f },
        };

        using (var stream = new MemoryStream())
            await tts.SaveAsync("你好", stream, TtsFormat.Pcm, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("v", engine.LastOptions.Voice);
        Assert.Equal(1f, engine.LastOptions.Rate);
        Assert.Equal(0f, engine.LastOptions.Volume);

        using (var stream = new MemoryStream())
            await tts.SaveAsync("你好", stream, TtsFormat.Pcm, new TtsOptions { Volume = 1.5f }, TestContext.Current.CancellationToken);

        Assert.Equal(1f, engine.LastOptions.Volume);
    }

    [Fact(DisplayName = "默认选项为空时回落到标准值")]
    public async Task SaveAsync_NullOptions_UsesDefaults()
    {
        var engine = new FakeTtsEngine("fake");
        using var tts = new TtsClient(engine) { Options = null };

        using var stream = new MemoryStream();
        await tts.SaveAsync("你好", stream, TtsFormat.Pcm, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(engine.LastOptions);
        Assert.Equal(1f, engine.LastOptions.Rate);
        Assert.Equal(1f, engine.LastOptions.Volume);
    }

    [Fact(DisplayName = "单次选项与默认选项互不影响")]
    public async Task SaveAsync_PerCallOptions_LeavesDefaults()
    {
        var engine = new FakeTtsEngine("fake");
        using var tts = new TtsClient(engine) { Options = new TtsOptions { Voice = "default", Rate = 1.2f } };

        using (var stream = new MemoryStream())
            await tts.SaveAsync("你好", stream, TtsFormat.Pcm, new TtsOptions { Voice = "custom", Rate = 2f }, TestContext.Current.CancellationToken);

        Assert.Equal("custom", engine.LastOptions.Voice);
        Assert.Equal("default", tts.Options.Voice);
        Assert.Equal(1.2f, tts.Options.Rate);
    }

    [Fact(DisplayName = "构造函数选项作为默认值生效")]
    public async Task Constructor_WithOptions_UsesThem()
    {
        var engine = new FakeTtsEngine("fake");
        using var tts = new TtsClient(engine, new TtsOptions { Voice = "ctor", Rate = 1.25f });

        using var stream = new MemoryStream();
        await tts.SaveAsync("你好", stream, TtsFormat.Pcm, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("ctor", engine.LastOptions.Voice);
        Assert.Equal(1.25f, engine.LastOptions.Rate);
    }

    [Fact(DisplayName = "扩展名大小写与 wave 别名都能推断 WAV")]
    public async Task SaveAsync_PathExtension_InfersWav()
    {
        var engine = new FakeTtsEngine("fake");
        using var tts = new TtsClient(engine);

        foreach (var ext in new[] { ".wave", ".WAV" })
        {
            var file = TempFile(ext);
            try
            {
                var result = await tts.SaveAsync("你好", file, cancellationToken: TestContext.Current.CancellationToken);
                var data = File.ReadAllBytes(file);

                Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(data, 0, 4));
                Assert.Equal(file, result.OutputPath);
            }
            finally
            {
                try { File.Delete(file); } catch { /* 忽略 */ }
            }
        }
    }

    [Fact(DisplayName = "多块合成的 WAV 长度被正确回填")]
    public async Task SaveAsync_WavHeaderPatched_WithTotalBytes()
    {
        var engine = new FakeTtsEngine("fake")
        {
            Chunks = new List<Byte[]> { new Byte[320], new Byte[480] },
        };
        using var tts = new TtsClient(engine);
        using var stream = new MemoryStream();

        await tts.SaveAsync("你好", stream, TtsFormat.Wav, cancellationToken: TestContext.Current.CancellationToken);

        var data = stream.ToArray();
        Assert.Equal(800, BitConverter.ToInt32(data, 40));
        Assert.Equal(44 + 800, data.Length);
    }

    [Fact(DisplayName = "块未携带格式时按默认 8kHz 写出")]
    public async Task SaveAsync_ChunkWithoutFormat_UsesDefaultFormat()
    {
        var engine = new FakeTtsEngine("fake") { Format = null };
        using var tts = new TtsClient(engine);
        using var stream = new MemoryStream();

        var result = await tts.SaveAsync("你好", stream, TtsFormat.Wav, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(8000, result.Format.SampleRate);
        Assert.Equal(8000, BitConverter.ToInt32(stream.ToArray(), 24));
    }

    [Fact(DisplayName = "释放后所有入口都抛 ObjectDisposedException")]
    public async Task Dispose_BlocksFurtherCalls()
    {
        var engine = new FakeTtsEngine("fake");
        var tts = new TtsClient(engine);

        tts.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => tts.SaveAsync("你好", TempFile(".wav"), cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => tts.SaveAsync("你好", new MemoryStream(), cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => tts.SayAsync("你好", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => DrainAsync(tts.StreamAsync("你好", cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact(DisplayName = "重复释放安全且会释放引擎")]
    public void Dispose_IsIdempotent_AndDisposesEngine()
    {
        var engine = new FakeTtsEngine("fake");
        var tts = new TtsClient(engine);

        tts.Dispose();
        tts.Dispose();

        Assert.True(engine.Disposed);
    }

    [Fact(DisplayName = "缺少播放器工厂时直接播报报错")]
    public async Task SayAsync_NoPlayerFactory_Throws()
    {
        using var tts = new TtsClient(new FakeTtsEngine("fake")) { CreatePlayer = null };

        await Assert.ThrowsAsync<NotSupportedException>(
            () => tts.SayAsync("你好", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "播放器工厂返回空时直接播报报错")]
    public async Task SayAsync_PlayerFactoryReturnsNull_Throws()
    {
        using var tts = new TtsClient(new FakeTtsEngine("fake")) { CreatePlayer = () => null };

        await Assert.ThrowsAsync<NotSupportedException>(
            () => tts.SayAsync("你好", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "PCM 回退没有产出音频时报错并释放播放器")]
    public async Task SayAsync_NoAudio_Throws()
    {
        var player = new FakeAudioPlayer();
        var engine = new FakeTtsEngine("fake") { Payload = [] };
        using var tts = new TtsClient(engine) { CreatePlayer = () => player };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tts.SayAsync("你好", cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(player.Disposed);
    }

    [Fact(DisplayName = "PCM 回退中引擎失败时释放播放器")]
    public async Task SayAsync_EngineFailure_DisposesPlayer()
    {
        var player = new FakeAudioPlayer();
        var engine = new FakeTtsEngine("fake") { Fail = true };
        using var tts = new TtsClient(engine) { CreatePlayer = () => player };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tts.SayAsync("你好", cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(player.Disposed);
    }

    [Fact(DisplayName = "PCM 回退写入全部音频块")]
    public async Task SayAsync_MultiChunk_WritesAllPcm()
    {
        var player = new FakeAudioPlayer();
        var engine = new FakeTtsEngine("fake")
        {
            Chunks = new List<Byte[]> { new Byte[320], new Byte[480] },
        };
        using var tts = new TtsClient(engine) { CreatePlayer = () => player };

        await tts.SayAsync("你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(800, player.Data.Count);
        Assert.Equal(16000, player.Format.SampleRate);
        Assert.True(player.Started);
        Assert.True(player.Disposed);
    }

    [Fact(DisplayName = "Stop 打断 PCM 播放回退")]
    public async Task SayAsync_Stop_InterruptsPcmPlayback()
    {
        var player = new FakeAudioPlayer();
        var engine = new SlowTtsEngine { Chunks = 100, DelayMs = 20 };
        using var tts = new TtsClient(engine) { CreatePlayer = () => player };

        var task = tts.SayAsync("你好", cancellationToken: TestContext.Current.CancellationToken);
        await TestWait.UntilAsync(() => player.Started, "PCM 播放未启动");

        tts.Stop();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(player.Disposed);
    }

    [Fact(DisplayName = "新的播报会打断上一句")]
    public async Task SayAsync_SecondCall_CancelsFirst()
    {
        var engine = new InterruptibleSpeakerEngine("speaker");
        using var tts = new TtsClient(engine);

        var first = tts.SayAsync("第一句", cancellationToken: TestContext.Current.CancellationToken);
        await TestWait.UntilAsync(() => engine.Speaks > 0, "首次播报未开始");

        await tts.SayAsync("第二句", cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(2, engine.Speaks);
    }

    [Fact(DisplayName = "直接播报把单次选项传给引擎")]
    public async Task SayAsync_ForwardsOptions()
    {
        var engine = new FakeSpeakerEngine("speaker");
        using var tts = new TtsClient(engine);

        await tts.SayAsync("你好", new TtsOptions { Voice = "v2", Rate = 1.5f, Volume = 0.25f }, TestContext.Current.CancellationToken);

        Assert.Equal("v2", engine.SpokenOptions.Voice);
        Assert.Equal(1.5f, engine.SpokenOptions.Rate);
        Assert.Equal(0.25f, engine.SpokenOptions.Volume);
    }

    [Fact(DisplayName = "流式输出跳过空块")]
    public async Task StreamAsync_SkipsEmptyChunks()
    {
        var engine = new FakeTtsEngine("fake")
        {
            Chunks = new List<Byte[]> { Array.Empty<Byte>(), null, new Byte[320], Array.Empty<Byte>() },
        };
        using var tts = new TtsClient(engine);

        var chunks = new List<TtsChunk>();
        await foreach (var chunk in tts.StreamAsync("你好", cancellationToken: TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        Assert.Single(chunks);
        Assert.Equal(320, chunks[0].Data.Length);
    }

    [Fact(DisplayName = "流式输出把单次选项传给引擎")]
    public async Task StreamAsync_ForwardsOptions()
    {
        var engine = new FakeTtsEngine("fake");
        using var tts = new TtsClient(engine);

        await foreach (var _ in tts.StreamAsync("你好", new TtsOptions { Voice = "v3" }, TestContext.Current.CancellationToken)) { }

        Assert.Equal("v3", engine.LastOptions.Voice);
    }

    [Fact(DisplayName = "流式输出响应取消")]
    public async Task StreamAsync_CancelledToken_Throws()
    {
        using var tts = new TtsClient(new NullTtsEngine());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DrainAsync(tts.StreamAsync("你好", cancellationToken: cts.Token)));
    }

    [Fact(DisplayName = "没有播放任务时 Stop 也不抛异常")]
    public void Stop_WithNoPlayback_DoesNotThrow()
    {
        using var tts = new TtsClient(new FakeTtsEngine("fake"));

        tts.Stop();
        tts.Stop();
    }

    [Fact(DisplayName = "默认引擎与当前平台匹配")]
    public void DefaultEngine_MatchesPlatform()
    {
        var engine = TtsClient.Default.Engine;

        Assert.NotNull(engine);
        if (OperatingSystem.IsWindows())
            Assert.Contains(engine.Name, new[] { "system", "espeak" });
        else if (OperatingSystem.IsMacOS())
            Assert.Equal("say", engine.Name);
        else
            Assert.Equal("espeak", engine.Name);
    }
}
