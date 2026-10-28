using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NewLife.Audio;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;

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

/// <summary>音频播放服务。解码、分块与背压全部交给 <see cref="AudioPlayer"/>，这里只维护 UI 状态</summary>
/// <remarks>
/// 位置刷新用 UI 定时器读取 <see cref="AudioPlayer.Position"/>，避免播放线程直接碰观察属性；
/// 文件信息（格式/编码/标签/时长）来自 <see cref="AudioProbe"/>，跳转由 <see cref="AudioPlayer.Seek"/> 完成。
/// </remarks>
public partial class AudioPlayerService : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource _cts;
    private AudioPlayer _player;
    private Boolean _disposed;

    /// <summary>构造播放服务</summary>
    public AudioPlayerService()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (s, e) => RefreshPosition());
    }

    #region 属性
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
    private String _filePath;

    /// <summary>当前文件名</summary>
    [ObservableProperty]
    private String _fileName;

    /// <summary>音频格式</summary>
    [ObservableProperty]
    private AudioFormat _audioFormat;

    /// <summary>元数据</summary>
    [ObservableProperty]
    private AudioMetadata _metadata;

    /// <summary>编码类型</summary>
    [ObservableProperty]
    private AVTypes _codecType;

    /// <summary>总帧数（容器帧数，未知时为 0）</summary>
    [ObservableProperty]
    private Int64 _totalFrames;

    /// <summary>当前帧索引（当前实现只汇报时间位置，保持为 0）</summary>
    [ObservableProperty]
    private Int64 _currentFrameIndex;

    /// <summary>是否可以播放</summary>
    public Boolean CanPlay => State == PlaybackState.Stopped || State == PlaybackState.Paused;

    /// <summary>是否可以暂停</summary>
    public Boolean CanPause => State == PlaybackState.Playing;

    /// <summary>是否可以停止</summary>
    public Boolean CanStop => State != PlaybackState.Stopped;

    /// <summary>是否有文件打开</summary>
    public Boolean IsFileOpen => FilePath != null;

    /// <summary>PCM 数据可用事件（用于频谱分析等外部消费）</summary>
    public event EventHandler<Byte[]> PcmDataAvailable;

    partial void OnStateChanged(PlaybackState value)
    {
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanStop));
    }

    partial void OnVolumeChanged(Double value)
    {
        if (_player != null) _player.Volume = value;
    }

    partial void OnFilePathChanged(String value)
    {
        FileName = value == null ? null : Path.GetFileName(value);
        OnPropertyChanged(nameof(IsFileOpen));
    }
    #endregion

    #region 方法
    /// <summary>打开音频文件（只读信息，不开始播放）</summary>
    /// <param name="filePath">文件路径</param>
    public void Open(String filePath)
    {
        StopPlayback();

        try
        {
            var info = AudioProbe.Probe(filePath);

            AudioFormat = info.Format;
            CodecType = info.Codec;
            Duration = info.Duration;
            Metadata = info.Metadata;
            TotalFrames = 0;
            CurrentFrameIndex = 0;
            Position = 0;
            FilePath = filePath;
        }
        catch (Exception ex)
        {
            CloseFile();
            throw new InvalidOperationException($"无法打开音频文件: {filePath}", ex);
        }
    }

    /// <summary>开始播放（暂停时继续）</summary>
    public void Play()
    {
        if (FilePath == null) return;

        if (State == PlaybackState.Paused && _player != null)
        {
            _player.Resume();
            State = PlaybackState.Playing;
            _timer.Start();
            return;
        }

        if (State == PlaybackState.Playing) return;

        var path = FilePath;
        var player = new AudioPlayer { Volume = Volume };
        player.PcmDataAvailable += OnPcmData;
        _player = player;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        State = PlaybackState.Playing;
        _timer.Start();

        _ = Task.Run(() =>
        {
            try
            {
                player.PlayFile(path, token);
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"播放异常: {ex.Message}");
            }
            finally
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_player, player)) StopPlayback();
                });
            }
        }, token);
    }

    /// <summary>暂停播放</summary>
    public void Pause()
    {
        if (State != PlaybackState.Playing) return;

        _player?.Pause();
        State = PlaybackState.Paused;
    }

    /// <summary>停止播放并复位位置</summary>
    public void Stop() => StopPlayback();

    /// <summary>跳转到指定时间位置（秒）</summary>
    /// <param name="seconds">目标位置（秒）</param>
    public void Seek(Double seconds)
    {
        if (_player == null) return;
        if (!_player.Seek(seconds)) return;

        Position = seconds;
        CurrentFrameIndex = 0;

        if (State == PlaybackState.Paused) _player.Resume();
        State = PlaybackState.Playing;
        _timer.Start();
    }
    #endregion

    #region 内部方法
    private void RefreshPosition()
    {
        var player = _player;
        if (player == null || State != PlaybackState.Playing) return;

        Position = player.Position;
    }

    private void OnPcmData(Object sender, Byte[] pcm) => PcmDataAvailable?.Invoke(this, pcm);

    private void StopPlayback()
    {
        _timer.Stop();

        _cts?.Cancel();
        _cts = null;

        var player = _player;
        _player = null;
        if (player != null)
        {
            player.PcmDataAvailable -= OnPcmData;
            try { player.Stop(); } catch { /* 设备失效时忽略 */ }
            try { player.Dispose(); } catch { /* 忽略 */ }
        }

        State = PlaybackState.Stopped;
        Position = 0;
        CurrentFrameIndex = 0;
    }

    private void CloseFile()
    {
        FilePath = null;
        FileName = null;
        AudioFormat = null;
        Metadata = null;
        Duration = 0;
        TotalFrames = 0;
        CurrentFrameIndex = 0;
        Position = 0;
        OnPropertyChanged(nameof(IsFileOpen));
    }

    /// <summary>释放服务</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopPlayback();
        _timer.Stop();
        CloseFile();
        GC.SuppressFinalize(this);
    }
    #endregion
}
