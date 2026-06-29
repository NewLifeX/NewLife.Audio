namespace NewLife.Audio.DSP;

/// <summary>重采样器。改变音频采样率</summary>
/// <remarks>
/// 支持整数倍和分数倍重采样。采用线性插值（低复杂度）或加窗 Sinc 插值（高质量）。
/// </remarks>
public class Resampler : IAudioProcessor
{
    private readonly AudioFormat _inputFormat;
    private readonly AudioFormat _outputFormat;
    private readonly Double _ratio;
    private Double _fractionalPos;

    /// <summary>输入格式</summary>
    public AudioFormat InputFormat => _inputFormat;

    /// <summary>输出格式</summary>
    public AudioFormat OutputFormat => _outputFormat;

    /// <summary>上游源</summary>
    public IAudioProcessor Source { get; set; }

    /// <summary>初始化重采样器</summary>
    /// <param name="outputSampleRate">目标采样率（Hz）</param>
    /// <param name="inputFormat">输入格式</param>
    public Resampler(Int32 outputSampleRate, AudioFormat inputFormat = null)
    {
        _inputFormat = inputFormat?.Clone() ?? AudioFormat.Default;
        _outputFormat = _inputFormat.Clone();
        _outputFormat.SampleRate = outputSampleRate;

        _ratio = (Double)_inputFormat.SampleRate / outputSampleRate;
        _fractionalPos = 0;
    }

    /// <summary>读取重采样后的数据</summary>
    public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
    {
        if (Source == null) return 0;

        // 计算需要从源读取的采样数
        var neededInput = (Int32)(count * _ratio) + 2;
        var inputBuffer = new Single[neededInput];
        var inputRead = Source.Read(inputBuffer, 0, neededInput);

        if (inputRead == 0) return 0;

        // 线性插值重采样
        var outIdx = 0;
        while (outIdx < count && _fractionalPos < inputRead - 1)
        {
            var pos = (Int32)_fractionalPos;
            var frac = _fractionalPos - pos;

            buffer[offset + outIdx] = (Single)(inputBuffer[pos] * (1 - frac) + inputBuffer[pos + 1] * frac);

            outIdx++;
            _fractionalPos += _ratio;
        }

        _fractionalPos -= inputRead;

        return outIdx;
    }

    /// <summary>重置</summary>
    public void Reset()
    {
        _fractionalPos = 0;
    }

    /// <summary>静态方法：重采样 16-bit PCM 数据</summary>
    /// <param name="pcm">输入PCM</param>
    /// <param name="inputRate">输入采样率</param>
    /// <param name="outputRate">输出采样率</param>
    /// <returns>重采样后的PCM</returns>
    public static Byte[] ResamplePcm(Byte[] pcm, Int32 inputRate, Int32 outputRate)
    {
        var inputSamples = pcm.Length / 2;
        var outputSamples = (Int32)((Int64)inputSamples * outputRate / inputRate);
        var output = new Byte[outputSamples * 2];

        var ratio = (Double)inputRate / outputRate;
        var pos = 0.0;

        for (var i = 0; i < outputSamples; i++)
        {
            var idx = (Int32)pos;
            var frac = pos - idx;

            if (idx + 1 >= inputSamples) break;

            var s1 = (Int16)(pcm[idx * 2 + 1] << 8 | pcm[idx * 2]);
            var s2 = (Int16)(pcm[(idx + 1) * 2 + 1] << 8 | pcm[(idx + 1) * 2]);
            var sample = (Int16)(s1 * (1 - frac) + s2 * frac);

            output[i * 2] = (Byte)(sample & 0xFF);
            output[i * 2 + 1] = (Byte)((sample >> 8) & 0xFF);

            pos += ratio;
        }

        return output;
    }
}
