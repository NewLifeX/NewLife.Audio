using System;
using NewLife.Audio.Streaming;
using Xunit;

namespace XUnitTest.Streaming;

public class RtpPacketTests
{
    [Fact(DisplayName = "RTP包序列化→反序列化往返正确")]
    public void RoundTrip_PreservesAllFields()
    {
        var original = new RtpPacket
        {
            Version = 2,
            Marker = true,
            PayloadType = 8, // G.711 A-law
            SequenceNumber = 12345,
            Timestamp = 67890,
            Ssrc = 0x12345678,
            Payload = [0xAA, 0xBB, 0xCC, 0xDD],
        };

        var bytes = original.ToBytes();
        Assert.True(bytes.Length >= 12 + 4);

        var parsed = RtpPacket.FromBytes(bytes);
        Assert.Equal(original.Version, parsed.Version);
        Assert.Equal(original.Marker, parsed.Marker);
        Assert.Equal(original.PayloadType, parsed.PayloadType);
        Assert.Equal(original.SequenceNumber, parsed.SequenceNumber);
        Assert.Equal(original.Timestamp, parsed.Timestamp);
        Assert.Equal(original.Ssrc, parsed.Ssrc);
        Assert.Equal(original.Payload.Length, parsed.Payload.Length);
        Assert.Equal(original.Payload[0], parsed.Payload[0]);
    }

    [Fact(DisplayName = "RTP包含CSRC时正确解析")]
    public void WithCsrc_ParsesCorrectly()
    {
        var packet = new RtpPacket
        {
            Version = 2,
            CsrcCount = 1,
            PayloadType = 0,
            SequenceNumber = 1,
            Timestamp = 100,
            Ssrc = 0x11111111,
            CsrcList = [0x22222222],
            Payload = [0x01],
        };

        var bytes = packet.ToBytes();
        Assert.True(bytes.Length >= 12 + 4 + 1); // header + CSRC + payload

        var parsed = RtpPacket.FromBytes(bytes);
        Assert.Equal(1, parsed.CsrcCount);
        if (parsed.CsrcList != null && parsed.CsrcList.Length > 0)
            Assert.Equal(0x22222222u, parsed.CsrcList[0]);
    }

    [Fact(DisplayName = "RTP解析不完整数据抛异常")]
    public void FromBytes_TooShort_Throws()
    {
        Assert.Throws<ArgumentException>(() => RtpPacket.FromBytes(new Byte[5]));
    }
}

public class JitterBufferTests
{
    [Fact(DisplayName = "JitterBuffer顺序写入可正确读取")]
    public void OrderedWrite_ReadsInOrder()
    {
        var jb = new JitterBuffer(60, 20);

        jb.Write(100, [0x01], 1000);
        jb.Write(101, [0x02], 1020);
        jb.Write(102, [0x03], 1040);

        Assert.True(jb.Read(out var p1));
        Assert.Equal(0x01, p1[0]);

        Assert.True(jb.Read(out var p2));
        Assert.Equal(0x02, p2[0]);

        Assert.True(jb.Read(out var p3));
        Assert.Equal(0x03, p3[0]);
    }

    [Fact(DisplayName = "JitterBuffer未初始化时Read返回false")]
    public void Read_NotInitialized_ReturnsFalse()
    {
        var jb = new JitterBuffer();
        Assert.False(jb.Read(out _));
    }

    [Fact(DisplayName = "JitterBuffer Reset清空状态")]
    public void Reset_ClearsBuffer()
    {
        var jb = new JitterBuffer();
        jb.Write(100, [0x01], 1000);
        jb.Reset();
        Assert.False(jb.Read(out _));
    }
}
