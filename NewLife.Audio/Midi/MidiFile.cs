using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NewLife.Audio.Midi;

/// <summary>标准 MIDI 文件（SMF 1.0）</summary>
/// <remarks>
/// 支持格式 0/1/2、PPQ 与 SMPTE 时基、VLQ、running status、Meta/SysEx 原样保留。
/// MIDI 是乐谱/事件流，不是采样音频；需要声音时用 <see cref="MidiSynthesizer"/> 渲染为 PCM。
/// </remarks>
/// <example>
/// <code>
/// var midi = MidiFile.Read("song.mid");
/// foreach (var note in midi.GetNotes())
///     Console.WriteLine($"{note.Name} {note.StartSeconds:n2}s {note.DurationSeconds:n2}s");
///
/// var pcm = MidiSynthesizer.Render(midi);
/// AudioFile.WritePcm("song.wav", pcm);
/// </code>
/// </example>
public sealed class MidiFile
{
    /// <summary>文件格式</summary>
    public MidiFileFormat Format { get; set; }

    /// <summary>时间基准（原始 16 位值；高位置 1 表示 SMPTE）</summary>
    public Int16 Division { get; set; } = 480;

    /// <summary>轨道列表</summary>
    public List<MidiTrack> Tracks { get; } = [];

    /// <summary>是否为 SMPTE 时基</summary>
    public Boolean IsSmpte => (Division & 0x8000) != 0;

    /// <summary>每四分音符 tick 数（PPQ），SMPTE 时为 0</summary>
    public Int32 TicksPerQuarterNote => IsSmpte ? 0 : Division & 0x7FFF;

    /// <summary>SMPTE 每秒 tick 数</summary>
    public Double SmpteTicksPerSecond
    {
        get
        {
            if (!IsSmpte) return 0;

            var frames = 256 - ((Division >> 8) & 0xFF);
            var ticksPerFrame = Division & 0xFF;
            return frames * ticksPerFrame;
        }
    }

    /// <summary>是否为 MPE（MIDI Polyphonic Expression）文件</summary>
    /// <remarks>检测 RPN 6（MPE Configuration）是否被设置为 1（Lower Zone）或 2（Upper Zone）。</remarks>
    public Boolean IsMpe
    {
        get
        {
            var rpnMsb = new Int32[16];
            var rpnLsb = new Int32[16];
            for (var i = 0; i < 16; i++)
            {
                rpnMsb[i] = 127;
                rpnLsb[i] = 127;
            }

            var events = new List<MidiEvent>();
            foreach (var track in Tracks) events.AddRange(track.Events);
            foreach (var item in events.OrderBy(e => e.Tick))
            {
                if (item.Type != MidiEventType.ControlChange || item.Channel < 0 || item.Channel > 15) continue;

                var channel = item.Channel;
                switch (item.Controller)
                {
                    case 101:
                        rpnMsb[channel] = item.ControllerValue;
                        break;
                    case 100:
                        rpnLsb[channel] = item.ControllerValue;
                        break;
                    case 6:
                        if (rpnMsb[channel] == 6 && rpnLsb[channel] == 0 && (item.ControllerValue == 1 || item.ControllerValue == 2))
                            return true;
                        break;
                }
            }

            return false;
        }
    }

    /// <summary>独立序列数（Format 2 为轨道数，其余为 1）</summary>
    public Int32 SequenceCount => Format == MidiFileFormat.MultiSong ? Tracks.Count : 1;

    /// <summary>获取独立序列（Format 2 按轨道拆分为独立文件）</summary>
    /// <param name="index">序列索引（0 基）</param>
    /// <returns>只包含该序列的 MIDI 文件</returns>
    /// <exception cref="ArgumentOutOfRangeException">索引越界</exception>
    public MidiFile GetSequence(Int32 index)
    {
        if (index < 0 || index >= SequenceCount)
            throw new ArgumentOutOfRangeException(nameof(index), "The MIDI sequence index is out of range.");

        if (Format != MidiFileFormat.MultiSong) return this;

        var file = new MidiFile
        {
            Format = MidiFileFormat.MultiTrack,
            Division = Division,
        };
        file.Tracks.Add(Tracks[index].Clone());
        return file;
    }

    /// <summary>总时长（秒）</summary>
    public Double Duration
    {
        get
        {
            var endTick = 0L;
            foreach (var track in Tracks)
            {
                var tick = track.EndTick;
                if (tick > endTick) endTick = tick;
            }

            return GetTempoMap().TickToSeconds(endTick);
        }
    }

    /// <summary>创建空 MIDI 文件</summary>
    /// <param name="format">文件格式</param>
    /// <param name="division">时间基准（PPQ）</param>
    /// <returns>MIDI 文件</returns>
    public static MidiFile Create(MidiFileFormat format = MidiFileFormat.MultiTrack, Int16 division = 480) => new()
    {
        Format = format,
        Division = division,
    };

    /// <summary>从字节数组解析</summary>
    /// <param name="data">SMF 数据</param>
    /// <returns>MIDI 文件</returns>
    /// <exception cref="ArgumentNullException">数据为空</exception>
    /// <exception cref="InvalidDataException">不是有效的 SMF 数据</exception>
    public static MidiFile Parse(Byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (data.Length < 14) throw new InvalidDataException("The MIDI data is too short to contain a header chunk.");
        if (data[0] != (Byte)'M' || data[1] != (Byte)'T' || data[2] != (Byte)'h' || data[3] != (Byte)'d')
            throw new InvalidDataException("Not a Standard MIDI File: missing MThd chunk.");

        var headerLength = ReadInt32(data, 4);
        if (headerLength < 6 || 8 + headerLength > data.Length)
            throw new InvalidDataException("The MIDI header chunk is invalid.");

        var format = (MidiFileFormat)ReadInt16(data, 8);
        var trackCount = ReadInt16(data, 10);
        var division = (Int16)ReadInt16(data, 12);
        if ((division & 0x8000) == 0 && (division & 0x7FFF) == 0)
            throw new InvalidDataException("The MIDI division must be a positive PPQ value.");
        if ((division & 0x8000) != 0 && (division & 0xFF) == 0)
            throw new InvalidDataException("The MIDI SMPTE division has no ticks per frame.");

        var file = new MidiFile { Format = format, Division = division };

        var position = 8 + headerLength;
        while (position + 8 <= data.Length)
        {
            var id = new String(new[] { (Char)data[position], (Char)data[position + 1], (Char)data[position + 2], (Char)data[position + 3] });
            var length = ReadInt32(data, position + 4);
            position += 8;

            if (length < 0 || position + length > data.Length)
                throw new InvalidDataException("The MIDI chunk length exceeds the file size.");

            if (id == "MTrk") file.Tracks.Add(MidiReader.ReadTrack(data, position, length));

            position += length;
        }

        if (file.Tracks.Count == 0)
            throw new InvalidDataException("The MIDI file does not contain any MTrk chunk.");

        // 头块声明的轨道数与实际不一致时以实际为准，不阻塞读取
        _ = trackCount;

        return file;
    }

    /// <summary>从流解析</summary>
    /// <param name="stream">数据流</param>
    /// <returns>MIDI 文件</returns>
    public static MidiFile Parse(Stream stream)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Parse(ms.ToArray());
    }

    /// <summary>读取 MIDI 文件</summary>
    /// <param name="path">文件路径</param>
    /// <returns>MIDI 文件</returns>
    public static MidiFile Read(String path) => Parse(File.ReadAllBytes(path));

    /// <summary>写入 MIDI 文件</summary>
    /// <param name="path">文件路径</param>
    public void Save(String path) => File.WriteAllBytes(path, ToArray());

    /// <summary>写入到流</summary>
    /// <param name="stream">目标流</param>
    public void Write(Stream stream)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));

        var data = ToArray();
        stream.Write(data, 0, data.Length);
    }

    /// <summary>序列化为字节数组</summary>
    /// <returns>SMF 数据</returns>
    public Byte[] ToArray() => MidiWriter.Write(this);

    /// <summary>构建速度映射</summary>
    /// <returns>速度映射</returns>
    public MidiTempoMap GetTempoMap() => new(this);

    /// <summary>获取所有轨道配对后的音符（含秒数）</summary>
    /// <returns>音符列表，按起始秒排序</returns>
    public IList<MidiNote> GetNotes()
    {
        var tempo = GetTempoMap();
        var list = new List<MidiNote>();

        for (var i = 0; i < Tracks.Count; i++)
        {
            var notes = Tracks[i].GetNotes();
            foreach (var note in notes)
            {
                note.Track = i;
                note.StartSeconds = tempo.TickToSeconds(note.StartTick);
                var endSeconds = tempo.TickToSeconds(note.EndTick);
                note.DurationSeconds = endSeconds > note.StartSeconds ? endSeconds - note.StartSeconds : 0;
                list.Add(note);
            }
        }

        list.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
        return list;
    }

    /// <summary>获取所有音符的最大复音数</summary>
    /// <returns>复音数</returns>
    public Int32 GetMaxPolyphony()
    {
        var notes = GetNotes();
        var points = new List<PolyphonyPoint>(notes.Count * 2);
        foreach (var note in notes)
        {
            points.Add(new PolyphonyPoint(note.StartSeconds, 1));
            points.Add(new PolyphonyPoint(note.EndSeconds, -1));
        }

        // 同一时刻先结束再开始，避免首尾相接的音符被算作重叠
        points.Sort((a, b) =>
        {
            var result = a.Seconds.CompareTo(b.Seconds);
            return result != 0 ? result : a.Delta.CompareTo(b.Delta);
        });

        var current = 0;
        var max = 0;
        foreach (var point in points)
        {
            current += point.Delta;
            if (current > max) max = current;
        }

        return max;
    }

    private readonly struct PolyphonyPoint
    {
        public Double Seconds { get; }

        public Int32 Delta { get; }

        public PolyphonyPoint(Double seconds, Int32 delta)
        {
            Seconds = seconds;
            Delta = delta;
        }
    }

    internal static Int32 ReadInt16(Byte[] data, Int32 offset) => (data[offset] << 8) | data[offset + 1];

    internal static Int32 ReadInt32(Byte[] data, Int32 offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
}
