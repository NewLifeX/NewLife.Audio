using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using NewLife.Audio.Codecs;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts;

/// <summary>在线服务返回的音频类型</summary>
public enum TtsResponseKind
{
    /// <summary>按响应头自动判断</summary>
    Auto = 0,

    /// <summary>WAV 容器</summary>
    Wav = 1,

    /// <summary>MP3 编码</summary>
    Mp3 = 2,

    /// <summary>裸 PCM，采样率由 <see cref="HttpTtsEngine.SampleRate"/> 指定</summary>
    RawPcm = 3,
}

/// <summary>在线 HTTP 语音合成引擎</summary>
/// <remarks>
/// 用一个可配置的 HTTP 请求打通大多数在线合成服务：地址、方法、请求体模板、请求头全部可注入，
/// 返回值支持 WAV、MP3 与裸 PCM。WAV 与裸 PCM 可以边收边产出，MP3 需要先整段解码。
/// <para>各家的鉴权方式差异很大（签名串、Token、时间戳），本引擎只负责“发请求 + 解码音频”，
/// 签名请用请求头或自定义子类补齐。</para>
/// </remarks>
public class HttpTtsEngine : TtsEngineBase
{
    private static readonly HttpClient _shared = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>服务地址，支持 {text} {voice} {speed} 占位（自动 URL 编码）</summary>
    public String Url { get; set; }

    /// <summary>HTTP 方法</summary>
    public String Method { get; set; } = "POST";

    /// <summary>请求体类型</summary>
    public String ContentType { get; set; } = "application/json";

    /// <summary>请求体模板，支持 {text} {text_json} {voice} {speed} 占位</summary>
    public String BodyTemplate { get; set; }

    /// <summary>请求头，用于携带鉴权令牌</summary>
    public IDictionary<String, String> Headers { get; } = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);

    /// <summary>返回音频类型</summary>
    public TtsResponseKind ResponseKind { get; set; } = TtsResponseKind.Auto;

    /// <summary>裸 PCM 时的采样率</summary>
    public Int32 SampleRate { get; set; } = 24000;

    /// <summary>声道数</summary>
    public Int32 Channels { get; set; } = 1;

    /// <summary>超时时间（秒）</summary>
    public Int32 TimeoutSeconds { get; set; } = 30;

    /// <summary>自定义 HttpClient，为 null 时使用内置共享实例</summary>
    public HttpClient Client { get; set; }

    /// <summary>初始化在线合成引擎</summary>
    public HttpTtsEngine() : base("http") { }

    /// <summary>是否可用。必须配置地址</summary>
    public override Boolean IsAvailable => !Url.IsNullOrEmpty();

    /// <summary>合成语音，逐块读取 HTTP 响应</summary>
    /// <param name="text">待合成文本</param>
    /// <param name="options">合成选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>PCM 分块异步序列</returns>
    public override async IAsyncEnumerable<TtsChunk> SynthesizeAsync(String text, TtsOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Url.IsNullOrEmpty()) throw new InvalidOperationException(L.T("The TTS service URL is not configured."));

        var client = Client ?? _shared;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (TimeoutSeconds > 0) cts.CancelAfter(TimeoutSeconds * 1000);
        var token = cts.Token;

        var body = FillBody(text, options);
        using var message = new HttpRequestMessage(new HttpMethod(Method.IsNullOrEmpty() ? "POST" : Method.ToUpperInvariant()), FillUrl(text, options));
        if (!body.IsNullOrEmpty()) message.Content = new StringContent(body, Encoding.UTF8, ContentType);

        foreach (var item in Headers)
        {
            if (!message.Headers.TryAddWithoutValidation(item.Key, item.Value))
                message.Content?.Headers.TryAddWithoutValidation(item.Key, item.Value);
        }

        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var kind = ResolveKind(response);
        var format = new AudioFormat
        {
            SampleRate = SampleRate > 0 ? SampleRate : 24000,
            Channels = Channels > 0 ? Channels : 1,
            BitsPerSample = 16,
        };

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

        if (kind == TtsResponseKind.Mp3)
        {
            // MP3 必须整段解码，走一次性路径
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, 81920, token).ConfigureAwait(false);
            var pcm = DecodeMp3(buffer.ToArray(), format);
            if (pcm.Length > 0) yield return new TtsChunk { Format = format, Data = pcm };
            yield break;
        }

        if (kind == TtsResponseKind.Wav)
        {
            var info = await TtsWav.SkipHeaderAsync(stream, token).ConfigureAwait(false);
            if (info != null)
            {
                if (info.SampleRate > 0) format.SampleRate = info.SampleRate;
                if (info.Channels > 0) format.Channels = info.Channels;
                if (info.BitsPerSample > 0) format.BitsPerSample = info.BitsPerSample;
            }
        }

        var chunk = new Byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, 0, chunk.Length, token).ConfigureAwait(false);
            if (read <= 0) break;

            var data = new Byte[read];
            Buffer.BlockCopy(chunk, 0, data, 0, read);

            yield return new TtsChunk { Format = format, Data = data };
        }
    }

    private TtsResponseKind ResolveKind(HttpResponseMessage response)
    {
        if (ResponseKind != TtsResponseKind.Auto) return ResponseKind;

        var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        if (type.Contains("wav")) return TtsResponseKind.Wav;
        if (type.Contains("mpeg") || type.Contains("mp3")) return TtsResponseKind.Mp3;
        return TtsResponseKind.RawPcm;
    }

    private String FillUrl(String text, TtsOptions options)
    {
        var url = Url ?? "";
        url = url.Replace("{text}", Uri.EscapeDataString(text ?? ""));
        url = url.Replace("{voice}", Uri.EscapeDataString(options?.Voice ?? ""));
        url = url.Replace("{speed}", (options?.Rate > 0 ? options.Rate : 1).ToString("0.###"));
        return url;
    }

    private String FillBody(String text, TtsOptions options)
    {
        var body = BodyTemplate;
        if (body.IsNullOrEmpty()) return null;

        var rate = options?.Rate > 0 ? options.Rate : 1;
        body = body.Replace("{text_json}", JsonEscape(text ?? ""));
        body = body.Replace("{text}", text ?? "");
        body = body.Replace("{voice}", options?.Voice ?? "");
        body = body.Replace("{speed}", rate.ToString("0.###"));
        return body;
    }

    private static String JsonEscape(String text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < ' ') sb.Append("\\u").Append(((Int32)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }

        return sb.ToString();
    }

    private static Byte[] DecodeMp3(Byte[] data, AudioFormat format)
    {
        // ID3 标签会把帧同步字往后推，先扫到第一个有效帧
        var offset = 0;
        while (offset + 4 < data.Length && !Mp3Codec.IsMp3Frame(data, offset)) offset++;

        var header = offset + 4 < data.Length ? Mp3Codec.ParseFrameHeader(data, offset) : null;
        if (header != null)
        {
            if (header.SampleRate > 0) format.SampleRate = header.SampleRate;
            format.Channels = header.ChannelMode == 3 ? 1 : 2;
        }

        var codec = new Mp3Codec();
        var packet = codec.ToPcm(data, null);
        return packet == null ? Array.Empty<Byte>() : packet.GetSpan().ToArray();
    }
}
