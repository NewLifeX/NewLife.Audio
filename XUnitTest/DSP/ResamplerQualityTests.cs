using System;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest.DSP;

/// <summary>重采样质量档测试：线性与加窗 sinc、抗混叠与流式连续性</summary>
public class ResamplerQualityTests
{
    [Fact(DisplayName = "Sinc档48k到16k重采样1kHz正弦的带内信噪比达到40dB")]
    public void Sinc_Resample_Sine_HighSnr()
    {
        var input = BuildSine(48000, 1000, 0.5, 48000);
        var output = ToSingle(Resampler.ResamplePcm(input, 48000, 16000, ResamplerQuality.Sinc));

        var snr = InBandSnr(output, 16000, 1000);
        Assert.True(snr > 40, $"Sinc 重采样带内信噪比过低：{snr:F1} dB");
    }

    [Fact(DisplayName = "Sinc档保持通带幅度")]
    public void Sinc_PreservesPassbandAmplitude()
    {
        var input = BuildSine(44100, 15000, 0.5, 44100 * 2);
        var output = ToSingle(Resampler.ResamplePcm(input, 44100, 48000, ResamplerQuality.Sinc));

        var skip = 2048;
        double sum = 0;
        var count = 0;
        for (var i = skip; i < output.Length - skip; i++)
        {
            sum += (Double)output[i] * output[i];
            count++;
        }

        var amplitude = Math.Sqrt(sum / count) * Math.Sqrt(2);
        Assert.InRange(amplitude, 0.48, 0.52);
    }

    [Fact(DisplayName = "Sinc档降采样抑制混叠")]
    public void Sinc_Downsample_RejectsAliasing()
    {
        // 10 kHz 音调降到 8 kHz 采样率：超出奈奎斯特，线性插值会折叠成 2 kHz，sinc 应大幅衰减
        var input = BuildSine(44100, 10000, 0.5, 44100);

        var linear = Rms(ToSingle(Resampler.ResamplePcm(input, 44100, 8000, ResamplerQuality.Linear)));
        var sinc = Rms(ToSingle(Resampler.ResamplePcm(input, 44100, 8000, ResamplerQuality.Sinc)));

        Assert.True(sinc < linear * 0.25, $"Sinc 混叠抑制不足：linear={linear:F4} sinc={sinc:F4}");
    }

    [Fact(DisplayName = "质量档报告群延迟")]
    public void Quality_Reports_Latency()
    {
        var format = new AudioFormat { SampleRate = 48000, Channels = 1, BitsPerSample = 16 };

        Assert.Equal(0, new Resampler(16000, format, ResamplerQuality.Linear).Latency);
        Assert.True(new Resampler(16000, format, ResamplerQuality.Sinc).Latency > 0);
        Assert.True(new Resampler(96000, format, ResamplerQuality.Sinc).LatencyMilliseconds > 0);
    }

    [Fact(DisplayName = "流式读取跨块保持插值连续")]
    public void Streaming_Read_IsContinuous()
    {
        var data = BuildSamples(48000, 1000, 0.5, 9600);
        var format = new AudioFormat { SampleRate = 48000, Channels = 1, BitsPerSample = 16 };

        var single = new Single[1600];
        var whole = new Resampler(16000, format, ResamplerQuality.Sinc) { Source = new BlockSource(data, 4096) };
        var n1 = whole.Read(single, 0, single.Length);

        var split = new Single[1600];
        var chunked = new Resampler(16000, format, ResamplerQuality.Sinc) { Source = new BlockSource(data, 64) };
        var n2 = 0;
        while (n2 < split.Length)
        {
            var chunk = chunked.Read(split, n2, Math.Min(137, split.Length - n2));
            if (chunk <= 0) break;
            n2 += chunk;
        }

        Assert.Equal(n1, n2);
        for (var i = 0; i < n1; i++)
        {
            Assert.True(Math.Abs(single[i] - split[i]) < 1e-4, $"第 {i} 个样本不一致：{single[i]} vs {split[i]}");
        }
    }

    [Fact(DisplayName = "静态重采样边界：相同采样率与非法参数")]
    public void Static_SameRate_And_Invalid()
    {
        var input = BuildSine(8000, 500, 0.4, 800);
        Assert.Equal(input.Length, Resampler.ResamplePcm(input, 8000, 8000, ResamplerQuality.Sinc).Length);
        Assert.Empty(Resampler.ResamplePcm(Array.Empty<Byte>(), 8000, 16000));
        Assert.Throws<ArgumentNullException>(() => Resampler.ResamplePcm(null, 8000, 16000));
        Assert.Throws<ArgumentOutOfRangeException>(() => Resampler.ResamplePcm(input, 0, 16000));
    }

    #region 辅助
    private static Single[] BuildSamples(Int32 rate, Double freq, Double amp, Int32 count)
    {
        var data = new Single[count];
        for (var i = 0; i < count; i++) data[i] = (Single)(Math.Sin(2 * Math.PI * freq * i / rate) * amp);

        return data;
    }

    private static Byte[] BuildSine(Int32 rate, Double freq, Double amp, Int32 count)
    {
        var data = new Byte[count * 2];
        for (var i = 0; i < count; i++)
        {
            var v = (Int16)(Math.Sin(2 * Math.PI * freq * i / rate) * amp * 32767);
            data[i * 2] = (Byte)(v & 0xFF);
            data[i * 2 + 1] = (Byte)((v >> 8) & 0xFF);
        }

        return data;
    }

    private static Single[] ToSingle(Byte[] pcm)
    {
        var data = new Single[pcm.Length / 2];
        for (var i = 0; i < data.Length; i++) data[i] = (Int16)(pcm[i * 2] | pcm[i * 2 + 1] << 8) / 32768f;

        return data;
    }

    private static Double Rms(Single[] data)
    {
        var sum = 0.0;
        foreach (var v in data) sum += (Double)v * v;

        return data.Length > 0 ? Math.Sqrt(sum / data.Length) : 0;
    }

    /// <summary>与同频理想正弦比较，搜索最佳亚样本相位并拟合增益后的信噪比</summary>
    private static Double InBandSnr(Single[] data, Int32 rate, Double freq)
    {
        var skip = Math.Min(2048, data.Length / 4);
        var count = data.Length - skip - skip / 2;
        if (count < 1024) return -999;

        var reference = new Double[count];
        var best = -999.0;

        for (var phase = 0.0; phase < 1.0; phase += 0.005)
        {
            double dot = 0, power = 0, signal = 0;
            for (var i = 0; i < count; i++)
            {
                reference[i] = Math.Sin(2 * Math.PI * freq * (i + phase) / rate);
                var v = data[skip + i];
                dot += v * reference[i];
                power += reference[i] * reference[i];
                signal += (Double)v * v;
            }

            var gain = dot / power;
            double error = 0;
            for (var i = 0; i < count; i++)
            {
                var d = data[skip + i] - gain * reference[i];
                error += d * d;
            }

            var snr = 10 * Math.Log10(signal / Math.Max(error, 1e-12));
            if (snr > best) best = snr;
        }

        return best;
    }

    private sealed class BlockSource : IAudioProcessor
    {
        private readonly Single[] _data;
        private readonly Int32 _blockSize;
        private Int32 _offset;

        public BlockSource(Single[] data, Int32 blockSize = Int32.MaxValue)
        {
            _data = data;
            _blockSize = blockSize;
        }

        public AudioFormat InputFormat => new() { SampleRate = 48000 };
        public AudioFormat OutputFormat => InputFormat;
        public IAudioProcessor Source { get; set; }

        public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
        {
            var n = Math.Min(Math.Min(count, _blockSize), _data.Length - _offset);
            if (n <= 0) return 0;

            Array.Copy(_data, _offset, buffer, offset, n);
            _offset += n;
            return n;
        }

        public void Reset() => _offset = 0;
    }
    #endregion
}
