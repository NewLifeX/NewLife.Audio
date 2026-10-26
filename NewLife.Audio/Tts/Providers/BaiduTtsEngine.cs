using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using NewLife.Serialization;

namespace NewLife.Audio.Tts;

/// <summary>百度语音合成引擎</summary>
/// <remarks>
/// 先通过 OAuth 用 API Key / Secret Key 换取 access_token 并缓存，再调用 <c>text2audio</c> 合成。
/// 成功时直接返回音频字节流，失败时返回 JSON 错误对象。
/// <para>参数映射：<see cref="TtsOptions.Rate"/> 按 <c>5+(倍率-1)×5</c> 映射到 spd（0~15，5 为常速）；
/// <see cref="TtsOptions.Volume"/> 按 <c>×5</c> 映射到 vol（0~15，5 为常音量）。</para>
/// </remarks>
/// <example>
/// <code>
/// var tts = new TtsClient(new BaiduTtsEngine
/// {
///     ApiKey = "your-api-key",
///     SecretKey = "your-secret-key",
///     Person = 0,
/// });
/// await tts.SaveAsync("设备A2温度过高", "alert.wav");
/// </code>
/// </example>
public class BaiduTtsEngine : CloudTtsEngine
{
    private readonly Object _tokenLock = new();
    private String _token;
    private Int64 _tokenExpire;

    /// <summary>应用 API Key</summary>
    public String ApiKey { get; set; }

    /// <summary>应用 Secret Key</summary>
    public String SecretKey { get; set; }

    /// <summary>access_token。与 API Key/Secret Key 二选一，优先使用</summary>
    public String Token { get; set; }

    /// <summary>合成服务地址</summary>
    public String Endpoint { get; set; } = "https://tsn.baidu.com/text2audio";

    /// <summary>令牌服务地址</summary>
    public String TokenEndpoint { get; set; } = "https://aip.baidubce.com/oauth/2.0/token";

    /// <summary>默认发音人编号，如 0 度小美、1 度小宇、3 度逍遥、4 度丫丫</summary>
    public Int32 Person { get; set; }

    /// <summary>语言，zh 为中文</summary>
    public String Language { get; set; } = "zh";

    /// <summary>输出编码：wav / pcm / mp3</summary>
    public String Codec { get; set; } = "wav";

    /// <summary>PCM 编码时的采样率，8000 走 8k 通道，其余走 16k</summary>
    public Int32 SampleRate { get; set; } = 16000;

    /// <summary>终端标识，用于区分调用方</summary>
    public String Cuid { get; set; } = "NewLife.Audio";

    /// <summary>默认音调，0~15，5 为正常</summary>
    public Int32 Pitch { get; set; } = 5;

    /// <summary>初始化百度合成引擎</summary>
    public BaiduTtsEngine() : base("baidu") => Voices = CreateBuiltinVoices();

    /// <summary>是否可用。需要 access_token，或 API Key/Secret Key 对</summary>
    public override Boolean IsAvailable => !Token.IsNullOrEmpty() || (!ApiKey.IsNullOrEmpty() && !SecretKey.IsNullOrEmpty());

    /// <summary>合成语音，逐块产出 PCM</summary>
    /// <param name="text">待合成文本</param>
    /// <param name="options">合成选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>PCM 分块异步序列</returns>
    public override async IAsyncEnumerable<TtsChunk> SynthesizeAsync(String text, TtsOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            throw new NotSupportedException(L.T("Baidu TTS requires an access token, or ApiKey and SecretKey."));

        using var cts = CreateTimeoutSource(cancellationToken);
        var token = cts.Token;

        var accessToken = await EnsureTokenAsync(token).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new FormUrlEncodedContent(BuildForm(text, options, accessToken)),
        };

        using var response = await GetClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        var media = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!response.IsSuccessStatusCode)
        {
            var body = await ReadBodyAsync(response).ConfigureAwait(false);
            throw new HttpRequestException(L.T("Baidu TTS request failed: {0} {1}", (Int32)response.StatusCode, body));
        }

        if (media.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var body = await ReadBodyAsync(response).ConfigureAwait(false);
            throw new InvalidOperationException(L.T("Baidu TTS service returned an error: {0}", ParseError(body)));
        }

        var format = CreateFormat(SampleRate);
        await foreach (var chunk in ReadAudioAsync(response, format, Codec, token).ConfigureAwait(false))
            yield return chunk;
    }

    private IDictionary<String, String> BuildForm(String text, TtsOptions options, String token)
    {
        var rate = options?.Rate > 0 ? options.Rate : 1;
        var speed = (Int32)Math.Round(5 + (rate - 1) * 5);
        if (speed < 0) speed = 0;
        else if (speed > 15) speed = 15;

        var volume = (Int32)Math.Round((options?.Volume ?? 1) * 5);
        if (volume < 0) volume = 0;
        else if (volume > 15) volume = 15;

        var person = Person;
        if (options != null && !options.Voice.IsNullOrEmpty() && Int32.TryParse(options.Voice, out var value)) person = value;

        return new Dictionary<String, String>(StringComparer.Ordinal)
        {
            ["tex"] = text ?? "",
            ["tok"] = token,
            ["cuid"] = Cuid ?? "NewLife.Audio",
            ["ctp"] = "1",
            ["lan"] = Language,
            ["spd"] = speed.ToString(),
            ["pit"] = Pitch.ToString(),
            ["vol"] = volume.ToString(),
            ["per"] = person.ToString(),
            ["aue"] = ResolveAue().ToString(),
        };
    }

    private Int32 ResolveAue()
    {
        var codec = (Codec ?? "").ToLowerInvariant();
        if (codec == "mp3") return 3;
        if (codec == "pcm") return SampleRate == 8000 ? 5 : 4;

        return 6;
    }

    private async Task<String> EnsureTokenAsync(CancellationToken cancellationToken)
    {
        if (!Token.IsNullOrEmpty()) return Token;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cached = _token;
        if (!cached.IsNullOrEmpty() && _tokenExpire > now + 60) return cached;

        // 并发调用可能重复换取令牌，服务端允许；后到者覆盖，语义不受影响
        var url = TokenEndpoint + "?grant_type=client_credentials&client_id=" + Uri.EscapeDataString(ApiKey ?? "") +
            "&client_secret=" + Uri.EscapeDataString(SecretKey ?? "");

        using var response = await GetClient().PostAsync(url, null, cancellationToken).ConfigureAwait(false);
        var body = await ReadBodyAsync(response).ConfigureAwait(false);

        IDictionary<String, Object> map;
        try { map = JsonParser.Decode(body); }
        catch { map = null; }

        var id = GetString(map, "access_token");
        if (id.IsNullOrEmpty())
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(L.T("Failed to obtain the Baidu access token: {0} {1}", (Int32)response.StatusCode, body));

            var error = GetString(map, "error_description") ?? body;
            throw new InvalidOperationException(L.T("Failed to obtain the Baidu access token: {0}", error));
        }

        var expires = GetInt64(map, "expires_in");
        lock (_tokenLock)
        {
            _token = id;
            _tokenExpire = now + (expires > 0 ? expires : 3600);
        }

        return id;
    }

    private static String ParseError(String body)
    {
        if (body.IsNullOrEmpty()) return "unknown error";

        try
        {
            var map = JsonParser.Decode(body);
            var message = GetString(map, "err_msg") ?? GetString(map, "error_description");
            var code = GetString(map, "err_no") ?? GetString(map, "error");
            return message.IsNullOrEmpty() ? body : message + (code.IsNullOrEmpty() ? "" : " (" + code + ")");
        }
        catch
        {
            return body;
        }
    }

    // 常用音色快照（按百度语音合成发音人编号），完整列表见服务文档
    private static List<TtsVoice> CreateBuiltinVoices() =>
    [
        CreateVoice("0", "度小美", "zh-CN", "Female", true),
        CreateVoice("1", "度小宇", "zh-CN", "Male", false),
        CreateVoice("3", "度逍遥", "zh-CN", "Male", false, TtsScenario.Narration),
        CreateVoice("4", "度丫丫", "zh-CN", "Female", false),
    ];
}
