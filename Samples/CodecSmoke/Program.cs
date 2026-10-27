using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Audio.DSP;
using NewLife.Audio.Tts;

// 编解码真机冒烟：在目标机上复验 AAC 外部参考向量的 SNR、MP3/Opus 往返与 espeak 语音合成，
// 用于确认跨架构（x64 / aarch64）行为一致——同一码流应给出同一组 SNR。
// 用法：CodecSmoke [all|aac|mp3|opus|tts] [资产目录]，任一失败返回非 0。

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

if (mode is "all" or "mp3")
{
    // 纯托管 MP3 编码器：整段一次编码（逐帧调用会重置 MDCT/多相历史），再解回对齐比波形。
    const Int32 frames = 38;
    var rate = 44100;
    var pcm = Tone(1152 * frames, rate);
    var codec = new Mp3Codec();

    var encoded = codec.FromPcm(pcm, new Mp3EncoderOptions { SampleRate = rate, Channels = 1, Bitrate = 320 }).GetSpan().ToArray();
    var decoded = codec.ToPcm(encoded, null).GetSpan().ToArray();
    var snr = AlignedSnr(ToDoubles(pcm), ToDoubles(decoded), 1152);
    Console.WriteLine($"[mp3] bytes={encoded.Length} decoded={decoded.Length} snr={snr:F2}dB");
    if (snr >= 35)
        Pass($"MP3 往返（1 kHz 正弦，{frames} 帧）: SNR {snr:F2} dB");
    else
        Fail($"MP3 往返 SNR {snr:F2} dB 低于门禁 35 dB");
}

if (mode is "all" or "opus")
{
    // Opus 走 ffmpeg(libopus) 进程回退，缺 ffmpeg 时只提示跳过。
    // 逐包解码时帧间重叠（2.5 ms）会丢，波形 SNR 不是有效指标，这里只验通路与音调。
    if (!OpusProcessCodec.IsAvailable)
    {
        Console.WriteLine("[SKIP] 未找到 ffmpeg/libopus，跳过 Opus 往返");
    }
    else
    {
        const Int32 rate = 48000;
        var options = new OpusOptions { SampleRate = rate };
        var pcm = Tone(options.SamplesPerFrame * 50, rate);
        using var codec = new OpusCodec();

        var packets = codec.Encode(pcm);
        var decoded = new List<Byte>();
        var badFrame = 0;
        foreach (var packet in packets)
        {
            var frame = codec.ToPcm(packet.GetSpan(), options).GetSpan().ToArray();
            if (frame.Length != options.SamplesPerFrame * 2) badFrame++;
            decoded.AddRange(frame);
        }

        var samples = ToDoubles(decoded.ToArray());
        var tone = Goertzel(samples, 0, samples.Length / 2, 1000, rate);
        var other = Goertzel(samples, 0, samples.Length / 2, 3000, rate);
        Console.WriteLine($"[opus] packets={packets.Count} decoded={decoded.Count} bad={badFrame} tone1k={tone:F0} tone3k={other:F0}");
        if (packets.Count == 50 && badFrame == 0 && tone > 100 && tone > other * 5)
            Pass($"Opus 进程往返（1 kHz 正弦，{packets.Count} 包）: 帧长一致、1 kHz 能量 {tone:F0}");
        else
            Fail($"Opus 进程往返不正常：packets={packets.Count} bad={badFrame} tone1k={tone:F0} tone3k={other:F0}");
    }
}

if (mode is "all" or "tts")
{
    var engine = new EspeakTtsEngine();
    Console.WriteLine($"[tts] engine={engine.Name} available={engine.IsAvailable}");
    if (!engine.IsAvailable)
    {
        Console.WriteLine("[SKIP] espeak-ng 不可用（apt install espeak-ng）");
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

static Byte[] Tone(Int32 samples, Int32 sampleRate)
{
    var pcm = new Byte[samples * 2];
    for (var i = 0; i < samples; i++)
    {
        var value = (Int16)(12000 * Math.Sin(2 * Math.PI * 1000 * i / sampleRate));
        pcm[i * 2] = (Byte)(value & 0xFF);
        pcm[i * 2 + 1] = (Byte)((value >> 8) & 0xFF);
    }

    return pcm;
}

// Goertzel：测单个频点的能量，用于确认解码出来的确实是那段音调而不是噪声。
static Double Goertzel(Double[] data, Int32 start, Int32 count, Double frequency, Int32 sampleRate)
{
    var w = 2 * Math.PI * frequency / sampleRate;
    var coeff = 2 * Math.Cos(w);
    var s0 = 0.0;
    var s1 = 0.0;
    var s2 = 0.0;
    var end = Math.Min(start + count, data.Length);
    for (var i = start; i < end; i++)
    {
        s0 = data[i] + coeff * s1 - s2;
        s2 = s1;
        s1 = s0;
    }

    var real = s1 - s2 * Math.Cos(w);
    var imag = s2 * Math.Sin(w);

    return Math.Sqrt(real * real + imag * imag) * 2.0 / Math.Max(count, 1);
}

// 编解码器有固定算法延迟，先按互相关定粗偏移，再在附近逐样本找 SNR 最高的偏移（编码器可能整体改电平，
// 故每个候选偏移都用最小二乘增益对齐）。纯正弦的互相关有多个等高峰值，只靠互相关定不准。
static Double AlignedSnr(Double[] ours, Double[] reference, Int32 trim)
{
    var center = FindLag(ours, reference, 200);

    var best = -999.0;
    for (var lag = center - 64; lag <= center + 64; lag++)
    {
        var snr = SnrAt(ours, reference, lag, trim);
        if (snr > best) best = snr;
    }

    return best;
}

static Double SnrAt(Double[] ours, Double[] reference, Int32 lag, Int32 trim)
{
    var sumAB = 0.0;
    var sumBB = 0.0;
    for (var i = trim; i < ours.Length - trim; i++)
    {
        var j = i + lag;
        if (j < 0 || j >= reference.Length) continue;

        sumAB += ours[i] * reference[j];
        sumBB += reference[j] * reference[j];
    }
    var gain = sumBB <= 0 ? 1 : sumAB / sumBB;

    var sumAA = 0.0;
    var sumErr = 0.0;
    for (var i = trim; i < ours.Length - trim; i++)
    {
        var j = i + lag;
        if (j < 0 || j >= reference.Length) continue;

        sumAA += ours[i] * ours[i];
        var error = ours[i] - gain * reference[j];
        sumErr += error * error;
    }

    return sumErr <= 0 || sumAA <= 0 ? 99 : 10 * Math.Log10(sumAA / sumErr);
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
