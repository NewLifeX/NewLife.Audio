using NewLife.Audio.DSP;

namespace NewLife.Audio.Devices;

/// <summary>音频设备信息</summary>
public class AudioDeviceInfo
{
    /// <summary>设备ID</summary>
    public String Id { get; set; }

    /// <summary>设备名称</summary>
    public String Name { get; set; }

    /// <summary>是否为输入设备</summary>
    public Boolean IsInput { get; set; }

    /// <summary>是否为默认设备</summary>
    public Boolean IsDefault { get; set; }

    /// <summary>支持的音频格式列表</summary>
    public List<AudioFormat> SupportedFormats { get; set; } = [];
}
