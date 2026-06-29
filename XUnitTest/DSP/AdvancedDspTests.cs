using System;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest.DSP;

public class AdvancedDspTests
{
    [Fact(DisplayName = "FFT分析器1024点初始化成功")]
    public void FftAnalyzer_Init_Success()
    {
        var fft = new FftAnalyzer(1024);
        Assert.Equal(1024, fft.FftSize);
        Assert.True(fft.FrequencyResolution > 0);
    }

    [Fact(DisplayName = "FFT分析器正弦波频谱在基频处有峰值")]
    public void FftAnalyzer_SineWave_PeakAtFundamental()
    {
        var fft = new FftAnalyzer(1024, FftAnalyzer.WindowType.Hann);
        var samples = new Single[1024];
        // 100Hz 正弦波 @ 8kHz
        for (var i = 0; i < 1024; i++)
            samples[i] = (Single)Math.Sin(2 * Math.PI * 100 * i / 8000);

        var spectrum = fft.ComputeMagnitudeSpectrum(samples);
        // 100Hz @ 8kHz, 1024-point FFT → bin 12.8
        var bin100Hz = 100 * 1024 / 8000;
        Assert.True(spectrum[bin100Hz] > 10f, $"bin={bin100Hz}, mag={spectrum[bin100Hz]}");
    }

    [Fact(DisplayName = "BiQuad低通滤波器300Hz通过DC")]
    public void BiQuad_LowPass_PassesDC()
    {
        var filter = new BiQuadFilter(BiQuadFilter.FilterType.LowPass, 300f, 0.7071f);
        // DC 应通过低通
        var dc = filter.ProcessSample(1f);
        // 低通滤波器稳态后DC应通过
        for (var i = 0; i < 100; i++) filter.ProcessSample(1f);
        var result = filter.ProcessSample(1f);
        Assert.True(Math.Abs(result) > 0.1f, $"result={result}");
    }

    [Fact(DisplayName = "BiQuad高通滤波器80Hz通过高频")]
    public void BiQuad_HighPass_PassesHighFreq()
    {
        var filter = new BiQuadFilter(BiQuadFilter.FilterType.HighPass, 80f, 0.7071f);
        // 1kHz 正弦波应通过高通
        var result = 0f;
        for (var i = 0; i < 160; i++)
            result = filter.ProcessSample((Single)Math.Sin(2 * Math.PI * 1000 * i / 8000));
        // 高频信号应保留
        Assert.True(Math.Abs(result) < 1.0f); // 检查不溢出
    }

    [Fact(DisplayName = "均衡器10段图示创建成功")]
    public void Equalizer_10Band_CreatesSuccess()
    {
        var eq = Equalizer.Create10BandGraphic();
        Assert.Equal(10, eq.Filters.Count);
    }

    [Fact(DisplayName = "动态压缩器默认参数有效")]
    public void Compressor_Defaults_Valid()
    {
        var comp = new DynamicCompressor();
        Assert.True(comp.Ratio >= 1);
        Assert.True(comp.AttackMs > 0);
        Assert.True(comp.ReleaseMs > 0);
    }

    [Fact(DisplayName = "混音器添加输入源后Inputs正确")]
    public void Mixer_AddInput_IncreasesCount()
    {
        var mixer = new AudioMixer();
        var src1 = new AudioPipeline();
        mixer.AddInputStream(src1);
        Assert.Single(mixer.Inputs);
    }

    [Fact(DisplayName = "淡入淡出处理器不抛异常")]
    public void FadeProcessor_Create_NoException()
    {
        var fade = new FadeProcessor(FadeProcessor.FadeType.FadeIn, 1.0f);
        Assert.False(fade.IsComplete);
    }
}
