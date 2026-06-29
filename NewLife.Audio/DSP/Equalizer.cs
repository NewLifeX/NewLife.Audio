namespace NewLife.Audio.DSP;

/// <summary>多段参数均衡器。串联多个 BiQuad 滤波器实现</summary>
public class Equalizer : IAudioProcessor
{
    private readonly AudioFormat _inputFormat;
    private readonly AudioFormat _outputFormat;
    private readonly List<BiQuadFilter> _filters = [];

    /// <summary>输入格式</summary>
    public AudioFormat InputFormat => _inputFormat;

    /// <summary>输出格式</summary>
    public AudioFormat OutputFormat => _outputFormat;

    /// <summary>上游源</summary>
    public IAudioProcessor Source { get; set; }

    /// <summary>滤波器段列表</summary>
    public IReadOnlyList<BiQuadFilter> Filters => _filters;

    /// <summary>初始化均衡器</summary>
    /// <param name="inputFormat">输入格式</param>
    public Equalizer(AudioFormat inputFormat = null)
    {
        _inputFormat = inputFormat?.Clone() ?? AudioFormat.Default;
        _outputFormat = _inputFormat.Clone();
    }

    /// <summary>添加均衡段</summary>
    /// <param name="type">滤波器类型</param>
    /// <param name="frequency">中心/截止频率（Hz）</param>
    /// <param name="q">品质因数</param>
    /// <param name="gainDB">增益（dB）</param>
    public Equalizer AddBand(BiQuadFilter.FilterType type, Single frequency, Single q = 1.0f, Single gainDB = 0f)
    {
        _filters.Add(new BiQuadFilter(type, frequency, q, gainDB, _inputFormat));
        return this;
    }

    /// <summary>建立经典 10 段图示均衡器（31Hz~16kHz，按倍频程）</summary>
    public static Equalizer Create10BandGraphic(AudioFormat format = null)
    {
        var eq = new Equalizer(format);
        var bands = new[] { 31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };
        foreach (var freq in bands)
            eq.AddBand(BiQuadFilter.FilterType.Peaking, freq, 1.4f, 0f);
        return eq;
    }

    /// <summary>建立经典 3 段音调控制（低音/中音/高音）</summary>
    public static Equalizer CreateToneControl(AudioFormat format = null)
    {
        var eq = new Equalizer(format);
        eq.AddBand(BiQuadFilter.FilterType.LowShelf, 300f, 1.0f, 0f);
        eq.AddBand(BiQuadFilter.FilterType.Peaking, 1000f, 1.0f, 0f);
        eq.AddBand(BiQuadFilter.FilterType.HighShelf, 3000f, 1.0f, 0f);
        return eq;
    }

    /// <summary>读取均衡后的数据</summary>
    public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
    {
        if (Source == null) return 0;
        if (_filters.Count == 0) return Source.Read(buffer, offset, count);

        // 为串联处理创建临时缓冲区
        var temp = new Single[count];
        var read = Source.Read(temp, 0, count);
        if (read == 0) return 0;

        foreach (var filter in _filters)
        {
            for (var i = 0; i < read; i++)
                temp[i] = filter.ProcessSample(temp[i]);
        }

        Array.Copy(temp, 0, buffer, offset, read);
        return read;
    }

    /// <summary>重置所有滤波器段</summary>
    public void Reset()
    {
        foreach (var filter in _filters)
            filter.Reset();
    }
}
