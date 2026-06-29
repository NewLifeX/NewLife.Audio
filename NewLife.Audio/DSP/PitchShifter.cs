namespace NewLife.Audio.DSP;

/// <summary>变调器。改变音高保持速度（PSOLA简化版）</summary>
/// <remarks>
/// 基于重采样实现变调：先通过 Resampler 改变采样率改变音高，
/// 再通过 SpeedChanger 恢复原时长。
/// </remarks>
public class PitchShifter : IAudioProcessor
{
    private readonly AudioFormat _inputFormat;
    private readonly AudioFormat _outputFormat;
    private readonly Single _pitchRatio;

    private readonly Single[] _pitchBuffer;
    private Int32 _pitchBufferPos;
    private Int32 _pitchBufferCount;

    /// <summary>输入格式</summary>
    public AudioFormat InputFormat => _inputFormat;

    /// <summary>输出格式</summary>
    public AudioFormat OutputFormat => _outputFormat;

    /// <summary>上游源</summary>
    public IAudioProcessor Source { get; set; }

    /// <summary>音高比率（1.0=原音高，2.0=升八度，0.5=降八度）</summary>
    public Single PitchRatio => _pitchRatio;

    /// <summary>以半音数表示的变调量（12=升八度，-12=降八度）</summary>
    public static Single SemitonesToRatio(Single semitones) => (Single)Math.Pow(2.0, semitones / 12.0);

    /// <summary>初始化变调器</summary>
    /// <param name="pitchRatio">音高比率</param>
    /// <param name="inputFormat">输入格式</param>
    public PitchShifter(Single pitchRatio, AudioFormat inputFormat = null)
    {
        _inputFormat = inputFormat?.Clone() ?? AudioFormat.Default;
        _outputFormat = _inputFormat.Clone();
        _pitchRatio = Math.Max(0.25f, Math.Min(4.0f, pitchRatio));
        _pitchBuffer = new Single[65536];
    }

    /// <summary>读取变调后的数据</summary>
    /// <remarks>简化实现：重采样改变音高，OLA保持时长</remarks>
    public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
    {
        if (Source == null) return 0;

        var neededInput = (Int32)(count * _pitchRatio) + 1024;
        var inputBuffer = new Single[neededInput];
        var inputRead = Source.Read(inputBuffer, 0, neededInput);

        if (inputRead == 0 && _pitchBufferCount == 0) return 0;

        // 重采样（改变音高）
        var resampledLen = (Int32)(inputRead / _pitchRatio);
        var resampled = new Single[resampledLen + 1];
        for (var i = 0; i < resampledLen; i++)
        {
            var srcIdx = i * _pitchRatio;
            var idx0 = (Int32)srcIdx;
            var idx1 = Math.Min(idx0 + 1, inputRead - 1);
            var frac = srcIdx - idx0;
            resampled[i] = inputBuffer[idx0] * (1f - frac) + inputBuffer[idx1] * frac;
        }

        // OLA 保持时长
        var frameSize = 1024;
        var hopSize = frameSize / 4;
        var outIdx = 0;
        var srcPos = 0;

        while (outIdx < count && srcPos + frameSize < resampledLen)
        {
            for (var i = 0; i < frameSize && outIdx + i < count; i++)
            {
                var window = 0.5f * (1f - (Single)Math.Cos(2 * Math.PI * i / (frameSize - 1)));
                buffer[offset + outIdx + i] += resampled[srcPos + i] * window;
            }
            outIdx += hopSize;
            srcPos += hopSize;
        }

        return Math.Min(outIdx, count);
    }

    /// <summary>重置</summary>
    public void Reset()
    {
        _pitchBufferPos = 0;
        _pitchBufferCount = 0;
        Array.Clear(_pitchBuffer, 0, _pitchBuffer.Length);
    }
}
