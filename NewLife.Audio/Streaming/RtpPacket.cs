using NewLife.Data;

namespace NewLife.Audio.Streaming;

/// <summary>RTP 包结构（RFC 3550）</summary>
public struct RtpPacket
{
    /// <summary>版本号（2）</summary>
    public Byte Version;

    /// <summary>填充标志</summary>
    public Boolean Padding;

    /// <summary>扩展标志</summary>
    public Boolean Extension;

    /// <summary>CSRC 计数</summary>
    public Byte CsrcCount;

    /// <summary>标记位</summary>
    public Boolean Marker;

    /// <summary>负载类型</summary>
    public Byte PayloadType;

    /// <summary>序列号</summary>
    public UInt16 SequenceNumber;

    /// <summary>时间戳</summary>
    public UInt32 Timestamp;

    /// <summary>同步源标识符</summary>
    public UInt32 Ssrc;

    /// <summary>贡献源标识符列表</summary>
    public UInt32[] CsrcList;

    /// <summary>负载数据</summary>
    public Byte[] Payload;

    /// <summary>从字节数组解析 RTP 包</summary>
    /// <param name="data">RTP 包字节数据</param>
    /// <returns>RTP 包结构</returns>
    public static RtpPacket FromBytes(Byte[] data)
    {
        if (data == null || data.Length < 12)
            throw new ArgumentException("RTP 包至少需要 12 字节头");

        var packet = new RtpPacket();

        var b0 = data[0];
        packet.Version = (Byte)((b0 >> 6) & 0x03);
        packet.Padding = ((b0 >> 5) & 0x01) != 0;
        packet.Extension = ((b0 >> 4) & 0x01) != 0;
        packet.CsrcCount = (Byte)(b0 & 0x0F);

        var b1 = data[1];
        packet.Marker = ((b1 >> 7) & 0x01) != 0;
        packet.PayloadType = (Byte)(b1 & 0x7F);

        packet.SequenceNumber = (UInt16)((data[2] << 8) | data[3]);
        packet.Timestamp = (UInt32)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);
        packet.Ssrc = (UInt32)((data[8] << 24) | (data[9] << 16) | (data[10] << 8) | data[11]);

        var offset = 12 + packet.CsrcCount * 4;
        packet.CsrcList = new UInt32[packet.CsrcCount];
        for (var i = 0; i < packet.CsrcCount; i++)
        {
            var csrcOffset = 12 + i * 4;
            packet.CsrcList[i] = (UInt32)((data[csrcOffset] << 24) | (data[csrcOffset + 1] << 16) |
                                         (data[csrcOffset + 2] << 8) | data[csrcOffset + 3]);
        }

        var payloadLen = data.Length - offset;
        if (packet.Padding && payloadLen > 0)
        {
            var paddingLen = data[data.Length - 1];
            payloadLen -= paddingLen;
        }

        packet.Payload = new Byte[Math.Max(0, payloadLen)];
        if (payloadLen > 0)
            Array.Copy(data, offset, packet.Payload, 0, payloadLen);

        return packet;
    }

    /// <summary>将 RTP 包序列化为字节数组</summary>
    /// <returns>RTP 包字节数据</returns>
    public Byte[] ToBytes()
    {
        var headerSize = 12 + CsrcCount * 4;
        var totalSize = headerSize + (Payload?.Length ?? 0);
        var data = new Byte[totalSize];

        data[0] = (Byte)((Version << 6) | (Padding ? 0x20 : 0) | (Extension ? 0x10 : 0) | (CsrcCount & 0x0F));
        data[1] = (Byte)((Marker ? 0x80 : 0) | (PayloadType & 0x7F));
        data[2] = (Byte)((SequenceNumber >> 8) & 0xFF);
        data[3] = (Byte)(SequenceNumber & 0xFF);
        data[4] = (Byte)((Timestamp >> 24) & 0xFF);
        data[5] = (Byte)((Timestamp >> 16) & 0xFF);
        data[6] = (Byte)((Timestamp >> 8) & 0xFF);
        data[7] = (Byte)(Timestamp & 0xFF);
        data[8] = (Byte)((Ssrc >> 24) & 0xFF);
        data[9] = (Byte)((Ssrc >> 16) & 0xFF);
        data[10] = (Byte)((Ssrc >> 8) & 0xFF);
        data[11] = (Byte)(Ssrc & 0xFF);

        // 写入 CSRC 列表
        var csrcOffset = 12;
        if (CsrcList != null)
        {
            for (var i = 0; i < Math.Min(CsrcList.Length, CsrcCount); i++)
            {
                var csrc = CsrcList[i];
                data[csrcOffset++] = (Byte)((csrc >> 24) & 0xFF);
                data[csrcOffset++] = (Byte)((csrc >> 16) & 0xFF);
                data[csrcOffset++] = (Byte)((csrc >> 8) & 0xFF);
                data[csrcOffset++] = (Byte)(csrc & 0xFF);
            }
        }

        if (Payload != null && Payload.Length > 0)
            Array.Copy(Payload, 0, data, headerSize, Payload.Length);

        return data;
    }
}
