using System;

namespace NewLife.Audio.Midi;

/// <summary>合成波形</summary>
public enum MidiWaveform
{
    /// <summary>正弦波</summary>
    Sine,

    /// <summary>三角波</summary>
    Triangle,

    /// <summary>锯齿波</summary>
    Saw,

    /// <summary>方波</summary>
    Square,

    /// <summary>噪声</summary>
    Noise,
}

/// <summary>MIDI 乐器参数</summary>
public sealed class MidiInstrument
{
    /// <summary>波形</summary>
    public MidiWaveform Waveform { get; set; }

    /// <summary>起音秒数</summary>
    public Double Attack { get; set; } = 0.01;

    /// <summary>衰减秒数</summary>
    public Double Decay { get; set; } = 0.1;

    /// <summary>延音电平（0~1）</summary>
    public Double Sustain { get; set; } = 0.6;

    /// <summary>释放秒数</summary>
    public Double Release { get; set; } = 0.25;

    /// <summary>音色增益</summary>
    public Double Gain { get; set; } = 0.8;
}

/// <summary>General MIDI 音色映射</summary>
/// <remarks>把 128 个 GM 音色归并为 16 组，用不同波形与 ADSR 近似；不依赖 SoundFont 采样。</remarks>
public static class GeneralMidi
{
    private static readonly String[] _names =
    [
        "Acoustic Grand Piano", "Bright Acoustic Piano", "Electric Grand Piano", "Honky-tonk Piano",
        "Electric Piano 1", "Electric Piano 2", "Harpsichord", "Clavinet",
        "Celesta", "Glockenspiel", "Music Box", "Vibraphone",
        "Marimba", "Xylophone", "Tubular Bells", "Dulcimer",
        "Drawbar Organ", "Percussive Organ", "Rock Organ", "Church Organ",
        "Reed Organ", "Accordion", "Harmonica", "Tango Accordion",
        "Acoustic Guitar (nylon)", "Acoustic Guitar (steel)", "Electric Guitar (jazz)", "Electric Guitar (clean)",
        "Electric Guitar (muted)", "Overdriven Guitar", "Distortion Guitar", "Guitar Harmonics",
        "Acoustic Bass", "Electric Bass (finger)", "Electric Bass (pick)", "Fretless Bass",
        "Slap Bass 1", "Slap Bass 2", "Synth Bass 1", "Synth Bass 2",
        "Violin", "Viola", "Cello", "Contrabass",
        "Tremolo Strings", "Pizzicato Strings", "Orchestral Harp", "Timpani",
        "String Ensemble 1", "String Ensemble 2", "Synth Strings 1", "Synth Strings 2",
        "Choir Aahs", "Voice Oohs", "Synth Voice", "Orchestra Hit",
        "Trumpet", "Trombone", "Tuba", "Muted Trumpet",
        "French Horn", "Brass Section", "Synth Brass 1", "Synth Brass 2",
        "Soprano Sax", "Alto Sax", "Tenor Sax", "Baritone Sax",
        "Oboe", "English Horn", "Bassoon", "Clarinet",
        "Piccolo", "Flute", "Recorder", "Pan Flute",
        "Blown Bottle", "Shakuhachi", "Whistle", "Ocarina",
        "Lead 1 (square)", "Lead 2 (sawtooth)", "Lead 3 (calliope)", "Lead 4 (chiff)",
        "Lead 5 (charang)", "Lead 6 (voice)", "Lead 7 (fifths)", "Lead 8 (bass + lead)",
        "Pad 1 (new age)", "Pad 2 (warm)", "Pad 3 (polysynth)", "Pad 4 (choir)",
        "Pad 5 (bowed)", "Pad 6 (metallic)", "Pad 7 (halo)", "Pad 8 (sweep)",
        "FX 1 (rain)", "FX 2 (soundtrack)", "FX 3 (crystal)", "FX 4 (atmosphere)",
        "FX 5 (brightness)", "FX 6 (goblins)", "FX 7 (echoes)", "FX 8 (sci-fi)",
        "Sitar", "Banjo", "Shamisen", "Koto",
        "Kalimba", "Bagpipe", "Fiddle", "Shanai",
        "Tinkle Bell", "Agogo", "Steel Drums", "Woodblock",
        "Taiko Drum", "Melodic Tom", "Synth Drum", "Reverse Cymbal",
        "Guitar Fret Noise", "Breath Noise", "Seashore", "Bird Tweet",
        "Telephone Ring", "Helicopter", "Applause", "Gunshot",
    ];

    /// <summary>获取音色名称</summary>
    /// <param name="program">音色号（0~127）</param>
    /// <returns>GM 音色名称</returns>
    public static String GetName(Int32 program)
    {
        if (program < 0) program = 0;
        if (program > 127) program = 127;
        return _names[program];
    }

    /// <summary>获取音色参数</summary>
    /// <param name="program">音色号（0~127）</param>
    /// <returns>乐器参数</returns>
    public static MidiInstrument GetInstrument(Int32 program)
    {
        if (program < 0) program = 0;
        if (program > 127) program = 127;

        var group = program / 8;
        return group switch
        {
            0 => Create(MidiWaveform.Triangle, 0.005, 0.18, 0.35, 0.25, 0.9),      // Piano
            1 => Create(MidiWaveform.Sine, 0.002, 0.30, 0.20, 0.30, 0.8),          // Chromatic percussion
            2 => Create(MidiWaveform.Square, 0.010, 0.05, 0.90, 0.10, 0.55),       // Organ
            3 => Create(MidiWaveform.Saw, 0.005, 0.25, 0.25, 0.20, 0.8),           // Guitar
            4 => Create(MidiWaveform.Sine, 0.005, 0.20, 0.50, 0.15, 0.9),          // Bass
            5 => Create(MidiWaveform.Saw, 0.080, 0.10, 0.80, 0.35, 0.5),           // Strings
            6 => Create(MidiWaveform.Saw, 0.100, 0.10, 0.85, 0.40, 0.45),          // Ensemble
            7 => Create(MidiWaveform.Square, 0.030, 0.10, 0.75, 0.20, 0.5),        // Brass
            8 => Create(MidiWaveform.Square, 0.020, 0.10, 0.80, 0.20, 0.5),        // Reed
            9 => Create(MidiWaveform.Sine, 0.030, 0.10, 0.85, 0.20, 0.6),          // Pipe
            10 => Create(MidiWaveform.Saw, 0.005, 0.10, 0.70, 0.20, 0.55),         // Synth lead
            11 => Create(MidiWaveform.Saw, 0.200, 0.10, 0.80, 0.60, 0.4),          // Synth pad
            12 => Create(MidiWaveform.Noise, 0.010, 0.20, 0.40, 0.50, 0.4),        // FX
            13 => Create(MidiWaveform.Triangle, 0.005, 0.20, 0.30, 0.20, 0.8),     // Ethnic
            14 => Create(MidiWaveform.Triangle, 0.001, 0.15, 0.00, 0.10, 0.9),     // Percussive
            _ => Create(MidiWaveform.Noise, 0.001, 0.20, 0.00, 0.20, 0.4),         // Sound effects
        };
    }

    private static MidiInstrument Create(MidiWaveform waveform, Double attack, Double decay, Double sustain, Double release, Double gain) => new()
    {
        Waveform = waveform,
        Attack = attack,
        Decay = decay,
        Sustain = sustain,
        Release = release,
        Gain = gain,
    };
}
