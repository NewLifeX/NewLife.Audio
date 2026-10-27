using System;
using System.IO;
using NewLife.Audio.Midi;
using Xunit;

namespace XUnitTest.Midi;

public class MidiWriterTests
{
    [Fact(DisplayName = "MIDI 写入自动补结束事件且不重复")]
    public void Write_AddsSingleEndOfTrack()
    {
        var midi = MidiFile.Create(MidiFileFormat.SingleTrack, 480);
        var track = new MidiTrack();
        midi.Tracks.Add(track);
        track.Add(MidiEvent.EndOfTrack(0));
        track.Add(MidiEvent.NoteOn(0, 0, 60, 100));
        track.Add(MidiEvent.NoteOff(480, 0, 60));

        var parsed = MidiFile.Parse(midi.ToArray());
        var events = parsed.Tracks[0].Events;
        var endCount = 0;
        foreach (var item in events)
        {
            if (item.Type == MidiEventType.Meta && item.MetaType == 0x2F) endCount++;
        }

        Assert.Equal(1, endCount);
    }

    [Fact(DisplayName = "MIDI 写入保持同 tick 事件的 Note Off 优先顺序")]
    public void Write_SameTick_NoteOffFirst()
    {
        var midi = MidiFile.Create(MidiFileFormat.SingleTrack, 480);
        var track = new MidiTrack();
        midi.Tracks.Add(track);
        track.Add(MidiEvent.NoteOn(0, 0, 60, 100));
        track.Add(MidiEvent.NoteOff(480, 0, 60));
        track.Add(MidiEvent.NoteOn(480, 0, 62, 100));
        track.Add(MidiEvent.NoteOff(960, 0, 62));

        var parsed = MidiFile.Parse(midi.ToArray());
        var events = parsed.Tracks[0].Events;
        var indexOff = -1;
        var indexOn = -1;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].Tick != 480) continue;
            if (events[i].Type == MidiEventType.NoteOff) indexOff = i;
            if (events[i].Type == MidiEventType.NoteOn) indexOn = i;
        }

        Assert.True(indexOff >= 0 && indexOn >= 0);
        Assert.True(indexOff < indexOn, $"Note Off 应排在 Note On 之前：{indexOff} vs {indexOn}");
    }

    [Fact(DisplayName = "MIDI 文件保存与读取往返一致")]
    public void SaveAndRead_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"newlife-midi-{Guid.NewGuid():N}.mid");
        try
        {
            var midi = MidiFile.Create(MidiFileFormat.MultiTrack, 960);
            var a = new MidiTrack { Name = "A" };
            var b = new MidiTrack { Name = "B" };
            midi.Tracks.Add(a);
            midi.Tracks.Add(b);
            a.Add(MidiEvent.Tempo(0, 500000));
            a.Add(MidiEvent.NoteOn(0, 0, 60, 110));
            a.Add(MidiEvent.NoteOff(960, 0, 60));
            b.Add(MidiEvent.NoteOn(0, 1, 64, 90));
            b.Add(MidiEvent.NoteOff(1920, 1, 64));

            midi.Save(path);
            var parsed = MidiFile.Read(path);

            Assert.Equal(MidiFileFormat.MultiTrack, parsed.Format);
            Assert.Equal(960, parsed.TicksPerQuarterNote);
            Assert.Equal(2, parsed.Tracks.Count);
            Assert.Equal("A", parsed.Tracks[0].Name);
            Assert.Equal("B", parsed.Tracks[1].Name);
            Assert.Equal(1.0, parsed.Duration, 3);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
