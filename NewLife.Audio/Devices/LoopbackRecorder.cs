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

    /// <summary>数据可用事件（请求格式的 PCM 字节）</summary>
    public event EventHandler<Byte[]> DataAvailable
    {
        add
        {
            _handler += value;
            _recorder.DataAvailable += value;
        }
        remove
        {
            _handler -= value;
            _recorder.DataAvailable -= value;
        }
    }

    /// <summary>触发一次数据可用回调</summary>
    /// <param name="pcm">请求格式的 PCM 字节</param>
    /// <remarks>保留自早期版本的公开入口，供自定义采集循环拿到 PCM 后主动分发。</remarks>
    public void OnDataAvailable(Byte[] pcm) => _handler?.Invoke(this, pcm);

    /// <summary>音频格式</summary>
    public AudioFormat Format => _format ?? _recorder.Format;

    /// <summary>是否正在录制</summary>
    public Boolean IsRecording => _recorder.IsRecording;

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

    /// <summary>释放资源</summary>
    public void Dispose() => _recorder.Dispose();
}
