using System;
using System.Collections.Generic;
using System.IO;

namespace NewLife.Audio.Midi;

/// <summary>MIDI 文件写入器</summary>
internal static class MidiWriter
{
    /// <summary>序列化 MIDI 文件</summary>
    /// <param name="file">MIDI 文件</param>
    /// <returns>SMF 数据</returns>
    public static Byte[] Write(MidiFile file)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));

        using var ms = new MemoryStream();
        WriteAscii(ms, "MThd");
        WriteInt32(ms, 6);
        WriteInt16(ms, (Int32)file.Format);
        WriteInt16(ms, file.Tracks.Count);
        WriteInt16(ms, file.Division & 0xFFFF);

        foreach (var track in file.Tracks) WriteTrack(ms, track);

        return ms.ToArray();
    }

    private static void WriteTrack(MemoryStream stream, MidiTrack track)
    {
        using var body = new MemoryStream();
        var events = new List<IndexedEvent>(track.Events.Count);
        var hasName = false;
        for (var i = 0; i < track.Events.Count; i++)
        {
            var item = track.Events[i];
            if (item.Type == MidiEventType.Meta && item.MetaType == 0x2F) continue;
            if (item.Type == MidiEventType.Meta && item.MetaType == 0x03) hasName = true;
            events.Add(new IndexedEvent(item, i));
        }
        if (!hasName && !String.IsNullOrEmpty(track.Name)) events.Add(new IndexedEvent(MidiEvent.TrackName(0, track.Name), -1));
        events.Sort(CompareEvent);

        var endTick = track.EndTick;
        var lastTick = 0L;
        foreach (var indexed in events)
        {
            var item = indexed.Event;
            var delta = item.Tick - lastTick;
            if (delta < 0) delta = 0;
            WriteVlq(body, delta);
            lastTick = item.Tick;

            WriteEvent(body, item);
        }

        WriteVlq(body, Math.Max(0, endTick - lastTick));
        WriteEvent(body, MidiEvent.EndOfTrack(endTick));

        var data = body.ToArray();
        WriteAscii(stream, "MTrk");
        WriteInt32(stream, data.Length);
        stream.Write(data, 0, data.Length);
    }

    private static void WriteEvent(MemoryStream stream, MidiEvent item)
    {
        switch (item.Type)
        {
            case MidiEventType.NoteOff:
            case MidiEventType.NoteOn:
            case MidiEventType.PolyphonicKeyPressure:
            case MidiEventType.ControlChange:
            case MidiEventType.PitchBend:
                stream.WriteByte(GetChannelStatus(item));
                stream.WriteByte((Byte)(item.Data1 & 0x7F));
                stream.WriteByte((Byte)(item.Data2 & 0x7F));
                break;

            case MidiEventType.ProgramChange:
            case MidiEventType.ChannelPressure:
                stream.WriteByte(GetChannelStatus(item));
                stream.WriteByte((Byte)(item.Data1 & 0x7F));
                break;

            case MidiEventType.Meta:
                stream.WriteByte(0xFF);
                stream.WriteByte((Byte)(item.MetaType & 0xFF));
                var meta = item.Data ?? [];
                WriteVlq(stream, meta.Length);
                stream.Write(meta, 0, meta.Length);
                break;

            case MidiEventType.SysEx:
                stream.WriteByte(item.Status == 0xF7 ? (Byte)0xF7 : (Byte)0xF0);
                var sysex = item.Data ?? [];
                WriteVlq(stream, sysex.Length);
                stream.Write(sysex, 0, sysex.Length);
                break;

            case MidiEventType.System:
                stream.WriteByte(item.Status);
                var system = item.Data ?? [];
                stream.Write(system, 0, system.Length);
                break;

            default:
                throw new InvalidDataException("Unsupported MIDI event type.");
        }
    }

    private static Byte GetChannelStatus(MidiEvent item)
    {
        if (item.Status >= 0x80 && item.Status < 0xF0) return item.Status;

        var type = item.Type switch
        {
            MidiEventType.NoteOff => 0x80,
            MidiEventType.NoteOn => 0x90,
            MidiEventType.PolyphonicKeyPressure => 0xA0,
            MidiEventType.ControlChange => 0xB0,
            MidiEventType.ProgramChange => 0xC0,
            MidiEventType.ChannelPressure => 0xD0,
            MidiEventType.PitchBend => 0xE0,
            _ => 0x90,
        };

        return (Byte)(type | (item.Channel & 0x0F));
    }

    private static void WriteVlq(MemoryStream stream, Int64 value)
    {
        if (value < 0) value = 0;
        if (value > 0x0FFFFFFF) throw new InvalidDataException("The MIDI delta time exceeds the variable-length quantity limit.");

        var buffer = new Byte[4];
        var index = 3;
        buffer[index] = (Byte)(value & 0x7F);
        value >>= 7;
        while (value > 0)
        {
            index--;
            buffer[index] = (Byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }

        stream.Write(buffer, index, 4 - index);
    }

    private static void WriteAscii(MemoryStream stream, String text)
    {
        for (var i = 0; i < text.Length; i++) stream.WriteByte((Byte)text[i]);
    }

    private static void WriteInt16(MemoryStream stream, Int32 value)
    {
        stream.WriteByte((Byte)((value >> 8) & 0xFF));
        stream.WriteByte((Byte)(value & 0xFF));
    }

    private static void WriteInt32(MemoryStream stream, Int32 value)
    {
        stream.WriteByte((Byte)((value >> 24) & 0xFF));
        stream.WriteByte((Byte)((value >> 16) & 0xFF));
        stream.WriteByte((Byte)((value >> 8) & 0xFF));
        stream.WriteByte((Byte)(value & 0xFF));
    }

    private static Int32 CompareEvent(IndexedEvent a, IndexedEvent b)
    {
        var result = a.Event.Tick.CompareTo(b.Event.Tick);
        if (result != 0) return result;

        var aOff = a.Event.Type == MidiEventType.NoteOff || (a.Event.Type == MidiEventType.NoteOn && a.Event.Data2 == 0);
        var bOff = b.Event.Type == MidiEventType.NoteOff || (b.Event.Type == MidiEventType.NoteOn && b.Event.Data2 == 0);
        if (aOff != bOff) return aOff ? -1 : 1;

        return a.Index.CompareTo(b.Index);
    }

    private readonly struct IndexedEvent
    {
        public MidiEvent Event { get; }

        public Int32 Index { get; }

        public IndexedEvent(MidiEvent e, Int32 index)
        {
            Event = e;
            Index = index;
        }
    }
}
