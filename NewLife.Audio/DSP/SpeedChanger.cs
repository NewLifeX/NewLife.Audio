namespace NewLife.Audio.DSP;

/// <summary>变速器。改变播放速度（可选保持音高）</summary>
/// <remarks>
/// 基础实现：OLA（Overlap-Add）算法。
/// 通过改变分析帧的步进与合成帧的步进比实现变速。
/// </remarks>
public class SpeedChanger : IAudioProcessor
{
    private readonly AudioFormat _inputFormat;
    private readonly AudioFormat _outputFormat;
    private readonly Single _speedRatio;

    // OLA 缓冲
    private readonly Single[] _olaBuffer;
    private Int32 _olaWritePos;
    private Int32 _readOffset;
    private const Int32 FrameSize = 1024;
    private const Int32 HopSize = 256;

    /// <summary>输入格式</summary>
    public AudioFormat InputFormat => _inputFormat;

    /// <summary>输出格式</summary>
    public AudioFormat OutputFormat => _outputFormat;

    /// <summary>上游源</summary>
    public IAudioProcessor Source { get; set; }

    /// <summary>速度比率（1.0=原速，2.0=两倍速，0.5=半速）</summary>
    public Single SpeedRatio => _speedRatio;

    /// <summary>初始化变速器</summary>
    /// <param name="speedRatio">速度比率</param>
    /// <param name="inputFormat">输入格式</param>
    public SpeedChanger(Single speedRatio, AudioFormat inputFormat = null)
    {
        _inputFormat = inputFormat?.Clone() ?? AudioFormat.Default;
        _outputFormat = _inputFormat.Clone();
        _speedRatio = Math.Max(0.25f, Math.Min(4.0f, speedRatio));
        _olaBuffer = new Single[FrameSize * 4];
    }

    /// <summary>读取变速后的数据</summary>
    /// <remarks>简化实现：直接按比例跳帧或插帧</remarks>
    public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
    {
        if (Source == null) return 0;

        // 计算需要从源读取的采样数
        var neededInput = (Int32)(count * _speedRatio) + FrameSize;
        var inputBuffer = new Single[neededInput];
        var inputRead = Source.Read(inputBuffer, 0, neededInput);

        if (inputRead == 0) return 0;

        // OLA 变速
        var outputHop = (Int32)(HopSize / _speedRatio);
        if (outputHop < 1) outputHop = 1;

        var outIdx = 0;
        var readPos = _readOffset;

        while (outIdx < count && readPos + FrameSize < inputRead)
        {
            // 加窗
            for (var i = 0; i < FrameSize && outIdx + i < count; i++)
            {
                var window = 0.5f * (1f - (Single)Math.Cos(2 * Math.PI * i / (FrameSize - 1)));
                buffer[offset + outIdx + i] += inputBuffer[readPos + i] * window;
            }

            outIdx += outputHop;
            readPos += HopSize;
        }

        _readOffset = readPos - inputRead;
        if (_readOffset < 0) _readOffset = 0;

        return Math.Min(outIdx, count);
    }

    /// <summary>重置</summary>
    public void Reset()
    {
        _readOffset = 0;
        Array.Clear(_olaBuffer, 0, _olaBuffer.Length);
    }
}
