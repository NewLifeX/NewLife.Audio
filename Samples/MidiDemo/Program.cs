using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NewLife.Audio;
using NewLife.Audio.Devices;
using NewLife.Audio.Midi;

namespace MidiDemo;

internal class Program
{
    private static void Main(String[] args)
    {
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

        try
        {
            switch (mode)
            {
                case "info":
                    ShowInfo(Require(args, 1, "file.mid"));
                    break;
                case "render":
                    Render(Require(args, 1, "file.mid"), Require(args, 2, "out.wav"), args.Length > 3 ? Int32.Parse(args[3]) : -1, args.Length > 4 ? args[4] : null);
                    break;
                case "play":
                    Play(Require(args, 1, "file.mid"), args.Length > 2 ? Int32.Parse(args[2]) : -1, args.Length > 3 ? args[3] : null);
                    break;
                case "gen":
                    Generate(args.Length > 1 ? args[1] : "AudioStudio/Assets/Midi");
                    break;
                default:
                    PrintHelp();
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("MIDI demo");
        Console.WriteLine("  MidiDemo info <file.mid>");
        Console.WriteLine("  MidiDemo render <file.mid> <out.wav|out.mp3|out.flac> [sequence] [bank.sf2|bank.dls]");
        Console.WriteLine("  MidiDemo play <file.mid> [sequence] [bank.sf2|bank.dls]");
        Console.WriteLine("  MidiDemo gen <dir>");
    }

    private static String Require(String[] args, Int32 index, String name)
    {
        if (args.Length <= index || String.IsNullOrWhiteSpace(args[index])) throw new ArgumentException($"Missing argument: {name}");
        return args[index];
    }

    private static void ShowInfo(String path)
    {
        var midi = MidiFile.Read(path);
        var tempo = midi.GetTempoMap();
        var notes = midi.GetNotes();
        var programs = new SortedSet<Int32>();

        foreach (var note in notes)
        {
            var program = 0;
            foreach (var item in midi.Tracks[note.Track].Events)
            {
                if (item.Tick > note.StartTick) break;
                if (item.Type == MidiEventType.ProgramChange && item.Channel == note.Channel) program = item.Program;
            }
            programs.Add(program);
        }

        Console.WriteLine($"File      : {Path.GetFullPath(path)}");
        Console.WriteLine($"Size      : {new FileInfo(path).Length:n0} bytes");
        Console.WriteLine($"Format    : {midi.Format}");
        Console.WriteLine($"Sequences : {midi.SequenceCount}");
        Console.WriteLine($"Division  : {(midi.IsSmpte ? $"{midi.SmpteTicksPerSecond:n0} ticks/s (SMPTE)" : $"{midi.TicksPerQuarterNote} PPQ")}");
        Console.WriteLine($"Tempo     : {tempo.BpmAt(0):n1} BPM");
        Console.WriteLine($"Duration  : {midi.Duration:n2} s");
        Console.WriteLine($"Tracks    : {midi.Tracks.Count}");
        Console.WriteLine($"Notes     : {notes.Count}");
        Console.WriteLine($"Polyphony : {midi.GetMaxPolyphony()}");

        for (var i = 0; i < midi.Tracks.Count; i++)
        {
            var track = midi.Tracks[i];
            Console.WriteLine($"  [{i}] {track.Name ?? "(unnamed)"}  events={track.Events.Count}  end={track.EndTick}");
        }

        if (programs.Count > 0)
        {
            Console.Write("Programs  : ");
            var first = true;
            foreach (var program in programs)
            {
                if (!first) Console.Write(", ");
                Console.Write($"{program} {GeneralMidi.GetName(program)}");
                first = false;
            }
            Console.WriteLine();
        }
    }

    private static void Render(String path, String output, Int32 sequence, String bankPath)
    {
        var midi = MidiFile.Read(path);
        var bank = LoadBank(bankPath);
        var options = new MidiRenderOptions { SoundBank = bank };
        var pcm = sequence >= 0 ? MidiSynthesizer.RenderSequence(midi, sequence, options) : MidiSynthesizer.Render(midi, options);
        AudioFile.WritePcm(output, pcm);
        var source = bank == null ? "built-in waveform" : $"{Path.GetFileName(bankPath)} ({bank.Presets.Count} presets)";
        Console.WriteLine($"Rendered {pcm.Duration:n2}s -> {output} ({new FileInfo(output).Length:n0} bytes), source: {source}");
    }

    private static void Play(String path, Int32 sequence, String bankPath)
    {
        var midi = MidiFile.Read(path);
        var bank = LoadBank(bankPath);
        var options = new MidiRenderOptions { SoundBank = bank };
        var pcm = sequence >= 0 ? MidiSynthesizer.RenderSequence(midi, sequence, options) : MidiSynthesizer.Render(midi, options);
        if (pcm.Data.Length == 0)
        {
            Console.WriteLine("No notes to play.");
            return;
        }

        using var player = AudioDriverFactory.Create().CreatePlayer();
        player.Init(pcm.Format);
        player.Play();

        var format = pcm.Format;
        var chunkBytes = Math.Max(format.SampleRate / 10, 1) * format.Channels * format.BytesPerSample;
        var bufferedLimit = Math.Max(format.SampleRate / 2, 1) * format.Channels * format.BytesPerSample;

        for (var offset = 0; offset < pcm.Data.Length; offset += chunkBytes)
        {
            var count = Math.Min(chunkBytes, pcm.Data.Length - offset);
            var buffer = new Byte[count];
            Array.Copy(pcm.Data, offset, buffer, 0, count);
            player.Write(buffer);

            while (player.BufferedBytes > bufferedLimit) Thread.Sleep(10);
        }

        while (player.BufferedBytes > 0) Thread.Sleep(50);
        player.Stop();
        Console.WriteLine($"Played {midi.Duration:n2}s.");
    }

    private static SoundBank LoadBank(String path)
    {
        if (String.IsNullOrWhiteSpace(path)) return null;

        var extension = Path.GetExtension(path);
        return String.Equals(extension, ".dls", StringComparison.OrdinalIgnoreCase)
            ? DlsSoundBank.Read(path)
            : (SoundBank)SoundFont.Read(path);
    }

    private static void Generate(String directory)
    {
        Directory.CreateDirectory(directory);

        GenerateOdeToJoy(Path.Combine(directory, "ode-to-joy.mid"));
        GenerateGreensleeves(Path.Combine(directory, "greensleeves.mid"));
        GenerateCanon(Path.Combine(directory, "canon-in-d.mid"));
        GenerateJasmineFlower(Path.Combine(directory, "jasmine-flower.mid"));

        Console.WriteLine($"Generated public-domain MIDI files in {Path.GetFullPath(directory)}");
        foreach (var file in Directory.GetFiles(directory, "*.mid")) Console.WriteLine($"  {Path.GetFileName(file)}  {new FileInfo(file).Length:n0} bytes");
    }

    private static void GenerateOdeToJoy(String path)
    {
        var melody = new TrackSpec("Melody", 0, 0, 64);
        Add(melody, 0, 1, 64, 64, 65, 67, 67, 65, 64, 62, 60, 60, 62, 64);
        Add(melody, 12, 1.5, 64);
        Add(melody, 13.5, 0.5, 62);
        Add(melody, 14, 2, 62);

        Add(melody, 16, 1, 64, 64, 65, 67, 67, 65, 64, 62, 60, 60, 62, 64);
        Add(melody, 28, 1.5, 62);
        Add(melody, 29.5, 0.5, 60);
        Add(melody, 30, 2, 60);

        Add(melody, 32, 1, 64, 64, 65, 67, 67, 65, 64, 62, 60, 60, 62, 64);
        Add(melody, 44, 1.5, 62);
        Add(melody, 45.5, 0.5, 60);
        Add(melody, 46, 2, 60);

        var bass = new TrackSpec("Bass", 32, 1, 64);
        AddChord(bass, 0, 4, 36);
        AddChord(bass, 4, 4, 43);
        AddChord(bass, 8, 4, 36);
        AddChord(bass, 12, 4, 43);
        AddChord(bass, 16, 4, 36);
        AddChord(bass, 20, 4, 43);
        AddChord(bass, 24, 4, 36);
        AddChord(bass, 28, 4, 36);
        AddChord(bass, 32, 4, 36);
        AddChord(bass, 36, 4, 43);
        AddChord(bass, 40, 4, 36);
        AddChord(bass, 44, 4, 36);

        var chords = new TrackSpec("Chords", 48, 2, 64);
        AddChord(chords, 0, 4, 48, 52, 55);
        AddChord(chords, 4, 4, 43, 47, 50);
        AddChord(chords, 8, 4, 48, 52, 55);
        AddChord(chords, 12, 4, 43, 47, 50);
        AddChord(chords, 16, 4, 48, 52, 55);
        AddChord(chords, 20, 4, 43, 47, 50);
        AddChord(chords, 24, 4, 48, 52, 55);
        AddChord(chords, 28, 4, 48, 52, 55);
        AddChord(chords, 32, 4, 48, 52, 55);
        AddChord(chords, 36, 4, 43, 47, 50);
        AddChord(chords, 40, 4, 48, 52, 55);
        AddChord(chords, 44, 4, 48, 52, 55);

        Build(120, 4, 4, melody, bass, chords).Save(path);
    }

    private static void GenerateGreensleeves(String path)
    {
        var melody = new TrackSpec("Melody", 73, 0, 64);
        Add(melody, 0, 2, 69); Add(melody, 2, 1, 72);
        Add(melody, 3, 2, 74); Add(melody, 5, 1, 76);
        Add(melody, 6, 2, 77); Add(melody, 8, 1, 76);
        Add(melody, 9, 2, 74); Add(melody, 11, 1, 71);
        Add(melody, 12, 2, 67); Add(melody, 14, 1, 69);
        Add(melody, 15, 2, 71); Add(melody, 17, 1, 72);
        Add(melody, 18, 2, 69); Add(melody, 20, 1, 68);
        Add(melody, 21, 3, 69);
        Add(melody, 24, 2, 72); Add(melody, 26, 1, 74);
        Add(melody, 27, 2, 76); Add(melody, 29, 1, 77);
        Add(melody, 30, 2, 76); Add(melody, 32, 1, 74);
        Add(melody, 33, 2, 71); Add(melody, 35, 1, 67);
        Add(melody, 36, 2, 69); Add(melody, 38, 1, 71);
        Add(melody, 39, 2, 72); Add(melody, 41, 1, 69);
        Add(melody, 42, 2, 69); Add(melody, 44, 1, 68);
        Add(melody, 45, 3, 69);

        var bass = new TrackSpec("Bass", 32, 1, 64);
        var roots = new[] { 45, 40, 45, 40, 43, 40, 45, 40, 45, 40, 43, 40, 45, 43, 40, 45 };
        for (var i = 0; i < roots.Length; i++) AddChord(bass, i * 3, 3, roots[i]);

        Build(100, 3, 4, melody, bass).Save(path);
    }

    private static void GenerateCanon(String path)
    {
        var chords = new[]
        {
            new[] { 62, 66, 69, 74 },
            new[] { 61, 64, 69, 73 },
            new[] { 59, 62, 66, 71 },
            new[] { 57, 61, 66, 69 },
            new[] { 55, 59, 62, 67 },
            new[] { 54, 57, 62, 66 },
            new[] { 55, 59, 62, 67 },
            new[] { 57, 61, 64, 69 },
        };
        var roots = new[] { 38, 45, 47, 42, 43, 38, 43, 45 };

        var arpeggio = new TrackSpec("Arpeggio", 48, 0, 64);
        var bass = new TrackSpec("Bass", 32, 1, 64);
        var pad = new TrackSpec("Pad", 48, 2, 64);

        for (var repeat = 0; repeat < 4; repeat++)
        {
            for (var i = 0; i < chords.Length; i++)
            {
                var start = (repeat * chords.Length + i) * 2.0;
                for (var n = 0; n < 4; n++) Add(arpeggio, start + n * 0.5, 0.5, chords[i][n]);
                AddChord(bass, start, 2, roots[i]);
                AddChord(pad, start, 2, chords[i]);
            }
        }

        Build(100, 4, 4, arpeggio, bass, pad).Save(path);
    }

    private static void GenerateJasmineFlower(String path)
    {
        var melody = new TrackSpec("Melody", 73, 0, 64);
        var phrase = new[] { 64, 64, 67, 69, 72, 72, 69, 67, 67, 69, 67, 64, 62, 64, 62, 60 };
        for (var i = 0; i < 2; i++) Add(melody, i * 16, 1, phrase);
        Add(melody, 32, 1, 62, 64, 67, 64, 62, 60, 57, 57);
        Add(melody, 40, 1, 64, 67, 69, 72, 67, 67, 64, 62);
        Add(melody, 48, 1, 62, 60, 57, 57);
        Add(melody, 52, 4, 60);

        var bass = new TrackSpec("Bass", 32, 1, 64);
        var roots = new[] { 48, 45, 41, 43, 48, 45, 41, 43, 41, 45, 48, 43, 41, 48 };
        for (var i = 0; i < roots.Length; i++) AddChord(bass, i * 4, 4, roots[i] - 12);

        var chords = new TrackSpec("Chords", 48, 2, 64);
        var triads = new[]
        {
            new[] { 48, 52, 55 }, new[] { 45, 48, 52 }, new[] { 41, 45, 48 }, new[] { 43, 47, 50 },
            new[] { 48, 52, 55 }, new[] { 45, 48, 52 }, new[] { 41, 45, 48 }, new[] { 43, 47, 50 },
            new[] { 41, 45, 48 }, new[] { 45, 48, 52 }, new[] { 48, 52, 55 }, new[] { 43, 47, 50 },
            new[] { 41, 45, 48 }, new[] { 48, 52, 55 },
        };
        for (var i = 0; i < triads.Length; i++) AddChord(chords, i * 4, 4, triads[i]);

        Build(90, 4, 4, melody, bass, chords).Save(path);
    }

    private static MidiFile Build(Double bpm, Int32 numerator, Int32 denominator, params TrackSpec[] tracks)
    {
        var midi = MidiFile.Create(MidiFileFormat.MultiTrack, 480);
        var conductor = new MidiTrack { Name = "Conductor" };
        conductor.Add(MidiEvent.TrackName(0, "Conductor"));
        conductor.Add(MidiEvent.Tempo(0, (Int32)Math.Round(60000000.0 / bpm)));
        conductor.Add(MidiEvent.TimeSignature(0, numerator, denominator));
        midi.Tracks.Add(conductor);

        const Int32 ticksPerBeat = 480;
        foreach (var spec in tracks)
        {
            var track = new MidiTrack { Name = spec.Name };
            track.Add(MidiEvent.TrackName(0, spec.Name));
            track.Add(MidiEvent.ProgramChange(0, spec.Channel, spec.Program));
            track.Add(MidiEvent.ControlChange(0, spec.Channel, 7, 110));
            track.Add(MidiEvent.ControlChange(0, spec.Channel, 10, spec.Pan));

            foreach (var note in spec.Notes)
            {
                var start = (Int64)Math.Round(note.Start * ticksPerBeat);
                var end = (Int64)Math.Round((note.Start + note.Duration) * ticksPerBeat);
                track.Add(MidiEvent.NoteOn(start, spec.Channel, note.Note, note.Velocity));
                track.Add(MidiEvent.NoteOff(end, spec.Channel, note.Note));
            }

            midi.Tracks.Add(track);
        }

        return midi;
    }

    private static void Add(TrackSpec track, Double start, Double duration, params Int32[] notes)
    {
        for (var i = 0; i < notes.Length; i++)
        {
            track.Notes.Add(new NoteSpec { Start = start + i * duration, Duration = duration, Note = notes[i] });
        }
    }

    private static void AddChord(TrackSpec track, Double start, Double duration, params Int32[] notes)
    {
        foreach (var note in notes) track.Notes.Add(new NoteSpec { Start = start, Duration = duration, Note = note });
    }

    private sealed class TrackSpec
    {
        public String Name { get; }

        public Int32 Program { get; }

        public Int32 Channel { get; }

        public Int32 Pan { get; }

        public List<NoteSpec> Notes { get; } = [];

        public TrackSpec(String name, Int32 program, Int32 channel, Int32 pan)
        {
            Name = name;
            Program = program;
            Channel = channel;
            Pan = pan;
        }
    }

    private sealed class NoteSpec
    {
        public Double Start { get; set; }

        public Double Duration { get; set; }

        public Int32 Note { get; set; }

        public Int32 Velocity { get; set; } = 100;
    }
}
