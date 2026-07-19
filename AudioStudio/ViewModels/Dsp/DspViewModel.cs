using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AudioStudio.Services;
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

    private readonly FftAnalyzer _fftAnalyzer;
    private readonly Double[] _fftInput = new Double[1024];
    private Int32 _fftInputIndex;

    /// <summary>获取均衡器各频段增益数组</summary>
    public Double[] GetEqualizerGains() =>
    [Eq31Hz, Eq62Hz, Eq125Hz, Eq250Hz, Eq500Hz, Eq1kHz, Eq2kHz, Eq4kHz, Eq8kHz, Eq16kHz];

    public DspViewModel()
    {
        var format = new AudioFormat { SampleRate = 44100, Channels = 2, BitsPerSample = 16 };
        _fftAnalyzer = new FftAnalyzer(1024, FftAnalyzer.WindowType.Hann, format);
    }

    /// <summary>从 PCM 数据计算真实频谱（由 AudioPlayerService 触发）</summary>
    public void ProcessPcmData(Byte[] pcmData)
    {
        if (!_isSpectrumActive || pcmData == null || pcmData.Length < 4) return;

        // 转换 16-bit PCM 为 float
        var sampleCount = pcmData.Length / 2;
        for (var i = 0; i < sampleCount && _fftInputIndex < _fftInput.Length; i++)
        {
            var sample = (Int16)(pcmData[i * 2 + 1] << 8 | pcmData[i * 2]);
            _fftInput[_fftInputIndex++] = sample / 32768f;
        }

        // 凑够 FFT 点数后执行分析
        if (_fftInputIndex >= _fftSize)
        {
            var floatBuffer = _fftInput.Select(v => (Single)v).ToArray();

            // 通过 FftAnalyzer 的 Read 管线获取频谱
            var bins = new Single[_fftSize / 2];
            var window = BuildWindow(_fftSize, (FftAnalyzer.WindowType)_selectedWindowIndex);
            for (var i = 0; i < _fftSize; i++)
                floatBuffer[i] *= window[i];

            // 执行 FFT（使用内部的 Cooley-Tukey 实现）
            var fftIn = new Single[_fftSize];
            var fftOut = new Single[_fftSize];
            Array.Copy(floatBuffer, fftIn, _fftSize);
            FftRadix2(fftIn, fftOut);

            // 幅度谱 → 映射到 32 个频带
            var newBins = new Double[32];
            var bandSize = (_fftSize / 2) / 32;
            for (var b = 0; b < 32; b++)
            {
                var sum = 0.0;
                for (var j = 0; j < bandSize; j++)
                {
                    var idx = b * bandSize + j;
                    if (idx < _fftSize / 2)
                    {
                        var mag = Math.Sqrt(fftIn[idx] * fftIn[idx] + fftOut[idx] * fftOut[idx]);
                        sum += mag;
                    }
                }
                newBins[b] = Math.Min(1.0, sum / bandSize * 10);
            }
            FrequencyBands = newBins;
            SpectrumBins = [.. newBins];

            _fftInputIndex = 0;
        }
    }

    private static Single[] BuildWindow(Int32 size, FftAnalyzer.WindowType type)
    {
        var w = new Single[size];
        for (var i = 0; i < size; i++)
        {
            w[i] = type switch
            {
                FftAnalyzer.WindowType.Hann => 0.5f * (1 - (Single)Math.Cos(2 * Math.PI * i / (size - 1))),
                FftAnalyzer.WindowType.Hamming => 0.54f - 0.46f * (Single)Math.Cos(2 * Math.PI * i / (size - 1)),
                FftAnalyzer.WindowType.Blackman => 0.42f - 0.5f * (Single)Math.Cos(2 * Math.PI * i / (size - 1)) + 0.08f * (Single)Math.Cos(4 * Math.PI * i / (size - 1)),
                _ => 1.0f,
            };
        }
        return w;
    }

    private static void FftRadix2(Single[] re, Single[] im)
    {
        var n = re.Length;
        var bits = (Int32)Math.Log(n, 2);

        // 位反转
        for (var i = 0; i < n; i++)
        {
            var j = BitReverse(i, bits);
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        // Cooley-Tukey
        for (var len = 2; len <= n; len <<= 1)
        {
            var half = len >> 1;
            var wAngle = -2.0 * Math.PI / len;
            for (var i = 0; i < n; i += len)
            {
                for (var j = 0; j < half; j++)
                {
                    var w = new Complex((Single)Math.Cos(wAngle * j), (Single)Math.Sin(wAngle * j));
                    var u = new Complex(re[i + j], im[i + j]);
                    var v = new Complex(re[i + j + half], im[i + j + half]) * w;
                    re[i + j] = (Single)(u.Real + v.Real);
                    im[i + j] = (Single)(u.Imag + v.Imag);
                    re[i + j + half] = (Single)(u.Real - v.Real);
                    im[i + j + half] = (Single)(u.Imag - v.Imag);
                }
            }
        }
    }

    private static Int32 BitReverse(Int32 x, Int32 bits)
    {
        var y = 0;
        for (var i = 0; i < bits; i++)
        {
            y = (y << 1) | (x & 1);
            x >>= 1;
        }
        return y;
    }

    private readonly struct Complex(Single real, Single imag)
    {
        public readonly Single Real = real;
        public readonly Single Imag = imag;

        public static Complex operator *(Complex a, Complex b) =>
            new(a.Real * b.Real - a.Imag * b.Imag, a.Real * b.Imag + a.Imag * b.Real);
    }

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

    /// <summary>模拟频谱分析（旧版，保留用于无播放时展示）</summary>
    public void SimulateSpectrum()
    {
        if (_isSpectrumActive) return; // 真实 FFT 激活时跳过模拟
        var rng = new Random();
        for (var i = 0; i < FrequencyBands.Length; i++)
        {
            var baseLevel = 1.0 - (Double)i / FrequencyBands.Length * 0.8;
            var noise = rng.NextDouble() * 0.3;
            FrequencyBands[i] = Math.Max(0, Math.Min(1, baseLevel * Volume + noise));
        }
        SpectrumBins = [.. FrequencyBands];
    }

    // ─── DSP 管线编辑器 ───

    /// <summary>DSP 管线节点项</summary>
    public sealed class PipelineNode : ObservableObject
    {
        /// <summary>处理器显示名称</summary>
        public String DisplayName { get; set; } = "";

        /// <summary>处理器类型描述</summary>
        public String TypeName { get; set; } = "";

        /// <summary>主要参数摘要</summary>
        public String ParamSummary { get; set; } = "";

        /// <summary>处理器在链表中的序号</summary>
        public Int32 Index { get; set; }
    }

    /// <summary>管线节点列表</summary>
    public ObservableCollection<PipelineNode> PipelineNodes { get; } = [];

    /// <summary>可选处理器类型列表</summary>
    public ObservableCollection<PipelineNode> AvailableProcessors { get; } =
    [
        new() { DisplayName = "音量控制", TypeName = "VolumeControl", ParamSummary = "增益 0~2x" },
        new() { DisplayName = "BiQuad 低通", TypeName = "LowPassFilter", ParamSummary = "截止 20~20kHz" },
        new() { DisplayName = "BiQuad 高通", TypeName = "HighPassFilter", ParamSummary = "截止 20~20kHz" },
        new() { DisplayName = "均衡器 10段", TypeName = "Equalizer", ParamSummary = "31Hz~16kHz ±12dB" },
        new() { DisplayName = "均衡器 3段", TypeName = "ToneControl", ParamSummary = "低音/中音/高音" },
        new() { DisplayName = "动态压缩器", TypeName = "DynamicCompressor", ParamSummary = "阈值/比例/启音/释放" },
        new() { DisplayName = "淡入淡出", TypeName = "FadeProcessor", ParamSummary = "淡入/淡出/淡变" },
        new() { DisplayName = "变速器", TypeName = "SpeedChanger", ParamSummary = "0.25x~4x" },
        new() { DisplayName = "变调器", TypeName = "PitchShifter", ParamSummary = "0.25x~4x" },
        new() { DisplayName = "重采样", TypeName = "Resampler", ParamSummary = "任意采样率互转" },
        new() { DisplayName = "声道转换", TypeName = "ChannelConverter", ParamSummary = "单声道↔立体声" },
        new() { DisplayName = "位深转换", TypeName = "BitDepthConverter", ParamSummary = "8/16↔浮点" },
    ];

    [ObservableProperty]
    private PipelineNode? _selectedAvailable;

    [ObservableProperty]
    private PipelineNode? _selectedPipelineNode;

    /// <summary>管线总节点数</summary>
    public Int32 NodeCount => PipelineNodes.Count;

    /// <summary>添加处理器到管线末尾</summary>
    [RelayCommand]
    private void AddToPipeline()
    {
        if (SelectedAvailable == null) return;
        PipelineNodes.Add(new PipelineNode
        {
            DisplayName = SelectedAvailable.DisplayName,
            TypeName = SelectedAvailable.TypeName,
            ParamSummary = SelectedAvailable.ParamSummary,
            Index = PipelineNodes.Count,
        });
        OnPropertyChanged(nameof(NodeCount));
    }

    /// <summary>从管线移除选中处理器</summary>
    [RelayCommand]
    private void RemoveFromPipeline()
    {
        if (SelectedPipelineNode == null) return;
        PipelineNodes.Remove(SelectedPipelineNode);
        Reindex();
    }

    /// <summary>上移选中处理器</summary>
    [RelayCommand]
    private void MoveUp()
    {
        var node = SelectedPipelineNode;
        if (node == null || node.Index <= 0) return;
        var idx = node.Index;
        (PipelineNodes[idx], PipelineNodes[idx - 1]) = (PipelineNodes[idx - 1], PipelineNodes[idx]);
        Reindex();
    }

    /// <summary>下移选中处理器</summary>
    [RelayCommand]
    private void MoveDown()
    {
        var node = SelectedPipelineNode;
        if (node == null || node.Index >= PipelineNodes.Count - 1) return;
        var idx = node.Index;
        (PipelineNodes[idx], PipelineNodes[idx + 1]) = (PipelineNodes[idx + 1], PipelineNodes[idx]);
        Reindex();
    }

    /// <summary>清空整个管线</summary>
    [RelayCommand]
    private void ClearPipeline()
    {
        PipelineNodes.Clear();
        OnPropertyChanged(nameof(NodeCount));
    }

    private void Reindex()
    {
        for (var i = 0; i < PipelineNodes.Count; i++)
            PipelineNodes[i].Index = i;
        OnPropertyChanged(nameof(NodeCount));
    }
}
