using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
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

    public StubHandler(Byte[] data, String media)
    {
        _data = data;
        _media = media;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastUri = request.RequestUri;
        if (request.Content != null) LastBody = await request.Content.ReadAsStringAsync();

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_data) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(_media);
        return response;
    }
}

public class NullTtsEngineTests
{
    [Fact(DisplayName = "静音引擎按字长给出等长 PCM，流式与完整结果一致")]
    public async Task Synthesize_ProducesSilenceOfExpectedLength()
    {
        var engine = new NullTtsEngine();
        var request = new TtsRequest { Text = "测试" };

        var complete = await engine.SynthesizeAsync(request, null, TestContext.Current.CancellationToken);
        var sink = new TtsBufferSink();
        var streaming = await engine.SynthesizeAsync(request, sink, TestContext.Current.CancellationToken);

        Assert.Equal(11520, complete.Pcm.Length);
        Assert.Equal(complete.Pcm.Length, (Int32)sink.Length);
        Assert.Equal(complete.Pcm.Length, (Int32)streaming.PcmBytes);
        Assert.Equal(16000, sink.Format.SampleRate);
    }

    [Fact(DisplayName = "静音引擎始终可用且能力为离线流式")]
    public void Capabilities_AreOfflineStreaming()
    {
        var engine = new NullTtsEngine();

        Assert.True(engine.IsAvailable);
        Assert.True(engine.HasCapability(TtsCapability.Offline));
        Assert.True(engine.HasCapability(TtsCapability.Streaming));
        Assert.Equal(0, engine.Quality);
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

        var result = await engine.SynthesizeAsync(new TtsRequest { Text = "你好" }, null, TestContext.Current.CancellationToken);

        Assert.Equal(1600, result.Pcm.Length);
        Assert.Equal(16000, result.Format.SampleRate);
        Assert.Equal(1600, (Int32)result.PcmBytes);
    }

    [Fact(DisplayName = "流式响应边收边写，块大小与总长一致")]
    public async Task Synthesize_Wav_StreamsToSink()
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

        var sink = new TtsBufferSink();
        var result = await engine.SynthesizeAsync(new TtsRequest { Text = "你好" }, sink, TestContext.Current.CancellationToken);

        Assert.Equal(4096, (Int32)sink.Length);
        Assert.Equal(4096, (Int32)result.PcmBytes);
        Assert.Equal(22050, sink.Format.SampleRate);
        Assert.True(engine.HasCapability(TtsCapability.Streaming));
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

        await engine.SynthesizeAsync(new TtsRequest { Text = "温度\"高\"", Voice = "v1", Speed = 1.5f }, null, TestContext.Current.CancellationToken);

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

        var result = await engine.SynthesizeAsync(new TtsRequest { Text = "你好" }, null, TestContext.Current.CancellationToken);

        Assert.Equal(800, result.Pcm.Length);
        Assert.Equal(24000, result.Format.SampleRate);
    }

    [Fact(DisplayName = "未配置地址时引擎不可用")]
    public void IsAvailable_RequiresUrl()
    {
        Assert.False(new HttpTtsEngine().IsAvailable);
        Assert.True(new HttpTtsEngine { Url = "http://localhost/x" }.IsAvailable);
    }

    [Fact(DisplayName = "流式能力随返回类型自动变化")]
    public void Capabilities_FollowResponseKind()
    {
        var engine = new HttpTtsEngine();

        Assert.False(engine.HasCapability(TtsCapability.Streaming));

        engine.ResponseKind = TtsResponseKind.Wav;
        Assert.True(engine.HasCapability(TtsCapability.Streaming));

        engine.ResponseKind = TtsResponseKind.Mp3;
        Assert.False(engine.HasCapability(TtsCapability.Streaming));
        Assert.True(engine.HasCapability(TtsCapability.Online));
    }
}

public class ProcessTtsEngineTests
{
    [Fact(DisplayName = "可执行文件不存在时引擎不可用")]
    public void IsAvailable_FalseWhenMissing()
    {
        var engine = new ProcessTtsEngine("piper", "piper-not-exists-xyz", "--output-raw");

        Assert.False(engine.IsAvailable);
        Assert.True(engine.HasCapability(TtsCapability.Streaming));
    }

    [Fact(DisplayName = "可执行文件存在时引擎可用")]
    public void IsAvailable_TrueWhenExists()
    {
        var exe = Environment.ProcessPath;
        var engine = new ProcessTtsEngine("self", exe, "");

        Assert.True(engine.IsAvailable);
    }
}
