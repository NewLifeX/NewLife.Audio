namespace NewLife.Audio.Midi;

/// <summary>UMP（Universal MIDI Packet）消息类型</summary>
public enum UmpMessageType : Byte
{
    /// <summary>Utility（NOOP/JR 等）</summary>
    Utility = 0x0,

    /// <summary>System Real Time / Common</summary>
    System = 0x1,

    /// <summary>MIDI 1.0 Channel Voice</summary>
    Midi1ChannelVoice = 0x2,

    /// <summary>Data（64 位）</summary>
    Data = 0x3,

    /// <summary>MIDI 2.0 Channel Voice</summary>
    Midi2ChannelVoice = 0x4,

    /// <summary>SysEx7（64 位）</summary>
    SysEx7 = 0x5,

    /// <summary>SysEx8（64 位）</summary>
    SysEx8 = 0x6,

    /// <summary>Flex Data（128 位）</summary>
    FlexData = 0xD,

    /// <summary>Stream（128 位）</summary>
    Stream = 0xF,
}
