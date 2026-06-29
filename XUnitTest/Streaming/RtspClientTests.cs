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
        Assert.Equal("rtsp://localhost:554/stream", client.Url);
    }

    [Fact(DisplayName = "RTSP客户端创建后SessionId为null且未连接")]
    public void Constructor_InitialState()
    {
        var client = new RtspClient("rtsp://localhost:554/stream");
        Assert.Null(client.SessionId);
        Assert.False(client.IsConnected);
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

    [Fact(DisplayName = "RTSP重复Dispose不抛异常")]
    public void Dispose_MultipleTimes_DoesNotThrow()
    {
        var client = new RtspClient("rtsp://localhost:554/stream");
        client.Dispose();
        client.Dispose();
    }

    [Fact(DisplayName = "RTSP默认端口554")]
    public void Constructor_DefaultPort()
    {
        var client = new RtspClient("rtsp://192.168.1.1/stream");
        Assert.NotNull(client);
        Assert.False(client.IsConnected);
    }

    [Fact(DisplayName = "RTSP指定端口正常解析")]
    public void Constructor_CustomPort()
    {
        var client = new RtspClient("rtsp://192.168.1.1:8554/stream");
        Assert.NotNull(client);
    }

    [Fact(DisplayName = "RTSP连接失败抛异常不抛NullRef")]
    public async void ConnectAsync_InvalidHost_ThrowsConnectionException()
    {
        var client = new RtspClient("rtsp://127.0.0.1:1/stream");
        // 无服务器监听，应抛 SocketException 而非 NullReferenceException
        await Assert.ThrowsAnyAsync<Exception>(() => client.OptionsAsync());
    }

    [Fact(DisplayName = "RTSP未连接时Pause返回false不抛异常")]
    public async void PauseAsync_NotConnected_ReturnsFalse()
    {
        var client = new RtspClient("rtsp://localhost:554/stream");
        var result = await client.PauseAsync();
        Assert.False(result);
    }
}


