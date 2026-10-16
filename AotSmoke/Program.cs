// NewLife.Audio NativeAOT compatibility smoke test.
// Runs a fixed set of scenarios against the audio library and reports PASS/FAIL per scenario.
// Same binary is run under JIT (dotnet run) and NativeAOT (publish -r <rid>) to compare.
//
// 说明：AOT 能力验收冒烟件，由 .github/workflows/aot.yml 的多 RID 矩阵自动执行（非 0 退出码即失败）。
// 场景覆盖：编解码往返、容器读写、RTP 封包组帧、DSP 信号链、语音处理、芯片头、统一工厂入口。
// 全部为纯托管计算，无网络与设备 I/O，可运行在 win-x64/linux-x64/linux-arm64/linux-la64。
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using NewLife.Audio;
using NewLife.Audio.ChipHeaders;
using NewLife.Audio.Codecs;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using NewLife.Audio.Speech;
using NewLife.Audio.Streaming;
using NewLife.Data;

try { Console.OutputEncoding = Encoding.UTF8; } catch { }

Console.WriteLine("=== NewLife.Audio AOT smoke ===");
Console.WriteLine($"framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"IsDynamicCodeSupported={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported} IsDynamicCodeCompiled={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled}");
Console.WriteLine($"EntryLocation={GetEntryLocation()}");

[UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "AOT 判真探针：以 Location 是否为空区分原生发布件与 JIT 运行，不用于路径判断")]
static String? GetEntryLocation() => Assembly.GetEntryAssembly()?.Location;

var results = new List<(String Name, Boolean Ok, String Error)>();
void Run(String name, Action action)
{
    try
    {
        action();
        results.Add((name, true, ""));
        Console.WriteLine($"[PASS] {name}");
    }
    catch (Exception ex)
    {
        results.Add((name, false, $"{ex.GetType().Name}: {ex.Message}"));
        Console.WriteLine($"[FAIL] {name} -> {ex.GetType().Name}: {ex.Message}");
    }
}

// 生成 16-bit 小端单声道正弦波 PCM
static Byte[] MakeSinePcm(Int32 samples, Double freq, Int32 sampleRate, Int32 amplitude = 16000)
{
    var pcm = new Byte[samples * 2];
    for (var i = 0; i < samples; i++)
    {
        var val = (Int16)(Math.Sin(2 * Math.PI * freq * i / sampleRate) * amplitude);
        pcm[i * 2] = (Byte)(val & 0xFF);
        pcm[i * 2 + 1] = (Byte)((val >> 8) & 0xFF);
    }
    return pcm;
}

var pcm16k = MakeSinePcm(1600, 400, 8000);          // 10 帧 @8kHz，G.711/ADPCM/工厂用
var pcm20m = MakeSinePcm(960, 1000, 48000, 8000);   // 1 帧 @48kHz，Opus 用

Run("G.711 A-law 编解码往返", () =>
{
    var codec = new G711ACodec();
    var encoded = codec.FromPcm(pcm16k, null);
    if (encoded.Total != pcm16k.Length / 2) throw new Exception($"编码长度 {encoded.Total} != {pcm16k.Length / 2}");
    var decoded = codec.ToPcm(encoded.GetSpan(), null);
    if (decoded.Total != pcm16k.Length) throw new Exception($"解码长度 {decoded.Total} != {pcm16k.Length}");
});

Run("G.711 μ-law 编解码往返", () =>
{
    var codec = new G711UCodec();
    var encoded = codec.FromPcm(pcm16k, null);
    if (encoded.Total != pcm16k.Length / 2) throw new Exception($"编码长度 {encoded.Total} != {pcm16k.Length / 2}");
    var decoded = codec.ToPcm(encoded.GetSpan(), null);
    if (decoded.Total != pcm16k.Length) throw new Exception($"解码长度 {decoded.Total} != {pcm16k.Length}");
});

Run("IMA ADPCM 编解码往返", () =>
{
    var codec = new ADPCMCodec();
    var samples = 1024;
    var pcm = MakeSinePcm(samples, 400, 8000, 28000);
    var encoded = codec.FromPcm(pcm, null);
    if (encoded.Total <= 200 || encoded.Total >= samples * 2) throw new Exception($"编码长度异常 {encoded.Total}");
    var decoded = codec.ToPcm(encoded.GetSpan(), null);
    if (decoded.Total != samples * 2) throw new Exception($"解码长度 {decoded.Total} != {samples * 2}");
});

Run("G.722 编解码往返", () =>
{
    var codec = new G722Codec();
    var samples = 160;
    var pcm = MakeSinePcm(samples, 400, 16000);
    var encoded = codec.FromPcm(pcm, null);
    if (encoded.Total != samples / 2) throw new Exception($"编码长度 {encoded.Total} != {samples / 2}");
    var decoded = codec.ToPcm(encoded.GetSpan(), null);
    if (decoded.Total != samples * 2) throw new Exception($"解码长度 {decoded.Total} != {samples * 2}");
});

Run("G.726 编解码往返", () =>
{
    var codec = new G726Codec(4);
    var samples = 320;
    var pcm = new Byte[samples * 2];
    var random = new Random(400);
    for (var i = 0; i < samples; i++)
    {
        var val = (Int16)random.Next(-3000, 3000);
        pcm[i * 2] = (Byte)(val & 0xFF);
        pcm[i * 2 + 1] = (Byte)((val >> 8) & 0xFF);
    }
    var encoded = codec.FromPcm(pcm, null);
    if (encoded.Total <= 0) throw new Exception("编码输出为空");
    var decoded = codec.ToPcm(encoded.GetSpan(), null);
    if (decoded.Total != samples * 2) throw new Exception($"解码长度 {decoded.Total} != {samples * 2}");
});

Run("MP3 编码与帧解析", () =>
{
    var codec = new Mp3Codec();
    var encoded = codec.FromPcm(new Byte[1152 * 2], null);
    if (encoded.Total < 4) throw new Exception($"编码长度 {encoded.Total} < 4");
    if (encoded[0] != 0xFF || (encoded[1] & 0xE0) != 0xE0) throw new Exception("缺少 MP3 同步字");

    var header = new Byte[] { 0xFF, 0xFB, 0x90, 0x00 };
    var info = Mp3Codec.ParseFrameHeader(header, 0);
    if (info == null || info.SampleRate != 44100) throw new Exception("MP3 帧头解析失败");
    if (!Mp3Codec.IsMp3Frame(header, 0)) throw new Exception("IsMp3Frame 有效帧返回 false");
});

Run("AAC ADTS 编码与解析", () =>
{
    var codec = new AacCodec();
    var pcm = MakeSinePcm(2048, 440, 44100);
    var encoded = codec.FromPcm(pcm, null);
    if (encoded.Total <= 0) throw new Exception("编码输出为空");
    if (encoded[0] != 0xFF || (encoded[1] & 0xF0) != 0xF0) throw new Exception("缺少 ADTS 同步字");

    var span = encoded.GetSpan();
    var adts = AacCodec.ParseAdtsHeader(span, 0);
    if (adts == null || adts.SampleRate <= 0) throw new Exception("ADTS 头解析失败");
});

Run("FLAC 编码往返", () =>
{
    var codec = new FlacCodec();
    var random = new Random(42);
    var samples = 128;
    var pcm = new Byte[samples * 2];
    for (var i = 0; i < samples; i++)
    {
        var val = (Int16)random.Next(-5000, 5000);
        pcm[i * 2] = (Byte)(val & 0xFF);
        pcm[i * 2 + 1] = (Byte)((val >> 8) & 0xFF);
    }
    var encoded = codec.FromPcm(pcm, null);
    if (encoded.Total <= 40) throw new Exception($"编码长度 {encoded.Total} <= 40");
    if (encoded[0] != (Byte)'f' || encoded[1] != (Byte)'L' || encoded[2] != (Byte)'a' || encoded[3] != (Byte)'C')
        throw new Exception("缺少 fLaC 标记");
    var decoded = codec.ToPcm(encoded.GetSpan(), null);
    if (decoded == null) throw new Exception("解码返回 null");
});

Run("Opus 编解码往返", () =>
{
    var codec = new OpusCodec();
    var encoded = codec.FromPcm(pcm20m, 64000);
    if (encoded.Total <= 0) throw new Exception("编码输出为空");
    var decoded = codec.ToPcm(encoded.GetSpan(), null);
    if (decoded.Total != 1920) throw new Exception($"解码长度 {decoded.Total} != 1920");
});

Run("Speex 编解码往返", () =>
{
    var codec = new SpeexCodec();
    var pcm = MakeSinePcm(1600, 400, 8000);
    var encoded = codec.FromPcm(pcm, null);
    if (encoded.Total <= 0) throw new Exception("编码输出为空");
    var decoded = codec.ToPcm(encoded.GetSpan(), null);
    if (decoded.Total <= 0) throw new Exception("解码输出为空");
});

Run("Vorbis 编码输出", () =>
{
    var codec = new VorbisCodec();
    var encoded = codec.FromPcm(new Byte[1024 * 2], null);
    if (encoded.Total <= 0) throw new Exception("编码输出为空");
});

Run("WAV 容器写入与读取", () =>
{
    var format = new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16 };
    using var ms = new MemoryStream();
    var writer = new WaveFileWriter(ms, format);
    writer.WriteFrame(new Byte[320]);
    writer.WriteFrame(new Byte[320]);
    writer.Flush();

    ms.Position = 0;
    var reader = new WaveFileReader(ms);
    if (reader.Format.SampleRate != 8000) throw new Exception($"采样率 {reader.Format.SampleRate} != 8000");
    var frame1 = reader.ReadFrame();
    if (frame1 == null || frame1.Total != 320) throw new Exception($"第1帧长度异常 {frame1?.Total}");
    var frame2 = reader.ReadFrame();
    if (frame2 == null || frame2.Total != 320) throw new Exception($"第2帧长度异常 {frame2?.Total}");
    var frame3 = reader.ReadFrame();
    if (frame3 != null) throw new Exception("数据结束后仍返回帧");
});

Run("RTP 封包与组帧", () =>
{
    // 单包往返：RtpPacket 序列化 → 反序列化
    var original = new RtpPacket
    {
        Version = 2,
        Marker = true,
        PayloadType = 8,
        SequenceNumber = 12345,
        Timestamp = 67890,
        Ssrc = 0x12345678,
        Payload = [0xAA, 0xBB, 0xCC, 0xDD],
    };
    var bytes = original.ToBytes();
    var parsed = RtpPacket.FromBytes(bytes);
    if (parsed.SequenceNumber != original.SequenceNumber || parsed.Ssrc != original.Ssrc || parsed.PayloadType != 8)
        throw new Exception("RTP 包往返字段不一致");

    // 封包器 → 解包器组帧（帧小于 MTU，单包 + Marker）
    var codec = new G711ACodec();
    var frame = codec.FromPcm(MakeSinePcm(640, 400, 8000), null).ReadBytes();
    var packetizer = new RtpPacketizer(8);
    var packets = packetizer.Packetize(frame, 640);
    if (packets.Length != 1) throw new Exception($"单帧分片数 {packets.Length} != 1");

    var depacketizer = new RtpDepacketizer();
    if (!depacketizer.Submit(packets[0], out var complete)) throw new Exception("未组出完整帧");
    if (complete.Length != frame.Length) throw new Exception($"组帧长度 {complete.Length} != {frame.Length}");
});

Run("DSP 信号链：音量+重采样+FFT", () =>
{
    var buffer = new Single[160];
    var vol = new VolumeControl(0.5f) { Source = new SineSource(8000, 440, 100000) };
    var read = vol.Read(buffer, 0, 160);
    if (read != 160) throw new Exception($"音量读取 {read} != 160");
    if (Math.Abs(buffer[10]) > 0.55f) throw new Exception($"半幅正弦超出范围 {buffer[10]}");

    var resampler = new Resampler(16000, new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16 })
    {
        Source = new SineSource(8000, 440, 100000),
    };
    var out2 = new Single[320];
    var read2 = resampler.Read(out2, 0, 320);
    if (read2 <= 0) throw new Exception("重采样输出为空");

    // 440Hz 正弦 @8kHz，1024 点 FFT 峰值应落在 440Hz 附近（分辨率 7.8Hz）
    var fft = new FftAnalyzer(1024);
    var samples = new Single[1024];
    for (var i = 0; i < samples.Length; i++)
    {
        samples[i] = (Single)Math.Sin(2 * Math.PI * 440 * i / 8000) * 0.5f;
    }
    var spectrum = fft.ComputeMagnitudeSpectrum(samples);
    if (spectrum.Length != 513) throw new Exception($"幅度谱长度 {spectrum.Length} != 513");
    var peak = 0;
    for (var i = 1; i < spectrum.Length; i++)
    {
        if (spectrum[i] > spectrum[peak]) peak = i;
    }
    var peakFreq = peak * 8000.0 / 1024;
    if (Math.Abs(peakFreq - 440) > 60) throw new Exception($"FFT 峰值频率 {peakFreq:F1}Hz 偏离 440Hz");
});

Run("语音处理：VAD 与 NS", () =>
{
    var vad = new GmmVad(8000, 1);
    var silence = new Byte[160 * 2];
    if (vad.IsSpeech(silence)) throw new Exception("静音被判定为语音");
    var prob = vad.GetSpeechProbability(silence);
    if (prob < 0f || prob > 1f) throw new Exception($"语音概率越界 {prob}");

    // NS：输出等长 PCM
    var ns = new SpectralNoiseSuppressor(16000);
    var noisy = MakeSinePcm(2048, 300, 16000, 8000);
    var clean = ns.Suppress(noisy);
    if (clean == null || clean.Length != noisy.Length) throw new Exception($"NS 输出长度 {clean?.Length} != {noisy.Length}");
    ns.Reset();
});

Run("芯片头：添加与剥离往返", () =>
{
    var original = new Byte[50];
    for (var i = 0; i < original.Length; i++)
    {
        original[i] = (Byte)(i + 1);
    }

    var hi = new HisiliconHeader();
    if (!hi.TryAdd(original, out var withHeader)) throw new Exception("海思头添加失败");
    if (!hi.TryTrim(withHeader.GetSpan(), out var stripped)) throw new Exception("海思头剥离失败");
    if (stripped.Total != original.Length) throw new Exception($"剥离长度 {stripped.Total} != {original.Length}");

    var dahua = new DahuaHeader();
    if (!dahua.TryAdd(original, out var withDahua)) throw new Exception("大华头添加失败");
    if (!dahua.TryTrim(withDahua.GetSpan(), out var stripped2)) throw new Exception("大华头剥离失败");

    var uniview = new UniviewHeader();
    if (!uniview.TryAdd(original, out var withUniview)) throw new Exception("宇视头添加失败");
    if (!uniview.TryTrim(withUniview.GetSpan(), out var stripped3)) throw new Exception("宇视头剥离失败");
});

Run("AudioCodecFactory 统一编解码入口", () =>
{
    var factory = new AudioCodecFactory();
    if (!factory.Registry.IsSupported(AVTypes.G722)) throw new Exception("G722 未注册");

    var encoded = factory.FromPcm(AVTypes.G711A, new ArrayPacket(MakeSinePcm(640, 400, 8000)));
    if (encoded.Total != 640) throw new Exception($"G711A 编码长度 {encoded.Total} != 640");

    var decoded = factory.ToPcm(AVTypes.G711A, encoded);
    if (decoded.Total != 1280) throw new Exception($"G711A 解码长度 {decoded.Total} != 1280");

    // 芯片头去除：海思 4 字节头
    var payload = new Byte[100];
    var withHi = new Byte[4 + 100];
    withHi[0] = 0x00;
    withHi[1] = 0x01;
    withHi[2] = (Byte)(100 / 2);
    withHi[3] = 0x00;
    Array.Copy(payload, 0, withHi, 4, 100);
    var trimmedPacket = factory.TrimHI(new ArrayPacket(withHi), out var trimmed);
    if (!trimmed || trimmedPacket.Total != 100) throw new Exception("工厂芯片头去除失败");
});

// ---- 汇总 ----
var failed = results.Count(r => !r.Ok);
Console.WriteLine();
Console.WriteLine($"=== {results.Count - failed}/{results.Count} passed, {failed} failed ===");
foreach (var (name, ok, error) in results)
{
    if (!ok) Console.WriteLine($"[FAIL] {name} -> {error}");
}

return failed == 0 ? 0 : 1;

/// <summary>正弦波测试信号源（IAudioProcessor 管线起点）</summary>
internal sealed class SineSource : IAudioProcessor
{
    private readonly Int32 _sampleRate;
    private readonly Double _freq;
    private readonly Int32 _total;
    private Int32 _pos;

    public AudioFormat InputFormat { get; }

    public AudioFormat OutputFormat { get; }

    public IAudioProcessor? Source { get; set; }

    public SineSource(Int32 sampleRate, Double freq, Int32 total)
    {
        _sampleRate = sampleRate;
        _freq = freq;
        _total = total;

        var format = new AudioFormat { SampleRate = sampleRate, Channels = 1, BitsPerSample = 16 };
        InputFormat = format;
        OutputFormat = format;
    }

    public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
    {
        var n = Math.Min(count, _total - _pos);
        for (var i = 0; i < n; i++)
        {
            buffer[offset + i] = (Single)(Math.Sin(2 * Math.PI * _freq * (_pos + i) / _sampleRate) * 0.5);
        }
        _pos += n;
        return n;
    }

    public void Reset() => _pos = 0;
}
