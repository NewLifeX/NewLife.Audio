using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio.Tts;
using Xunit;

namespace XUnitTest.Tts;

/// <summary>在线 HTTP 引擎的请求模板、解码与错误处理</summary>
public class HttpTtsEngineEdgeCaseTests
{
    private static HttpTtsEngine Create(StubHandler stub, Action<HttpTtsEngine> configure = null)
    {
        var engine = new HttpTtsEngine
        {
            Name = "stub",
            Url = "http://localhost/tts",
            Client = new HttpClient(stub),
        };
        configure?.Invoke(engine);
        return engine;
    }

    private static Byte[] AsciiData(Int32 size)
    {
        var data = new Byte[size];
        for (var i = 0; i < size; i++) data[i] = (Byte)('a' + i % 26);
        return data;
    }

    /// <summary>构造带未知块和扩展 fmt 块的 WAV，验证流式跳块</summary>
    private static Byte[] BuildWavWithExtraChunks(Int32 sampleRate, Byte[] pcm)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + 10 + pcm.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(Encoding.ASCII.GetBytes("LIST"));
        writer.Write(10);
        writer.Write(new Byte[10]);

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(18);
        writer.Write((Int16)1);
        writer.Write((Int16)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((Int16)2);
        writer.Write((Int16)16);
        writer.Write((Int16)0);

        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();

        return ms.ToArray();
    }

    /// <summary>构造块长度为 0 的损坏 WAV，解析放弃后剩余字节按裸 PCM 输出</summary>
    private static Byte[] BuildTruncatedWav(Byte[] payload)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(0);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("junk"));
        writer.Write(0);
        writer.Write(payload);
        writer.Flush();

        return ms.ToArray();
    }

    /// <summary>构造带超长 fmt 块的 WAV，验证扩展字段被读完</summary>
    private static Byte[] BuildWavWithLongFmt(Int32 sampleRate, Byte[] pcm)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + 64 + pcm.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(80);
        writer.Write((Int16)1);
        writer.Write((Int16)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((Int16)2);
        writer.Write((Int16)16);
        writer.Write(new Byte[64]);

        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();

        return ms.ToArray();
    }

    /// <summary>构造未知块长度超过剩余字节的 WAV，解析会中途失败</summary>
    private static Byte[] BuildWavWithBrokenChunk()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(0);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("LIST"));
        writer.Write(100);
        writer.Write(new Byte[10]);
        writer.Flush();

        return ms.ToArray();
    }

    /// <summary>构造只有 fmt 块、没有 data 块的 WAV</summary>
    private static Byte[] BuildWavWithoutData(Int32 sampleRate)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(28);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((Int16)1);
        writer.Write((Int16)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((Int16)2);
        writer.Write((Int16)16);
        writer.Flush();

        return ms.ToArray();
    }

    [Fact(DisplayName = "未配置地址时合成直接拒绝")]
    public async Task MissingUrl_Throws()
    {
        var engine = new HttpTtsEngine();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "服务返回错误状态时抛出 HTTP 异常")]
    public async Task ErrorStatus_Throws()
    {
        var stub = new StubHandler(new Byte[16], "audio/wav") { Status = HttpStatusCode.InternalServerError };
        var engine = Create(stub);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "自定义请求头参与请求")]
    public async Task Headers_Applied()
    {
        var stub = new StubHandler(TtsTestHelper.BuildWav(16000, 320), "audio/wav");
        var engine = Create(stub, e => e.Headers["Authorization"] = "Bearer token");

        await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(stub.LastHeaders.ContainsKey("Authorization"));
        Assert.Equal("Bearer token", stub.LastHeaders["Authorization"]);
    }

    [Fact(DisplayName = "请求方法与请求体类型可配置")]
    public async Task MethodAndContentType_Applied()
    {
        var stub = new StubHandler(TtsTestHelper.BuildWav(16000, 320), "audio/wav");
        var engine = Create(stub, e =>
        {
            e.Method = "GET";
            e.BodyTemplate = "{\"text\":\"{text}\"}";
            e.ContentType = "text/plain";
        });

        await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("GET", stub.LastMethod);
        Assert.Equal("text/plain", stub.LastContentType);
    }

    [Fact(DisplayName = "没有请求体模板时不发送请求体")]
    public async Task NoBodyTemplate_SendsNoContent()
    {
        var stub = new StubHandler(TtsTestHelper.BuildWav(16000, 320), "audio/wav");
        var engine = Create(stub);

        await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(stub.LastBody);
    }

    [Fact(DisplayName = "内容头类请求头落到请求体头上")]
    public async Task Headers_ContentHeader_FallsBackToContent()
    {
        var stub = new StubHandler(TtsTestHelper.BuildWav(16000, 320), "audio/wav");
        var engine = Create(stub, e =>
        {
            e.BodyTemplate = "{\"text\":\"{text}\"}";
            e.Headers["Content-Type"] = "application/json; charset=utf-8";
        });

        await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("application/json", stub.LastContentType);
    }

    [Fact(DisplayName = "请求体模板转义 JSON 特殊字符")]
    public async Task BodyTemplate_EscapesJson()
    {
        var stub = new StubHandler(TtsTestHelper.BuildWav(16000, 320), "audio/wav");
        var engine = Create(stub, e => e.BodyTemplate = "{\"text\":\"{text_json}\"}");

        await TtsChunkHelper.CollectAsync(engine, "a\"b\\c\n\r\t\u0001", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("\\\"", stub.LastBody);
        Assert.Contains("\\\\", stub.LastBody);
        Assert.Contains("\\n", stub.LastBody);
        Assert.Contains("\\r", stub.LastBody);
        Assert.Contains("\\t", stub.LastBody);
        Assert.Contains("\\u0001", stub.LastBody);
    }

    [Fact(DisplayName = "地址模板默认语速为 1")]
    public async Task UrlTemplate_DefaultSpeed()
    {
        var stub = new StubHandler(TtsTestHelper.BuildWav(16000, 320), "audio/wav");
        var engine = Create(stub, e => e.Url = "http://localhost/tts?voice={voice}&speed={speed}");

        await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("speed=1", stub.LastUri.AbsoluteUri);
        Assert.Contains("voice=", stub.LastUri.AbsoluteUri);
    }

    [Fact(DisplayName = "WAV 响应跳过未知块与扩展 fmt 块")]
    public async Task Wav_SkipsUnknownChunks()
    {
        var pcm = AsciiData(1200);
        var stub = new StubHandler(BuildWavWithExtraChunks(22050, pcm), "audio/wav");
        var engine = Create(stub, e => e.ResponseKind = TtsResponseKind.Wav);

        var (data, format) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(pcm, data);
        Assert.Equal(22050, format.SampleRate);
    }

    [Fact(DisplayName = "WAV 头损坏时剩余字节按裸 PCM 输出")]
    public async Task Wav_Malformed_FallsBackToRemainingBytes()
    {
        var payload = AsciiData(400);
        var stub = new StubHandler(BuildTruncatedWav(payload), "audio/wav");
        var engine = Create(stub, e =>
        {
            e.ResponseKind = TtsResponseKind.Wav;
            e.SampleRate = 8000;
        });

        var (pcm, format) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(payload, pcm);
        Assert.Equal(8000, format.SampleRate);
    }

    [Fact(DisplayName = "WAV 超长 fmt 块整体读完再定位 data")]
    public async Task Wav_LongFmtChunk_IsConsumed()
    {
        var pcm = AsciiData(800);
        var stub = new StubHandler(BuildWavWithLongFmt(22050, pcm), "audio/wav");
        var engine = Create(stub, e => e.ResponseKind = TtsResponseKind.Wav);

        var (data, format) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(pcm, data);
        Assert.Equal(22050, format.SampleRate);
    }

    [Fact(DisplayName = "WAV 未知块长度越界时解析放弃且不产出")]
    public async Task Wav_BrokenChunk_YieldsNothing()
    {
        var stub = new StubHandler(BuildWavWithBrokenChunk(), "audio/wav");
        var engine = Create(stub, e => e.ResponseKind = TtsResponseKind.Wav);

        var (pcm, _) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(pcm);
    }

    [Fact(DisplayName = "WAV 只有 fmt 块没有 data 块时不产出")]
    public async Task Wav_NoDataChunk_YieldsNothing()
    {
        var stub = new StubHandler(BuildWavWithoutData(16000), "audio/wav");
        var engine = Create(stub, e => e.ResponseKind = TtsResponseKind.Wav);

        var (pcm, _) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(pcm);
    }

    [Fact(DisplayName = "裸 PCM 声道数取配置值")]
    public async Task RawPcm_UsesConfiguredChannels()
    {
        var stub = new StubHandler(AsciiData(640), "audio/L16");
        var engine = Create(stub, e =>
        {
            e.SampleRate = 8000;
            e.Channels = 2;
        });

        var (pcm, format) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(640, pcm.Length);
        Assert.Equal(2, format.Channels);
        Assert.Equal(8000, format.SampleRate);
    }

    [Fact(DisplayName = "合成响应取消")]
    public async Task Cancelled_Throws()
    {
        var stub = new StubHandler(TtsTestHelper.BuildWav(16000, 320), "audio/wav");
        var engine = Create(stub);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: cts.Token));
    }

    [Fact(DisplayName = "超过单次超时时间时合成取消")]
    public async Task Timeout_Cancels()
    {
        var stub = new StubHandler(TtsTestHelper.BuildWav(16000, 320), "audio/wav")
        {
            Delay = TimeSpan.FromSeconds(30),
        };
        var engine = Create(stub, e => e.TimeoutSeconds = 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "不支持音色枚举时返回空列表")]
    public void GetVoices_Empty()
    {
        Assert.Empty(new HttpTtsEngine { Url = "http://localhost/tts" }.GetVoices());
    }
}
