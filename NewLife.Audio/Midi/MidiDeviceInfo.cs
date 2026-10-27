using System;

namespace NewLife.Audio.Midi;

/// <summary>MIDI 设备信息</summary>
public sealed class MidiDeviceInfo
{
    /// <summary>设备索引</summary>
    public Int32 Index { get; set; }

    /// <summary>设备名称</summary>
    public String Name { get; set; }

    /// <summary>制造商</summary>
    public String Manufacturer { get; set; }

    /// <summary>驱动版本</summary>
    public Int32 Version { get; set; }

    /// <inheritdoc/>
    public override String ToString() => Name ?? String.Empty;
}
