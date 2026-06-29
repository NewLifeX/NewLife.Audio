using System.Runtime.InteropServices;
using NewLife.Audio.DSP;
using NewLife.Data;

namespace NewLife.Audio.Speech;

/// <summary>基于GMM（高斯混合模型）的语音活动检测器</summary>
/// <remarks>
/// 6子带能量分析 + 2分量高斯混合模型 + Hangover防抖动。
/// 参考 WebRTC VAD 原理，纯 C# 实现。
/// 帧长固定 10ms（80/160/240/320/480 样本对应 8k/16k/24k/32k/48kHz）。
/// 实现 IAudioProcessor 接口，可直接接入 M3 信号链。Read() 透传样本不修改数据，仅更新检测状态。
/// </remarks>
public class GmmVad : IVoiceActivityDetector, IAudioProcessor
{
    private Int32 _aggressiveness;
    private Int32 _sampleRate;
    private Int32 _frameSamples;
    private readonly AudioFormat _format;

    // 子带能量历史
    private readonly Single[] _subbandEnergy = new Single[6];
    private readonly Single[] _subbandMean = new Single[6];
    private readonly Single[] _subbandVar = new Single[6];

    // Hangover 计数器（帧数）
    private Int32 _hangoverCount;
    private const Int32 HangoverFrames = 15; // ~200ms at 10ms frames
    private Boolean _wasSpeech;

    // GMM 参数（简化版：均值/方差阈值）
    private static readonly Single[] NoiseMeanInit = [120f, 100f, 90f, 80f, 70f, 60f];
    private static readonly Single[] SpeechMeanMin = [200f, 180f, 160f, 140f, 120f, 100f];

    // 子带频率范围（Hz）
    private static readonly (Int32 Low, Int32 High)[] Subbands =
    [
        (80, 250), (250, 500), (500, 1000), (1000, 2000), (2000, 3000), (3000, 4000)
    ];

    /// <summary>检测灵敏度（0~3，3最激进=更多判定为语音）</summary>
    public Int32 Aggressiveness
    {
        get => _aggressiveness;
        set => _aggressiveness = value < 0 ? 0 : value > 3 ? 3 : value;
    }

    /// <summary>输入格式</summary>
    public AudioFormat InputFormat => _format;

    /// <summary>输出格式（与输入相同，VAD 不修改数据）</summary>
    public AudioFormat OutputFormat => _format;

    /// <summary>上游数据源</summary>
    public IAudioProcessor Source { get; set; }

    /// <summary>最近一次检测的语音概率（0~1）</summary>
    public Single LastSpeechProbability { get; private set; }

    /// <summary>最近一次检测是否为语音</summary>
    public Boolean LastIsSpeech { get; private set; }

    /// <summary>初始化 VAD</summary>
    /// <param name="sampleRate">采样率（Hz），默认 8000</param>
    /// <param name="aggressiveness">灵敏度（0~3），默认 1</param>
    public GmmVad(Int32 sampleRate = 8000, Int32 aggressiveness = 1)
    {
        _sampleRate = sampleRate;
        _aggressiveness = aggressiveness < 0 ? 0 : aggressiveness > 3 ? 3 : aggressiveness;
        _frameSamples = sampleRate / 100; // 10ms
        _format = new AudioFormat { SampleRate = sampleRate, Channels = 1, BitsPerSample = 16 };
        Reset();
    }

    /// <summary>判断是否为语音帧</summary>
    public Boolean IsSpeech(ReadOnlySpan<Byte> frame)
    {
        var prob = GetSpeechProbability(frame);
        var threshold = 0.5f - _aggressiveness * 0.1f;

        var isSpeech = prob > threshold;

        // Hangover 逻辑
        if (isSpeech)
        {
            _hangoverCount = HangoverFrames;
            _wasSpeech = true;
            return true;
        }

        if (_hangoverCount > 0)
        {
            _hangoverCount--;
            return true;
        }

        _wasSpeech = false;
        return false;
    }

    /// <summary>获取语音概率（0.0~1.0）</summary>
    public Single GetSpeechProbability(ReadOnlySpan<Byte> frame)
    {
        if (frame.Length < _frameSamples * 2) return 0f;

        // 计算6子带能量
        ComputeSubbandEnergy(frame);

        // 简化 GMM 评分：比较各子带能量与阈值
        var speechScore = 0f;
        var totalScore = 0f;

        for (var b = 0; b < 6; b++)
        {
            var energy = _subbandEnergy[b];
            var noiseLevel = _subbandMean[b] * (1f + _aggressiveness * 0.2f);
            var speechLevel = SpeechMeanMin[b] * (1f - _aggressiveness * 0.15f);

            if (energy > speechLevel)
                speechScore += 1f;
            else if (energy > noiseLevel)
                speechScore += (energy - noiseLevel) / (speechLevel - noiseLevel + 1f);

            totalScore += 1f;
        }

        // 更新噪声估计（缓慢适应）
        for (var b = 0; b < 6; b++)
        {
            _subbandMean[b] = _subbandMean[b] * 0.95f + _subbandEnergy[b] * 0.05f;
            var diff = _subbandEnergy[b] - _subbandMean[b];
            _subbandVar[b] = _subbandVar[b] * 0.95f + diff * diff * 0.05f;
        }

        return speechScore / totalScore < 0f ? 0f : speechScore / totalScore > 1f ? 1f : speechScore / totalScore;
    }

    /// <summary>重置检测器状态</summary>
    public void Reset()
    {
        _hangoverCount = 0;
        _wasSpeech = false;
        LastSpeechProbability = 0f;
        LastIsSpeech = false;
        for (var i = 0; i < 6; i++)
        {
            _subbandEnergy[i] = 0;
            _subbandMean[i] = NoiseMeanInit[i];
            _subbandVar[i] = 100f;
        }
    }

    /// <summary>从上游源读取数据，执行 VAD 检测后透传样本（不修改数据）</summary>
    /// <param name="buffer">输出缓冲区</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="count">采样数</param>
    /// <returns>实际读取的采样数</returns>
    public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
    {
        if (Source == null) return 0;

        var read = Source.Read(buffer, offset, count);
        if (read == 0) return 0;

        // 转换 Float → Int16 用于 VAD 检测
        var pcmBytes = new Byte[read * 2];
        var pcmSamples = MemoryMarshal.Cast<Byte, Int16>(pcmBytes.AsSpan());
        for (var i = 0; i < read; i++)
        {
            var sample = buffer[offset + i];
            pcmSamples[i] = (Int16)(Math.Max(-1f, Math.Min(1f, sample)) * 32767f);
        }

        // 执行 VAD 检测
        LastSpeechProbability = GetSpeechProbability(pcmBytes);
        LastIsSpeech = IsSpeech(pcmBytes);

        // 透传样本（不修改）
        return read;
    }

    private void ComputeSubbandEnergy(ReadOnlySpan<Byte> samples)
    {
        var sampleCount = Math.Min(samples.Length / 2, _frameSamples);

        // 预转换 PCM Int16 → 浮点样本，避免内层 DFT 循环重复位运算
        var floatSamples = new Single[sampleCount];
        var shortSamples = MemoryMarshal.Cast<Byte, Int16>(samples);
        for (var n = 0; n < sampleCount; n++)
            floatSamples[n] = (Single)(shortSamples[n] / 32768.0);

        // 简易 FFT 计算子带能量
        for (var b = 0; b < 6; b++)
        {
            var lowFreq = Subbands[b].Low;
            var highFreq = Subbands[b].High;
            var energy = 0.0;
            var validSamples = 0;

            var lowBin = lowFreq * _frameSamples / _sampleRate;
            var highBin = highFreq * _frameSamples / _sampleRate;

            for (var k = lowBin; k <= highBin && k < sampleCount / 2; k++)
            {
                var real = 0.0;
                var imag = 0.0;
                for (var n = 0; n < sampleCount; n++)
                {
                    var sample = floatSamples[n];
                    var angle = 2.0 * Math.PI * k * n / sampleCount;
                    real += sample * Math.Cos(angle);
                    imag -= sample * Math.Sin(angle);
                }
                energy += real * real + imag * imag;
                validSamples++;
            }

            _subbandEnergy[b] = validSamples > 0 ? (Single)(energy / (validSamples * sampleCount)) : 0f;
        }
    }
}
