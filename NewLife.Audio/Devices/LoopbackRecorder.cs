using NewLife.Audio.DSP;

namespace NewLife.Audio.Devices;

/// <summary>环路录制器（系统音频输出捕获）</summary>
public class LoopbackRecorder : IAudioRecorder, IDisposable
{
    private AudioFormat _format;
    private Boolean _isRecording;

    public event EventHandler<Byte[]> DataAvailable;
    public AudioFormat Format => _format;
    public Boolean IsRecording => _isRecording;

    public void Init(AudioFormat format, String deviceId = null)
    {
        _format = format ?? AudioFormat.Default;
    }

    public void StartRecording() => _isRecording = true;
    public void StopRecording() => _isRecording = false;
    public void Dispose() => StopRecording();

    protected void OnDataAvailable(Byte[] data) => DataAvailable?.Invoke(this, data);
}
