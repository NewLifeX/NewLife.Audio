using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Devices;

/// <summary>音频播放器接口</summary>
public interface IAudioPlayer : IDisposable
{
    /// <summary>初始化播放器</summary>
    /// <param name="format">音频格式</param>
    /// <param name="deviceId">设备ID，null=默认设备</param>
    void Init(AudioFormat format, String deviceId = null);

    /// <summary>开始播放</summary>
    void Play();

    /// <summary>暂停播放</summary>
    void Pause();

    /// <summary>停止播放</summary>
    void Stop();

    /// <summary>写入音频数据到播放缓冲区</summary>
    /// <param name="data">PCM 音频数据</param>
    void Write(Byte[] data);

    /// <summary>缓冲区中待播放的字节数</summary>
    Int32 BufferedBytes { get; }

    /// <summary>播放停止事件</summary>
    event EventHandler PlaybackStopped;
}

/// <summary>音频录制器接口（事件 + 读取双通道）</summary>
/// <remarks>
/// 麦克风是实时数据源。驱动内部按时把硬件数据取出放入有界队列，因此：<br/>
/// 1. 消费方（编码、落盘、业务处理）在 <see cref="BufferSeconds"/> 内变慢不会丢音频；<br/>
/// 2. 主通道是 <see cref="Read"/>/<see cref="ReadAsync"/>，取走即消费；<see cref="DataAvailable"/> 是旁路观察
/// （入队时若无订阅者零开销，有订阅者则给一份副本，订阅方随便存，不影响主读取）。<br/>
/// 队列不做背压：麦克风不会因为消费慢而放慢，队列只能“容量封顶 + 丢最旧 + 计入 <see cref="DroppedBytes"/>”。
/// </remarks>
public interface IAudioRecorder : IDisposable
{
    /// <summary>音频格式</summary>
    AudioFormat Format { get; }

    /// <summary>是否正在录制</summary>
    Boolean IsRecording { get; }

    /// <summary>缓冲秒数，默认 10；0 表示只保留最小缓冲（最低延迟）</summary>
    /// <remarks>在 <see cref="StartRecording"/> 之前设置生效。决定慢消费可以容忍的时长，内存占用约等于 ByteRate×秒数。</remarks>
    Int32 BufferSeconds { get; set; }

    /// <summary>当前排队等待消费的字节数</summary>
    Int64 BufferedBytes { get; }

    /// <summary>因队列容量满而丢弃的字节数（消费端持续落后于采集速度）</summary>
    Int64 DroppedBytes { get; }

    /// <summary>硬件设备缓冲被覆盖/欠载的次数（由驱动上报）</summary>
    Int64 Overruns { get; }

    /// <summary>初始化录制器</summary>
    /// <param name="format">音频格式</param>
    /// <param name="deviceId">设备ID</param>
    void Init(AudioFormat format, String deviceId = null);

    /// <summary>开始录制</summary>
    void StartRecording();

    /// <summary>停止录制</summary>
    void StopRecording();

    /// <summary>同步读取一段 PCM（脚本、控制台用）</summary>
    /// <param name="buffer">目标缓冲，建议按整帧对齐</param>
    /// <param name="timeoutMilliseconds">等待超时时间（毫秒），小于等于 0 表示一直等</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>实际写入的字节数；0 表示超时或已停止</returns>
    Int32 Read(Span<Byte> buffer, Int32 timeoutMilliseconds, CancellationToken cancellationToken = default);

    /// <summary>异步读取一段 PCM（等队列信号，不占用线程）</summary>
    /// <param name="buffer">目标缓冲，建议按整帧对齐</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>实际写入的字节数；0 表示已停止</returns>
    ValueTask<Int32> ReadAsync(Memory<Byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>数据可用事件（旁路观察：UI 电平/波形；数据副本归订阅者所有）</summary>
    event EventHandler<Byte[]> DataAvailable;
}

/// <summary>录制器扩展：整块读取、异步枚举与分块大小</summary>
/// <remarks>异步枚举产出的帧来自共享数组池，用完必须 <see cref="AudioFrame.Dispose"/> 归还。</remarks>
/// <example>
/// <code>
/// using var recorder = driver.CreateRecorder();
/// recorder.Init(new AudioFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 });
/// await foreach (var frame in recorder.StreamAsync().WithCancellation(ct))
/// {
///     using (frame) Console.WriteLine($"{frame.Timestamp.TotalSeconds:F2}s {frame.Length} 字节");
/// }
/// </code>
/// </example>
public static class AudioRecorderExtensions
{
    /// <summary>取一个默认分块字节数（20 毫秒）</summary>
    /// <param name="format">音频格式</param>
    /// <returns>整帧对齐的字节数</returns>
    public static Int32 GetChunkBytes(AudioFormat format)
    {
        if (format == null || format.SampleRate <= 0) return 4096;

        var frameBytes = Math.Max(1, format.Channels * format.BytesPerSample);
        var samples = Math.Max(1, format.SampleRate / 50);

        return samples * frameBytes;
    }

    /// <summary>读取到缓冲填满为止</summary>
    /// <param name="recorder">录制器</param>
    /// <param name="buffer">目标缓冲</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>实际写入的字节数；小于缓冲长度表示已停止</returns>
    public static async ValueTask<Int32> ReadFullyAsync(this IAudioRecorder recorder, Memory<Byte> buffer, CancellationToken cancellationToken = default)
    {
        if (recorder == null) throw new ArgumentNullException(nameof(recorder));

        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await recorder.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;

            offset += read;
        }

        return offset;
    }

    /// <summary>把录制器变成异步枚举（未开始录制时自动开始，结束后按需停止）</summary>
    /// <param name="recorder">录制器</param>
    /// <param name="chunkBytes">每块字节数，0 表示按 20 毫秒计算</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>按时间顺序产出的音频帧，用完必须释放</returns>
    public static async IAsyncEnumerable<AudioFrame> StreamAsync(this IAudioRecorder recorder, Int32 chunkBytes = 0, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (recorder == null) throw new ArgumentNullException(nameof(recorder));

        var format = recorder.Format;
        if (chunkBytes <= 0) chunkBytes = GetChunkBytes(format);
        if (chunkBytes <= 0) chunkBytes = 4096;

        var started = false;
        if (!recorder.IsRecording)
        {
            recorder.StartRecording();
            started = true;
        }

        var timestamp = TimeSpan.Zero;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = AudioFrame.Rent(format, chunkBytes, timestamp);

                var read = await FillAsync(recorder, frame, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    frame.Dispose();
                    yield break;
                }

                frame.Count = read;
                var duration = frame.Duration;

                yield return frame;

                timestamp += duration;
            }
        }
        finally
        {
            if (started) recorder.StopRecording();
        }
    }

    private static async ValueTask<Int32> FillAsync(IAudioRecorder recorder, AudioFrame frame, CancellationToken cancellationToken)
    {
        try
        {
            return await recorder.ReadAsync(frame.GetMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 取消是正常结束：归还缓冲后让枚举器平滑退出
            frame.Dispose();
            return 0;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }
}

/// <summary>音频驱动适配器接口</summary>
public interface IAudioDriver
{
    /// <summary>驱动名称</summary>
    String Name { get; }

    /// <summary>枚举设备</summary>
    IReadOnlyList<AudioDeviceInfo> EnumerateDevices();

    /// <summary>获取默认输出设备</summary>
    AudioDeviceInfo GetDefaultOutputDevice();

    /// <summary>获取默认输入设备</summary>
    AudioDeviceInfo GetDefaultInputDevice();

    /// <summary>创建播放器</summary>
    IAudioPlayer CreatePlayer();

    /// <summary>创建录制器</summary>
    IAudioRecorder CreateRecorder();
}
