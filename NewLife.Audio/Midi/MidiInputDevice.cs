using System;
using System.Collections.Generic;

namespace NewLife.Audio.Midi;

/// <summary>MIDI 输入设备工厂</summary>
/// <remarks>Windows 使用 WinMM `midiIn`；其他平台返回空列表并在打开时抛出 <see cref="PlatformNotSupportedException"/>。</remarks>
public static class MidiInputDevice
{
    /// <summary>枚举输入设备</summary>
    /// <returns>设备列表；非 Windows 返回空列表</returns>
    public static IList<MidiDeviceInfo> Enumerate()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) return [];

        return WinMmMidiInput.Enumerate();
    }

    /// <summary>打开输入设备</summary>
    /// <param name="index">设备索引</param>
    /// <returns>MIDI 输入</returns>
    public static IMidiInput Open(Int32 index)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            throw new PlatformNotSupportedException("MIDI input is only implemented on Windows (WinMM).");

        return WinMmMidiInput.Open(index);
    }

    /// <summary>打开默认输入设备</summary>
    /// <returns>MIDI 输入</returns>
    public static IMidiInput OpenDefault()
    {
        var devices = Enumerate();
        if (devices.Count == 0) throw new InvalidOperationException("No MIDI input device is available.");

        return Open(devices[0].Index);
    }
}
