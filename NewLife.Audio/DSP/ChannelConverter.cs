namespace NewLife.Audio.DSP;

/// <summary>声道转换器。支持单声道↔立体声↔多声道转换</summary>
/// <remarks>
/// 作为管线处理器时按「输出声道数」重新编排采样：单声道复制到各声道，
/// 多声道下混为单声道取平均，其余情况按声道索引取模映射（缺省补零）。
/// 内部以 ±1.0 浮点交错缓冲读写，不改变采样率与位深。
/// </remarks>
public class ChannelConverter : IAudioProcessor
{
    private readonly AudioFormat _inputFormat;
    private readonly AudioFormat _outputFormat;
    private Single[] _input = [];

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
        if (targetChannels <= 0) throw new ArgumentOutOfRangeException(nameof(targetChannels));

        _inputFormat = inputFormat?.Clone() ?? AudioFormat.Default;
        _outputFormat = _inputFormat.Clone();
        _outputFormat.Channels = targetChannels;
    }

    /// <summary>读取转换后的采样数据</summary>
    /// <param name="buffer">输出缓冲（交错）</param>
    /// <param name="offset">输出起始位置</param>
    /// <param name="count">期望的采样数（不是帧数）</param>
    /// <returns>实际写入的采样数</returns>
    public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
    {
        if (Source == null) return 0;

        var inChannels = Math.Max(1, _inputFormat.Channels);
        var outChannels = Math.Max(1, _outputFormat.Channels);
        if (inChannels == outChannels) return Source.Read(buffer, offset, count);

        var frames = count / outChannels;
        if (frames <= 0) return 0;

        var needed = frames * inChannels;
        if (_input.Length < needed) _input = new Single[needed];
        var read = Source.Read(_input, 0, needed);
        if (read <= 0) return 0;

        var readFrames = read / inChannels;
        var written = Convert(_input, inChannels, buffer, offset, outChannels, readFrames);

        return written;
    }

    /// <summary>重置</summary>
    public void Reset()
    {
        _input = [];
        Source?.Reset();
    }

    /// <summary>按声道数重排采样</summary>
    /// <param name="input">输入交错采样</param>
    /// <param name="inputChannels">输入声道数</param>
    /// <param name="output">输出交错采样</param>
    /// <param name="offset">输出起始位置</param>
    /// <param name="outputChannels">输出声道数</param>
    /// <param name="frames">帧数</param>
    /// <returns>写入的采样数</returns>
    public static Int32 Convert(Single[] input, Int32 inputChannels, Single[] output, Int32 offset, Int32 outputChannels, Int32 frames)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (inputChannels <= 0) throw new ArgumentOutOfRangeException(nameof(inputChannels));
        if (outputChannels <= 0) throw new ArgumentOutOfRangeException(nameof(outputChannels));

        for (var i = 0; i < frames; i++)
        {
            var src = i * inputChannels;

            if (outputChannels == 1)
            {
                // 下混：所有声道取平均，避免削波
                var sum = 0f;
                for (var ch = 0; ch < inputChannels; ch++) sum += input[src + ch];
                output[offset + i] = sum / inputChannels;
                continue;
            }

            if (inputChannels == 1)
            {
                var v = input[src];
                for (var ch = 0; ch < outputChannels; ch++) output[offset + i * outputChannels + ch] = v;
                continue;
            }

            // 其余情况：按索引映射，缺省补零
            for (var ch = 0; ch < outputChannels; ch++)
            {
                output[offset + i * outputChannels + ch] = ch < inputChannels ? input[src + ch] : 0f;
            }
        }

        return frames * outputChannels;
    }

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
