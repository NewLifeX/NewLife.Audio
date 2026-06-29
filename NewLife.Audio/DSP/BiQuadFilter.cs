namespace NewLife.Audio.DSP;

/// <summary>BiQuad 数字滤波器。基于 Robert Bristow-Johnson 音频 EQ 标准公式</summary>
/// <remarks>
/// 支持类型：低通(LPF)、高通(HPF)、带通(BPF)、带阻(BSF/Notch)、峰值(Peaking)、
/// 低架(LowShelf)、高架(HighShelf)。
/// 
/// 公式来源：https://webaudio.github.io/Audio-EQ-Cookbook/audio-eq-cookbook.html
/// </remarks>
public class BiQuadFilter : IAudioProcessor
{
    /// <summary>滤波器类型</summary>
    public enum FilterType
    {
        LowPass,
        HighPass,
        BandPass,
        Notch,
        Peaking,
        LowShelf,
        HighShelf,
    }

    private readonly AudioFormat _inputFormat;
    private readonly AudioFormat _outputFormat;
    private readonly FilterType _type;

    // 系数
    private Single _b0, _b1, _b2;
    private Single _a1, _a2;

    // 状态
    private Single _x1, _x2;
    private Single _y1, _y2;

    private Single _frequency;
    private Single _q;
    private Single _gainDB;

    /// <summary>输入格式</summary>
    public AudioFormat InputFormat => _inputFormat;

    /// <summary>输出格式</summary>
    public AudioFormat OutputFormat => _outputFormat;

    /// <summary>上游源</summary>
    public IAudioProcessor Source { get; set; }

    /// <summary>中心/截止频率（Hz）</summary>
    public Single Frequency
    {
        get => _frequency;
        set { _frequency = value; Recalculate(); }
    }

    /// <summary>品质因数 Q</summary>
    public Single Q
    {
        get => _q;
        set { _q = value; Recalculate(); }
    }

    /// <summary>增益（dB），仅 Peaking/LowShelf/HighShelf 有效</summary>
    public Single GainDB
    {
        get => _gainDB;
        set { _gainDB = value; Recalculate(); }
    }

    /// <summary>初始化 BiQuad 滤波器</summary>
    /// <param name="type">滤波器类型</param>
    /// <param name="frequency">中心/截止频率（Hz）</param>
    /// <param name="q">品质因数（默认 0.7071 = Butterworth）</param>
    /// <param name="gainDB">增益（dB），仅 Peaking/Shelf 有效</param>
    /// <param name="inputFormat">输入格式</param>
    public BiQuadFilter(FilterType type, Single frequency, Single q = 0.7071f, Single gainDB = 0f, AudioFormat inputFormat = null)
    {
        _inputFormat = inputFormat?.Clone() ?? AudioFormat.Default;
        _outputFormat = _inputFormat.Clone();
        _type = type;
        _frequency = frequency;
        _q = q;
        _gainDB = gainDB;
        Recalculate();
    }

    /// <summary>读取滤波后的数据</summary>
    public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
    {
        if (Source == null) return 0;

        var read = Source.Read(buffer, offset, count);
        for (var i = 0; i < read; i++)
        {
            var idx = offset + i;
            var x0 = buffer[idx];

            // 差分方程
            var y0 = _b0 * x0 + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;

            _x2 = _x1;
            _x1 = x0;
            _y2 = _y1;
            _y1 = y0;

            buffer[idx] = y0;
        }

        return read;
    }

    /// <summary>重置滤波器状态</summary>
    public void Reset()
    {
        _x1 = _x2 = _y1 = _y2 = 0;
    }

    /// <summary>处理单个采样</summary>
    public Single ProcessSample(Single sample)
    {
        var y0 = _b0 * sample + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1; _x1 = sample;
        _y2 = _y1; _y1 = y0;
        return y0;
    }

    private void Recalculate()
    {
        var w0 = 2.0 * Math.PI * _frequency / _inputFormat.SampleRate;
        var cosW0 = (Single)Math.Cos(w0);
        var sinW0 = (Single)Math.Sin(w0);
        var alpha = sinW0 / (2f * _q);
        var A = (Single)Math.Pow(10, _gainDB / 40f);

        switch (_type)
        {
            case FilterType.LowPass:
                _b0 = (1f - cosW0) / 2f;
                _b1 = 1f - cosW0;
                _b2 = (1f - cosW0) / 2f;
                _a1 = -2f * cosW0;
                _a2 = 1f - alpha;
                {
                    var norm = 1f / (1f + alpha);
                    _b0 *= norm; _b1 *= norm; _b2 *= norm;
                    _a1 *= norm; _a2 *= norm;
                }
                break;

            case FilterType.HighPass:
                _b0 = (1f + cosW0) / 2f;
                _b1 = -(1f + cosW0);
                _b2 = (1f + cosW0) / 2f;
                _a1 = -2f * cosW0;
                _a2 = 1f - alpha;
                {
                    var norm = 1f / (1f + alpha);
                    _b0 *= norm; _b1 *= norm; _b2 *= norm;
                    _a1 *= norm; _a2 *= norm;
                }
                break;

            case FilterType.BandPass:
                _b0 = alpha;
                _b1 = 0f;
                _b2 = -alpha;
                _a1 = -2f * cosW0;
                _a2 = 1f - alpha;
                {
                    var norm = 1f / (1f + alpha);
                    _b0 *= norm; _b1 *= norm; _b2 *= norm;
                    _a1 *= norm; _a2 *= norm;
                }
                break;

            case FilterType.Notch:
                _b0 = 1f;
                _b1 = -2f * cosW0;
                _b2 = 1f;
                _a1 = -2f * cosW0;
                _a2 = 1f - alpha;
                {
                    var norm = 1f / (1f + alpha);
                    _b0 *= norm; _b1 *= norm; _b2 *= norm;
                    _a1 *= norm; _a2 *= norm;
                }
                break;

            case FilterType.Peaking:
                _b0 = 1f + alpha * A;
                _b1 = -2f * cosW0;
                _b2 = 1f - alpha * A;
                _a1 = -2f * cosW0;
                _a2 = 1f - alpha / A;
                {
                    var norm = 1f / (1f + alpha / A);
                    _b0 *= norm; _b1 *= norm; _b2 *= norm;
                    _a1 *= norm; _a2 *= norm;
                }
                break;

            case FilterType.LowShelf:
                {
                    var twoSqrtAAlpha = 2f * (Single)Math.Sqrt(A) * alpha;
                    _b0 = A * ((A + 1f) - (A - 1f) * cosW0 + twoSqrtAAlpha);
                    _b1 = 2f * A * ((A - 1f) - (A + 1f) * cosW0);
                    _b2 = A * ((A + 1f) - (A - 1f) * cosW0 - twoSqrtAAlpha);
                    _a1 = -2f * ((A - 1f) + (A + 1f) * cosW0);
                    _a2 = (A + 1f) + (A - 1f) * cosW0 - twoSqrtAAlpha;
                    var norm = 1f / ((A + 1f) + (A - 1f) * cosW0 + twoSqrtAAlpha);
                    _b0 *= norm; _b1 *= norm; _b2 *= norm;
                    _a1 *= norm; _a2 *= norm;
                }
                break;

            case FilterType.HighShelf:
                {
                    var twoSqrtAAlpha = 2f * (Single)Math.Sqrt(A) * alpha;
                    _b0 = A * ((A + 1f) + (A - 1f) * cosW0 + twoSqrtAAlpha);
                    _b1 = -2f * A * ((A - 1f) + (A + 1f) * cosW0);
                    _b2 = A * ((A + 1f) + (A - 1f) * cosW0 - twoSqrtAAlpha);
                    _a1 = 2f * ((A - 1f) - (A + 1f) * cosW0);
                    _a2 = (A + 1f) - (A - 1f) * cosW0 - twoSqrtAAlpha;
                    var norm = 1f / ((A + 1f) - (A - 1f) * cosW0 + twoSqrtAAlpha);
                    _b0 *= norm; _b1 *= norm; _b2 *= norm;
                    _a1 *= norm; _a2 *= norm;
                }
                break;
        }
    }
}
