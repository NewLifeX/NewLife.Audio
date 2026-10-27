using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Data;
using Xunit;

namespace XUnitTest.Codecs;

/// <summary>编解码质量与互操作门禁</summary>
/// <remarks>
/// 统一口径：1 秒 16 位单声道测试信号（1 kHz 正弦 + 白噪混合）走「编码 → 解码」往返，断言三件事：
/// ① 对齐后的信噪比不低于该编码器下限；② 归一化相关度不低于下限（波形形状未走样）；
/// ③ 输出时长误差在门限内（无丢帧/多帧）。另有独立用例断言编解码实时因子小于 1，即快于实时。
/// 阈值取实测值下调 3 dB 后取整，既锁定当前质量，又能对后续调优引入的退化报警。
/// </remarks>
public class CodecQualityGateTests
{
    #region 门禁表
    /// <summary>单个编码器的门禁指标</summary>
    /// <param name="Name">名称</param>
    /// <param name="Codec">编码器实例</param>
    /// <param name="SampleRate">测试采样率</param>
    /// <param name="Option">编码选项</param>
    /// <param name="MinSnr">最低信噪比（dB）</param>
    /// <param name="MinCorrelation">最低相关度</param>
    /// <param name="MaxDurationErrorMs">最大时长误差（毫秒）</param>
    public record Gate(String Name, IAudioCodec Codec, Int32 SampleRate, Object Option, Double MinSnr, Double MinCorrelation, Double MaxDurationErrorMs);

    /// <summary>门禁清单，新增编码器时在此登记</summary>
    /// <returns></returns>
    public static IEnumerable<Object[]> Gates()
    {
        // 电话窄带编解码：波形保真度低，但时延与长度必须严格
        yield return [new Gate("G.711 A律", new G711ACodec(), 8000, null, 30, 0.999, 0)];
        yield return [new Gate("G.711 μ律", new G711UCodec(), 8000, null, 30, 0.999, 0)];
        yield return [new Gate("G.722", new G722Codec(), 16000, null, 9, 0.96, 2)];
        yield return [new Gate("IMA ADPCM", new ADPCMCodec(), 8000, null, 16, 0.99, 2)];
        yield return [new Gate("G.726 32kbps", new G726Codec(), 8000, null, 16, 0.99, 2)];
        yield return [new Gate("Speex 8kHz", new SpeexCodec(), 8000, null, 16, 0.99, 2)];

        // 通用压缩编码：有算法延迟，时长误差按帧长放宽
        yield return [new Gate("MP3 128kbps", new Mp3Codec(), 44100, new Mp3EncoderOptions { SampleRate = 44100, Channels = 1, Bitrate = 128 }, 11, 0.97, 70)];
        yield return [new Gate("AAC-LC 64kbps", new AacCodec(), 44100, new AacEncoderOptions { SampleRate = 44100, Channels = 1, Bitrate = 64000 }, 11, 0.97, 70)];
        yield return [new Gate("Vorbis q5", new VorbisCodec(), 44100, new VorbisOptions { SampleRate = 44100, Channels = 1, Quality = 5 }, 8, 0.95, 70)];
    }
    #endregion

    #region 质量门禁
    [Theory(DisplayName = "编码往返后信噪比、相关度与时长满足门禁")]
    [MemberData(nameof(Gates))]
    public void RoundTrip_MeetsQualityGate(Gate gate)
    {
        var signal = CreateSignal(gate.SampleRate, 1.0);
        var pcm = ToBytes(signal);

        var encoded = gate.Codec.FromPcm(pcm, gate.Option);
        var codecData = encoded.GetSpan().ToArray();
        Assert.True(codecData.Length > 0, $"{gate.Name} 编码输出为空");

        var decoded = gate.Codec.ToPcm(codecData, null).ReadBytes();
        var actual = ToSamples(decoded);
        Assert.True(actual.Length > 0, $"{gate.Name} 解码输出为空");

        var (snr, lag, correlation) = Align(signal, actual, gate.SampleRate);
        var durationErrorMs = Math.Abs(actual.Length - signal.Length) * 1000.0 / gate.SampleRate;
        var detail = $"{gate.Name}: SNR={snr:F1}dB 相关度={correlation:F3} 延迟={lag} 时长误差={durationErrorMs:F1}ms";

        Assert.True(snr >= gate.MinSnr, $"{detail}，低于 SNR 门禁 {gate.MinSnr}dB");
        Assert.True(correlation >= gate.MinCorrelation, $"{detail}，低于相关度门禁 {gate.MinCorrelation}");
        Assert.True(durationErrorMs <= gate.MaxDurationErrorMs, $"{detail}，超出时长误差门禁 {gate.MaxDurationErrorMs}ms");
    }

    [Theory(DisplayName = "编解码耗时快于实时")]
    [MemberData(nameof(Gates))]
    public void RoundTrip_IsFasterThanRealtime(Gate gate)
    {
        var seconds = 2.0;
        var pcm = ToBytes(CreateSignal(gate.SampleRate, seconds));

        // 预热，避免首次 JIT 计入实时因子
        _ = gate.Codec.ToPcm(gate.Codec.FromPcm(pcm, gate.Option).GetSpan().ToArray(), null).ReadBytes();

        var watch = Stopwatch.StartNew();
        var encoded = gate.Codec.FromPcm(pcm, gate.Option).GetSpan().ToArray();
        var decoded = gate.Codec.ToPcm(encoded, null).ReadBytes();
        watch.Stop();

        Assert.True(decoded.Length > 0);
        var factor = watch.Elapsed.TotalSeconds / seconds;

        // 纯托管编码器在低功耗 ARM 上达不到实时：Linux ARM64（4 核 SBC）实测 MP3 128k 1.39×、Vorbis q5 2.37×，
        // 因此这些平台只守住"不出现数量级退化"，实时流式仍应走原生后端或更低质量档。
        var limit = RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64 ? 3.0 : 1.0;
        Assert.True(factor < limit, $"{gate.Name}: 实时因子={factor:F3}，超出 {limit:F1}× 门禁（{watch.ElapsedMilliseconds}ms / {seconds}s）");
    }

    [Theory(DisplayName = "损坏与截断码流只允许抛出明确的无效数据类异常")]
    [MemberData(nameof(Gates))]
    public void Decode_CorruptOrTruncatedData_DoesNotThrowUnexpected(Gate gate)
    {
        // 随机字节：允许判定为「无效数据/不支持」，但不允许越界、空引用等实现缺陷异常逃逸
        var garbage = new Byte[64];
        new Random(20240601).NextBytes(garbage);
        AssertDecoderEitherWorksOrRejects(gate, garbage, "随机字节");

        // 截断：合法码流砍掉尾部后同样只允许明确拒绝
        var encoded = gate.Codec.FromPcm(ToBytes(CreateSignal(gate.SampleRate, 0.25)), gate.Option).GetSpan().ToArray();
        if (encoded.Length > 8)
        {
            var truncated = encoded.AsSpan(0, encoded.Length / 3).ToArray();
            AssertDecoderEitherWorksOrRejects(gate, truncated, "截断码流");
        }
    }

    private static void AssertDecoderEitherWorksOrRejects(Gate gate, Byte[] data, String scenario)
    {
        try
        {
            Assert.NotNull(gate.Codec.ToPcm(data, null));
        }
        catch (InvalidDataException)
        {
            // 明确判定数据无效，符合预期
        }
        catch (NotSupportedException)
        {
            // 缺少必要配置（如 AAC 的 AudioSpecificConfig），同样属于明确拒绝
        }
        catch (Exception ex)
        {
            Assert.Fail($"{gate.Name} 解码{scenario}时抛出未预期异常 {ex.GetType().Name}：{ex.Message}");
        }
    }
    #endregion

    #region 辅助
    /// <summary>生成 1 kHz 正弦 + 白噪混合信号</summary>
    /// <param name="sampleRate">采样率</param>
    /// <param name="seconds">时长（秒）</param>
    /// <returns>16 位样本</returns>
    private static Int16[] CreateSignal(Int32 sampleRate, Double seconds)
    {
        var count = (Int32)(sampleRate * seconds);
        var samples = new Int16[count];
        var random = new Random(20240601);

        // 正弦取 0.35 满量程、白噪取 0.15 满量程，兼顾谱纯度与宽带覆盖
        for (var i = 0; i < count; i++)
        {
            var tone = Math.Sin(2 * Math.PI * 1000 * i / sampleRate) * 0.35;
            var noise = (random.NextDouble() * 2 - 1) * 0.15;
            samples[i] = (Int16)Math.Clamp(Math.Round((tone + noise) * 32767), -32768, 32767);
        }

        return samples;
    }

    /// <summary>在 ±1 帧范围内搜索最佳对齐位置后计算信噪比与相关度</summary>
    /// <param name="reference">原始样本</param>
    /// <param name="actual">解码样本</param>
    /// <param name="sampleRate">采样率，用于确定搜索范围与最小统计长度</param>
    /// <returns>信噪比、延迟与归一化相关度</returns>
    private static (Double Snr, Int32 Lag, Double Correlation) Align(Int16[] reference, Int16[] actual, Int32 sampleRate)
    {
        var maxLag = Math.Min(Math.Max(sampleRate / 10, 256), actual.Length / 2);
        var minCount = Math.Min(reference.Length, actual.Length) / 2;

        var bestLag = 0;
        var bestSnr = Double.NegativeInfinity;

        // 粗搜：步长 16，统计窗口取中段 4096 样本，覆盖算法延迟（MP3 1057、AAC 1024、Vorbis 2048 等）
        for (var lag = 0; lag <= maxLag; lag += 16)
        {
            var snr = MeasureSnr(reference, actual, lag, Math.Min(4096, minCount));
            if (snr > bestSnr)
            {
                bestSnr = snr;
                bestLag = lag;
            }
        }

        // 细搜：在粗搜结果附近逐点确认，并用全部重叠样本统计
        for (var lag = Math.Max(0, bestLag - 16); lag <= Math.Min(maxLag, bestLag + 16); lag++)
        {
            var snr = MeasureSnr(reference, actual, lag, minCount);
            if (snr > bestSnr)
            {
                bestSnr = snr;
                bestLag = lag;
            }
        }

        var (signal, noise, cross, actualPower) = Accumulate(reference, actual, bestLag, minCount);
        var correlation = cross / Math.Sqrt(Math.Max(signal * actualPower, 1e-9));

        return (10 * Math.Log10(signal / Math.Max(noise, 1e-9)), bestLag, correlation);
    }

    private static Double MeasureSnr(Int16[] reference, Int16[] actual, Int32 lag, Int32 count)
    {
        if (count <= 0 || lag + count > actual.Length || count > reference.Length) return Double.NegativeInfinity;

        var (signal, noise, _, _) = Accumulate(reference, actual, lag, count);

        return 10 * Math.Log10(signal / Math.Max(noise, 1e-9));
    }

    private static (Double Signal, Double Noise, Double Cross, Double ActualPower) Accumulate(Int16[] reference, Int16[] actual, Int32 lag, Int32 count)
    {
        Double signal = 0, noise = 0, cross = 0, actualPower = 0;
        for (var i = 0; i < count; i++)
        {
            Double r = reference[i];
            Double a = actual[lag + i];
            var diff = r - a;
            signal += r * r;
            actualPower += a * a;
            cross += r * a;
            noise += diff * diff;
        }

        return (signal, noise, cross, actualPower);
    }

    private static Byte[] ToBytes(Int16[] samples)
    {
        var bytes = new Byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            bytes[i * 2] = (Byte)(samples[i] & 0xFF);
            bytes[i * 2 + 1] = (Byte)(samples[i] >> 8 & 0xFF);
        }

        return bytes;
    }

    private static Int16[] ToSamples(Byte[] bytes)
    {
        var samples = new Int16[bytes.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (Int16)(bytes[i * 2 + 1] << 8 | bytes[i * 2]);
        }

        return samples;
    }
    #endregion
}
