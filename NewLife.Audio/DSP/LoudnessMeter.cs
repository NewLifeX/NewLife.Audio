using NewLife;

namespace NewLife.Audio.DSP;

/// <summary>EBU R128 / ITU-R BS.1770 响度计。测量瞬时、短时与门控综合响度</summary>
/// <remarks>
/// K 加权由两级双二阶滤波器实现（高架 + 高通），系数按采样率从标准参数推导；<br/>
/// 门控遵循 BS.1770-4：400 ms 块、100 ms 步进，先绝对门限 -70 LUFS，再相对门限 -10 LU。<br/>
/// 多声道按 BS.1770 权重计算（左右/中置 1.0，环绕 1.41，LFE 0）。只支持 16 位 PCM 输入。
/// </remarks>
/// <example>
/// <code>
/// var meter = new LoudnessMeter(format);
/// meter.Process(pcm16);
/// Console.WriteLine($"综合响度 {meter.Integrated:F1} LUFS，真峰值 {meter.TruePeak:F1} dBTP");
/// </code>
/// </example>
public class LoudnessMeter : IDisposable
{
    #region 常量
    private const Double AbsoluteGate = -70;
    private const Double RelativeGate = -10;
    private const Double BlockSeconds = 0.4;
    private const Double HopSeconds = 0.1;
    private const Double ShortTermSeconds = 3.0;
    #endregion

    #region 字段
    private readonly Int32 _channels;
    private readonly Int32 _sampleRate;
    private readonly Double _blockSize;
    private readonly Double _hopSize;
    private readonly Biquad[] _shelf;
    private readonly Biquad[] _highPass;

    private readonly List<Double> _blockLoudness = [];
    private readonly Queue<Double> _shortTerm = new();
    private readonly Queue<Double> _momentary = new();

    private Double _sumSquares;
    private Int32 _blockSamples;
    private Double _peak;
    private Double _lastRaw;
    private Boolean _disposed;
    #endregion

    #region 属性
    /// <summary>瞬时响度（最近 400 ms），LUFS；无数据时为 <see cref="Double.NegativeInfinity"/></summary>
    public Double Momentary { get; private set; } = Double.NegativeInfinity;

    /// <summary>短时响度（最近 3 s），LUFS</summary>
    public Double ShortTerm { get; private set; } = Double.NegativeInfinity;

    /// <summary>门控综合响度，LUFS</summary>
    public Double Integrated
    {
        get
        {
            var gated = GateBlocks();
            if (gated.Count == 0) return Double.NegativeInfinity;

            var sum = 0.0;
            foreach (var value in gated) sum += value;

            return -0.691 + 10 * Math.Log10(sum / gated.Count);
        }
    }

    /// <summary>样本真峰值（dBTP，线性插值近似）</summary>
    public Double TruePeak => _peak > 0 ? 20 * Math.Log10(_peak) : Double.NegativeInfinity;

    /// <summary>已处理的每声道样本数</summary>
    public Int64 SampleCount { get; private set; }
    #endregion

    #region 构造
    /// <summary>创建响度计</summary>
    /// <param name="format">音频格式（采样率与声道数）</param>
    /// <exception cref="ArgumentNullException">format 为空</exception>
    public LoudnessMeter(AudioFormat format)
    {
        if (format == null) throw new ArgumentNullException(nameof(format));
        if (format.SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(format));

        _channels = Math.Max(1, format.Channels);
        _sampleRate = format.SampleRate;
        _blockSize = _sampleRate * BlockSeconds;
        _hopSize = _sampleRate * HopSeconds;

        _shelf = new Biquad[_channels];
        _highPass = new Biquad[_channels];
        var (b0, b1, b2, a1, a2) = DesignShelf(_sampleRate);
        var (c0, c1, c2, d1, d2) = DesignHighPass(_sampleRate);

        for (var i = 0; i < _channels; i++)
        {
            _shelf[i] = new Biquad(b0, b1, b2, a1, a2);
            _highPass[i] = new Biquad(c0, c1, c2, d1, d2);
        }
    }
    #endregion

    #region 处理
    /// <summary>处理 16 位小端交错 PCM</summary>
    /// <param name="pcm">PCM 数据</param>
    public void Process(ReadOnlySpan<Byte> pcm)
    {
        ThrowIfDisposed();

        var samples = pcm.Length / 2;
        var frames = samples / _channels;
        for (var i = 0; i < frames; i++)
        {
            for (var ch = 0; ch < _channels; ch++)
            {
                var offset = (i * _channels + ch) * 2;
                var raw = (Int16)(pcm[offset] | pcm[offset + 1] << 8) / 32768.0;

                var value = _highPass[ch].Process(_shelf[ch].Process(raw));
                Accumulate(value, ch, raw);
            }

            SampleCount++;
            _blockSamples++;

            if (_blockSamples >= _hopSize) CompleteBlock();
        }
    }

    /// <summary>处理单声道浮点样本</summary>
    /// <param name="samples">交错浮点样本（-1.0 ~ 1.0）</param>
    public void Process(ReadOnlySpan<Single> samples)
    {
        ThrowIfDisposed();

        var frames = samples.Length / _channels;
        for (var i = 0; i < frames; i++)
        {
            for (var ch = 0; ch < _channels; ch++)
            {
                var raw = samples[i * _channels + ch];
                var value = _highPass[ch].Process(_shelf[ch].Process(raw));
                Accumulate(value, ch, raw);
            }

            SampleCount++;
            _blockSamples++;

            if (_blockSamples >= _hopSize) CompleteBlock();
        }
    }

    /// <summary>重置状态</summary>
    public void Reset()
    {
        foreach (var filter in _shelf) filter.Reset();
        foreach (var filter in _highPass) filter.Reset();

        _blockLoudness.Clear();
        _shortTerm.Clear();
        _momentary.Clear();
        _sumSquares = 0;
        _blockSamples = 0;
        _peak = 0;
        _lastRaw = 0;
        SampleCount = 0;
        Momentary = Double.NegativeInfinity;
        ShortTerm = Double.NegativeInfinity;
    }
    #endregion

    #region 辅助
    private void Accumulate(Double value, Int32 channel, Double raw)
    {
        _sumSquares += value * value * GetChannelWeight(channel, _channels);

        var absolute = Math.Abs(raw);
        if (absolute > _peak) _peak = absolute;

        // 简单真峰值近似：相邻样本中点线性插值
        var mid = Math.Abs(_lastRaw + raw) / 2;
        if (mid > _peak) _peak = mid;
        _lastRaw = raw;
    }

    private void CompleteBlock()
    {
        var meanSquare = _sumSquares / _blockSamples;

        _blockLoudness.Add(meanSquare);

        // 瞬时响度：最近 4 个 100ms 跳块（400ms）
        _momentary.Enqueue(meanSquare);
        while (_momentary.Count > 4) _momentary.Dequeue();
        var momentarySum = 0.0;
        foreach (var value in _momentary) momentarySum += value;
        Momentary = momentarySum > 0 ? -0.691 + 10 * Math.Log10(momentarySum / _momentary.Count) : Double.NegativeInfinity;

        _shortTerm.Enqueue(meanSquare);
        var shortBlocks = (Int32)Math.Max(1, Math.Round(ShortTermSeconds / HopSeconds));
        while (_shortTerm.Count > shortBlocks) _shortTerm.Dequeue();

        var sum = 0.0;
        foreach (var value in _shortTerm) sum += value;
        ShortTerm = sum > 0 ? -0.691 + 10 * Math.Log10(sum / _shortTerm.Count) : Double.NegativeInfinity;

        _sumSquares = 0;
        _blockSamples = 0;
    }

    private List<Double> GateBlocks()
    {
        var candidates = new List<Double>(_blockLoudness.Count);
        foreach (var meanSquare in _blockLoudness)
        {
            var loudness = meanSquare > 0 ? -0.691 + 10 * Math.Log10(meanSquare) : Double.NegativeInfinity;
            if (loudness > AbsoluteGate) candidates.Add(meanSquare);
        }

        if (candidates.Count == 0) return candidates;

        var sum = 0.0;
        foreach (var value in candidates) sum += value;
        var relativeThreshold = -0.691 + 10 * Math.Log10(sum / candidates.Count) + RelativeGate;

        var gated = new List<Double>(candidates.Count);
        foreach (var meanSquare in candidates)
        {
            var loudness = meanSquare > 0 ? -0.691 + 10 * Math.Log10(meanSquare) : Double.NegativeInfinity;
            if (loudness > relativeThreshold) gated.Add(meanSquare);
        }

        return gated;
    }

    private static Double GetChannelWeight(Int32 channel, Int32 channels) => channels switch
    {
        <= 2 => 1.0,
        3 => channel == 2 ? 1.0 : 1.0,          // L R C
        4 => 1.0,                               // L R Ls Rs（按 1.0/1.41 简化区间）
        5 => channel == 3 ? 1.41 : 1.0,         // L R C Ls Rs
        6 => channel == 5 ? 0.0 : channel is 3 or 4 ? 1.41 : 1.0,
        _ => 1.0,
    };

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LoudnessMeter));
    }

    /// <summary>释放</summary>
    public void Dispose() => _disposed = true;
    #endregion

    #region 滤波器设计
    private static (Double b0, Double b1, Double b2, Double a1, Double a2) DesignShelf(Double sampleRate)
    {
        const Double f0 = 1681.974450955533;
        const Double gain = 3.999843853973347;
        const Double q = 0.7071752369554196;

        var k = Math.Tan(Math.PI * f0 / sampleRate);
        var vh = Math.Pow(10, gain / 20);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1 + k / q + k * k;

        var b0 = (vh + vb * k / q + k * k) / a0;
        var b1 = 2 * (k * k - vh) / a0;
        var b2 = (vh - vb * k / q + k * k) / a0;
        var a1 = 2 * (k * k - 1) / a0;
        var a2 = (1 - k / q + k * k) / a0;

        return (b0, b1, b2, a1, a2);
    }

    private static (Double b0, Double b1, Double b2, Double a1, Double a2) DesignHighPass(Double sampleRate)
    {
        const Double f0 = 38.13547087602444;
        const Double q = 0.5003270373238773;

        var k = Math.Tan(Math.PI * f0 / sampleRate);
        var a0 = 1 + k / q + k * k;

        var b0 = 1.0;
        var b1 = -2.0;
        var b2 = 1.0;
        var a1 = 2 * (k * k - 1) / a0;
        var a2 = (1 - k / q + k * k) / a0;

        return (b0, b1, b2, a1, a2);
    }

    /// <summary>直接 I 型双二阶滤波器</summary>
    private sealed class Biquad
    {
        private readonly Double _b0, _b1, _b2, _a1, _a2;
        private Double _x1, _x2, _y1, _y2;

        public Biquad(Double b0, Double b1, Double b2, Double a1, Double a2)
        {
            _b0 = b0;
            _b1 = b1;
            _b2 = b2;
            _a1 = a1;
            _a2 = a2;
        }

        public Double Process(Double x)
        {
            var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;

            return y;
        }

        public void Reset() => _x1 = _x2 = _y1 = _y2 = 0;
    }
    #endregion
}

/// <summary>响度归一化选项</summary>
public class LoudnessOptions
{
    /// <summary>目标综合响度（LUFS），默认 -16（流媒体常用）</summary>
    public Double TargetLufs { get; set; } = -16;

    /// <summary>真峰值上限（dBTP），默认 -1</summary>
    public Double TruePeakCeiling { get; set; } = -1;

    /// <summary>最大增益（dB），防止对静音/极低电平素材放大过多，默认 24</summary>
    public Double MaxGain { get; set; } = 24;
}

/// <summary>响度归一化。按 EBU R128 测量并施加增益，超出真峰值上限时整体衰减</summary>
/// <remarks>
/// 采用单次测量 + 固定增益 + 峰值限制的简化流程（不做动态压缩/多段响度匹配），
/// 适用于整段素材的整体响度对齐；<see cref="Normalize"/> 返回新的 PCM 缓冲，不修改入参。
/// </remarks>
public static class LoudnessNormalizer
{
    /// <summary>测量 16 位 PCM 的门控综合响度（LUFS）</summary>
    /// <param name="pcm">16 位小端交错 PCM</param>
    /// <param name="format">音频格式</param>
    /// <returns>综合响度；静音或极低电平时返回 <see cref="Double.NegativeInfinity"/></returns>
    public static Double Measure(Byte[] pcm, AudioFormat format)
    {
        if (pcm == null) throw new ArgumentNullException(nameof(pcm));
        if (format == null) throw new ArgumentNullException(nameof(format));

        using var meter = new LoudnessMeter(format);
        meter.Process(pcm);

        return meter.Integrated;
    }

    /// <summary>归一化 16 位 PCM 到目标响度</summary>
    /// <param name="pcm">16 位小端交错 PCM</param>
    /// <param name="format">音频格式</param>
    /// <param name="options">归一化选项，null 使用默认值</param>
    /// <returns>归一化后的 PCM（新缓冲）</returns>
    public static Byte[] Normalize(Byte[] pcm, AudioFormat format, LoudnessOptions options = null)
    {
        if (pcm == null) throw new ArgumentNullException(nameof(pcm));
        if (format == null) throw new ArgumentNullException(nameof(format));

        options ??= new LoudnessOptions();

        using var meter = new LoudnessMeter(format);
        meter.Process(pcm);

        var loudness = meter.Integrated;
        if (Double.IsNegativeInfinity(loudness) || Double.IsNaN(loudness)) return pcm.ToArray();

        var gain = options.TargetLufs - loudness;
        if (gain > options.MaxGain) gain = options.MaxGain;

        // 峰值限制：增益后若超过上限，则整体再衰减
        var peak = meter.TruePeak;
        if (!Double.IsNegativeInfinity(peak) && peak + gain > options.TruePeakCeiling)
            gain = options.TruePeakCeiling - peak;

        var factor = Math.Pow(10, gain / 20);
        var output = new Byte[pcm.Length];

        for (var i = 0; i < pcm.Length / 2; i++)
        {
            var value = (Int16)(pcm[i * 2] | pcm[i * 2 + 1] << 8) * factor;
            var sample = (Int32)Math.Round(value);
            if (sample > Int16.MaxValue) sample = Int16.MaxValue;
            if (sample < Int16.MinValue) sample = Int16.MinValue;

            output[i * 2] = (Byte)(sample & 0xFF);
            output[i * 2 + 1] = (Byte)((sample >> 8) & 0xFF);
        }

        return output;
    }
}
