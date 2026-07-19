using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AudioStudio.Models;
using AudioStudio.Services;
using NewLife.Audio.DSP;

namespace AudioStudio.ViewModels.Convert;

/// <summary>格式转换视图模型</summary>
public partial class ConversionViewModel : ViewModelBase
{
    private readonly ConversionService _service = new();

    [ObservableProperty]
    private String? _inputFilePath;

    [ObservableProperty]
    private String? _inputFileName;

    [ObservableProperty]
    private String? _outputFilePath;

    [ObservableProperty]
    private String _selectedOutputFormat = ".wav";

    /// <summary>目标采样率（0=沿用源格式）</summary>
    [ObservableProperty]
    private Int32 _targetSampleRate;

    /// <summary>目标声道数（0=沿用源格式）</summary>
    [ObservableProperty]
    private Int32 _targetChannels;

    /// <summary>目标位深（0=沿用源格式）</summary>
    [ObservableProperty]
    private Int32 _targetBitDepth;

    /// <summary>是否自定义音频参数</summary>
    [ObservableProperty]
    private Boolean _customizeFormat;

    [ObservableProperty]
    private Double _progress;

    [ObservableProperty]
    private Boolean _isConverting;

    [ObservableProperty]
    private String _statusText = "就绪";

    /// <summary>支持的输出格式</summary>
    public String[] SupportedFormats => ConversionService.GetSupportedOutputFormats();

    /// <summary>采样率选项</summary>
    public Int32[] SampleRateOptions => [0, 8000, 11025, 16000, 22050, 44100, 48000, 96000];

    /// <summary>声道选项</summary>
    public String[] ChannelOptions => ["自动", "单声道", "立体声"];

    /// <summary>位深选项</summary>
    public String[] BitDepthOptions => ["自动", "8 bit", "16 bit", "32 bit"];

    /// <summary>转换历史</summary>
    public ObservableCollection<ConversionTaskItem> History { get; } = [];

    /// <summary>选择输入文件</summary>
    [RelayCommand]
    private async Task SelectInputFile()
    {
        var top = GetMainWindow();
        if (top == null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "选择要转换的音频文件",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new("音频文件") { Patterns = ["*.wav", "*.mp3", "*.flac", "*.ogg", "*.opus", "*.mp4", "*.m4a", "*.aac", "*.wma"] }
            ]
        });

        if (files.Count > 0)
        {
            InputFilePath = files[0].Path.LocalPath;
            InputFileName = Path.GetFileName(InputFilePath);
            UpdateOutputPath();
        }
    }

    /// <summary>选择输出路径</summary>
    [RelayCommand]
    private async Task SelectOutputFile()
    {
        var top = GetMainWindow();
        if (top == null) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "选择输出文件",
            SuggestedFileName = InputFileName != null
                ? Path.GetFileNameWithoutExtension(InputFileName) + SelectedOutputFormat
                : "output" + SelectedOutputFormat,
            DefaultExtension = SelectedOutputFormat.TrimStart('.'),
        });

        if (file != null)
            OutputFilePath = file.Path.LocalPath;
    }

    /// <summary>执行转换</summary>
    [RelayCommand]
    private async Task Convert()
    {
        if (String.IsNullOrEmpty(InputFilePath) || String.IsNullOrEmpty(OutputFilePath))
            return;

        if (IsConverting) return;

        IsConverting = true;
        Progress = 0;
        StatusText = "转换中...";

        try
        {
            var progress = new Progress<Double>(p => Progress = p);

            // 构建目标格式参数
            AudioFormat? targetFormat = null;
            if (_customizeFormat)
            {
                targetFormat = new AudioFormat();
                if (_targetSampleRate > 0) targetFormat.SampleRate = _targetSampleRate;
                if (_targetChannels > 0) targetFormat.Channels = _targetChannels;
                if (_targetBitDepth > 0) targetFormat.BitsPerSample = _targetBitDepth;
            }

            await _service.ConvertFileAsync(InputFilePath, OutputFilePath,
                targetFormat: targetFormat,
                progress: progress);

            StatusText = "转换完成 ✓";
            History.Insert(0, new ConversionTaskItem
            {
                InputFile = InputFileName ?? InputFilePath,
                OutputFile = Path.GetFileName(OutputFilePath),
                Timestamp = DateTime.Now,
                Status = "成功"
            });
        }
        catch (Exception ex)
        {
            StatusText = $"转换失败: {ex.Message}";
            History.Insert(0, new ConversionTaskItem
            {
                InputFile = InputFileName ?? InputFilePath,
                OutputFile = Path.GetFileName(OutputFilePath),
                Timestamp = DateTime.Now,
                Status = $"失败: {ex.Message}"
            });
        }
        finally
        {
            IsConverting = false;
        }
    }

    private void UpdateOutputPath()
    {
        if (InputFilePath == null) return;
        var dir = Path.GetDirectoryName(InputFilePath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(InputFilePath);
        OutputFilePath = Path.Combine(dir, $"{name}_converted{SelectedOutputFormat}");
    }

    partial void OnSelectedOutputFormatChanged(String value)
    {
        UpdateOutputPath();
    }

    private static Avalonia.Controls.Window? GetMainWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
            return lifetime.MainWindow;
        return null;
    }
}

/// <summary>转换任务项</summary>
public class ConversionTaskItem
{
    public String InputFile { get; set; } = "";
    public String OutputFile { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public String Status { get; set; } = "";
    public String TimeText => Timestamp.ToString("HH:mm:ss");
}
