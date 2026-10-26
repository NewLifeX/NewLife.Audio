using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NewLife.Audio.Codecs;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Streaming;

/// <summary>编码输出参数</summary>
public class AudioEncodeOptions
{
    /// <summary>目标容器（按扩展名，支持 .wav/.flac/.mp3），为空时跟随输出流无法推断则用 .wav</summary>
    public String Container { get; set; } = ".wav";

    /// <summary>目标采样率（Hz），0 表示与输入一致</summary>
    public Int32 SampleRate { get; set; }

    /// <summary>目标声道数，0 表示与输入一致</summary>
    public Int32 Channels { get; set; }

    /// <summary>重采样质量，默认加窗 Sinc</summary>
    public ResamplerQuality ResampleQuality { get; set; } = ResamplerQuality.Sinc;

    /// <summary>MP3 码率（kbps）</summary>
    public Int32 Mp3BitrateKbps { get; set; } = 128;

    /// <summary>MP3 码率模式</summary>
    public Mp3RateMode Mp3Mode { get; set; } = Mp3RateMode.Cbr;

    /// <summary>FLAC 压缩级别 0~8</summary>
    public Int32 FlacCompressionLevel { get; set; } = 5;

    /// <summary>写入容器头的元数据（WAV INFO / FLAC VORBIS_COMMENT / MP3 ID3v2）</summary>
    public AudioMetadata Metadata { get; set; }

    /// <summary>释放管线时是否保留输出流打开，默认 false（由管线负责关闭）</summary>
    public Boolean LeaveOpen { get; set; }
}

/// <summary>压缩音频输出管线：PCM → 重采样/声道转换 → 编码 → 容器</summary>
/// <remarks>
/// 面向 TTS 与实时合成场景：调用方边合成边 <see cref="Write"/> 16 位小端交错 PCM，
/// 管线内部完成声道与采样率转换，再交给容器写入器编码落盘或写入网络流。<br/>
/// 内部持有少量尾部缓冲以保证输出总帧数与「输入帧数 × 目标率 / 源率」一致（不足补零、超出截断），
/// 避免重采样核延迟导致的时长漂移。写入为同步阻塞语义，天然形成背压；
/// 调用 <see cref="Complete"/> 或越过 <see cref="FlushIntervalBytes"/> 时立即冲刷，便于流式首包尽快发出。
/// </remarks>
/// <example>
/// <code>
/// using var pipeline = new AudioEncodePipeline(response.Body, inputFormat, new AudioEncodeOptions { Container = ".mp3", SampleRate = 24000 });
/// pipeline.Write(pcmChunk);
/// pipeline.Complete();
/// Console.WriteLine($"首包 {pipeline.FirstChunkLatency.TotalMilliseconds:F0} ms");
/// </code>
/// </example>
public class AudioEncodePipeline : IDisposable
{
    #region 属性
    private readonly IAudioContainerWriter _writer;
    private readonly AudioFormat _inputFormat;
    private readonly AudioFormat _outputFormat;
    private readonly ResamplerQuality _quality;
    private readonly Int32 _inputChannels;
    private readonly Int32 _outputChannels;
    private readonly Single[][] _planes;
    private readonly Resampler[] _resamplers;
    private readonly List<Single> _pending = [];
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private Int64 _inputFrames;

    /// <summary>输入音频格式</summary>
    public AudioFormat InputFormat => _inputFormat;

    /// <summary>输出音频格式</summary>
    public AudioFormat OutputFormat => _outputFormat;

    /// <summary>已交给编码器的 PCM 字节数</summary>
    public Int64 OutputBytes { get; private set; }

    /// <summary>已写入的输出帧数</summary>
    public Int64 OutputFrames { get; private set; }

    /// <summary>已接收的输入帧数</summary>
    public Int64 InputFrames => _inputFrames;

    /// <summary>首包延迟（首次真正写出编码数据所需时间），未写出时为 <see cref="TimeSpan.Zero"/></summary>
    public TimeSpan FirstChunkLatency { get; private set; }

    /// <summary>触发立即冲刷的字节阈值，默认 64 KB</summary>
    public Int32 FlushIntervalBytes { get; set; } = 64 * 1024;

    /// <summary>是否已完成</summary>
    public Boolean IsCompleted { get; private set; }
    #endregion

    #region 构造
    /// <summary>构造编码管线</summary>
    /// <param name="output">输出流（文件或网络流）</param>
    /// <param name="inputFormat">输入 PCM 格式（16 位整型）</param>
    /// <param name="options">编码参数</param>
    public AudioEncodePipeline(Stream output, AudioFormat inputFormat, AudioEncodeOptions options)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));
        _inputFormat = inputFormat?.Clone() ?? throw new ArgumentNullException(nameof(inputFormat));
        options ??= new AudioEncodeOptions();

        _inputChannels = Math.Max(1, _inputFormat.Channels);
        _outputChannels = options.Channels > 0 ? options.Channels : _inputChannels;
        var sampleRate = options.SampleRate > 0 ? options.SampleRate : _inputFormat.SampleRate;
        _quality = options.ResampleQuality;

        _outputFormat = new AudioFormat
        {
            SampleRate = sampleRate,
            Channels = _outputChannels,
            BitsPerSample = 16,
            SamplesPerFrame = _inputFormat.SamplesPerFrame,
            Encoding = AVTypes.LPCM,
        };

        _writer = CreateWriter(options.LeaveOpen ? new KeepOpenStream(output) : output, _outputFormat, options);
        if (options.Metadata != null) _writer.WriteMetadata(options.Metadata);

        _planes = new Single[_inputChannels][];
        _resamplers = new Resampler[_inputChannels];
        if (sampleRate != _inputFormat.SampleRate)
        {
            var planeFormat = new AudioFormat { SampleRate = _inputFormat.SampleRate, Channels = 1, BitsPerSample = 16 };
            for (var ch = 0; ch < _inputChannels; ch++)
            {
                _resamplers[ch] = new Resampler(sampleRate, planeFormat, _quality);
            }
        }
    }

    private static IAudioContainerWriter CreateWriter(Stream output, AudioFormat format, AudioEncodeOptions options)
    {
        var container = (options.Container ?? "").ToLowerInvariant();
        return container switch
        {
            ".flac" => new FlacFileWriter(output, format, new FlacWriterOptions { CompressionLevel = options.FlacCompressionLevel }),
            ".mp3" => new Mp3FileWriter(output, format, options.Mp3BitrateKbps),
            _ => new WaveFileWriter(output, format),
        };
    }
    #endregion

    #region 方法
    /// <summary>写入一段 16 位小端交错 PCM</summary>
    /// <param name="pcm">PCM 数据</param>
    public void Write(ReadOnlySpan<Byte> pcm)
    {
        if (IsCompleted) throw new InvalidOperationException("The pipeline has already completed.");
        if (pcm.Length < 2) return;

        var samples = PcmConverter.ToFloat(pcm, SampleFormat.Int16);
        WriteSamples(samples);
    }

    /// <summary>写入一段浮点交错采样（±1.0）</summary>
    /// <param name="samples">交错采样</param>
    public void WriteSamples(ReadOnlySpan<Single> samples)
    {
        if (IsCompleted) throw new InvalidOperationException("The pipeline has already completed.");
        if (samples.Length == 0) return;

        var frames = samples.Length / _inputChannels;
        if (frames <= 0) return;
        _inputFrames += frames;

        // 逐声道拆平面
        for (var ch = 0; ch < _inputChannels; ch++)
        {
            if (_planes[ch] == null || _planes[ch].Length < frames) _planes[ch] = new Single[frames];
            var plane = _planes[ch];
            for (var i = 0; i < frames; i++) plane[i] = samples[i * _inputChannels + ch];
        }

        var outFrames = _resamplers[0] != null ? 0 : frames;
        var converted = new Single[_inputChannels][];
        if (_resamplers[0] != null)
        {
            outFrames = Int32.MaxValue;
            for (var ch = 0; ch < _inputChannels; ch++)
            {
                var resampler = _resamplers[ch];
                resampler.Source = new PlaneSource(_planes[ch], frames);
                var estimate = (Int32)Math.Ceiling(frames * (Double)_outputFormat.SampleRate / _inputFormat.SampleRate) + 2 * resampler.Latency + 16;
                var buffer = new Single[estimate];
                var read = resampler.Read(buffer, 0, estimate);
                converted[ch] = buffer;
                if (read < outFrames) outFrames = read;
            }
            if (outFrames == Int32.MaxValue) outFrames = 0;
        }
        else
        {
            for (var ch = 0; ch < _inputChannels; ch++) converted[ch] = _planes[ch];
        }

        if (outFrames <= 0) return;

        // 交织 + 声道转换后按目标帧数输出
        var interleaved = new Single[outFrames * _inputChannels];
        for (var ch = 0; ch < _inputChannels; ch++)
        {
            var plane = converted[ch];
            for (var i = 0; i < outFrames; i++) interleaved[i * _inputChannels + ch] = plane[i];
        }

        var mapped = new Single[outFrames * _outputChannels];
        var mappedFrames = outFrames;
        if (_outputChannels == _inputChannels) Array.Copy(interleaved, mapped, mapped.Length);
        else ChannelConverter.Convert(interleaved, _inputChannels, mapped, 0, _outputChannels, outFrames);

        Append(mapped, mappedFrames, false);
    }

    /// <summary>完成写入：冲刷尾部缓冲并关闭容器</summary>
    public void Complete()
    {
        if (IsCompleted) return;

        Append(null, 0, true);
        _writer.Flush();
        IsCompleted = true;
    }

    /// <summary>释放管线并完成写入</summary>
    public void Dispose()
    {
        if (!IsCompleted) Complete();
        _writer.Dispose();
    }
    #endregion

    #region 辅助
    private void Append(Single[] samples, Int32 frames, Boolean final)
    {
        if (frames > 0) _pending.AddRange(samples);

        var available = _pending.Count / _outputChannels;
        var target = _resamplers[0] != null ? ExpectedFrames : _inputFrames;

        if (final)
        {
            // 收尾：补零到目标帧数（重采样核延迟会少产出末尾若干帧），再按目标截断
            var need = (Int32)Math.Max(0, target - OutputFrames);
            if (need > available)
            {
                _pending.AddRange(new Single[(need - available) * _outputChannels]);
                available = need;
            }
        }

        var take = final
            ? Math.Min(available, (Int32)Math.Max(0, target - OutputFrames))
            : available;

        // 非最终调用时预留尾部，等待重采样核延迟补足；不重采样则全部输出
        if (!final && take > 0)
        {
            var tail = _resamplers[0] != null ? _resamplers[0].Latency + 1 : 0;
            take = Math.Max(0, take - tail);
        }

        if (take <= 0) return;

        var count = take * _outputChannels;
        var all = _pending.ToArray();
        var samples16 = PcmConverter.FromFloat(all.AsSpan(0, count), SampleFormat.Int16);

        if (_writer is Mp3FileWriter mp3) mp3.WritePcm(samples16);
        else _writer.WriteFrame(samples16);

        _pending.RemoveRange(0, count);
        OutputFrames += take;
        OutputBytes += samples16.Length;

        if (FirstChunkLatency == TimeSpan.Zero) FirstChunkLatency = _watch.Elapsed;
    }

    private Int64 ExpectedFrames => _inputFormat.SampleRate > 0
        ? (Int64)Math.Round((Double)_inputFrames * _outputFormat.SampleRate / _inputFormat.SampleRate)
        : 0;

    /// <summary>不关闭底层流的包装（<see cref="AudioEncodeOptions.LeaveOpen"/>）</summary>
    private sealed class KeepOpenStream : Stream
    {
        private readonly Stream _inner;

        public KeepOpenStream(Stream inner) => _inner = inner;

        public override Boolean CanRead => _inner.CanRead;

        public override Boolean CanSeek => _inner.CanSeek;

        public override Boolean CanWrite => _inner.CanWrite;

        public override Int64 Length => _inner.Length;

        public override Int64 Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count) => _inner.Read(buffer, offset, count);

        public override Int64 Seek(Int64 offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(Int64 value) => _inner.SetLength(value);

        public override void Write(Byte[] buffer, Int32 offset, Int32 count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(Boolean disposing)
        {
            // 保留底层流
        }
    }

    /// <summary>把一段平面采样作为管线源的适配器</summary>
    private sealed class PlaneSource : IAudioProcessor
    {
        private readonly Single[] _data;
        private readonly Int32 _count;
        private Int32 _position;

        public PlaneSource(Single[] data, Int32 count)
        {
            _data = data;
            _count = count;
        }

        public AudioFormat InputFormat => new();

        public AudioFormat OutputFormat => new();

        public IAudioProcessor Source { get; set; }

        public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
        {
            var n = Math.Min(count, _count - _position);
            if (n <= 0) return 0;

            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;

            return n;
        }

        public void Reset() => _position = 0;
    }
    #endregion
}
