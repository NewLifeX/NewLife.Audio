using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio.Containers;
using NewLife.Audio.Devices;
using NewLife.Audio.DSP;

namespace NewLife.Audio;

/// <summary>播放门面</summary>
/// <remarks>
/// 把“播放一个音频文件”变成一次调用：门面内部完成解码、分块、按积压水位等待与收尾等待，
/// 调用方不需要关心 <see cref="IAudioPlayer"/> 的分块大小与 <c>BufferedBytes</c> 轮询。<br/>
/// 支持任意可解码格式（WAV/FLAC/MP3/AIFF/OGG/MP4/CAF…），音量按 16 位样本实时缩放。
/// </remarks>
/// <example>
/// <code>
/// // 播完才返回
/// AudioPlayer.PlayFile("record.mp3");
///
/// // 需要暂停/进度时用实例
/// using var player = new AudioPlayer { Volume = 0.6 };
/// var task = player.PlayFileAsync("record.mp3", ct);
/// player.Pause();
/// player.Resume();
/// </code>
/// </example>
public class AudioPlayer : IDisposable
{
    /// <summary>流式解码的默认分块字节数</summary>
    private const Int32 DefaultChunkBytes = 64 * 1024;

    private readonly IAudioDriver _driver;
    private readonly String _deviceId;
    private readonly Object _sync = new();
    private IAudioPlayer _player;
    private AudioPlaybackSession _session;
    private Double _position;
    private Boolean _disposed;

    #region 属性
    /// <summary>音量，0~1，默认 1（原始音量）</summary>
    public Double Volume { get; set; } = 1.0;

    /// <summary>当前音频总时长（秒）</summary>
    public Double Duration { get; private set; }

    /// <summary>当前播放位置（秒）</summary>
    public Double Position
    {
        get
        {
            lock (_sync) return _session != null ? _session.Position : _position;
        }
    }

    /// <summary>是否正在播放</summary>
    public Boolean IsPlaying
    {
        get
        {
            lock (_sync) return _player != null;
        }
    }

    /// <summary>PCM 数据可用（每块送入播放器前触发，供频谱/波形使用）</summary>
    public event EventHandler<Byte[]> PcmDataAvailable;
    #endregion

    #region 构造
    /// <summary>构造播放门面</summary>
    /// <param name="driver">音频驱动，为空时自动选择平台最优后端；测试可注入假驱动</param>
    /// <param name="deviceId">输出设备 ID，为空时使用默认设备</param>
    public AudioPlayer(IAudioDriver driver = null, String deviceId = null)
    {
        _driver = driver ?? AudioDriverFactory.Create();
        _deviceId = deviceId;
    }
    #endregion

    #region 播放
    /// <summary>播放文件并返回（播完才返回）</summary>
    /// <param name="path">音频文件路径</param>
    /// <param name="cancellationToken">取消标记</param>
    public void PlayFile(String path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (path == null) throw new ArgumentNullException(nameof(path));

        var session = CreateSession(path, out var player);
        try
        {
            session.Play(cancellationToken);
        }
        finally
        {
            FinishSession(session, player);
        }
    }

    /// <summary>跳转到指定时间位置（秒），仅对文件播放有效</summary>
    /// <param name="seconds">目标位置（秒）</param>
    /// <returns>是否成功跳转</returns>
    public Boolean Seek(Double seconds)
    {
        AudioPlaybackSession session;
        lock (_sync) session = _session;

        return session != null && session.Seek(seconds);
    }

    /// <summary>播放文件（不占用调用方线程）</summary>
    /// <param name="path">音频文件路径</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>播完或取消后完成的任务</returns>
    /// <remarks>文件打开与解码会话建立是同步的，返回任务后即可调用 <see cref="Seek"/>。</remarks>
    public Task PlayFileAsync(String path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (path == null) throw new ArgumentNullException(nameof(path));
        cancellationToken.ThrowIfCancellationRequested();

        // 同步建立会话，避免调用方拿到任务后立即 Seek 时 _session 仍为空
        var session = CreateSession(path, out var player);

        return Task.Run(() =>
        {
            try
            {
                session.Play(cancellationToken);
            }
            finally
            {
                FinishSession(session, player);
            }
        }, CancellationToken.None);
    }

    /// <summary>播放一段 PCM（播完才返回）</summary>
    /// <param name="audio">PCM 音频</param>
    /// <param name="cancellationToken">取消标记</param>
    public void Play(AudioPcm audio, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (audio == null) throw new ArgumentNullException(nameof(audio));

        Duration = audio.Duration;
        PlayCore([audio], cancellationToken);
    }

    /// <summary>播放一段 PCM（不占用调用方线程）</summary>
    /// <param name="audio">PCM 音频</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>播完或取消后完成的任务</returns>
    public Task PlayAsync(AudioPcm audio, CancellationToken cancellationToken = default) =>
        Task.Run(() => Play(audio, cancellationToken), cancellationToken);

    /// <summary>播放数据流（播完才返回）</summary>
    /// <param name="stream">音频数据流，必须可定位</param>
    /// <param name="info">探测信息；为空时自动探测</param>
    /// <param name="cancellationToken">取消标记</param>
    public void Play(Stream stream, AudioStreamInfo info = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (stream == null) throw new ArgumentNullException(nameof(stream));

        info ??= AudioProbe.Probe(stream);
        Duration = info.Duration;
        PlayCore(AudioFile.StreamPcm(stream, info, DefaultChunkBytes), cancellationToken);
    }

    /// <summary>暂停播放</summary>
    public void Pause()
    {
        lock (_sync) _player?.Pause();
    }

    /// <summary>恢复播放</summary>
    public void Resume()
    {
        lock (_sync) _player?.Play();
    }

    /// <summary>停止播放</summary>
    public void Stop()
    {
        lock (_sync) _player?.Stop();
    }

    /// <summary>释放播放门面</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        IAudioPlayer player;
        lock (_sync)
        {
            player = _player;
            _player = null;
        }
        player?.Dispose();
    }
    #endregion

    #region 静态捷径
    /// <summary>播放文件（播完才返回）</summary>
    /// <param name="path">音频文件路径</param>
    /// <param name="volume">音量，0~1</param>
    public static void PlayFile(String path, Double volume = 1.0)
    {
        using var player = new AudioPlayer { Volume = volume };

        player.PlayFile(path);
    }

    /// <summary>播放文件（不占用调用方线程）</summary>
    /// <param name="path">音频文件路径</param>
    /// <param name="volume">音量，0~1</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>播完或取消后完成的任务</returns>
    public static Task PlayFileAsync(String path, Double volume = 1.0, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            using var player = new AudioPlayer { Volume = volume };

            player.PlayFile(path, cancellationToken);
        }, cancellationToken);
    }
    #endregion

    #region 辅助
    /// <summary>建立文件播放会话并登记到门面</summary>
    /// <param name="path">音频文件路径</param>
    /// <param name="player">创建出的播放器</param>
    /// <returns>播放会话</returns>
    private AudioPlaybackSession CreateSession(String path, out IAudioPlayer player)
    {
        lock (_sync)
        {
            if (_session != null) throw new InvalidOperationException("A playback session is already active.");

            player = _driver.CreatePlayer();
            try
            {
                var session = new AudioPlaybackSession(path, player, new PlayerRenderAdapter(player), _deviceId, Volume);
                _session = session;
                _player = player;
                Duration = session.Duration;
                session.PcmDataAvailable += OnPcmDataAvailable;

                return session;
            }
            catch
            {
                player.Dispose();
                player = null;
                throw;
            }
        }
    }

    /// <summary>结束会话并清理播放器</summary>
    /// <param name="session">播放会话</param>
    /// <param name="player">播放器</param>
    private void FinishSession(AudioPlaybackSession session, IAudioPlayer player)
    {
        session.PcmDataAvailable -= OnPcmDataAvailable;

        // 播放结束后保留最后位置，便于 UI 显示
        var position = session.Position;
        session.Dispose();

        lock (_sync)
        {
            _position = position;
            if (ReferenceEquals(_session, session)) _session = null;
            if (ReferenceEquals(_player, player)) _player = null;
        }

        player.Dispose();
    }

    /// <summary>逐块送入播放器并等待播完</summary>
    /// <param name="chunks">PCM 分块</param>
    /// <param name="cancellationToken">取消标记</param>
    private void PlayCore(IEnumerable<AudioPcm> chunks, CancellationToken cancellationToken)
    {
        var player = _driver.CreatePlayer();
        lock (_sync)
        {
            _player = player;
            _position = 0;
        }

        var render = new PlayerRenderAdapter(player);
        Byte[] volumeBuffer = null;

        try
        {
            var started = false;
            var watermark = 0;
            var chunkBytes = 0;
            var byteRate = 0;

            foreach (var chunk in chunks)
            {
                var data = chunk.Data;
                if (data == null || data.Length == 0) continue;

                var format = chunk.Format ?? AudioFormat.Default;
                if (!started)
                {
                    player.Init(format, _deviceId);
                    player.Play();

                    byteRate = Math.Max(1, format.ByteRate);
                    watermark = Math.Max(byteRate / 2, 1);

                    var frameBytes = Math.Max(1, format.Channels * format.BytesPerSample);
                    chunkBytes = Math.Max(frameBytes, byteRate / 10 / frameBytes * frameBytes);

                    started = true;
                }

                for (var offset = 0; offset < data.Length; offset += chunkBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var count = Math.Min(chunkBytes, data.Length - offset);
                    ReadOnlySpan<Byte> span = data.AsSpan(offset, count);
                    if (Volume >= 0 && Math.Abs(Volume - 1) > 0.0001)
                    {
                        volumeBuffer ??= new Byte[chunkBytes];
                        ApplyVolume(span, volumeBuffer.AsSpan(0, count), Volume);
                        span = volumeBuffer.AsSpan(0, count);
                    }

                    while (!render.WaitForSpace(watermark, 500))
                        cancellationToken.ThrowIfCancellationRequested();

                    // 频谱/波形订阅者拿独立副本，后续写入不再改动它
                    if (PcmDataAvailable != null) PcmDataAvailable.Invoke(this, span.ToArray());

                    render.Write(span);

                    lock (_sync) _position += (Double)count / byteRate;
                }
            }

            // 等驱动把缓冲区播完；无输出设备时缓冲不会下降，由取消兜底
            while (render.BufferedBytes > 0)
            {
                if (cancellationToken.IsCancellationRequested) break;
                Thread.Sleep(20);
            }
        }
        finally
        {
            try
            {
                player.Stop();
            }
            catch
            {
                // 设备已失效时 Stop 可能失败，忽略
            }

            player.Dispose();
            lock (_sync)
            {
                if (ReferenceEquals(_player, player)) _player = null;
            }
        }
    }

    /// <summary>按音量缩放 16 位小端 PCM</summary>
    /// <param name="source">源 PCM</param>
    /// <param name="target">目标缓冲，长度与源一致</param>
    /// <param name="volume">音量，0~1</param>
    private static void ApplyVolume(ReadOnlySpan<Byte> source, Span<Byte> target, Double volume)
    {
        var gain = Math.Max(0, Math.Min(1, volume));
        var count = Math.Min(source.Length, target.Length);

        for (var i = 0; i + 1 < count; i += 2)
        {
            var sample = (Int16)(source[i] | source[i + 1] << 8);
            var scaled = (Int32)(sample * gain);
            if (scaled > Int16.MaxValue) scaled = Int16.MaxValue;
            else if (scaled < Int16.MinValue) scaled = Int16.MinValue;

            target[i] = (Byte)(scaled & 0xFF);
            target[i + 1] = (Byte)((scaled >> 8) & 0xFF);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AudioPlayer));
    }

    private void OnPcmDataAvailable(Object sender, Byte[] pcm) => PcmDataAvailable?.Invoke(this, pcm);
    #endregion

    #region 播放适配
    /// <summary>把 <see cref="IAudioPlayer"/> 适配成带背压的 <see cref="IAudioRender"/></summary>
    private sealed class PlayerRenderAdapter : IAudioRender
    {
        private readonly IAudioPlayer _player;

        public PlayerRenderAdapter(IAudioPlayer player) => _player = player;

        public Int32 BufferedBytes => _player.BufferedBytes;

        public void Write(ReadOnlySpan<Byte> data)
        {
            if (_player is IAudioRender render)
            {
                render.Write(data);
                return;
            }

            _player.Write(data.ToArray());
        }

        public Boolean WaitForSpace(Int32 maxBufferedBytes, Int32 timeoutMilliseconds)
        {
            if (_player is IAudioRender render) return render.WaitForSpace(maxBufferedBytes, timeoutMilliseconds);

            // 第三方播放器没有背压信号，只能按积压轮询
            var watch = Stopwatch.StartNew();
            while (_player.BufferedBytes > maxBufferedBytes)
            {
                if (timeoutMilliseconds >= 0 && watch.ElapsedMilliseconds >= timeoutMilliseconds) return false;

                Thread.Sleep(5);
            }

            return true;
        }
    }
    #endregion
}
