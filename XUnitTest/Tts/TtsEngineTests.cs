using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio.DSP;
using NewLife.Audio.Tts;
using Xunit;

namespace XUnitTest.Tts;

/// <summary>固定返回一段音频的 HTTP 桩</summary>
internal class StubHandler : HttpMessageHandler
{
    private readonly Byte[] _data;
    private readonly String _media;

    public String LastBody { get; private set; }

    public Uri LastUri { get; private set; }

    public String LastMethod { get; private set; }

    public String LastContentType { get; private set; }

    public IDictionary<String, String> LastHeaders { get; } = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);

    /// <summary>响应状态码，默认 200</summary>
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>响应前的延时，用于超时与取消测试</summary>
    public TimeSpan Delay { get; set; }

    public StubHandler(Byte[] data, String media)
    {
        _data = data;
        _media = media;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastUri = request.RequestUri;
        LastMethod = request.Method.Method;
        if (request.Content != null)
        {
            LastBody = await request.Content.ReadAsStringAsync();
            LastContentType = request.Content.Headers.ContentType?.MediaType;
        }

        foreach (var item in request.Headers) LastHeaders[item.Key] = String.Join(",", item.Value);
        if (request.Content != null)
        {
            foreach (var item in request.Content.Headers) LastHeaders[item.Key] = String.Join(",", item.Value);
        }

        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);

        var response = new HttpResponseMessage(Status) { Content = new ByteArrayContent(_data) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(_media);
        return response;
    }
}

internal static class TtsChunkHelper
{
    public static async Task<(Byte[] Data, AudioFormat Format)> CollectAsync(ITtsEngine engine, String text, TtsOptions options = null, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream();
        AudioFormat format = null;
        await foreach (var chunk in engine.SynthesizeAsync(text, options, cancellationToken))
        {
            format ??= chunk.Format;
            stream.Write(chunk.Data, 0, chunk.Data.Length);
        }

        return (stream.ToArray(), format);
    }
}

public class NullTtsEngineTests
{
    [Fact(DisplayName = "静音引擎按字长给出等长 PCM")]
    public async Task Synthesize_ProducesSilenceOfExpectedLength()
    {
        var engine = new NullTtsEngine();

        var (pcm, format) = await TtsChunkHelper.CollectAsync(engine, "测试", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(11520, pcm.Length);
        Assert.Equal(16000, format.SampleRate);
    }

    [Fact(DisplayName = "静音引擎始终可用")]
    public void IsAvailable_AlwaysTrue()
    {
        var engine = new NullTtsEngine();

        Assert.True(engine.IsAvailable);
        Assert.Equal("silence", engine.Name);
    }
}

public class HttpTtsEngineTests
{
    [Fact(DisplayName = "自动识别 WAV 响应并剥掉文件头")]
    public async Task Synthesize_Wav_DecodesToPcm()
    {
        var wav = TtsTestHelper.BuildWav(16000, 1600);
        var stub = new StubHandler(wav, "audio/wav");
        var engine = new HttpTtsEngine
        {
            Name = "stub",
            Url = "http://localhost/tts",
            Client = new HttpClient(stub),
        };

        var (pcm, format) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1600, pcm.Length);
        Assert.Equal(16000, format.SampleRate);
    }

    [Fact(DisplayName = "流式 WAV 响应逐块产出，总长一致")]
    public async Task Synthesize_Wav_StreamsChunks()
    {
        var wav = TtsTestHelper.BuildWav(22050, 4096);
        var stub = new StubHandler(wav, "audio/wav");
        var engine = new HttpTtsEngine
        {
            Name = "stub",
            Url = "http://localhost/tts",
            ResponseKind = TtsResponseKind.Wav,
            Client = new HttpClient(stub),
        };

        var (pcm, format) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4096, pcm.Length);
        Assert.Equal(22050, format.SampleRate);
    }

    [Fact(DisplayName = "文本进入 URL 时自动编码，音色与语速进入请求体")]
    public async Task Synthesize_FillsTemplates()
    {
        var wav = TtsTestHelper.BuildWav(16000, 320);
        var stub = new StubHandler(wav, "audio/wav");
        var engine = new HttpTtsEngine
        {
            Name = "stub",
            Url = "http://localhost/tts?tex={text}&voice={voice}",
            Method = "POST",
            BodyTemplate = "{\"text\":\"{text_json}\",\"speed\":{speed}}",
            Client = new HttpClient(stub),
        };

        await TtsChunkHelper.CollectAsync(engine, "温度\"高\"", new TtsOptions { Voice = "v1", Rate = 1.5f }, TestContext.Current.CancellationToken);

        Assert.Contains("%E6%B8%A9%E5%BA%A6", stub.LastUri.AbsoluteUri);
        Assert.Contains("voice=v1", stub.LastUri.AbsoluteUri);
        Assert.Contains("\\\"高\\\"", stub.LastBody);
        Assert.Contains("1.5", stub.LastBody);
    }

    [Fact(DisplayName = "未知响应类型按裸 PCM 处理，采样率取配置值")]
    public async Task Synthesize_UnknownType_TreatsAsRawPcm()
    {
        var stub = new StubHandler(new Byte[800], "application/octet-stream");
        var engine = new HttpTtsEngine
        {
            Name = "stub",
            Url = "http://localhost/tts",
            SampleRate = 24000,
            Client = new HttpClient(stub),
        };

        var (pcm, format) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(800, pcm.Length);
        Assert.Equal(24000, format.SampleRate);
    }

    [Fact(DisplayName = "未配置地址时引擎不可用")]
    public void IsAvailable_RequiresUrl()
    {
        Assert.False(new HttpTtsEngine().IsAvailable);
        Assert.True(new HttpTtsEngine { Url = "http://localhost/x" }.IsAvailable);
    }
}

public class ProcessTtsEngineTests
{
    [Fact(DisplayName = "可执行文件不存在时引擎不可用")]
    public void IsAvailable_FalseWhenMissing()
    {
        var engine = new ProcessTtsEngine("piper", "piper-not-exists-xyz", "--output-raw");

        Assert.False(engine.IsAvailable);
    }

    [Fact(DisplayName = "可执行文件存在时引擎可用")]
    public void IsAvailable_TrueWhenExists()
    {
        var exe = Environment.ProcessPath;
        var engine = new ProcessTtsEngine("self", exe, "");

        Assert.True(engine.IsAvailable);
    }
}

public class SystemSpeechEngineTests
{
    [Fact(DisplayName = "系统语音引擎在非 Windows 上不可用")]
    public void IsAvailable_FalseOnNonWindows()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        Assert.False(new SystemSpeechEngine().IsAvailable);
    }

    [Fact(DisplayName = "系统语音引擎枚举音色不抛异常")]
    public void GetVoices_DoesNotThrow()
    {
        var voices = new SystemSpeechEngine().GetVoices();

        Assert.NotNull(voices);
    }

    [Fact(DisplayName = "Windows 系统语音可合成 PCM")]
    public async Task Synthesize_OnWindows_ProducesPcm()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var engine = new SystemSpeechEngine();
        if (!engine.IsAvailable) return;
        if (engine.GetVoices().Count == 0) return;

        var (pcm, format) = await TtsChunkHelper.CollectAsync(engine, "hello", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(pcm.Length > 0);
        Assert.True(format.SampleRate > 0);
    }
}

public class CommandTtsEngineTests
{
    [Fact(DisplayName = "espeak 引擎探测不抛异常")]
    public void Espeak_Probe_DoesNotThrow()
    {
        var engine = new EspeakTtsEngine();

        Assert.NotNull(engine.GetVoices());
    }

    [Fact(DisplayName = "macOS say 引擎探测不抛异常")]
    public void Say_Probe_DoesNotThrow()
    {
        var engine = new MacSayTtsEngine();

        Assert.NotNull(engine.GetVoices());
    }
}
