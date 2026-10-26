using System;
using System.Collections.Generic;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Speech;

/// <summary>语音前处理参数</summary>
public class SpeechPreprocessOptions
{
    /// <summary>处理帧长（毫秒），默认 10 ms</summary>
    public Int32 FrameMilliseconds { get; set; } = 10;

    /// <summary>是否启用回声消除，默认 true</summary>
    public Boolean EnableAec { get; set; } = true;

    /// <summary>是否启用降噪，默认 true</summary>
    public Boolean EnableNoiseSuppression { get; set; } = true;

    /// <summary>是否启用自动增益，默认 true</summary>
    public Boolean EnableAgc { get; set; } = true;

    /// <summary>远端参考延迟补偿（毫秒），用于对齐声学回路延迟，默认 0</summary>
    public Int32 ReferenceDelayMs { get; set; }

    /// <summary>AEC 滤波器长度（taps），默认 256</summary>
    public Int32 FilterLength { get; set; } = 256;

    /// <summary>AEC 自适应步长，默认 0.1</summary>
    public Single StepSize { get; set; } = 0.1f;

    /// <summary>收敛判定阈值（dB），ERLE 达到该值视为已收敛，默认 6</summary>
    public Double ConvergeErleDb { get; set; } = 6;

    /// <summary>失锁判定阈值（dB），远端有声但 ERLE 低于该值连续若干帧视为发散，默认 1</summary>
    public Double ResetErleDb { get; set; } = 1;

    /// <summary>连续多少帧满足失锁条件后重置滤波器，默认 20</summary>
    public Int32 ResetFrames { get; set; } = 20;

    /// <summary>AGC 目标电平（dBFS），默认 -18</summary>
    public Single AgcTargetLevelDB { get; set; } = -18;

    /// <summary>双讲判定阈值：近端能量超过远端能量的倍数，默认 2</summary>
    public Single DoubleTalkRatio { get; set; } = 2;
}

/// <summary>语音前处理运行指标</summary>
public class SpeechPreprocessMetrics
{
    /// <summary>已处理帧数</summary>
    public Int64 Frames { get; set; }

    /// <summary>判定为双讲的帧数</summary>
    public Int64 DoubleTalkFrames { get; set; }

    /// <summary>滤波器重置（失锁恢复）次数</summary>
    public Int32 ResetCount { get; set; }

    /// <summary>当前 ERLE 估计（dB）</summary>
    public Double ErleDb { get; set; }

    /// <summary>双讲帧占比 0~1</summary>
    public Double DoubleTalkRatio => Frames > 0 ? (Double)DoubleTalkFrames / Frames : 0;

    /// <summary>是否已收敛（末帧 ERLE 达到阈值且非双讲）</summary>
    public Boolean Converged { get; set; }

    /// <summary>最近一帧远端参考能量（dBFS）</summary>
    public Double FarEndLevelDb { get; set; }

    /// <summary>最近一帧近端输入能量（dBFS）</summary>
    public Double NearEndLevelDb { get; set; }

    /// <summary>最近一帧处理输出能量（dBFS）</summary>
    public Double OutputLevelDb { get; set; }
}

/// <summary>语音前处理管线（工程化）：10 ms 帧对齐 + 延迟补偿 + 回声消除 + 降噪 + 增益</summary>
/// <remarks>
/// 把任意长度的近端 PCM 重新按固定帧长（默认 10 ms，与主流语音编码帧长一致）切分后逐帧处理：
/// 远端参考先做延迟补偿再送入回声消除器，随后依次经过降噪与自动增益。<br/>
/// 同时统计 ERLE、双讲占比、收敛状态；当远端有声但 ERLE 长时间低于阈值（滤波器发散/回路变化）时
/// 自动重置并累计次数，实现失锁恢复。
/// </remarks>
/// <example>
/// <code>
/// using var pipeline = new SpeechProcessingPipeline(new AudioFormat { SampleRate = 16000, Channels = 1 });
/// pipeline.PushFarEnd(farPcm);
/// foreach (var frame in pipeline.Push(nearPcm)) Send(frame);
/// Console.WriteLine($"ERLE {pipeline.Metrics.ErleDb:F1} dB, 双讲 {pipeline.Metrics.DoubleTalkRatio:P0}");
/// </code>
/// </example>
public class SpeechProcessingPipeline : IDisposable
{
    #region 属性
    private readonly SpeechPreprocessOptions _options;
    private readonly AudioFormat _format;
    private readonly Int32 _frameSamples;
    private readonly Int32 _frameBytes;
    private readonly AcousticEchoCanceller _aec;
    private readonly SpectralNoiseSuppressor _ns;
    private readonly AutomaticGainControl _agc;
    private readonly List<Byte> _pending = [];
    private readonly Queue<Byte[]> _farEndFrames = new();
    private readonly Int32 _delayFrames;
    private Int32 _weakFrames;

    /// <summary>输入/输出音频格式</summary>
    public AudioFormat Format => _format;

    /// <summary>帧长（样本数，每声道）</summary>
    public Int32 FrameSamples => _frameSamples;

    /// <summary>帧长（字节数）</summary>
    public Int32 FrameBytes => _frameBytes;

    /// <summary>运行指标</summary>
    public SpeechPreprocessMetrics Metrics { get; } = new();
    #endregion

    #region 构造
    /// <summary>构造前处理管线</summary>
    /// <param name="format">音频格式（16 位整型）</param>
    /// <param name="options">前处理参数</param>
    public SpeechProcessingPipeline(AudioFormat format, SpeechPreprocessOptions options = null)
    {
        _format = format?.Clone() ?? throw new ArgumentNullException(nameof(format));
        _options = options ?? new SpeechPreprocessOptions();

        var frameMs = _options.FrameMilliseconds > 0 ? _options.FrameMilliseconds : 10;
        _frameSamples = Math.Max(1, _format.SampleRate * frameMs / 1000);
        _frameBytes = _frameSamples * Math.Max(1, _format.Channels) * 2;
        _delayFrames = Math.Max(0, _options.ReferenceDelayMs / frameMs);

        if (_options.EnableAec) _aec = new AcousticEchoCanceller(_options.FilterLength, _options.StepSize);
        if (_options.EnableNoiseSuppression) _ns = new SpectralNoiseSuppressor();
        if (_options.EnableAgc) _agc = new AutomaticGainControl { TargetLevelDB = _options.AgcTargetLevelDB };
    }
    #endregion

    #region 方法
    /// <summary>送入远端参考 PCM（扬声器播放的信号），可任意长度</summary>
    /// <param name="pcm">16 位小端交错 PCM</param>
    public void PushFarEnd(ReadOnlySpan<Byte> pcm)
    {
        if (pcm.Length == 0) return;

        var offset = 0;
        while (offset + _frameBytes <= pcm.Length)
        {
            var frame = new Byte[_frameBytes];
            pcm.Slice(offset, _frameBytes).CopyTo(frame);
            _farEndFrames.Enqueue(frame);
            offset += _frameBytes;
        }

        // 尾部不足一帧时补零，保持参考与近端帧对齐
        if (offset < pcm.Length)
        {
            var frame = new Byte[_frameBytes];
            pcm.Slice(offset).CopyTo(frame);
            _farEndFrames.Enqueue(frame);
        }

        // 只保留延迟补偿所需的帧数，避免无限增长
        var keep = _delayFrames + 2;
        while (_farEndFrames.Count > keep) _farEndFrames.Dequeue();
    }

    /// <summary>送入近端 PCM，返回按帧处理后的结果</summary>
    /// <param name="pcm">16 位小端交错 PCM</param>
    /// <returns>逐帧处理结果（每帧 <see cref="FrameBytes"/> 字节）</returns>
    public IList<Byte[]> Push(ReadOnlySpan<Byte> pcm)
    {
        var results = new List<Byte[]>();
        if (pcm.Length > 0)
        {
            var bytes = pcm.ToArray();
            _pending.AddRange(bytes);
        }

        while (_pending.Count >= _frameBytes)
        {
            var frame = new Byte[_frameBytes];
            _pending.CopyTo(0, frame, 0, _frameBytes);
            _pending.RemoveRange(0, _frameBytes);
            results.Add(ProcessFrame(frame));
        }

        return results;
    }

    /// <summary>冲刷缓冲中不足一帧的尾部（补零后处理）</summary>
    /// <returns>最后一帧处理结果，无残留时为空列表</returns>
    public IList<Byte[]> Flush()
    {
        var results = new List<Byte[]>();
        if (_pending.Count == 0) return results;

        var frame = new Byte[_frameBytes];
        _pending.CopyTo(0, frame, 0, _pending.Count);
        _pending.Clear();
        results.Add(ProcessFrame(frame));

        return results;
    }

    /// <summary>重置全部状态</summary>
    public void Reset()
    {
        _pending.Clear();
        _farEndFrames.Clear();
        _weakFrames = 0;
        _aec?.Reset();
        _ns?.Reset();
        _agc?.Reset();
    }

    /// <summary>释放资源</summary>
    public void Dispose() => Reset();
    #endregion

    #region 辅助
    private Byte[] ProcessFrame(Byte[] nearFrame)
    {
        Metrics.Frames++;

        // 远端参考：按延迟补偿取历史帧，不足时补静音
        Byte[] farFrame = null;
        if (_aec != null)
        {
            if (_farEndFrames.Count > _delayFrames)
            {
                for (var i = 0; i < _delayFrames; i++) _farEndFrames.Dequeue();
                farFrame = _farEndFrames.Count > 0 ? _farEndFrames.Dequeue() : null;
            }
            else
            {
                _farEndFrames.Clear();
            }

            _aec.ProcessFarEnd(farFrame ?? new Byte[_frameBytes]);
        }

        Metrics.FarEndLevelDb = Level(farFrame);
        Metrics.NearEndLevelDb = Level(nearFrame);

        var output = _aec?.ProcessNearEnd(nearFrame) ?? nearFrame;

        // 降噪与增益按帧处理
        if (_ns != null) output = _ns.Suppress(output);
        if (_agc != null) output = ApplyAgc(output);

        Metrics.OutputLevelDb = Level(output);
        Metrics.ErleDb = _aec?.ErleEstimate ?? 0;

        // 双讲判定：近端明显高于远端且回声消除效果有限
        var isDoubleTalk = Metrics.NearEndLevelDb > Metrics.FarEndLevelDb + 20 * Math.Log10(Math.Max(1e-6, _options.DoubleTalkRatio))
            && Metrics.NearEndLevelDb > -60;
        if (isDoubleTalk) Metrics.DoubleTalkFrames++;
        Metrics.Converged = !isDoubleTalk && Metrics.ErleDb >= _options.ConvergeErleDb;

        // 失锁恢复：远端有声但 ERLE 长期偏低，判定滤波器发散或回路改变
        if (_aec != null && Metrics.FarEndLevelDb > -45 && !isDoubleTalk)
        {
            if (Metrics.ErleDb < _options.ResetErleDb)
            {
                _weakFrames++;
                if (_weakFrames >= Math.Max(1, _options.ResetFrames))
                {
                    _aec.Reset();
                    Metrics.ResetCount++;
                    _weakFrames = 0;
                }
            }
            else
            {
                _weakFrames = 0;
            }
        }

        return output;
    }

    private Byte[] ApplyAgc(Byte[] pcm)
    {
        var samples = PcmConverter.ToFloat(pcm, SampleFormat.Int16);
        var buffer = new Single[samples.Length];
        Array.Copy(samples, buffer, samples.Length);

        // AutomaticGainControl 以固定源读取，这里用一次性源驱动
        _agc.Source = new ArraySource(buffer);
        var read = _agc.Read(buffer, 0, buffer.Length);

        return PcmConverter.FromFloat(buffer.AsSpan(0, Math.Max(0, read)), SampleFormat.Int16);
    }

    private static Double Level(Byte[] pcm)
    {
        if (pcm == null || pcm.Length < 2) return -120;

        var sum = 0.0;
        var count = pcm.Length / 2;
        for (var i = 0; i < count; i++)
        {
            var v = (Int16)(pcm[i * 2] | pcm[i * 2 + 1] << 8) / 32768.0;
            sum += v * v;
        }

        var rms = Math.Sqrt(sum / count);

        return rms > 0 ? 20 * Math.Log10(rms) : -120;
    }

    private sealed class ArraySource : IAudioProcessor
    {
        private readonly Single[] _data;
        private Int32 _position;

        public ArraySource(Single[] data) => _data = data;

        public AudioFormat InputFormat => new();

        public AudioFormat OutputFormat => new();

        public IAudioProcessor Source { get; set; }

        public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
        {
            var n = Math.Min(count, _data.Length - _position);
            if (n <= 0) return 0;

            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;

            return n;
        }

        public void Reset() => _position = 0;
    }
    #endregion
}
