using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NewLife.Audio;
using NewLife.Audio.Devices;

namespace AudioStudio.Services;

/// <summary>MIDI 渲染后的内存 PCM 播放服务</summary>
public partial class MidiPlaybackService : ObservableObject, IDisposable
{
    private IAudioPlayer? _player;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private AudioPcm? _pcm;
    private Int32 _offsetSamples;
    private Int64 _writtenSamples;
    private Boolean _disposed;

    /// <summary>播放状态</summary>
    [ObservableProperty]
    private PlaybackState _state = PlaybackState.Stopped;

    /// <summary>当前播放位置（秒）</summary>
    [ObservableProperty]
    private Double _position;

    /// <summary>总时长（秒）</summary>
    [ObservableProperty]
    private Double _duration;

    /// <summary>音量 0.0~1.0</summary>
    [ObservableProperty]
    private Double _volume = 0.8;

    /// <summary>播放自然结束</summary>
    public event EventHandler? PlaybackEnded;

    /// <summary>是否已加载 PCM</summary>
    public Boolean IsLoaded => _pcm != null;

    /// <summary>加载 PCM，重置位置</summary>
    /// <param name="pcm">已渲染的 PCM</param>
    public void Load(AudioPcm pcm)
    {
        Stop();

        _pcm = pcm;
        Duration = pcm.Duration;
        Position = 0;
        _offsetSamples = 0;
        _writtenSamples = 0;
    }

    /// <summary>播放</summary>
    public void Play()
    {
        if (_pcm == null || _pcm.Data.Length == 0) return;

        if (State == PlaybackState.Paused)
        {
            _player?.Play();
            State = PlaybackState.Playing;
            return;
        }

        if (State == PlaybackState.Playing) return;

        var driver = AudioDriverFactory.Create();
        _player = driver.CreatePlayer();
        _player.Init(_pcm.Format);
        _player.Play();

        _writtenSamples = 0;
        State = PlaybackState.Playing;

        _cts = new CancellationTokenSource();
        _thread = new Thread(PlaybackLoop)
        {
            Name = "MidiPlayback",
            IsBackground = true,
        };
        _thread.Start(_cts.Token);
    }

    /// <summary>暂停</summary>
    public void Pause()
    {
        if (State != PlaybackState.Playing) return;

        _player?.Pause();
        State = PlaybackState.Paused;
    }

    /// <summary>停止并回到开头</summary>
    public void Stop() => StopInternal(true);

    /// <summary>跳转到指定秒数</summary>
    /// <param name="seconds">目标秒数</param>
    public void Seek(Double seconds)
    {
        if (_pcm == null) return;

        if (seconds < 0) seconds = 0;
        if (seconds > Duration) seconds = Duration;

        var wasPlaying = State == PlaybackState.Playing;
        StopInternal(false);

        _offsetSamples = (Int32)Math.Round(seconds * _pcm.Format.SampleRate);
        _writtenSamples = 0;
        Position = seconds;

        if (wasPlaying) Play();
    }

    private void PlaybackLoop(Object? state)
    {
        var token = (CancellationToken)state!;
        var pcm = _pcm;
        var player = _player;
        if (pcm == null || player == null) return;

        var format = pcm.Format;
        var bytesPerFrame = format.Channels * 2;
        var chunkSamples = Math.Max(1, format.SampleRate / 20);
        var chunkBytes = chunkSamples * bytesPerFrame;
        var limitBytes = Math.Max(1, format.SampleRate / 2) * bytesPerFrame;
        var offset = _offsetSamples * bytesPerFrame;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (State == PlaybackState.Paused)
                {
                    Thread.Sleep(20);
                    continue;
                }

                if (State != PlaybackState.Playing) break;

                var remaining = pcm.Data.Length - offset;
                if (remaining <= 0) break;

                var count = Math.Min(chunkBytes, remaining);
                var buffer = new Byte[count];
                Array.Copy(pcm.Data, offset, buffer, 0, count);
                ApplyVolume(buffer, Volume);
                player.Write(buffer);

                offset += count;
                _writtenSamples += count / bytesPerFrame;
                UpdatePosition(format, bytesPerFrame, player);

                while (!token.IsCancellationRequested && player.BufferedBytes > limitBytes) Thread.Sleep(5);
            }

            if (!token.IsCancellationRequested)
            {
                while (player.BufferedBytes > 0) Thread.Sleep(20);

                Dispatcher.UIThread.Post(() =>
                {
                    if (State != PlaybackState.Playing) return;

                    StopInternal(true);
                    PlaybackEnded?.Invoke(this, EventArgs.Empty);
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MIDI 播放异常: {ex.Message}");
        }
    }

    private void UpdatePosition(NewLife.Audio.DSP.AudioFormat format, Int32 bytesPerFrame, IAudioPlayer player)
    {
        var played = _writtenSamples - player.BufferedBytes / bytesPerFrame;
        if (played < 0) played = 0;

        var seconds = (_offsetSamples + played) / (Double)format.SampleRate;
        if (seconds > Duration) seconds = Duration;
        Position = seconds;
    }

    private static void ApplyVolume(Byte[] pcmData, Double volume)
    {
        if (pcmData.Length < 2 || Math.Abs(volume - 1.0) < 0.01) return;

        var samples = MemoryMarshal.Cast<Byte, Int16>(new Span<Byte>(pcmData));
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = (Int32)(samples[i] * volume);
            if (sample > Int16.MaxValue) sample = Int16.MaxValue;
            if (sample < Int16.MinValue) sample = Int16.MinValue;
            samples[i] = (Int16)sample;
        }
    }

    private void StopInternal(Boolean resetPosition)
    {
        _cts?.Cancel();
        _cts = null;

        try
        {
            _player?.Stop();
        }
        catch
        {
            // 播放器可能已释放
        }

        _player?.Dispose();
        _player = null;

        if (_thread != null && _thread.IsAlive && _thread != Thread.CurrentThread) _thread.Join(200);
        _thread = null;

        State = PlaybackState.Stopped;
        _writtenSamples = 0;
        if (resetPosition)
        {
            _offsetSamples = 0;
            Position = 0;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _pcm = null;
    }
}
