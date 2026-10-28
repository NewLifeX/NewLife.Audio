using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio.DSP;
using NewLife.Log;

namespace NewLife.Audio.Devices;

/// <summary>录制器实现基类：线程、有界队列、事件与读取一次到位</summary>
/// <remarks>
/// 硬件差异只留在子类：<br/>
/// 1. <b>拉式硬件</b>（ALSA <c>readi</c>、WASAPI 事件、winmm 预投递缓冲）：实现 <see cref="Open"/>/<see cref="Close"/> 与 <see cref="ReadNative"/>，
///    基类起一条采集线程按时调用它；<br/>
/// 2. <b>推送式硬件</b>（厂商回调、SDK 主动推数据）：构造时传 <c>pushMode: true</c>，实现 <see cref="Open"/>/<see cref="Close"/>，
///    在回调里调用 <see cref="Push"/> 即可，基类不建线程。<br/>
/// 两种情况对外一致：<see cref="DataAvailable"/> 事件 + <see cref="Read"/>/<see cref="ReadAsync"/>。
/// 队列容量由 <see cref="BufferSeconds"/> 决定，溢出丢最旧并计入 <see cref="DroppedBytes"/>；不做背压（麦克风不会等）。
/// </remarks>
/// <example>
/// <code>
/// internal sealed class MyRecorder : AudioRecorderBase
/// {
///     protected override void Open() { /* 打开设备、启动原生采集 */ }
///     protected override void Close() { /* 停止并释放设备 */ }
///     protected override Int32 ReadNative(Span&lt;Byte&gt; buffer, Int32 timeoutMilliseconds, CancellationToken cancellationToken)
///         => /* 阻塞读取一块 PCM，返回字节数 */ 0;
/// }
/// </code>
/// </example>
public abstract class AudioRecorderBase : IAudioRecorder
{
    private readonly Queue<AudioFrame> _queue = new();
    private readonly Object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private Thread _thread;
    private Int64 _queuedBytes;
    private Int64 _droppedBytes;
    private Int64 _overruns;
    private Int64 _capacityBytes;
    private Int32 _chunkBytes;
    private volatile Boolean _running;
    private Boolean _disposed;

    /// <summary>构造录制器基类</summary>
    /// <param name="pushMode">是否推送式硬件：true 时由驱动回调 <see cref="Push"/>，基类不建采集线程</param>
    protected AudioRecorderBase(Boolean pushMode = false)
    {
        PushMode = pushMode;
        Format = AudioFormat.Default;
    }

    #region 属性
    /// <summary>音频格式</summary>
    public AudioFormat Format { get; protected set; }

    /// <summary>设备 ID</summary>
    public String DeviceId { get; protected set; }

    /// <summary>缓冲秒数，默认 10；0 表示只保留最小缓冲（最低延迟）</summary>
    public Int32 BufferSeconds { get; set; } = 10;

    /// <summary>是否正在录制</summary>
    public Boolean IsRecording => _running;

    /// <summary>当前排队等待消费的字节数</summary>
    public Int64 BufferedBytes
    {
        get { lock (_lock) return _queuedBytes; }
    }

    /// <summary>因队列容量满而丢弃的字节数</summary>
    public Int64 DroppedBytes => Interlocked.Read(ref _droppedBytes);

    /// <summary>硬件设备缓冲被覆盖/欠载的次数</summary>
    public Int64 Overruns => Interlocked.Read(ref _overruns);

    /// <summary>数据可用事件（旁路观察，参数为独立副本）</summary>
    public event EventHandler<Byte[]> DataAvailable;

    /// <summary>是否推送式硬件</summary>
    protected Boolean PushMode { get; }
    #endregion

    #region 生命周期
    /// <summary>初始化录制器</summary>
    /// <param name="format">音频格式</param>
    /// <param name="deviceId">设备 ID</param>
    public virtual void Init(AudioFormat format, String deviceId = null)
    {
        ThrowIfDisposed();

        Format = format?.Clone() ?? AudioFormat.Default;
        DeviceId = deviceId;
        _chunkBytes = AudioRecorderExtensions.GetChunkBytes(Format);
    }

    /// <summary>开始录制</summary>
    public void StartRecording()
    {
        ThrowIfDisposed();
        if (_running) return;

        if (Format == null) Init(null);

        _chunkBytes = AudioRecorderExtensions.GetChunkBytes(Format);

        // 至少留两块，避免 BufferSeconds=0 时一点调度抖动就丢数据
        var byteRate = Math.Max(Format.ByteRate, 1);
        _capacityBytes = Math.Max(_chunkBytes * 2L, (Int64)byteRate * Math.Max(0, BufferSeconds));

        lock (_lock)
        {
            foreach (var frame in _queue) frame.Dispose();
            _queue.Clear();
            _queuedBytes = 0;
        }

        Open();

        _running = true;

        // 拉式硬件：起一条线程按时把硬件数据取出来；推式硬件由驱动在回调里 Push
        if (!PushMode)
        {
            _thread = new Thread(Pump)
            {
                IsBackground = true,
                Name = GetType().Name + "Pump",
            };
            _thread.Start();
        }
    }

    /// <summary>停止录制（队列中尚未取走的数据仍可继续读取）</summary>
    public void StopRecording()
    {
        if (!_running) return;

        _running = false;

        // 唤醒可能正在等待数据的消费者
        _signal.Release();

        // 先等采集线程退出（它在 ReadNative 内最多等一个周期），再关设备，避免关句柄时仍有原生读取
        var thread = _thread;
        _thread = null;
        if (thread != null && thread != Thread.CurrentThread && thread.IsAlive) thread.Join(1000);

        try
        {
            Close();
        }
        catch (Exception ex)
        {
            XTrace.Log.InfoPrefixed($"{GetType().Name}: ", "Close failed: {0}", ex.Message);
        }
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopRecording();

        lock (_lock)
        {
            foreach (var frame in _queue) frame.Dispose();
            _queue.Clear();
            _queuedBytes = 0;
        }

        _signal.Dispose();
    }
    #endregion

    #region 读取
    /// <summary>同步读取一段 PCM</summary>
    /// <param name="buffer">目标缓冲</param>
    /// <param name="timeoutMilliseconds">等待超时时间（毫秒），小于等于 0 表示一直等</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>实际写入的字节数；0 表示超时或已停止</returns>
    public Int32 Read(Span<Byte> buffer, Int32 timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (buffer.Length == 0) return 0;

        var watch = timeoutMilliseconds > 0 ? Stopwatch.StartNew() : null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var frame = TryDequeue();
            if (frame != null) return CopyFrame(frame, buffer);

            if (!_running) return 0;
            if (watch != null && watch.ElapsedMilliseconds >= timeoutMilliseconds) return 0;

            _signal.Wait(100, cancellationToken);
        }
    }

    /// <summary>异步读取一段 PCM（等队列信号，不占用线程）</summary>
    /// <param name="buffer">目标缓冲</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>实际写入的字节数；0 表示已停止</returns>
    public async ValueTask<Int32> ReadAsync(Memory<Byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (buffer.Length == 0) return 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var frame = TryDequeue();
            if (frame != null) return CopyFrame(frame, buffer.Span);

            if (!_running) return 0;

            // 等的是“队列有新数据”的信号，不是硬件，所以这里挂起而不占线程
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
    #endregion

    #region 供子类使用
    /// <summary>打开并启动硬件采集</summary>
    protected abstract void Open();

    /// <summary>停止并释放硬件采集</summary>
    protected abstract void Close();

    /// <summary>阻塞读取一块硬件 PCM（拉式硬件实现；推送式硬件返回 0 即可）</summary>
    /// <param name="buffer">目标缓冲</param>
    /// <param name="timeoutMilliseconds">等待超时时间（毫秒）</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>实际写入的字节数；0 表示超时或已停止</returns>
    protected virtual Int32 ReadNative(Span<Byte> buffer, Int32 timeoutMilliseconds, CancellationToken cancellationToken) => 0;

    /// <summary>推送一块 PCM（推送式硬件在回调中调用）</summary>
    /// <param name="pcm">16 位小端交错 PCM</param>
    protected void Push(ReadOnlySpan<Byte> pcm)
    {
        if (pcm.Length == 0 || !_running) return;

        var frame = AudioFrame.Rent(Format, pcm.Length);
        pcm.CopyTo(frame.GetMemory().Span);

        Enqueue(frame);
    }

    /// <summary>记录一次硬件缓冲覆盖/欠载</summary>
    protected void AddOverrun() => Interlocked.Increment(ref _overruns);

    /// <summary>由驱动主动结束采集（设备拔出、重连失败等），消费者随后会读到 0</summary>
    protected void StopCapture()
    {
        _running = false;
        _signal.Release();
    }
    #endregion

    #region 辅助
    /// <summary>采集循环：按时从硬件取数据并排队（仅拉式硬件使用）</summary>
    private void Pump()
    {
        while (_running)
        {
            var frame = AudioFrame.Rent(Format, _chunkBytes);
            Int32 read;
            try
            {
                read = ReadNative(frame.GetMemory().Span, 100, CancellationToken.None);
            }
            catch (Exception ex)
            {
                frame.Dispose();
                if (!_running) break;

                XTrace.Log.InfoPrefixed($"{GetType().Name}: ", "Read failed: {0}", ex.Message);
                Thread.Sleep(10);
                continue;
            }

            if (read <= 0)
            {
                frame.Dispose();
                if (!_running) break;

                continue;
            }

            frame.Count = read;
            Enqueue(frame);
        }
    }

    private void Enqueue(AudioFrame frame)
    {
        EventHandler<Byte[]> handler;
        lock (_lock)
        {
            _queue.Enqueue(frame);
            _queuedBytes += frame.Length;

            // 容量封顶：丢最旧，至少保留刚采到的这一块，保证消费者总能拿到最新数据
            while (_queuedBytes > _capacityBytes && _queue.Count > 1)
            {
                var dropped = _queue.Dequeue();
                _queuedBytes -= dropped.Length;
                Interlocked.Add(ref _droppedBytes, dropped.Length);
                dropped.Dispose();
            }

            handler = DataAvailable;
        }

        _signal.Release();

        // 旁路观察：给订阅者一份副本，异常不影响采集
        if (handler != null)
        {
            var copy = new Byte[frame.Length];
            frame.GetSpan().CopyTo(copy);

            try
            {
                handler(this, copy);
            }
            catch (Exception ex)
            {
                XTrace.Log.InfoPrefixed($"{GetType().Name}: ", "Data handler failed: {0}", ex.Message);
            }
        }
    }

    private AudioFrame TryDequeue()
    {
        lock (_lock)
        {
            if (_queue.Count == 0) return null;

            var frame = _queue.Dequeue();
            _queuedBytes -= frame.Length;

            return frame;
        }
    }

    private static Int32 CopyFrame(AudioFrame frame, Span<Byte> buffer)
    {
        try
        {
            var count = Math.Min(frame.Length, buffer.Length);
            frame.GetSpan()[..count].CopyTo(buffer);

            return count;
        }
        finally
        {
            frame.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }
    #endregion
}
