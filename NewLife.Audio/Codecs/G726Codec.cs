using NewLife.Data;

namespace NewLife.Audio.Codecs;

/// <summary>G.726 ADPCM 语音编解码器（16/24/32/40 kbps）</summary>
/// <remarks>
/// ITU-T G.726 标准：自适应差分脉冲编码调制。
/// 支持 4 种比特率：16kbps(2bit)、24kbps(3bit)、32kbps(4bit)、40kbps(5bit)。
/// 相比 G.711 提供更高压缩比，相比 IMA ADPCM 提供更好的语音质量。
/// </remarks>
public class G726Codec : IAudioCodec, ICodecInfo
{
    /// <summary>编码比特率（2/3/4/5 bits per sample）</summary>
    private readonly Int32 _bitsPerSample;

    /// <summary>量化级数</summary>
    private readonly Int32 _levels;

    /// <summary>步长表索引</summary>
    private Int32 _stepIndex;

    /// <summary>预测值</summary>
    private Int32 _predictedValue;

    // 步长表
    private static readonly Int32[] StepSizeTable =
    [
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17,
            19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
            50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
            130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
            337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
            876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
            2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358,
            5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
            15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    ];

    private static readonly Int32[] StepAdjustTable = [0, 0, 0, 1, 2, 4, 8, 16];

    /// <summary>编解码器名称</summary>
    public String Name => $"G.726-{_bitsPerSample * 8}k";

    /// <summary>版本号</summary>
    public String Version => "1.0";

    /// <summary>支持的编码类型</summary>
    public IReadOnlyCollection<AVTypes> SupportedTypes { get; } = [AVTypes.G726];

    /// <summary>有状态编解码器（每个流独立状态）</summary>
    public Boolean IsStateful => true;

    /// <summary>初始化 G.726 编解码器</summary>
    /// <param name="bitsPerSample">每样本比特数：2(16kbps)、3(24kbps)、4(32kbps)、5(40kbps)，默认4</param>
    public G726Codec(Int32 bitsPerSample = 4)
    {
        if (bitsPerSample < 2 || bitsPerSample > 5)
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "比特数必须在 2~5 之间");

        _bitsPerSample = bitsPerSample;
        _levels = 1 << bitsPerSample;
        _stepIndex = 0;
        _predictedValue = 0;
    }

    /// <summary>G.726音频数据转PCM</summary>
    /// <param name="audio">G.726 编码数据</param>
    /// <param name="option"></param>
    /// <returns>16-bit PCM</returns>
    public Packet ToPcm(Packet audio, Object option)
    {
        var audioData = audio.ReadBytes();
        var pcm = new Byte[audioData.Length * 2 * 8 / _bitsPerSample];
        var outIdx = 0;

        Int32 buffer = 0;
        var bitsInBuffer = 0;

        for (var i = 0; i < audioData.Length; i++)
        {
            buffer = (buffer << 8) | audioData[i];
            bitsInBuffer += 8;

            while (bitsInBuffer >= _bitsPerSample && outIdx < pcm.Length - 1)
            {
                bitsInBuffer -= _bitsPerSample;
                var code = (buffer >> bitsInBuffer) & (_levels - 1);

                var sample = DecodeSample(code);
                pcm[outIdx++] = (Byte)(sample & 0xFF);
                pcm[outIdx++] = (Byte)((sample >> 8) & 0xFF);
            }
        }

        return pcm;
    }

    /// <summary>PCM转G.726音频数据</summary>
    /// <param name="pcm">16-bit PCM</param>
    /// <param name="option"></param>
    /// <returns>G.726 编码数据</returns>
    public Packet FromPcm(Packet pcm, Object option)
    {
        var pcmData = pcm.ReadBytes();
        var sampleCount = pcmData.Length / 2;
        var outputBits = sampleCount * _bitsPerSample;
        var output = new Byte[(outputBits + 7) / 8];

        var outIdx = 0;
        var buffer = 0;
        var bitsInBuffer = 0;

        for (var i = 0; i < pcmData.Length - 1; i += 2)
        {
            var sample = (Int16)(pcmData[i + 1] << 8 | pcmData[i]);
            var code = EncodeSample(sample);

            buffer = (buffer << _bitsPerSample) | code;
            bitsInBuffer += _bitsPerSample;

            if (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                output[outIdx++] = (Byte)(buffer >> bitsInBuffer);
                buffer &= (1 << bitsInBuffer) - 1;
            }
        }

        // 填充剩余位（不足一字节则右移）
        if (bitsInBuffer > 0)
        {
            buffer <<= 8 - bitsInBuffer;
            output[outIdx++] = (Byte)buffer;
        }

        return output;
    }

    private Int16 DecodeSample(Int32 code)
    {
        var ss = StepSizeTable[_stepIndex];

        // 量化重建（中升量化器）
        var diff = 0;
        var halfLevels = _levels / 2;
        if (code < halfLevels)
        {
            diff = -ss * (halfLevels - 1 - code) - ss / 2;
        }
        else
        {
            diff = ss * (code - halfLevels) + ss / 2;
        }

        _predictedValue += diff / 2; // 折半增益防止震荡
        if (_predictedValue > 32767) _predictedValue = 32767;
        if (_predictedValue < -32768) _predictedValue = -32768;

        UpdateStepIndex(code);
        return (Int16)_predictedValue;
    }

    private Int32 EncodeSample(Int16 sample)
    {
        var ss = StepSizeTable[_stepIndex];
        var diff = sample - _predictedValue;

        // 量化
        var halfLevels = _levels / 2;
        var absDiff = Math.Abs(diff);
        var code = Math.Min(absDiff / ss, halfLevels - 1);

        if (diff < 0)
            code = halfLevels - 1 - code;
        else
            code = halfLevels + code;

        if (code < 0) code = 0;
        if (code >= _levels) code = _levels - 1;

        // 本地解码（重建）
        var reconstructedDiff = 0;
        if (code < halfLevels)
            reconstructedDiff = -ss * (halfLevels - 1 - code) - ss / 2;
        else
            reconstructedDiff = ss * (code - halfLevels) + ss / 2;

        _predictedValue += reconstructedDiff / 2;
        if (_predictedValue > 32767) _predictedValue = 32767;
        if (_predictedValue < -32768) _predictedValue = -32768;

        UpdateStepIndex(code);
        return code;
    }

    private void UpdateStepIndex(Int32 code)
    {
        var halfLevels = _levels / 2;
        var adjustIndex = code < halfLevels ? halfLevels - 1 - code : code - halfLevels;
        _stepIndex += StepAdjustTable[Math.Min(adjustIndex, StepAdjustTable.Length - 1)];

        if (_stepIndex < 0) _stepIndex = 0;
        if (_stepIndex >= StepSizeTable.Length) _stepIndex = StepSizeTable.Length - 1;
    }
}
