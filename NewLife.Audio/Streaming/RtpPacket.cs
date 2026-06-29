using NewLife.Buffers;

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

        var reader = new SpanReader(data);
        reader.IsLittleEndian = false; // RTP 网络字节序为大端

        var packet = new RtpPacket();

        var b0 = reader.ReadByte();
        packet.Version = (Byte)((b0 >> 6) & 0x03);
        packet.Padding = ((b0 >> 5) & 0x01) != 0;
        packet.Extension = ((b0 >> 4) & 0x01) != 0;
        packet.CsrcCount = (Byte)(b0 & 0x0F);

        var b1 = reader.ReadByte();
        packet.Marker = ((b1 >> 7) & 0x01) != 0;
        packet.PayloadType = (Byte)(b1 & 0x7F);

        packet.SequenceNumber = reader.ReadUInt16();
        packet.Timestamp = reader.ReadUInt32();
        packet.Ssrc = reader.ReadUInt32();

        packet.CsrcList = new UInt32[packet.CsrcCount];
        for (var i = 0; i < packet.CsrcCount; i++)
        {
            packet.CsrcList[i] = reader.ReadUInt32();
        }

        var payloadLen = reader.Available;
        if (packet.Padding && payloadLen > 0)
        {
            // 填充长度在最后一个字节
            var paddingLen = data[data.Length - 1];
            payloadLen -= paddingLen;
        }

        packet.Payload = new Byte[Math.Max(0, payloadLen)];
        if (payloadLen > 0)
            reader.ReadBytes(payloadLen).CopyTo(packet.Payload.AsSpan());

        return packet;
    }

    /// <summary>将 RTP 包序列化为字节数组</summary>
    /// <returns>RTP 包字节数据</returns>
    public Byte[] ToBytes()
    {
        var headerSize = 12 + CsrcCount * 4;
        var totalSize = headerSize + (Payload?.Length ?? 0);
        var data = new Byte[totalSize];

        var writer = new SpanWriter(data);
        writer.IsLittleEndian = false; // RTP 网络字节序为大端

        writer.WriteByte((Byte)((Version << 6) | (Padding ? 0x20 : 0) | (Extension ? 0x10 : 0) | (CsrcCount & 0x0F)));
        writer.WriteByte((Byte)((Marker ? 0x80 : 0) | (PayloadType & 0x7F)));
        writer.Write(SequenceNumber);
        writer.Write(Timestamp);
        writer.Write(Ssrc);

        // 写入 CSRC 列表
        if (CsrcList != null)
        {
            for (var i = 0; i < Math.Min(CsrcList.Length, CsrcCount); i++)
            {
                writer.Write(CsrcList[i]);
            }
        }

        if (Payload != null && Payload.Length > 0)
            writer.Write(Payload.AsSpan());

        return data;
    }
}
