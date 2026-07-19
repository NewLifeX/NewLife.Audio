using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Audio.Containers;
using NewLife.Audio.Devices;
using NewLife.Audio.DSP;
using NewLife.Data;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioStudio.Services;

/// <summary>播放状态</summary>
public enum PlaybackState
{
    /// <summary>停止</summary>
    Stopped,

    /// <summary>播放中</summary>
    Playing,

    /// <summary>暂停</summary>
    Paused
}

/// <summary>音频播放服务。封装 NewLife.Audio 的播放管线</summary>
public partial class AudioPlayerService : ObservableObject, IDisposable
{
    #region 属性
    private IAudioPlayer? _player;
    private IAudioContainerReader? _reader;
    private IAudioCodec? _codec;
    private CancellationTokenSource? _cts;
    private Thread? _playbackThread;
    private Int64 _currentFrame;
    private Boolean _disposed;

    /// <summary>播放状态</summary>
    [ObservableProperty]
    private PlaybackState _state = PlaybackState.Stopped;

    /// <summary>当前播放位置（秒）</summary>
    [ObservableProperty]
    private Double _position;

    /// <summary>音频总时长（秒）</summary>
    [ObservableProperty]
    private Double _duration;

    /// <summary>音量 0.0~1.0</summary>
    [ObservableProperty]
    private Double _volume = 0.8;

    /// <summary>当前文件路径</summary>
    [ObservableProperty]
    private String? _filePath;

    /// <summary>当前文件名</summary>
    [ObservableProperty]
    private String? _fileName;

    /// <summary>音频格式</summary>
    [ObservableProperty]
    private AudioFormat? _audioFormat;

    /// <summary>元数据</summary>
    [ObservableProperty]
    private AudioMetadata? _metadata;

    /// <summary>编码类型</summary>
    [ObservableProperty]
    private AVTypes _codecType;

    /// <summary>总帧数</summary>
    [ObservableProperty]
    private Int64 _totalFrames;

    /// <summary>当前帧索引</summary>
    [ObservableProperty]
    private Int64 _currentFrameIndex;

    /// <summary>是否可以播放</summary>
    public Boolean CanPlay => State == PlaybackState.Stopped || State == PlaybackState.Paused;

    /// <summary>是否可以暂停</summary>
    public Boolean CanPause => State == PlaybackState.Playing;

    /// <summary>是否可以停止</summary>
    public Boolean CanStop => State != PlaybackState.Stopped;

    /// <summary>是否有文件打开</summary>
    public Boolean IsFileOpen => _reader != null;

    /// <summary>PCM 数据可用事件（用于频谱分析等外部消费）</summary>
    public event EventHandler<Byte[]>? PcmDataAvailable;

    partial void OnStateChanged(PlaybackState value)
    {
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanStop));
    }
    #endregion

    #region 方法
    /// <summary>打开音频文件</summary>
    public void Open(String filePath)
    {
        Stop();

        CloseReader();

        try
        {
            _reader = AudioContainerFactory.CreateReader(filePath);
            _audioFormat = _reader.Format;
            _codecType = _reader.CodecType;
            _duration = _reader.Duration;
            _totalFrames = _reader.TotalFrames;
            _metadata = _reader.Metadata;
            _filePath = filePath;
            _fileName = Path.GetFileName(filePath);
            _currentFrame = 0;
            _currentFrameIndex = 0;
            _position = 0;

            // 获取编解码器
            if (AudioServiceHelper.CodecRegistry.IsSupported(_codecType))
                _codec = AudioServiceHelper.CodecRegistry.GetCodec(_codecType);
            else
                _codec = null;
        }
        catch (Exception ex)
        {
            CloseReader();
            throw new InvalidOperationException($"无法打开音频文件: {filePath}", ex);
        }
    }

    /// <summary>开始播放</summary>
    public void Play()
    {
        if (_reader == null) return;

        if (State == PlaybackState.Paused)
        {
            _player?.Play();
            State = PlaybackState.Playing;
            return;
        }

        if (State == PlaybackState.Playing) return;

        // 创建播放器
        var backend = AudioBackendFactory.Create();
        _player = backend.CreatePlayer();
        _player.Init(_audioFormat!);
        _player.PlaybackStopped += OnPlaybackStopped;

        _player.Play();

        State = PlaybackState.Playing;

        // 启动后台播放线程
        _cts = new CancellationTokenSource();
        _playbackThread = new Thread(PlaybackLoop)
        {
            Name = "AudioPlayback",
            IsBackground = true
        };
        _playbackThread.Start(_cts.Token);
    }

    /// <summary>暂停播放</summary>
    public void Pause()
    {
        if (State != PlaybackState.Playing) return;

        _player?.Pause();
        State = PlaybackState.Paused;
    }

    /// <summary>停止播放</summary>
    public void Stop()
    {
        StopPlayback();
    }

    /// <summary>跳转到指定时间位置（秒）</summary>
    public void Seek(Double seconds)
    {
        if (_reader == null || _totalFrames <= 0) return;

        var frameIndex = (Int64)(seconds / _duration * _totalFrames);
        frameIndex = Math.Clamp(frameIndex, 0, _totalFrames - 1);

        _reader.SeekFrame(frameIndex);
        _currentFrame = frameIndex;
        _currentFrameIndex = frameIndex;
        Position = seconds;

        // 如果正在播放，清空播放器缓冲区
        if (State == PlaybackState.Playing)
        {
            _player?.Stop();
            _player?.Play();
        }
    }
    #endregion

    #region 内部方法
    private void PlaybackLoop(Object? state)
    {
        var token = (CancellationToken)state!;
        var reader = _reader;
        var codec = _codec;
        var player = _player;
        if (reader == null || player == null) return;

        try
        {
            while (!token.IsCancellationRequested && State == PlaybackState.Playing)
            {
                // 读取一帧编码数据
                var packet = reader.ReadFrame();
                if (packet == null) break; // 播放结束

                // 解码为 PCM
                Byte[] pcmData;
                if (codec != null)
                {
                    var pcmPacket = codec.ToPcm(packet.GetSpan(), null);
                    pcmData = pcmPacket.ToArray();
                }
                else
                {
                    // 无编解码器时直接透传（PCM 容器）
                    pcmData = packet.ToArray();
                }

                // 应用音量
                if (Math.Abs(_volume - 1.0) > 0.01)
                {
                    ApplyVolume(pcmData, _volume);
                }

                // 触发 PCM 数据事件（用于频谱分析）
                PcmDataAvailable?.Invoke(this, pcmData);

                // 写入播放缓冲区（非阻塞）
                player.Write(pcmData);

                // 更新进度
                _currentFrame++;
                _currentFrameIndex = _currentFrame;
                if (_totalFrames > 0)
                    Position = (Double)_currentFrame / _totalFrames * _duration;

                // 避免缓冲区溢出，等待消耗
                while (!token.IsCancellationRequested &&
                       player.BufferedBytes > _audioFormat!.BytesPerFrame * 32)
                {
                    Thread.Sleep(10);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"播放线程异常: {ex.Message}");
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                // 播放自然结束，确保在主线程上通知
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (State == PlaybackState.Playing)
                        StopPlayback();
                });
            }
        }
    }

    private static void ApplyVolume(Byte[] pcmData, Double volume)
    {
        if (pcmData.Length < 2) return;

        // 16-bit PCM 音量调整
        var span = new Span<Byte>(pcmData);
        var samples = MemoryMarshal.Cast<Byte, Int16>(span);
        for (Int32 i = 0; i < samples.Length; i++)
        {
            var sample = (Int32)(samples[i] * volume);
            if (sample > Int16.MaxValue) sample = Int16.MaxValue;
            if (sample < Int16.MinValue) sample = Int16.MinValue;
            samples[i] = (Int16)sample;
        }
    }

    private void StopPlayback()
    {
        _cts?.Cancel();
        _cts = null;

        if (_player != null)
        {
            try { _player.Stop(); }
            catch { /* ignore */ }
            try { _player.Dispose(); }
            catch { /* ignore */ }
            _player = null;
        }

        _playbackThread = null;
        State = PlaybackState.Stopped;
        _currentFrame = 0;
        _currentFrameIndex = 0;
        Position = 0;
    }

    private void CloseReader()
    {
        if (_reader != null)
        {
            try { _reader.Dispose(); }
            catch { /* ignore */ }
            _reader = null;
        }
        _codec = null;
        _audioFormat = null;
        _metadata = null;
        _duration = 0;
        _totalFrames = 0;
    }

    private void OnPlaybackStopped(Object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (State == PlaybackState.Playing)
                StopPlayback();
        });
    }
    #endregion

    #region Dispose
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopPlayback();
        CloseReader();
        GC.SuppressFinalize(this);
    }
    #endregion
}
