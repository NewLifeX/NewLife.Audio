using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AudioStudio.Models;

namespace AudioStudio.ViewModels.Player;

/// <summary>播放器视图模型</summary>
public partial class PlaybackViewModel : ViewModelBase
{
    #region 属性
    private readonly Services.AudioPlayerService _player;

    /// <summary>播放服务</summary>
    public Services.AudioPlayerService Player => _player;

    /// <summary>播放列表</summary>
    public ObservableCollection<PlaylistItem> Playlist { get; } = [];

    /// <summary>当前播放项</summary>
    [ObservableProperty]
    private PlaylistItem? _currentItem;

    /// <summary>是否有文件加载</summary>
    public Boolean HasFile => _player.IsFileOpen;

    /// <summary>文件格式详情文本</summary>
    [ObservableProperty]
    private String _formatInfoText = String.Empty;

    /// <summary>元数据信息文本</summary>
    [ObservableProperty]
    private String _metadataText = String.Empty;

    /// <summary>波形数据（最新的 PCM 帧）</summary>
    [ObservableProperty]
    private Byte[] _waveformData = [];
    #endregion

    #region 构造
    public PlaybackViewModel()
    {
        _player = new Services.AudioPlayerService();
        _player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Services.AudioPlayerService.IsFileOpen))
                OnPropertyChanged(nameof(HasFile));

            // 文件打开后更新信息
            if (e.PropertyName == nameof(Services.AudioPlayerService.AudioFormat) ||
                e.PropertyName == nameof(Services.AudioPlayerService.Metadata))
            {
                UpdateInfoText();
            }
        };

        // 订阅 PCM 数据用于波形显示
        _player.PcmDataAvailable += OnPcmData;
    }

    private void OnPcmData(Object? sender, Byte[] data)
    {
        // 降采样到约 10K 字节用于波形渲染
        if (data == null || data.Length == 0) return;
        var step = Math.Max(1, data.Length / 10000);
        var sampled = new Byte[(data.Length + step - 1) / step / 2 * 2];
        var idx = 0;
        for (var i = 0; i < data.Length && idx < sampled.Length; i += step * 2)
        {
            if (i + 1 < data.Length)
            {
                sampled[idx++] = data[i];
                sampled[idx++] = data[i + 1];
            }
        }
        WaveformData = sampled;
    }
    #endregion

    #region 命令
    /// <summary>打开文件</summary>
    [RelayCommand]
    private async Task OpenFile()
    {
        // 需要获取顶层窗口以调用文件对话框
        var top = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime
            ? lifetime.MainWindow
            : null;
        if (top == null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择音频文件",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new("音频文件")
                {
                    Patterns = ["*.wav", "*.mp3", "*.flac", "*.ogg", "*.opus", "*.mp4", "*.m4a", "*.aac", "*.wma"]
                },
                new("所有文件")
                {
                    Patterns = ["*.*"]
                }
            ]
        });

        foreach (var file in files)
        {
            AddToPlaylist(file.Path.LocalPath);
        }
    }

    /// <summary>播放/暂停切换</summary>
    [RelayCommand]
    private void PlayPause()
    {
        if (_player.State == Services.PlaybackState.Playing)
            _player.Pause();
        else if (_player.State == Services.PlaybackState.Paused)
            _player.Play();
        else if (_player.State == Services.PlaybackState.Stopped && CurrentItem != null)
            PlayFile(CurrentItem.FilePath);
    }

    /// <summary>停止</summary>
    [RelayCommand]
    private void Stop()
    {
        _player.Stop();
    }

    /// <summary>添加文件到播放列表</summary>
    [RelayCommand]
    private async Task AddFiles()
    {
        await OpenFile();
    }

    /// <summary>清空播放列表</summary>
    [RelayCommand]
    private void ClearPlaylist()
    {
        _player.Stop();
        Playlist.Clear();
        CurrentItem = null;
    }

    /// <summary>双击播放选中项</summary>
    [RelayCommand]
    private void PlaySelected(PlaylistItem? item)
    {
        if (item == null) return;
        PlayFile(item.FilePath);
        CurrentItem = item;
    }

    /// <summary>从播放列表移除</summary>
    [RelayCommand]
    private void RemoveFromPlaylist(PlaylistItem? item)
    {
        if (item == null) return;
        if (CurrentItem == item)
        {
            _player.Stop();
            CurrentItem = null;
        }
        Playlist.Remove(item);
    }

    /// <summary>上一首</summary>
    [RelayCommand]
    private void Previous()
    {
        if (CurrentItem == null || Playlist.Count == 0) return;
        var index = Playlist.IndexOf(CurrentItem);
        index = index > 0 ? index - 1 : Playlist.Count - 1;
        CurrentItem = Playlist[index];
        PlayFile(CurrentItem.FilePath);
    }

    /// <summary>下一首</summary>
    [RelayCommand]
    private void Next()
    {
        if (CurrentItem == null || Playlist.Count == 0) return;
        var index = Playlist.IndexOf(CurrentItem);
        index = index < Playlist.Count - 1 ? 0 : index + 1;
        CurrentItem = Playlist[index];
        PlayFile(CurrentItem.FilePath);
    }
    #endregion

    #region 内部方法
    private void AddToPlaylist(String filePath)
    {
        // 避免重复
        if (Playlist.Any(p => String.Equals(p.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
            return;

        var fi = new FileInfo(filePath);
        var item = new PlaylistItem
        {
            FilePath = filePath,
            FileName = fi.Name,
            Title = Path.GetFileNameWithoutExtension(filePath),
            FileSize = fi.Exists ? fi.Length : 0
        };

        // 尝试读取元数据获取标题和时长
        try
        {
            using var reader = NewLife.Audio.Containers.AudioContainerFactory.CreateReader(filePath);
            if (reader.Metadata != null)
            {
                if (!String.IsNullOrEmpty(reader.Metadata.Title))
                    item.Title = reader.Metadata.Title;
                item.Artist = reader.Metadata.Artist ?? String.Empty;
            }
            item.Duration = TimeSpan.FromSeconds(reader.Duration);
        }
        catch
        {
            // 忽略读取元数据失败
        }

        Playlist.Add(item);

        // 第一个文件自动设为当前
        CurrentItem ??= item;
    }

    private void PlayFile(String filePath)
    {
        try
        {
            _player.Open(filePath);
            _player.Play();
            UpdateInfoText();
        }
        catch (Exception ex)
        {
            // 错误处理
            System.Diagnostics.Debug.WriteLine($"播放失败: {ex.Message}");
        }
    }

    private void UpdateInfoText()
    {
        var fmt = _player.AudioFormat;
        if (fmt != null)
        {
            FormatInfoText = $"{fmt.SampleRate} Hz / {fmt.BitsPerSample} bit / {fmt.Channels} 声道 / {_player.CodecType}";
        }

        var meta = _player.Metadata;
        if (meta != null)
        {
            var parts = new System.Collections.Generic.List<String>();
            if (!String.IsNullOrEmpty(meta.Title)) parts.Add($"标题: {meta.Title}");
            if (!String.IsNullOrEmpty(meta.Artist)) parts.Add($"艺术家: {meta.Artist}");
            if (!String.IsNullOrEmpty(meta.Album)) parts.Add($"专辑: {meta.Album}");
            if (!String.IsNullOrEmpty(meta.Genre)) parts.Add($"风格: {meta.Genre}");
            if (meta.Year > 0) parts.Add($"年份: {meta.Year}");
            MetadataText = parts.Count > 0 ? String.Join("  |  ", parts) : "无元数据信息";
        }
        else
        {
            MetadataText = "无元数据信息";
        }
    }
    #endregion
}
