using System;
using System.Threading;
using NewLife.Audio;
using NewLife.Audio.Devices;
using NewLife.Audio.DSP;

// ALSA 后端真机验证：枚举 / 播放（默认与直连硬件）/ 采集（默认与 null 设备）。
// 用法：AlsaProbe [all|enum|play|rec|rec-null]，任一失败返回非 0。

var mode = args.Length > 0 ? args[0] : "all";
var failed = 0;

void Step(String name, Action action)
{
    try
    {
        action();
        Console.WriteLine($"[PASS] {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"[FAIL] {name}: {ex.GetType().Name}: {ex.Message}");
    }
}

void Play(String deviceId)
{
    var format = new AudioFormat { SampleRate = 48000, Channels = 2, BitsPerSample = 16 };
    using var player = AudioDriverFactory.Create().CreatePlayer();
    player.Init(format, deviceId);
    player.Play();

    var frames = format.SampleRate / 2;
    var data = new Byte[frames * format.Channels * 2];
    for (var i = 0; i < frames; i++)
    {
        var v = (Int16)(12000 * Math.Sin(2 * Math.PI * 440 * i / format.SampleRate));
        for (var c = 0; c < format.Channels; c++)
        {
            data[(i * format.Channels + c) * 2] = (Byte)(v & 0xFF);
            data[(i * format.Channels + c) * 2 + 1] = (Byte)((v >> 8) & 0xFF);
        }
    }

    var chunk = data.Length / 4;
    for (var i = 0; i < 4; i++) player.Write(data.AsSpan(i * chunk, chunk).ToArray());

    Thread.Sleep(800);
    var left = player.BufferedBytes;
    player.Stop();

    Console.WriteLine($"       device={deviceId ?? "default"} bufferedBeforeStop={left}");
    if (left != 0) throw new InvalidOperationException($"缓冲区未消化完（{left} 字节）");
}

void Capture(String deviceId)
{
    var format = new AudioFormat { SampleRate = 48000, Channels = 2, BitsPerSample = 16 };
    using var recorder = AudioDriverFactory.Create().CreateRecorder();
    var bytes = 0L;
    var blocks = 0;
    recorder.DataAvailable += (s, data) =>
    {
        Interlocked.Add(ref bytes, data.Length);
        Interlocked.Increment(ref blocks);
    };

    recorder.Init(format, deviceId);
    recorder.StartRecording();
    Thread.Sleep(600);
    recorder.StopRecording();

    Console.WriteLine($"       device={deviceId ?? "default"} blocks={blocks} bytes={bytes}");
    if (bytes <= 0) throw new InvalidOperationException("没有采集到数据");
}

Console.WriteLine($"linux={AlsaDriver.IsLinux} available={AlsaDriver.IsAvailable} supported={AudioDriverFactory.IsSupported}");
Console.WriteLine($"backends=[{String.Join(",", AudioDriverFactory.GetAvailableBackends())}]");

if (mode is "all" or "enum")
{
    Step("枚举设备", () =>
    {
        var driver = new AlsaDriver();
        var list = driver.EnumerateDevices();
        var outs = 0;
        var ins = 0;
        foreach (var d in list)
        {
            if (d.IsInput) ins++;
            else outs++;
        }

        Console.WriteLine($"       devices={list.Count} outputs={outs} inputs={ins}");
        if (list.Count == 0) throw new InvalidOperationException("枚举结果为空");
        if (outs == 0) throw new InvalidOperationException("没有输出设备");

        var def = driver.GetDefaultOutputDevice();
        if (def == null) throw new InvalidOperationException("默认输出设备为 null");
        Console.WriteLine($"       default={def.Id}");
    });
}

if (mode is "all" or "play")
{
    Step("播放 0.5 秒 440Hz 正弦（默认设备）", () => Play(null));
    Step("播放 0.5 秒 440Hz 正弦（直连 hw:CARD=Device,DEV=0）", () => Play("hw:CARD=Device,DEV=0"));
}

if (mode is "all" or "rec") Step("采集 0.5 秒（默认设备）", () => Capture(null));
if (mode is "all" or "rec" or "rec-null") Step("采集 0.5 秒（null 设备，验证读取→事件通路）", () => Capture("null"));

Console.WriteLine(failed == 0 ? "=== ALL PASS ===" : $"=== {failed} FAILED ===");
return failed == 0 ? 0 : 1;
