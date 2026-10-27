using System;
using NewLife.Audio;
using NewLife.Audio.Midi;
using Xunit;

namespace XUnitTest.Midi;

public class MidiSynthesizerTests
{
    [Fact(DisplayName = "MIDI 合成 A4 音符频率接近 440Hz")]
    public void Render_A4_FrequencyIs440()
    {
        var midi = CreateNote(69, 1.0, 73);
        var pcm = MidiSynthesizer.Render(midi, new MidiRenderOptions { SampleRate = 44100, Channels = 1, ReleaseTail = 0.2 });
        var samples = ToSamples(pcm);

        var frequency = EstimateFrequency(samples, 44100, 0.3, 0.9);
        Assert.InRange(frequency, 430, 450);
    }

    [Fact(DisplayName = "MIDI 合成时长包含释放尾巴")]
    public void Render_DurationIncludesReleaseTail()
    {
        var midi = CreateNote(69, 1.0, 73);
        var pcm = MidiSynthesizer.Render(midi, new MidiRenderOptions { SampleRate = 44100, Channels = 1, ReleaseTail = 0.5 });

        Assert.InRange(pcm.Duration, 1.45, 1.55);
    }

    [Fact(DisplayName = "MIDI 音符关闭后能量衰减")]
    public void Render_NoteOff_Decays()
    {
        var midi = CreateNote(69, 0.5, 73);
        var pcm = MidiSynthesizer.Render(midi, new MidiRenderOptions { SampleRate = 44100, Channels = 1, ReleaseTail = 0.5 });
        var samples = ToSamples(pcm);

        var playing = Rms(samples, 44100, 0.2, 0.4);
        var released = Rms(samples, 44100, 0.9, 1.0);

        Assert.True(playing > 0.01, $"播放段能量过低：{playing}");
        Assert.True(released < playing * 0.2, $"释放段未衰减：{released} vs {playing}");
    }

    [Fact(DisplayName = "MIDI 声像左右声道能量不同")]
    public void Render_Pan_ChangesChannelEnergy()
    {
        var leftMidi = CreateNote(69, 1.0, 73, pan: 0);
        var rightMidi = CreateNote(69, 1.0, 73, pan: 127);

        var leftPcm = MidiSynthesizer.Render(leftMidi, new MidiRenderOptions { SampleRate = 22050, Channels = 2, ReleaseTail = 0.1 });
        var rightPcm = MidiSynthesizer.Render(rightMidi, new MidiRenderOptions { SampleRate = 22050, Channels = 2, ReleaseTail = 0.1 });

        var leftEnergy = ChannelRms(leftPcm, 0);
        var leftRightEnergy = ChannelRms(leftPcm, 1);
        var rightLeftEnergy = ChannelRms(rightPcm, 0);
        var rightEnergy = ChannelRms(rightPcm, 1);

        Assert.True(leftEnergy > leftRightEnergy * 2, $"左声像：左 {leftEnergy} vs 右 {leftRightEnergy}");
        Assert.True(rightEnergy > rightLeftEnergy * 2, $"右声像：右 {rightEnergy} vs 左 {rightLeftEnergy}");
    }

    [Fact(DisplayName = "MIDI 多音符复音合成不削波")]
    public void Render_Polyphony_DoesNotClip()
    {
        var midi = MidiFile.Create(MidiFileFormat.SingleTrack, 480);
        var track = new MidiTrack();
        midi.Tracks.Add(track);
        track.Add(MidiEvent.Tempo(0, 500000));
        for (var i = 0; i < 12; i++)
        {
            track.Add(MidiEvent.NoteOn(0, 0, 48 + i, 120));
            track.Add(MidiEvent.NoteOff(960, 0, 48 + i));
        }

        var pcm = MidiSynthesizer.Render(midi, new MidiRenderOptions { SampleRate = 22050, Channels = 2, MaxPolyphony = 64 });
        var samples = ToSamples(pcm);

        foreach (var sample in samples) Assert.InRange(sample, Int16.MinValue, Int16.MaxValue);
        Assert.True(Rms(samples, 22050, 0.1, 0.4) > 0.01);
    }

    [Fact(DisplayName = "MIDI 空文件渲染为空 PCM")]
    public void Render_EmptyFile_ReturnsEmpty()
    {
        var midi = MidiFile.Create(MidiFileFormat.SingleTrack, 480);
        midi.Tracks.Add(new MidiTrack());

        var pcm = MidiSynthesizer.Render(midi);

        Assert.Empty(pcm.Data);
        Assert.Equal(44100, pcm.Format.SampleRate);
    }

    [Fact(DisplayName = "MIDI 鼓通道输出打击乐")]
    public void Render_DrumChannel_ProducesSound()
    {
        var midi = MidiFile.Create(MidiFileFormat.SingleTrack, 480);
        var track = new MidiTrack();
        midi.Tracks.Add(track);
        track.Add(MidiEvent.Tempo(0, 500000));
        track.Add(MidiEvent.NoteOn(0, 9, 36, 127));
        track.Add(MidiEvent.NoteOff(120, 9, 36));

        var pcm = MidiSynthesizer.Render(midi, new MidiRenderOptions { SampleRate = 22050, Channels = 1, ReleaseTail = 0.1 });
        var samples = ToSamples(pcm);

        Assert.True(Rms(samples, 22050, 0, 0.1) > 0.01);
    }

    private static MidiFile CreateNote(Int32 note, Double seconds, Int32 program, Int32 pan = 64)
    {
        var midi = MidiFile.Create(MidiFileFormat.SingleTrack, 480);
        var track = new MidiTrack();
        midi.Tracks.Add(track);
        track.Add(MidiEvent.Tempo(0, 500000));
        track.Add(MidiEvent.ProgramChange(0, 0, program));
        track.Add(MidiEvent.ControlChange(0, 0, 7, 127));
        track.Add(MidiEvent.ControlChange(0, 0, 10, pan));
        track.Add(MidiEvent.ControlChange(0, 0, 11, 127));
        track.Add(MidiEvent.NoteOn(0, 0, note, 127));
        track.Add(MidiEvent.NoteOff((Int64)Math.Round(seconds * 960), 0, note));
        return midi;
    }

    private static Int16[] ToSamples(AudioPcm pcm)
    {
        var data = pcm.Data;
        var samples = new Int16[data.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = (Int16)((data[i * 2 + 1] << 8) | data[i * 2]);
        return samples;
    }

    private static Double EstimateFrequency(Int16[] samples, Int32 sampleRate, Double startSeconds, Double endSeconds)
    {
        var start = (Int32)(startSeconds * sampleRate);
        var end = Math.Min(samples.Length, (Int32)(endSeconds * sampleRate));
        if (end <= start + 1) return 0;

        var crossings = 0;
        var last = samples[start];
        for (var i = start + 1; i < end; i++)
        {
            if (last <= 0 && samples[i] > 0) crossings++;
            last = samples[i];
        }

        return crossings / (endSeconds - startSeconds);
    }

    private static Double Rms(Int16[] samples, Int32 sampleRate, Double startSeconds, Double endSeconds)
    {
        var start = Math.Max(0, (Int32)(startSeconds * sampleRate));
        var end = Math.Min(samples.Length, (Int32)(endSeconds * sampleRate));
        if (end <= start) return 0;

        var sum = 0.0;
        for (var i = start; i < end; i++)
        {
            var value = samples[i] / 32768.0;
            sum += value * value;
        }

        return Math.Sqrt(sum / (end - start));
    }

    private static Double ChannelRms(AudioPcm pcm, Int32 channel)
    {
        var data = pcm.Data;
        var channels = pcm.Format.Channels;
        var sum = 0.0;
        var count = 0;
        for (var i = 0; i + channels * 2 <= data.Length; i += channels * 2)
        {
            var offset = i + channel * 2;
            var value = (Int16)((data[offset + 1] << 8) | data[offset]) / 32768.0;
            sum += value * value;
            count++;
        }

        return count > 0 ? Math.Sqrt(sum / count) : 0;
    }
}
