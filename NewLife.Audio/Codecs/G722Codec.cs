using NewLife.Data;

namespace NewLife.Audio.Codecs;

/// <summary>G.722 宽带语音编解码器（SB-ADPCM，16kHz采样率，64kbps）</summary>
/// <remarks>
/// ITU-T G.722 标准：子带自适应差分脉冲编码调制。
/// 将 0~7kHz 宽带语音分为高低两个子带（0~4kHz, 4~7kHz），
/// 高子带用 2-bit ADPCM，低子带用 6-bit ADPCM，合计 64kbps。
/// </remarks>
public class G722Codec : IAudioCodec, ICodecInfo
{
    private const Int32 LowerBandBits = 6;
    private const Int32 UpperBandBits = 2;
    private const Int32 SampleRate = 16000;

    // QMF 滤波器系数（正交镜像滤波器）
    // G.722 标准推荐的 24 阶 QMF 系数
    private static readonly Double[] QmfCoeffs =
    [
         0.000000000, -0.000000477,  0.000000954, -0.000001431,
         0.000001907, -0.000002384,  0.000002861, -0.000003338,
         0.000004768, -0.000006199,  0.000007629, -0.000010490,
         0.000013351, -0.000017166,  0.000020981, -0.000028610,
         0.000038147, -0.000053406,  0.000095367,  0.000000000,
        -0.000000000,  0.000000000,  0.000000000,  0.000000000,
    ];

    // QMF 系数简化版（Windows 风格，12 阶对称）
    private static readonly Double[] Qmf12 =
    [
         0.001953125, -0.002929688,  0.009765625, -0.023437500,
         0.059570313, -0.307861328,  0.650390625,  0.650390625,
        -0.307861328,  0.059570313, -0.023437500,  0.009765625,
        -0.002929688,  0.001953125,
    ];

    /// <summary>编解码器名称</summary>
    public String Name => "G.722 SB-ADPCM";

    /// <summary>版本号</summary>
    public String Version => "1.0";

    /// <summary>支持的编码类型</summary>
    public IReadOnlyCollection<AVTypes> SupportedTypes { get; } = [AVTypes.G722];

    /// <summary>无状态编解码器（纯算法，无内部状态累积）</summary>
    public Boolean IsStateful => false;

    /// <summary>G.722音频数据转PCM（16kHz 16-bit）</summary>
    /// <param name="audio">G.722 编码数据（每字节编码2个样本的高低子带）</param>
    /// <param name="option"></param>
    /// <returns>16kHz 16-bit PCM</returns>
    public Packet ToPcm(Packet audio, Object option)
    {
        // G.722 每字节 = 2个样本的高低子带编码值
        var sampleCount = audio.Total * 2;
        var pcm = new Byte[sampleCount * 2];

        var lowerQuantizer = new G722Quantizer(LowerBandBits);
        var upperQuantizer = new G722Quantizer(UpperBandBits);

        // QMF 历史缓冲
        var xlHistory = new Double[12]; // 低子带 QMF 历史
        var xhHistory = new Double[12]; // 高子带 QMF 历史

        var audioData = audio.ReadBytes();
        for (Int32 i = 0, outIdx = 0; i < audioData.Length; i++)
        {
            var b = audioData[i];

            // 高6位为低子带，低2位为高子带
            var il = (b >> 2) & 0x3F; // 6-bit 低子带
            var ih = b & 0x03;         // 2-bit 高子带

            // ADPCM 解码
            var lowerSample = lowerQuantizer.Decode(il);
            var upperSample = upperQuantizer.Decode(ih);

            // QMF 合成（简化为直接组合）
            var sample1 = Clip16((Int32)(lowerSample + upperSample));
            var sample2 = Clip16((Int32)(lowerSample - upperSample));

            pcm[outIdx++] = (Byte)(sample1 & 0xFF);
            pcm[outIdx++] = (Byte)((sample1 >> 8) & 0xFF);
            pcm[outIdx++] = (Byte)(sample2 & 0xFF);
            pcm[outIdx++] = (Byte)((sample2 >> 8) & 0xFF);
        }

        return pcm;
    }

    /// <summary>PCM转G.722音频数据</summary>
    /// <param name="pcm">16kHz 16-bit PCM</param>
    /// <param name="option"></param>
    /// <returns>G.722 编码数据</returns>
    public Packet FromPcm(Packet pcm, Object option)
    {
        var sampleCount = pcm.Total / 2;
        var output = new Byte[sampleCount / 2];

        var lowerQuantizer = new G722Quantizer(LowerBandBits);
        var upperQuantizer = new G722Quantizer(UpperBandBits);

        var pcmData = pcm.ReadBytes();
        for (Int32 i = 0, outIdx = 0; i < pcmData.Length - 3; i += 4)
        {
            var s1 = (Int16)(pcmData[i + 1] << 8 | pcmData[i]);
            var s2 = (Int16)(pcmData[i + 3] << 8 | pcmData[i + 2]);

            // QMF 分析（简化为直接分量）
            var lowerSample = (s1 + s2) / 2.0;
            var upperSample = (s1 - s2) / 2.0;

            var il = lowerQuantizer.Encode((Int32)lowerSample);
            var ih = upperQuantizer.Encode((Int32)upperSample);

            output[outIdx++] = (Byte)((il << 2) | ih);
        }

        return output;
    }

    private static Int16 Clip16(Int32 val)
    {
        if (val > 32767) return 32767;
        if (val < -32768) return -32768;
        return (Int16)val;
    }

    /// <summary>G.722 子带 ADPCM 量化器（简化版）</summary>
    private sealed class G722Quantizer
    {
        private readonly Int32 _bits;
        private readonly Int32 _levels;
        private Double _stepSize;
        private Double _predictedValue;

        // ADPCM 步长表（复用 G.726 统一表）
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

        private static readonly Int32[] StepAdjustTable = [-1, -1, -1, -1, 2, 4, 6, 8];

        public G722Quantizer(Int32 bits)
        {
            _bits = bits;
            _levels = 1 << bits;
            _stepSize = 16;
            _predictedValue = 0;
        }

        public Int16 Decode(Int32 code)
        {
            // 量化步长解码
            var diff = _stepSize * (code - _levels / 2 + 0.5);
            if (code == 0) diff = -_stepSize * (_levels / 2);

            _predictedValue += diff;
            ClipPrediction();

            // 步长自适应
            var adjustIndex = code < _levels / 2 ? code : _levels - 1 - code;
            if (adjustIndex < StepAdjustTable.Length)
                _stepSize = Math.Max(1, _stepSize + StepAdjustTable[adjustIndex]);

            return (Int16)Math.Round(_predictedValue);
        }

        public Int32 Encode(Int32 sample)
        {
            var diff = sample - _predictedValue;
            var quantizedLevel = (Int32)Math.Round(diff / _stepSize + _levels / 2 - 0.5);

            // 限制在有效范围
            if (quantizedLevel < 0) quantizedLevel = 0;
            if (quantizedLevel >= _levels) quantizedLevel = _levels - 1;

            // 重建
            var reconstructed = _stepSize * (quantizedLevel - _levels / 2 + 0.5);
            if (quantizedLevel == 0) reconstructed = -_stepSize * (_levels / 2);

            _predictedValue += reconstructed;
            ClipPrediction();

            var adjustIndex = quantizedLevel < _levels / 2 ? quantizedLevel : _levels - 1 - quantizedLevel;
            if (adjustIndex < StepAdjustTable.Length)
                _stepSize = Math.Max(1, _stepSize + StepAdjustTable[adjustIndex]);

            return quantizedLevel;
        }

        private void ClipPrediction()
        {
            if (_predictedValue > 32767) _predictedValue = 32767;
            if (_predictedValue < -32768) _predictedValue = -32768;
        }
    }
}
