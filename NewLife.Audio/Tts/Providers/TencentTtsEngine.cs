using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using NewLife.Serialization;

namespace NewLife.Audio.Tts;

/// <summary>腾讯云语音合成（TTS）引擎</summary>
/// <remarks>
/// 调用 <c>TextToVoice</c> 接口，响应里的 Audio 字段是 base64 编码的整段音频，
/// 因此先整段解码再产出 PCM，不是边收边转。
/// <para>鉴权使用 TC3-HMAC-SHA256：每次请求按日期派生签名密钥，签名覆盖请求体哈希与 content-type/host 两个头。</para>
/// <para>参数映射：<see cref="TtsOptions.Rate"/> 按 0.6/0.8/1.0/1.2/1.5/2.5 就近映射到 Speed（-2~6）；
/// <see cref="TtsOptions.Volume"/> 按 <c>(音量-1)×10</c> 映射到 Volume（-10~0）。</para>
/// </remarks>
/// <example>
/// <code>
/// var tts = new TtsClient(new TencentTtsEngine
/// {
///     SecretId = "AKID...",
///     SecretKey = "your-secret",
///     VoiceType = 101001,
/// });
/// await tts.SaveAsync("设备A2温度过高", "alert.wav");
/// </code>
/// </example>
public class TencentTtsEngine : CloudTtsEngine
{
    private static readonly Double[] _speedRates = [0.6, 0.8, 1.0, 1.2, 1.5, 2.5];
    private static readonly Int32[] _speedValues = [-2, -1, 0, 1, 2, 6];

    /// <summary>云 API 密钥 Id</summary>
    public String SecretId { get; set; }

    /// <summary>云 API 密钥 Key</summary>
    public String SecretKey { get; set; }

    /// <summary>服务地址</summary>
    public String Endpoint { get; set; } = "https://tts.tencentcloudapi.com";

    /// <summary>地域，如 ap-guangzhou；为空时不发送地域头</summary>
    public String Region { get; set; } = "ap-guangzhou";

    /// <summary>默认音色编号，如 101001</summary>
    public Int32 VoiceType { get; set; } = 101001;

    /// <summary>输出编码：wav / pcm / mp3</summary>
    public String Codec { get; set; } = "wav";

    /// <summary>采样率</summary>
    public Int32 SampleRate { get; set; } = 16000;

    /// <summary>模型类型，1 为默认模型</summary>
    public Int32 ModelType { get; set; } = 1;

    /// <summary>主语言，1 为中文</summary>
    public Int32 PrimaryLanguage { get; set; } = 1;

    /// <summary>初始化腾讯云合成引擎</summary>
    public TencentTtsEngine() : base("tencent") => Voices = CreateBuiltinVoices();

    /// <summary>是否可用。需要密钥对</summary>
    public override Boolean IsAvailable => !SecretId.IsNullOrEmpty() && !SecretKey.IsNullOrEmpty();

    /// <summary>合成语音，产出整段 PCM</summary>
    /// <param name="text">待合成文本</param>
    /// <param name="options">合成选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>PCM 分块异步序列</returns>
    public override async IAsyncEnumerable<TtsChunk> SynthesizeAsync(String text, TtsOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            throw new NotSupportedException(L.T("Tencent TTS requires SecretId and SecretKey."));

        using var cts = CreateTimeoutSource(cancellationToken);
        var token = cts.Token;

        var payload = BuildBody(text, options);
        var timestamp = GetTimestamp();
        var date = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation("X-TC-Action", "TextToVoice");
        request.Headers.TryAddWithoutValidation("X-TC-Version", "2019-08-23");
        request.Headers.TryAddWithoutValidation("X-TC-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        if (!Region.IsNullOrEmpty()) request.Headers.TryAddWithoutValidation("X-TC-Region", Region);
        request.Headers.TryAddWithoutValidation("Authorization", BuildAuthorization(payload, timestamp, date));

        using var response = await GetClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        var body = await ReadBodyAsync(response).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(L.T("Tencent TTS request failed: {0} {1}", (Int32)response.StatusCode, body));

        IDictionary<String, Object> map;
        try { map = JsonParser.Decode(body); }
        catch { map = null; }

        var result = GetObject(map, "Response");
        if (result == null)
            throw new InvalidOperationException(L.T("Tencent TTS service returned an error: {0}", body));

        var error = GetObject(result, "Error");
        if (error != null)
            throw new InvalidOperationException(L.T("Tencent TTS service returned an error: {0} {1}", GetString(error, "Code"), GetString(error, "Message")));

        var audio = GetString(result, "Audio");
        if (audio.IsNullOrEmpty())
            throw new InvalidOperationException(L.T("Tencent TTS returned no audio."));

        Byte[] data;
        try { data = Convert.FromBase64String(audio); }
        catch (FormatException) { throw new InvalidOperationException(L.T("Tencent TTS returned invalid base64 audio.")); }

        var format = CreateFormat(SampleRate);
        var chunk = DecodeAudio(data, format, Codec);
        if (chunk != null) yield return chunk;
    }

    /// <summary>取请求时间戳（Unix 秒），测试可覆写为固定值</summary>
    /// <returns>Unix 秒</returns>
    protected virtual Int64 GetTimestamp() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>取会话 Id，测试可覆写为固定值</summary>
    /// <returns>会话 Id</returns>
    protected virtual String CreateSessionId() => Guid.NewGuid().ToString("N");

    private String BuildBody(String text, TtsOptions options)
    {
        var rate = options?.Rate > 0 ? options.Rate : 1;
        var speed = MapSpeed(rate);

        var volume = (Int32)Math.Round(((options?.Volume ?? 1) - 1) * 10);
        if (volume < -10) volume = -10;
        else if (volume > 10) volume = 10;

        var voice = VoiceType;
        if (options != null && !options.Voice.IsNullOrEmpty() && Int32.TryParse(options.Voice, out var type)) voice = type;

        var sb = new StringBuilder();
        var first = true;
        sb.Append('{');
        TtsJson.AppendField(sb, ref first, "Text", text ?? "");
        TtsJson.AppendField(sb, ref first, "SessionId", CreateSessionId());
        TtsJson.AppendField(sb, ref first, "ModelType", ModelType);
        TtsJson.AppendField(sb, ref first, "VoiceType", voice);
        TtsJson.AppendField(sb, ref first, "Speed", speed);
        TtsJson.AppendField(sb, ref first, "Volume", volume);
        TtsJson.AppendField(sb, ref first, "ProjectId", 0);
        TtsJson.AppendField(sb, ref first, "PrimaryLanguage", PrimaryLanguage);
        TtsJson.AppendField(sb, ref first, "SampleRate", SampleRate);
        TtsJson.AppendField(sb, ref first, "Codec", Codec);
        sb.Append('}');

        return sb.ToString();
    }

    private static Int32 MapSpeed(Single rate)
    {
        var best = 2;
        var diff = Double.MaxValue;
        for (var i = 0; i < _speedRates.Length; i++)
        {
            var item = Math.Abs(_speedRates[i] - rate);
            if (item < diff)
            {
                diff = item;
                best = i;
            }
        }

        return _speedValues[best];
    }

    private String BuildAuthorization(String payload, Int64 timestamp, String date)
    {
        const String contentType = "application/json; charset=utf-8";
        const String signedHeaders = "content-type;host";

        var host = new Uri(Endpoint).Host;
        var canonicalRequest = String.Join("\n", new[]
        {
            "POST",
            "/",
            "",
            "content-type:" + contentType,
            "host:" + host,
            "",
            signedHeaders,
            Sha256Hex(payload),
        });

        var scope = date + "/tts/tc3_request";
        var stringToSign = "TC3-HMAC-SHA256\n" + timestamp + "\n" + scope + "\n" + Sha256Hex(canonicalRequest);

        var secretDate = HmacSha256(Encoding.UTF8.GetBytes("TC3" + SecretKey), date);
        var secretService = HmacSha256(secretDate, "tts");
        var secretSigning = HmacSha256(secretService, "tc3_request");
        var signature = ToHex(HmacSha256(secretSigning, stringToSign));

        return "TC3-HMAC-SHA256 Credential=" + SecretId + "/" + scope + ", SignedHeaders=" + signedHeaders + ", Signature=" + signature;
    }

    private static Byte[] HmacSha256(Byte[] key, String text)
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

    // 常用音色快照（2026-10 按腾讯云语音合成音色列表校对），完整列表见服务文档
    private static List<TtsVoice> CreateBuiltinVoices() =>
    [
        CreateVoice("101001", "智瑜", "zh-CN", "Female", true, TtsScenario.Alert, TtsScenario.Notification),
        CreateVoice("101004", "智云", "zh-CN", "Male", false),
        CreateVoice("101011", "智燕", "zh-CN", "Female", false, TtsScenario.Narration),
        CreateVoice("101013", "智辉", "zh-CN", "Male", false, TtsScenario.Narration),
        CreateVoice("101016", "智甜", "zh-CN", "Female", false),
        CreateVoice("101019", "智彤", "zh-HK", "Female", false),
        CreateVoice("101055", "智付", "zh-CN", "Female", false, TtsScenario.Conversation),
        CreateVoice("101050", "WeJack", "en", "Male", false),
    ];
}
