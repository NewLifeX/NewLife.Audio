using System.Net.Http;
using NewLife.Data;

namespace NewLife.Audio.Streaming;

/// <summary>HTTP 音频流源。从 HTTP 服务器拉取音频流（Icecast/SHOUTcast 协议）</summary>
public class HttpAudioStreamSource : IDisposable
{
    private readonly String _url;
    private HttpClient _client;
    private CancellationTokenSource _cts;

    /// <summary>音频流 URL</summary>
    public String Url => _url;

    /// <summary>流元数据（从 Icy-* 头解析）</summary>
    public String StreamName { get; private set; }

    /// <summary>格式描述</summary>
    public String ContentType { get; private set; }

    /// <summary>音频帧可用事件</summary>
    public event EventHandler<Byte[]> AudioDataReceived;

    /// <summary>初始化 HTTP 音频流源</summary>
    /// <param name="url">流 URL</param>
    public HttpAudioStreamSource(String url)
    {
        _url = url ?? throw new ArgumentNullException(nameof(url));
        _client = new HttpClient();
    }

    /// <summary>开始接收音频流</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var response = await _client.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
        response.EnsureSuccessStatusCode();

        ContentType = response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";

        // 解析 Icy-* 头
        if (response.Headers.TryGetValues("icy-name", out var names))
            StreamName = String.Join("; ", names);

        using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new Byte[4096];

        while (!_cts.IsCancellationRequested)
        {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP
            var read = await stream.ReadAsync(buffer, _cts.Token);
#else
            var read = await stream.ReadAsync(buffer, 0, buffer.Length);
#endif
            if (read == 0) break;

            var chunk = new Byte[read];
            Array.Copy(buffer, 0, chunk, 0, read);
            AudioDataReceived?.Invoke(this, chunk);
        }
    }

    /// <summary>停止接收</summary>
    public void Stop() => _cts?.Cancel();

    /// <summary>释放</summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _client?.Dispose();
    }
}
