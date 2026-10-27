using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using AudioStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NewLife.Audio;
using NewLife.Audio.Midi;

namespace AudioStudio.ViewModels.Midi;

/// <summary>内置 MIDI 示例</summary>
public sealed class MidiDemoItem
{
    /// <summary>显示名称</summary>
    public String Name { get; init; } = String.Empty;

    /// <summary>说明</summary>
    public String Description { get; init; } = String.Empty;

    /// <summary>资源 URI</summary>
    public String Resource { get; init; } = String.Empty;

    /// <inheritdoc/>
    public override String ToString() => Name;
}

/// <summary>MIDI 音序视图模型</summary>
public partial class MidiViewModel : ViewModelBase
{
    private readonly MidiPlaybackService _player;
    private MidiFile? _midi;
    private AudioPcm? _pcm;

    /// <summary>播放服务</summary>
    public MidiPlaybackService Player => _player;

    /// <summary>内置示例</summary>
    public ObservableCollection<MidiDemoItem> Demos { get; } =
    [
        new() { Name = "欢乐颂", Description = "贝多芬 · 公有领域", Resource = "avares://AudioStudio/Assets/Midi/ode-to-joy.mid" },
        new() { Name = "绿袖子", Description = "英国民谣 · 公有领域", Resource = "avares://AudioStudio/Assets/Midi/greensleeves.mid" },
        new() { Name = "卡农", Description = "帕赫贝尔 · 公有领域", Resource = "avares://AudioStudio/Assets/Midi/canon-in-d.mid" },
        new() { Name = "茉莉花", Description = "中国民歌 · 公有领域", Resource = "avares://AudioStudio/Assets/Midi/jasmine-flower.mid" },
    ];

    /// <summary>当前选中的内置示例</summary>
    [ObservableProperty]
    private MidiDemoItem? _selectedDemo;

    /// <summary>文件名</summary>
    [ObservableProperty]
    private String _fileName = "未加载";

    /// <summary>文件信息</summary>
    [ObservableProperty]
    private String _infoText = "打开 .mid 文件，或从上方选择一个内置示例。";

    /// <summary>状态文本</summary>
    [ObservableProperty]
    private String _statusText = "就绪";

    /// <summary>波形数据（16 位 PCM）</summary>
    [ObservableProperty]
    private Byte[] _waveformData = [];

    /// <summary>采样率</summary>
    [ObservableProperty]
    private Int32 _sampleRate = 44100;

    /// <summary>声道数</summary>
    [ObservableProperty]
    private Int32 _channels = 2;

    /// <summary>总时长（秒）</summary>
    [ObservableProperty]
    private Double _duration;

    /// <summary>当前播放位置（秒）</summary>
    [ObservableProperty]
    private Double _position;

    /// <summary>音符列表（迷你钢琴卷）</summary>
    [ObservableProperty]
    private IList<MidiNote> _notes = [];

    /// <summary>节拍线</summary>
    [ObservableProperty]
    private IList<Double> _beatTimes = [];

    /// <summary>小节线</summary>
    [ObservableProperty]
    private IList<Double> _barTimes = [];

    /// <summary>是否正在加载</summary>
    [ObservableProperty]
    private Boolean _isBusy;

    /// <summary>是否有文件</summary>
    public Boolean HasFile => _midi != null && _pcm != null;

    /// <summary>播放状态</summary>
    public PlaybackState State => _player.State;

    /// <summary>MIDI 音序视图模型</summary>
    public MidiViewModel()
    {
        _player = new MidiPlaybackService();
        _player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MidiPlaybackService.Position)) Position = _player.Position;
            if (e.PropertyName == nameof(MidiPlaybackService.State)) OnPropertyChanged(nameof(State));
        };
        _player.PlaybackEnded += (_, _) => StatusText = "播放结束";

        SelectedDemo = Demos[0];
        _ = LoadDemoAsync(SelectedDemo);
    }

    /// <summary>打开 MIDI 文件</summary>
    [RelayCommand]
    private async Task OpenFile()
    {
        var top = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime
            ? lifetime.MainWindow
            : null;
        if (top == null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 MIDI 文件",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new("MIDI 文件")
                {
                    Patterns = ["*.mid", "*.midi", "*.smf"]
                },
                new("所有文件")
                {
                    Patterns = ["*.*"]
                }
            ]
        });

        var file = files.FirstOrDefault();
        if (file == null) return;

        await LoadFileAsync(file.Path.LocalPath);
    }

    /// <summary>加载选中的内置示例</summary>
    [RelayCommand]
    private Task LoadDemo() => LoadDemoAsync(SelectedDemo);

    private async Task LoadDemoAsync(MidiDemoItem? demo)
    {
        if (demo == null) return;

        IsBusy = true;
        StatusText = $"正在加载 {demo.Name}...";

        try
        {
            using var stream = AssetLoader.Open(new Uri(demo.Resource));
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);

            var data = ms.ToArray();
            var midi = await Task.Run(() => MidiFile.Parse(data));
            var pcm = await Task.Run(() => MidiSynthesizer.Render(midi));

            ApplyFile(demo.Name + ".mid", midi, pcm);
            StatusText = $"已加载 {demo.Name}";
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadFileAsync(String path)
    {
        IsBusy = true;
        StatusText = $"正在解析 {Path.GetFileName(path)}...";

        try
        {
            var midi = await Task.Run(() => MidiFile.Read(path));
            var pcm = await Task.Run(() => MidiSynthesizer.Render(midi));

            ApplyFile(Path.GetFileName(path), midi, pcm);
            StatusText = $"已加载 {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFile(String name, MidiFile midi, AudioPcm pcm)
    {
        _player.Stop();
        _midi = midi;
        _pcm = pcm;

        FileName = name;
        WaveformData = pcm.Data;
        SampleRate = pcm.Format.SampleRate;
        Channels = pcm.Format.Channels;
        Duration = pcm.Duration;
        Position = 0;

        var notes = midi.GetNotes();
        Notes = notes;

        var tempo = midi.GetTempoMap();
        var beats = new List<Double>();
        var bars = new List<Double>();
        foreach (var item in tempo.GetBeatLines(midi.Duration))
        {
            if (item.Item2) bars.Add(item.Item1);
            else beats.Add(item.Item1);
        }
        BeatTimes = beats;
        BarTimes = bars;

        var programs = new SortedSet<Int32>();
        foreach (var note in notes)
        {
            var program = 0;
            foreach (var item in midi.Tracks[note.Track].Events)
            {
                if (item.Tick > note.StartTick) break;
                if (item.Type == MidiEventType.ProgramChange && item.Channel == note.Channel) program = item.Program;
            }
            programs.Add(program);
        }

        var programText = programs.Count > 0
            ? String.Join(", ", programs.Select(p => $"{p} {GeneralMidi.GetName(p)}"))
            : "无";

        InfoText = $"{midi.Format} · {(midi.IsSmpte ? $"{midi.SmpteTicksPerSecond:n0} ticks/s" : $"{midi.TicksPerQuarterNote} PPQ")} · "
            + $"{tempo.BpmAt(0):n1} BPM · {midi.Duration:n2}s · {midi.Tracks.Count} 轨 · {notes.Count} 音符 · 复音 {midi.GetMaxPolyphony()}\n"
            + $"音色：{programText}";

        _player.Load(pcm);
        OnPropertyChanged(nameof(HasFile));
    }

    /// <summary>播放/暂停</summary>
    [RelayCommand]
    private void PlayPause()
    {
        if (_pcm == null) return;

        try
        {
            if (_player.State == PlaybackState.Playing) _player.Pause();
            else _player.Play();
        }
        catch (Exception ex)
        {
            StatusText = $"播放失败：{ex.Message}";
        }
    }

    /// <summary>停止</summary>
    [RelayCommand]
    private void Stop() => _player.Stop();

    /// <summary>定位到指定秒数</summary>
    /// <param name="seconds">目标秒数</param>
    public void SeekTo(Double seconds)
    {
        _player.Seek(seconds);
        Position = seconds;
    }

    /// <summary>另存为 WAV</summary>
    [RelayCommand]
    private Task SaveWav() => SaveAsync("wav", "WAV 音频", "*.wav");

    /// <summary>另存为 MP3</summary>
    [RelayCommand]
    private Task SaveMp3() => SaveAsync("mp3", "MP3 音频", "*.mp3");

    private async Task SaveAsync(String extension, String label, String pattern)
    {
        var pcm = _pcm;
        if (pcm == null) return;

        var top = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime
            ? lifetime.MainWindow
            : null;
        if (top == null) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存渲染结果",
            DefaultExtension = extension,
            SuggestedFileName = Path.GetFileNameWithoutExtension(FileName),
            FileTypeChoices =
            [
                new(label)
                {
                    Patterns = [pattern]
                }
            ]
        });

        if (file == null) return;

        try
        {
            var path = file.Path.LocalPath;
            await Task.Run(() => AudioFile.WritePcm(path, pcm));
            StatusText = $"已保存 {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
        }
    }
}
