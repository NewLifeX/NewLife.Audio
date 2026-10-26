using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XUnitTest.Tts;

/// <summary>一次请求的快照。请求对象在 SendAsync 返回后会被释放，因此只保留可断言的数据</summary>
internal sealed class HttpCall
{
    public String Method { get; set; }

    public Uri Uri { get; set; }

    public String UriText => Uri?.AbsoluteUri;

    public String Body { get; set; }

    public String ContentType { get; set; }

    public IDictionary<String, String> Headers { get; } = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>按路由返回响应的 HTTP 桩，同时记录请求快照</summary>
internal sealed class RouteHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpCall, Boolean> Match, Func<HttpCall, HttpResponseMessage> Respond)> _routes = [];

    public List<HttpCall> Calls { get; } = [];

    public RouteHandler Map(Func<HttpCall, Boolean> match, Func<HttpCall, HttpResponseMessage> respond)
    {
        _routes.Add((match, respond));
        return this;
    }

    public RouteHandler Map(String uriPart, Func<HttpCall, HttpResponseMessage> respond) =>
        Map(c => c.UriText != null && c.UriText.Contains(uriPart), respond);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var call = new HttpCall
        {
            Method = request.Method.Method,
            Uri = request.RequestUri,
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(),
            ContentType = request.Content?.Headers.ContentType?.MediaType,
        };
        foreach (var item in request.Headers) call.Headers[item.Key] = String.Join(",", item.Value);
        if (request.Content != null)
        {
            foreach (var item in request.Content.Headers) call.Headers[item.Key] = String.Join(",", item.Value);
        }

        Calls.Add(call);

        foreach (var route in _routes)
        {
            if (route.Match(call)) return route.Respond(call);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no route: " + call.UriText) };
    }
}

/// <summary>云服务响应与请求体解析</summary>
internal static class CloudTtsHelper
{
    public static HttpResponseMessage Audio(Byte[] data, String mediaType)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    public static HttpResponseMessage Json(String json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status) =>
        new(status) { Content = new StringContent("error") };

    public static String Base64Wav(Int32 sampleRate, Int32 dataSize) =>
        Convert.ToBase64String(TtsTestHelper.BuildWav(sampleRate, dataSize));

    public static IDictionary<String, String> ParseForm(String body)
    {
        var map = new Dictionary<String, String>(StringComparer.Ordinal);
        if (String.IsNullOrEmpty(body)) return map;

        foreach (var pair in body.Split('&'))
        {
            var index = pair.IndexOf('=');
            if (index <= 0) continue;

            var key = Uri.UnescapeDataString(pair.Substring(0, index).Replace("+", " "));
            var value = Uri.UnescapeDataString(pair.Substring(index + 1).Replace("+", " "));
            map[key] = value;
        }

        return map;
    }

    /// <summary>阿里云 RPC 签名使用的百分号编码规则</summary>
    public static String AliyunEncode(String value) => Uri.EscapeDataString(value ?? "")
        .Replace("+", "%20")
        .Replace("*", "%2A")
        .Replace("%7E", "~");

    /// <summary>从查询串里取参数（已解码）</summary>
    public static IDictionary<String, String> ParseQuery(Uri uri)
    {
        var map = new Dictionary<String, String>(StringComparer.Ordinal);
        var query = uri.Query;
        if (String.IsNullOrEmpty(query)) return map;

        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var index = pair.IndexOf('=');
            if (index <= 0) continue;

            map[Uri.UnescapeDataString(pair.Substring(0, index))] = Uri.UnescapeDataString(pair.Substring(index + 1));
        }

        return map;
    }
}
