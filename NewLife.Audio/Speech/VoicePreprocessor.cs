using System.Runtime.InteropServices;
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
    private readonly FixedBufferSource _agcBufferSource;

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
        _agcBufferSource = new FixedBufferSource(_inputFormat);
        _agc.Source = _agcBufferSource;

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
            var pcmSamples = MemoryMarshal.Cast<Byte, Int16>(tempPcm.AsSpan());
            for (var i = 0; i < read; i++)
            {
                pcmSamples[i] = (Int16)(buffer[offset + i] * 32767f);
            }

            CurrentSpeechProbability = _vad.GetSpeechProbability(tempPcm);

            if (!_vad.IsSpeech(tempPcm) && _silenceOnVad)
            {
                // 输出静音
                Array.Clear(buffer, offset, read);
                return read;
            }
        }

        // Step 3: AGC（通过 AutomaticGainControl 的完整包络跟随逻辑）
        if (_enableAGC)
        {
            // 将 HPF+VAD 处理后的数据喂给 AGC 的固定缓冲源
            _agcBufferSource.SetData(buffer, offset, read);
            var agcRead = _agc.Read(buffer, offset, read);
            if (agcRead > 0) read = agcRead;
        }

        return read;
    }

    /// <summary>重置所有组件</summary>
    public void Reset()
    {
        _highPassFilter.Reset();
        _vad.Reset();
        _agc.Reset();
        _agcBufferSource.Reset();
        CurrentSpeechProbability = 0;
    }

    #region 辅助

    /// <summary>固定缓冲源。实现 IAudioProcessor 作为数据提供者，供下游处理器（如 AGC）从其 Read() 拉取数据</summary>
    private sealed class FixedBufferSource : IAudioProcessor
    {
        private readonly AudioFormat _format;
        private Single[] _buffer;
        private Int32 _offset;
        private Int32 _length;
        private Int32 _pos;

        /// <summary>输入格式</summary>
        public AudioFormat InputFormat => _format;

        /// <summary>输出格式</summary>
        public AudioFormat OutputFormat => _format;

        /// <summary>上游源（不使用）</summary>
        public IAudioProcessor Source { get; set; }

        /// <summary>初始化固定缓冲源</summary>
        /// <param name="format">音频格式</param>
        public FixedBufferSource(AudioFormat format)
        {
            _format = format;
            _buffer = [];
        }

        /// <summary>设置待读取的数据</summary>
        /// <param name="buffer">源缓冲区</param>
        /// <param name="offset">起始偏移</param>
        /// <param name="length">数据长度</param>
        public void SetData(Single[] buffer, Int32 offset, Int32 length)
        {
            if (_buffer.Length < length)
                _buffer = new Single[length];
            Array.Copy(buffer, offset, _buffer, 0, length);
            _offset = 0;
            _length = length;
            _pos = 0;
        }

        /// <summary>读取数据</summary>
        public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
        {
            var remaining = _length - _pos;
            if (remaining <= 0) return 0;

            var toCopy = Math.Min(count, remaining);
            Array.Copy(_buffer, _pos, buffer, offset, toCopy);
            _pos += toCopy;
            return toCopy;
        }

        /// <summary>重置</summary>
        public void Reset()
        {
            _pos = 0;
            _length = 0;
        }
    }

    #endregion
}
