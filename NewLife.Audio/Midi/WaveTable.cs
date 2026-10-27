using System;
using System.Collections.Generic;

namespace NewLife.Audio.Midi;

/// <summary>带限波表</summary>
/// <remarks>按八度缓存波形表，谐波限制在 Nyquist 以内，避免高频混叠。</remarks>
internal static class WaveTable
{
    private const Int32 Size = 2048;
    private static readonly Dictionary<Int32, Double[]> _cache = [];
    private static readonly Object _lock = new();

    /// <summary>获取指定波形与音高的波表</summary>
    /// <param name="waveform">波形</param>
    /// <param name="note">音符号</param>
    /// <returns>归一化波表</returns>
    public static Double[] Get(MidiWaveform waveform, Int32 note)
    {
        if (note < 0) note = 0;
        if (note > 127) note = 127;

        var octave = note / 12;
        var key = ((Int32)waveform << 8) | octave;

        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var table)) return table;

            table = Build(waveform, octave);
            _cache[key] = table;
            return table;
        }
    }

    private static Double[] Build(MidiWaveform waveform, Int32 octave)
    {
        var table = new Double[Size];
        var highestNote = Math.Min(127, (octave + 1) * 12 - 1);
        var frequency = 440.0 * Math.Pow(2.0, (highestNote - 69) / 12.0);
        var maxHarmonics = (Int32)(20000.0 / Math.Max(20.0, frequency));
        if (maxHarmonics < 1) maxHarmonics = 1;
        if (maxHarmonics > 32) maxHarmonics = 32;

        if (waveform == MidiWaveform.Noise)
        {
            var seed = (UInt32)(octave * 2654435761u + 12345);
            for (var i = 0; i < Size; i++)
            {
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                table[i] = (seed & 0xFFFF) / 32768.0 - 1.0;
            }
        }
        else
        {
            for (var i = 0; i < Size; i++)
            {
                var phase = 2.0 * Math.PI * i / Size;
                table[i] = waveform switch
                {
                    MidiWaveform.Sine => Math.Sin(phase),
                    MidiWaveform.Triangle => Triangle(phase, maxHarmonics),
                    MidiWaveform.Saw => Saw(phase, maxHarmonics),
                    MidiWaveform.Square => Square(phase, maxHarmonics),
                    _ => 0,
                };
            }
        }

        var peak = 0.0;
        foreach (var value in table)
        {
            var abs = Math.Abs(value);
            if (abs > peak) peak = abs;
        }

        if (peak > 0)
        {
            for (var i = 0; i < Size; i++) table[i] /= peak;
        }

        return table;
    }

    private static Double Triangle(Double phase, Int32 harmonics)
    {
        var value = 0.0;
        for (var n = 1; n <= harmonics; n += 2)
        {
            var sign = ((n - 1) / 2) % 2 == 0 ? 1.0 : -1.0;
            value += sign * Math.Sin(n * phase) / (n * n);
        }

        return value * 8.0 / (Math.PI * Math.PI);
    }

    private static Double Saw(Double phase, Int32 harmonics)
    {
        var value = 0.0;
        for (var n = 1; n <= harmonics; n++) value += Math.Sin(n * phase) / n;

        return value * 2.0 / Math.PI;
    }

    private static Double Square(Double phase, Int32 harmonics)
    {
        var value = 0.0;
        for (var n = 1; n <= harmonics; n += 2) value += Math.Sin(n * phase) / n;

        return value * 4.0 / Math.PI;
    }
}
