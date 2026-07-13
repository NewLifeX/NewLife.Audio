using CommunityToolkit.Mvvm.ComponentModel;
using AudioStudio.ViewModels.Player;
using AudioStudio.ViewModels.Convert;
using AudioStudio.ViewModels.Devices;
using AudioStudio.ViewModels.Record;
using AudioStudio.ViewModels.Dsp;
using AudioStudio.ViewModels.Tools;

namespace AudioStudio.ViewModels;

/// <summary>主窗口视图模型</summary>
public partial class MainViewModel : ViewModelBase
{
    /// <summary>播放器</summary>
    public PlaybackViewModel Playback { get; }

    /// <summary>格式转换</summary>
    public ConversionViewModel Conversion { get; }

    /// <summary>音频设备</summary>
    public DeviceViewModel Device { get; }

    /// <summary>录制</summary>
    public RecordingViewModel Recording { get; }

    /// <summary>DSP 处理</summary>
    public DspViewModel Dsp { get; }

    /// <summary>编解码工具</summary>
    public ToolsViewModel Tools { get; }

    /// <summary>当前选中 Tab 索引</summary>
    [ObservableProperty]
    private Int32 _selectedTab;

    public MainViewModel()
    {
        Playback = new PlaybackViewModel();
        Conversion = new ConversionViewModel();
        Device = new DeviceViewModel();
        Recording = new RecordingViewModel();
        Dsp = new DspViewModel();
        Tools = new ToolsViewModel();
    }
}
