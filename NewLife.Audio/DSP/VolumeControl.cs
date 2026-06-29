namespace NewLife.Audio.DSP;

/// <summary>音量控制器。支持线性/对数增益、静音检测、峰值限幅</summary>
public class VolumeControl : IAudioProcessor
{
    private readonly AudioFormat _inputFormat;
    private readonly AudioFormat _outputFormat;
    private Single _gain;
    private Single _peakLevel;

    /// <summary>输入格式</summary>
    public AudioFormat InputFormat => _inputFormat;

    /// <summary>输出格式</summary>
    public AudioFormat OutputFormat => _outputFormat;

    /// <summary>上游源</summary>
    public IAudioProcessor Source { get; set; }

    /// <summary>线性增益系数（1.0 = 不变，0.5 = -6dB，2.0 = +6dB）</summary>
    public Single Gain
    {
        get => _gain;
        set => _gain = Math.Max(0, value);
    }

    /// <summary>以分贝表示的增益</summary>
    public Single GainDB
    {
        get => 20f * (Single)Math.Log10(Math.Max(_gain, 0.0001f));
        set => _gain = (Single)Math.Pow(10, value / 20f);
    }

    /// <summary>峰值限幅阈值（1.0 = 0dBFS，0dBFS以上限幅）</summary>
    public Single PeakLimit { get; set; } = 1.0f;

    /// <summary>上次读取的峰值电平</summary>
    public Single PeakLevel => _peakLevel;

    /// <summary>是否启用静音</summary>
    public Boolean Muted { get; set; }

    /// <summary>初始化音量控制器</summary>
    /// <param name="gain">线性增益，默认 1.0</param>
    /// <param name="inputFormat">输入格式</param>
    public VolumeControl(Single gain = 1.0f, AudioFormat inputFormat = null)
    {
        _inputFormat = inputFormat?.Clone() ?? AudioFormat.Default;
        _outputFormat = _inputFormat.Clone();
        _gain = gain;
    }

    /// <summary>读取处理后的采样数据</summary>
    public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
    {
        if (Source == null) return 0;

        var read = Source.Read(buffer, offset, count);

        if (Muted)
        {
            Array.Clear(buffer, offset, read);
            _peakLevel = 0;
            return read;
        }

        _peakLevel = 0;
        for (var i = 0; i < read; i++)
        {
            var idx = offset + i;
            var sample = buffer[idx] * _gain;

            // 峰值限幅
            if (sample > PeakLimit) sample = PeakLimit;
            else if (sample < -PeakLimit) sample = -PeakLimit;

            buffer[idx] = sample;

            var abs = Math.Abs(sample);
            if (abs > _peakLevel) _peakLevel = abs;
        }

        return read;
    }

    /// <summary>重置</summary>
    public void Reset()
    {
        _peakLevel = 0;
    }
}
