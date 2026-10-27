using System;
using System.IO;
using System.Threading;
using NewLife.Audio;
using NewLife.Audio.DSP;
using NewLife.Audio.Tts;

// 编解码真机冒烟：在目标机上复验 AAC 外部参考向量的 SNR 与 espeak 语音合成，
// 用于确认跨架构（x64 / aarch64）行为一致——同一码流应给出同一组 SNR。
// 用法：CodecSmoke [all|aac|tts] [资产目录]，任一失败返回非 0。

var mode = args.Length > 0 ? args[0] : "all";
var assets = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "aacplus");
var failed = 0;

void Fail(String message)
{
    failed++;
    Console.WriteLine($"[FAIL] {message}");
}

void Pass(String message) => Console.WriteLine($"[PASS] {message}");

// 外部向量门禁与 XUnitTest/Codecs/AacExternalVectorTests 同口径：对齐后比较最小声道 SNR。
void Check(String name, String stream, String reference, Double skip, Double minSnr, Double minCorr)
{
    var ours = AudioFile.ReadPcm(Path.Combine(assets, stream));
    var refPcm = AudioFile.ReadPcm(Path.Combine(assets, reference));
    var format = ours.Format;

    var skipSamples = (Int32)(skip * format.SampleRate) * format.Channels;
    var samples = ToDoubles(ours.Data);
    if (skipSamples > 0 && skipSamples < samples.Length)
    {
        var trimmed = new Double[samples.Length - skipSamples];
        Array.Copy(samples, skipSamples, trimmed, 0, trimmed.Length);
        samples = trimmed;
    }

    var worstSnr = Double.MaxValue;
    var worstCorr = 1.0;
    var channels = Math.Min(format.Channels, refPcm.Format.Channels);
    for (var ch = 0; ch < channels; ch++)
    {
        var x = Extract(samples, format.Channels, ch);
        var y = Extract(ToDoubles(refPcm.Data), refPcm.Format.Channels, ch);
        var lag = FindLag(x, y, 200);
        var (snr, corr) = Compare(x, y, lag);
        if (snr < worstSnr) worstSnr = snr;
        if (corr < worstCorr) worstCorr = corr;
        Console.WriteLine($"  {name} ch{ch}: lag={lag} snr={snr:F2}dB corr={corr:F4}");
    }

    if (worstSnr >= minSnr && worstCorr >= minCorr)
        Pass($"{name}: SNR {worstSnr:F2} dB / 相关度 {worstCorr:F4}");
    else
        Fail($"{name}: SNR {worstSnr:F2} dB（门禁 {minSnr}）/ 相关度 {worstCorr:F4}（门禁 {minCorr}）");
}

if (mode is "all" or "aac")
{
    if (!Directory.Exists(assets))
    {
        Fail($"资产目录不存在：{assets}（把 XUnitTest/Assets/aacplus 拷过来）");
    }
    else
    {
        Console.WriteLine($"[aac] assets={assets}");
        Check("AAC-LC 22.05k", "lc-22k.mp4", "lc-22k.ref.wav", 0, 60, 0.9999);
        Check("HE-AAC v1 44.1k", "heaac-v1-44k.mp4", "heaac-v1-44k.ref.wav", 0, 60, 0.9999);
        Check("HE-AAC v1 后段", "heaac-v1-44k.mp4", "heaac-v1-44k.late.ref.wav", 5, 60, 0.9999);
        Check("HE-AAC v2 稳定段", "heaac-v2-ps-44k.mp4", "heaac-v2-ps-44k.ref.wav", 3, 35, 0.9999);
        Check("HE-AAC v2 后段", "heaac-v2-ps-44k.mp4", "heaac-v2-ps-44k.late.ref.wav", 5, 60, 0.9999);
    }
}

if (mode is "all" or "tts")
{
    var engine = new EspeakTtsEngine();
    Console.WriteLine($"[tts] engine={engine.Name} available={engine.IsAvailable}");
    if (!engine.IsAvailable)
    {
        Fail("espeak-ng 不可用（apt install espeak-ng）");
    }
    else
    {
        var voices = engine.GetVoices();
        Console.WriteLine($"[tts] voices={voices.Count}");

        Int64 bytes = 0;
        Int32 peak = 0;
        AudioFormat format = null;
        try
        {
            var chunks = 0;
            var enumerator = engine.SynthesizeAsync("Hello from NewLife Audio.", new TtsOptions { Voice = "en" }, CancellationToken.None).GetAsyncEnumerator();
            try
            {
                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    var chunk = enumerator.Current;
                    format ??= chunk.Format;
                    chunks++;
                    bytes += chunk.Data.Length;
                    for (var i = 0; i + 1 < chunk.Data.Length; i += 2)
                    {
                        var value = Math.Abs((Int32)BitConverter.ToInt16(chunk.Data, i));
                        if (value > peak) peak = value;
                    }
                }
            }
            finally
            {
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            Console.WriteLine($"[tts] chunks={chunks} bytes={bytes} rate={format?.SampleRate} ch={format?.Channels} peak={peak}");
        }
        catch (Exception ex)
        {
            Fail($"合成失败：{ex.GetType().Name}: {ex.Message}");
        }

        if (bytes > 0 && peak > 1000)
            Pass($"espeak 合成：{bytes} 字节，峰值 {peak}");
        else
            Fail("espeak 合成没有产出有效音频");
    }
}

Console.WriteLine(failed == 0 ? "全部通过" : $"失败 {failed} 项");
return failed == 0 ? 0 : 1;

static Double[] ToDoubles(Byte[] data)
{
    var samples = new Double[data.Length / 2];
    for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(data, i * 2);

    return samples;
}

static Double[] Extract(Double[] interleaved, Int32 channels, Int32 ch)
{
    if (channels <= 1) return interleaved;

    var frames = interleaved.Length / channels;
    var output = new Double[frames];
    for (var i = 0; i < frames; i++) output[i] = interleaved[i * channels + ch];

    return output;
}

// 16 倍抽取粗估偏移，再在 ±16 内细化。编码器/解码器存在固定延迟，必须先对齐再比波形。
static Int32 FindLag(Double[] ours, Double[] reference, Int32 range)
{
    const Int32 decimation = 16;
    var a = Decimate(ours, decimation);
    var b = Decimate(reference, decimation);

    var coarse = 0;
    var best = Double.MinValue;
    for (var lag = -range; lag <= range; lag++)
    {
        var sum = 0.0;
        var count = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var j = i + lag;
            if (j < 0 || j >= b.Length) continue;
            sum += a[i] * b[j];
            count++;
        }
        if (count < 100) continue;
        if (sum > best) { best = sum; coarse = lag; }
    }

    var fine = coarse * decimation;
    var fineBest = Double.MinValue;
    for (var lag = fine - decimation; lag <= fine + decimation; lag++)
    {
        var sum = 0.0;
        var count = 0;
        for (var i = 0; i < ours.Length; i += 4)
        {
            var j = i + lag;
            if (j < 0 || j >= reference.Length) continue;
            sum += ours[i] * reference[j];
            count++;
        }
        if (count < 100) continue;
        if (sum > fineBest) { fineBest = sum; fine = lag; }
    }

    return fine;
}

static Double[] Decimate(Double[] input, Int32 factor)
{
    var output = new Double[input.Length / factor];
    for (var i = 0; i < output.Length; i++) output[i] = input[i * factor];

    return output;
}

static (Double snr, Double corr) Compare(Double[] a, Double[] b, Int32 lag)
{
    var sumAB = 0.0;
    var sumAA = 0.0;
    var sumBB = 0.0;
    var sumErr = 0.0;
    var count = 0;

    for (var i = 0; i < a.Length; i++)
    {
        var j = i + lag;
        if (j < 0 || j >= b.Length) continue;

        sumAB += a[i] * b[j];
        sumAA += a[i] * a[i];
        sumBB += b[j] * b[j];
        var error = a[i] - b[j];
        sumErr += error * error;
        count++;
    }

    if (count == 0 || sumAA <= 0 || sumBB <= 0) return (0, 0);

    var corr = sumAB / Math.Sqrt(sumAA * sumBB);
    var snr = sumErr <= 0 ? 99 : 10 * Math.Log10(sumAA / sumErr);

    return (snr, corr);
}
