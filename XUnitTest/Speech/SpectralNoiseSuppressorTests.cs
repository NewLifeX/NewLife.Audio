using System;
using NewLife.Audio.Speech;
using Xunit;

namespace XUnitTest.Speech;

public class SpectralNoiseSuppressorTests
{
    [Fact(DisplayName = "NS默认学习率0.1与抑制强度12dB")]
    public void Defaults_AreCorrect()
    {
        var ns = new SpectralNoiseSuppressor();
        Assert.Equal(0.1f, ns.LearningRate, 3);
        Assert.Equal(12f, ns.SuppressionLevelDB, 3);
    }

    [Fact(DisplayName = "NS学习率与抑制强度越界自动收敛到边界值")]
    public void Properties_Clamped()
    {
        var ns = new SpectralNoiseSuppressor();
        ns.LearningRate = 2f;
        Assert.Equal(1f, ns.LearningRate);
        ns.LearningRate = -1f;
        Assert.Equal(0f, ns.LearningRate);

        ns.SuppressionLevelDB = 100f;
        Assert.Equal(40f, ns.SuppressionLevelDB);
        ns.SuppressionLevelDB = -5f;
        Assert.Equal(0f, ns.SuppressionLevelDB);
    }

    [Fact(DisplayName = "NS处理短于FFT帧的数据原样返回")]
    public void Suppress_ShortData_ReturnsInput()
    {
        var ns = new SpectralNoiseSuppressor(16000, 512);
        var shortData = new Byte[100]; // 小于 fftSize*2
        var result = ns.Suppress(shortData);
        Assert.Same(shortData, result);
    }

    [Fact(DisplayName = "NS处理随机噪声输出等长且能量衰减")]
    public void Suppress_Noise_LengthPreserved()
    {
        var ns = new SpectralNoiseSuppressor(16000);
        var random = new Random(42);
        var pcm = new Byte[2048 * 2];
        random.NextBytes(pcm);

        var result = ns.Suppress(pcm);
        Assert.NotNull(result);
        Assert.Equal(pcm.Length, result.Length);

        // 频谱减法对稳态噪声应产生能量衰减
        Assert.True(Rms(result) < Rms(pcm), $"输出RMS {Rms(result)} >= 输入RMS {Rms(pcm)}");
    }

    [Fact(DisplayName = "NS处理静音输出等长数据")]
    public void Suppress_Silence_LengthPreserved()
    {
        var ns = new SpectralNoiseSuppressor(16000);
        var silence = new Byte[1600 * 2];
        var result = ns.Suppress(silence);
        Assert.Equal(silence.Length, result.Length);
    }

    [Fact(DisplayName = "NS Reset后可继续处理不抛异常")]
    public void Reset_ThenSuppress_Works()
    {
        var ns = new SpectralNoiseSuppressor(16000);
        var pcm = new Byte[2048 * 2];
        ns.Suppress(pcm);
        ns.Reset();
        var result = ns.Suppress(pcm);
        Assert.Equal(pcm.Length, result.Length);
    }

    private static Double Rms(Byte[] pcm)
    {
        var sum = 0.0;
        var count = pcm.Length / 2;
        for (var i = 0; i < count; i++)
        {
            var v = (Int16)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            sum += (Double)v * v;
        }
        return Math.Sqrt(sum / count);
    }
}
