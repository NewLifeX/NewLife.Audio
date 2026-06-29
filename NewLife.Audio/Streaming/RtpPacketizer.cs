using NewLife.Data;

namespace NewLife.Audio.Streaming;

/// <summary>RTP 封包器。将编码音频帧封装为 RFC 3550 RTP 包</summary>
/// <remarks>
/// 按 MTU 自动分片：若编码帧超过 MTU，拆分为多个 RTP 包，最后一个包设置 Marker 位。<br/>
/// 单帧情况下直接单包封装，Marker 位置 1。
/// </remarks>
public class RtpPacketizer
{
    private readonly Byte _payloadType;
    private readonly UInt16 _mtu;
    private readonly RtpSession _session;

    /// <summary>负载类型（0=PCMU, 8=PCMA, 96=Opus, 97=AAC 等）</summary>
    public Byte PayloadType => _payloadType;

    /// <summary>最大传输单元（含 12 字节 RTP 头）</summary>
    public UInt16 Mtu => _mtu;

    /// <summary>初始化 RTP 封包器</summary>
    /// <param name="payloadType">RTP 负载类型</param>
    /// <param name="mtu">最大传输单元，默认 1400</param>
    /// <param name="session">RTP 会话（共享序列号和时间戳）</param>
    public RtpPacketizer(Byte payloadType, UInt16 mtu = 1400, RtpSession session = null)
    {
        _payloadType = payloadType;
        _mtu = mtu;
        _session = session ?? new RtpSession();
    }

    /// <summary>将编码帧封装为一个或多个 RTP 包</summary>
    /// <param name="frame">编码音频帧</param>
    /// <param name="timestampIncrement">时间戳增量（采样数）</param>
    /// <returns>RTP 包数组</returns>
    public RtpPacket[] Packetize(Byte[] frame, UInt32 timestampIncrement)
    {
        if (frame == null || frame.Length == 0) return [];

        var maxPayloadSize = _mtu - 12; // 减去 RTP 头
        var packetCount = (frame.Length + maxPayloadSize - 1) / maxPayloadSize;
        var packets = new RtpPacket[packetCount];
        var ts = _session.NextTimestamp(timestampIncrement);

        for (var i = 0; i < packetCount; i++)
        {
            var offset = i * maxPayloadSize;
            var len = Math.Min(maxPayloadSize, frame.Length - offset);
            var isLast = i == packetCount - 1;

            var payload = new Byte[len];
            Array.Copy(frame, offset, payload, 0, len);

            packets[i] = new RtpPacket
            {
                Version = 2,
                Padding = false,
                Extension = false,
                CsrcCount = 0,
                Marker = isLast,
                PayloadType = _payloadType,
                SequenceNumber = _session.NextSequenceNumber(),
                Timestamp = ts,
                Ssrc = _session.Ssrc,
                Payload = payload,
            };
        }

        return packets;
    }
}
