using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NewLife.Audio;
using NewLife.Audio.Midi;
using Xunit;

namespace XUnitTest.Midi;

public class SoundFontTests
{
    [Fact(DisplayName = "SoundFont 2 解析预设与采样")]
    public void SoundFont_Read_ParsesPresetAndSample()
    {
        var data = CreateSoundFont();
        var bank = SoundFont.Read(data);

        var preset = Assert.Single(bank.Presets);
        Assert.Equal("Test Piano", preset.Name);
        Assert.Equal(0, preset.Bank);
        Assert.Equal(0, preset.Program);

        var region = Assert.Single(preset.Regions);
        Assert.Equal(0, region.KeyLow);
        Assert.Equal(127, region.KeyHigh);
        Assert.Equal(69, region.RootKey);
        Assert.True(region.Loop);
        Assert.Equal(100, region.LoopStart);
        Assert.Equal(900, region.LoopEnd);
        Assert.InRange(region.Sample.Data.Length, 900, 1200);
        Assert.Equal(44100, region.Sample.SampleRate);
    }

    [Fact(DisplayName = "SoundFont 2 采样参与 MIDI 渲染")]
    public void SoundFont_Render_ProducesSound()
    {
        var bank = SoundFont.Read(CreateSoundFont());
        var midi = CreateNote(69, 0.5, 0);
        var pcm = MidiSynthesizer.Render(midi, new MidiRenderOptions
        {
            SampleRate = 22050,
            Channels = 1,
            ReleaseTail = 0.1,
            SoundBank = bank,
        });

        var samples = ToSamples(pcm);
        Assert.True(Rms(samples, 22050, 0.05, 0.35) > 0.01);
    }

    [Fact(DisplayName = "DLS 解析预设与波形")]
    public void Dls_Read_ParsesPresetAndWave()
    {
        var bank = DlsSoundBank.Read(CreateDls());

        var preset = Assert.Single(bank.Presets);
        Assert.Equal(0, preset.Bank);
        Assert.Equal(0, preset.Program);

        var region = Assert.Single(preset.Regions);
        Assert.Equal(0, region.KeyLow);
        Assert.Equal(127, region.KeyHigh);
        Assert.Equal(69, region.RootKey);
        Assert.True(region.Loop);
        Assert.Equal(100, region.LoopStart);
        Assert.Equal(900, region.LoopEnd);
        Assert.InRange(region.Sample.Data.Length, 900, 1200);
    }

    [Fact(DisplayName = "DLS 采样参与 MIDI 渲染")]
    public void Dls_Render_ProducesSound()
    {
        var bank = DlsSoundBank.Read(CreateDls());
        var midi = CreateNote(69, 0.5, 0);
        var pcm = MidiSynthesizer.Render(midi, new MidiRenderOptions
        {
            SampleRate = 22050,
            Channels = 1,
            ReleaseTail = 0.1,
            SoundBank = bank,
        });

        var samples = ToSamples(pcm);
        Assert.True(Rms(samples, 22050, 0.05, 0.35) > 0.01);
    }

    [Fact(DisplayName = "非 SoundFont 数据被拒绝")]
    public void SoundFont_Read_InvalidDataThrows()
    {
        Assert.Throws<InvalidDataException>(() => SoundFont.Read(Encoding.ASCII.GetBytes("not a soundfont")));
    }

    private static MidiFile CreateNote(Int32 note, Double seconds, Int32 program)
    {
        var midi = MidiFile.Create(MidiFileFormat.SingleTrack, 480);
        var track = new MidiTrack();
        midi.Tracks.Add(track);
        track.Add(MidiEvent.Tempo(0, 500000));
        track.Add(MidiEvent.ProgramChange(0, 0, program));
        track.Add(MidiEvent.ControlChange(0, 0, 7, 127));
        track.Add(MidiEvent.ControlChange(0, 0, 11, 127));
        track.Add(MidiEvent.NoteOn(0, 0, note, 127));
        track.Add(MidiEvent.NoteOff((Int64)Math.Round(seconds * 960), 0, note));
        return midi;
    }

    private static Byte[] CreateSoundFont()
    {
        var pcm = CreatePcm();

        var info = List("INFO",
            Chunk("ifil", new Byte[] { 2, 0, 1, 0 }),
            Chunk("INAM", Encoding.ASCII.GetBytes("Test Bank")));
        var sdta = List("sdta", Chunk("smpl", pcm));
        var pdta = List("pdta",
            Chunk("phdr", Concat(PresetRecord("Test Piano", 0, 0, 0), new Byte[38])),
            Chunk("pbag", Concat(BagRecord(0), BagRecord(1))),
            Chunk("pgen", Concat(GenRecord(41, 0), GenRecord(0, 0))),
            Chunk("inst", Concat(InstrumentRecord("Test Instrument", 0), new Byte[22])),
            Chunk("ibag", Concat(BagRecord(0), BagRecord(3))),
            Chunk("igen", Concat(GenRecord(53, 0), GenRecord(54, 1), GenRecord(58, 69), GenRecord(0, 0))),
            Chunk("shdr", Concat(SampleRecord("Test Wave", 0, pcm.Length / 2, 100, 900, 44100, 69), new Byte[46])));

        return Riff("sfbk", info, sdta, pdta);
    }

    private static Byte[] CreateDls()
    {
        var pcm = CreatePcm();
        var waveSample = CreateWaveSample();

        var instrument = List("ins ",
            Chunk("insh", Concat(U32(0), U32(0), U32(0), U32(0))),
            List("rgn ",
                Chunk("rgnh", Concat(U16(0), U16(127), U16(0), U16(127), U16(0), U16(0))),
                Chunk("wlnk", Concat(U16(0), U16(0), U32(0), U32(0))),
                Chunk("wsmp", waveSample)));
        var lins = List("lins", instrument);

        var wave = List("wave",
            Chunk("fmt ", Concat(U16(1), U16(1), U32(44100), U32(88200), U16(2), U16(16))),
            Chunk("data", pcm),
            Chunk("wsmp", waveSample));
        var wvpl = List("wvpl", wave);

        return Riff("DLS ",
            Chunk("colh", U32(1)),
            lins,
            wvpl);
    }

    private static Byte[] CreateWaveSample() => Concat(
        U32(20), U16(69), I16(0), I32(0), U32(0), U32(1),
        U32(16), U32(0), U32(100), U32(800));

    private static Byte[] CreatePcm()
    {
        var result = new Byte[1200 * 2];
        for (var i = 0; i < 1200; i++)
        {
            var value = (Int16)(Math.Sin(2.0 * Math.PI * 440.0 * i / 44100.0) * 16000);
            result[i * 2] = (Byte)(value & 0xFF);
            result[i * 2 + 1] = (Byte)((value >> 8) & 0xFF);
        }

        return result;
    }

    private static Byte[] PresetRecord(String name, Int32 program, Int32 bank, Int32 bagIndex) => Concat(
        Name(name), U16(program), U16(bank), U16(bagIndex), U32(0), U32(0), U32(0));

    private static Byte[] InstrumentRecord(String name, Int32 bagIndex) => Concat(Name(name), U16(bagIndex));

    private static Byte[] SampleRecord(String name, Int32 start, Int32 end, Int32 loopStart, Int32 loopEnd, Int32 sampleRate, Int32 rootKey) => Concat(
        Name(name), U32(start), U32(end), U32(loopStart), U32(loopEnd), U32(sampleRate), new Byte[] { (Byte)rootKey, 0 }, U16(0), U16(1));

    private static Byte[] BagRecord(Int32 genIndex) => Concat(U16(genIndex), U16(0));

    private static Byte[] GenRecord(Int32 oper, Int32 amount) => Concat(U16(oper), I16(amount));

    private static Byte[] Name(String name)
    {
        var bytes = new Byte[20];
        var value = Encoding.ASCII.GetBytes(name);
        Array.Copy(value, bytes, Math.Min(value.Length, bytes.Length));
        return bytes;
    }

    private static Byte[] Riff(String type, params Byte[][] chunks)
    {
        var body = Concat(Encoding.ASCII.GetBytes(type), Concat(chunks));
        return Concat(Encoding.ASCII.GetBytes("RIFF"), U32(body.Length), body);
    }

    private static Byte[] List(String type, params Byte[][] chunks)
    {
        var body = Concat(Encoding.ASCII.GetBytes(type), Concat(chunks));
        return Chunk("LIST", body);
    }

    private static Byte[] Chunk(String id, Byte[] data)
    {
        var pad = (data.Length & 1) == 0 ? Array.Empty<Byte>() : new Byte[1];
        return Concat(Encoding.ASCII.GetBytes(id), U32(data.Length), data, pad);
    }

    private static Byte[] Concat(params Byte[][] items)
    {
        var list = new List<Byte>();
        foreach (var item in items)
        {
            if (item != null) list.AddRange(item);
        }

        return list.ToArray();
    }

    private static Byte[] U16(Int32 value) => new[] { (Byte)(value & 0xFF), (Byte)((value >> 8) & 0xFF) };

    private static Byte[] I16(Int32 value) => U16(value);

    private static Byte[] U32(Int32 value) => new[]
    {
        (Byte)(value & 0xFF),
        (Byte)((value >> 8) & 0xFF),
        (Byte)((value >> 16) & 0xFF),
        (Byte)((value >> 24) & 0xFF),
    };

    private static Byte[] I32(Int32 value) => U32(value);

    private static Int16[] ToSamples(AudioPcm pcm)
    {
        var data = pcm.Data;
        var samples = new Int16[data.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = (Int16)((data[i * 2 + 1] << 8) | data[i * 2]);
        return samples;
    }

    private static Double Rms(Int16[] samples, Int32 sampleRate, Double startSeconds, Double endSeconds)
    {
        var start = Math.Max(0, (Int32)(startSeconds * sampleRate));
        var end = Math.Min(samples.Length, (Int32)(endSeconds * sampleRate));
        var sum = 0.0;
        var count = 0;
        for (var i = start; i < end; i++)
        {
            var value = samples[i] / 32768.0;
            sum += value * value;
            count++;
        }

        return count == 0 ? 0 : Math.Sqrt(sum / count);
    }
}
