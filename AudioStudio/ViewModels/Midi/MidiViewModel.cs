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
    private SoundBank? _soundBank;
    private IMidiOutput? _midiOutput;
    private MidiOutputSequencer? _sequencer;

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

    /// <summary>独立序列编号（1 基，仅 Format 2）</summary>
    public ObservableCollection<Int32> Sequences { get; } = [];

    /// <summary>当前选中的序列编号（1 基）</summary>
    [ObservableProperty]
    private Int32 _selectedSequence = 1;

    /// <summary>是否包含多个独立序列</summary>
    public Boolean HasMultipleSequences => _midi != null && _midi.SequenceCount > 1;

    /// <summary>MIDI 输出设备</summary>
    public ObservableCollection<MidiDeviceInfo> OutputDevices { get; } = [];

    /// <summary>当前选中的 MIDI 输出设备</summary>
    [ObservableProperty]
    private MidiDeviceInfo? _selectedOutputDevice;

    /// <summary>是否有 MIDI 输出设备</summary>
    public Boolean HasMidiOutput => OutputDevices.Count > 0;

    /// <summary>MIDI 输出状态</summary>
    [ObservableProperty]
    private String _midiOutputStatus = "未检测到 MIDI 输出设备";

    /// <summary>当前采样音源名称</summary>
    [ObservableProperty]
    private String _soundBankName = "内置波形合成";

    /// <summary>是否已加载采样音源</summary>
    public Boolean HasSoundBank => _soundBank != null;

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
        RefreshDevices();
        _ = LoadDemoAsync(SelectedDemo);
    }

    /// <summary>刷新 MIDI 输出设备</summary>
    [RelayCommand]
    private void RefreshDevices()
    {
        var selected = SelectedOutputDevice?.Index ?? -1;
        OutputDevices.Clear();
        foreach (var item in MidiOutputDevice.Enumerate()) OutputDevices.Add(item);

        SelectedOutputDevice = OutputDevices.FirstOrDefault(d => d.Index == selected) ?? OutputDevices.FirstOrDefault();
        HasMidiOutputChanged();
        MidiOutputStatus = OutputDevices.Count > 0
            ? $"已检测到 {OutputDevices.Count} 个 MIDI 输出设备"
            : "未检测到 MIDI 输出设备（Windows 需要已安装的 MIDI 端口）";
    }

    private void HasMidiOutputChanged() => OnPropertyChanged(nameof(HasMidiOutput));

    /// <summary>发送测试音到 MIDI 输出设备</summary>
    [RelayCommand]
    private async Task TestMidiOutput()
    {
        var device = SelectedOutputDevice;
        if (device == null) return;

        try
        {
            MidiOutputStatus = "正在发送测试音...";
            await Task.Run(() =>
            {
                using var output = MidiOutputDevice.Open(device.Index);
                output.SendShortMessage(0x90, 60, 100);
                Task.Delay(250).Wait();
                output.SendShortMessage(0x80, 60, 0);
                output.Reset();
            });
            MidiOutputStatus = $"测试音已发送到 {device.Name}";
        }
        catch (Exception ex)
        {
            MidiOutputStatus = $"发送失败：{ex.Message}";
        }
    }

    /// <summary>把当前 MIDI 发送到输出设备</summary>
    [RelayCommand]
    private async Task PlayToDevice()
    {
        var midi = _midi;
        var device = SelectedOutputDevice;
        if (midi == null || device == null) return;

        StopDevicePlayback();
        try
        {
            _midiOutput = MidiOutputDevice.Open(device.Index);
            _sequencer = new MidiOutputSequencer(midi, _midiOutput, Math.Max(0, SelectedSequence - 1));
            MidiOutputStatus = $"正在发送到 {device.Name}...";

            await _sequencer.PlayAsync();
            MidiOutputStatus = _sequencer.IsCancelled ? "已停止发送" : "MIDI 发送完成";
        }
        catch (Exception ex)
        {
            MidiOutputStatus = $"发送失败：{ex.Message}";
        }
        finally
        {
            _midiOutput?.Dispose();
            _midiOutput = null;
            _sequencer = null;
        }
    }

    /// <summary>停止 MIDI 设备发送</summary>
    [RelayCommand]
    private void StopDevicePlayback()
    {
        var sequencer = _sequencer;
        if (sequencer == null) return;

        try { sequencer.Stop(); } catch { /* 设备可能已关闭 */ }
        MidiOutputStatus = "已停止发送";
    }

    /// <summary>加载 SoundFont/DLS 采样音源</summary>
    [RelayCommand]
    private async Task LoadSoundBank()
    {
        var top = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime
            ? lifetime.MainWindow
            : null;
        if (top == null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 SoundFont 或 DLS 音源",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new("采样音源")
                {
                    Patterns = ["*.sf2", "*.dls"]
                },
                new("所有文件")
                {
                    Patterns = ["*.*"]
                }
            ]
        });

        var file = files.FirstOrDefault();
        if (file == null) return;

        var path = file.Path.LocalPath;
        IsBusy = true;
        StatusText = $"正在加载音源 {Path.GetFileName(path)}...";
        try
        {
            var bank = await Task.Run(() => LoadBank(path));
            _soundBank = bank;
            SoundBankName = $"{Path.GetFileName(path)}（{bank.Presets.Count} 个预设）";
            OnPropertyChanged(nameof(HasSoundBank));
            await RerenderAsync();
            StatusText = $"已加载音源 {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText = $"加载音源失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>恢复内置波形合成</summary>
    [RelayCommand]
    private async Task ClearSoundBank()
    {
        if (_soundBank == null) return;

        _soundBank = null;
        SoundBankName = "内置波形合成";
        OnPropertyChanged(nameof(HasSoundBank));
        IsBusy = true;
        try
        {
            await RerenderAsync();
            StatusText = "已恢复内置波形合成";
        }
        catch (Exception ex)
        {
            StatusText = $"重新渲染失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static SoundBank LoadBank(String path)
    {
        var extension = Path.GetExtension(path);
        return String.Equals(extension, ".dls", StringComparison.OrdinalIgnoreCase)
            ? DlsSoundBank.Read(path)
            : (SoundBank)SoundFont.Read(path);
    }

    private MidiRenderOptions CreateRenderOptions() => new() { SoundBank = _soundBank };

    private async Task RerenderAsync()
    {
        var midi = _midi;
        if (midi == null) return;

        var index = Math.Clamp(SelectedSequence - 1, 0, midi.SequenceCount - 1);
        var pcm = await Task.Run(() => MidiSynthesizer.RenderSequence(midi, index, CreateRenderOptions()));
        ApplyMidi(FileName, midi, pcm, index);
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
            var pcm = await Task.Run(() => MidiSynthesizer.RenderSequence(midi, 0, CreateRenderOptions()));

            ApplyMidi(demo.Name + ".mid", midi, pcm, 0);
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
            var pcm = await Task.Run(() => MidiSynthesizer.RenderSequence(midi, 0, CreateRenderOptions()));

            ApplyMidi(Path.GetFileName(path), midi, pcm, 0);
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

    private void ApplyMidi(String name, MidiFile midi, AudioPcm pcm, Int32 sequenceIndex)
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

        var renderMidi = midi.GetSequence(sequenceIndex);
        var notes = renderMidi.GetNotes();
        Notes = notes;

        var tempo = renderMidi.GetTempoMap();
        var beats = new List<Double>();
        var bars = new List<Double>();
        foreach (var item in tempo.GetBeatLines(renderMidi.Duration))
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
            foreach (var item in renderMidi.Tracks[note.Track].Events)
            {
                if (item.Tick > note.StartTick) break;
                if (item.Type == MidiEventType.ProgramChange && item.Channel == note.Channel) program = item.Program;
            }
            programs.Add(program);
        }

        var programText = programs.Count > 0
            ? String.Join(", ", programs.Select(p => $"{p} {GeneralMidi.GetName(p)}"))
            : "无";

        var sequenceText = midi.SequenceCount > 1 ? $" · 序列 {sequenceIndex + 1}/{midi.SequenceCount}" : String.Empty;
        var sourceText = _soundBank != null ? $"采样音源：{SoundBankName}" : "合成音源：内置波形";
        InfoText = $"{midi.Format}{sequenceText} · {(renderMidi.IsSmpte ? $"{renderMidi.SmpteTicksPerSecond:n0} ticks/s" : $"{renderMidi.TicksPerQuarterNote} PPQ")} · "
            + $"{tempo.BpmAt(0):n1} BPM · {renderMidi.Duration:n2}s · {renderMidi.Tracks.Count} 轨 · {notes.Count} 音符 · 复音 {renderMidi.GetMaxPolyphony()}\n"
            + $"音色：{programText}\n{sourceText}";

        if (Sequences.Count != midi.SequenceCount)
        {
            Sequences.Clear();
            for (var i = 1; i <= midi.SequenceCount; i++) Sequences.Add(i);
        }
        SelectedSequence = sequenceIndex + 1;
        OnPropertyChanged(nameof(HasMultipleSequences));

        _player.Load(pcm);
        OnPropertyChanged(nameof(HasFile));
    }

    /// <summary>加载当前选中的 Format 2 序列</summary>
    [RelayCommand]
    private async Task LoadSequence()
    {
        var midi = _midi;
        if (midi == null || midi.SequenceCount <= 1) return;

        var index = Math.Clamp(SelectedSequence - 1, 0, midi.SequenceCount - 1);
        IsBusy = true;
        StatusText = $"正在加载序列 {index + 1}...";

        try
        {
            var pcm = await Task.Run(() => MidiSynthesizer.RenderSequence(midi, index, CreateRenderOptions()));
            ApplyMidi(FileName, midi, pcm, index);
            StatusText = $"已加载序列 {index + 1}";
        }
        catch (Exception ex)
        {
            StatusText = $"加载序列失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
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
