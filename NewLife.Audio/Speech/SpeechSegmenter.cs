using System;
using System.Collections.Generic;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Speech;

/// <summary>语音分段器：按能量端点把连续 PCM 切成一句一段</summary>
/// <remarks>
/// 逐帧计算短时能量，超过阈值判定为语音；连续静音达到 <see cref="SpeechRecognitionOptions.EndpointSilenceMs"/>
/// 时结束当前语句。输出包含句首句尾时间，供上层生成时间戳。适合作为端点检测的通用实现，
/// 需要更强鲁棒性时可用 <see cref="GmmVad"/> 替换判定逻辑。
/// </remarks>
public class SpeechSegmenter
{
    private readonly AudioFormat _format;
    private readonly SpeechRecognitionOptions _options;
    private readonly Int32 _frameBytes;
    private readonly Int32 _silenceFrames;
    private readonly Int32 _maxFrames;

    private readonly List<Byte> _buffer = [];
    private Int32 _silenceCount;
    private Boolean _inSpeech;
    private Double _totalSamples;

    /// <summary>已处理的采样点总数（每声道）</summary>
    public Double TotalSamples => _totalSamples;

    /// <summary>缓冲区中尚未结束的语音 PCM（用于部分结果），无语音时为 null</summary>
    public Byte[] BufferedPcm => _buffer.Count > 0 ? _buffer.ToArray() : null;

    /// <summary>构造分段器</summary>
    /// <param name="format">音频格式</param>
    /// <param name="options">识别参数（端点与阈值）</param>
    public SpeechSegmenter(AudioFormat format, SpeechRecognitionOptions options)
    {
        _format = format ?? throw new ArgumentNullException(nameof(format));
        _options = options ?? new SpeechRecognitionOptions();

        var frameSamples = format.SamplesPerFrame > 0 ? format.SamplesPerFrame : 160;
        _frameBytes = frameSamples * format.Channels * 2;
        if (_frameBytes <= 0) _frameBytes = 320;

        var frameMs = 1000.0 * frameSamples / Math.Max(1, format.SampleRate);
        _silenceFrames = Math.Max(1, (Int32)Math.Round(_options.EndpointSilenceMs / frameMs));
        _maxFrames = Math.Max(_silenceFrames, (Int32)Math.Round(_options.MaxUtteranceMs / frameMs));
    }

    /// <summary>送入一段 PCM，返回本次结束的语句（可能为空列表）</summary>
    /// <param name="pcm">16 位小端交错 PCM</param>
    /// <returns>完成的语句 PCM 及其时间范围</returns>
    public IList<(Byte[] Pcm, TimeSpan Start, TimeSpan End, Boolean IsEndpoint)> Accept(Byte[] pcm)
    {
        var results = new List<(Byte[], TimeSpan, TimeSpan, Boolean)>();
        if (pcm == null || pcm.Length == 0) return results;

        var offset = 0;
        while (offset + _frameBytes <= pcm.Length)
        {
            var frame = new Byte[_frameBytes];
            Array.Copy(pcm, offset, frame, 0, _frameBytes);
            offset += _frameBytes;

            var speech = IsSpeech(frame);
            _totalSamples += _frameBytes / 2 / Math.Max(1, _format.Channels);

            if (speech)
            {
                _silenceCount = 0;
                _inSpeech = true;
            }
            else if (_inSpeech)
            {
                _silenceCount++;
            }

            if (_inSpeech) _buffer.AddRange(frame);

            var framesInBuffer = _buffer.Count / _frameBytes;

            // 句尾静音够长，或超过最长语句长度，则切出一句
            if (_inSpeech && (_silenceCount >= _silenceFrames || framesInBuffer >= _maxFrames))
            {
                var segment = Flush(_silenceCount >= _silenceFrames);
                if (segment.Pcm != null) results.Add(segment);
            }
            else if (!_inSpeech && _buffer.Count > 0)
            {
                _buffer.Clear();
            }
        }

        return results;
    }

    /// <summary>冲刷缓冲中的最后一句</summary>
    /// <returns>语句 PCM 及其时间范围；缓冲为空时返回 null 元组</returns>
    public (Byte[] Pcm, TimeSpan Start, TimeSpan End, Boolean IsEndpoint) Flush() => Flush(false);

    private (Byte[] Pcm, TimeSpan Start, TimeSpan End, Boolean IsEndpoint) Flush(Boolean isEndpoint)
    {
        var frames = _buffer.Count / _frameBytes;
        var frameSamples = _frameBytes / 2 / Math.Max(1, _format.Channels);
        var minFrames = Math.Max(1, (Int32)Math.Round((Double)_options.MinUtteranceMs * _format.SampleRate / 1000 / frameSamples));

        var pcm = _buffer.ToArray();
        var end = TimeSpan.FromSeconds(_totalSamples / Math.Max(1, _format.SampleRate));
        var start = end - TimeSpan.FromSeconds((Double)frames * frameSamples / Math.Max(1, _format.SampleRate));

        _buffer.Clear();
        _silenceCount = 0;
        _inSpeech = false;

        // 过短的段视为噪声丢弃
        if (frames < minFrames) return (null, start, end, isEndpoint);

        return (pcm, start, end, isEndpoint);
    }

    private Boolean IsSpeech(Byte[] frame)
    {
        var samples = frame.Length / 2;
        if (samples == 0) return false;

        var sum = 0.0;
        for (var i = 0; i < samples; i++)
        {
            var v = (Int16)(frame[i * 2] | frame[i * 2 + 1] << 8) / 32768.0;
            sum += v * v;
        }

        var rms = Math.Sqrt(sum / samples);

        return rms >= _options.EnergyThreshold;
    }
}
