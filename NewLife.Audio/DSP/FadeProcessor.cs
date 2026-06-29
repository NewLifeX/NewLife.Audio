namespace NewLife.Audio.DSP;

/// <summary>淡入淡出处理器。在线性/对数/余弦曲线之间选择</summary>
public class FadeProcessor : IAudioProcessor
{
    private readonly AudioFormat _inputFormat;
    private readonly AudioFormat _outputFormat;
    private Int32 _totalSamples;
    private Int32 _processedSamples;

    /// <summary>淡入淡出类型</summary>
    public enum FadeType { FadeIn, FadeOut }

    /// <summary>曲线类型</summary>
    public enum CurveType { Linear, Logarithmic, Cosine }

    private readonly FadeType _fadeType;
    private readonly CurveType _curve;
    private readonly Single _durationSeconds;

    /// <summary>输入格式</summary>
    public AudioFormat InputFormat => _inputFormat;

    /// <summary>输出格式</summary>
    public AudioFormat OutputFormat => _outputFormat;

    /// <summary>上游源</summary>
    public IAudioProcessor Source { get; set; }

    /// <summary>是否已完成淡变</summary>
    public Boolean IsComplete => _processedSamples >= _totalSamples;

    /// <summary>初始化淡入淡出处理器</summary>
    /// <param name="fadeType">淡入或淡出</param>
    /// <param name="durationSeconds">持续时间（秒）</param>
    /// <param name="curve">曲线类型，默认线性</param>
    /// <param name="inputFormat">输入格式</param>
    public FadeProcessor(FadeType fadeType, Single durationSeconds, CurveType curve = CurveType.Linear, AudioFormat inputFormat = null)
    {
        _inputFormat = inputFormat?.Clone() ?? AudioFormat.Default;
        _outputFormat = _inputFormat.Clone();
        _fadeType = fadeType;
        _durationSeconds = durationSeconds;
        _curve = curve;
        _totalSamples = (Int32)(_inputFormat.SampleRate * durationSeconds);
        _processedSamples = 0;
    }

    /// <summary>读取淡变后的数据</summary>
    public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
    {
        if (Source == null) return 0;

        var read = Source.Read(buffer, offset, count);
        for (var i = 0; i < read; i++)
        {
            if (_processedSamples >= _totalSamples)
            {
                buffer[offset + i] = _fadeType == FadeType.FadeIn ? buffer[offset + i] : 0;
                continue;
            }

            var progress = (Single)_processedSamples / _totalSamples;
            var gain = ComputeGain(progress);

            buffer[offset + i] *= gain;
            _processedSamples++;
        }

        return read;
    }

    /// <summary>重置</summary>
    public void Reset() => _processedSamples = 0;

    private Single ComputeGain(Single progress)
    {
        var rawGain = _curve switch
        {
            CurveType.Logarithmic => (Single)(Math.Log10(1 + 9 * progress)),
            CurveType.Cosine => (Single)((1 - Math.Cos(Math.PI * progress)) / 2.0),
            _ => progress, // Linear
        };

        return _fadeType == FadeType.FadeIn ? rawGain : 1f - rawGain;
    }
}
