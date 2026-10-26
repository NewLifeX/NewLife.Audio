using System;
using System.IO;
using NewLife.Audio;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest.Codecs;

/// <summary>AAC 外部参考向量：aacPlusDecoderCheckPackage v2.1（LC / HE-AAC v1 / HE-AAC v2）</summary>
/// <remarks>
/// 码流与参考 PCM 均取自公共测试包，参考 PCM 为官方参考解码器输出（LC/HEv1 截取 2 秒，HEv2 截取 3~7 秒，
/// 后段门禁用 5~6 秒各 1 秒切片）。
/// 断言口径：先按互相关对齐（编码器/解码器存在固定延迟），再比较归一化相关度与对齐后 SNR。
/// <para>LC 与 HE-AAC v1 在上述窗口内与参考几乎逐样本一致（SNR 约 62 dB），门禁守在 45 dB；
/// HE-AAC v2（PS）稳定段 12.7/14.5 dB，门禁守在 11 dB。用整段参考复测时第 4 秒后 SNR 降至 11~14 dB，
/// 但频带能量比与时延都正常（差异在噪声/正弦分量的随机实现），详见功能清单收口记录。</para>
/// </remarks>
public class AacExternalVectorTests
{
    private static String AssetPath(String name) => Path.Combine(AppContext.BaseDirectory, "Assets", "aacplus", name);

    [Fact(DisplayName = "AAC-LC 外部向量：22.05kHz 单声道对齐后 SNR ≥ 45 dB")]
    public void AacLc_ExternalVector()
    {
        var (snr, corr, format) = Compare("lc-22k.mp4", "lc-22k.ref.wav");

        Assert.Equal(22050, format.SampleRate);
        Assert.Equal(1, format.Channels);
        Assert.True(snr >= 45, $"SNR {snr:F2} dB 低于门禁 45 dB");
        Assert.True(corr >= 0.999, $"相关度 {corr:F4} 低于门禁 0.999");
    }

    [Fact(DisplayName = "HE-AAC v1（SBR）外部向量：44.1kHz 单声道对齐后 SNR ≥ 45 dB")]
    public void HeAacV1_ExternalVector()
    {
        var (snr, corr, format) = Compare("heaac-v1-44k.mp4", "heaac-v1-44k.ref.wav");

        Assert.Equal(44100, format.SampleRate);
        Assert.Equal(1, format.Channels);
        Assert.True(snr >= 45, $"SNR {snr:F2} dB 低于门禁 45 dB");
        Assert.True(corr >= 0.999, $"相关度 {corr:F4} 低于门禁 0.999");
    }

    [Fact(DisplayName = "HE-AAC v2（SBR+PS）外部向量：双声道稳定段 SNR ≥ 11 dB")]
    public void HeAacV2_ExternalVector()
    {
        // 参考向量取自 3~7 秒：PS 状态在开头数秒需收敛（已知），稳定段才具备可比性。
        // 混合滤波器组补虚部 + SBR patch 不再截断到 5 个之后，稳定段 SNR 提升到 12.68/14.46 dB、相关度 0.9732/0.9821，
        // 13~15 kHz 频带从静默恢复到 -0.3 dB。
        var (snr, corr, format) = Compare("heaac-v2-ps-44k.mp4", "heaac-v2-ps-44k.ref.wav", skipSeconds: 3);

        Assert.Equal(44100, format.SampleRate);
        Assert.Equal(2, format.Channels);
        Assert.True(snr >= 11, $"SNR {snr:F2} dB 低于门禁 11 dB");
        Assert.True(corr >= 0.96, $"相关度 {corr:F4} 低于门禁 0.96");
    }

    [Fact(DisplayName = "HE-AAC v1 后段（5~6 秒）外部向量：SNR ≥ 9 dB")]
    public void HeAacV1_LateSegment()
    {
        // 后段是 SBR 噪声/正弦分量占主导的区间，当前实现与参考解码器存在波形级差异（频谱与时延正常），
        // 门禁只用于防止继续退化，不代表该区间已与参考一致。详见功能清单收口记录。
        var (snr, corr, format) = Compare("heaac-v1-44k.mp4", "heaac-v1-44k.late.ref.wav", skipSeconds: 5);

        Assert.Equal(44100, format.SampleRate);
        Assert.True(snr >= 9, $"SNR {snr:F2} dB 低于门禁 9 dB");
        Assert.True(corr >= 0.93, $"相关度 {corr:F4} 低于门禁 0.93");
    }

    [Fact(DisplayName = "HE-AAC v2 后段（5~6 秒）外部向量：双声道 SNR ≥ 7 dB")]
    public void HeAacV2_LateSegment()
    {
        var (snr, corr, format) = Compare("heaac-v2-ps-44k.mp4", "heaac-v2-ps-44k.late.ref.wav", skipSeconds: 5);

        Assert.Equal(44100, format.SampleRate);
        Assert.Equal(2, format.Channels);
        Assert.True(snr >= 7, $"SNR {snr:F2} dB 低于门禁 7 dB");
        Assert.True(corr >= 0.90, $"相关度 {corr:F4} 低于门禁 0.90");
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
