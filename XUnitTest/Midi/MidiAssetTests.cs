using System;
using System.IO;
using NewLife.Audio.Midi;
using Xunit;

namespace XUnitTest.Midi;

public class MidiAssetTests
{
    private static readonly String[] _files =
    [
        "ode-to-joy.mid",
        "greensleeves.mid",
        "canon-in-d.mid",
        "jasmine-flower.mid",
    ];

    [Theory(DisplayName = "内置 MIDI 示例可解析且时长有效")]
    [InlineData("ode-to-joy.mid")]
    [InlineData("greensleeves.mid")]
    [InlineData("canon-in-d.mid")]
    [InlineData("jasmine-flower.mid")]
    public void BuiltInMidi_Parses(String name)
    {
        var path = GetPath(name);
        var midi = MidiFile.Read(path);
        var notes = midi.GetNotes();

        Assert.True(midi.Tracks.Count > 0);
        Assert.True(notes.Count > 0);
        Assert.True(midi.Duration > 1);
        Assert.True(new FileInfo(path).Length < 20 * 1024);
    }

    [Theory(DisplayName = "内置 MIDI 示例可渲染出声音")]
    [InlineData("ode-to-joy.mid")]
    [InlineData("greensleeves.mid")]
    [InlineData("canon-in-d.mid")]
    [InlineData("jasmine-flower.mid")]
    public void BuiltInMidi_Renders(String name)
    {
        var midi = MidiFile.Read(GetPath(name));
        var pcm = MidiSynthesizer.Render(midi, new MidiRenderOptions { SampleRate = 22050, Channels = 2, ReleaseTail = 0.2 });

        Assert.True(pcm.Data.Length > 0);
        Assert.True(pcm.Duration > 1);

        var peak = 0;
        for (var i = 0; i + 1 < pcm.Data.Length; i += 2)
        {
            var sample = Math.Abs((Int32)(Int16)((pcm.Data[i + 1] << 8) | pcm.Data[i]));
            if (sample > peak) peak = sample;
        }

        Assert.True(peak > 1000, $"渲染结果能量过低：{peak}");
    }

    [Theory(DisplayName = "内置 MIDI 示例读写往返后音符数一致")]
    [InlineData("ode-to-joy.mid")]
    [InlineData("greensleeves.mid")]
    [InlineData("canon-in-d.mid")]
    [InlineData("jasmine-flower.mid")]
    public void BuiltInMidi_RoundTrips(String name)
    {
        var midi = MidiFile.Read(GetPath(name));
        var notes = midi.GetNotes();
        var path = Path.Combine(Path.GetTempPath(), $"newlife-midi-{Guid.NewGuid():N}.mid");

        try
        {
            midi.Save(path);
            var parsed = MidiFile.Read(path);

            Assert.Equal(notes.Count, parsed.GetNotes().Count);
            Assert.Equal(midi.Duration, parsed.Duration, 3);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact(DisplayName = "内置 MIDI 资产总大小小于 200KB")]
    public void BuiltInMidi_TotalSizeIsSmall()
    {
        var total = 0L;
        foreach (var name in _files) total += new FileInfo(GetPath(name)).Length;

        Assert.True(total < 200 * 1024, $"MIDI 资产总大小 {total} 字节");
    }

    private static String GetPath(String name) => Path.Combine(AppContext.BaseDirectory, "Assets", "Midi", name);
}
