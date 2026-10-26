using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using NewLife.Serialization;

namespace NewLife.Audio.Tts;

/// <summary>阿里云智能语音交互（NLS）合成引擎</summary>
/// <remarks>
/// 走 NLS 的 RESTful 流式合成接口：一次 POST 换回整段 WAV/PCM/MP3，响应流边收边转 PCM。
/// <para>鉴权支持两种方式：直接提供 <see cref="Token"/>（控制台或令牌服务获取，24 小时有效），
/// 或提供 <see cref="AccessKeyId"/>/<see cref="AccessKeySecret"/> 由引擎调用 CreateToken 自动换取并缓存。</para>
/// <para>参数映射：<see cref="TtsOptions.Rate"/> 按 <c>(倍率-1)×500</c> 映射到 speech_rate（-500~500）；
/// <see cref="TtsOptions.Volume"/> 按 <c>×100</c> 映射到 volume（0~100）。</para>
/// </remarks>
/// <example>
/// <code>
/// var tts = new TtsClient(new AliyunTtsEngine
/// {
///     AppKey = "your-appkey",
///     AccessKeyId = "LTAI...",
///     AccessKeySecret = "your-secret",
///     Voice = "xiaoyun",
/// });
/// await tts.SaveAsync("设备A2温度过高", "alert.wav");
/// </code>
/// </example>
public class AliyunTtsEngine : CloudTtsEngine
{
    private readonly Object _tokenLock = new();
    private String _token;
    private Int64 _tokenExpire;

    /// <summary>项目 AppKey（控制台创建项目获得）</summary>
    public String AppKey { get; set; }

    /// <summary>NLS 访问令牌。与 AccessKey 二选一，优先使用</summary>
    public String Token { get; set; }

    /// <summary>阿里云访问密钥 Id，用于自动换取令牌</summary>
    public String AccessKeyId { get; set; }

    /// <summary>阿里云访问密钥 Secret，用于自动换取令牌</summary>
    public String AccessKeySecret { get; set; }

    /// <summary>合成服务地址</summary>
    public String Endpoint { get; set; } = "https://nls-gateway-cn-shanghai.aliyuncs.com/stream/v1/tts";

    /// <summary>令牌服务地址</summary>
    public String TokenEndpoint { get; set; } = "https://nls-meta.cn-shanghai.aliyuncs.com/";

    /// <summary>地域，用于换取令牌</summary>
    public String Region { get; set; } = "cn-shanghai";

    /// <summary>默认音色，如 xiaoyun、aixia</summary>
    public String Voice { get; set; } = "xiaoyun";

    /// <summary>输出编码：wav / pcm / mp3</summary>
    public String Codec { get; set; } = "wav";

    /// <summary>采样率</summary>
    public Int32 SampleRate { get; set; } = 16000;

    /// <summary>音调，范围 -500~500，默认 0</summary>
    public Int32 PitchRate { get; set; }

    /// <summary>初始化阿里云合成引擎</summary>
    public AliyunTtsEngine() : base("aliyun") => Voices = CreateBuiltinVoices();


    /// <summary>是否可用。需要 AppKey，以及 Token 或 AccessKey 对</summary>
    public override Boolean IsAvailable => !AppKey.IsNullOrEmpty() &&
        (!Token.IsNullOrEmpty() || (!AccessKeyId.IsNullOrEmpty() && !AccessKeySecret.IsNullOrEmpty()));

    /// <summary>合成语音，逐块产出 PCM</summary>
    /// <param name="text">待合成文本</param>
    /// <param name="options">合成选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>PCM 分块异步序列</returns>
    public override async IAsyncEnumerable<TtsChunk> SynthesizeAsync(String text, TtsOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            throw new NotSupportedException(L.T("Aliyun TTS requires AppKey and Token, or AppKey with AccessKeyId and AccessKeySecret."));

        using var cts = CreateTimeoutSource(cancellationToken);
        var token = cts.Token;

        var accessToken = await EnsureTokenAsync(token).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(BuildBody(text, options, accessToken), Encoding.UTF8, "application/json"),
        };

        using var response = await GetClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await ReadBodyAsync(response).ConfigureAwait(false);
            throw new HttpRequestException(L.T("Aliyun TTS request failed: {0} {1}", (Int32)response.StatusCode, body));
        }

        var media = response.Content.Headers.ContentType?.MediaType ?? "";
        if (media.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var body = await ReadBodyAsync(response).ConfigureAwait(false);
            throw new InvalidOperationException(L.T("Aliyun TTS service returned an error: {0}", ParseError(body)));
        }

        var format = CreateFormat(SampleRate);
        await foreach (var chunk in ReadAudioAsync(response, format, Codec, token).ConfigureAwait(false))
            yield return chunk;
    }

    private String BuildBody(String text, TtsOptions options, String token)
    {
        var rate = options?.Rate > 0 ? options.Rate : 1;
        var speechRate = (Int32)Math.Round((rate - 1) * 500);
        if (speechRate < -500) speechRate = -500;
        else if (speechRate > 500) speechRate = 500;

        var volume = (Int32)Math.Round((options?.Volume ?? 1) * 100);
        if (volume < 0) volume = 0;
        else if (volume > 100) volume = 100;

        var voice = options != null && !options.Voice.IsNullOrEmpty() ? options.Voice : Voice;

        var sb = new StringBuilder();
        var first = true;
        sb.Append('{');
        TtsJson.AppendField(sb, ref first, "appkey", AppKey);
        TtsJson.AppendField(sb, ref first, "token", token);
        TtsJson.AppendField(sb, ref first, "text", text ?? "");
        TtsJson.AppendField(sb, ref first, "format", Codec);
        TtsJson.AppendField(sb, ref first, "sample_rate", SampleRate);
        TtsJson.AppendField(sb, ref first, "voice", voice);
        TtsJson.AppendField(sb, ref first, "volume", volume);
        TtsJson.AppendField(sb, ref first, "speech_rate", speechRate);
        TtsJson.AppendField(sb, ref first, "pitch_rate", PitchRate);
        sb.Append('}');

        return sb.ToString();
    }

    private async Task<String> EnsureTokenAsync(CancellationToken cancellationToken)
    {
        if (!Token.IsNullOrEmpty()) return Token;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cached = _token;
        if (!cached.IsNullOrEmpty() && _tokenExpire > now + 60) return cached;

        // 并发调用可能重复换取令牌，服务端允许；后到者覆盖，语义不受影响
        var id = await FetchTokenAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    private async Task<String> FetchTokenAsync(CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<String, String>(StringComparer.Ordinal)
        {
            ["AccessKeyId"] = AccessKeyId,
            ["Action"] = "CreateToken",
            ["Format"] = "JSON",
            ["RegionId"] = Region,
            ["SignatureMethod"] = "HMAC-SHA1",
            ["SignatureNonce"] = Guid.NewGuid().ToString("N"),
            ["SignatureVersion"] = "1.0",
            ["Timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["Version"] = "2019-02-28",
        };

        var canonical = CanonicalQuery(parameters);
        var signature = SignTokenRequest(canonical);

        var url = TokenEndpoint;
        if (!url.EndsWith("/")) url += "/";
        url += "?" + canonical + "&Signature=" + PercentEncode(signature);

        using var response = await GetClient().GetAsync(url, cancellationToken).ConfigureAwait(false);
        var body = await ReadBodyAsync(response).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(L.T("Failed to obtain the Aliyun NLS token: {0} {1}", (Int32)response.StatusCode, body));

        IDictionary<String, Object> map;
        try { map = JsonParser.Decode(body); }
        catch { map = null; }

        var tokenMap = GetObject(map, "Token");
        var id = GetString(tokenMap, "Id");
        if (id.IsNullOrEmpty())
            throw new InvalidOperationException(L.T("Failed to obtain the Aliyun NLS token: {0}", ParseError(body)));

        var expire = GetInt64(tokenMap, "ExpireTime");
        lock (_tokenLock)
        {
            _token = id;
            _tokenExpire = expire > 0 ? expire : DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        }

        return id;
    }

    private String SignTokenRequest(String canonical)
    {
        var stringToSign = "GET&" + PercentEncode("/") + "&" + PercentEncode(canonical);
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(AccessKeySecret + "&"));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));

        return Convert.ToBase64String(hash);
    }

    private static String CanonicalQuery(IDictionary<String, String> parameters)
    {
        var sb = new StringBuilder();
        foreach (var item in parameters.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(PercentEncode(item.Key)).Append('=').Append(PercentEncode(item.Value));
        }

        return sb.ToString();
    }

    private static String PercentEncode(String value) => Uri.EscapeDataString(value ?? "")
        .Replace("+", "%20")
        .Replace("*", "%2A")
        .Replace("%7E", "~");

    private static String ParseError(String body)
    {
        if (body.IsNullOrEmpty()) return "unknown error";

        try
        {
            var map = JsonParser.Decode(body);
            var text = GetString(map, "StatusText") ?? GetString(map, "Message") ?? GetString(map, "Code");
            return text.IsNullOrEmpty() ? body : text;
        }
        catch
        {
            return body;
        }
    }

    // 常用音色快照（2026-10 按阿里云语音合成音色列表校对），完整列表见服务文档
    private static List<TtsVoice> CreateBuiltinVoices() =>
    [
        CreateVoice("xiaoyun", "小云", "zh-CN", "Female", true),
        CreateVoice("xiaogang", "小刚", "zh-CN", "Male", false),
        CreateVoice("ruoxi", "若兮", "zh-CN", "Female", false, TtsScenario.Narration),
        CreateVoice("siqi", "思琪", "zh-CN", "Female", false, TtsScenario.Narration),
        CreateVoice("sijia", "思佳", "zh-CN", "Female", false, TtsScenario.Notification),
        CreateVoice("aimei", "艾美", "zh-CN", "Female", false, TtsScenario.Conversation),
        CreateVoice("sitong", "思彤", "zh-CN", "Female", false),
        CreateVoice("shanshan", "姗姗", "zh-HK", "Female", false),
        CreateVoice("emily", "Emily", "en-GB", "Female", false),
    ];
}
