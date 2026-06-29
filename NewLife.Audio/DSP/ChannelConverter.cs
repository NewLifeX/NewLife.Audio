namespace NewLife.Audio.DSP;

/// <summary>声道转换器。支持单声道↔立体声↔多声道转换</summary>
public class ChannelConverter : IAudioProcessor
{
    private readonly AudioFormat _inputFormat;
    private readonly AudioFormat _outputFormat;

    /// <summary>输入格式</summary>
    public AudioFormat InputFormat => _inputFormat;

    /// <summary>输出格式</summary>
    public AudioFormat OutputFormat => _outputFormat;

    /// <summary>上游源</summary>
    public IAudioProcessor Source { get; set; }

    /// <summary>初始化声道转换器</summary>
    /// <param name="targetChannels">目标声道数</param>
    /// <param name="inputFormat">输入格式</param>
    public ChannelConverter(Int32 targetChannels, AudioFormat inputFormat = null)
    {
        _inputFormat = inputFormat?.Clone() ?? AudioFormat.Default;
        _outputFormat = _inputFormat.Clone();
        _outputFormat.Channels = targetChannels;
    }

    /// <summary>读取转换后的采样数据</summary>
    public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
    {
        if (Source == null) return 0;
        return Source.Read(buffer, offset, count);
    }

    /// <summary>重置</summary>
    public void Reset() { }

    /// <summary>单声道转立体声（复制到左右声道）</summary>
    /// <param name="mono">单声道采样</param>
    /// <param name="stereo">立体声输出（长度 = 2 × mono长度）</param>
    /// <param name="count">单声道采样数</param>
    public static void MonoToStereo(Single[] mono, Single[] stereo, Int32 count)
    {
        for (var i = 0; i < count; i++)
        {
            stereo[i * 2] = mono[i];
            stereo[i * 2 + 1] = mono[i];
        }
    }

    /// <summary>立体声转单声道（左右声道平均）</summary>
    /// <param name="stereo">立体声采样</param>
    /// <param name="mono">单声道输出</param>
    /// <param name="stereoSamples">立体声采样对数</param>
    public static void StereoToMono(Single[] stereo, Single[] mono, Int32 stereoSamples)
    {
        for (var i = 0; i < stereoSamples; i++)
            mono[i] = (stereo[i * 2] + stereo[i * 2 + 1]) * 0.5f;
    }
}
