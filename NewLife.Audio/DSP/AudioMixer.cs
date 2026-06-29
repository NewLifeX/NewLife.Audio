namespace NewLife.Audio.DSP;

/// <summary>音频混音器。多输入源混合为一个输出</summary>
/// <remarks>
/// 所有输入源采样率和声道数必须一致。
/// 32-bit 浮点累加后除以输入源数量实现归一化（防止削波），也可配置为手动增益。
/// </remarks>
public class AudioMixer : IAudioProcessor
{
    private readonly AudioFormat _inputFormat;
    private readonly AudioFormat _outputFormat;
    private readonly List<IAudioProcessor> _inputs = [];
    private readonly List<Single> _inputGains = [];

    /// <summary>输入格式</summary>
    public AudioFormat InputFormat => _inputFormat;

    /// <summary>输出格式</summary>
    public AudioFormat OutputFormat => _outputFormat;

    /// <summary>上游源（混音器不使用此属性，由 AddInputStream 管理）</summary>
    public IAudioProcessor Source { get; set; }

    /// <summary>输入源列表</summary>
    public IReadOnlyList<IAudioProcessor> Inputs => _inputs;

    /// <summary>是否自动归一化（除以输入源数量）</summary>
    public Boolean AutoNormalize { get; set; } = true;

    /// <summary>初始化混音器</summary>
    /// <param name="inputFormat">输入格式</param>
    public AudioMixer(AudioFormat inputFormat = null)
    {
        _inputFormat = inputFormat?.Clone() ?? AudioFormat.Default;
        _outputFormat = _inputFormat.Clone();
    }

    /// <summary>添加输入源</summary>
    /// <param name="source">音频处理器源</param>
    /// <param name="gain">增益系数（默认 1.0）</param>
    public AudioMixer AddInputStream(IAudioProcessor source, Single gain = 1.0f)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        _inputs.Add(source);
        _inputGains.Add(gain);
        return this;
    }

    /// <summary>移除输入源</summary>
    /// <param name="source">要移除的源</param>
    public Boolean RemoveInputStream(IAudioProcessor source)
    {
        var idx = _inputs.IndexOf(source);
        if (idx < 0) return false;
        _inputs.RemoveAt(idx);
        _inputGains.RemoveAt(idx);
        return true;
    }

    /// <summary>读取混音后的数据</summary>
    public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
    {
        if (_inputs.Count == 0) return 0;

        Array.Clear(buffer, offset, count);

        var maxRead = 0;
        var tempBuffer = new Single[count];

        for (var idx = 0; idx < _inputs.Count; idx++)
        {
            var source = _inputs[idx];
            var gain = _inputGains[idx];

            Array.Clear(tempBuffer, 0, count);
            var read = source.Read(tempBuffer, 0, count);
            if (read > maxRead) maxRead = read;

            for (var i = 0; i < read; i++)
                buffer[offset + i] += tempBuffer[i] * gain;
        }

        // 归一化
        if (AutoNormalize && _inputs.Count > 0)
        {
            var norm = 1.0f / _inputs.Count;
            for (var i = 0; i < maxRead; i++)
                buffer[offset + i] *= norm;
        }

        return maxRead;
    }

    /// <summary>重置</summary>
    public void Reset()
    {
        foreach (var input in _inputs)
            input.Reset();
    }
}
