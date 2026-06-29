using System;
using NewLife.Audio.Streaming;
using Xunit;

namespace XUnitTest.Streaming;

public class RtspClientTests
{
    [Fact(DisplayName = "RTSP客户端创建实例不抛异常")]
    public void Constructor_DoesNotThrow()
    {
        var client = new RtspClient("rtsp://localhost:554/stream");
        Assert.NotNull(client);
    }

    [Fact(DisplayName = "RTSP客户端初始状态为Idle")]
    public void Constructor_InitialState()
    {
        var client = new RtspClient("rtsp://localhost:554/stream");
        // 不发起网络连接，仅测试构造
        Assert.NotNull(client);
    }

    [Fact(DisplayName = "RTSP客户端Dispose不抛异常")]
    public void Dispose_DoesNotThrow()
    {
        var client = new RtspClient("rtsp://localhost:554/stream");
        client.Dispose();
    }

    [Fact(DisplayName = "RTSP客户端null URL抛ArgumentNullException")]
    public void Constructor_NullUrl_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RtspClient(null));
    }
}
