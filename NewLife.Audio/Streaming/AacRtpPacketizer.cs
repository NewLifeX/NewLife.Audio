namespace NewLife.Audio.Streaming;

/// <summary>AAC RTP 封包器（RFC 3640 AAC-hbr）</summary>
/// <remarks>
/// 每个 RTP 包携带 16-bit AU-headers-length 与一个 16-bit AU-header（13-bit size + 3-bit index），
/// 随后是 AAC access unit 数据。AU 超过 MTU 时按数据分片，最后一个分片设置 Marker。
/// </remarks>
public sealed class AacRtpPacketizer
{
    private readonly Byte _payloadType;
    private readonly UInt16 _mtu;
    private readonly RtpSession _session;

    /// <summary>负载类型</summary>
    public Byte PayloadType => _payloadType;

    /// <summary>最大传输单元（含 RTP 头）</summary>
    public UInt16 Mtu => _mtu;

    /// <summary>初始化 AAC RTP 封包器</summary>
    /// <param name="payloadType">RTP 负载类型（通常为动态类型 96-127）</param>
    /// <param name="mtu">最大传输单元，默认 1400</param>
    /// <param name="session">RTP 会话</param>
    public AacRtpPacketizer(Byte payloadType, UInt16 mtu = 1400, RtpSession session = null)
    {
        if (mtu <= 16) throw new ArgumentOutOfRangeException(nameof(mtu));

        _payloadType = payloadType;
        _mtu = mtu;
        _session = session ?? new RtpSession();
    }

    /// <summary>将 AAC access unit 封装为 RTP 包</summary>
    /// <param name="accessUnit">raw AAC access unit</param>
    /// <param name="timestampIncrement">时间戳增量（采样数）</param>
    /// <returns>RTP 包数组</returns>
    public RtpPacket[] Packetize(Byte[] accessUnit, UInt32 timestampIncrement)
    {
        if (accessUnit == null || accessUnit.Length == 0) return [];

        var maxData = _mtu - 12 - 4; // RTP header + AU-headers-length + AU-header
        var count = (accessUnit.Length + maxData - 1) / maxData;
        var packets = new RtpPacket[count];
        var timestamp = _session.NextTimestamp(timestampIncrement);

        for (var i = 0; i < count; i++)
        {
            var offset = i * maxData;
            var length = Math.Min(maxData, accessUnit.Length - offset);
            var payload = new Byte[4 + length];

            // AU-headers-length = 16 bits
            payload[0] = 0;
            payload[1] = 16;

            // AU-header: AU-size(13) + AU-index(3)；分片时仍标记完整 AU 大小
            var auSize = accessUnit.Length << 3;
            payload[2] = (Byte)(auSize >> 8);
            payload[3] = (Byte)auSize;
            Array.Copy(accessUnit, offset, payload, 4, length);

            packets[i] = new RtpPacket
            {
                Version = 2,
                Padding = false,
                Extension = false,
                CsrcCount = 0,
                Marker = i == count - 1,
                PayloadType = _payloadType,
                SequenceNumber = _session.NextSequenceNumber(),
                Timestamp = timestamp,
                Ssrc = _session.Ssrc,
                Payload = payload,
            };
        }

        return packets;
    }
}
