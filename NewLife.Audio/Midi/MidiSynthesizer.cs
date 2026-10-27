using System;
using System.Collections.Generic;
using System.Linq;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Midi;

/// <summary>纯托管 MIDI 波形合成器</summary>
/// <remarks>
/// 不依赖 SoundFont/DLS 采样，按 General MIDI 音色分组选择波形与 ADSR 包络，
/// 支持力度、音量、表情、声像、弯音、延音踏板与复音限制，输出 16 位 PCM。
/// 音色为电子 MIDI 风格，追求小体积与零素材，不追求真实乐器采样品质。
/// </remarks>
/// <example>
/// <code>
/// var midi = MidiFile.Read("song.mid");
/// var pcm = MidiSynthesizer.Render(midi, new MidiRenderOptions { SampleRate = 44100 });
/// AudioFile.WritePcm("song.wav", pcm);
/// </code>
/// </example>
public static class MidiSynthesizer
{
    /// <summary>渲染为 PCM</summary>
    /// <param name="file">MIDI 文件</param>
    /// <param name="options">渲染选项，null 使用默认值</param>
    /// <returns>16 位 PCM 音频</returns>
    /// <exception cref="ArgumentNullException">文件为空</exception>
    /// <exception cref="ArgumentOutOfRangeException">采样率或声道数无效</exception>
    public static AudioPcm Render(MidiFile file, MidiRenderOptions options = null)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));

        options ??= new MidiRenderOptions();
        if (options.SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(options.SampleRate));
        if (options.Channels != 1 && options.Channels != 2) throw new ArgumentOutOfRangeException(nameof(options.Channels), "Only mono and stereo are supported.");

        var sampleRate = options.SampleRate;
        var channels = options.Channels;
        var tempo = file.GetTempoMap();
        var notes = file.GetNotes();
        var automation = BuildAutomation(file, tempo);

        var endSeconds = file.Duration;
        var voices = new List<Voice>(notes.Count);
        foreach (var note in notes)
        {
            if (note.Number < 0 || note.Number > 127) continue;

            var state = automation[note.Channel & 0x0F];
            var noteEnd = note.EndSeconds;

            // 延音踏板：音符结束时踏板仍按下，则延长到下一次踏板释放
            if (FindValue(state.Sustain, note.EndTick, 0) >= 64)
            {
                var offTick = FindNextSustainOff(state.Sustain, note.EndTick);
                if (offTick == Int64.MaxValue) noteEnd = file.Duration;
                else noteEnd = tempo.TickToSeconds(offTick);
            }

            if (noteEnd <= note.StartSeconds) noteEnd = note.StartSeconds + 0.01;

            var program = FindValue(state.Program, note.StartTick, 0);
            var volume = FindValue(state.Volume, note.StartTick, 100);
            var expression = FindValue(state.Expression, note.StartTick, 127);
            var pan = FindValue(state.Pan, note.StartTick, 64);
            var bend = FindValue(state.PitchBend, note.StartTick, 0);

            voices.Add(new Voice
            {
                Note = note,
                Program = program,
                Volume = volume,
                Expression = expression,
                Pan = pan,
                Bend = bend,
                EndSeconds = noteEnd,
            });

            if (noteEnd > endSeconds) endSeconds = noteEnd;
        }

        if (voices.Count == 0) return CreateEmpty(sampleRate, channels);

        endSeconds += Math.Max(0, options.ReleaseTail);
        if (endSeconds <= 0) return CreateEmpty(sampleRate, channels);

        var totalSamples = (Int64)Math.Ceiling(endSeconds * sampleRate);
        if (totalSamples > Int32.MaxValue / 2 / channels)
            throw new InvalidOperationException("The rendered MIDI audio is too long.");

        var sampleCount = (Int32)totalSamples;
        var left = new Single[sampleCount];
        var right = channels == 2 ? new Single[sampleCount] : null;

        var maxPolyphony = Math.Max(1, options.MaxPolyphony);
        var active = new List<Voice>(maxPolyphony);

        foreach (var voice in voices)
        {
            var start = voice.Note.StartSeconds;
            for (var i = active.Count - 1; i >= 0; i--)
            {
                if (active[i].EndSeconds <= start) active.RemoveAt(i);
            }

            // 复音已满时丢弃新音符，避免无界增长；常规歌曲很少触发
            if (active.Count >= maxPolyphony) continue;

            active.Add(voice);

            if ((voice.Note.Channel & 0x0F) == 9) RenderDrum(voice, left, right, sampleRate, options.MasterVolume);
            else RenderMelodic(voice, left, right, sampleRate, options.MasterVolume);
        }

        var data = ToPcm16(left, right, sampleRate, channels);
        return new AudioPcm
        {
            Format = new AudioFormat
            {
                SampleRate = sampleRate,
                Channels = channels,
                BitsPerSample = 16,
                Encoding = AVTypes.PCM_AUDIO,
                SamplesPerFrame = 1,
            },
            Codec = AVTypes.PCM_AUDIO,
            Data = data,
        };
    }

    private static AudioPcm CreateEmpty(Int32 sampleRate, Int32 channels) => new()
    {
        Format = new AudioFormat
        {
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = 16,
            Encoding = AVTypes.PCM_AUDIO,
            SamplesPerFrame = 1,
        },
        Codec = AVTypes.PCM_AUDIO,
        Data = [],
    };

    private static void RenderMelodic(Voice voice, Single[] left, Single[] right, Int32 sampleRate, Double masterVolume)
    {
        var note = voice.Note;
        var instrument = GeneralMidi.GetInstrument(voice.Program);
        var table = WaveTable.Get(instrument.Waveform, note.Number);
        var bendRatio = Math.Pow(2.0, (voice.Bend / 8192.0) * 2.0 / 12.0);
        var frequency = note.Frequency * bendRatio;
        var duration = voice.EndSeconds - note.StartSeconds;
        var total = duration + Math.Max(0.01, instrument.Release);

        var startSample = (Int32)Math.Round(note.StartSeconds * sampleRate);
        var sampleCount = (Int32)Math.Ceiling(total * sampleRate);
        var endSample = startSample + sampleCount;
        if (startSample >= left.Length || startSample < 0) return;
        if (endSample > left.Length) endSample = left.Length;

        var velocity = Clamp01(note.Velocity / 127.0);
        var amplitude = Math.Pow(velocity, 1.5)
            * Clamp01(voice.Volume / 127.0)
            * Clamp01(voice.Expression / 127.0)
            * instrument.Gain
            * masterVolume;

        if (amplitude <= 0) return;

        var (panLeft, panRight) = GetPan(voice.Pan);
        var phase = 0.0;
        var increment = frequency / sampleRate * table.Length;
        var tableLength = table.Length;

        for (var sample = startSample; sample < endSample; sample++)
        {
            var t = (sample - startSample) / (Double)sampleRate;
            var envelope = GetEnvelope(t, duration, instrument);
            if (envelope <= 0 && t > duration) break;

            var index = (Int32)phase;
            var fraction = phase - index;
            index %= tableLength;
            var next = (index + 1) % tableLength;
            var value = table[index] + (table[next] - table[index]) * fraction;

            var output = (Single)(value * envelope * amplitude);
            left[sample] += output * (Single)panLeft;
            if (right != null) right[sample] += output * (Single)panRight;

            phase += increment;
            if (phase >= tableLength) phase -= tableLength * Math.Floor(phase / tableLength);
        }
    }

    private static void RenderDrum(Voice voice, Single[] left, Single[] right, Int32 sampleRate, Double masterVolume)
    {
        var note = voice.Note;
        var velocity = Math.Pow(Clamp01(note.Velocity / 127.0), 1.3);
        var amplitude = velocity * Clamp01(voice.Volume / 127.0) * Clamp01(voice.Expression / 127.0) * masterVolume;
        if (amplitude <= 0) return;

        var duration = note.Number switch
        {
            35 or 36 => 0.25,
            38 or 40 => 0.25,
            42 or 44 => 0.08,
            46 => 0.35,
            49 or 57 => 0.9,
            51 or 59 => 0.6,
            >= 41 and <= 50 => 0.35,
            _ => 0.2,
        };

        var startSample = (Int32)Math.Round(note.StartSeconds * sampleRate);
        var sampleCount = (Int32)Math.Ceiling(duration * sampleRate);
        var endSample = startSample + sampleCount;
        if (startSample >= left.Length || startSample < 0) return;
        if (endSample > left.Length) endSample = left.Length;

        var (panLeft, panRight) = GetPan(voice.Pan);
        var phase = 0.0;
        var seed = (UInt32)(startSample + note.Number * 7919) | 1u;
        var lastNoise = 0.0;

        for (var sample = startSample; sample < endSample; sample++)
        {
            var t = (sample - startSample) / (Double)sampleRate;
            var noise = NextNoise(ref seed);
            Double value;

            switch (note.Number)
            {
                case 35:
                case 36:
                    {
                        var frequency = 45.0 + 130.0 * Math.Exp(-t * 28.0);
                        phase += 2.0 * Math.PI * frequency / sampleRate;
                        value = Math.Sin(phase) * Math.Exp(-t * 16.0) + noise * 0.05 * Math.Exp(-t * 80.0);
                        break;
                    }
                case 38:
                case 40:
                    value = noise * 0.7 * Math.Exp(-t * 22.0) + Math.Sin(2.0 * Math.PI * 180.0 * t) * 0.25 * Math.Exp(-t * 30.0);
                    break;
                case 42:
                case 44:
                case 46:
                    {
                        var decay = note.Number == 46 ? 10.0 : 55.0;
                        var high = noise - lastNoise;
                        lastNoise = noise;
                        value = high * 0.4 * Math.Exp(-t * decay);
                        break;
                    }
                case 49:
                case 57:
                    value = noise * 0.35 * Math.Exp(-t * 4.0);
                    break;
                case 51:
                case 59:
                    value = noise * 0.3 * Math.Exp(-t * 5.0) + Math.Sin(2.0 * Math.PI * 320.0 * t) * 0.1 * Math.Exp(-t * 8.0);
                    break;
                case 41:
                case 43:
                case 45:
                case 47:
                case 48:
                case 50:
                    {
                        var baseFrequency = 110.0 * Math.Pow(2.0, (note.Number - 41) / 12.0);
                        var frequency = baseFrequency * (1.0 + 0.5 * Math.Exp(-t * 20.0));
                        phase += 2.0 * Math.PI * frequency / sampleRate;
                        value = Math.Sin(phase) * Math.Exp(-t * 12.0);
                        break;
                    }
                default:
                    value = noise * 0.4 * Math.Exp(-t * 20.0);
                    break;
            }

            var output = (Single)(value * amplitude);
            left[sample] += output * (Single)panLeft;
            if (right != null) right[sample] += output * (Single)panRight;
        }
    }

    private static Double GetEnvelope(Double t, Double duration, MidiInstrument instrument)
    {
        var attack = Math.Max(0.001, instrument.Attack);
        var decay = Math.Max(0.001, instrument.Decay);
        var release = Math.Max(0.001, instrument.Release);

        var level = EnvelopeLevel(t, attack, decay, instrument.Sustain);
        if (t <= duration) return level;

        var releaseLevel = EnvelopeLevel(duration, attack, decay, instrument.Sustain);
        var rt = t - duration;
        return rt >= release ? 0 : releaseLevel * (1.0 - rt / release);
    }

    private static Double EnvelopeLevel(Double t, Double attack, Double decay, Double sustain)
    {
        if (t <= 0) return 0;
        if (t < attack) return t / attack;
        if (t < attack + decay) return 1.0 - (1.0 - sustain) * (t - attack) / decay;
        return sustain;
    }

    private static (Double Left, Double Right) GetPan(Int32 pan)
    {
        var value = Math.Max(0, Math.Min(127, pan)) / 127.0;
        var angle = value * Math.PI / 2.0;
        return (Math.Cos(angle), Math.Sin(angle));
    }

    private static Double Clamp01(Double value)
    {
        if (value < 0) return 0;
        if (value > 1) return 1;
        return value;
    }

    private static Double NextNoise(ref UInt32 seed)
    {
        seed ^= seed << 13;
        seed ^= seed >> 17;
        seed ^= seed << 5;
        return (seed & 0xFFFF) / 32768.0 - 1.0;
    }

    private static Byte[] ToPcm16(Single[] left, Single[] right, Int32 sampleRate, Int32 channels)
    {
        var totalSamples = left.Length;
        var data = new Byte[totalSamples * channels * 2];
        var fade = Math.Min(totalSamples / 2, sampleRate / 200);
        var scale = 0.95;

        for (var i = 0; i < totalSamples; i++)
        {
            var l = Math.Tanh(left[i] * 1.2) * scale;
            var r = right != null ? Math.Tanh(right[i] * 1.2) * scale : l;

            if (fade > 0)
            {
                if (i < fade)
                {
                    var gain = (Double)i / fade;
                    l *= gain;
                    r *= gain;
                }
                else if (i >= totalSamples - fade)
                {
                    var gain = (Double)(totalSamples - i) / fade;
                    l *= gain;
                    r *= gain;
                }
            }

            Int16 value;
            if (channels == 1)
            {
                value = ToInt16((l + r) * 0.5);
                var offset = i * 2;
                data[offset] = (Byte)(value & 0xFF);
                data[offset + 1] = (Byte)((value >> 8) & 0xFF);
            }
            else
            {
                value = ToInt16(l);
                var offset = i * 4;
                data[offset] = (Byte)(value & 0xFF);
                data[offset + 1] = (Byte)((value >> 8) & 0xFF);

                value = ToInt16(r);
                data[offset + 2] = (Byte)(value & 0xFF);
                data[offset + 3] = (Byte)((value >> 8) & 0xFF);
            }
        }

        return data;
    }

    private static Int16 ToInt16(Double value)
    {
        var sample = (Int32)Math.Round(value * 32767.0);
        if (sample > Int16.MaxValue) sample = Int16.MaxValue;
        if (sample < Int16.MinValue) sample = Int16.MinValue;
        return (Int16)sample;
    }

    private static ChannelState[] BuildAutomation(MidiFile file, MidiTempoMap tempo)
    {
        var states = new ChannelState[16];
        for (var i = 0; i < states.Length; i++) states[i] = new ChannelState();

        var events = new List<MidiEvent>();
        foreach (var track in file.Tracks) events.AddRange(track.Events);
        events = events.OrderBy(e => e.Tick).ToList();

        foreach (var item in events)
        {
            if (item.Channel < 0 || item.Channel > 15) continue;

            var state = states[item.Channel];
            switch (item.Type)
            {
                case MidiEventType.ProgramChange:
                    state.Program.Add(new AutomationPoint(item.Tick, item.Program));
                    break;
                case MidiEventType.PitchBend:
                    state.PitchBend.Add(new AutomationPoint(item.Tick, item.PitchBendValue));
                    break;
                case MidiEventType.ControlChange:
                    switch (item.Controller)
                    {
                        case 7:
                            state.Volume.Add(new AutomationPoint(item.Tick, item.ControllerValue));
                            break;
                        case 10:
                            state.Pan.Add(new AutomationPoint(item.Tick, item.ControllerValue));
                            break;
                        case 11:
                            state.Expression.Add(new AutomationPoint(item.Tick, item.ControllerValue));
                            break;
                        case 64:
                            state.Sustain.Add(new AutomationPoint(item.Tick, item.ControllerValue));
                            break;
                    }
                    break;
            }
        }

        return states;
    }

    private static Int32 FindValue(List<AutomationPoint> points, Int64 tick, Int32 defaultValue)
    {
        var index = FindIndex(points, tick);
        return index >= 0 ? points[index].Value : defaultValue;
    }

    private static Int64 FindNextSustainOff(List<AutomationPoint> points, Int64 tick)
    {
        var index = FindIndex(points, tick);
        for (var i = index + 1; i < points.Count; i++)
        {
            if (points[i].Value < 64) return points[i].Tick;
        }

        return Int64.MaxValue;
    }

    private static Int32 FindIndex(List<AutomationPoint> points, Int64 tick)
    {
        var low = 0;
        var high = points.Count - 1;
        var result = -1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (points[mid].Tick <= tick)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }

    private sealed class ChannelState
    {
        public List<AutomationPoint> Program { get; } = [];

        public List<AutomationPoint> Volume { get; } = [];

        public List<AutomationPoint> Pan { get; } = [];

        public List<AutomationPoint> Expression { get; } = [];

        public List<AutomationPoint> Sustain { get; } = [];

        public List<AutomationPoint> PitchBend { get; } = [];
    }

    private readonly struct AutomationPoint
    {
        public Int64 Tick { get; }

        public Int32 Value { get; }

        public AutomationPoint(Int64 tick, Int32 value)
        {
            Tick = tick;
            Value = value;
        }
    }

    private sealed class Voice
    {
        public MidiNote Note { get; set; }

        public Int32 Program { get; set; }

        public Int32 Volume { get; set; }

        public Int32 Expression { get; set; }

        public Int32 Pan { get; set; }

        public Int32 Bend { get; set; }

        public Double EndSeconds { get; set; }
    }
}
