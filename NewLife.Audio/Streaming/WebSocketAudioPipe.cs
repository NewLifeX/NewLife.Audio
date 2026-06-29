using NewLife.Data;
using NewLife.Audio.DSP;
using NewLife.Audio.Writers;

namespace NewLife.Audio.Streaming;

/// <summary>WebSocket 音频管道。双向低延迟音频传输</summary>
/// <remarks>
/// 封装音频帧为 WebSocket 二进制消息，支持双向传输。
/// 配合 NewLife.Http 的 WebSocketSession/WebSocketClient 使用。
/// 每帧格式：[1字节编码类型][2字节序列号][2字节长度][N字节负载]。
/// </remarks>
public class WebSocketAudioPipe
{
    /// <summary>音频格式</summary>
    public AudioFormat Format { get; }

    /// <summary>编码类型</summary>
    public AVTypes CodecType { get; }

    private UInt16 _sendSeq;
    private UInt16 _recvSeq;
    private Boolean _isOpen;

    /// <summary>已发送帧数</summary>
    public UInt16 SendSequence => _sendSeq;

    /// <summary>已接收帧数</summary>
    public UInt16 ReceiveSequence => _recvSeq;

    /// <summary>管道是否打开</summary>
    public Boolean IsOpen => _isOpen;

    /// <summary>音频帧接收事件</summary>
    public event EventHandler<Byte[]> AudioFrameReceived;

    /// <summary>初始化 WebSocket 音频管道</summary>
    /// <param name="format">音频格式</param>
    /// <param name="codecType">编码类型</param>
    public WebSocketAudioPipe(AudioFormat format, AVTypes codecType)
    {
        Format = format ?? AudioFormat.Default;
        CodecType = codecType;
        _isOpen = true;
    }

    /// <summary>发送音频帧。自动添加帧头（编码类型+序列号+长度）</summary>
    /// <param name="audioData">编码后的音频数据</param>
    /// <returns>WebSocket 消息字节</returns>
    public Byte[] SendFrame(Byte[] audioData)
    {
        if (!_isOpen) throw new InvalidOperationException("管道已关闭");

        var headerSize = 5;
        var message = new Byte[headerSize + audioData.Length];

        message[0] = (Byte)CodecType;
        message[1] = (Byte)((_sendSeq >> 8) & 0xFF);
        message[2] = (Byte)(_sendSeq & 0xFF);
        message[3] = (Byte)((audioData.Length >> 8) & 0xFF);
        message[4] = (Byte)(audioData.Length & 0xFF);

        Array.Copy(audioData, 0, message, headerSize, audioData.Length);
        _sendSeq++;

        return message;
    }

    /// <summary>接收并解析 WebSocket 消息为音频帧</summary>
    /// <param name="message">WebSocket 消息字节</param>
    /// <returns>解析后的音频数据，null 表示无效</returns>
    public Byte[] ReceiveFrame(Byte[] message)
    {
        if (!_isOpen || message.Length < 5) return null;

        var codec = (AVTypes)message[0];
        var seq = (UInt16)((message[1] << 8) | message[2]);
        var len = (UInt16)((message[3] << 8) | message[4]);

        if (len + 5 > message.Length) return null;

        _recvSeq = seq;

        var audioData = new Byte[len];
        Array.Copy(message, 5, audioData, 0, len);

        AudioFrameReceived?.Invoke(this, audioData);
        return audioData;
    }

    /// <summary>将 PCM 数据编码并通过管道发送</summary>
    /// <param name="pcmData">PCM 音频数据</param>
    /// <param name="encoder">编码器委托</param>
    /// <returns>WebSocket 消息（可用于直接发送）</returns>
    public Byte[] SendPcm(Byte[] pcmData, Func<Byte[], Byte[]> encoder)
    {
        var encoded = encoder(pcmData);
        return SendFrame(encoded);
    }

    /// <summary>关闭管道</summary>
    public void Close()
    {
        _isOpen = false;
    }
}
