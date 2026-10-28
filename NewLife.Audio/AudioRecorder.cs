using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio.Devices;
using NewLife.Audio.DSP;
using NewLife.Audio.Streaming;

namespace NewLife.Audio;

/// <summary>录音选项</summary>
/// <remarks>默认 44100 Hz / 单声道 / 16 位，适合语音备忘与示例；需要更高品质时自行指定。</remarks>
public class AudioRecordOptions
{
    /// <summary>录音格式，默认 44100 Hz / 单声道 / 16 位</summary>
    public AudioFormat Format { get; set; } = new() { SampleRate = 44100, Channels = 1, BitsPerSample = 16 };

    /// <summary>输入设备 ID，为空时使用默认设备</summary>
    public String DeviceId { get; set; }

    /// <summary>缓冲秒数，默认 10 秒；0 表示只保留最小缓冲（最低延迟，抗抖动能力最弱）</summary>
    /// <remarks>
    /// 驱动内部按时把硬件数据取出放入有界队列，消费方（编码、落盘、业务处理）在此时长内变慢不会丢音频；
    /// 超出容量后丢弃最旧数据并计入 <see cref="AudioRecorder.DroppedBytes"/>。
    /// 44.1 kHz 单声道 16 位下 10 秒约 860 KB，48 kHz 立体声下约 1.9 MB。
    /// </remarks>
    public Int32 BufferSeconds { get; set; } = 10;
}

/// <summary>录音门面</summary>
/// <remarks>
/// 把“麦克风”变成一个可枚举的数据源：<see cref="Capture(TimeSpan, CancellationToken)"/> 一行录完，<see cref="RecordToFile(String, TimeSpan, AudioEncodeOptions, CancellationToken)"/> 边录边编码落盘，
/// <see cref="StreamAsync"/> 交给长稳处理（VAD、推流、实时识别）。<br/>
/// 本类只做协调与装配：采集线程、有界队列、事件与读取都在驱动（<see cref="IAudioRecorder"/>）内部，
/// 因此编码或落盘偶尔变慢不会丢音频；队列满时丢最旧并计入 <see cref="DroppedBytes"/>。<br/>
/// 电平/波形直接读 <see cref="Peak"/> 与 <see cref="Rms"/>（读取时算好，零分配）；确需事件时订阅驱动的 `DataAvailable`。
/// </remarks>
/// <example>
/// <code>
/// // 录 5 秒
/// var pcm = AudioRecorder.Capture(TimeSpan.FromSeconds(5));
///
/// // 边录边存 WAV，内存占用与录音时长无关
/// AudioRecorder.RecordToFile("record.wav", TimeSpan.FromSeconds(5));
///
/// // 流式处理
/// using var recorder = new AudioRecorder();
/// await foreach (var frame in recorder.StreamAsync().WithCancellation(ct))
/// {
///     using (frame) Process(frame.GetSpan());
/// }
/// </code>
/// </example>
public class AudioRecorder : IDisposable
{
    private readonly IAudioRecorder _recorder;
    private Boolean _disposed;

    #region 属性
    /// <summary>实际使用的录制器（驱动实现，含采集线程与队列）</summary>
    public IAudioRecorder Source => _recorder;

    /// <summary>实际使用的采集格式</summary>
    public AudioFormat Format => _recorder.Format;

    /// <summary>硬件设备缓冲被覆盖/欠载的次数（驱动层上报）</summary>
    public Int64 Overruns => _recorder.Overruns;

    /// <summary>因队列容量满而丢弃的字节数（消费端持续落后于采集速度）</summary>
    public Int64 DroppedBytes => _recorder.DroppedBytes;

    /// <summary>当前排队等待消费的字节数</summary>
    public Int64 BufferedBytes => _recorder.BufferedBytes;

    /// <summary>最近一次统计的峰值电平（0~1）</summary>
    public Single Peak { get; private set; }

    /// <summary>最近一次统计的均方根电平（0~1）</summary>
    public Single Rms { get; private set; }
    #endregion

    #region 构造
    /// <summary>构造录音门面</summary>
    /// <param name="options">录音选项，为空时使用默认格式与默认设备</param>
    /// <param name="driver">音频驱动，为空时自动选择平台最优后端；测试可注入假驱动</param>
    public AudioRecorder(AudioRecordOptions options = null, IAudioDriver driver = null)
    {
        options ??= new AudioRecordOptions();
        driver ??= AudioDriverFactory.Create();

        _recorder = driver.CreateRecorder();
        _recorder.BufferSeconds = options.BufferSeconds;
        _recorder.Init(options.Format?.Clone() ?? new AudioFormat { SampleRate = 44100, Channels = 1, BitsPerSample = 16 }, options.DeviceId);
    }
    #endregion

    #region 方法
    /// <summary>流式采集：每块是一个池化音频帧，用完必须释放</summary>
    /// <param name="chunkBytes">每块字节数，0 表示按 20 毫秒计算</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>按时间顺序产出的音频帧</returns>
    public IAsyncEnumerable<AudioFrame> StreamAsync(Int32 chunkBytes = 0, CancellationToken cancellationToken = default) =>
        _recorder.StreamAsync(chunkBytes, cancellationToken);

    /// <summary>录制指定时长并返回 PCM（同步阻塞）</summary>
    /// <param name="duration">录制时长</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>录制的 PCM 音频</returns>
    public AudioPcm Capture(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var total = GetTotalBytes(duration);
        var buffer = ArrayPool<Byte>.Shared.Rent((Int32)Math.Min(total, Int32.MaxValue));
        var offset = 0;

        _recorder.StartRecording();
        var idle = Stopwatch.StartNew();
        try
        {
            while (offset < total)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var want = (Int32)Math.Min(buffer.Length - offset, total - offset);
                if (want <= 0) break;

                var read = _recorder.Read(buffer.AsSpan(offset, want), 200, cancellationToken);
                if (read > 0)
                {
                    UpdateLevel(buffer.AsSpan(offset, read));
                    offset += read;
                    idle.Restart();
                }
                else if (!_recorder.IsRecording || idle.Elapsed >= TimeSpan.FromSeconds(2))
                {
                    // 设备启动或静音期间首读可能超时；给 2 秒宽限，避免刚打开就判定失败
                    break;
                }
            }
        }
        finally
        {
            _recorder.StopRecording();
        }

        var data = new Byte[offset];
        Buffer.BlockCopy(buffer, 0, data, 0, offset);
        ArrayPool<Byte>.Shared.Return(buffer);

        return new AudioPcm { Format = Format?.Clone(), Codec = AVTypes.LPCM, Data = data };
    }

    /// <summary>录制指定时长并返回 PCM（不占用调用方线程）</summary>
    /// <param name="duration">录制时长</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>录制的 PCM 音频</returns>
    public Task<AudioPcm> CaptureAsync(TimeSpan duration, CancellationToken cancellationToken = default) =>
        Task.Run(() => Capture(duration, cancellationToken), cancellationToken);

    /// <summary>录制指定时长并直接写入文件（边录边编码，内存与时长无关）</summary>
    /// <param name="path">目标路径，扩展名决定容器与编码</param>
    /// <param name="duration">录制时长</param>
    /// <param name="options">编码参数；<see cref="AudioEncodeOptions.Container"/> 默认取扩展名</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>写入的 PCM 字节数</returns>
    public Int64 RecordToFile(String path, TimeSpan duration, AudioEncodeOptions options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (path == null) throw new ArgumentNullException(nameof(path));

        var directory = Path.GetDirectoryName(path);
        if (!directory.IsNullOrEmpty()) Directory.CreateDirectory(directory);

        options ??= new AudioEncodeOptions();
        options.Container = Path.GetExtension(path);

        var total = GetTotalBytes(duration);
        var chunkBytes = AudioRecorderExtensions.GetChunkBytes(Format);
        var buffer = ArrayPool<Byte>.Shared.Rent(chunkBytes);
        var offset = 0L;

        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        try
        {
            using var pipeline = new AudioEncodePipeline(stream, Format, options);

            _recorder.StartRecording();
            var idle = Stopwatch.StartNew();
            try
            {
                while (offset < total)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var want = (Int32)Math.Min(buffer.Length, total - offset);
                    var read = _recorder.Read(buffer.AsSpan(0, want), 200, cancellationToken);
                    if (read > 0)
                    {
                        UpdateLevel(buffer.AsSpan(0, read));
                        pipeline.Write(buffer.AsSpan(0, read));
                        offset += read;
                        idle.Restart();
                    }
                    else if (!_recorder.IsRecording || idle.Elapsed >= TimeSpan.FromSeconds(2))
                    {
                        // 设备启动或静音期间首读可能超时；给 2 秒宽限，避免刚打开就写出空文件
                        break;
                    }
                }
            }
            finally
            {
                _recorder.StopRecording();
            }

            pipeline.Complete();
        }
        finally
        {
            ArrayPool<Byte>.Shared.Return(buffer);
            stream.Dispose();
        }

        return offset;
    }

    /// <summary>录制指定时长并直接写入文件（不占用调用方线程）</summary>
    /// <param name="path">目标路径</param>
    /// <param name="duration">录制时长</param>
    /// <param name="options">编码参数</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>写入的 PCM 字节数</returns>
    public Task<Int64> RecordToFileAsync(String path, TimeSpan duration, AudioEncodeOptions options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => RecordToFile(path, duration, options, cancellationToken), cancellationToken);

    /// <summary>释放录音门面</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _recorder.Dispose();
    }
    #endregion

    #region 静态捷径
    /// <summary>录制指定时长并返回 PCM</summary>
    /// <param name="duration">录制时长</param>
    /// <param name="options">录音选项</param>
    /// <returns>录制的 PCM 音频</returns>
    public static AudioPcm Capture(TimeSpan duration, AudioRecordOptions options = null)
    {
        using var recorder = new AudioRecorder(options);

        return recorder.Capture(duration);
    }

    /// <summary>录制指定时长并直接写入文件</summary>
    /// <param name="path">目标路径</param>
    /// <param name="duration">录制时长</param>
    /// <param name="options">编码参数</param>
    /// <param name="recordOptions">录音选项</param>
    /// <returns>写入的 PCM 字节数</returns>
    public static Int64 RecordToFile(String path, TimeSpan duration, AudioEncodeOptions options = null, AudioRecordOptions recordOptions = null)
    {
        using var recorder = new AudioRecorder(recordOptions);

        return recorder.RecordToFile(path, duration, options);
    }
    #endregion

    #region 辅助
    /// <summary>按格式与时长计算需要的字节数（整帧对齐）</summary>
    /// <param name="duration">时长</param>
    /// <returns>字节数</returns>
    private Int64 GetTotalBytes(TimeSpan duration)
    {
        var format = Format ?? AudioFormat.Default;
        var frameBytes = Math.Max(1, format.Channels * format.BytesPerSample);
        var bytes = (Int64)(format.ByteRate * Math.Max(0, duration.TotalSeconds));

        return Math.Max(frameBytes, bytes / frameBytes * frameBytes);
    }

    /// <summary>统计峰值与均方根电平</summary>
    /// <param name="pcm">16 位小端 PCM</param>
    private void UpdateLevel(ReadOnlySpan<Byte> pcm)
    {
        var peak = 0;
        var sum = 0.0;
        var count = 0;

        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (Int16)(pcm[i] | pcm[i + 1] << 8);
            var abs = Math.Abs((Int32)sample);
            if (abs > peak) peak = abs;

            sum += (Double)sample * sample;
            count++;
        }

        if (count == 0) return;

        Peak = peak / 32768f;
        Rms = (Single)(Math.Sqrt(sum / count) / 32768);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AudioRecorder));
    }
    #endregion
}
