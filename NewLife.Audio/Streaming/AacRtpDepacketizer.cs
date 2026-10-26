namespace NewLife.Audio.Streaming;


/// <summary>AAC RTP 解包器（RFC 3640 AAC-hbr）</summary>
/// <remarks>按时间戳聚合分片，Marker 到达时输出完整 AAC access unit。</remarks>
public sealed class AacRtpDepacketizer
{
    private readonly MemoryStream _pending = new();
    private UInt32 _timestamp;
    private Boolean _hasTimestamp;

    /// <summary>提交一个 RTP 包</summary>
    /// <param name="packet">RTP 包</param>
    /// <param name="accessUnit">完整 AAC access unit（若 Marker 到达）</param>
    /// <returns>是否输出完整 access unit</returns>
    public Boolean Submit(RtpPacket packet, out Byte[] accessUnit)
    {
        accessUnit = null;
        var payload = packet.Payload;
        if (payload == null || payload.Length < 4) return false;

        var headerBits = (payload[0] << 8) | payload[1];
        var headerBytes = headerBits / 8;
        if (headerBits % 8 != 0 || headerBytes < 2 || 2 + headerBytes > payload.Length) return false;

        if (!_hasTimestamp || _timestamp != packet.Timestamp)
        {
            _pending.SetLength(0);
            _timestamp = packet.Timestamp;
            _hasTimestamp = true;
        }

        // 当前实现每个包一个 AU-header；多 AU 包的数据仍按顺序拼接，由 Marker 统一输出
        var offset = 2 + headerBytes;
        _pending.Write(payload, offset, payload.Length - offset);

        if (!packet.Marker) return false;

        accessUnit = _pending.ToArray();
        _pending.SetLength(0);
        _hasTimestamp = false;
        return accessUnit.Length > 0;
    }

    /// <summary>重置状态</summary>
    public void Reset()
    {
        _pending.SetLength(0);
        _timestamp = 0;
        _hasTimestamp = false;
    }
}
