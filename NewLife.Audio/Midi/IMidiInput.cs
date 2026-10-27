using System;

namespace NewLife.Audio.Midi;

/// <summary>MIDI 输入设备接口</summary>
public interface IMidiInput : IDisposable
{
    /// <summary>设备信息</summary>
    MidiDeviceInfo Device { get; }

    /// <summary>收到 MIDI 消息</summary>
    event EventHandler<MidiEvent> MessageReceived;

    /// <summary>开始接收</summary>
    void Start();

    /// <summary>停止接收</summary>
    void Stop();
}
