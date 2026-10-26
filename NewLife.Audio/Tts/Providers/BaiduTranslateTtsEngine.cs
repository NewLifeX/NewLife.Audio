using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;

namespace NewLife.Audio.Tts;

/// <summary>百度翻译发音引擎（免费、无需密钥）</summary>
/// <remarks>
/// 调用百度翻译网页使用的发音接口，返回 16kHz 单声道 MP3，再由库内 MP3 解码器转 PCM。
/// 适合无云账号的演示与低用量播报，属于<b>非官方接口</b>，服务端可能随时调整或限流；
/// 生产环境建议改用需要密钥的 <see cref="BaiduTtsEngine"/>。
/// <para>参数映射：<see cref="TtsOptions.Rate"/> 按 <c>5+(倍率-1)×5</c> 映射到 spd（1~15，5 为常速）；
/// 该接口不支持音量。</para>
/// </remarks>
/// <example>
/// <code>
/// var tts = new TtsClient(new BaiduTranslateTtsEngine());
/// await tts.SaveAsync("设备A2温度过高", "alert.wav");
/// </code>
/// </example>
public class BaiduTranslateTtsEngine : CloudTtsEngine
{
    /// <summary>合成服务地址</summary>
    public String Endpoint { get; set; } = "https://fanyi.baidu.com/gettts";

    /// <summary>语言，zh 为中文，en 为英文</summary>
    public String Language { get; set; } = "zh";

    /// <summary>User-Agent。接口要求浏览器标识</summary>
    public String UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    /// <summary>Referer。接口要求来源页</summary>
    public String Referer { get; set; } = "https://fanyi.baidu.com/";

    /// <summary>初始化百度翻译发音引擎</summary>
    public BaiduTranslateTtsEngine() : base("baidu-web") { }

    /// <summary>是否可用。免密钥接口恒为 true，实际可用性取决于网络与服务端策略</summary>
    public override Boolean IsAvailable => true;

    /// <summary>合成语音，产出整段 PCM</summary>
    /// <param name="text">待合成文本</param>
    /// <param name="options">合成选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>PCM 分块异步序列</returns>
    public override async IAsyncEnumerable<TtsChunk> SynthesizeAsync(String text, TtsOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cts = CreateTimeoutSource(cancellationToken);
        var token = cts.Token;

        var url = Endpoint + "?lan=" + Uri.EscapeDataString(Language ?? "zh") +
            "&text=" + Uri.EscapeDataString(text ?? "") +
            "&spd=" + MapSpeed(options != null && options.Rate > 0 ? options.Rate : 1).ToString() +
            "&source=web";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!UserAgent.IsNullOrEmpty()) request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (!Referer.IsNullOrEmpty()) request.Headers.TryAddWithoutValidation("Referer", Referer);

        using var response = await GetClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadBodyAsync(response).ConfigureAwait(false);
            throw new HttpRequestException(L.T("Baidu translate TTS request failed: {0} {1}", (Int32)response.StatusCode, error));
        }

        var data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        var media = response.Content.Headers.ContentType?.MediaType ?? "";
        if (media.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0 || (data.Length > 0 && data[0] == '{'))
            throw new InvalidOperationException(L.T("Baidu translate TTS service returned an error: {0}", ParseError(data)));

        var format = CreateFormat(16000);
        var chunk = DecodeAudio(data, format, "mp3");
        if (chunk != null) yield return chunk;
    }

    private static Int32 MapSpeed(Single rate)
    {
        if (rate <= 0) rate = 1;

        var speed = (Int32)Math.Round(5 + (rate - 1) * 5, MidpointRounding.AwayFromZero);
        if (speed < 1) speed = 1;
        else if (speed > 15) speed = 15;

        return speed;
    }

    private static String ParseError(Byte[] data)
    {
        if (data == null || data.Length == 0) return "unknown error";

        var text = Encoding.UTF8.GetString(data);
        try
        {
            var map = NewLife.Serialization.JsonParser.Decode(text);
            var message = GetString(map, "errmsg") ?? GetString(map, "error_msg") ?? GetString(map, "err_msg");
            if (!message.IsNullOrEmpty()) return message;
        }
        catch
        {
            // 非 JSON 时退回原文片段
        }

        return text.Length > 200 ? text.Substring(0, 200) : text;
    }
}
