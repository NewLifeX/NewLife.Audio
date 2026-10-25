using System.Diagnostics;
using System.Net.Http;
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
/// 用一个可配置的 HTTP 请求打通绝大多数在线合成服务：地址、方法、请求体模板、请求头全部可注入，
/// 返回值支持 WAV、MP3 与裸 PCM 三种形态，其中 WAV 与裸 PCM 是边收边播的真流式。
/// <para>
/// 各家的鉴权方式差异很大（签名串、Token、时间戳），本引擎只负责“发请求 + 解码音频”，
/// 签名请用请求头或自定义子类补齐。协议细节不宜放进通用库。
/// </para>
/// <para>在线服务的三条硬约束：需要网络、有调用费用、文本会离开设备，因此受 <see cref="TtsRuntimeProfile.AllowOnline"/> 管控。</para>
/// </remarks>
/// <example>
/// <code>
/// var online = new HttpTtsEngine
/// {
///     Name = "baidu",
///     Url = "https://tsn.baidu.com/text2audio?tex={text}&amp;lan=zh&amp;cuid=dev&amp;ctp=1&amp;tok=xxx",
///     Method = "GET",
///     ResponseKind = TtsResponseKind.Mp3,
///     Quality = 85,
/// };
/// </code>
/// </example>
public class HttpTtsEngine : TtsEngineBase
{
    private static readonly HttpClient _shared = new() { Timeout = TimeSpan.FromSeconds(60) };

    private TtsResponseKind _responseKind = TtsResponseKind.Auto;

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
    /// <remarks>返回 WAV 或裸 PCM 时引擎具备流式能力，能力位会随之自动更新。</remarks>
    public TtsResponseKind ResponseKind
    {
        get => _responseKind;
        set
        {
            _responseKind = value;
            UpdateCapabilities();
        }
    }

    /// <summary>裸 PCM 时的采样率</summary>
    public Int32 SampleRate { get; set; } = 24000;

    /// <summary>声道数</summary>
    public Int32 Channels { get; set; } = 1;

    /// <summary>超时时间（秒）</summary>
    public Int32 TimeoutSeconds { get; set; } = 30;

    /// <summary>自定义 HttpClient，为 null 时使用内置共享实例</summary>
    public HttpClient Client { get; set; }

    /// <summary>初始化在线合成引擎</summary>
    public HttpTtsEngine()
    {
        Name = "http";
        Capabilities = TtsCapability.Online;
        Quality = 85;
        // 在线引擎本身不占内存，缓冲一段音频即可
        RequiredMemoryBytes = 8L * 1024 * 1024;
    }

    /// <summary>是否可用。必须配置地址</summary>
    public override Boolean IsAvailable => !Url.IsNullOrEmpty();

    /// <summary>合成语音</summary>
    /// <param name="request">合成请求</param>
    /// <param name="sink">分块输出目标，可为 null</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>合成结果</returns>
    public override async Task<TtsResult> SynthesizeAsync(TtsRequest request, ITtsSink sink, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (Url.IsNullOrEmpty()) throw new InvalidOperationException(L.T("The TTS service URL is not configured."));

        UpdateCapabilities();

        var client = Client ?? _shared;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (TimeoutSeconds > 0) cts.CancelAfter(TimeoutSeconds * 1000);
        var token = cts.Token;

        var body = FillBody(request);
        using var message = new HttpRequestMessage(new HttpMethod(Method.IsNullOrEmpty() ? "POST" : Method.ToUpperInvariant()), FillUrl(request));
        if (!body.IsNullOrEmpty()) message.Content = new StringContent(body, Encoding.UTF8, ContentType);

        foreach (var item in Headers)
        {
            if (!message.Headers.TryAddWithoutValidation(item.Key, item.Value))
                message.Content?.Headers.TryAddWithoutValidation(item.Key, item.Value);
        }

        var watch = Stopwatch.StartNew();
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var kind = ResolveKind(response);
        var format = new AudioFormat
        {
            SampleRate = SampleRate,
            Channels = Channels,
            BitsPerSample = 16,
        };

        var result = new TtsResult
        {
            Source = TtsSource.Engine,
            Engine = Name,
            Text = request.Text,
            Format = format,
        };

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

        if (kind == TtsResponseKind.Mp3)
        {
            // MP3 必须整段解码，走一次性路径
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, 81920, token).ConfigureAwait(false);
            var mp3 = buffer.ToArray();
            var pcm = DecodeMp3(mp3, format);
            result.Pcm = pcm;
            result.PcmBytes = pcm.Length;
            result.Format = format;
            WritePcm(sink, pcm, format);
        }
        else if (sink == null)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, 81920, token).ConfigureAwait(false);
            var data = buffer.ToArray();
            var pcm = kind == TtsResponseKind.Wav ? ExtractWav(data, format) : data;
            result.Pcm = pcm;
            result.PcmBytes = pcm.Length;
            result.Format = format;
        }
        else
        {
            // WAV 需要先读头拿到真实采样率，之后才是边收边播
            if (kind == TtsResponseKind.Wav)
            {
                var info = await TtsWav.SkipHeaderAsync(stream, token).ConfigureAwait(false);
                if (info != null)
                {
                    if (info.SampleRate > 0) format.SampleRate = info.SampleRate;
                    if (info.Channels > 0) format.Channels = info.Channels;
                }
            }

            sink.Open(format);

            var buffer = new Byte[8192];
            var first = true;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                if (read <= 0) break;

                if (first)
                {
                    result.FirstChunkTime = watch.Elapsed.TotalSeconds;
                    first = false;
                }

                sink.Write(buffer, 0, read);
                result.PcmBytes += read;
            }
            sink.Close();
        }

        watch.Stop();
        result.ElapsedTime = watch.Elapsed.TotalSeconds;
        if (result.FirstChunkTime <= 0) result.FirstChunkTime = result.ElapsedTime;

        return result;
    }

    private void UpdateCapabilities()
    {
        var streaming = _responseKind == TtsResponseKind.Wav || _responseKind == TtsResponseKind.RawPcm;
        Capabilities = streaming ? TtsCapability.Online | TtsCapability.Streaming : TtsCapability.Online;
    }

    /// <summary>把结果写到输出目标</summary>
    private static void WritePcm(ITtsSink sink, Byte[] pcm, AudioFormat format)
    {
        if (sink == null || pcm == null || pcm.Length == 0) return;

        sink.Open(format);
        sink.Write(pcm, 0, pcm.Length);
        sink.Close();
    }

    private TtsResponseKind ResolveKind(HttpResponseMessage response)
    {
        if (ResponseKind != TtsResponseKind.Auto) return ResponseKind;

        var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        if (type.Contains("wav")) return TtsResponseKind.Wav;
        if (type.Contains("mpeg") || type.Contains("mp3")) return TtsResponseKind.Mp3;
        return TtsResponseKind.RawPcm;
    }

    private String FillUrl(TtsRequest request)
    {
        var url = Url ?? "";
        url = url.Replace("{text}", Uri.EscapeDataString(request.Text ?? ""));
        url = url.Replace("{voice}", Uri.EscapeDataString(request.Voice ?? ""));
        url = url.Replace("{speed}", request.Speed.ToString("0.###"));
        return url;
    }

    private String FillBody(TtsRequest request)
    {
        var body = BodyTemplate;
        if (body.IsNullOrEmpty()) return null;

        body = body.Replace("{text_json}", JsonEscape(request.Text ?? ""));
        body = body.Replace("{text}", request.Text ?? "");
        body = body.Replace("{voice}", request.Voice ?? "");
        body = body.Replace("{speed}", request.Speed.ToString("0.###"));
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

    private static Byte[] ExtractWav(Byte[] data, AudioFormat format)
    {
        if (!TtsWav.TryParse(data, out var info)) return data;

        if (info.SampleRate > 0) format.SampleRate = info.SampleRate;
        if (info.Channels > 0) format.Channels = info.Channels;

        var pcm = new Byte[info.DataSize];
        Buffer.BlockCopy(data, info.DataOffset, pcm, 0, info.DataSize);
        return pcm;
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
        return packet == null ? [] : packet.GetSpan().ToArray();
    }
}
