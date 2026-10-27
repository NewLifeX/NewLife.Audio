using System;
using System.Collections.Generic;
using System.IO;

namespace NewLife.Audio.Midi;

/// <summary>Downloadable Sounds（DLS）采样音源</summary>
/// <remarks>
/// 解析 RIFF/DLS 容器中的乐器列表（lins）与波形池（wvpl），支持 Level 1/2 的 PCM 波形、
/// 按键/力度范围、根音、细调、衰减与循环。DLS 的调制器、滤波器与复杂包络不参与合成，
/// 使用简化的默认音量包络；立体声波形在读取时下混为单声道。
/// </remarks>
/// <example>
/// <code>
/// var bank = DlsSoundBank.Read("sample.dls");
/// var midi = MidiFile.Read("song.mid");
/// var pcm = MidiSynthesizer.Render(midi, new MidiRenderOptions { SoundBank = bank });
/// </code>
/// </example>
public sealed class DlsSoundBank : SoundBank
{
    private readonly List<SoundBankPreset> _presets = [];

    /// <summary>预设列表</summary>
    public override IList<SoundBankPreset> Presets => _presets;

    /// <summary>从文件读取 DLS</summary>
    /// <param name="path">文件路径</param>
    /// <returns>采样音源</returns>
    public static DlsSoundBank Read(String path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));

        return Read(File.ReadAllBytes(path));
    }

    /// <summary>从流读取 DLS</summary>
    /// <param name="stream">数据流</param>
    /// <returns>采样音源</returns>
    public static DlsSoundBank Read(Stream stream)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Read(memory.ToArray());
    }

    /// <summary>从字节数组读取 DLS</summary>
    /// <param name="data">文件字节</param>
    /// <returns>采样音源</returns>
    /// <exception cref="ArgumentNullException">数据为空</exception>
    /// <exception cref="InvalidDataException">文件不是有效的 DLS</exception>
    public static DlsSoundBank Read(Byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));

        var root = RiffReader.Parse(data);
        if (!String.Equals(root.Type, "DLS ", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The file is not a DLS bank.");

        var lins = root.FindList("lins");
        var wvpl = root.FindList("wvpl");
        if (lins == null || wvpl == null) throw new InvalidDataException("The DLS bank is missing lins or wvpl.");

        var waves = ReadWaves(wvpl);
        var bank = new DlsSoundBank();
        bank.BuildPresets(lins, waves);

        return bank;
    }

    private void BuildPresets(RiffChunk lins, List<SoundBankSample> waves)
    {
        foreach (var instrument in lins.Children)
        {
            if (!String.Equals(instrument.Id, "LIST", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(instrument.Type, "ins ", StringComparison.OrdinalIgnoreCase))
                continue;

            var header = instrument.Find("insh");
            if (header == null) continue;

            var bankNumber = (Int32)(RiffReader.ReadUInt32(header, 8) & 0x7FFFFFFF);
            if ((RiffReader.ReadUInt32(header, 8) & 0x80000000) != 0) bankNumber = 128;
            if (bankNumber > 127) bankNumber = 128;

            var preset = new SoundBankPreset
            {
                Name = "DLS " + RiffReader.ReadUInt32(header, 12),
                Bank = bankNumber,
                Program = (Int32)(RiffReader.ReadUInt32(header, 12) & 0x7F),
            };

            var regions = new List<RiffChunk>();
            foreach (var child in instrument.Children)
            {
                if (String.Equals(child.Id, "LIST", StringComparison.OrdinalIgnoreCase) &&
                    (String.Equals(child.Type, "rgn ", StringComparison.OrdinalIgnoreCase) ||
                     String.Equals(child.Type, "rgn2", StringComparison.OrdinalIgnoreCase)))
                    regions.Add(child);
            }
            if (regions.Count == 0) regions.Add(instrument);

            foreach (var region in regions)
            {
                var range = region.Find("rgnh");
                var link = region.Find("wlnk");
                if (link == null) continue;

                var tableIndex = (Int32)RiffReader.ReadUInt32(link, 8);
                if (tableIndex < 0 || tableIndex >= waves.Count) continue;

                var sample = waves[tableIndex];
                if (sample.Data.Length == 0) continue;

                var keyLow = range != null ? RiffReader.ReadUInt16(range, 0) : (UInt16)0;
                var keyHigh = range != null ? RiffReader.ReadUInt16(range, 2) : (UInt16)127;
                var velocityLow = range != null ? RiffReader.ReadUInt16(range, 4) : (UInt16)0;
                var velocityHigh = range != null ? RiffReader.ReadUInt16(range, 6) : (UInt16)127;
                if (keyLow > 127) keyLow = 0;
                if (keyHigh > 127) keyHigh = 127;
                if (velocityLow > 127) velocityLow = 0;
                if (velocityHigh > 127) velocityHigh = 127;
                if (keyLow > keyHigh || velocityLow > velocityHigh) continue;

                var waveSample = ReadWaveSample(region.Find("wsmp"));

                var loopStart = waveSample != null && waveSample.Loop ? waveSample.LoopStart : sample.LoopStart;
                var loopEnd = waveSample != null && waveSample.Loop ? waveSample.LoopEnd : sample.LoopEnd;
                if (loopStart < 0) loopStart = 0;
                if (loopEnd > sample.Data.Length) loopEnd = sample.Data.Length;

                var regionInfo = new SoundBankRegion
                {
                    Sample = sample,
                    KeyLow = keyLow,
                    KeyHigh = keyHigh,
                    VelocityLow = velocityLow,
                    VelocityHigh = velocityHigh,
                    RootKey = waveSample != null ? waveSample.UnityNote : -1,
                    FineTune = waveSample != null ? waveSample.FineTune : 0,
                    Attenuation = waveSample != null ? waveSample.Attenuation : 0,
                    Loop = loopEnd > loopStart,
                    SampleStart = 0,
                    SampleEnd = sample.Data.Length,
                    LoopStart = loopStart,
                    LoopEnd = loopEnd,
                };
                preset.Regions.Add(regionInfo);
            }

            if (preset.Regions.Count > 0) _presets.Add(preset);
        }
    }

    private static List<SoundBankSample> ReadWaves(RiffChunk wvpl)
    {
        var result = new List<SoundBankSample>();
        foreach (var wave in wvpl.Children)
        {
            if (!String.Equals(wave.Id, "LIST", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(wave.Type, "wave", StringComparison.OrdinalIgnoreCase))
                continue;

            var format = wave.Find("fmt ");
            var data = wave.Find("data");
            if (format == null || data == null || data.Data.Count == 0) continue;

            var formatTag = RiffReader.ReadUInt16(format, 0);
            if (formatTag != 1 && formatTag != 0xFFFE) continue;

            var channels = Math.Max(1, (Int32)RiffReader.ReadUInt16(format, 2));
            var sampleRate = Math.Max(1, (Int32)RiffReader.ReadUInt32(format, 4));
            var bits = RiffReader.ReadUInt16(format, 14);
            if (bits != 8 && bits != 16) continue;

            var waveSample = ReadWaveSample(wave.Find("wsmp"));
            var sample = new SoundBankSample
            {
                Name = "DLS wave " + result.Count,
                Data = ReadPcm(data, channels, bits),
                SampleRate = sampleRate,
                RootKey = waveSample != null ? waveSample.UnityNote : 60,
                PitchCorrection = 0,
                LoopStart = waveSample != null ? waveSample.LoopStart : 0,
                LoopEnd = waveSample != null ? waveSample.LoopEnd : 0,
                Loop = waveSample != null && waveSample.Loop,
            };
            if (sample.LoopEnd > sample.Data.Length) sample.LoopEnd = sample.Data.Length;
            if (sample.LoopEnd <= sample.LoopStart) sample.Loop = false;

            result.Add(sample);
        }

        return result;
    }

    private static Single[] ReadPcm(RiffChunk chunk, Int32 channels, Int32 bits)
    {
        var array = chunk.Data.Array;
        var offset = chunk.Data.Offset;
        var count = chunk.Data.Count;
        if (bits == 8)
        {
            var frames = count / channels;
            var result = new Single[frames];
            for (var i = 0; i < frames; i++)
            {
                var sum = 0;
                for (var channel = 0; channel < channels; channel++) sum += array[offset + i * channels + channel] - 128;
                result[i] = sum / (Single)channels / 128.0f;
            }

            return result;
        }
        else
        {
            var frames = count / (channels * 2);
            var result = new Single[frames];
            for (var i = 0; i < frames; i++)
            {
                var sum = 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    var index = offset + (i * channels + channel) * 2;
                    sum += (Int16)(array[index] | array[index + 1] << 8);
                }
                result[i] = sum / (Single)channels / 32768.0f;
            }

            return result;
        }
    }

    private static WaveSample ReadWaveSample(RiffChunk chunk)
    {
        if (chunk == null || chunk.Data.Count < 16) return null;

        var unityNote = RiffReader.ReadUInt16(chunk, 4);
        var fineTune = RiffReader.ReadInt16(chunk, 6);
        var attenuation = RiffReader.ReadInt32(chunk, 8);
        var loopCount = chunk.Data.Count >= 16 ? RiffReader.ReadUInt32(chunk, 16) : 0;

        var result = new WaveSample
        {
            UnityNote = unityNote <= 127 ? unityNote : 60,
            FineTune = fineTune,
            Attenuation = Math.Max(0, attenuation / 65536.0),
        };

        if (loopCount > 0 && chunk.Data.Count >= 36)
        {
            var loopType = RiffReader.ReadUInt32(chunk, 24);
            var loopStart = (Int32)RiffReader.ReadUInt32(chunk, 28);
            var loopLength = (Int32)RiffReader.ReadUInt32(chunk, 32);
            if (loopType == 0 && loopLength > 0)
            {
                result.Loop = true;
                result.LoopStart = loopStart;
                result.LoopEnd = loopStart + loopLength;
            }
        }

        return result;
    }

    private sealed class WaveSample
    {
        public Int32 UnityNote { get; set; } = 60;

        public Double FineTune { get; set; }

        public Double Attenuation { get; set; }

        public Boolean Loop { get; set; }

        public Int32 LoopStart { get; set; }

        public Int32 LoopEnd { get; set; }
    }
}
