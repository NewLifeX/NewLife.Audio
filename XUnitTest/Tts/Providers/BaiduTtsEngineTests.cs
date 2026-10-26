using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using NewLife.Audio.Tts;
using Xunit;

namespace XUnitTest.Tts;

/// <summary>百度语音合成引擎</summary>
public class BaiduTtsEngineTests
{
    private const String TokenEndpoint = "https://aip.example.com/oauth/2.0/token";
    private const String SynthesizeEndpoint = "https://tsn.example.com/text2audio";

    private static BaiduTtsEngine Create(RouteHandler handler, Action<BaiduTtsEngine> configure = null)
    {
        var engine = new BaiduTtsEngine
        {
            ApiKey = "api-key",
            SecretKey = "secret-key",
            Endpoint = SynthesizeEndpoint,
            TokenEndpoint = TokenEndpoint,
            Client = new HttpClient(handler),
        };
        configure?.Invoke(engine);

        return engine;
    }

    private static RouteHandler DefaultHandler(Int32 sampleRate = 16000, Int32 dataSize = 640) =>
        new RouteHandler()
            .Map("oauth/2.0/token", _ => CloudTtsHelper.Json("{\"access_token\":\"tok-1\",\"expires_in\":2592000}"))
            .Map("text2audio", _ => CloudTtsHelper.Audio(TtsTestHelper.BuildWav(sampleRate, dataSize), "audio/wav"));

    [Fact(DisplayName = "百度：缺少凭据时不可用")]
    public void IsAvailable_RequiresCredentials()
    {
        Assert.False(new BaiduTtsEngine().IsAvailable);
        Assert.False(new BaiduTtsEngine { ApiKey = "ak" }.IsAvailable);
        Assert.True(new BaiduTtsEngine { ApiKey = "ak", SecretKey = "sk" }.IsAvailable);
        Assert.True(new BaiduTtsEngine { Token = "tok" }.IsAvailable);
    }

    [Fact(DisplayName = "百度：换取令牌、表单参数与 WAV 解码")]
    public async Task Synthesize_ExchangesTokenAndPostsForm()
    {
        var handler = DefaultHandler();
        var engine = Create(handler);

        var (pcm, format) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(640, pcm.Length);
        Assert.Equal(16000, format.SampleRate);

        Assert.Equal(2, handler.Calls.Count);
        var tokenCall = handler.Calls[0];
        Assert.Equal("POST", tokenCall.Method);
        Assert.Contains("grant_type=client_credentials", tokenCall.UriText);
        Assert.Contains("client_id=api-key", tokenCall.UriText);
        Assert.Contains("client_secret=secret-key", tokenCall.UriText);

        var ttsCall = handler.Calls[1];
        Assert.Equal("POST", ttsCall.Method);
        Assert.Equal("application/x-www-form-urlencoded", ttsCall.ContentType);

        var form = CloudTtsHelper.ParseForm(ttsCall.Body);
        Assert.Equal("你好", form["tex"]);
        Assert.Equal("tok-1", form["tok"]);
        Assert.Equal("1", form["ctp"]);
        Assert.Equal("zh", form["lan"]);
        Assert.Equal("5", form["spd"]);
        Assert.Equal("5", form["pit"]);
        Assert.Equal("5", form["vol"]);
        Assert.Equal("0", form["per"]);
        Assert.Equal("6", form["aue"]);
        Assert.Equal("NewLife.Audio", form["cuid"]);
    }

    [Fact(DisplayName = "百度：令牌在有效期内复用")]
    public async Task Synthesize_CachesToken()
    {
        var handler = DefaultHandler();
        var engine = Create(handler);

        await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);
        await TtsChunkHelper.CollectAsync(engine, "再会", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Calls.Count(c => c.UriText.Contains("oauth/2.0/token")));
        Assert.Equal(2, handler.Calls.Count(c => c.UriText.Contains("text2audio")));
    }

    [Fact(DisplayName = "百度：显式令牌跳过换取流程")]
    public async Task Synthesize_ManualToken_SkipsExchange()
    {
        var handler = DefaultHandler();
        var engine = Create(handler, e => e.Token = "manual-token");

        await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(handler.Calls);
        var form = CloudTtsHelper.ParseForm(handler.Calls[0].Body);
        Assert.Equal("manual-token", form["tok"]);
    }

    [Fact(DisplayName = "百度：语速、音量与发音人映射")]
    public async Task Synthesize_MapsOptions()
    {
        var handler = DefaultHandler();
        var engine = Create(handler);

        await TtsChunkHelper.CollectAsync(engine, "你好",
            new TtsOptions { Voice = "3", Rate = 2f, Volume = 0.6f },
            TestContext.Current.CancellationToken);

        var form = CloudTtsHelper.ParseForm(handler.Calls[1].Body);
        Assert.Equal("3", form["per"]);
        Assert.Equal("10", form["spd"]);
        Assert.Equal("3", form["vol"]);
    }

    [Fact(DisplayName = "百度：PCM 编码按采样率选择 16k/8k 通道")]
    public async Task Synthesize_PcmCodec_MapsAue()
    {
        var handler = DefaultHandler();
        var engine = Create(handler, e =>
        {
            e.Codec = "pcm";
            e.SampleRate = 8000;
        });
        await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("5", CloudTtsHelper.ParseForm(handler.Calls[1].Body)["aue"]);

        handler.Calls.Clear();
        engine.SampleRate = 16000;
        await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);
        var call = handler.Calls.Single(c => c.UriText.Contains("text2audio"));
        Assert.Equal("4", CloudTtsHelper.ParseForm(call.Body)["aue"]);
    }

    [Fact(DisplayName = "百度：WAV 头内的采样率优先于配置值")]
    public async Task Synthesize_UsesHeaderSampleRate()
    {
        var handler = DefaultHandler(8000, 320);
        var engine = Create(handler);

        var (pcm, format) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(320, pcm.Length);
        Assert.Equal(8000, format.SampleRate);
    }

    [Fact(DisplayName = "百度：合成错误对象携带错误说明")]
    public async Task Synthesize_ServiceError_Throws()
    {
        var handler = new RouteHandler()
            .Map("oauth/2.0/token", _ => CloudTtsHelper.Json("{\"access_token\":\"tok-1\",\"expires_in\":2592000}"))
            .Map("text2audio", _ => CloudTtsHelper.Json("{\"err_no\":500,\"err_msg\":\"invalid token\",\"sn\":\"x\"}"));
        var engine = Create(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("invalid token", ex.Message);
        Assert.Contains("500", ex.Message);
    }

    [Fact(DisplayName = "百度：令牌换取失败时抛出说明")]
    public async Task TokenFailure_Throws()
    {
        var handler = new RouteHandler()
            .Map("oauth/2.0/token", _ => CloudTtsHelper.Json("{\"error\":\"invalid_client\",\"error_description\":\"unknown client id\"}"));
        var engine = Create(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("unknown client id", ex.Message);
    }

    [Fact(DisplayName = "百度：合成本身返回 HTTP 错误时抛出请求异常")]
    public async Task Synthesize_HttpError_Throws()
    {
        var handler = new RouteHandler()
            .Map("oauth/2.0/token", _ => CloudTtsHelper.Json("{\"access_token\":\"tok-1\",\"expires_in\":2592000}"))
            .Map("text2audio", _ => CloudTtsHelper.Status(HttpStatusCode.BadGateway));
        var engine = Create(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "百度：缺少凭据时合成直接拒绝")]
    public async Task Unavailable_Throws()
    {
        await Assert.ThrowsAsync<NotSupportedException>(
            () => TtsChunkHelper.CollectAsync(new BaiduTtsEngine(), "你好", cancellationToken: TestContext.Current.CancellationToken));
    }
}
