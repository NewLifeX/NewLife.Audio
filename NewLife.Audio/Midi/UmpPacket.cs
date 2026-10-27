using System;

namespace NewLife.Audio.Midi;

/// <summary>UMP 包</summary>
/// <remarks>
/// 支持 MIDI 1.0/2.0 Channel Voice 的解析与 SysEx7 负载提取；
/// Data/Flex Data/Stream 等保留原始字，供上层扩展。
/// </remarks>
public sealed class UmpPacket
{
    /// <summary>原始 32 位字（大端语义）</summary>
    public UInt32[] Words { get; }

    /// <summary>消息类型</summary>
    public UmpMessageType MessageType => (UmpMessageType)((Words[0] >> 28) & 0x0F);

    /// <summary>UMP Group（0~15）</summary>
    public Int32 Group => (Int32)((Words[0] >> 24) & 0x0F);

    /// <summary>状态半字节（Channel Voice 的 0x8~0xE）</summary>
    public Int32 StatusNibble => (Int32)((Words[0] >> 20) & 0x0F);

    /// <summary>通道号（0~15）</summary>
    public Int32 Channel => (Int32)((Words[0] >> 16) & 0x0F);

    /// <summary>包字数</summary>
    public Int32 WordCount => Words.Length;

    /// <summary>创建 UMP 包</summary>
    /// <param name="words">32 位字数组</param>
    public UmpPacket(UInt32[] words)
    {
        if (words == null || words.Length == 0) throw new ArgumentNullException(nameof(words));
        if (words.Length != GetWordCount((UmpMessageType)((words[0] >> 28) & 0x0F)))
            throw new ArgumentException("The UMP word count does not match the message type.", nameof(words));

        Words = words;
    }

    /// <summary>根据消息类型获取包字数</summary>
    /// <param name="type">消息类型</param>
    /// <returns>32 位字数</returns>
    public static Int32 GetWordCount(UmpMessageType type) => type switch
    {
        UmpMessageType.Data or UmpMessageType.Midi2ChannelVoice or UmpMessageType.SysEx7 or UmpMessageType.SysEx8 => 2,
        UmpMessageType.FlexData or UmpMessageType.Stream => 4,
        _ => 1,
    };

    /// <summary>转换为 MIDI 事件（仅 Channel Voice）</summary>
    /// <param name="tick">事件时间</param>
    /// <returns>MIDI 事件；非 Channel Voice 返回 null</returns>
    public MidiEvent ToMidiEvent(Int64 tick)
    {
        if (MessageType == UmpMessageType.Midi1ChannelVoice) return ToMidi1Event(tick);
        if (MessageType == UmpMessageType.Midi2ChannelVoice) return ToMidi2Event(tick);
        return null;
    }

    /// <summary>提取 SysEx7 负载</summary>
    /// <returns>负载字节（最多 6 字节）</returns>
    public Byte[] GetSysEx7Payload()
    {
        if (MessageType != UmpMessageType.SysEx7) return [];

        var count = (Int32)((Words[0] >> 16) & 0x0F);
        if (count < 0) count = 0;
        if (count > 6) count = 6;

        var data = new Byte[count];
        if (count > 0) data[0] = (Byte)((Words[0] >> 8) & 0xFF);
        if (count > 1) data[1] = (Byte)(Words[0] & 0xFF);
        if (count > 2) data[2] = (Byte)((Words[1] >> 24) & 0xFF);
        if (count > 3) data[3] = (Byte)((Words[1] >> 16) & 0xFF);
        if (count > 4) data[4] = (Byte)((Words[1] >> 8) & 0xFF);
        if (count > 5) data[5] = (Byte)(Words[1] & 0xFF);

        return data;
    }

    /// <summary>SysEx7 状态（0 完整、1 开始、2 继续、3 结束）</summary>
    public Int32 SysEx7Status => (Int32)((Words[0] >> 20) & 0x0F);

    private MidiEvent ToMidi1Event(Int64 tick)
    {
        var status = StatusNibble;
        var channel = Channel;
        var data1 = (Int32)((Words[0] >> 8) & 0x7F);
        var data2 = (Int32)(Words[0] & 0x7F);

        return CreateEvent(tick, status, channel, data1, data2);
    }

    private MidiEvent ToMidi2Event(Int64 tick)
    {
        var status = StatusNibble;
        var channel = Channel;
        var note = (Int32)((Words[0] >> 8) & 0x7F);

        switch (status)
        {
            case 0x8:
            case 0x9:
                {
                    var velocity = (Int32)((Words[1] >> 16) & 0xFFFF);
                    return CreateEvent(tick, status, channel, note, Scale16To7(velocity));
                }
            case 0xA:
                {
                    var pressure = (Int32)(Words[1] & 0xFFFFFFFF);
                    return CreateEvent(tick, status, channel, note, Scale32To7(pressure));
                }
            case 0xB:
                {
                    var controller = (Int32)((Words[0] >> 8) & 0x7F);
                    var value = (Int32)(Words[1] & 0xFFFFFFFF);
                    return CreateEvent(tick, status, channel, controller, Scale32To7(value));
                }
            case 0xC:
                return CreateEvent(tick, status, channel, note, 0);
            case 0xD:
                {
                    var pressure = (Int32)(Words[1] & 0xFFFFFFFF);
                    return CreateEvent(tick, status, channel, Scale32To7(pressure), 0);
                }
            case 0xE:
                {
                    var value = (Int64)(Words[1] & 0xFFFFFFFF);
                    var bend = (Int32)((value - 0x80000000L) >> 18);
                    return MidiEvent.PitchBend(tick, channel, bend);
                }
            default:
                return null;
        }
    }

    private static MidiEvent CreateEvent(Int64 tick, Int32 status, Int32 channel, Int32 data1, Int32 data2)
    {
        switch (status)
        {
            case 0x8:
                return MidiEvent.NoteOff(tick, channel, data1, data2);
            case 0x9:
                return data2 > 0 ? MidiEvent.NoteOn(tick, channel, data1, data2) : MidiEvent.NoteOff(tick, channel, data1);
            case 0xA:
                return new MidiEvent
                {
                    Tick = tick,
                    Type = MidiEventType.PolyphonicKeyPressure,
                    Status = (Byte)(0xA0 | (channel & 0x0F)),
                    Channel = channel & 0x0F,
                    Data1 = data1 & 0x7F,
                    Data2 = data2 & 0x7F,
                };
            case 0xB:
                return MidiEvent.ControlChange(tick, channel, data1, data2);
            case 0xC:
                return MidiEvent.ProgramChange(tick, channel, data1);
            case 0xD:
                return new MidiEvent
                {
                    Tick = tick,
                    Type = MidiEventType.ChannelPressure,
                    Status = (Byte)(0xD0 | (channel & 0x0F)),
                    Channel = channel & 0x0F,
                    Data1 = data1 & 0x7F,
                };
            case 0xE:
                return MidiEvent.PitchBend(tick, channel, (((data2 & 0x7F) << 7) | (data1 & 0x7F)) - 8192);
            default:
                return null;
        }
    }

    private static Int32 Scale16To7(Int32 value)
    {
        var scaled = (value * 127 + 32767) / 65535;
        if (scaled < 0) scaled = 0;
        if (scaled > 127) scaled = 127;
        return scaled;
    }

    private static Int32 Scale32To7(Int32 value)
    {
        var scaled = (Int64)(value & 0xFFFFFFFF) * 127 / 0xFFFFFFFFL;
        if (scaled < 0) scaled = 0;
        if (scaled > 127) scaled = 127;
        return (Int32)scaled;
    }
}
