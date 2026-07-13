using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AudioStudio.Services;

namespace AudioStudio.ViewModels.Devices;

/// <summary>音频设备视图模型</summary>
public partial class DeviceViewModel : ViewModelBase
{
    private readonly DeviceService _service = new();

    [ObservableProperty]
    private ObservableCollection<DeviceItem> _outputDevices = [];

    [ObservableProperty]
    private ObservableCollection<DeviceItem> _inputDevices = [];

    [ObservableProperty]
    private DeviceItem? _selectedOutputDevice;

    [ObservableProperty]
    private DeviceItem? _selectedInputDevice;

    [ObservableProperty]
    private String _backendName = "";

    [ObservableProperty]
    private String _statusText = "就绪";

    [ObservableProperty]
    private Double _testFrequency = 440;

    [ObservableProperty]
    private Double _testVolume = 0.3;

    [ObservableProperty]
    private Boolean _isPlaying;

    [ObservableProperty]
    private Boolean _isRecording;

    [ObservableProperty]
    private String _selectedBackend = "自动";

    /// <summary>可用后端列表</summary>
    public String[] BackendOptions => ["自动", "waveout", "wasapi", "asio"];

    public DeviceViewModel()
    {
        _service.RecordingDataAvailable += OnRecordingData;
        Initialize();
    }

    private void Initialize()
    {
        try
        {
            _service.Initialize(SelectedBackend == "自动" ? null : SelectedBackend);
            BackendName = _service.BackendName;
            RefreshDevices();
            StatusText = $"已初始化: {BackendName}";
        }
        catch (Exception ex)
        {
            StatusText = $"初始化失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        _service.RefreshDevices();
        RefreshDevices();
        StatusText = "已刷新";
    }

    [RelayCommand]
    private void PlayTestTone()
    {
        if (IsPlaying)
        {
            _service.StopTestTone();
            IsPlaying = false;
            StatusText = "测试音已停止";
            return;
        }

        _service.PlayTestTone(
            SelectedOutputDevice?.Id,
            TestFrequency,
            3000, // 3秒
            TestVolume);
        IsPlaying = true;
        StatusText = $"播放测试音: {TestFrequency}Hz";
    }

    [RelayCommand]
    private void StartCapture()
    {
        if (IsRecording)
        {
            _service.StopRecording();
            IsRecording = false;
            StatusText = "录制已停止";
            return;
        }

        _service.StartRecording(SelectedInputDevice?.Id);
        IsRecording = true;
        StatusText = "录制中...";
    }

    [RelayCommand]
    private void StopCapture()
    {
        _service.StopRecording();
        IsRecording = false;
        StatusText = "录制已停止";
    }

    partial void OnSelectedBackendChanged(String value)
    {
        _service.StopTestTone();
        _service.StopRecording();
        Initialize();
    }

    private void RefreshDevices()
    {
        OutputDevices = new ObservableCollection<DeviceItem>(
            _service.OutputDevices.Select(d => new DeviceItem
            {
                Id = d.Id,
                Name = d.Name,
                IsDefault = d.IsDefault,
                FormatInfo = d.SupportedFormats.Count > 0
                    ? String.Join(", ", d.SupportedFormats.Select(f => $"{f.SampleRate/1000.0:F1}kHz/{f.Channels}ch/{f.BitsPerSample}bit"))
                    : "格式信息未提供"
            }));

        InputDevices = new ObservableCollection<DeviceItem>(
            _service.InputDevices.Select(d => new DeviceItem
            {
                Id = d.Id,
                Name = d.Name,
                IsDefault = d.IsDefault,
                FormatInfo = d.SupportedFormats.Count > 0
                    ? String.Join(", ", d.SupportedFormats.Select(f => $"{f.SampleRate/1000.0:F1}kHz/{f.Channels}ch/{f.BitsPerSample}bit"))
                    : "格式信息未提供"
            }));

        SelectedOutputDevice = OutputDevices.FirstOrDefault(d => d.IsDefault) ?? OutputDevices.FirstOrDefault();
        SelectedInputDevice = InputDevices.FirstOrDefault(d => d.IsDefault) ?? InputDevices.FirstOrDefault();
    }

    private void OnRecordingData(Object? sender, Byte[] data)
    {
        // 电平指示
        if (data.Length > 0)
        {
            var max = 0;
            for (var i = 0; i < data.Length && i < 4096; i += 2)
            {
                var sample = (Int16)(data[i] | (data[i + 1] << 8));
                if (Math.Abs(sample) > max) max = Math.Abs(sample);
            }
            var level = max / (Double)Int16.MaxValue;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                StatusText = $"录制中... 电平: {level * 100:F0}%";
            });
        }
    }
}

/// <summary>设备列表项</summary>
public class DeviceItem
{
    public String Id { get; set; } = "";
    public String Name { get; set; } = "";
    public Boolean IsDefault { get; set; }
    public String FormatInfo { get; set; } = "";
    public String DefaultTag => IsDefault ? "(默认)" : "";
}
