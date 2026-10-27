using System;

namespace NewLife.Audio.Midi;

/// <summary>MIDI 事件</summary>
/// <remarks>
/// 统一事件模型保留原始状态字节与数据，未识别的 Meta/SysEx/系统消息可原样写回。
/// 时间单位为 tick，具体秒数由 <see cref="MidiTempoMap"/> 换算。
/// </remarks>
public sealed class MidiEvent
{
    /// <summary>事件时间（tick）</summary>
    public Int64 Tick { get; set; }

    /// <summary>事件类型</summary>
    public MidiEventType Type { get; set; }

    /// <summary>原始状态字节（通道事件含通道号，Meta 为 0xFF）</summary>
    public Byte Status { get; set; }

    /// <summary>通道号（0~15）</summary>
    public Int32 Channel { get; set; }

    /// <summary>数据 1：音符号、控制器号、音色号等</summary>
    public Int32 Data1 { get; set; }

    /// <summary>数据 2：力度、控制器值等</summary>
    public Int32 Data2 { get; set; }

    /// <summary>Meta 事件类型（0x03 轨道名、0x51 速度、0x58 拍号等）</summary>
    public Int32 MetaType { get; set; }

    /// <summary>附加数据（Meta/SysEx 原始字节）</summary>
    public Byte[] Data { get; set; } = [];

    /// <summary>音符号</summary>
    public Int32 NoteNumber => Data1;

    /// <summary>力度</summary>
    public Int32 Velocity => Data2;

    /// <summary>控制器号</summary>
    public Int32 Controller => Data1;

    /// <summary>控制器值</summary>
    public Int32 ControllerValue => Data2;

    /// <summary>音色号</summary>
    public Int32 Program => Data1;

    /// <summary>弯音值（-8192~8191，0 为居中）</summary>
    public Int32 PitchBendValue => (((Data2 & 0x7F) << 7) | (Data1 & 0x7F)) - 8192;

    /// <summary>速度（微秒/四分音符），仅对 0x51 Meta 事件有效</summary>
    public Int32 TempoMicroseconds => MetaType == 0x51 && Data != null && Data.Length >= 3
        ? (Data[0] << 16) | (Data[1] << 8) | Data[2]
        : 0;

    /// <summary>拍号分子，仅对 0x58 Meta 事件有效</summary>
    public Int32 TimeSignatureNumerator => MetaType == 0x58 && Data != null && Data.Length >= 2 ? Data[0] : 4;

    /// <summary>拍号分母，仅对 0x58 Meta 事件有效</summary>
    public Int32 TimeSignatureDenominator => MetaType == 0x58 && Data != null && Data.Length >= 2 ? 1 << Data[1] : 4;

    /// <summary>创建音符开启事件</summary>
    /// <param name="tick">事件时间</param>
    /// <param name="channel">通道号（0~15）</param>
    /// <param name="note">音符号（0~127）</param>
    /// <param name="velocity">力度（0~127）</param>
    /// <returns>MIDI 事件</returns>
    public static MidiEvent NoteOn(Int64 tick, Int32 channel, Int32 note, Int32 velocity) => new()
    {
        Tick = tick,
        Type = MidiEventType.NoteOn,
        Status = (Byte)(0x90 | (channel & 0x0F)),
        Channel = channel & 0x0F,
        Data1 = note & 0x7F,
        Data2 = velocity & 0x7F,
    };

    /// <summary>创建音符关闭事件</summary>
    /// <param name="tick">事件时间</param>
    /// <param name="channel">通道号（0~15）</param>
    /// <param name="note">音符号（0~127）</param>
    /// <param name="velocity">释放力度（0~127）</param>
    /// <returns>MIDI 事件</returns>
    public static MidiEvent NoteOff(Int64 tick, Int32 channel, Int32 note, Int32 velocity = 0) => new()
    {
        Tick = tick,
        Type = MidiEventType.NoteOff,
        Status = (Byte)(0x80 | (channel & 0x0F)),
        Channel = channel & 0x0F,
        Data1 = note & 0x7F,
        Data2 = velocity & 0x7F,
    };

    /// <summary>创建控制器事件</summary>
    /// <param name="tick">事件时间</param>
    /// <param name="channel">通道号（0~15）</param>
    /// <param name="controller">控制器号（0~127）</param>
    /// <param name="value">控制器值（0~127）</param>
    /// <returns>MIDI 事件</returns>
    public static MidiEvent ControlChange(Int64 tick, Int32 channel, Int32 controller, Int32 value) => new()
    {
        Tick = tick,
        Type = MidiEventType.ControlChange,
        Status = (Byte)(0xB0 | (channel & 0x0F)),
        Channel = channel & 0x0F,
        Data1 = controller & 0x7F,
        Data2 = value & 0x7F,
    };

    /// <summary>创建音色变更事件</summary>
    /// <param name="tick">事件时间</param>
    /// <param name="channel">通道号（0~15）</param>
    /// <param name="program">音色号（0~127）</param>
    /// <returns>MIDI 事件</returns>
    public static MidiEvent ProgramChange(Int64 tick, Int32 channel, Int32 program) => new()
    {
        Tick = tick,
        Type = MidiEventType.ProgramChange,
        Status = (Byte)(0xC0 | (channel & 0x0F)),
        Channel = channel & 0x0F,
        Data1 = program & 0x7F,
    };

    /// <summary>创建弯音事件</summary>
    /// <param name="tick">事件时间</param>
    /// <param name="channel">通道号（0~15）</param>
    /// <param name="value">弯音值（-8192~8191，0 为居中）</param>
    /// <returns>MIDI 事件</returns>
    public static MidiEvent PitchBend(Int64 tick, Int32 channel, Int32 value)
    {
        var raw = value + 8192;
        if (raw < 0) raw = 0;
        if (raw > 16383) raw = 16383;

        return new MidiEvent
        {
            Tick = tick,
            Type = MidiEventType.PitchBend,
            Status = (Byte)(0xE0 | (channel & 0x0F)),
            Channel = channel & 0x0F,
            Data1 = raw & 0x7F,
            Data2 = (raw >> 7) & 0x7F,
        };
    }

    /// <summary>创建 Meta 事件</summary>
    /// <param name="tick">事件时间</param>
    /// <param name="metaType">Meta 类型</param>
    /// <param name="data">附加数据</param>
    /// <returns>MIDI 事件</returns>
    public static MidiEvent Meta(Int64 tick, Int32 metaType, Byte[] data) => new()
    {
        Tick = tick,
        Type = MidiEventType.Meta,
        Status = 0xFF,
        MetaType = metaType & 0xFF,
        Data = data ?? [],
    };

    /// <summary>创建速度事件</summary>
    /// <param name="tick">事件时间</param>
    /// <param name="microsecondsPerQuarter">每四分音符微秒数（500000 = 120 BPM）</param>
    /// <returns>MIDI 事件</returns>
    public static MidiEvent Tempo(Int64 tick, Int32 microsecondsPerQuarter) => Meta(tick, 0x51,
    [
        (Byte)((microsecondsPerQuarter >> 16) & 0xFF),
        (Byte)((microsecondsPerQuarter >> 8) & 0xFF),
        (Byte)(microsecondsPerQuarter & 0xFF),
    ]);

    /// <summary>创建拍号事件</summary>
    /// <param name="tick">事件时间</param>
    /// <param name="numerator">拍号分子</param>
    /// <param name="denominator">拍号分母（如 4 表示四四拍）</param>
    /// <param name="clocksPerClick">每拍 MIDI 时钟数</param>
    /// <param name="thirtySecondsPerQuarter">每四分音符的 32 分音符数</param>
    /// <returns>MIDI 事件</returns>
    public static MidiEvent TimeSignature(Int64 tick, Int32 numerator, Int32 denominator, Int32 clocksPerClick = 24, Int32 thirtySecondsPerQuarter = 8)
    {
        var power = 0;
        var value = denominator > 0 ? denominator : 4;
        while (value > 1 && power < 7)
        {
            value >>= 1;
            power++;
        }

        return Meta(tick, 0x58,
        [
            (Byte)(numerator & 0xFF),
            (Byte)power,
            (Byte)(clocksPerClick & 0xFF),
            (Byte)(thirtySecondsPerQuarter & 0xFF),
        ]);
    }

    /// <summary>创建轨道名事件</summary>
    /// <param name="tick">事件时间</param>
    /// <param name="name">轨道名</param>
    /// <returns>MIDI 事件</returns>
    public static MidiEvent TrackName(Int64 tick, String name) => Meta(tick, 0x03, MidiText.Encode(name));

    /// <summary>创建结束事件</summary>
    /// <param name="tick">事件时间</param>
    /// <returns>MIDI 事件</returns>
    public static MidiEvent EndOfTrack(Int64 tick) => Meta(tick, 0x2F, []);
}
