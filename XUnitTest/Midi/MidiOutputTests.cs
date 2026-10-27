using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using NewLife.Audio.Midi;
using Xunit;

namespace XUnitTest.Midi;

public class MidiOutputTests
{
    [Fact(DisplayName = "MIDI 输出序列器按时间顺序发送事件")]
    public void Sequencer_SendsEventsInOrder()
    {
        var midi = MidiFile.Create(MidiFileFormat.SingleTrack, 480);
        var track = new MidiTrack();
        midi.Tracks.Add(track);
        track.Add(MidiEvent.Tempo(0, 500000));
        track.Add(MidiEvent.NoteOn(0, 0, 60, 100));
        track.Add(MidiEvent.ControlChange(240, 0, 7, 90));
        track.Add(MidiEvent.NoteOff(480, 0, 60));

        var output = new RecordingOutput();
        var player = new MidiOutputSequencer(midi, output) { Speed = 20 };
        var watch = Stopwatch.StartNew();
        player.Play();
        watch.Stop();

        Assert.Equal(3, output.Messages.Count);
        Assert.Equal(MidiEventType.NoteOn, output.Messages[0].Type);
        Assert.Equal(MidiEventType.ControlChange, output.Messages[1].Type);
        Assert.Equal(MidiEventType.NoteOff, output.Messages[2].Type);
        Assert.Equal(1, output.ResetCount);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"播放耗时 {watch.ElapsedMilliseconds} ms");
    }

    [Fact(DisplayName = "MIDI 输出序列器可选择 Format 2 序列")]
    public void Sequencer_SelectsFormat2Sequence()
    {
        var midi = MidiFile.Create(MidiFileFormat.MultiSong, 480);
        var first = new MidiTrack();
        first.Add(MidiEvent.Tempo(0, 500000));
        first.Add(MidiEvent.NoteOn(0, 0, 60, 100));
        first.Add(MidiEvent.NoteOff(240, 0, 60));
        midi.Tracks.Add(first);

        var second = new MidiTrack();
        second.Add(MidiEvent.Tempo(0, 500000));
        second.Add(MidiEvent.NoteOn(0, 0, 64, 100));
        second.Add(MidiEvent.NoteOff(240, 0, 64));
        midi.Tracks.Add(second);

        var output = new RecordingOutput();
        var player = new MidiOutputSequencer(midi, output, 1) { Speed = 20, SendAllNotesOffOnStop = false };
        player.Play();

        Assert.Contains(output.Messages, e => e.Type == MidiEventType.NoteOn && e.NoteNumber == 64);
        Assert.DoesNotContain(output.Messages, e => e.Type == MidiEventType.NoteOn && e.NoteNumber == 60);
    }

    [Fact(DisplayName = "MIDI 输出序列器可停止并标记取消")]
    public async Task Sequencer_Stop_SetsCancelled()
    {
        var midi = MidiFile.Create(MidiFileFormat.SingleTrack, 480);
        var track = new MidiTrack();
        midi.Tracks.Add(track);
        track.Add(MidiEvent.Tempo(0, 500000));
        track.Add(MidiEvent.NoteOn(0, 0, 60, 100));
        track.Add(MidiEvent.NoteOff(480, 0, 60));

        var output = new RecordingOutput();
        var player = new MidiOutputSequencer(midi, output) { Speed = 0.01 };
        var task = player.PlayAsync();
        await Task.Delay(50);
        player.Stop();
        await task;

        Assert.True(player.IsCancelled);
        Assert.True(output.ResetCount > 0);
    }

    [Fact(DisplayName = "MIDI 输出设备枚举返回非空集合")]
    public void MidiOutputDevice_Enumerate_ReturnsList()
    {
        Assert.NotNull(MidiOutputDevice.Enumerate());
        Assert.NotNull(MidiInputDevice.Enumerate());
    }

    private sealed class RecordingOutput : IMidiOutput
    {
        public MidiDeviceInfo Device { get; } = new() { Index = 0, Name = "Recording" };

        public List<MidiEvent> Messages { get; } = [];

        public Int32 ResetCount { get; private set; }

        public void SendShortMessage(Byte status, Byte data1, Byte data2 = 0)
        {
            Messages.Add(new MidiEvent
            {
                Type = GetType(status),
                Status = status,
                Channel = status & 0x0F,
                Data1 = data1,
                Data2 = data2,
            });
        }

        public void SendSysEx(Byte[] data) => Messages.Add(new MidiEvent { Type = MidiEventType.SysEx, Data = data });

        public void Send(MidiEvent e) => Messages.Add(e);

        public void Reset() => ResetCount++;

        public void Dispose()
        {
        }

        private static MidiEventType GetType(Byte status) => (status & 0xF0) switch
        {
            0x80 => MidiEventType.NoteOff,
            0x90 => MidiEventType.NoteOn,
            0xA0 => MidiEventType.PolyphonicKeyPressure,
            0xB0 => MidiEventType.ControlChange,
            0xC0 => MidiEventType.ProgramChange,
            0xD0 => MidiEventType.ChannelPressure,
            0xE0 => MidiEventType.PitchBend,
            _ => MidiEventType.System,
        };
    }
}
