using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NewLife.Audio.Tts;

// TtsNotify —— 局域网语音播报器（UDP 收文字 → 本机 TTS 播报）
// ---------------------------------------------------------------------------
// 场景：设备自检、烧写产线、长时间构建的结果不必盯屏幕——局域网任一机器广播一行文字，本机音箱直接念出来。
//       同一个程序两种角色：默认常驻监听；--send 时改为向局域网广播一句话。
//
//   TtsNotify                        常驻监听（默认 UDP 5502，并加入组播 239.255.42.99）
//   TtsNotify --port 5503            指定端口
//   TtsNotify --no-group             不加入组播（只收广播/单播）
//   TtsNotify --voice 慧慧 --rate 1.1 --volume 0.9
//   TtsNotify --test                 本机把测试语料念一遍（先确认音箱与音色）
//   TtsNotify --say "A6 自检通过"    本机直接念一句，不走网络
//   TtsNotify --send "A6 自检通过"   向局域网广播一句（--host 可指定目标，默认 255.255.255.255）
//   TtsNotify --list-voices          列出本机可用音色
//
// 报文：UTF-8 纯文本一行；也接受 JSON {"text":"...","device":"..."}（设备端 A2/A4 镜像的
//       usr/sbin/ttsnotify 即按此协议发送：默认 UDP 5502、组播 239.255.42.99）。
// 发送端示例（任何语言，发一行 UTF-8 文本即可）：
//   pwsh -c "$c=[Net.Sockets.UdpClient]::new(); $c.EnableBroadcast=$true; $b=[Text.Encoding]::UTF8.GetBytes('A6 自检通过'); $c.Send($b,$b.Length,[Net.IPEndPoint]::new([Net.IPAddress]::Broadcast,5502))"
//
// 引擎：Windows 走系统 SAPI（本示例已引用 System.Speech）、Linux 走 espeak-ng、macOS 走 say。

Console.OutputEncoding = Encoding.UTF8;

var options = new NotifyOptions();
if (!options.Parse(args)) return;

// 测试语料：先念一遍确认音箱、音量与中文音色都正常
String[] testPhrases =
[
    "语音播报器已启动",
    "A6 烧写完成，自检全部通过，可以换下一台了",
];

using var tts = new TtsClient();
var ttsOptions = new TtsOptions
{
    Voice = ResolveVoice(tts, options.Voice),
    Rate = options.Rate,
    Volume = options.Volume,
};

switch (options.Mode)
{
    case "voices":
        ListVoices(tts);
        return;
    case "say":
        await SpeakAsync(tts, options.Text, ttsOptions, CancellationToken.None);
        return;
    case "test":
        foreach (var text in testPhrases)
            await SpeakAsync(tts, text, ttsOptions, CancellationToken.None);
        return;
    case "send":
        Send(options);
        return;
}

await ListenAsync(tts, options, ttsOptions);

// 音色支持中文名模糊匹配（如"慧慧"），匹配不上按原值当作引擎原生音色标识
static String ResolveVoice(TtsClient tts, String keyword)
{
    if (String.IsNullOrEmpty(keyword)) return null;

    var voice = tts.GetVoices().FirstOrDefault(e =>
        (!String.IsNullOrEmpty(e.Name) && e.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
        (!String.IsNullOrEmpty(e.Id) && e.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase)));

    return voice?.Id ?? keyword;
}

static void ListVoices(TtsClient tts)
{
    var voices = tts.GetVoices();
    if (voices.Count == 0)
    {
        Console.WriteLine($"引擎 {tts.Engine?.Name} 未枚举到音色；Linux 需安装 espeak-ng，Windows 需 System.Speech 包");
        return;
    }

    Console.WriteLine($"引擎 {tts.Engine.Name}，共 {voices.Count} 个音色：");
    foreach (var voice in voices)
        Console.WriteLine($"  {(voice.IsDefault ? "*" : " ")} {voice.Name} | {voice.Culture} | {voice.Gender} | Id={voice.Id}");
}

static async Task SpeakAsync(TtsClient tts, String text, TtsOptions options, CancellationToken cancellationToken)
{
    var watch = Stopwatch.StartNew();
    try
    {
        await tts.SayAsync(text, options, cancellationToken);
        Console.WriteLine($"[播报] {text}（{watch.Elapsed.TotalSeconds:F1}s）");
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        Console.WriteLine($"[失败] {text}：{ex.Message}");
    }
}

// 报文兼容：纯文本直接使用；JSON 取 text 字段（device 仅用于控制台展示）
static String ParsePayload(String raw, out String device)
{
    device = null;
    if (!raw.StartsWith('{')) return raw;

    try
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return raw;

        if (root.TryGetProperty("device", out var d) && d.ValueKind == JsonValueKind.String) device = d.GetString();
        if (root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString();
    }
    catch (JsonException) { }

    return raw;
}

static void Send(NotifyOptions options)
{
    using var client = new UdpClient();
    client.EnableBroadcast = true;
    client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);

    // 默认只发广播；显式指定 --group 时再补发组播（两者同时发会让收听端念两遍）
    var bytes = Encoding.UTF8.GetBytes(options.Text);
    var targets = new List<String> { options.Host };
    if (options.UseGroup && options.GroupExplicit && !String.IsNullOrEmpty(options.Group)) targets.Add(options.Group);

    foreach (var target in targets)
    {
        try
        {
            var count = client.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse(target), options.Port));
            Console.WriteLine($"[发送] {target}:{options.Port} → {options.Text}（{count} 字节）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[失败] 发送到 {target}：{ex.Message}");
        }
    }
}

static async Task ListenAsync(TtsClient tts, NotifyOptions options, TtsOptions ttsOptions)
{
    using var client = new UdpClient();
    try
    {
        // 允许同端口多实例（调试时常与旧实例并存）
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, options.Port));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[失败] 绑定 UDP {options.Port}：{ex.Message}");
        return;
    }

    if (options.UseGroup && !String.IsNullOrEmpty(options.Group))
    {
        try { client.JoinMulticastGroup(IPAddress.Parse(options.Group)); }
        catch (Exception ex) { Console.WriteLine($"[警告] 加入组播 {options.Group} 失败：{ex.Message}（广播/单播不受影响）"); }
    }

    // 有界队列：播报慢于来报时丢最旧的，避免越积越多
    var queue = Channel.CreateBounded<String>(new BoundedChannelOptions(32)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
    });

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (s, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    var speaker = Task.Run(async () =>
    {
        try
        {
            await foreach (var text in queue.Reader.ReadAllAsync(cts.Token))
                await SpeakAsync(tts, text, ttsOptions, cts.Token);
        }
        catch (OperationCanceledException) { }
    });

    Console.WriteLine($"引擎 {tts.Engine?.Name}；收听中：udp://0.0.0.0:{options.Port}" + (options.UseGroup ? $"，组播 {options.Group}" : ""));
    Console.WriteLine("局域网内广播一行 UTF-8 文本即可（Ctrl+C 退出）");
    Console.WriteLine();

    // 多网卡主机会收到同一广播的多份副本（每个网卡一份），短时间内的相同文本只念一次
    var lastText = (String)null;
    var lastTime = DateTime.MinValue;

    while (!cts.IsCancellationRequested)
    {
        UdpReceiveResult result;
        try
        {
            result = await client.ReceiveAsync(cts.Token);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            Console.WriteLine($"[接收异常] {ex.Message}");
            continue;
        }

        var raw = Encoding.UTF8.GetString(result.Buffer).Trim('\uFEFF', '\0', ' ', '\r', '\n', '\t');
        if (raw.Length == 0) continue;

        var text = ParsePayload(raw, out var device);
        if (String.IsNullOrEmpty(text)) continue;
        if (text.Length > 500) text = text[..500];

        Console.WriteLine(device == null
            ? $"[收到] {result.RemoteEndPoint} → {text}"
            : $"[收到] {result.RemoteEndPoint}（{device}）→ {text}");
        if (text == lastText && (DateTime.Now - lastTime).TotalSeconds < 2)
        {
            Console.WriteLine("[忽略] 短时间内重复报文");
            continue;
        }

        lastText = text;
        lastTime = DateTime.Now;
        queue.Writer.TryWrite(text);
    }

    queue.Writer.TryComplete();
    try { await speaker; } catch { /* 退出时的取消异常 */ }
    Console.WriteLine("已退出");
}

/// <summary>命令行参数</summary>
sealed class NotifyOptions
{
    /// <summary>UDP 端口（与设备端 usr/sbin/ttsnotify 的默认端口一致）</summary>
    public Int32 Port { get; set; } = 5502;

    /// <summary>广播目标地址</summary>
    public String Host { get; set; } = "255.255.255.255";

    /// <summary>组播地址</summary>
    public String Group { get; set; } = "239.255.42.99";

    /// <summary>是否启用组播</summary>
    public Boolean UseGroup { get; set; } = true;

    /// <summary>是否显式指定过组播地址（发送端只在显式指定时才补发组播）</summary>
    public Boolean GroupExplicit { get; set; }

    /// <summary>音色关键字或原生标识</summary>
    public String Voice { get; set; }

    /// <summary>语速倍率</summary>
    public Single Rate { get; set; } = 1;

    /// <summary>音量 0~1</summary>
    public Single Volume { get; set; } = 1;

    /// <summary>运行模式：listen / say / test / send / voices</summary>
    public String Mode { get; set; } = "listen";

    /// <summary>要播报的文本</summary>
    public String Text { get; set; }

    /// <summary>解析参数</summary>
    /// <param name="args">命令行参数</param>
    /// <returns>参数合法返回 true</returns>
    public Boolean Parse(String[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h":
                case "--help":
                    Help();
                    return false;
                case "--port":
                    if (!Next(args, ref i, out var port) || !Int32.TryParse(port, out var p) || p is <= 0 or > 65535) return Error("--port 需要 1~65535 的端口号");
                    Port = p;
                    break;
                case "--host":
                    if (!Next(args, ref i, out var host)) return Error("--host 缺少地址");
                    Host = host;
                    break;
                case "--group":
                    if (!Next(args, ref i, out var group)) return Error("--group 缺少地址");
                    Group = group;
                    GroupExplicit = true;
                    break;
                case "--no-group":
                    UseGroup = false;
                    break;
                case "--voice":
                    if (!Next(args, ref i, out var voice)) return Error("--voice 缺少音色");
                    Voice = voice;
                    break;
                case "--rate":
                    if (!Next(args, ref i, out var rate) || !Single.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) return Error("--rate 需要数字，如 1.2");
                    Rate = r;
                    break;
                case "--volume":
                    if (!Next(args, ref i, out var volume) || !Single.TryParse(volume, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return Error("--volume 需要 0~1 的数字");
                    Volume = Math.Clamp(v, 0, 1);
                    break;
                case "--say":
                    if (!Next(args, ref i, out var say)) return Error("--say 缺少文本");
                    Mode = "say";
                    Text = say;
                    break;
                case "--send":
                    if (!Next(args, ref i, out var send)) return Error("--send 缺少文本");
                    Mode = "send";
                    Text = send;
                    break;
                case "--test":
                    Mode = "test";
                    break;
                case "--list-voices":
                    Mode = "voices";
                    break;
                default:
                    return Error($"未知参数：{args[i]}");
            }
        }

        return true;
    }

    static Boolean Next(String[] args, ref Int32 index, out String value)
    {
        if (index + 1 >= args.Length)
        {
            value = null;
            return false;
        }

        value = args[++index];
        return true;
    }

    static Boolean Error(String message)
    {
        Console.WriteLine(message);
        Help();
        return false;
    }

    static void Help()
    {
        Console.WriteLine("""
            用法：TtsNotify [选项]

              （无参数）            常驻监听 UDP 5502，收到文本即播报
              --port <端口>         UDP 端口，默认 5502（与设备端 ttsnotify 一致）
              --group <地址>        组播地址（收听默认加入；发送时需显式指定才补发组播）
              --no-group            不加入组播（只收广播/单播）
              --voice <名称>        音色（支持中文名模糊匹配，如 慧慧）
              --rate <倍率>         语速，默认 1
              --volume <0~1>        音量，默认 1
              --test                本机播报测试语料
              --say <文本>          本机直接播报一句，不走网络
              --send <文本>         向局域网广播一句（--host 指定目标）
              --host <地址>         广播目标，默认 255.255.255.255
              --list-voices         列出本机可用音色
              -h, --help            帮助
            """);
    }
}
