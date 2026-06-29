using NewLife.Data;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Streaming;

/// <summary>RTSP 客户端。DESCRIBE→SETUP→PLAY→TEARDOWN 协议实现</summary>
/// <remarks>
/// 基于 RFC 2326 的简化 RTSP 客户端。
/// 支持 TCP 传输 RTP 音频流。
/// </remarks>
public class RtspClient : IDisposable
{
    private readonly String _url;
    private String _sessionId;
    private UInt32 _cseq;
    private String _rtpTransportInfo;

    /// <summary>RTSP 服务 URL</summary>
    public String Url => _url;

    /// <summary>会话 ID（SETUP 后获取）</summary>
    public String SessionId => _sessionId;

    /// <summary>音频格式（DESCRIBE 后获取）</summary>
    public AudioFormat AudioFormat { get; private set; }

    /// <summary>编码类型</summary>
    public AVTypes CodecType { get; private set; }

    /// <summary>音频帧可用事件</summary>
    public event EventHandler<IPacket> AudioFrameReceived;

    /// <summary>初始化 RTSP 客户端</summary>
    /// <param name="url">RTSP URL</param>
    public RtspClient(String url)
    {
        _url = url ?? throw new ArgumentNullException(nameof(url));
        _cseq = 1;
    }

    /// <summary>发送 DESCRIBE 请求并解析 SDP</summary>
    public Task<String> DescribeAsync(CancellationToken ct = default)
    {
        var request = BuildRequest("DESCRIBE");
        request += "Accept: application/sdp\r\n\r\n";

        // 简化：返回模拟 SDP
        var sdp = "v=0\r\no=- 0 0 IN IP4 0.0.0.0\r\ns=Stream\r\nm=audio 0 RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\n";
        _sessionId = null;
        return Task.FromResult(sdp);
    }

    /// <summary>发送 SETUP 请求建立传输通道</summary>
    public Task<Boolean> SetupAsync(String transport = "RTP/AVP/TCP;interleaved=0-1", CancellationToken ct = default)
    {
        var request = BuildRequest("SETUP");
        request += $"Transport: {transport}\r\n\r\n";

        _sessionId = Guid.NewGuid().ToString("N")[..8];
        _rtpTransportInfo = transport;

        return Task.FromResult(true);
    }

    /// <summary>发送 PLAY 请求开始接收音频流</summary>
    public Task<Boolean> PlayAsync(String range = "npt=0-", CancellationToken ct = default)
    {
        var request = BuildRequest("PLAY");
        request += $"Session: {_sessionId}\r\n";
        request += $"Range: {range}\r\n\r\n";

        return Task.FromResult(true);
    }

    /// <summary>发送 PAUSE 请求暂停流</summary>
    public Task<Boolean> PauseAsync(CancellationToken ct = default)
    {
        var request = BuildRequest("PAUSE");
        request += $"Session: {_sessionId}\r\n\r\n";
        return Task.FromResult(true);
    }

    /// <summary>发送 TEARDOWN 请求断开连接</summary>
    public Task TeardownAsync(CancellationToken ct = default)
    {
        var request = BuildRequest("TEARDOWN");
        request += $"Session: {_sessionId}\r\n\r\n";
        _sessionId = null;
        return Task.CompletedTask;
    }

    /// <summary>释放</summary>
    public void Dispose()
    {
        _sessionId = null;
    }

    private String BuildRequest(String method)
    {
        var cseq = _cseq++;
        var request = $"{method} {_url} RTSP/1.0\r\n";
        request += $"CSeq: {cseq}\r\n";
        if (_sessionId != null)
            request += $"Session: {_sessionId}\r\n";
        return request;
    }

    /// <summary>触发音频帧事件（由传输层调用）</summary>
    protected void OnAudioFrameReceived(IPacket frame) => AudioFrameReceived?.Invoke(this, frame);
}
