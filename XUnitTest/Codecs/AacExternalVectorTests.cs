using System;
using System.IO;
using NewLife.Audio;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest.Codecs;

/// <summary>AAC 外部参考向量：aacPlusDecoderCheckPackage v2.1（LC / HE-AAC v1 / HE-AAC v2）</summary>
/// <remarks>
/// 码流与参考 PCM 均取自公共测试包，参考 PCM 为官方参考解码器输出（截取前 2 秒）。
/// 断言口径：先按互相关对齐（编码器/解码器存在固定延迟），再比较归一化相关度与对齐后 SNR。
/// <para>HE-AAC v2（PS）残余缺陷：12~17 kHz 仍有频带缺失（13~15 kHz 为静默），属 SBR 高频再生范畴，门禁暂守 7 dB。</para>
/// </remarks>
public class AacExternalVectorTests
{
    private static String AssetPath(String name) => Path.Combine(AppContext.BaseDirectory, "Assets", "aacplus", name);

    [Fact(DisplayName = "AAC-LC 外部向量：22.05kHz 单声道对齐后 SNR ≥ 15 dB")]
    public void AacLc_ExternalVector()
    {
        var (snr, corr, format) = Compare("lc-22k.mp4", "lc-22k.ref.wav");

        Assert.Equal(22050, format.SampleRate);
        Assert.Equal(1, format.Channels);
        Assert.True(snr >= 15, $"SNR {snr:F2} dB 低于门禁 15 dB");
        Assert.True(corr >= 0.98, $"相关度 {corr:F4} 低于门禁 0.98");
    }

    [Fact(DisplayName = "HE-AAC v1（SBR）外部向量：44.1kHz 单声道对齐后 SNR ≥ 10 dB")]
    public void HeAacV1_ExternalVector()
    {
        var (snr, corr, format) = Compare("heaac-v1-44k.mp4", "heaac-v1-44k.ref.wav");

        Assert.Equal(44100, format.SampleRate);
        Assert.Equal(1, format.Channels);
        Assert.True(snr >= 10, $"SNR {snr:F2} dB 低于门禁 10 dB");
        Assert.True(corr >= 0.95, $"相关度 {corr:F4} 低于门禁 0.95");
    }

    [Fact(DisplayName = "HE-AAC v2（SBR+PS）外部向量：双声道稳定段 SNR ≥ 7 dB")]
    public void HeAacV2_ExternalVector()
    {
        // 参考向量取自 3~7 秒：PS 状态在开头数秒需收敛（已知），稳定段才具备可比性。
        // 混合滤波器组补齐虚部输入（Ps/Filterbank.cs HybridAnalysis）后，稳定段 SNR 由 4.96/5.83 dB 提升到 8.88/10.92 dB，
        // 相关度由 0.87/0.90 提升到 0.9376/0.9602；残余缺口在 12~17 kHz（SBR 高频再生）。
        var (snr, corr, format) = Compare("heaac-v2-ps-44k.mp4", "heaac-v2-ps-44k.ref.wav", skipSeconds: 3);

        Assert.Equal(44100, format.SampleRate);
        Assert.Equal(2, format.Channels);
        Assert.True(snr >= 7, $"SNR {snr:F2} dB 低于门禁 7 dB");
        Assert.True(corr >= 0.92, $"相关度 {corr:F4} 低于门禁 0.92");
    }

    private static (Double snr, Double corr, AudioFormat format) Compare(String streamName, String referenceName, Double skipSeconds = 0)
    {
        var audio = AudioFile.ReadPcm(AssetPath(streamName));
        var reference = AudioFile.ReadPcm(AssetPath(referenceName));

        var format = audio.Format;
        Assert.Equal(format.SampleRate, reference.Format.SampleRate);
        Assert.Equal(format.Channels, reference.Format.Channels);

        var ours = ToDoubles(audio.Data);
        var expected = ToDoubles(reference.Data);

        var skipFrames = (Int32)(skipSeconds * format.SampleRate);
        var skipSamples = skipFrames * format.Channels;
        if (skipSamples > 0 && skipSamples < ours.Length)
        {
            var trimmed = new Double[ours.Length - skipSamples];
            Array.Copy(ours, skipSamples, trimmed, 0, trimmed.Length);
            ours = trimmed;
        }

        var snr = 0.0;
        var corr = 0.0;
        for (var ch = 0; ch < format.Channels; ch++)
        {
            var x = Extract(ours, format.Channels, ch);
            var y = Extract(expected, format.Channels, ch);
            // 参考向量已按窗口截取，与“跳过开头”的 ours 从同一位置开始比较
            var lag = FindLag(x, y);
            var (chSnr, chCorr) = Aligned(x, y, lag);
            if (ch == 0 || chSnr < snr) snr = chSnr;
            if (ch == 0 || chCorr < corr) corr = chCorr;
        }

        return (snr, corr, format);
    }

    private static Double[] ToDoubles(Byte[] data)
    {
        var samples = new Double[data.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(data, i * 2);

        return samples;
    }

    private static Double[] Extract(Double[] interleaved, Int32 channels, Int32 channel)
    {
        if (channels <= 1) return interleaved;

        var frames = interleaved.Length / channels;
        var output = new Double[frames];
        for (var i = 0; i < frames; i++) output[i] = interleaved[i * channels + channel];

        return output;
    }

    // 16 倍抽取粗估延迟，再在 ±16 内细化
    private static Int32 FindLag(Double[] ours, Double[] reference)
    {
        const Int32 decimation = 16;
        var a = Decimate(ours, decimation);
        var b = Decimate(reference, decimation);

        var coarse = 0;
        var best = Double.MinValue;
        for (var lag = -200; lag <= 200; lag++)
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

    private static Double[] Decimate(Double[] input, Int32 factor)
    {
        var output = new Double[input.Length / factor];
        for (var i = 0; i < output.Length; i++)
        {
            var sum = 0.0;
            for (var k = 0; k < factor; k++) sum += input[i * factor + k];
            output[i] = sum / factor;
        }

        return output;
    }

    private static (Double snr, Double corr) Aligned(Double[] ours, Double[] reference, Int32 lag)
    {
        var sumAB = 0.0;
        var sumAA = 0.0;
        var sumBB = 0.0;
        var sumErr = 0.0;
        var count = 0;
        for (var i = 0; i < ours.Length; i++)
        {
            var j = i + lag;
            if (j < 0 || j >= reference.Length) continue;

            sumAB += ours[i] * reference[j];
            sumAA += ours[i] * ours[i];
            sumBB += reference[j] * reference[j];
            var error = ours[i] - reference[j];
            sumErr += error * error;
            count++;
        }

        if (count == 0 || sumAA <= 0 || sumBB <= 0) return (0, 0);

        var corr = sumAB / Math.Sqrt(sumAA * sumBB);
        var snr = sumErr <= 0 ? 99 : 10 * Math.Log10(sumAA / sumErr);

        return (snr, corr);
    }
}
