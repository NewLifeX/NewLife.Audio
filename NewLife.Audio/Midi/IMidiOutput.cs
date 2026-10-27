using System;

namespace NewLife.Audio.Midi;

/// <summary>MIDI 输出设备接口</summary>
public interface IMidiOutput : IDisposable
{
    /// <summary>设备信息</summary>
    MidiDeviceInfo Device { get; }

    /// <summary>发送短消息（Note/CC/Program/PitchBend）</summary>
    /// <param name="status">状态字节（含通道）</param>
    /// <param name="data1">数据 1</param>
    /// <param name="data2">数据 2</param>
    void SendShortMessage(Byte status, Byte data1, Byte data2 = 0);

    /// <summary>发送 SysEx</summary>
    /// <param name="data">SysEx 数据（不含 F0/F7 边界）</param>
    void SendSysEx(Byte[] data);

    /// <summary>发送 MIDI 事件</summary>
    /// <param name="e">MIDI 事件</param>
    void Send(MidiEvent e);

    /// <summary>复位输出（停止所有音符）</summary>
    void Reset();
}
