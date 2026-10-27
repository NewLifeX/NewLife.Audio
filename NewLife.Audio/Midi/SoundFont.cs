using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NewLife.Audio.Midi;

/// <summary>SoundFont 2 采样音源</summary>
/// <remarks>
/// 解析 RIFF/sfbk 容器中的 INFO、sdta、pdta 三个列表，读取预设（phdr）、乐器（inst）、
/// 区域（pbag/pgen、ibag/igen）与采样头（shdr），把 16 位 PCM 采样转换为单声道浮点数据。
/// 支持 GM bank/program、按键/力度范围、根音、粗调/细调、衰减、声像、音量包络与循环。
/// 不处理 Modulator、滤波器与立体声采样配对，复杂音色按单声道近似。
/// </remarks>
/// <example>
/// <code>
/// var bank = SoundFont.Read("TimGM6mb.sf2");
/// var midi = MidiFile.Read("song.mid");
/// var pcm = MidiSynthesizer.Render(midi, new MidiRenderOptions { SoundBank = bank });
/// </code>
/// </example>
public sealed class SoundFont : SoundBank
{
    private readonly List<SoundBankPreset> _presets = [];

    /// <summary>预设列表</summary>
    public override IList<SoundBankPreset> Presets => _presets;

    /// <summary>从文件读取 SoundFont</summary>
    /// <param name="path">文件路径</param>
    /// <returns>采样音源</returns>
    public static SoundFont Read(String path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));

        return Read(File.ReadAllBytes(path));
    }

    /// <summary>从流读取 SoundFont</summary>
    /// <param name="stream">数据流</param>
    /// <returns>采样音源</returns>
    public static SoundFont Read(Stream stream)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Read(memory.ToArray());
    }

    /// <summary>从字节数组读取 SoundFont</summary>
    /// <param name="data">文件字节</param>
    /// <returns>采样音源</returns>
    /// <exception cref="ArgumentNullException">数据为空</exception>
    /// <exception cref="InvalidDataException">文件不是有效的 SoundFont 2</exception>
    public static SoundFont Read(Byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));

        var root = RiffReader.Parse(data);
        if (!String.Equals(root.Type, "sfbk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The file is not a SoundFont 2 bank.");

        var sdta = root.FindList("sdta");
        var pdta = root.FindList("pdta");
        if (sdta == null || pdta == null) throw new InvalidDataException("The SoundFont is missing sdta or pdta.");

        var waves = ReadWaveData(sdta.Find("smpl"));
        var sampleRecords = ReadSampleRecords(pdta.Find("shdr"), waves);
        var presets = ReadPresetRecords(pdta.Find("phdr"));
        var bags = ReadBagRecords(pdta.Find("pbag"));
        var gens = ReadGenRecords(pdta.Find("pgen"));
        var instruments = ReadInstrumentRecords(pdta.Find("inst"));
        var instBags = ReadBagRecords(pdta.Find("ibag"));
        var instGens = ReadGenRecords(pdta.Find("igen"));

        var bank = new SoundFont();
        bank.BuildPresets(presets, bags, gens, instruments, instBags, instGens, sampleRecords);

        return bank;
    }

    private void BuildPresets(
        List<PresetRecord> presets,
        List<BagRecord> bags,
        List<GenRecord> gens,
        List<InstrumentRecord> instruments,
        List<BagRecord> instBags,
        List<GenRecord> instGens,
        List<SampleRecord> samples)
    {
        if (presets.Count == 0 || bags.Count == 0 || gens.Count == 0) return;

        for (var i = 0; i < presets.Count; i++)
        {
            var preset = presets[i];
            var presetBags = i + 1 < presets.Count ? presets[i + 1].BagIndex : bags.Count - 1;
            if (preset.BagIndex < 0 || preset.BagIndex >= bags.Count || presetBags <= preset.BagIndex) continue;

            var result = new SoundBankPreset
            {
                Name = preset.Name,
                Bank = preset.Bank,
                Program = preset.Program,
            };

            var global = new Dictionary<Int32, Int32>();
            for (var bagIndex = preset.BagIndex; bagIndex < presetBags && bagIndex < bags.Count; bagIndex++)
            {
                var zone = ReadZoneGens(bags, gens, bagIndex);
                if (!zone.TryGetValue(GenInstrument, out var instrumentIndex))
                {
                    Merge(global, zone);
                    continue;
                }

                if (instrumentIndex < 0 || instrumentIndex >= instruments.Count) continue;

                var instrument = instruments[instrumentIndex];
                var instrumentBags = instrumentIndex + 1 < instruments.Count ? instruments[instrumentIndex + 1].BagIndex : instBags.Count - 1;
                if (instrument.BagIndex < 0 || instrument.BagIndex >= instBags.Count || instrumentBags <= instrument.BagIndex) continue;

                var instrumentGlobal = new Dictionary<Int32, Int32>();
                for (var instBagIndex = instrument.BagIndex; instBagIndex < instrumentBags && instBagIndex < instBags.Count; instBagIndex++)
                {
                    var instZone = ReadZoneGens(instBags, instGens, instBagIndex);
                    if (!instZone.TryGetValue(GenSampleId, out var sampleIndex))
                    {
                        Merge(instrumentGlobal, instZone);
                        continue;
                    }

                    if (sampleIndex < 0 || sampleIndex >= samples.Count) continue;

                    var region = CreateRegion(samples[sampleIndex], global, zone, instrumentGlobal, instZone);
                    if (region != null && region.Sample.Data.Length > 0) result.Regions.Add(region);
                }
            }

            if (result.Regions.Count > 0) _presets.Add(result);
        }
    }

    private static SoundBankRegion CreateRegion(SampleRecord sample, Dictionary<Int32, Int32> presetGlobal, Dictionary<Int32, Int32> presetZone, Dictionary<Int32, Int32> instrumentGlobal, Dictionary<Int32, Int32> instrumentZone)
    {
        var data = sample.Sample.Data;
        if (data.Length == 0) return null;

        var keyLow = 0;
        var keyHigh = 127;
        ApplyRange(presetGlobal, presetZone, GenKeyRange, ref keyLow, ref keyHigh);
        ApplyRange(instrumentGlobal, instrumentZone, GenKeyRange, ref keyLow, ref keyHigh);

        var velocityLow = 0;
        var velocityHigh = 127;
        ApplyRange(presetGlobal, presetZone, GenVelocityRange, ref velocityLow, ref velocityHigh);
        ApplyRange(instrumentGlobal, instrumentZone, GenVelocityRange, ref velocityLow, ref velocityHigh);

        if (keyLow > keyHigh || velocityLow > velocityHigh) return null;

        var coarse = GetAdd(presetGlobal, presetZone, GenCoarseTune) + GetAdd(instrumentGlobal, instrumentZone, GenCoarseTune);
        var fine = GetAdd(presetGlobal, presetZone, GenFineTune) + GetAdd(instrumentGlobal, instrumentZone, GenFineTune);
        var attenuation = (GetAdd(presetGlobal, presetZone, GenInitialAttenuation) + GetAdd(instrumentGlobal, instrumentZone, GenInitialAttenuation)) / 10.0;
        var panValue = GetAdd(presetGlobal, presetZone, GenPan) + GetAdd(instrumentGlobal, instrumentZone, GenPan);
        var pan = 64 + (Int32)Math.Round(panValue / 500.0 * 64.0);
        if (pan < 0) pan = 0;
        if (pan > 127) pan = 127;

        var sampleModes = GetValue(instrumentGlobal, instrumentZone, GenSampleModes, 0);
        var sampleStart = Math.Max(0, GetValue(instrumentGlobal, instrumentZone, GenStartAddrsOffset, 0));
        var sampleEndOffset = GetValue(instrumentGlobal, instrumentZone, GenEndAddrsOffset, 0);
        var loopStartOffset = GetValue(instrumentGlobal, instrumentZone, GenStartLoopAddrsOffset, 0);
        var loopEndOffset = GetValue(instrumentGlobal, instrumentZone, GenEndLoopAddrsOffset, 0);

        var sampleEnd = data.Length + sampleEndOffset;
        if (sampleEnd > data.Length) sampleEnd = data.Length;
        if (sampleEnd <= sampleStart) return null;

        var loopStart = sample.Sample.LoopStart + loopStartOffset;
        var loopEnd = sample.Sample.LoopEnd + loopEndOffset;
        if (loopStart < sampleStart) loopStart = sampleStart;
        if (loopEnd > sampleEnd) loopEnd = sampleEnd;

        var loop = (sampleModes == 1 || sampleModes == 3) && loopEnd > loopStart;

        return new SoundBankRegion
        {
            Sample = sample.Sample,
            KeyLow = keyLow,
            KeyHigh = keyHigh,
            VelocityLow = velocityLow,
            VelocityHigh = velocityHigh,
            RootKey = GetValue(instrumentGlobal, instrumentZone, GenOverridingRootKey, -1),
            CoarseTune = coarse,
            FineTune = fine,
            Attenuation = attenuation,
            Pan = pan,
            Attack = ToSeconds(GetValue(instrumentGlobal, instrumentZone, GenAttackVolEnv, -12000), 0.005),
            Hold = ToSeconds(GetValue(instrumentGlobal, instrumentZone, GenHoldVolEnv, -12000), 0),
            Decay = ToSeconds(GetValue(instrumentGlobal, instrumentZone, GenDecayVolEnv, -12000), 0.05),
            Sustain = ToLevel(GetValue(instrumentGlobal, instrumentZone, GenSustainVolEnv, 0)),
            Release = ToSeconds(GetValue(instrumentGlobal, instrumentZone, GenReleaseVolEnv, -12000), 0.2),
            Loop = loop,
            SampleStart = sampleStart,
            SampleEnd = sampleEnd,
            LoopStart = loopStart,
            LoopEnd = loopEnd,
        };
    }

    private static Dictionary<Int32, Int32> ReadZoneGens(List<BagRecord> bags, List<GenRecord> gens, Int32 bagIndex)
    {
        var result = new Dictionary<Int32, Int32>();
        if (bagIndex < 0 || bagIndex >= bags.Count) return result;

        var start = bags[bagIndex].GenIndex;
        var end = bagIndex + 1 < bags.Count ? bags[bagIndex + 1].GenIndex : gens.Count;
        if (start < 0) start = 0;
        if (end > gens.Count) end = gens.Count;

        for (var i = start; i < end; i++)
        {
            var gen = gens[i];
            result[gen.Oper] = gen.Amount;
        }

        return result;
    }

    private static void Merge(Dictionary<Int32, Int32> target, Dictionary<Int32, Int32> source)
    {
        foreach (var item in source) target[item.Key] = item.Value;
    }

    private static void ApplyRange(Dictionary<Int32, Int32> global, Dictionary<Int32, Int32> zone, Int32 oper, ref Int32 low, ref Int32 high)
    {
        ApplyRange(global, oper, ref low, ref high);
        ApplyRange(zone, oper, ref low, ref high);
    }

    private static void ApplyRange(Dictionary<Int32, Int32> source, Int32 oper, ref Int32 low, ref Int32 high)
    {
        if (!source.TryGetValue(oper, out var value)) return;

        var zoneLow = value & 0xFF;
        var zoneHigh = (value >> 8) & 0xFF;
        if (zoneLow > low) low = zoneLow;
        if (zoneHigh < high) high = zoneHigh;
    }

    private static Int32 GetValue(Dictionary<Int32, Int32> global, Dictionary<Int32, Int32> zone, Int32 oper, Int32 defaultValue)
    {
        if (zone.TryGetValue(oper, out var value)) return value;
        if (global.TryGetValue(oper, out value)) return value;
        return defaultValue;
    }

    private static Int32 GetAdd(Dictionary<Int32, Int32> global, Dictionary<Int32, Int32> zone, Int32 oper)
    {
        var value = 0;
        if (global.TryGetValue(oper, out var item)) value += item;
        if (zone.TryGetValue(oper, out item)) value += item;
        return value;
    }

    private static Double ToSeconds(Int32 timecents, Double defaultValue)
    {
        if (timecents <= -12000) return defaultValue;
        if (timecents > 8000) timecents = 8000;

        var value = Math.Pow(2.0, timecents / 1200.0);
        if (value < 0.001) return 0.001;
        if (value > 60.0) return 60.0;

        return value;
    }

    private static Double ToLevel(Int32 centibels)
    {
        if (centibels <= 0) return 1.0;
        if (centibels >= 1440) return 0.0;

        return Math.Pow(10.0, -centibels / 200.0);
    }

    private static List<SampleRecord> ReadSampleRecords(RiffChunk chunk, Single[] waves)
    {
        var result = new List<SampleRecord>();
        if (chunk == null) return result;

        var records = ReadRecords(chunk, 46);
        for (var i = 0; i + 1 < records.Count; i++)
        {
            var record = records[i];
            var start = (Int32)Math.Min(U32(record, 20), (UInt32)waves.Length);
            var end = (Int32)Math.Min(U32(record, 24), (UInt32)waves.Length);
            if (end <= start) continue;

            var loopStart = (Int32)Math.Min(U32(record, 28), (UInt32)waves.Length);
            var loopEnd = (Int32)Math.Min(U32(record, 32), (UInt32)waves.Length);

            var length = end - start;
            var data = new Single[length];
            Array.Copy(waves, start, data, 0, length);

            var sample = new SoundBankSample
            {
                Name = ReadName(record, 0),
                Data = data,
                SampleRate = Math.Max(1, (Int32)U32(record, 36)),
                RootKey = record[40] <= 127 ? record[40] : 60,
                PitchCorrection = (SByte)record[41],
                LoopStart = Math.Max(0, loopStart - start),
                LoopEnd = Math.Max(0, loopEnd - start),
            };
            if (sample.LoopEnd > data.Length) sample.LoopEnd = data.Length;
            sample.Loop = sample.LoopEnd > sample.LoopStart;

            result.Add(new SampleRecord { Sample = sample });
        }

        return result;
    }

    private static List<PresetRecord> ReadPresetRecords(RiffChunk chunk)
    {
        var result = new List<PresetRecord>();
        if (chunk == null) return result;

        var records = ReadRecords(chunk, 38);
        for (var i = 0; i + 1 < records.Count; i++)
        {
            var record = records[i];
            result.Add(new PresetRecord
            {
                Name = ReadName(record, 0),
                Program = U16(record, 20),
                Bank = U16(record, 22),
                BagIndex = U16(record, 24),
            });
        }

        return result;
    }

    private static List<InstrumentRecord> ReadInstrumentRecords(RiffChunk chunk)
    {
        var result = new List<InstrumentRecord>();
        if (chunk == null) return result;

        var records = ReadRecords(chunk, 22);
        for (var i = 0; i + 1 < records.Count; i++)
        {
            result.Add(new InstrumentRecord
            {
                Name = ReadName(records[i], 0),
                BagIndex = U16(records[i], 20),
            });
        }

        return result;
    }

    private static List<BagRecord> ReadBagRecords(RiffChunk chunk)
    {
        var result = new List<BagRecord>();
        if (chunk == null) return result;

        var records = ReadRecords(chunk, 4);
        foreach (var record in records)
        {
            result.Add(new BagRecord
            {
                GenIndex = U16(record, 0),
            });
        }

        return result;
    }

    private static List<GenRecord> ReadGenRecords(RiffChunk chunk)
    {
        var result = new List<GenRecord>();
        if (chunk == null) return result;

        var records = ReadRecords(chunk, 4);
        foreach (var record in records)
        {
            result.Add(new GenRecord
            {
                Oper = U16(record, 0),
                Amount = (Int16)U16(record, 2),
            });
        }

        return result;
    }

    private static Single[] ReadWaveData(RiffChunk chunk)
    {
        if (chunk == null || chunk.Data.Count < 2) return [];

        var array = chunk.Data.Array;
        var offset = chunk.Data.Offset;
        var count = chunk.Data.Count / 2;
        var result = new Single[count];
        for (var i = 0; i < count; i++)
        {
            var value = (Int16)(array[offset + i * 2] | array[offset + i * 2 + 1] << 8);
            result[i] = value / 32768.0f;
        }

        return result;
    }

    private static List<Byte[]> ReadRecords(RiffChunk chunk, Int32 size)
    {
        var result = new List<Byte[]>();
        if (chunk == null || size <= 0) return result;

        for (var offset = 0; offset + size <= chunk.Data.Count; offset += size)
        {
            var record = new Byte[size];
            Array.Copy(chunk.Data.Array, chunk.Data.Offset + offset, record, 0, size);
            result.Add(record);
        }

        return result;
    }

    private static String ReadName(Byte[] data, Int32 offset)
    {
        var length = 0;
        while (length < 20 && offset + length < data.Length && data[offset + length] != 0) length++;

        return Encoding.UTF8.GetString(data, offset, length);
    }

    private static UInt16 U16(Byte[] data, Int32 offset) => (UInt16)(data[offset] | data[offset + 1] << 8);

    private static UInt32 U32(Byte[] data, Int32 offset) => (UInt32)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);

    private const Int32 GenStartAddrsOffset = 0;
    private const Int32 GenEndAddrsOffset = 1;
    private const Int32 GenStartLoopAddrsOffset = 2;
    private const Int32 GenEndLoopAddrsOffset = 3;
    private const Int32 GenPan = 17;
    private const Int32 GenAttackVolEnv = 34;
    private const Int32 GenHoldVolEnv = 35;
    private const Int32 GenDecayVolEnv = 36;
    private const Int32 GenSustainVolEnv = 37;
    private const Int32 GenReleaseVolEnv = 38;
    private const Int32 GenInstrument = 41;
    private const Int32 GenKeyRange = 43;
    private const Int32 GenVelocityRange = 44;
    private const Int32 GenInitialAttenuation = 48;
    private const Int32 GenCoarseTune = 51;
    private const Int32 GenFineTune = 52;
    private const Int32 GenSampleId = 53;
    private const Int32 GenSampleModes = 54;
    private const Int32 GenOverridingRootKey = 58;

    private sealed class PresetRecord
    {
        public String Name { get; set; }

        public Int32 Program { get; set; }

        public Int32 Bank { get; set; }

        public Int32 BagIndex { get; set; }
    }

    private sealed class InstrumentRecord
    {
        public String Name { get; set; }

        public Int32 BagIndex { get; set; }
    }

    private sealed class BagRecord
    {
        public Int32 GenIndex { get; set; }
    }

    private sealed class GenRecord
    {
        public Int32 Oper { get; set; }

        public Int32 Amount { get; set; }
    }

    private sealed class SampleRecord
    {
        public SoundBankSample Sample { get; set; }
    }
}
