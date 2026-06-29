namespace NewLife.Audio.DSP;

/// <summary>音频信号处理管线。串联多个处理器形成处理链</summary>
/// <remarks>
/// 自身实现 <see cref="IAudioProcessor"/>，可作为其他管线的上游源。
/// 内部维护处理器列表，Read 从最后一个处理器拉取数据。
/// </remarks>
public class AudioPipeline : IAudioProcessor
{
    private readonly List<IAudioProcessor> _processors = [];

    /// <summary>管线中的处理器列表</summary>
    public IReadOnlyList<IAudioProcessor> Processors => _processors;

    /// <summary>输入格式（第一个处理器的输入格式）</summary>
    public AudioFormat InputFormat => _processors.Count > 0 ? _processors[0].InputFormat : AudioFormat.Default;

    /// <summary>输出格式（最后一个处理器的输出格式）</summary>
    public AudioFormat OutputFormat => _processors.Count > 0 ? _processors[^1].OutputFormat : AudioFormat.Default;

    /// <summary>上游源</summary>
    public IAudioProcessor Source
    {
        get => _processors.Count > 0 ? _processors[0].Source : null;
        set
        {
            if (_processors.Count > 0)
                _processors[0].Source = value;
        }
    }

    /// <summary>添加处理器到管线末尾。自动设置前一个处理器的输出为当前处理器的输入</summary>
    /// <param name="processor">要添加的处理器</param>
    public AudioPipeline AddProcessor(IAudioProcessor processor)
    {
        if (processor == null) throw new ArgumentNullException(nameof(processor));

        if (_processors.Count > 0)
            processor.Source = _processors[^1];

        _processors.Add(processor);
        return this;
    }

    /// <summary>移除指定处理器</summary>
    /// <param name="processor">要移除的处理器</param>
    /// <returns>是否成功移除</returns>
    public Boolean RemoveProcessor(IAudioProcessor processor)
    {
        var index = _processors.IndexOf(processor);
        if (index < 0) return false;

        _processors.RemoveAt(index);

        // 重新链接
        if (index > 0 && index < _processors.Count)
            _processors[index].Source = _processors[index - 1];

        return true;
    }

    /// <summary>从管线最后一个处理器拉取数据</summary>
    /// <inheritdoc />
    public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
    {
        if (_processors.Count == 0) return 0;
        return _processors[^1].Read(buffer, offset, count);
    }

    /// <summary>重置所有处理器状态</summary>
    public void Reset()
    {
        foreach (var processor in _processors)
            processor.Reset();
    }
}
