using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio.Devices;
using NewLife.Audio.DSP;
using NewLife.Audio.Tts;
using NewLife.Audio.Tts.Advanced;
using Xunit;

namespace XUnitTest.Tts;

/// <summary>可控的测试引擎</summary>
internal class FakeTtsEngine : TtsEngineBase
{
    public Int32 Calls;

    public Boolean Fail;

    public Boolean Available = true;

    public Byte[] Payload = new Byte[320];

    public String LastText;

    public TtsOptions LastOptions;

    public FakeTtsEngine(String name) : base(name) { }

    public override Boolean IsAvailable => Available;

    public override async IAsyncEnumerable<TtsChunk> SynthesizeAsync(String text, TtsOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Calls++;
        LastText = text;
        LastOptions = options;
        if (Fail) throw new InvalidOperationException("fake failure");

        await Task.Yield();

        var format = new AudioFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 };
        yield return new TtsChunk { Format = format, Data = Payload };
    }
}

/// <summary>支持直接播报的测试引擎</summary>
internal class FakeSpeakerEngine : FakeTtsEngine, ITtsSpeaker
{
    public String SpokenText;

    public TtsOptions SpokenOptions;

    public Boolean Stopped;

    public FakeSpeakerEngine(String name) : base(name) { }

    public Task SpeakAsync(String text, TtsOptions options, CancellationToken cancellationToken)
    {
        SpokenText = text;
        SpokenOptions = options;
        return Task.CompletedTask;
    }

    public void Stop() => Stopped = true;
}

/// <summary>会一直等待取消的直接播报引擎</summary>
internal class BlockingSpeakerEngine : FakeTtsEngine, ITtsSpeaker
{
    public BlockingSpeakerEngine(String name) : base(name) { }

    public async Task SpeakAsync(String text, TtsOptions options, CancellationToken cancellationToken) =>
        await Task.Delay(Timeout.Infinite, cancellationToken);

    public void Stop() { }
}

/// <summary>不可 seek 的输出流，用于验证流式 WAV 头</summary>
internal class NonSeekableStream : MemoryStream
{
    public override Boolean CanSeek => false;
}

/// <summary>不落地的播放器，用于验证 PCM 回退</summary>
internal class FakeAudioPlayer : IAudioPlayer
{
    public AudioFormat Format;

    public readonly List<Byte> Data = new();

    public Boolean Started;

    public Boolean Stopped;

    public Boolean Disposed;

    public Int32 BufferedBytes => 0;

    public event EventHandler PlaybackStopped;

    public void Init(AudioFormat format, String deviceId = null) => Format = format;

    public void Play() => Started = true;

    public void Pause() { }

    public void Stop()
    {
        Stopped = true;
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    public void Write(Byte[] data) => Data.AddRange(data);

    public void Dispose() => Disposed = true;
}

public class TtsClientTests
{
    private static String TempFile(String ext) => Path.Combine(Path.GetTempPath(), "tts-" + Guid.NewGuid().ToString("N") + ext);

    [Fact(DisplayName = "SayAsync 优先使用引擎的直接播报能力")]
    public async Task SayAsync_UsesDirectSpeaker()
    {
        var engine = new FakeSpeakerEngine("speaker");
        using var tts = new TtsClient(engine);

        await tts.SayAsync("你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("你好", engine.SpokenText);
        Assert.Equal(0, engine.Calls);
    }

    [Fact(DisplayName = "SayAsync 对非直出引擎走 PCM 播放回退")]
    public async Task SayAsync_FallsBackToPcmPlayer()
    {
        var engine = new FakeTtsEngine("fake");
        var player = new FakeAudioPlayer();
        using var tts = new TtsClient(engine) { CreatePlayer = () => player };

        await tts.SayAsync("你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(player.Started);
        Assert.True(player.Stopped);
        Assert.True(player.Disposed);
        Assert.Equal(320, player.Data.Count);
        Assert.Equal(16000, player.Format.SampleRate);
    }

    [Fact(DisplayName = "保存 WAV 文件：头、长度与 PCM 数据正确")]
    public async Task SaveAsync_WritesValidWav()
    {
        var engine = new FakeTtsEngine("fake");
        using var tts = new TtsClient(engine);
        var file = TempFile(".wav");

        try
        {
            var result = await tts.SaveAsync("你好", file, cancellationToken: TestContext.Current.CancellationToken);
            var data = File.ReadAllBytes(file);

            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(data, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(data, 8, 4));
            Assert.Equal("data", System.Text.Encoding.ASCII.GetString(data, 36, 4));
            Assert.Equal(320, BitConverter.ToInt32(data, 40));
            Assert.Equal(44 + 320, data.Length);
            Assert.Equal(320, (Int32)result.Bytes);
            Assert.Equal(file, result.OutputPath);
        }
        finally
        {
            try { File.Delete(file); } catch { /* 忽略 */ }
        }
    }

    [Fact(DisplayName = "保存到可 seek 流时回填 WAV 长度")]
    public async Task SaveAsync_Stream_PatchesSize()
    {
        var engine = new FakeTtsEngine("fake");
        using var tts = new TtsClient(engine);
        using var stream = new MemoryStream();

        await tts.SaveAsync("你好", stream, TtsFormat.Wav, cancellationToken: TestContext.Current.CancellationToken);

        var data = stream.ToArray();
        Assert.Equal(320, BitConverter.ToInt32(data, 40));
        Assert.Equal(44 + 320, data.Length);
    }

    [Fact(DisplayName = "保存到不可 seek 流时使用流式 WAV 头")]
    public async Task SaveAsync_NonSeekable_UsesStreamingHeader()
    {
        var engine = new FakeTtsEngine("fake");
        using var tts = new TtsClient(engine);
        using var stream = new NonSeekableStream();

        await tts.SaveAsync("你好", stream, TtsFormat.Wav, cancellationToken: TestContext.Current.CancellationToken);

        var data = stream.ToArray();
        Assert.Equal(0x7FFFFFFF, BitConverter.ToInt32(data, 40));
        Assert.Equal(44 + 320, data.Length);
    }

    [Fact(DisplayName = "保存裸 PCM 不写容器头")]
    public async Task SaveAsync_Pcm_WritesRawData()
    {
        var engine = new FakeTtsEngine("fake");
        using var tts = new TtsClient(engine);
        using var stream = new MemoryStream();

        await tts.SaveAsync("你好", stream, TtsFormat.Pcm, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(engine.Payload, stream.ToArray());
    }

    [Fact(DisplayName = "不支持的扩展名直接拒绝")]
    public async Task SaveAsync_UnsupportedExtension_Throws()
    {
        using var tts = new TtsClient(new FakeTtsEngine("fake"));

        await Assert.ThrowsAsync<NotSupportedException>(
            () => tts.SaveAsync("你好", TempFile(".mp3"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "StreamAsync 逐块产出 PCM 并携带格式")]
    public async Task StreamAsync_YieldsPcmChunks()
    {
        var engine = new FakeTtsEngine("fake");
        using var tts = new TtsClient(engine);

        var chunks = new List<TtsChunk>();
        await foreach (var chunk in tts.StreamAsync("你好", cancellationToken: TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        Assert.Single(chunks);
        Assert.Equal(320, chunks[0].Data.Length);
        Assert.Equal(16000, chunks[0].Format.SampleRate);
    }

    [Fact(DisplayName = "单次选项正确传到引擎")]
    public async Task Options_AreForwarded()
    {
        var engine = new FakeTtsEngine("fake");
        using var tts = new TtsClient(engine) { Options = new TtsOptions { Voice = "default", Rate = 1.0f } };

        using var stream = new MemoryStream();
        await tts.SaveAsync("你好", stream, TtsFormat.Pcm,
            new TtsOptions { Voice = "custom", Rate = 1.5f, Volume = 0.5f },
            TestContext.Current.CancellationToken);

        Assert.Equal("custom", engine.LastOptions.Voice);
        Assert.Equal(1.5f, engine.LastOptions.Rate);
        Assert.Equal(0.5f, engine.LastOptions.Volume);
    }

    [Fact(DisplayName = "空文本直接拒绝")]
    public async Task EmptyText_Throws()
    {
        using var tts = new TtsClient(new FakeTtsEngine("fake"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => tts.SaveAsync(" ", new MemoryStream(), TtsFormat.Pcm, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "SayAsync 可被 Stop 取消")]
    public async Task SayAsync_Stop_Cancels()
    {
        var engine = new BlockingSpeakerEngine("blocking");
        using var tts = new TtsClient(engine);

        var task = tts.SayAsync("你好", cancellationToken: TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        tts.Stop();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact(DisplayName = "保存裸 PCM 文件按扩展名推断")]
    public async Task SaveAsync_PcmPath_InfersFormat()
    {
        var engine = new FakeTtsEngine("fake");
        using var tts = new TtsClient(engine);
        var file = TempFile(".pcm");

        try
        {
            await tts.SaveAsync("你好", file, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(engine.Payload, File.ReadAllBytes(file));
        }
        finally
        {
            try { File.Delete(file); } catch { /* 忽略 */ }
        }
    }

    [Fact(DisplayName = "缓存装饰器第二次命中缓存")]
    public async Task CachedTtsEngine_HitsCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "tts-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var inner = new FakeTtsEngine("fake");
            using var cached = new CachedTtsEngine(inner, new TtsCache(root));

            var first = new List<TtsChunk>();
            await foreach (var chunk in cached.SynthesizeAsync("温度过高", null, TestContext.Current.CancellationToken))
                first.Add(chunk);

            var second = new List<TtsChunk>();
            await foreach (var chunk in cached.SynthesizeAsync("温度过高", null, TestContext.Current.CancellationToken))
                second.Add(chunk);

            Assert.Equal(1, inner.Calls);
            Assert.Single(first);
            Assert.Single(second);
            Assert.Equal(first[0].Data, second[0].Data);
            Assert.Equal(16000, second[0].Format.SampleRate);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* 忽略 */ }
        }
    }

    [Fact(DisplayName = "缓存装饰器在 SaveAsync 场景第二次命中")]
    public async Task TtsClient_WithCache_HitsOnSecondSave()
    {
        var root = Path.Combine(Path.GetTempPath(), "tts-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var inner = new FakeTtsEngine("fake");
            using var tts = new TtsClient(new CachedTtsEngine(inner, new TtsCache(root)));

            using var first = new MemoryStream();
            await tts.SaveAsync("温度过高", first, TtsFormat.Pcm, cancellationToken: TestContext.Current.CancellationToken);

            using var second = new MemoryStream();
            await tts.SaveAsync("温度过高", second, TtsFormat.Pcm, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, inner.Calls);
            Assert.Equal(first.ToArray(), second.ToArray());
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* 忽略 */ }
        }
    }

    [Fact(DisplayName = "预生成库可批量生成并命中")]
    public async Task PreGenerated_BuildAndTryGet()
    {
        var root = Path.Combine(Path.GetTempPath(), "tts-pregen-" + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = new FakeTtsEngine("fake");
            var pregen = new TtsPreGenerated(engine, new TtsCache(root));
            var built = await pregen.GenerateAsync(["设备离线"], cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, built);
            Assert.True(pregen.TryGet("设备离线", null, out var pcm, out var format));
            Assert.Equal(320, pcm.Length);
            Assert.Equal(16000, format.SampleRate);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* 忽略 */ }
        }
    }

    [Fact(DisplayName = "默认客户端可创建且引擎不为空")]
    public void Default_IsAvailable()
    {
        Assert.NotNull(TtsClient.Default);
        Assert.NotNull(TtsClient.Default.Engine);
    }
}
