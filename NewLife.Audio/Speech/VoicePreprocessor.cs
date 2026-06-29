using NewLife.Audio.DSP;
using NewLife.Data;

namespace NewLife.Audio.Speech;

/// <summary>语音前置处理管线。串联高通滤波 → VAD → AGC，即开即用</summary>
/// <remarks>
/// 实现 IAudioProcessor 接口，可直接接入 M3 信号链。
/// 可配置开关：EnableVAD / EnableAGC / SilenceOnVad。
/// VAD 判断静音时默认输出静音包（DTX），适应 IoT 带宽敏感场景。
/// </remarks>
public class VoicePreprocessor : IAudioProcessor
{
    private readonly AudioFormat _inputFormat;
    private readonly AudioFormat _outputFormat;

    private readonly BiQuadFilter _highPassFilter;
    private readonly GmmVad _vad;
    private readonly AutomaticGainControl _agc;

    private Boolean _enableVAD;
    private Boolean _enableAGC;
    private Boolean _silenceOnVad;

    /// <summary>输入格式</summary>
    public AudioFormat InputFormat => _inputFormat;

    /// <summary>输出格式</summary>
    public AudioFormat OutputFormat => _outputFormat;

    /// <summary>上游源</summary>
    public IAudioProcessor Source { get; set; }

    /// <summary>是否启用 VAD</summary>
    public Boolean EnableVAD
    {
        get => _enableVAD;
        set => _enableVAD = value;
    }

    /// <summary>是否启用 AGC</summary>
    public Boolean EnableAGC
    {
        get => _enableAGC;
        set => _enableAGC = value;
    }

    /// <summary>VAD 检测到静音时是否输出静音（关闭则原样输出）</summary>
    public Boolean SilenceOnVad
    {
        get => _silenceOnVad;
        set => _silenceOnVad = value;
    }

    /// <summary>VAD 灵敏度（0~3）</summary>
    public Int32 VadAggressiveness
    {
        get => _vad.Aggressiveness;
        set => _vad.Aggressiveness = value;
    }

    /// <summary>AGC 目标电平（dBFS）</summary>
    public Single AgcTargetLevelDB
    {
        get => _agc.TargetLevelDB;
        set => _agc.TargetLevelDB = value;
    }

    /// <summary>当前 VAD 语音概率（0~1）</summary>
    public Single CurrentSpeechProbability { get; private set; }

    /// <summary>当前 AGC 增益系数</summary>
    public Single CurrentAgcGain => _agc.CurrentGain;

    /// <summary>初始化语音前置管线</summary>
    /// <param name="sampleRate">采样率（Hz），默认 8000</param>
    public VoicePreprocessor(Int32 sampleRate = 8000)
    {
        _inputFormat = new AudioFormat { SampleRate = sampleRate, Channels = 1, BitsPerSample = 16 };
        _outputFormat = _inputFormat.Clone();

        // 80Hz 高通滤波器（去除直流偏移和低频噪声）
        _highPassFilter = new BiQuadFilter(BiQuadFilter.FilterType.HighPass, 80f, 0.7071f, 0f, _inputFormat);

        _vad = new GmmVad(sampleRate);
        _agc = new AutomaticGainControl(-18f, 20f, _inputFormat);

        _enableVAD = true;
        _enableAGC = true;
        _silenceOnVad = true;
    }

    /// <summary>读取处理后的数据</summary>
    public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
    {
        if (Source == null) return 0;

        var read = Source.Read(buffer, offset, count);
        if (read == 0) return 0;

        // Step 1: 高通滤波
        for (var i = 0; i < read; i++)
            buffer[offset + i] = _highPassFilter.ProcessSample(buffer[offset + i]);

        // Step 2: VAD 检测
        if (_enableVAD)
        {
            // 构建临时 Packet 用于 VAD
            var tempPcm = new Byte[read * 2];
            for (var i = 0; i < read; i++)
            {
                var sample = (Int16)(buffer[offset + i] * 32767f);
                tempPcm[i * 2] = (Byte)(sample & 0xFF);
                tempPcm[i * 2 + 1] = (Byte)((sample >> 8) & 0xFF);
            }

            CurrentSpeechProbability = _vad.GetSpeechProbability((Packet)tempPcm);

            if (!_vad.IsSpeech((Packet)tempPcm) && _silenceOnVad)
            {
                // 输出静音
                Array.Clear(buffer, offset, read);
                return read;
            }
        }

        // Step 3: AGC
        if (_enableAGC)
        {
            var agcBuffer = new Single[read];
            Array.Copy(buffer, offset, agcBuffer, 0, read);
            // 手动逐采样处理 AGC
            for (var i = 0; i < read; i++)
            {
                var sample = agcBuffer[i];
                // 简化 AGC（RMS检测 + 增益调整）
                var rms = Math.Abs(sample);
                var targetGain = rms > 0.001f ? _agc.TargetLevel / rms : _agc.MaxGain;
                // 不直接设 Source，逐采样处理
                buffer[offset + i] = sample * Math.Min(targetGain, _agc.MaxGain);
            }
        }

        return read;
    }

    /// <summary>重置所有组件</summary>
    public void Reset()
    {
        _highPassFilter.Reset();
        _vad.Reset();
        _agc.Reset();
        CurrentSpeechProbability = 0;
    }
}
