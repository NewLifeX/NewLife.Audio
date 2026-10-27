using System;
using System.Collections.Generic;
using System.IO;
using NewLife.Audio.Midi;
using Xunit;

namespace XUnitTest.Midi;

public class MidiFileTests
{
    [Fact(DisplayName = "MIDI 空文件写入后可以解析")]
    public void EmptyFile_RoundTrips()
    {
        var midi = MidiFile.Create(MidiFileFormat.SingleTrack, 480);
        midi.Tracks.Add(new MidiTrack());

        var data = midi.ToArray();
        var parsed = MidiFile.Parse(data);

        Assert.Equal(MidiFileFormat.SingleTrack, parsed.Format);
        Assert.Equal(480, parsed.TicksPerQuarterNote);
        Assert.Single(parsed.Tracks);
    }

    [Fact(DisplayName = "MIDI VLQ 边界 tick 往返一致")]
    public void VlqBoundaries_RoundTrip()
    {
        var ticks = new Int64[] { 0, 1, 127, 128, 16383, 16384, 2097151, 2097152, 268435455 };
        var midi = MidiFile.Create(MidiFileFormat.SingleTrack, 480);
        var track = new MidiTrack();
        midi.Tracks.Add(track);

        var index = 0;
        foreach (var tick in ticks)
        {
            track.Add(MidiEvent.NoteOn(tick, 0, 60, 100));
            track.Add(MidiEvent.NoteOff(tick + 1, 0, 60));
            index++;
        }

        var parsed = MidiFile.Parse(midi.ToArray());
        var notes = parsed.Tracks[0].GetNotes();

        Assert.Equal(ticks.Length, notes.Count);
        for (var i = 0; i < ticks.Length; i++) Assert.Equal(ticks[i], notes[i].StartTick);
    }

    [Fact(DisplayName = "MIDI running status 解析正确")]
    public void RunningStatus_Parses()
    {
        var data = BuildRunningStatusFile();
        var midi = MidiFile.Parse(data);
        var events = midi.Tracks[0].Events;

        Assert.Contains(events, e => e.Type == MidiEventType.NoteOn && e.Channel == 0 && e.NoteNumber == 60 && e.Velocity == 100);
        Assert.Contains(events, e => e.Type == MidiEventType.NoteOn && e.Channel == 0 && e.NoteNumber == 62 && e.Velocity == 100);
        Assert.Contains(events, e => e.Type == MidiEventType.NoteOff && e.Channel == 0 && e.NoteNumber == 60);
        Assert.Contains(events, e => e.Type == MidiEventType.NoteOff && e.Channel == 0 && e.NoteNumber == 62);

        var notes = midi.Tracks[0].GetNotes();
        Assert.Equal(2, notes.Count);
    }

    [Fact(DisplayName = "MIDI 速度映射按 tempo 分段积分")]
    public void TempoMap_IntegratesSegments()
    {
        var midi = MidiFile.Create(MidiFileFormat.SingleTrack, 480);
        var track = new MidiTrack();
        midi.Tracks.Add(track);
        track.Add(MidiEvent.Tempo(0, 500000));
        track.Add(MidiEvent.Tempo(480, 1000000));
        track.Add(MidiEvent.NoteOn(0, 0, 60, 100));
        track.Add(MidiEvent.NoteOff(960, 0, 60));

        var map = midi.GetTempoMap();

        Assert.Equal(0.5, map.TickToSeconds(480), 3);
        Assert.Equal(1.5, map.TickToSeconds(960), 3);
        Assert.Equal(60.0, map.BpmAt(480), 3);
        Assert.Equal(960L, map.SecondsToTick(1.5));
    }

    [Fact(DisplayName = "MIDI 未知 Meta 与 SysEx 原样保留")]
    public void UnknownMetaAndSysEx_RoundTrip()
    {
        var midi = MidiFile.Create(MidiFileFormat.SingleTrack, 480);
        var track = new MidiTrack();
        midi.Tracks.Add(track);
        track.Add(MidiEvent.TrackName(0, "测试轨"));
        track.Add(MidiEvent.Meta(10, 0x7F, new Byte[] { 1, 2, 3 }));
        track.Add(new MidiEvent { Tick = 20, Type = MidiEventType.SysEx, Status = 0xF0, Data = new Byte[] { 0x7D, 1, 2 } });
        track.Add(MidiEvent.NoteOn(30, 0, 60, 100));
        track.Add(MidiEvent.NoteOff(480, 0, 60));

        var parsed = MidiFile.Parse(midi.ToArray());
        var events = parsed.Tracks[0].Events;

        Assert.Equal("测试轨", parsed.Tracks[0].Name);
        Assert.Contains(events, e => e.Type == MidiEventType.Meta && e.MetaType == 0x7F && e.Data.Length == 3 && e.Data[2] == 3);
        Assert.Contains(events, e => e.Type == MidiEventType.SysEx && e.Data.Length == 3 && e.Data[0] == 0x7D);
    }

    [Fact(DisplayName = "MIDI SMPTE 时基按 tick 每秒换算")]
    public void SmpteDivision_ConvertsSeconds()
    {
        var midi = MidiFile.Create(MidiFileFormat.SingleTrack, unchecked((Int16)0xE728));
        midi.Tracks.Add(new MidiTrack());

        Assert.True(midi.IsSmpte);
        Assert.Equal(1000.0, midi.SmpteTicksPerSecond, 3);
        Assert.Equal(1.0, midi.GetTempoMap().TickToSeconds(1000), 3);
    }

    [Fact(DisplayName = "MIDI 重叠同音按先进先出配对")]
    public void OverlappingNotes_PairFifo()
    {
        var midi = MidiFile.Create(MidiFileFormat.SingleTrack, 480);
        var track = new MidiTrack();
        midi.Tracks.Add(track);
        track.Add(MidiEvent.NoteOn(0, 0, 60, 10));
        track.Add(MidiEvent.NoteOn(100, 0, 60, 20));
        track.Add(MidiEvent.NoteOff(200, 0, 60));
        track.Add(MidiEvent.NoteOff(300, 0, 60));

        var notes = track.GetNotes();

        Assert.Equal(2, notes.Count);
        Assert.Equal(10, notes[0].Velocity);
        Assert.Equal(0, notes[0].StartTick);
        Assert.Equal(200, notes[0].EndTick);
        Assert.Equal(20, notes[1].Velocity);
        Assert.Equal(100, notes[1].StartTick);
        Assert.Equal(300, notes[1].EndTick);
    }

    [Fact(DisplayName = "MIDI 未闭合音符在轨道结束时截断")]
    public void UnclosedNote_EndsAtTrackEnd()
    {
        var midi = MidiFile.Create(MidiFileFormat.SingleTrack, 480);
        var track = new MidiTrack();
        midi.Tracks.Add(track);
        track.Add(MidiEvent.NoteOn(0, 0, 60, 100));
        track.Add(MidiEvent.Meta(480, 0x01, new Byte[] { 1 }));

        var notes = track.GetNotes();

        Assert.Single(notes);
        Assert.Equal(480, notes[0].EndTick);
    }

    [Fact(DisplayName = "MIDI division 为零时拒绝解析")]
    public void ZeroDivision_Throws()
    {
        var data = new Byte[]
        {
            (Byte)'M', (Byte)'T', (Byte)'h', (Byte)'d', 0, 0, 0, 6,
            0, 0, 0, 1, 0, 0,
            (Byte)'M', (Byte)'T', (Byte)'r', (Byte)'k', 0, 0, 0, 4,
            0, 0xFF, 0x2F, 0x00,
        };

        Assert.Throws<InvalidDataException>(() => MidiFile.Parse(data));
    }

    [Fact(DisplayName = "MIDI 文件时长取所有轨道最大值")]
    public void Duration_UsesLongestTrack()
    {
        var midi = MidiFile.Create(MidiFileFormat.MultiTrack, 480);
        var a = new MidiTrack();
        var b = new MidiTrack();
        midi.Tracks.Add(a);
        midi.Tracks.Add(b);
        a.Add(MidiEvent.NoteOn(0, 0, 60, 100));
        a.Add(MidiEvent.NoteOff(480, 0, 60));
        b.Add(MidiEvent.NoteOn(0, 1, 64, 100));
        b.Add(MidiEvent.NoteOff(960, 1, 64));

        Assert.Equal(1.0, midi.Duration, 3);
    }

    private static Byte[] BuildRunningStatusFile()
    {
        var data = new List<Byte>
        {
            (Byte)'M', (Byte)'T', (Byte)'h', (Byte)'d',
            0, 0, 0, 6,
            0, 0,
            0, 1,
            0x01, 0xE0,
            (Byte)'M', (Byte)'T', (Byte)'r', (Byte)'k',
        };

        var track = new List<Byte>
        {
            0x00, 0x90, 60, 100,
            0x00, 62, 100,
            0x60, 0x80, 60, 64,
            0x00, 62, 64,
            0x00, 0xFF, 0x2F, 0x00,
        };

        var length = track.Count;
        data.Add((Byte)((length >> 24) & 0xFF));
        data.Add((Byte)((length >> 16) & 0xFF));
        data.Add((Byte)((length >> 8) & 0xFF));
        data.Add((Byte)(length & 0xFF));
        data.AddRange(track);
        return data.ToArray();
    }
}
