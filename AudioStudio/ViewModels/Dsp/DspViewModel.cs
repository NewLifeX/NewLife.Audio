using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NewLife.Audio.DSP;

namespace AudioStudio.ViewModels.Dsp;

/// <summary>DSP 处理视图模型</summary>
public partial class DspViewModel : ViewModelBase
{
    [ObservableProperty]
    private Double _volume = 1.0;

    [ObservableProperty]
    private Boolean _isMuted;

    [ObservableProperty]
    private Double _pan = 0.0; // -1.0~1.0

    [ObservableProperty]
    private Double _bassGain = 0.0;

    [ObservableProperty]
    private Double _midGain = 0.0;

    [ObservableProperty]
    private Double _trebleGain = 0.0;

    // 10段均衡器 (31Hz ~ 16kHz)
    [ObservableProperty]
    private Double _eq31Hz;

    [ObservableProperty]
    private Double _eq62Hz;

    [ObservableProperty]
    private Double _eq125Hz;

    [ObservableProperty]
    private Double _eq250Hz;

    [ObservableProperty]
    private Double _eq500Hz;

    [ObservableProperty]
    private Double _eq1kHz;

    [ObservableProperty]
    private Double _eq2kHz;

    [ObservableProperty]
    private Double _eq4kHz;

    [ObservableProperty]
    private Double _eq8kHz;

    [ObservableProperty]
    private Double _eq16kHz;

    // 滤镜
    [ObservableProperty]
    private Boolean _enableLowPass;

    [ObservableProperty]
    private Double _lowPassCutoff = 20000;

    [ObservableProperty]
    private Boolean _enableHighPass;

    [ObservableProperty]
    private Double _highPassCutoff = 20;

    // 变速变调
    [ObservableProperty]
    private Double _speed = 1.0;

    [ObservableProperty]
    private Double _pitch = 1.0;

    // FFT 频谱
    [ObservableProperty]
    private Double[] _spectrumBins = new Double[32];

    [ObservableProperty]
    private Int32 _fftSize = 1024;

    [ObservableProperty]
    private Int32 _selectedWindowIndex;

    [ObservableProperty]
    private Boolean _isSpectrumActive;

    /// <summary>FFT 窗函数选项</summary>
    public String[] WindowOptions => ["矩形窗", "Hann窗", "Hamming窗", "Blackman窗"];

    /// <summary>频谱数据（用于绑定波形图）</summary>
    public Double[] FrequencyBands { get; private set; } = new Double[32];

    private readonly FftAnalyzer _fftAnalyzer = new();
    private readonly Random _rng = new();

    /// <summary>重置均衡器</summary>
    [RelayCommand]
    private void ResetEqualizer()
    {
        BassGain = MidGain = TrebleGain = 0;
        Eq31Hz = Eq62Hz = Eq125Hz = Eq250Hz = Eq500Hz = 0;
        Eq1kHz = Eq2kHz = Eq4kHz = Eq8kHz = Eq16kHz = 0;
    }

    /// <summary>重置所有 DSP 设置</summary>
    [RelayCommand]
    private void ResetAll()
    {
        Volume = 1.0;
        IsMuted = false;
        Pan = 0;
        Speed = 1.0;
        Pitch = 1.0;
        EnableLowPass = false;
        EnableHighPass = false;
        LowPassCutoff = 20000;
        HighPassCutoff = 20;
        ResetEqualizer();
    }

    /// <summary>模拟频谱分析（生成随机频谱数据用于展示）</summary>
    public void SimulateSpectrum()
    {
        for (var i = 0; i < FrequencyBands.Length; i++)
        {
            // 模拟音频频谱：低频高能，高频衰减
            var baseLevel = 1.0 - (Double)i / FrequencyBands.Length * 0.8;
            var noise = _rng.NextDouble() * 0.3;
            FrequencyBands[i] = Math.Max(0, Math.Min(1, baseLevel * Volume + noise));
        }
        SpectrumBins = [.. FrequencyBands];
    }

    /// <summary>获取均衡器各频段增益数组（用于创建 Equalizer）</summary>
    public Double[] GetEqualizerGains() =>
    [Eq31Hz, Eq62Hz, Eq125Hz, Eq250Hz, Eq500Hz, Eq1kHz, Eq2kHz, Eq4kHz, Eq8kHz, Eq16kHz];
}
