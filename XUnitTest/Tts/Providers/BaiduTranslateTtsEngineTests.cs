using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using NewLife.Audio.Tts;
using Xunit;

namespace XUnitTest.Tts;

/// <summary>百度翻译发音（免密钥）</summary>
public class BaiduTranslateTtsEngineTests
{
    private const String Endpoint = "https://fanyi.example.com/gettts";

    private static BaiduTranslateTtsEngine Create(RouteHandler handler) =>
        new() { Endpoint = Endpoint, Client = new HttpClient(handler) };

    private static RouteHandler AudioHandler() =>
        new RouteHandler().Map("fanyi.example.com", _ => CloudTtsHelper.Audio(BuildSilentMp3(), "audio/mpeg"));

    [Fact(DisplayName = "百度翻译：免密钥接口始终可用")]
    public void IsAvailable_AlwaysTrue()
    {
        Assert.True(new BaiduTranslateTtsEngine().IsAvailable);
    }

    [Fact(DisplayName = "百度翻译：地址参数、浏览器头与 MP3 解码")]
    public async Task Synthesize_SendsQueryAndDecodesMp3()
    {
        var handler = AudioHandler();
        var engine = Create(handler);

        var (pcm, format) = await TtsChunkHelper.CollectAsync(engine, "你好 世界", cancellationToken: TestContext.Current.CancellationToken);

        // 4 帧 × 1152 样本 × 2 声道 × 2 字节
        Assert.Equal(4 * 1152 * 2 * 2, pcm.Length);
        Assert.Equal(44100, format.SampleRate);
        Assert.Equal(2, format.Channels);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("GET", call.Method);
        Assert.Contains("lan=zh", call.UriText);
        Assert.Contains("source=web", call.UriText);
        Assert.Contains("spd=5", call.UriText);
        Assert.Contains(Uri.EscapeDataString("你好 世界"), call.UriText);
        Assert.StartsWith("Mozilla/5.0", call.Headers["User-Agent"]);
        Assert.Equal("https://fanyi.baidu.com/", call.Headers["Referer"]);
    }

    [Fact(DisplayName = "百度翻译：语速倍率映射到 1~15 档")]
    public async Task Synthesize_MapsRate()
    {
        var handler = AudioHandler();
        var engine = Create(handler);

        await TtsChunkHelper.CollectAsync(engine, "你好", new TtsOptions { Rate = 2f }, TestContext.Current.CancellationToken);
        Assert.Contains("spd=10", handler.Calls[0].UriText);

        handler.Calls.Clear();
        await TtsChunkHelper.CollectAsync(engine, "你好", new TtsOptions { Rate = 0.5f }, TestContext.Current.CancellationToken);
        Assert.Contains("spd=3", handler.Calls[0].UriText);

        handler.Calls.Clear();
        await TtsChunkHelper.CollectAsync(engine, "你好", new TtsOptions { Rate = 3f }, TestContext.Current.CancellationToken);
        Assert.Contains("spd=15", handler.Calls[0].UriText);
    }

    [Fact(DisplayName = "百度翻译：JSON 错误响应携带说明")]
    public async Task Synthesize_ErrorJson_Throws()
    {
        var handler = new RouteHandler().Map("fanyi.example.com", _ => CloudTtsHelper.Json("{\"errno\":1001,\"errmsg\":\"service busy\"}"));
        var engine = Create(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("service busy", ex.Message);
    }

    [Fact(DisplayName = "百度翻译：HTTP 错误状态抛出请求异常")]
    public async Task Synthesize_HttpError_Throws()
    {
        var handler = new RouteHandler().Map("fanyi.example.com", _ => CloudTtsHelper.Status(HttpStatusCode.TooManyRequests));
        var engine = Create(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "百度翻译：空响应不产出音频")]
    public async Task Synthesize_EmptyResponse_YieldsNothing()
    {
        var handler = new RouteHandler().Map("fanyi.example.com", _ => CloudTtsHelper.Audio([], "audio/mpeg"));
        var engine = Create(handler);

        var (pcm, _) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(pcm);
    }

    /// <summary>构造标准 MPEG1 Layer III 静音帧（128kbps/44.1kHz/立体声）</summary>
    private static Byte[] BuildSilentMp3(Int32 frames = 4)
    {
        const Int32 frameSize = 417;
        var data = new Byte[frameSize * frames];
        for (var i = 0; i < frames; i++)
        {
            var offset = i * frameSize;
            data[offset] = 0xFF;
            data[offset + 1] = 0xFB;
            data[offset + 2] = 0x90;
            data[offset + 3] = 0x00;
        }

        return data;
    }
}
