using System;
using System.Linq;
using System.Threading.Tasks;
using NewLife.Audio.Streaming;
using Xunit;

namespace XUnitTest.Streaming;

public class RtpSessionTests
{
    [Fact(DisplayName = "RTP会话序列号逐次递增")]
    public void NextSequenceNumber_Increments()
    {
        var session = new RtpSession();
        var first = session.NextSequenceNumber();
        var second = session.NextSequenceNumber();

        Assert.Equal((UInt16)(first + 1), second);
    }

    [Fact(DisplayName = "RTP会话时间戳按增量推进")]
    public void NextTimestamp_AddsIncrement()
    {
        var session = new RtpSession();
        var first = session.NextTimestamp(160);
        var second = session.NextTimestamp(320);

        Assert.Equal(first + 160u, second);
    }

    [Fact(DisplayName = "RTP会话Reset后序列号与时间戳变化")]
    public void Reset_ChangesState()
    {
        var session = new RtpSession();
        var seq = session.CurrentSequenceNumber;
        var ts = session.CurrentTimestamp;

        session.Reset();

        // 随机值可能极小概率相同，连续重置后至少有一项变化
        Assert.True(session.CurrentSequenceNumber != seq || session.CurrentTimestamp != ts);
    }

    [Fact(DisplayName = "RTP会话SSRC为随机非零值")]
    public void Ssrc_IsNonZero()
    {
        var session = new RtpSession();
        Assert.NotEqual(0u, session.Ssrc);
    }
}

public class RtpPacketizerTests
{
    [Fact(DisplayName = "RTP封包器单帧封装为单包并置Marker位")]
    public void Packetize_SingleFrame_ReturnsSinglePacket()
    {
        var session = new RtpSession();
        var packetizer = new RtpPacketizer(8, 1400, session);
        var frame = new Byte[] { 1, 2, 3, 4, 5 };

        var packets = packetizer.Packetize(frame, 160);

        var packet = Assert.Single(packets);
        Assert.True(packet.Marker);
        Assert.Equal(8, packet.PayloadType);
        Assert.Equal(2, packet.Version);
        Assert.Equal(session.Ssrc, packet.Ssrc);
        Assert.Equal(frame, packet.Payload);
        Assert.Equal(session.CurrentTimestamp - 160u, packet.Timestamp);
    }

    [Fact(DisplayName = "RTP封包器按MTU分片且仅末包置Marker位")]
    public void Packetize_LargeFrame_FragmentsByMtu()
    {
        var packetizer = new RtpPacketizer(96, 16);
        var frame = Enumerable.Range(0, 20).Select(i => (Byte)i).ToArray();

        var packets = packetizer.Packetize(frame, 960);

        Assert.Equal(5, packets.Length);
        Assert.False(packets[0].Marker);
        Assert.False(packets[1].Marker);
        Assert.False(packets[2].Marker);
        Assert.False(packets[3].Marker);
        Assert.True(packets[4].Marker);
        Assert.Equal(frame, packets.SelectMany(p => p.Payload).ToArray());

        for (var i = 1; i < packets.Length; i++)
        {
            Assert.Equal((UInt16)(packets[i - 1].SequenceNumber + 1), packets[i].SequenceNumber);
            Assert.Equal(packets[0].Timestamp, packets[i].Timestamp);
        }
    }

    [Fact(DisplayName = "RTP封包器空帧返回空数组")]
    public void Packetize_EmptyFrame_ReturnsEmpty()
    {
        var packetizer = new RtpPacketizer(0);
        Assert.Empty(packetizer.Packetize([], 160));
        Assert.Empty(packetizer.Packetize(null, 160));
    }

    [Fact(DisplayName = "RTP封包器暴露负载类型与MTU")]
    public void Properties_ReflectConstructor()
    {
        var packetizer = new RtpPacketizer(97, 1200);
        Assert.Equal(97, packetizer.PayloadType);
        Assert.Equal(1200, packetizer.Mtu);
    }
}

public class RtpDepacketizerTests
{
    [Fact(DisplayName = "RTP解包器单包直接输出完整帧")]
    public void Submit_SinglePacket_ReturnsFrame()
    {
        var depacketizer = new RtpDepacketizer();
        var packet = new RtpPacket { SequenceNumber = 100, Marker = true, Payload = [1, 2, 3] };

        Assert.True(depacketizer.Submit(packet, out var frame));
        Assert.Equal(new Byte[] { 1, 2, 3 }, frame);
        Assert.Equal(1, depacketizer.TotalReceived);
        Assert.Equal(0, depacketizer.TotalLost);
    }

    [Fact(DisplayName = "RTP解包器按Marker位重组分片帧")]
    public void Submit_FragmentedPackets_Reassembles()
    {
        var depacketizer = new RtpDepacketizer();

        Assert.False(depacketizer.Submit(new RtpPacket { SequenceNumber = 10, Marker = false, Payload = [1, 2] }, out _));
        Assert.True(depacketizer.Submit(new RtpPacket { SequenceNumber = 11, Marker = true, Payload = [3, 4] }, out var frame));

        Assert.Equal(new Byte[] { 1, 2, 3, 4 }, frame);
        Assert.Equal(2, depacketizer.TotalReceived);
    }

    [Fact(DisplayName = "RTP解包器检测序列号跳变并统计丢包")]
    public void Submit_LostPacket_TracksLoss()
    {
        var depacketizer = new RtpDepacketizer();

        depacketizer.Submit(new RtpPacket { SequenceNumber = 100, Marker = false, Payload = [1] }, out _);
        depacketizer.Submit(new RtpPacket { SequenceNumber = 102, Marker = false, Payload = [2] }, out _);

        Assert.Equal(1, depacketizer.TotalLost);
        Assert.True(depacketizer.LossRate > 0);
    }

    [Fact(DisplayName = "RTP解包器序列号回绕时正常重组不挂死")]
    public void Submit_SequenceWrap_Reassembles()
    {
        var task = Task.Run(() =>
        {
            var depacketizer = new RtpDepacketizer();
            depacketizer.Submit(new RtpPacket { SequenceNumber = 65534, Marker = false, Payload = [1] }, out _);
            depacketizer.Submit(new RtpPacket { SequenceNumber = 65535, Marker = true, Payload = [2] }, out var frame);
            return frame;
        });

        Assert.True(task.Wait(TimeSpan.FromSeconds(5)), "序列号回绕时解包器挂死");
        Assert.Equal(new Byte[] { 1, 2 }, task.Result);
    }

    [Fact(DisplayName = "RTP解包器Reset清空统计与缓存")]
    public void Reset_ClearsState()
    {
        var depacketizer = new RtpDepacketizer();
        depacketizer.Submit(new RtpPacket { SequenceNumber = 1, Marker = false, Payload = [1] }, out _);

        depacketizer.Reset();

        Assert.Equal(0, depacketizer.TotalReceived);
        Assert.Equal(0, depacketizer.TotalLost);
        Assert.Equal(0f, depacketizer.LossRate);

        Assert.True(depacketizer.Submit(new RtpPacket { SequenceNumber = 50, Marker = true, Payload = [9] }, out var frame));
        Assert.Equal(new Byte[] { 9 }, frame);
    }
}
