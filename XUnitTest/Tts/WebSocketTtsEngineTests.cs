using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio.Tts;
using Xunit;

namespace XUnitTest.Tts;

public class WebSocketTtsEngineTests
{
    [Fact(DisplayName = "WebSocket TTS：逐帧接收裸 PCM")]
    public async Task Synthesize_ReceivesRawPcmFrames()
    {
        if (!OperatingSystem.IsWindows()) return;

        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var buffer = new Byte[1024];
            var result = await socket.ReceiveAsync(new ArraySegment<Byte>(buffer), CancellationToken.None);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            Assert.Equal("hello", Encoding.UTF8.GetString(buffer, 0, result.Count));

            var first = new Byte[320];
            var second = new Byte[640];
            Array.Fill(first, (Byte)1);
            Array.Fill(second, (Byte)2);

            await socket.SendAsync(new ArraySegment<Byte>(first), WebSocketMessageType.Binary, true, CancellationToken.None);
            await socket.SendAsync(new ArraySegment<Byte>(second), WebSocketMessageType.Binary, true, CancellationToken.None);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        });

        var engine = new WebSocketTtsEngine
        {
            Url = $"ws://127.0.0.1:{port}/",
            SampleRate = 16000,
            Channels = 1,
        };

        var chunks = new List<Byte[]>();
        await foreach (var chunk in engine.SynthesizeAsync("hello", null, CancellationToken.None))
            chunks.Add(chunk.Data);

        await server;

        Assert.Equal(2, chunks.Count);
        Assert.Equal(320, chunks[0].Length);
        Assert.Equal(640, chunks[1].Length);
        Assert.All(chunks[0], value => Assert.Equal(1, value));
        Assert.All(chunks[1], value => Assert.Equal(2, value));
    }

    private static Int32 GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
