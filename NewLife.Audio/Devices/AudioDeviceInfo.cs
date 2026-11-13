using NewLife.Audio.DSP;

namespace NewLife.Audio.Devices;

/// <summary>音频设备信息</summary>
public class AudioDeviceInfo
{
    /// <summary>设备ID</summary>
    public String Id { get; set; }

    /// <summary>设备名称</summary>
    public String Name { get; set; }

    /// <summary>设备接口友好名称（如 Realtek High Definition Audio）。后端可查询时填充</summary>
    public String DeviceFriendlyName { get; set; }

    /// <summary>是否为输入设备</summary>
    public Boolean IsInput { get; set; }

    /// <summary>是否为默认设备</summary>
    public Boolean IsDefault { get; set; }

    /// <summary>设备共享模式混音格式（声道数/采样率/位深）。后端可查询时填充，否则为 null</summary>
    public AudioFormat MixFormat { get; set; }

    /// <summary>支持的音频格式列表</summary>
    public List<AudioFormat> SupportedFormats { get; set; } = [];
}
