using NewLife;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Devices;

/// <summary>环路录制器（系统音频输出捕获，WASAPI Loopback）</summary>
/// <remarks>
/// 通过 WASAPI 渲染端点 + <c>AUDCLNT_STREAMFLAGS_LOOPBACK</c> 捕获系统播放的声音（扬声器混音），
/// 并按请求格式转换采样率、声道与位深后通过 <see cref="DataAvailable"/> 输出 PCM。<br/>
/// 非 Windows 平台或设备不可用时 <see cref="StartRecording"/> 明确抛出异常，不再输出模拟数据。
/// </remarks>
/// <example>
/// <code>
/// using var recorder = new LoopbackRecorder();
/// recorder.Init(new AudioFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 });
/// recorder.DataAvailable += (s, pcm) =&gt; Console.WriteLine(pcm.Length);
/// recorder.StartRecording();
/// </code>
/// </example>
public class LoopbackRecorder : IAudioRecorder
{
    private readonly WasapiRecorder _recorder = new(true);
    private AudioFormat _format;

    private EventHandler<Byte[]> _handler;

    /// <summary>构造环路录制器</summary>
    public LoopbackRecorder()
    {
        // 事件统一从内层录制器转发，避免重复订阅
        _recorder.DataAvailable += OnInnerData;
    }

    /// <summary>创建环路录制器实例</summary>
    /// <returns>WASAPI loopback 录制器，调用方负责释放</returns>
    public static IAudioRecorder CreateRecorder() => new WasapiRecorder(true);

    /// <summary>数据可用事件（请求格式的 PCM 字节）</summary>
    public event EventHandler<Byte[]> DataAvailable
    {
        add => _handler += value;
        remove => _handler -= value;
    }

    /// <summary>触发一次数据可用回调</summary>
    /// <param name="pcm">请求格式的 PCM 字节</param>
    /// <remarks>保留自早期版本的公开入口，供自定义采集循环拿到 PCM 后主动分发。</remarks>
    public void OnDataAvailable(Byte[] pcm) => _handler?.Invoke(this, pcm);

    /// <summary>音频格式</summary>
    public AudioFormat Format => _format ?? _recorder.Format;

    /// <summary>是否正在录制</summary>
    public Boolean IsRecording => _recorder.IsRecording;

    /// <summary>缓冲秒数，默认 10；0 表示只保留最小缓冲</summary>
    public Int32 BufferSeconds
    {
        get => _recorder.BufferSeconds;
        set => _recorder.BufferSeconds = value;
    }

    /// <summary>当前排队等待消费的字节数</summary>
    public Int64 BufferedBytes => _recorder.BufferedBytes;

    /// <summary>因队列容量满而丢弃的字节数</summary>
    public Int64 DroppedBytes => _recorder.DroppedBytes;

    /// <summary>硬件设备缓冲被覆盖/欠载的次数</summary>
    public Int64 Overruns => _recorder.Overruns;

    /// <summary>当前平台是否支持系统声音回采</summary>
    public static Boolean IsSupported => WasapiDriver.IsPlatformSupported;

    /// <summary>初始化环路录制器</summary>
    /// <param name="format">请求的 PCM 格式</param>
    /// <param name="deviceId">渲染端点 ID；为空时使用默认扬声器</param>
    public void Init(AudioFormat format, String deviceId = null)
    {
        _format = format?.Clone() ?? new AudioFormat
        {
            SampleRate = 44100,
            Channels = 2,
            BitsPerSample = 16,
        };
        _recorder.Init(_format, deviceId);
    }

    /// <summary>开始录制</summary>
    /// <exception cref="PlatformNotSupportedException">非 Windows 平台</exception>
    /// <exception cref="InvalidOperationException">没有可用的渲染端点</exception>
    public void StartRecording()
    {
        if (!WasapiDriver.IsPlatformSupported) throw new PlatformNotSupportedException(L.T("No audio backend is available."));
        if (_format == null) Init(null);

        _recorder.StartRecording();
    }

    /// <summary>停止录制</summary>
    public void StopRecording() => _recorder.StopRecording();

    /// <summary>同步读取一段 PCM</summary>
    /// <param name="buffer">目标缓冲</param>
    /// <param name="timeoutMilliseconds">等待超时时间（毫秒）</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>实际写入的字节数</returns>
    public Int32 Read(Span<Byte> buffer, Int32 timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        _recorder.Read(buffer, timeoutMilliseconds, cancellationToken);

    /// <summary>异步读取一段 PCM</summary>
    /// <param name="buffer">目标缓冲</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>实际写入的字节数</returns>
    public ValueTask<Int32> ReadAsync(Memory<Byte> buffer, CancellationToken cancellationToken = default) =>
        _recorder.ReadAsync(buffer, cancellationToken);

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        _recorder.DataAvailable -= OnInnerData;
        _recorder.Dispose();
    }

    private void OnInnerData(Object sender, Byte[] pcm) => _handler?.Invoke(this, pcm);
}
