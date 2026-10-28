using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts;

/// <summary>在线 WebSocket 流式语音合成引擎</summary>
/// <remarks>
/// 面向“长连接 + 文本请求 + 二进制音频帧”的合成服务：连接后发送一条文本/JSON 请求，
/// 逐帧接收二进制音频并转成 PCM 分块。裸 PCM 可以边收边产出；WAV/MP3 需要完整响应后解码。
/// <para>各家协议差异较大，地址、请求模板、请求头与结束帧文本均可配置；鉴权请放在
/// <see cref="Headers"/> 或自定义子类中。</para>
/// </remarks>
/// <example>
/// <code>
/// var tts = new TtsClient(new WebSocketTtsEngine
/// {
///     Url = "wss://example.com/tts",
///     TextTemplate = "{\"text\":\"{text_json}\"}",
/// });
/// await foreach (var chunk in tts.StreamAsync("你好")) { ... }
/// </code>
/// </example>
public class WebSocketTtsEngine : TtsEngineBase
{
    private const Int32 DefaultBufferSize = 16 * 1024;

    /// <summary>WebSocket 地址（ws:// 或 wss://）</summary>
    public String Url { get; set; }

    /// <summary>请求文本模板，支持 {text} {text_json} {voice} {speed}</summary>
    public String TextTemplate { get; set; } = "{text}";

    /// <summary>请求头，用于携带鉴权令牌</summary>
    public IDictionary<String, String> Headers { get; } = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);

    /// <summary>返回音频类型</summary>
    public TtsResponseKind ResponseKind { get; set; } = TtsResponseKind.RawPcm;

    /// <summary>裸 PCM 时的采样率</summary>
    public Int32 SampleRate { get; set; } = 24000;

    /// <summary>声道数</summary>
    public Int32 Channels { get; set; } = 1;

    /// <summary>连接与收包超时（秒）</summary>
    public Int32 TimeoutSeconds { get; set; } = 30;

    /// <summary>接收缓冲大小（字节）</summary>
    public Int32 BufferSize { get; set; } = DefaultBufferSize;

    /// <summary>收到该文本帧时结束（不区分大小写）；为空时只依赖服务端关闭</summary>
    public String EndText { get; set; } = "END";

    /// <summary>初始化 WebSocket 合成引擎</summary>
    public WebSocketTtsEngine() : base("websocket") { }

    /// <summary>是否可用。必须配置 wss/ws 地址</summary>
    public override Boolean IsAvailable => !Url.IsNullOrEmpty();

    /// <summary>合成语音，逐帧接收 WebSocket 二进制音频</summary>
    /// <param name="text">待合成文本</param>
    /// <param name="options">合成选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>PCM 分块异步序列</returns>
    public override async IAsyncEnumerable<TtsChunk> SynthesizeAsync(String text, TtsOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Url.IsNullOrEmpty()) throw new InvalidOperationException(L.T("The TTS service URL is not configured."));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (TimeoutSeconds > 0) cts.CancelAfter(TimeoutSeconds * 1000);
        var token = cts.Token;

        using var socket = new ClientWebSocket();
        foreach (var item in Headers) socket.Options.SetRequestHeader(item.Key, item.Value);

        await socket.ConnectAsync(new Uri(Url), token).ConfigureAwait(false);

        var format = new AudioFormat
        {
            SampleRate = SampleRate > 0 ? SampleRate : 24000,
            Channels = Channels > 0 ? Channels : 1,
            BitsPerSample = 16,
        };

        var request = FillText(text, options);
        var requestBytes = Encoding.UTF8.GetBytes(request);
        await socket.SendAsync(new ArraySegment<Byte>(requestBytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);

        var bufferSize = Math.Max(256, BufferSize);
        var buffer = new Byte[bufferSize];
        using var received = new MemoryStream();
        using var aggregate = new MemoryStream();
        var endRequested = false;

        while (!endRequested && socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<Byte>(buffer), token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    endRequested = true;
                    break;
                }
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    if (!EndText.IsNullOrEmpty() && String.Equals(message.Trim(), EndText, StringComparison.OrdinalIgnoreCase))
                    {
                        endRequested = true;
                        break;
                    }
                    continue;
                }

                received.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (endRequested || received.Length == 0) continue;

            var data = received.ToArray();
            received.SetLength(0);

            if (ResponseKind == TtsResponseKind.RawPcm)
            {
                yield return new TtsChunk { Format = format, Data = data };
            }
            else
            {
                // WAV/MP3 可能是多个二进制帧，先聚合，结束或关闭后一次解码
                aggregate.Write(data, 0, data.Length);
            }
        }

        if (ResponseKind != TtsResponseKind.RawPcm && aggregate.Length > 0)
        {
            var pcm = Decode(aggregate.ToArray(), format);
            if (pcm.Length > 0) yield return new TtsChunk { Format = format, Data = pcm };
        }

        if (socket.State == WebSocketState.CloseReceived)
        {
            try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false); }
            catch { /* 服务端可能已关闭 */ }
        }
        else if (socket.State == WebSocketState.Open)
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false); }
            catch { /* 服务端可能已关闭 */ }
        }
    }

    private Byte[] Decode(Byte[] data, AudioFormat format)
    {
        if (ResponseKind == TtsResponseKind.Mp3) return TtsMp3.Decode(data, format);

        var pcm = AudioFile.ReadPcm(new MemoryStream(data));
        if (pcm.Format.SampleRate > 0) format.SampleRate = pcm.Format.SampleRate;
        if (pcm.Format.Channels > 0) format.Channels = pcm.Format.Channels;

        return pcm.Data;
    }

    private String FillText(String text, TtsOptions options)
    {
        var template = TextTemplate.IsNullOrEmpty() ? "{text}" : TextTemplate;
        var rate = options?.Rate > 0 ? options.Rate : 1;

        return template
            .Replace("{text_json}", TtsJson.Escape(text ?? ""))
            .Replace("{text}", text ?? "")
            .Replace("{voice}", options?.Voice ?? "")
            .Replace("{speed}", rate.ToString("0.###"));
    }
}
