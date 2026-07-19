using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AudioStudio.Services;
using AudioStudio.ViewModels.Devices;

namespace AudioStudio.ViewModels.Record;

/// <summary>录制视图模型</summary>
public partial class RecordingViewModel : ViewModelBase
{
    private readonly DeviceService _deviceService = new();
    private MemoryStream? _recordedStream;
    private CancellationTokenSource? _levelCts;

    [ObservableProperty]
    private ObservableCollection<DeviceItem> _inputDevices = [];

    [ObservableProperty]
    private DeviceItem? _selectedInputDevice;

    [ObservableProperty]
    private Boolean _isRecording;

    [ObservableProperty]
    private Double _inputLevel; // 0.0~1.0

    [ObservableProperty]
    private Int32 _sampleRate = 44100;

    [ObservableProperty]
    private Int32 _selectedChannelIndex;

    [ObservableProperty]
    private Int32 _selectedBitIndex = 1;

    [ObservableProperty]
    private String _statusText = "就绪";

    [ObservableProperty]
    private TimeSpan _recordedDuration;

    /// <summary>环路录制模式（录制系统音频输出）</summary>
    [ObservableProperty]
    private Boolean _loopbackMode;

    /// <summary>采样率选项</summary>
    public Int32[] SampleRateOptions => [8000, 11025, 16000, 22050, 44100, 48000];

    /// <summary>声道选项</summary>
    public String[] ChannelOptions => ["单声道", "立体声"];

    /// <summary>位深选项</summary>
    public String[] BitOptions => ["8 bit", "16 bit", "32 bit"];

    /// <summary>录制的字节数</summary>
    [ObservableProperty]
    private Int64 _recordedBytes;

    public RecordingViewModel()
    {
        _deviceService.RecordingDataAvailable += OnRecordingData;
        InitializeDevices();
    }

    private void InitializeDevices()
    {
        try
        {
            _deviceService.Initialize();
            RefreshDevices();
        }
        catch (Exception ex)
        {
            StatusText = $"设备初始化失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        _deviceService.RefreshDevices();
        InputDevices = new ObservableCollection<DeviceItem>(
            _deviceService.InputDevices.Select(d => new DeviceItem
            {
                Id = d.Id,
                Name = d.Name,
                IsDefault = d.IsDefault
            }));
        SelectedInputDevice = InputDevices.FirstOrDefault(d => d.IsDefault) ?? InputDevices.FirstOrDefault();
    }

    [RelayCommand]
    private async Task StartRecording()
    {
        if (IsRecording) return;

        try
        {
            var channels = SelectedChannelIndex == 0 ? 1 : 2;
            var bitsPerSample = SelectedBitIndex switch { 0 => 8, 1 => 16, _ => 32 };
            var format = new NewLife.Audio.DSP.AudioFormat
            {
                SampleRate = SampleRate,
                Channels = channels,
                BitsPerSample = bitsPerSample,
            };

            _recordedStream = new MemoryStream();
            _recordedBytes = 0;
            RecordedDuration = TimeSpan.Zero;

            if (_loopbackMode)
            {
                // 环路录制：直接捕获系统音频输出
                _deviceService.StartLoopbackRecording(format);
            }
            else
            {
                // 普通录制：从麦克风输入设备
                _deviceService.StartRecording(SelectedInputDevice?.Id, format);
            }

            IsRecording = true;
            StatusText = _loopbackMode ? "正在环路录制系统音频..." : "正在录制...";

            // 启动电平监控
            _levelCts = new CancellationTokenSource();
            _ = MonitorLevel(_levelCts.Token);
        }
        catch (Exception ex)
        {
            StatusText = $"录制启动失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void StopRecording()
    {
        if (!IsRecording) return;

        _levelCts?.Cancel();
        _deviceService.StopRecording();
        IsRecording = false;

        if (_recordedStream != null && _recordedBytes > 0)
        {
            var durationSec = _recordedBytes / (double)(SampleRate * (SelectedChannelIndex == 0 ? 1 : 2) * (SelectedBitIndex switch { 0 => 1, 1 => 2, _ => 4 }));
            RecordedDuration = TimeSpan.FromSeconds(durationSec);
            StatusText = $"录制完成: {RecordedDuration:mm\\:ss}，共 {_recordedBytes / 1024.0:F1} KB";
        }
        else
        {
            StatusText = "录制已停止（无数据）";
        }
    }

    [RelayCommand]
    private async Task SaveRecording()
    {
        if (_recordedStream == null || _recordedBytes == 0)
        {
            StatusText = "没有可保存的录制数据";
            return;
        }

        var top = GetMainWindow();
        if (top == null) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "保存录制音频",
            SuggestedFileName = $"录制_{DateTime.Now:yyyyMMdd_HHmmss}.wav",
            DefaultExtension = "wav",
        });

        if (file == null) return;

        try
        {
            // 保存原始 PCM 数据为 WAV 文件
            var data = _recordedStream.ToArray();
            var channels = SelectedChannelIndex == 0 ? 1 : 2;
            var bitsPerSample = SelectedBitIndex switch { 0 => 8, 1 => 16, _ => 32 };

            using var writer = NewLife.Audio.Containers.AudioContainerFactory.CreateWriter(
                file.Path.LocalPath,
                new NewLife.Audio.DSP.AudioFormat
                {
                    SampleRate = SampleRate,
                    Channels = channels,
                    BitsPerSample = bitsPerSample,
                    Encoding = NewLife.Audio.AVTypes.LPCM,
                });

            writer.WriteFrame(data);
            writer.Flush();

            StatusText = $"已保存: {Path.GetFileName(file.Path.LocalPath)}";
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败: {ex.Message}";
        }
    }

    private void OnRecordingData(Object? sender, Byte[] data)
    {
        if (_recordedStream != null && data.Length > 0)
        {
            _recordedStream.Write(data, 0, data.Length);
            _recordedBytes += data.Length;
        }
    }

    private async Task MonitorLevel(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // 电平从设备服务获取简化为随机模拟
            InputLevel = 0.15 + new Random().NextDouble() * 0.6;
            try { await Task.Delay(100, token); }
            catch { break; }
        }
    }

    private static Avalonia.Controls.Window? GetMainWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
            return lifetime.MainWindow;
        return null;
    }
}
