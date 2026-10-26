using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using NewLife.Audio.Tts;
using Xunit;

namespace XUnitTest.Tts;

/// <summary>微软 Azure（Bing）语音合成引擎</summary>
public class AzureTtsEngineTests
{
    private const String DefaultEndpoint = "https://eastasia.tts.speech.microsoft.com/cognitiveservices/v1";

    private static AzureTtsEngine Create(RouteHandler handler, Action<AzureTtsEngine> configure = null)
    {
        var engine = new AzureTtsEngine
        {
            SubscriptionKey = "subscription-key",
            Region = "eastasia",
            Client = new HttpClient(handler),
        };
        configure?.Invoke(engine);

        return engine;
    }

    [Fact(DisplayName = "Azure：缺少密钥或令牌时不可用")]
    public void IsAvailable_RequiresCredentials()
    {
        Assert.False(new AzureTtsEngine().IsAvailable);
        Assert.True(new AzureTtsEngine { SubscriptionKey = "key" }.IsAvailable);
        Assert.True(new AzureTtsEngine { Token = "token" }.IsAvailable);
    }

    [Fact(DisplayName = "Azure：按区域组装地址、发送 SSML 并解码 WAV")]
    public async Task Synthesize_SendsSsmlAndDecodesWav()
    {
        var handler = new RouteHandler()
            .Map("eastasia.tts.speech.microsoft.com", _ => CloudTtsHelper.Audio(TtsTestHelper.BuildWav(16000, 640), "audio/wav"));
        var engine = Create(handler);

        var (pcm, format) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(640, pcm.Length);
        Assert.Equal(16000, format.SampleRate);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("POST", call.Method);
        Assert.Equal(DefaultEndpoint, call.UriText);
        Assert.Equal("application/ssml+xml", call.ContentType);
        Assert.Equal("subscription-key", call.Headers["Ocp-Apim-Subscription-Key"]);
        Assert.Equal("riff-16khz-16bit-mono-pcm", call.Headers["X-Microsoft-OutputFormat"]);
        Assert.Equal("NewLife.Audio", call.Headers["User-Agent"]);

        Assert.Contains("xml:lang='zh-CN'", call.Body);
        Assert.Contains("<voice name='zh-CN-XiaoxiaoNeural'>", call.Body);
        Assert.Contains("rate='+0%'", call.Body);
        Assert.Contains("volume='+0%'", call.Body);
        Assert.Contains(">你好</prosody>", call.Body);
    }

    [Fact(DisplayName = "Azure：语速、音量与音色映射到 SSML")]
    public async Task Synthesize_MapsOptions()
    {
        var handler = new RouteHandler()
            .Map("eastasia.tts.speech.microsoft.com", _ => CloudTtsHelper.Audio(TtsTestHelper.BuildWav(16000, 320), "audio/wav"));
        var engine = Create(handler);

        await TtsChunkHelper.CollectAsync(engine, "hello",
            new TtsOptions { Voice = "en-US-AriaNeural", Rate = 1.2f, Volume = 0.5f },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(handler.Calls);
        Assert.Contains("xml:lang='en-US'", call.Body);
        Assert.Contains("<voice name='en-US-AriaNeural'>", call.Body);
        Assert.Contains("rate='+20%'", call.Body);
        Assert.Contains("volume='-50%'", call.Body);
    }

    [Fact(DisplayName = "Azure：显式语言优先于音色推断")]
    public async Task Synthesize_ExplicitLanguageWins()
    {
        var handler = new RouteHandler()
            .Map("eastasia.tts.speech.microsoft.com", _ => CloudTtsHelper.Audio(TtsTestHelper.BuildWav(16000, 320), "audio/wav"));
        var engine = Create(handler, e => e.Language = "ja-JP");

        await TtsChunkHelper.CollectAsync(engine, "こんにちは", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("xml:lang='ja-JP'", handler.Calls[0].Body);
    }

    [Fact(DisplayName = "Azure：SSML 转义文本中的 XML 特殊字符")]
    public async Task Synthesize_EscapesXml()
    {
        var handler = new RouteHandler()
            .Map("eastasia.tts.speech.microsoft.com", _ => CloudTtsHelper.Audio(TtsTestHelper.BuildWav(16000, 320), "audio/wav"));
        var engine = Create(handler);

        await TtsChunkHelper.CollectAsync(engine, "a<b&c>\"'", cancellationToken: TestContext.Current.CancellationToken);

        var body = handler.Calls[0].Body;
        Assert.Contains("a&lt;b&amp;c&gt;&quot;&apos;", body);
    }

    [Fact(DisplayName = "Azure：自定义地址优先于区域组装")]
    public async Task Synthesize_CustomEndpoint()
    {
        var handler = new RouteHandler()
            .Map("custom.example.com", _ => CloudTtsHelper.Audio(TtsTestHelper.BuildWav(16000, 320), "audio/wav"));
        var engine = Create(handler, e => e.Endpoint = "https://custom.example.com/tts");

        await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("https://custom.example.com/tts", handler.Calls[0].UriText);
    }

    [Fact(DisplayName = "Azure：授权令牌走 Bearer 头")]
    public async Task Synthesize_TokenUsesBearer()
    {
        var handler = new RouteHandler()
            .Map("eastasia.tts.speech.microsoft.com", _ => CloudTtsHelper.Audio(TtsTestHelper.BuildWav(16000, 320), "audio/wav"));
        var engine = Create(handler, e =>
        {
            e.SubscriptionKey = null;
            e.Token = "bearer-token";
        });

        await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("Bearer bearer-token", call.Headers["Authorization"]);
        Assert.False(call.Headers.ContainsKey("Ocp-Apim-Subscription-Key"));
    }

    [Fact(DisplayName = "Azure：MP3 输出格式走整段解码")]
    public async Task Synthesize_Mp3OutputFormat_Decodes()
    {
        var handler = new RouteHandler()
            .Map("eastasia.tts.speech.microsoft.com", _ => CloudTtsHelper.Audio([0xFF, 0xFB, 0x90, 0x00, 0, 0, 0, 0], "audio/mpeg"));
        var engine = Create(handler, e => e.OutputFormat = "audio-24khz-48kbitrate-mono-mp3");

        var (pcm, _) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        // 自带解码器对非标准帧不产出音频，但不能把 MP3 字节当 PCM 透传
        Assert.NotEqual(8, pcm.Length);
    }

    [Fact(DisplayName = "Azure：裸 PCM 输出按格式串推导采样率")]
    public async Task Synthesize_RawPcm_UsesFormatSampleRate()
    {
        var payload = new Byte[320];
        var handler = new RouteHandler()
            .Map("eastasia.tts.speech.microsoft.com", _ => CloudTtsHelper.Audio(payload, "audio/pcm"));
        var engine = Create(handler, e => e.OutputFormat = "raw-24khz-16bit-mono-pcm");

        var (pcm, format) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(payload, pcm);
        Assert.Equal(24000, format.SampleRate);
    }

    [Fact(DisplayName = "Azure：HTTP 错误状态抛出请求异常")]
    public async Task Synthesize_HttpError_Throws()
    {
        var handler = new RouteHandler()
            .Map("eastasia.tts.speech.microsoft.com", _ => CloudTtsHelper.Status(HttpStatusCode.BadRequest));
        var engine = Create(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "Azure：缺少密钥时合成直接拒绝")]
    public async Task Unavailable_Throws()
    {
        await Assert.ThrowsAsync<NotSupportedException>(
            () => TtsChunkHelper.CollectAsync(new AzureTtsEngine(), "你好", cancellationToken: TestContext.Current.CancellationToken));
    }
}
