using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using NewLife.Serialization;
using NewLife.Audio.Tts;
using Xunit;

namespace XUnitTest.Tts;

/// <summary>阿里云 NLS 合成引擎</summary>
public class AliyunTtsEngineTests
{
    private const String TokenEndpoint = "https://nls-meta.example.com/";
    private const String StreamEndpoint = "https://nls-gateway.example.com/stream/v1/tts";

    private static AliyunTtsEngine Create(RouteHandler handler, Action<AliyunTtsEngine> configure = null)
    {
        var engine = new AliyunTtsEngine
        {
            AppKey = "app-key",
            Token = "static-token",
            Endpoint = StreamEndpoint,
            TokenEndpoint = TokenEndpoint,
            Client = new HttpClient(handler),
        };
        configure?.Invoke(engine);

        return engine;
    }

    [Fact(DisplayName = "阿里云：凭据不足时不可用")]
    public void IsAvailable_RequiresCredentials()
    {
        Assert.False(new AliyunTtsEngine().IsAvailable);
        Assert.False(new AliyunTtsEngine { AppKey = "a" }.IsAvailable);
        Assert.False(new AliyunTtsEngine { AppKey = "a", AccessKeyId = "id" }.IsAvailable);
        Assert.True(new AliyunTtsEngine { AppKey = "a", Token = "t" }.IsAvailable);
        Assert.True(new AliyunTtsEngine { AppKey = "a", AccessKeyId = "id", AccessKeySecret = "s" }.IsAvailable);
    }

    [Fact(DisplayName = "阿里云：请求体字段与 WAV 响应解码")]
    public async Task Synthesize_SendsJsonBodyAndDecodesWav()
    {
        var handler = new RouteHandler()
            .Map("stream/v1/tts", _ => CloudTtsHelper.Audio(TtsTestHelper.BuildWav(16000, 640), "audio/wav"));
        var engine = Create(handler);

        var (pcm, format) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(640, pcm.Length);
        Assert.Equal(16000, format.SampleRate);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("POST", call.Method);
        Assert.Equal("application/json", call.ContentType);
        Assert.Equal(StreamEndpoint, call.UriText);

        var body = JsonParser.Decode(call.Body);
        Assert.Equal("app-key", body["appkey"]);
        Assert.Equal("static-token", body["token"]);
        Assert.Equal("你好", body["text"]);
        Assert.Equal("wav", body["format"]);
        Assert.Equal("16000", body["sample_rate"].ToString());
        Assert.Equal("xiaoyun", body["voice"]);
        Assert.Equal("100", body["volume"].ToString());
        Assert.Equal("0", body["speech_rate"].ToString());
        Assert.Equal("0", body["pitch_rate"].ToString());
    }

    [Fact(DisplayName = "阿里云：语速、音量与音色映射")]
    public async Task Synthesize_MapsOptions()
    {
        var handler = new RouteHandler()
            .Map("stream/v1/tts", _ => CloudTtsHelper.Audio(TtsTestHelper.BuildWav(16000, 320), "audio/wav"));
        var engine = Create(handler);

        await TtsChunkHelper.CollectAsync(engine, "你好",
            new TtsOptions { Voice = "aixia", Rate = 0.5f, Volume = 0.5f },
            TestContext.Current.CancellationToken);

        var body = JsonParser.Decode(handler.Calls[0].Body);
        Assert.Equal("aixia", body["voice"]);
        Assert.Equal("-250", body["speech_rate"].ToString());
        Assert.Equal("50", body["volume"].ToString());

        handler.Calls.Clear();
        await TtsChunkHelper.CollectAsync(engine, "你好", new TtsOptions { Rate = 2f }, TestContext.Current.CancellationToken);
        body = JsonParser.Decode(handler.Calls[0].Body);
        Assert.Equal("500", body["speech_rate"].ToString());
    }

    [Fact(DisplayName = "阿里云：AccessKey 自动换取令牌并缓存")]
    public async Task Synthesize_AutoToken_FetchesAndCaches()
    {
        var expire = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        var handler = new RouteHandler()
            .Map("nls-meta", _ => CloudTtsHelper.Json($"{{\"Token\":{{\"Id\":\"auto-token\",\"ExpireTime\":{expire}}}}}"))
            .Map("stream/v1/tts", _ => CloudTtsHelper.Audio(TtsTestHelper.BuildWav(16000, 320), "audio/wav"));
        var engine = Create(handler, e =>
        {
            e.Token = null;
            e.AccessKeyId = "ak-id";
            e.AccessKeySecret = "ak-secret";
        });

        await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);
        await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        var streamCall = handler.Calls.First(c => c.UriText.Contains("stream/v1/tts"));
        var body = JsonParser.Decode(streamCall.Body);
        Assert.Equal("auto-token", body["token"]);

        Assert.Equal(1, handler.Calls.Count(c => c.UriText.Contains("nls-meta")));
        Assert.Equal(2, handler.Calls.Count(c => c.UriText.Contains("stream/v1/tts")));
    }

    [Fact(DisplayName = "阿里云：令牌请求签名可被独立复算")]
    public async Task Synthesize_TokenRequestSignatureIsValid()
    {
        var expire = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        var handler = new RouteHandler()
            .Map("nls-meta", _ => CloudTtsHelper.Json($"{{\"Token\":{{\"Id\":\"auto-token\",\"ExpireTime\":{expire}}}}}"))
            .Map("stream/v1/tts", _ => CloudTtsHelper.Audio(TtsTestHelper.BuildWav(16000, 320), "audio/wav"));
        var engine = Create(handler, e =>
        {
            e.Token = null;
            e.AccessKeyId = "ak-id";
            e.AccessKeySecret = "ak-secret";
        });

        await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        var tokenCall = handler.Calls.Single(c => c.UriText.Contains("nls-meta"));
        Assert.Equal("GET", tokenCall.Method);

        var query = CloudTtsHelper.ParseQuery(tokenCall.Uri);
        Assert.Equal("CreateToken", query["Action"]);
        Assert.Equal("ak-id", query["AccessKeyId"]);
        Assert.Equal("HMAC-SHA1", query["SignatureMethod"]);
        Assert.True(query.ContainsKey("Signature"));

        var canonical = RebuildCanonical(query);
        var stringToSign = "GET&" + CloudTtsHelper.AliyunEncode("/") + "&" + CloudTtsHelper.AliyunEncode(canonical);
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes("ak-secret&"));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

        Assert.Equal(expected, query["Signature"]);
    }

    [Fact(DisplayName = "阿里云：令牌接口返回错误时抛出说明")]
    public async Task TokenFailure_Throws()
    {
        var handler = new RouteHandler()
            .Map("nls-meta", _ => CloudTtsHelper.Json("{\"Message\":\"invalid access key\",\"Code\":\"InvalidAccessKeyId\"}"));
        var engine = Create(handler, e =>
        {
            e.Token = null;
            e.AccessKeyId = "ak-id";
            e.AccessKeySecret = "ak-secret";
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("invalid access key", ex.Message);
    }

    [Fact(DisplayName = "阿里云：服务端返回 JSON 错误时抛出说明")]
    public async Task Synthesize_ErrorJson_Throws()
    {
        var handler = new RouteHandler()
            .Map("stream/v1/tts", _ => CloudTtsHelper.Json("{\"StatusCode\":40000000,\"StatusText\":\"invalid token\"}"));
        var engine = Create(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("invalid token", ex.Message);
    }

    [Fact(DisplayName = "阿里云：HTTP 错误状态抛出请求异常")]
    public async Task Synthesize_HttpError_Throws()
    {
        var handler = new RouteHandler()
            .Map("stream/v1/tts", _ => CloudTtsHelper.Status(HttpStatusCode.Forbidden));
        var engine = Create(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "阿里云：裸 PCM 编码直接透传")]
    public async Task Synthesize_PcmCodec_PassesThrough()
    {
        var payload = new Byte[320];
        for (var i = 0; i < payload.Length; i++) payload[i] = (Byte)i;

        var handler = new RouteHandler()
            .Map("stream/v1/tts", _ => CloudTtsHelper.Audio(payload, "audio/pcm"));
        var engine = Create(handler, e => e.Codec = "pcm");

        var (pcm, format) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(payload, pcm);
        Assert.Equal(16000, format.SampleRate);
    }

    [Fact(DisplayName = "阿里云：缺少凭据时合成直接拒绝")]
    public async Task Unavailable_Throws()
    {
        var engine = new AliyunTtsEngine { AppKey = "app-key" };

        await Assert.ThrowsAsync<NotSupportedException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));
    }

    private static String RebuildCanonical(IDictionary<String, String> query)
    {
        var sb = new StringBuilder();
        foreach (var item in query.Where(e => e.Key != "Signature").OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(CloudTtsHelper.AliyunEncode(item.Key)).Append('=').Append(CloudTtsHelper.AliyunEncode(item.Value));
        }

        return sb.ToString();
    }
}
