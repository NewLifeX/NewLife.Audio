namespace NewLife.Audio.DSP;

/// <summary>动态压缩器。降低超过阈值的信号电平，提升整体响度</summary>
/// <remarks>
/// 标准压缩器参数：阈值(Threshold)、压缩比(Ratio)、启音时间(Attack)、释放时间(Release)。
/// 软拐点（Soft Knee）平滑过渡，增益补偿（Makeup Gain）。
/// </remarks>
public class DynamicCompressor : IAudioProcessor
{
    private readonly AudioFormat _inputFormat;
    private readonly AudioFormat _outputFormat;

    private Single _threshold;    // 线性阈值（0~1）
    private Single _ratio;        // 压缩比
    private Single _attackTime;   // 启音时间（秒）
    private Single _releaseTime;  // 释放时间（秒）
    private Single _kneeWidth;    // 拐点宽度（dB）
    private Single _makeupGain;   // 增益补偿（线性）

    // 包络跟随状态
    private Single _envelope;
    private Single _attackCoeff;
    private Single _releaseCoeff;

    /// <summary>输入格式</summary>
    public AudioFormat InputFormat => _inputFormat;

    /// <summary>输出格式</summary>
    public AudioFormat OutputFormat => _outputFormat;

    /// <summary>上游源</summary>
    public IAudioProcessor Source { get; set; }

    /// <summary>阈值（dBFS），默认 -20dB</summary>
    public Single ThresholdDB
    {
        get => 20f * (Single)Math.Log10(Math.Max(_threshold, 0.0001f));
        set
        {
            _threshold = (Single)Math.Pow(10, value / 20f);
            RecalculateCoefficients();
        }
    }

    /// <summary>压缩比（1:1 = 不压缩，∞:1 = 限幅），默认 4:1</summary>
    public Single Ratio
    {
        get => _ratio;
        set { _ratio = Math.Max(1, value); RecalculateCoefficients(); }
    }

    /// <summary>启音时间（ms），默认 10ms</summary>
    public Single AttackMs
    {
        get => _attackTime * 1000f;
        set
        {
            _attackTime = value / 1000f;
            RecalculateCoefficients();
        }
    }

    /// <summary>释放时间（ms），默认 100ms</summary>
    public Single ReleaseMs
    {
        get => _releaseTime * 1000f;
        set
        {
            _releaseTime = value / 1000f;
            RecalculateCoefficients();
        }
    }

    /// <summary>拐点宽度（dB），默认 6dB（软拐点）</summary>
    public Single KneeWidthDB
    {
        get => _kneeWidth;
        set => _kneeWidth = Math.Max(0, value);
    }

    /// <summary>增益补偿（dB），默认 0dB</summary>
    public Single MakeupGainDB
    {
        get => 20f * (Single)Math.Log10(Math.Max(_makeupGain, 0.0001f));
        set => _makeupGain = (Single)Math.Pow(10, value / 20f);
    }

    /// <summary>当前增益衰减（dBFS）</summary>
    public Single CurrentGainReductionDB => _envelope > 1e-10f ? -20f * (Single)Math.Log10(_envelope) : 0f;

    /// <summary>初始化动态压缩器</summary>
    /// <param name="inputFormat">输入格式</param>
    public DynamicCompressor(AudioFormat inputFormat = null)
    {
        _inputFormat = inputFormat?.Clone() ?? AudioFormat.Default;
        _outputFormat = _inputFormat.Clone();
        _threshold = (Single)Math.Pow(10, -20f / 20f); // -20dB
        _ratio = 4f;
        _attackTime = 0.01f;
        _releaseTime = 0.1f;
        _kneeWidth = 6f;
        _makeupGain = 1f;
        _envelope = 0;
        RecalculateCoefficients();
    }

    /// <summary>读取压缩后的数据</summary>
    public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
    {
        if (Source == null) return 0;

        var read = Source.Read(buffer, offset, count);
        for (var i = 0; i < read; i++)
        {
            var idx = offset + i;
            var input = Math.Abs(buffer[idx]);
            var inputDB = input > 1e-10f ? 20f * (Single)Math.Log10(input) : -120f;
            var thresholdDB = 20f * (Single)Math.Log10(_threshold);

            // 计算增益衰减
            Single gainReductionDB;
            if (inputDB < thresholdDB - _kneeWidth / 2f)
            {
                gainReductionDB = 0f; // 低于阈值，不压缩
            }
            else if (inputDB > thresholdDB + _kneeWidth / 2f)
            {
                gainReductionDB = (inputDB - thresholdDB) * (1f - 1f / _ratio); // 高于阈值+拐点
            }
            else
            {
                // 软拐点范围内
                var kneeHalf = _kneeWidth / 2f;
                var delta = inputDB - (thresholdDB - kneeHalf);
                var gain = delta / _kneeWidth;
                gainReductionDB = gain * gain * _kneeWidth * (1f - 1f / _ratio) / 4f;
            }

            var targetEnvelope = (Single)Math.Pow(10, -gainReductionDB / 20f);

            // 包络跟随
            var coeff = targetEnvelope < _envelope ? _attackCoeff : _releaseCoeff;
            _envelope = coeff * _envelope + (1f - coeff) * targetEnvelope;

            buffer[idx] *= _envelope * _makeupGain;
        }

        return read;
    }

    /// <summary>重置</summary>
    public void Reset() => _envelope = 0;

    private void RecalculateCoefficients()
    {
        var sr = _inputFormat.SampleRate;
        _attackCoeff = (Single)Math.Exp(-1.0 / (sr * _attackTime));
        _releaseCoeff = (Single)Math.Exp(-1.0 / (sr * _releaseTime));
    }
}
