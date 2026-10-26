using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using NewLife.Audio.Tts;
using NewLife.Serialization;
using Xunit;

namespace XUnitTest.Tts;

/// <summary>固定时间戳与会话 Id 的腾讯云引擎，便于复算签名</summary>
internal sealed class FixedTencentEngine : TencentTtsEngine
{
    public Int64 FixedTimestamp { get; set; } = 1700000000;

    public String FixedSession { get; set; } = "session-1";

    protected override Int64 GetTimestamp() => FixedTimestamp;

    protected override String CreateSessionId() => FixedSession;
}

/// <summary>腾讯云语音合成引擎</summary>
public class TencentTtsEngineTests
{
    private const String Endpoint = "https://tts.example.com";

    private static FixedTencentEngine Create(RouteHandler handler, Action<TencentTtsEngine> configure = null)
    {
        var engine = new FixedTencentEngine
        {
            SecretId = "secret-id",
            SecretKey = "secret-key",
            Endpoint = Endpoint,
            Client = new HttpClient(handler),
        };
        configure?.Invoke(engine);

        return engine;
    }

    private static RouteHandler AudioHandler(Int32 sampleRate = 16000, Int32 dataSize = 480) =>
        new RouteHandler().Map("tts.example.com", _ =>
            CloudTtsHelper.Json($"{{\"Response\":{{\"Audio\":\"{CloudTtsHelper.Base64Wav(sampleRate, dataSize)}\",\"RequestId\":\"r\"}}}}"));

    [Fact(DisplayName = "腾讯云：缺少密钥时不可用")]
    public void IsAvailable_RequiresKeys()
    {
        Assert.False(new TencentTtsEngine().IsAvailable);
        Assert.False(new TencentTtsEngine { SecretId = "id" }.IsAvailable);
        Assert.True(new TencentTtsEngine { SecretId = "id", SecretKey = "key" }.IsAvailable);
    }

    [Fact(DisplayName = "腾讯云：请求头、请求体与 base64 音频解码")]
    public async Task Synthesize_SendsSignedRequestAndDecodesBase64()
    {
        var handler = AudioHandler();
        var engine = Create(handler);

        var (pcm, format) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(480, pcm.Length);
        Assert.Equal(16000, format.SampleRate);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("POST", call.Method);
        Assert.Equal("application/json", call.ContentType);
        Assert.Equal("TextToVoice", call.Headers["X-TC-Action"]);
        Assert.Equal("2019-08-23", call.Headers["X-TC-Version"]);
        Assert.Equal("1700000000", call.Headers["X-TC-Timestamp"]);
        Assert.Equal("ap-guangzhou", call.Headers["X-TC-Region"]);
        Assert.StartsWith("TC3-HMAC-SHA256 Credential=secret-id/", call.Headers["Authorization"]);

        var body = JsonParser.Decode(call.Body);
        Assert.Equal("你好", body["Text"]);
        Assert.Equal("session-1", body["SessionId"]);
        Assert.Equal("101001", body["VoiceType"].ToString());
        Assert.Equal("0", body["Speed"].ToString());
        Assert.Equal("0", body["Volume"].ToString());
        Assert.Equal("16000", body["SampleRate"].ToString());
        Assert.Equal("wav", body["Codec"]);
        Assert.Equal("1", body["ModelType"].ToString());
        Assert.Equal("1", body["PrimaryLanguage"].ToString());
    }

    [Fact(DisplayName = "腾讯云：Authorization 签名可被独立复算")]
    public async Task Synthesize_SignatureIsValid()
    {
        var handler = AudioHandler();
        var engine = Create(handler);

        await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        var call = Assert.Single(handler.Calls);
        var expected = BuildAuthorization("secret-id", "secret-key", "tts.example.com", call.Body, 1700000000);

        Assert.Equal(expected, call.Headers["Authorization"]);
    }

    [Fact(DisplayName = "腾讯云：语速、音量与音色编号映射")]
    public async Task Synthesize_MapsOptions()
    {
        var handler = AudioHandler();
        var engine = Create(handler);

        await TtsChunkHelper.CollectAsync(engine, "你好",
            new TtsOptions { Voice = "101002", Rate = 2.5f, Volume = 0.5f },
            TestContext.Current.CancellationToken);

        var body = JsonParser.Decode(handler.Calls[0].Body);
        Assert.Equal("101002", body["VoiceType"].ToString());
        Assert.Equal("6", body["Speed"].ToString());
        Assert.Equal("-5", body["Volume"].ToString());

        handler.Calls.Clear();
        await TtsChunkHelper.CollectAsync(engine, "你好", new TtsOptions { Rate = 0.6f }, TestContext.Current.CancellationToken);
        body = JsonParser.Decode(handler.Calls[0].Body);
        Assert.Equal("-2", body["Speed"].ToString());
    }

    [Fact(DisplayName = "腾讯云：WAV 头内的采样率优先于配置值")]
    public async Task Synthesize_UsesHeaderSampleRate()
    {
        var handler = AudioHandler(24000, 320);
        var engine = Create(handler);

        var (pcm, format) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(320, pcm.Length);
        Assert.Equal(24000, format.SampleRate);
    }

    [Fact(DisplayName = "腾讯云：错误对象携带错误码与说明")]
    public async Task Synthesize_ServiceError_Throws()
    {
        var handler = new RouteHandler().Map("tts.example.com", _ =>
            CloudTtsHelper.Json("{\"Response\":{\"Error\":{\"Code\":\"AuthFailure.SignatureFailure\",\"Message\":\"signature error\"},\"RequestId\":\"r\"}}"));
        var engine = Create(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("AuthFailure.SignatureFailure", ex.Message);
        Assert.Contains("signature error", ex.Message);
    }

    [Fact(DisplayName = "腾讯云：响应不是有效结构时抛出原文")]
    public async Task Synthesize_InvalidPayload_Throws()
    {
        var handler = new RouteHandler().Map("tts.example.com", _ => CloudTtsHelper.Json("{\"Unexpected\":1}"));
        var engine = Create(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Unexpected", ex.Message);
    }

    [Fact(DisplayName = "腾讯云：没有音频字段时抛出说明")]
    public async Task Synthesize_NoAudio_Throws()
    {
        var handler = new RouteHandler().Map("tts.example.com", _ => CloudTtsHelper.Json("{\"Response\":{\"RequestId\":\"r\"}}"));
        var engine = Create(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "腾讯云：base64 音频非法时抛出说明")]
    public async Task Synthesize_InvalidBase64_Throws()
    {
        var handler = new RouteHandler().Map("tts.example.com", _ => CloudTtsHelper.Json("{\"Response\":{\"Audio\":\"***\",\"RequestId\":\"r\"}}"));
        var engine = Create(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "腾讯云：HTTP 错误状态抛出请求异常")]
    public async Task Synthesize_HttpError_Throws()
    {
        var handler = new RouteHandler().Map("tts.example.com", _ => CloudTtsHelper.Status(HttpStatusCode.Unauthorized));
        var engine = Create(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "腾讯云：缺少密钥时合成直接拒绝")]
    public async Task Unavailable_Throws()
    {
        await Assert.ThrowsAsync<NotSupportedException>(
            () => TtsChunkHelper.CollectAsync(new TencentTtsEngine(), "你好", cancellationToken: TestContext.Current.CancellationToken));
    }

    private static String BuildAuthorization(String secretId, String secretKey, String host, String payload, Int64 timestamp)
    {
        var date = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var canonicalRequest = String.Join("\n", new[]
        {
            "POST",
            "/",
            "",
            "content-type:application/json; charset=utf-8",
            "host:" + host,
            "",
            "content-type;host",
            Sha256Hex(payload),
        });

        var scope = date + "/tts/tc3_request";
        var stringToSign = "TC3-HMAC-SHA256\n" + timestamp + "\n" + scope + "\n" + Sha256Hex(canonicalRequest);

        var secretDate = Hmac(Encoding.UTF8.GetBytes("TC3" + secretKey), date);
        var secretService = Hmac(secretDate, "tts");
        var secretSigning = Hmac(secretService, "tc3_request");
        var signature = ToHex(Hmac(secretSigning, stringToSign));

        return "TC3-HMAC-SHA256 Credential=" + secretId + "/" + scope + ", SignedHeaders=content-type;host, Signature=" + signature;
    }

    private static Byte[] Hmac(Byte[] key, String text)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(text));
    }

    private static String Sha256Hex(String text)
    {
        using var sha = SHA256.Create();
        return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    private static String ToHex(Byte[] data)
    {
        var sb = new StringBuilder(data.Length * 2);
        foreach (var item in data) sb.Append(item.ToString("x2"));

        return sb.ToString();
    }
}
