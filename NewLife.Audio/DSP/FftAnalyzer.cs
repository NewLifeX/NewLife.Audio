namespace NewLife.Audio.DSP;

/// <summary>FFT 频谱分析器。基于 Cooley-Tukey 基数-2 实数 FFT</summary>
/// <remarks>
/// 支持多种窗函数（Hann/Hamming/Blackman/矩形）。
/// 输出幅度谱和功率谱（dBFS）。
/// </remarks>
public class FftAnalyzer : IAudioProcessor
{
    private readonly AudioFormat _inputFormat;
    private readonly AudioFormat _outputFormat;
    private readonly Int32 _fftSize;
    private readonly Single[] _window;
    private readonly Int32[] _bitReversed;
    private readonly Single[] _cosTable;
    private readonly Single[] _sinTable;

    /// <summary>FFT 点数（2的幂）</summary>
    public Int32 FftSize => _fftSize;

    /// <summary>频率分辨率（Hz）</summary>
    public Single FrequencyResolution => (Single)_inputFormat.SampleRate / _fftSize;

    /// <summary>输入格式</summary>
    public AudioFormat InputFormat => _inputFormat;

    /// <summary>输出格式</summary>
    public AudioFormat OutputFormat => _outputFormat;

    /// <summary>上游源</summary>
    public IAudioProcessor Source { get; set; }

    /// <summary>窗函数类型</summary>
    public enum WindowType { Rectangular, Hann, Hamming, Blackman }

    /// <summary>初始化 FFT 分析器</summary>
    /// <param name="fftSize">FFT 点数（2的幂，默认1024）</param>
    /// <param name="windowType">窗函数类型</param>
    /// <param name="inputFormat">输入格式</param>
    public FftAnalyzer(Int32 fftSize = 1024, WindowType windowType = WindowType.Hann, AudioFormat inputFormat = null)
    {
        _inputFormat = inputFormat?.Clone() ?? AudioFormat.Default;
        _outputFormat = _inputFormat.Clone();
        _fftSize = fftSize;

        // 预计算窗口
        _window = BuildWindow(fftSize, windowType);

        // 预计算位反转索引
        _bitReversed = new Int32[fftSize];
        var bits = (Int32)Math.Log(fftSize, 2);
        for (var i = 0; i < fftSize; i++)
            _bitReversed[i] = BitReverse(i, bits);

        // 预计算旋转因子
        _cosTable = new Single[fftSize / 2];
        _sinTable = new Single[fftSize / 2];
        for (var i = 0; i < fftSize / 2; i++)
        {
            var angle = -2.0 * Math.PI * i / fftSize;
            _cosTable[i] = (Single)Math.Cos(angle);
            _sinTable[i] = (Single)Math.Sin(angle);
        }
    }

    /// <summary>读取处理后的数据（传递原始数据，不改变）</summary>
    public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
    {
        if (Source == null) return 0;
        return Source.Read(buffer, offset, count);
    }

    /// <summary>重置</summary>
    public void Reset() { }

    /// <summary>计算幅度谱（线性幅度）</summary>
    /// <param name="samples">时域采样数据（长度 = fftSize）</param>
    /// <returns>幅度谱（长度 = fftSize/2 + 1）</returns>
    public Single[] ComputeMagnitudeSpectrum(Single[] samples)
    {
        var real = new Single[_fftSize];
        var imag = new Single[_fftSize];

        // 加窗 + 拷贝到实部
        for (var i = 0; i < Math.Min(samples.Length, _fftSize); i++)
            real[_bitReversed[i]] = samples[i] * _window[i];

        // 原位 FFT
        FftInPlace(real, imag, false);

        // 计算幅度谱
        var spectrum = new Single[_fftSize / 2 + 1];
        for (var i = 0; i < spectrum.Length; i++)
            spectrum[i] = (Single)Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]);

        return spectrum;
    }

    /// <summary>计算功率谱（dBFS）</summary>
    /// <param name="samples">时域采样数据</param>
    /// <returns>功率谱（dBFS，长度 = fftSize/2 + 1）</returns>
    public Single[] ComputePowerSpectrumDB(Single[] samples)
    {
        var magnitude = ComputeMagnitudeSpectrum(samples);
        var power = new Single[magnitude.Length];
        var norm = _fftSize * 0.5f; // 归一化因子
        for (var i = 0; i < power.Length; i++)
        {
            var val = magnitude[i] / norm;
            power[i] = val > 1e-10f ? 20f * (Single)Math.Log10(val) : -120f;
        }
        return power;
    }

    #region FFT 核心

    private void FftInPlace(Single[] real, Single[] imag, Boolean inverse)
    {
        var n = _fftSize;
        for (var len = 2; len <= n; len <<= 1)
        {
            var halfLen = len >> 1;
            var step = n / len;
            for (var i = 0; i < n; i += len)
            {
                for (var j = 0; j < halfLen; j++)
                {
                    var twiddleIdx = j * step;
                    var cos = _cosTable[twiddleIdx];
                    var sin = inverse ? -_sinTable[twiddleIdx] : _sinTable[twiddleIdx];

                    var tReal = real[i + j + halfLen] * cos - imag[i + j + halfLen] * sin;
                    var tImag = real[i + j + halfLen] * sin + imag[i + j + halfLen] * cos;

                    real[i + j + halfLen] = real[i + j] - tReal;
                    imag[i + j + halfLen] = imag[i + j] - tImag;
                    real[i + j] += tReal;
                    imag[i + j] += tImag;
                }
            }
        }

        if (inverse)
        {
            for (var i = 0; i < n; i++)
            {
                real[i] /= n;
                imag[i] /= n;
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

    #endregion

    #region 窗函数

    private static Single[] BuildWindow(Int32 size, WindowType type)
    {
        var window = new Single[size];
        for (var i = 0; i < size; i++)
        {
            window[i] = type switch
            {
                WindowType.Hann => 0.5f * (1 - (Single)Math.Cos(2 * Math.PI * i / (size - 1))),
                WindowType.Hamming => 0.54f - 0.46f * (Single)Math.Cos(2 * Math.PI * i / (size - 1)),
                WindowType.Blackman => 0.42f - 0.5f * (Single)Math.Cos(2 * Math.PI * i / (size - 1)) + 0.08f * (Single)Math.Cos(4 * Math.PI * i / (size - 1)),
                _ => 1.0f, // Rectangular
            };
        }
        return window;
    }

    #endregion
}
