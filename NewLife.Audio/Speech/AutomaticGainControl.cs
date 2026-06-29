using NewLife.Audio.DSP;

namespace NewLife.Audio.Speech;

/// <summary>自动增益控制器。动态调整音频电平至目标范围</summary>
/// <remarks>
/// 实现 IAudioProcessor 接口，无缝接入 M3 信号链。
/// 慢启快放策略：Attack ~10ms, Release ~500ms。
/// </remarks>
public class AutomaticGainControl : IAudioProcessor
{
    private readonly AudioFormat _inputFormat;
    private readonly AudioFormat _outputFormat;

    private Single _targetLevel;
    private Single _currentGain;
    private Single _maxGain;
    private Single _attackCoeff;
    private Single _releaseCoeff;

    // RMS 包络
    private Single _rmsEnvelope;

    /// <summary>输入格式</summary>
    public AudioFormat InputFormat => _inputFormat;

    /// <summary>输出格式</summary>
    public AudioFormat OutputFormat => _outputFormat;

    /// <summary>上游源</summary>
    public IAudioProcessor Source { get; set; }

    /// <summary>目标电平（线性，0~1，默认 -18dBFS ≈ 0.125）</summary>
    public Single TargetLevel
    {
        get => _targetLevel;
        set => _targetLevel = Clamp(value, 0.001f, 1f);
    }

    /// <summary>目标电平（dBFS），默认 -18dB</summary>
    public Single TargetLevelDB
    {
        get => 20f * (Single)Math.Log10(_targetLevel);
        set => _targetLevel = (Single)Math.Pow(10, value / 20f);
    }

    /// <summary>启音时间（ms），默认 10ms</summary>
    public Single AttackMs
    {
        get => -1000f / ((Single)Math.Log(_attackCoeff) * _inputFormat.SampleRate);
        set => _attackCoeff = (Single)Math.Exp(-1000.0 / (_inputFormat.SampleRate * value));
    }

    /// <summary>释放时间（ms），默认 500ms</summary>
    public Single ReleaseMs
    {
        get => -1000f / ((Single)Math.Log(_releaseCoeff) * _inputFormat.SampleRate);
        set => _releaseCoeff = (Single)Math.Exp(-1000.0 / (_inputFormat.SampleRate * value));
    }

    /// <summary>当前增益系数</summary>
    public Single CurrentGain => _currentGain;

    /// <summary>最大增益系数（限制噪声放大），默认 10x</summary>
    public Single MaxGain
    {
        get => _maxGain;
        set => _maxGain = Math.Max(1f, value);
    }

    /// <summary>初始化 AGC</summary>
    /// <param name="targetLevelDB">目标电平（dBFS），默认 -18dB</param>
    /// <param name="maxGainDB">最大增益（dB），默认 20dB</param>
    /// <param name="inputFormat">输入格式</param>
    public AutomaticGainControl(Single targetLevelDB = -18f, Single maxGainDB = 20f, AudioFormat inputFormat = null)
    {
        _inputFormat = inputFormat?.Clone() ?? AudioFormat.Default;
        _outputFormat = _inputFormat.Clone();
        _targetLevel = (Single)Math.Pow(10, targetLevelDB / 20f);
        _currentGain = 1f;
        _maxGain = (Single)Math.Pow(10, maxGainDB / 20f);
        _rmsEnvelope = 0.001f;

        AttackMs = 10f;
        ReleaseMs = 500f;
    }

    /// <summary>读取增益调整后的数据</summary>
    public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
    {
        if (Source == null) return 0;

        var read = Source.Read(buffer, offset, count);

        for (var i = 0; i < read; i++)
        {
            var sample = buffer[offset + i];

            // RMS 包络检测
            _rmsEnvelope = _rmsEnvelope * 0.99f + sample * sample * 0.01f;
            var rms = (Single)Math.Sqrt(_rmsEnvelope);

            // 目标增益
            var targetGain = rms > 1e-10f ? _targetLevel / rms : _maxGain;
            targetGain = Math.Min(targetGain, _maxGain);

            // 包络跟随增益
            var coeff = targetGain < _currentGain ? _attackCoeff : _releaseCoeff;
            _currentGain = coeff * _currentGain + (1f - coeff) * targetGain;

            buffer[offset + i] = sample * _currentGain;
        }

        return read;
    }

    /// <summary>重置</summary>
    public void Reset()
    {
        _currentGain = 1f;
        _rmsEnvelope = 0.001f;
    }

    private static Single Clamp(Single value, Single min, Single max) => value < min ? min : value > max ? max : value;
}
