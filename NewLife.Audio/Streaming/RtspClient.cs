using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using NewLife.Data;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Streaming;

/// <summary>RTSP 客户端。DESCRIBE→SETUP→PLAY→TEARDOWN 协议实现</summary>
/// <remarks>
/// 基于 RFC 2326 的 RTSP 客户端，支持 TCP 传输 RTP 音频流（interleaved 模式）。
/// 标准流程：OPTIONS → DESCRIBE → SETUP → PLAY → (TEARDOWN)
/// </remarks>
public class RtspClient : IDisposable
{
    #region 属性
    private readonly String _url;
    private String _host;
    private Int32 _port;
    private String _path;
    private String _sessionId;
    private UInt32 _cseq;
    private TcpClient _tcpClient;
    private NetworkStream _networkStream;
    private CancellationTokenSource _receiveCts;
    private Task _receiveTask;

    /// <summary>RTSP 服务 URL</summary>
    public String Url => _url;

    /// <summary>会话 ID（SETUP 后获取）</summary>
    public String SessionId => _sessionId;

    /// <summary>音频格式（DESCRIBE 后获取）</summary>
    public AudioFormat AudioFormat { get; private set; }

    /// <summary>编码类型</summary>
    public AVTypes CodecType { get; private set; }

    /// <summary>是否已连接</summary>
    public Boolean IsConnected => _tcpClient?.Connected == true;

    /// <summary>音频帧可用事件</summary>
    public event EventHandler<IPacket> AudioFrameReceived;
    #endregion

    #region 构造
    /// <summary>初始化 RTSP 客户端</summary>
    /// <param name="url">RTSP URL（如 rtsp://192.168.1.1:554/stream）</param>
    public RtspClient(String url)
    {
        _url = url ?? throw new ArgumentNullException(nameof(url));
        _cseq = 1;
        ParseUrl(_url);
    }

    /// <summary>解析 RTSP URL，提取主机、端口、路径</summary>
    private void ParseUrl(String url)
    {
        var uri = new Uri(url);
        _host = uri.Host;
        _port = uri.Port > 0 ? uri.Port : 554;
        _path = uri.PathAndQuery;
    }
    #endregion

    #region 方法
    /// <summary>建立 TCP 连接</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_tcpClient?.Connected == true) return;

        _tcpClient?.Dispose();
        _tcpClient = new TcpClient();
#if NET5_0_OR_GREATER
        await _tcpClient.ConnectAsync(_host, _port, ct);
#else
        await _tcpClient.ConnectAsync(_host, _port);
        ct.ThrowIfCancellationRequested();
#endif
        _networkStream = _tcpClient.GetStream();
    }

    /// <summary>发送 OPTIONS 请求探测服务器能力</summary>
    /// <returns>服务器支持的 Public 方法列表</returns>
    public async Task<String> OptionsAsync(CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);

        var request = BuildRequest("OPTIONS");
        var response = await SendRequestAsync(request, ct);
        var rtspResponse = ParseResponse(response);

        return rtspResponse.Headers.TryGetValue("Public", out var pub) ? pub : String.Empty;
    }

    /// <summary>发送 DESCRIBE 请求并解析 SDP</summary>
    /// <returns>SDP 原始文本</returns>
    public async Task<String> DescribeAsync(CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);

        var request = BuildRequest("DESCRIBE");
        request += "Accept: application/sdp\r\n\r\n";

        var response = await SendRequestAsync(request, ct);
        var rtspResponse = ParseResponse(response);

        if (rtspResponse.StatusCode != 200)
            throw new InvalidOperationException($"DESCRIBE 返回 {rtspResponse.StatusCode} {rtspResponse.ReasonPhrase}");

        var sdp = rtspResponse.Body;
        ParseSdp(sdp);

        return sdp;
    }

    /// <summary>发送 SETUP 请求建立传输通道</summary>
    /// <param name="transport">传输参数，默认 RTP/AVP/TCP;interleaved=0-1</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否成功</returns>
    public async Task<Boolean> SetupAsync(String transport = "RTP/AVP/TCP;interleaved=0-1", CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);

        var request = BuildRequest("SETUP");
        request += $"Transport: {transport}\r\n\r\n";

        var response = await SendRequestAsync(request, ct);
        var rtspResponse = ParseResponse(response);

        if (rtspResponse.StatusCode != 200)
            return false;

        // 提取 Session ID
        if (rtspResponse.Headers.TryGetValue("Session", out var sessionHeader))
        {
            // Session 头格式：sessionId[;timeout=xx]
            var semiIdx = sessionHeader.IndexOf(';');
            _sessionId = semiIdx > 0 ? sessionHeader[..semiIdx] : sessionHeader;
        }

        return true;
    }

    /// <summary>发送 PLAY 请求开始接收音频流</summary>
    /// <param name="range">播放范围，默认 npt=0-</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否成功</returns>
    public async Task<Boolean> PlayAsync(String range = "npt=0-", CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);

        var request = BuildRequest("PLAY");
        request += $"Session: {_sessionId}\r\n";
        request += $"Range: {range}\r\n\r\n";

        var response = await SendRequestAsync(request, ct);
        var rtspResponse = ParseResponse(response);

        if (rtspResponse.StatusCode != 200)
            return false;

        // 启动后台接收循环
        StartReceiveLoop();

        return true;
    }

    /// <summary>发送 PAUSE 请求暂停流</summary>
    /// <returns>是否成功</returns>
    public async Task<Boolean> PauseAsync(CancellationToken ct = default)
    {
        if (_tcpClient?.Connected != true) return false;

        StopReceiveLoop();

        var request = BuildRequest("PAUSE");
        request += $"Session: {_sessionId}\r\n\r\n";

        var response = await SendRequestAsync(request, ct);
        var rtspResponse = ParseResponse(response);

        return rtspResponse.StatusCode == 200;
    }

    /// <summary>发送 TEARDOWN 请求断开连接</summary>
    public async Task TeardownAsync(CancellationToken ct = default)
    {
        StopReceiveLoop();

        if (_tcpClient?.Connected == true)
        {
            var request = BuildRequest("TEARDOWN");
            request += $"Session: {_sessionId}\r\n\r\n";

            await SendRequestAsync(request, ct);
        }

        _sessionId = null;
        Disconnect();
    }

    /// <summary>释放</summary>
    public void Dispose()
    {
        StopReceiveLoop();
        Disconnect();
        _receiveCts?.Dispose();
    }
    #endregion

    #region 辅助
    /// <summary>确保已连接</summary>
    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_tcpClient?.Connected != true)
            await ConnectAsync(ct);
    }

    /// <summary>断开 TCP 连接</summary>
    private void Disconnect()
    {
        _networkStream?.Dispose();
        _networkStream = null;
        _tcpClient?.Dispose();
        _tcpClient = null;
    }

    /// <summary>构建 RTSP 请求</summary>
    private String BuildRequest(String method)
    {
        var cseq = _cseq++;
        var sb = new StringBuilder();
        sb.Append($"{method} {_path} RTSP/1.0\r\n");
        sb.Append($"CSeq: {cseq}\r\n");
        sb.Append($"User-Agent: NewLife.Audio/1.0\r\n");
        sb.Append($"Host: {_host}:{_port}\r\n");
        if (_sessionId != null)
            sb.Append($"Session: {_sessionId}\r\n");
        return sb.ToString();
    }

    /// <summary>发送 RTSP 请求并接收响应</summary>
    private async Task<String> SendRequestAsync(String request, CancellationToken ct)
    {
        var requestBytes = Encoding.ASCII.GetBytes(request);
#if NET5_0_OR_GREATER
        await _networkStream.WriteAsync(requestBytes, ct);
#else
        await _networkStream.WriteAsync(requestBytes, 0, requestBytes.Length, ct);
#endif

        // 读取响应
        var buffer = new Byte[4096];
        var responseBuilder = new StringBuilder();
        var contentLength = 0;
        var headerEnd = false;
        var bodyRead = 0;

        while (true)
        {
#if NET5_0_OR_GREATER
            var read = await _networkStream.ReadAsync(buffer, ct);
#else
            var read = await _networkStream.ReadAsync(buffer, 0, buffer.Length, ct);
#endif
            if (read == 0) break;

            var chunk = Encoding.ASCII.GetString(buffer, 0, read);
            responseBuilder.Append(chunk);

            if (!headerEnd)
            {
                var responseText = responseBuilder.ToString();
                var headerBodySplit = responseText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerBodySplit >= 0)
                {
                    headerEnd = true;

                    // 解析 Content-Length
                    var headerPart = responseText[..headerBodySplit];
                    var clMatch = Regex.Match(headerPart, @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
                    if (clMatch.Success)
                        contentLength = Int32.Parse(clMatch.Groups[1].Value);

                    // 已读取的 body 部分
                    bodyRead = responseText.Length - headerBodySplit - 4;

                    if (contentLength == 0 || bodyRead >= contentLength)
                        break;
                }
            }
            else
            {
                bodyRead += read;
                if (contentLength > 0 && bodyRead >= contentLength)
                    break;

                // 无 Content-Length，读取到连接关闭或超时
                if (contentLength == 0)
                    break;
            }
        }

        return responseBuilder.ToString();
    }

    /// <summary>RTSP 响应解析结果</summary>
    internal struct RtspResponse
    {
        public Int32 StatusCode;
        public String ReasonPhrase;
        public Dictionary<String, String> Headers;
        public String Body;
    }

    /// <summary>解析 RTSP 响应</summary>
    internal RtspResponse ParseResponse(String rawResponse)
    {
        var result = new RtspResponse
        {
            Headers = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase),
            Body = String.Empty,
        };

        var lines = rawResponse.Split(["\r\n", "\n"], StringSplitOptions.None);

        // 解析状态行：RTSP/1.0 200 OK
        if (lines.Length > 0)
        {
            var statusParts = lines[0].Split(' ');
            if (statusParts.Length >= 3 && Int32.TryParse(statusParts[1], out var code))
            {
                result.StatusCode = code;
                result.ReasonPhrase = statusParts.Length > 2 ? String.Join(" ", statusParts, 2, statusParts.Length - 2) : String.Empty;
            }
        }

        // 解析头部
        var i = 1;
        for (; i < lines.Length; i++)
        {
            var line = lines[i];
            if (String.IsNullOrEmpty(line)) break; // 空行 = 头部结束

            var colonIdx = line.IndexOf(':');
            if (colonIdx > 0)
            {
                var key = line[..colonIdx].Trim();
                var value = line[(colonIdx + 1)..].Trim();
                result.Headers[key] = value;
            }
        }

        // Body 从空行后开始
        if (i < lines.Length - 1)
        {
            result.Body = String.Join("\r\n", lines, i + 1, lines.Length - i - 1);
        }

        return result;
    }

    /// <summary>解析 SDP，提取音频格式信息</summary>
    /// <param name="sdp">SDP 文本</param>
    internal void ParseSdp(String sdp)
    {
        AudioFormat = AudioFormat.Default;
        CodecType = AVTypes.LPCM;

        if (String.IsNullOrEmpty(sdp)) return;

        var lines = sdp.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        String payloadType = null;
        String rtpmapLine = null;

        foreach (var line in lines)
        {
            // m=audio 0 RTP/AVP 8
            if (line.StartsWith("m=audio"))
            {
                var mParts = line.Split(' ');
                if (mParts.Length >= 4)
                    payloadType = mParts[3];
            }

            // a=rtpmap:8 PCMA/8000/1
            if (line.StartsWith("a=rtpmap:"))
            {
                var aParts = line.Split(':');
                if (aParts.Length >= 2)
                {
                    var mapParts = aParts[1].Split(' ');
                    if (mapParts.Length >= 2 && mapParts[0] == payloadType)
                    {
                        rtpmapLine = mapParts[1];
                        break;
                    }
                    // 如果只有一个音频轨道，直接使用第一条 rtpmap
                    if (rtpmapLine == null)
                        rtpmapLine = mapParts.Length >= 2 ? mapParts[1] : mapParts[0];
                }
            }
        }

        if (rtpmapLine != null)
            ParseRtpmap(rtpmapLine);
    }

    /// <summary>解析 a=rtpmap 行：PCMA/8000/1</summary>
    internal void ParseRtpmap(String rtpmap)
    {
        // 格式：codecName/sampleRate[/channels]
        var parts = rtpmap.Split('/');
        if (parts.Length < 2) return;

        var codecName = parts[0].ToUpperInvariant();
        var sampleRate = Int32.TryParse(parts[1], out var sr) ? sr : 8000;
        var channels = parts.Length >= 3 && Int32.TryParse(parts[2], out var ch) ? ch : 1;

        CodecType = codecName switch
        {
            "PCMA" => AVTypes.G711A,
            "PCMU" => AVTypes.G711U,
            "L16" or "L8" => AVTypes.LPCM,
            "MPA" or "MPEG" => AVTypes.MPEGAUDIO,
            "MP4A-LATM" or "MPEG4-GENERIC" => AVTypes.AAC,
            "OPUS" => AVTypes.Transparent,
            _ => AVTypes.Transparent,
        };

        AudioFormat = new AudioFormat
        {
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = 16,
            Encoding = CodecType,
        };
    }

    /// <summary>启动后台 RTP 帧接收循环</summary>
    private void StartReceiveLoop()
    {
        StopReceiveLoop();
        _receiveCts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
    }

    /// <summary>停止后台接收循环</summary>
    private void StopReceiveLoop()
    {
        _receiveCts?.Cancel();
        _receiveTask = null;
    }

    /// <summary>后台接收循环：解析 RTSP interleaved 帧（$ + channel + length + data）</summary>
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            var headerBuffer = new Byte[4]; // $channel(1) length(2) padding(1)? 实际是 $channel(1) length(2)
            var stream = _networkStream;

            while (!ct.IsCancellationRequested && stream != null)
            {
                // 读取帧头：$ (1) + channel (1) + length (2) = 4 bytes
                var headerBytesRead = 0;
                while (headerBytesRead < 4)
                {
#if NET5_0_OR_GREATER
                    var r = await stream.ReadAsync(headerBuffer.AsMemory(headerBytesRead, 4 - headerBytesRead), ct);
#else
                    var r = await stream.ReadAsync(headerBuffer, headerBytesRead, 4 - headerBytesRead, ct);
#endif
                    if (r == 0) return; // 连接关闭
                    headerBytesRead += r;
                }

                // 验证帧头标识 $
                if (headerBuffer[0] != 0x24)
                {
                    // 如果不是 $，可能是 RTSP 响应文本（服务器主动发送的 ANNOUNCE 等），跳过
                    continue;
                }

                var channel = headerBuffer[1];
                var length = (headerBuffer[2] << 8) | headerBuffer[3];

                if (length <= 0 || length > 65535) continue;

                // 读取帧数据
                var frameData = new Byte[length];
                var frameBytesRead = 0;
                while (frameBytesRead < length)
                {
#if NET5_0_OR_GREATER
                    var r = await stream.ReadAsync(frameData.AsMemory(frameBytesRead, length - frameBytesRead), ct);
#else
                    var r = await stream.ReadAsync(frameData, frameBytesRead, length - frameBytesRead, ct);
#endif
                    if (r == 0) return;
                    frameBytesRead += r;
                }

                // 仅处理 RTP 数据通道（偶数通道为 RTP，奇数通道为 RTCP）
                if (channel % 2 == 0)
                {
                    OnAudioFrameReceived(new ArrayPacket(frameData));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    /// <summary>触发音频帧事件</summary>
    protected void OnAudioFrameReceived(IPacket frame) => AudioFrameReceived?.Invoke(this, frame);
    #endregion
}
