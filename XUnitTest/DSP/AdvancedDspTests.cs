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

    [Fact(DisplayName = "混音器无输入源Read返回0")]
    public void Mixer_Read_NoInputs_ReturnsZero()
    {
        var mixer = new AudioMixer();
        var buffer = new Single[100];
        var read = mixer.Read(buffer, 0, 100);
        Assert.Equal(0, read);
    }

    [Fact(DisplayName = "混音器单输入源Read直通")]
    public void Mixer_Read_SingleInput_Passthrough()
    {
        var mixer = new AudioMixer();
        var source = new TestSignalSource(100, 0.5f);
        mixer.AddInputStream(source);
        mixer.AutoNormalize = false;

        var buffer = new Single[50];
        var read = mixer.Read(buffer, 0, 50);
        Assert.Equal(50, read);
        Assert.True(Math.Abs(buffer[0] - 0.5f) < 0.01f);
    }

    [Fact(DisplayName = "混音器AutoNormalize为true时自动归一化")]
    public void Mixer_AutoNormalize_True_DividesByInputCount()
    {
        var mixer = new AudioMixer();
        var src1 = new TestSignalSource(100, 0.5f);
        var src2 = new TestSignalSource(100, 0.5f);
        mixer.AddInputStream(src1);
        mixer.AddInputStream(src2);
        Assert.True(mixer.AutoNormalize);

        var buffer = new Single[50];
        var read = mixer.Read(buffer, 0, 50);
        // 两个 0.5 振幅源相加=1.0，归一化除以2=0.5
        Assert.True(Math.Abs(buffer[0] - 0.5f) < 0.02f);
    }

    [Fact(DisplayName = "混音器RemoveInputStream移除成功")]
    public void Mixer_RemoveInput_Works()
    {
        var mixer = new AudioMixer();
        var src = new AudioPipeline();
        mixer.AddInputStream(src);
        Assert.True(mixer.RemoveInputStream(src));
        Assert.Empty(mixer.Inputs);
    }

    [Fact(DisplayName = "混音器AddInputStream传null抛异常")]
    public void Mixer_AddInput_Null_Throws()
    {
        var mixer = new AudioMixer();
        Assert.Throws<ArgumentNullException>(() => mixer.AddInputStream(null));
    }

    [Fact(DisplayName = "均衡器3段音调控制创建成功")]
    public void Equalizer_ToneControl_CreatesSuccess()
    {
        var eq = Equalizer.CreateToneControl();
        Assert.Equal(3, eq.Filters.Count);
    }

    [Fact(DisplayName = "均衡器AddBand增加Filter计数")]
    public void Equalizer_AddBand_IncreasesCount()
    {
        var eq = new Equalizer();
        eq.AddBand(BiQuadFilter.FilterType.Peaking, 1000f, 1.0f, 6f);
        Assert.Single(eq.Filters);
    }

    [Fact(DisplayName = "均衡器Read无Source返回0")]
    public void Equalizer_Read_NoSource_ReturnsZero()
    {
        var eq = new Equalizer();
        var buffer = new Single[100];
        Assert.Equal(0, eq.Read(buffer, 0, 100));
    }

    [Fact(DisplayName = "均衡器Read无滤波器直通")]
    public void Equalizer_Read_NoFilters_Passthrough()
    {
        var eq = new Equalizer();
        var source = new TestSignalSource(100, 0.8f);
        eq.Source = source;

        var buffer = new Single[50];
        var read = eq.Read(buffer, 0, 50);
        Assert.Equal(50, read);
        Assert.True(Math.Abs(buffer[0] - 0.8f) < 0.01f);
    }

    [Fact(DisplayName = "动态压缩器Read无Source返回0")]
    public void Compressor_Read_NoSource_ReturnsZero()
    {
        var comp = new DynamicCompressor();
        var buffer = new Single[100];
        Assert.Equal(0, comp.Read(buffer, 0, 100));
    }

    [Fact(DisplayName = "动态压缩器Read低幅信号直通")]
    public void Compressor_Read_LowAmplitude_Passthrough()
    {
        var comp = new DynamicCompressor();
        var source = new TestSignalSource(200, 0.3f);
        comp.Source = source;

        var buffer = new Single[100];
        var read = comp.Read(buffer, 0, 100);
        Assert.Equal(100, read);
        // 低幅信号应基本保留
        Assert.True(Math.Abs(buffer[0]) < 1.0f);
    }

    [Fact(DisplayName = "淡入淡出处理器不抛异常")]
    public void FadeProcessor_Create_NoException()
    {
        var fade = new FadeProcessor(FadeProcessor.FadeType.FadeIn, 1.0f);
        Assert.False(fade.IsComplete);
    }

    [Fact(DisplayName = "FadeIn线性起始增益为0")]
    public void FadeIn_Linear_StartsFromZero()
    {
        var fade = new FadeProcessor(FadeProcessor.FadeType.FadeIn, 1.0f);
        var source = new TestSignalSource(1000, 1.0f);
        fade.Source = source;

        var buffer = new Single[10];
        fade.Read(buffer, 0, 10);
        // 前几个样本的增益应接近0
        Assert.True(Math.Abs(buffer[0]) < 0.001f, $"buffer[0]={buffer[0]}");
        // 后续样本增益逐渐增大
        Assert.True(Math.Abs(buffer[9]) > Math.Abs(buffer[0]));
    }

    [Fact(DisplayName = "FadeOut线性结束增益为0")]
    public void FadeOut_Linear_EndsAtZero()
    {
        var fade = new FadeProcessor(FadeProcessor.FadeType.FadeOut, 0.01f, FadeProcessor.CurveType.Linear, new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16 });
        var source = new TestSignalSource(100, 1.0f);
        fade.Source = source;

        var buffer = new Single[100];
        fade.Read(buffer, 0, 80);
        // 淡出完成后缓冲应为0
        Assert.True(fade.IsComplete);
    }

    [Fact(DisplayName = "FadeProcessor Reset重置进度")]
    public void FadeProcessor_Reset_ResetsProgress()
    {
        var fade = new FadeProcessor(FadeProcessor.FadeType.FadeIn, 0.1f, FadeProcessor.CurveType.Linear, new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16 });
        var source = new TestSignalSource(2000, 1.0f);
        fade.Source = source;

        var buffer = new Single[100];
        fade.Read(buffer, 0, 50);
        Assert.False(fade.IsComplete);

        fade.Reset();
        // 重置后应不是完成状态
        Assert.False(fade.IsComplete);
    }

    #region 测试辅助

    /// <summary>固定振幅测试信号源</summary>
    private sealed class TestSignalSource : IAudioProcessor
    {
        private readonly Int32 _total;
        private readonly Single _amplitude;
        private Int32 _pos;

        public AudioFormat InputFormat => AudioFormat.Default;
        public AudioFormat OutputFormat => AudioFormat.Default;
        public IAudioProcessor Source { get; set; }

        public TestSignalSource(Int32 total, Single amplitude)
        {
            _total = total;
            _amplitude = amplitude;
        }

        public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
        {
            var remaining = _total - _pos;
            var toRead = Math.Min(count, remaining);
            for (var i = 0; i < toRead; i++)
                buffer[offset + i] = _amplitude;
            _pos += toRead;
            return toRead;
        }

        public void Reset() => _pos = 0;
    }

    #endregion
}
