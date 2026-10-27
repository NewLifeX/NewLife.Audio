using System;
using NewLife.Audio.Midi;
using Xunit;

namespace XUnitTest.Midi;

public class MidiSequenceTests
{
    [Fact(DisplayName = "Format 2 按轨道拆分为独立序列")]
    public void Format2_GetSequence_ReturnsSingleTrack()
    {
        var midi = CreateFormat2();

        Assert.Equal(2, midi.SequenceCount);
        Assert.Single(midi.GetSequence(0).Tracks);
        Assert.Single(midi.GetSequence(1).Tracks);
        Assert.Equal(3, midi.GetSequence(0).Tracks[0].Events.Count);
        Assert.Equal(3, midi.GetSequence(1).Tracks[0].Events.Count);
    }

    [Fact(DisplayName = "Format 2 序列独立渲染时长不同")]
    public void Format2_RenderSequence_UsesOwnTempo()
    {
        var midi = CreateFormat2();

        var first = MidiSynthesizer.RenderSequence(midi, 0, new MidiRenderOptions { SampleRate = 22050, Channels = 1, ReleaseTail = 0.1 });
        var second = MidiSynthesizer.RenderSequence(midi, 1, new MidiRenderOptions { SampleRate = 22050, Channels = 1, ReleaseTail = 0.1 });

        Assert.InRange(first.Duration, 0.55, 0.65);
        Assert.InRange(second.Duration, 1.05, 1.15);
        Assert.NotEqual(first.Data.Length, second.Data.Length);
    }

    [Fact(DisplayName = "Format 2 序列索引越界抛异常")]
    public void Format2_SequenceIndexOutOfRange_Throws()
    {
        var midi = CreateFormat2();

        Assert.Throws<ArgumentOutOfRangeException>(() => midi.GetSequence(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => MidiSynthesizer.RenderSequence(midi, -1));
    }

    [Fact(DisplayName = "Format 1 只有一个序列且渲染整首")]
    public void Format1_SequenceCountIsOne()
    {
        var midi = MidiFile.Create(MidiFileFormat.MultiTrack, 480);
        var track = new MidiTrack();
        midi.Tracks.Add(track);
        track.Add(MidiEvent.Tempo(0, 500000));
        track.Add(MidiEvent.NoteOn(0, 0, 60, 100));
        track.Add(MidiEvent.NoteOff(480, 0, 60));

        Assert.Equal(1, midi.SequenceCount);
        Assert.Same(midi, midi.GetSequence(0));
        Assert.True(MidiSynthesizer.RenderSequence(midi, 0).Data.Length > 0);
    }

    private static MidiFile CreateFormat2()
    {
        var midi = MidiFile.Create(MidiFileFormat.MultiSong, 480);

        var first = new MidiTrack { Name = "First" };
        first.Add(MidiEvent.Tempo(0, 500000));
        first.Add(MidiEvent.NoteOn(0, 0, 60, 100));
        first.Add(MidiEvent.NoteOff(480, 0, 60));
        midi.Tracks.Add(first);

        var second = new MidiTrack { Name = "Second" };
        second.Add(MidiEvent.Tempo(0, 250000));
        second.Add(MidiEvent.NoteOn(0, 0, 64, 100));
        second.Add(MidiEvent.NoteOff(1920, 0, 64));
        midi.Tracks.Add(second);

        return midi;
    }
}
