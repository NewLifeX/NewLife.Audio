using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;

namespace NewLife.Audio.Tts;

/// <summary>微软 Azure（Bing）语音合成引擎</summary>
/// <remarks>
/// 调用 Azure AI Speech 的 REST 合成接口，请求体为 SSML，响应按 <see cref="OutputFormat"/> 返回 WAV 或 MP3。
/// 这是微软官方对外提供的 Bing/Edge 同源神经网络音色接口，需要订阅密钥或授权令牌。
/// <para>参数映射：<see cref="TtsOptions.Rate"/> 与 <see cref="TtsOptions.Volume"/> 通过 SSML prosody 的百分比表达，
/// 1 为原速原音量（<c>+0%</c>）。</para>
/// <para>音色用完整音色名，如 <c>zh-CN-XiaoxiaoNeural</c>；语言默认取音色前两段。</para>
/// </remarks>
/// <example>
/// <code>
/// var tts = new TtsClient(new AzureTtsEngine
/// {
///     SubscriptionKey = "your-key",
///     Region = "eastasia",
///     Voice = "zh-CN-XiaoxiaoNeural",
/// });
/// await tts.SaveAsync("设备A2温度过高", "alert.wav");
/// </code>
/// </example>
public class AzureTtsEngine : CloudTtsEngine
{
    /// <summary>订阅密钥。与 <see cref="Token"/> 二选一，优先使用</summary>
    public String SubscriptionKey { get; set; }

    /// <summary>授权令牌，用于短期凭证场景</summary>
    public String Token { get; set; }

    /// <summary>区域，如 eastasia、eastus；用于组装默认地址</summary>
    public String Region { get; set; } = "eastasia";

    /// <summary>服务地址。为空时按 <see cref="Region"/> 组装</summary>
    public String Endpoint { get; set; }

    /// <summary>默认音色，如 zh-CN-XiaoxiaoNeural</summary>
    public String Voice { get; set; } = "zh-CN-XiaoxiaoNeural";

    /// <summary>SSML 语言，如 zh-CN。为空时取音色前两段</summary>
    public String Language { get; set; }

    /// <summary>输出格式，如 riff-16khz-16bit-mono-pcm、audio-24khz-48kbitrate-mono-mp3</summary>
    public String OutputFormat { get; set; } = "riff-16khz-16bit-mono-pcm";

    /// <summary>User-Agent，便于服务端统计</summary>
    public String UserAgent { get; set; } = "NewLife.Audio";

    /// <summary>初始化 Azure 合成引擎</summary>
    public AzureTtsEngine() : base("azure") { }

    /// <summary>是否可用。需要订阅密钥或授权令牌</summary>
    public override Boolean IsAvailable => !SubscriptionKey.IsNullOrEmpty() || !Token.IsNullOrEmpty();

    /// <summary>合成语音，逐块产出 PCM</summary>
    /// <param name="text">待合成文本</param>
    /// <param name="options">合成选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>PCM 分块异步序列</returns>
    public override async IAsyncEnumerable<TtsChunk> SynthesizeAsync(String text, TtsOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            throw new NotSupportedException(L.T("Microsoft Azure TTS requires a subscription key or an authorization token."));

        using var cts = CreateTimeoutSource(cancellationToken);
        var token = cts.Token;

        var voice = options != null && !options.Voice.IsNullOrEmpty() ? options.Voice : Voice;
        var language = !Language.IsNullOrEmpty() ? Language : GetLanguage(voice);
        var ssml = BuildSsml(text, options, voice, language);

        using var request = new HttpRequestMessage(HttpMethod.Post, GetEndpoint())
        {
            Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml"),
        };
        request.Headers.TryAddWithoutValidation("X-Microsoft-OutputFormat", OutputFormat);
        if (!UserAgent.IsNullOrEmpty()) request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (!SubscriptionKey.IsNullOrEmpty())
            request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", SubscriptionKey);
        else
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);

        using var response = await GetClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await ReadBodyAsync(response).ConfigureAwait(false);
            throw new HttpRequestException(L.T("Microsoft Azure TTS request failed: {0} {1}", (Int32)response.StatusCode, body));
        }

        var format = CreateFormat(ResolveSampleRate());
        await foreach (var chunk in ReadAudioAsync(response, format, ResolveCodec(), token).ConfigureAwait(false))
            yield return chunk;
    }

    private String GetEndpoint()
    {
        if (!Endpoint.IsNullOrEmpty()) return Endpoint;

        var region = Region.IsNullOrEmpty() ? "eastasia" : Region;
        return "https://" + region + ".tts.speech.microsoft.com/cognitiveservices/v1";
    }

    private String BuildSsml(String text, TtsOptions options, String voice, String language)
    {
        var rate = FormatPercent(options?.Rate > 0 ? options.Rate : 1);
        var volume = FormatPercent(options?.Volume ?? 1);

        var sb = new StringBuilder(256);
        sb.Append("<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='").Append(XmlEscape(language)).Append("'>");
        sb.Append("<voice name='").Append(XmlEscape(voice)).Append("'>");
        sb.Append("<prosody rate='").Append(rate).Append("' volume='").Append(volume).Append("'>");
        sb.Append(XmlEscape(text));
        sb.Append("</prosody></voice></speak>");

        return sb.ToString();
    }

    private static String FormatPercent(Single value)
    {
        var percent = (Int32)Math.Round((value - 1) * 100);
        return percent >= 0 ? "+" + percent + "%" : percent + "%";
    }

    private static String GetLanguage(String voice)
    {
        if (voice.IsNullOrEmpty()) return "zh-CN";

        var parts = voice.Split('-');
        return parts.Length >= 2 ? parts[0] + "-" + parts[1] : "zh-CN";
    }

    private Int32 ResolveSampleRate()
    {
        var text = OutputFormat ?? "";
        var index = text.IndexOf("khz", StringComparison.OrdinalIgnoreCase);
        if (index > 0)
        {
            var start = index - 1;
            while (start >= 0 && Char.IsDigit(text[start])) start--;

            if (Int32.TryParse(text.Substring(start + 1, index - start - 1), out var khz) && khz > 0) return khz * 1000;
        }

        return 16000;
    }

    private String ResolveCodec()
    {
        var text = (OutputFormat ?? "").ToLowerInvariant();
        if (text.Contains("mp3")) return "mp3";
        if (text.Contains("riff") || text.Contains("wav")) return "wav";

        return "pcm";
    }

    private static String XmlEscape(String text)
    {
        if (text == null) return "";

        return text.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}
