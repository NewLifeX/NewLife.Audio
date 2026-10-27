namespace NewLife.Audio.Midi;

/// <summary>MIDI 事件类型</summary>
public enum MidiEventType
{
    /// <summary>音符关闭</summary>
    NoteOff,

    /// <summary>音符开启（力度为 0 时等价于音符关闭）</summary>
    NoteOn,

    /// <summary>复音键压力</summary>
    PolyphonicKeyPressure,

    /// <summary>控制器变更</summary>
    ControlChange,

    /// <summary>音色变更</summary>
    ProgramChange,

    /// <summary>通道压力</summary>
    ChannelPressure,

    /// <summary>弯音</summary>
    PitchBend,

    /// <summary>Meta 事件（速度、拍号、轨道名、歌词等）</summary>
    Meta,

    /// <summary>系统独占消息</summary>
    SysEx,

    /// <summary>系统公共消息（保留原样）</summary>
    System,
}
