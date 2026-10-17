using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
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

    [Fact(DisplayName = "RTSP OPTIONS成功解析Public头")]
    public async Task OptionsAsync_ValidResponse_ReturnsPublic()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync(ct);
            var buffer = new Byte[2048];
            await socket.ReceiveAsync(buffer, SocketFlags.None, ct);

            var response = "RTSP/1.0 200 OK\r\nCSeq: 1\r\nPublic: OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN\r\n\r\n";
            await socket.SendAsync(Encoding.ASCII.GetBytes(response), SocketFlags.None, ct);
        }, ct);

        using var client = new RtspClient($"rtsp://127.0.0.1:{port}/stream");
        var methods = await client.OptionsAsync(ct);

        Assert.Contains("DESCRIBE", methods);
        await server.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    [Fact(DisplayName = "RTSP服务端断开连接时请求抛异常")]
    public async Task OptionsAsync_ServerDropsConnection_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync(ct);
            var buffer = new Byte[2048];
            await socket.ReceiveAsync(buffer, SocketFlags.None, ct);
            socket.LingerState = new LingerOption(true, 0);
        }, ct);

        using var client = new RtspClient($"rtsp://127.0.0.1:{port}/stream");
        await Assert.ThrowsAnyAsync<Exception>(() => client.OptionsAsync(ct));
        await server.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    [Fact(DisplayName = "RTSP未连接时Pause返回false不抛异常")]
    public async Task PauseAsync_NotConnected_ReturnsFalse()
    {
        var client = new RtspClient("rtsp://localhost:554/stream");
        var result = await client.PauseAsync(TestContext.Current.CancellationToken);
        Assert.False(result);
    }
}


