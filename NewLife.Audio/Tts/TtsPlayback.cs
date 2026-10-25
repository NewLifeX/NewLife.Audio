using NewLife.Audio.Devices;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts;

/// <summary>PCM 播放回退。用于不支持直接播报的引擎</summary>
internal sealed class TtsPlayback : IDisposable
{
    private readonly IAudioPlayer _player;
    private AudioFormat _format;
    private Boolean _started;
    private Boolean _disposed;

    /// <summary>已写入的 PCM 字节数</summary>
    public Int64 BytesWritten { get; private set; }

    public TtsPlayback(IAudioPlayer player) => _player = player ?? throw new ArgumentNullException(nameof(player));

    public void Write(TtsChunk chunk)
    {
        if (chunk?.Data == null || chunk.Data.Length == 0) return;

        if (!_started)
        {
            _format = chunk.Format?.Clone() ?? AudioFormat.Default;
            _player.Init(_format);
            _player.Play();
            _started = true;
        }

        _player.Write(chunk.Data);
        BytesWritten += chunk.Data.Length;
    }

    /// <summary>等待写入的音频播完</summary>
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        var rate = _format?.ByteRate ?? 0;
        if (rate <= 0 || BytesWritten <= 0) return;

        var ms = BytesWritten * 1000 / rate;
        if (ms <= 0) return;

        await Task.Delay((Int32)Math.Min(ms, Int32.MaxValue), cancellationToken).ConfigureAwait(false);
    }

    public void Stop()
    {
        try { _player.Stop(); } catch { /* 已停止 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _player.Dispose();
    }
}
