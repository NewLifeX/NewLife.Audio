using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NewLife.Audio.Midi;

/// <summary>MIDI 速度变化</summary>
public sealed class MidiTempoChange
{
    /// <summary>起始 tick</summary>
    public Int64 Tick { get; set; }

    /// <summary>起始秒</summary>
    public Double Seconds { get; set; }

    /// <summary>微秒/四分音符</summary>
    public Int32 MicrosecondsPerQuarter { get; set; }

    /// <summary>BPM</summary>
    public Double Bpm => MicrosecondsPerQuarter > 0 ? 60000000.0 / MicrosecondsPerQuarter : 0;
}

/// <summary>MIDI 拍号变化</summary>
public sealed class MidiTimeSignatureChange
{
    /// <summary>起始 tick</summary>
    public Int64 Tick { get; set; }

    /// <summary>起始秒</summary>
    public Double Seconds { get; set; }

    /// <summary>拍号分子</summary>
    public Int32 Numerator { get; set; }

    /// <summary>拍号分母</summary>
    public Int32 Denominator { get; set; }
}

/// <summary>MIDI 速度映射</summary>
/// <remarks>
/// PPQ 时基下按 tempo 事件分段积分；SMPTE 时基下 tick 与秒直接线性换算，忽略 tempo。
/// 默认速度为 500000 微秒/四分音符（120 BPM）。
/// </remarks>
public sealed class MidiTempoMap
{
    private readonly List<MidiTempoChange> _changes = [];
    private readonly List<MidiTimeSignatureChange> _signatures = [];
    private readonly Int32 _division;
    private readonly Boolean _isSmpte;
    private readonly Double _ticksPerSecond;

    /// <summary>速度变化列表（按 tick 升序）</summary>
    public IList<MidiTempoChange> TempoChanges => _changes;

    /// <summary>拍号变化列表（按 tick 升序）</summary>
    public IList<MidiTimeSignatureChange> TimeSignatures => _signatures;

    /// <summary>是否为 SMPTE 时基</summary>
    public Boolean IsSmpte => _isSmpte;

    /// <summary>每四分音符 tick 数（PPQ），SMPTE 时为 0</summary>
    public Int32 TicksPerQuarterNote => _isSmpte ? 0 : _division;

    /// <summary>每秒 tick 数（SMPTE）</summary>
    public Double TicksPerSecond => _ticksPerSecond;

    /// <summary>从 MIDI 文件构建速度映射</summary>
    /// <param name="file">MIDI 文件</param>
    public MidiTempoMap(MidiFile file)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));

        _isSmpte = file.IsSmpte;
        _division = file.IsSmpte ? 0 : file.TicksPerQuarterNote;
        _ticksPerSecond = file.IsSmpte ? file.SmpteTicksPerSecond : 0;
        if (!_isSmpte && _division <= 0) throw new InvalidDataException("The MIDI division must be a positive PPQ value.");
        if (_isSmpte && _ticksPerSecond <= 0) throw new InvalidDataException("The MIDI SMPTE division is invalid.");

        var tempos = new List<MidiEvent>();
        var signatures = new List<MidiEvent>();
        foreach (var track in file.Tracks)
        {
            foreach (var item in track.Events)
            {
                if (item.Type != MidiEventType.Meta) continue;

                if (item.MetaType == 0x51 && item.Data != null && item.Data.Length >= 3) tempos.Add(item);
                else if (item.MetaType == 0x58 && item.Data != null && item.Data.Length >= 2) signatures.Add(item);
            }
        }

        tempos = tempos.OrderBy(e => e.Tick).ToList();
        signatures = signatures.OrderBy(e => e.Tick).ToList();

        if (!_isSmpte)
        {
            var lastTick = 0L;
            var lastSeconds = 0.0;
            var lastTempo = 500000;

            _changes.Add(new MidiTempoChange { Tick = 0, Seconds = 0, MicrosecondsPerQuarter = lastTempo });

            foreach (var item in tempos)
            {
                var tempo = item.TempoMicroseconds;
                if (tempo <= 0) continue;

                // 同一 tick 的重复速度只保留最后一个
                if (item.Tick == lastTick && _changes.Count > 0)
                {
                    _changes[_changes.Count - 1].MicrosecondsPerQuarter = tempo;
                    lastTempo = tempo;
                    continue;
                }

                if (item.Tick < lastTick) continue;

                lastSeconds += (item.Tick - lastTick) * lastTempo / (_division * 1000000.0);
                lastTick = item.Tick;
                lastTempo = tempo;
                _changes.Add(new MidiTempoChange { Tick = lastTick, Seconds = lastSeconds, MicrosecondsPerQuarter = tempo });
            }
        }

        foreach (var item in signatures)
        {
            _signatures.Add(new MidiTimeSignatureChange
            {
                Tick = item.Tick,
                Seconds = TickToSeconds(item.Tick),
                Numerator = item.TimeSignatureNumerator,
                Denominator = item.TimeSignatureDenominator,
            });
        }

        if (_signatures.Count == 0)
        {
            _signatures.Add(new MidiTimeSignatureChange
            {
                Tick = 0,
                Seconds = 0,
                Numerator = 4,
                Denominator = 4,
            });
        }
    }

    /// <summary>tick 转秒</summary>
    /// <param name="tick">tick</param>
    /// <returns>秒</returns>
    public Double TickToSeconds(Int64 tick)
    {
        if (tick <= 0) return 0;

        if (_isSmpte) return _ticksPerSecond > 0 ? tick / _ticksPerSecond : 0;

        var index = FindChange(tick);
        var change = _changes[index];
        return change.Seconds + (tick - change.Tick) * change.MicrosecondsPerQuarter / (_division * 1000000.0);
    }

    /// <summary>秒转 tick</summary>
    /// <param name="seconds">秒</param>
    /// <returns>tick</returns>
    public Int64 SecondsToTick(Double seconds)
    {
        if (seconds <= 0) return 0;

        if (_isSmpte) return _ticksPerSecond > 0 ? (Int64)Math.Round(seconds * _ticksPerSecond) : 0;

        var index = FindChangeBySeconds(seconds);
        var change = _changes[index];
        return change.Tick + (Int64)Math.Round((seconds - change.Seconds) * _division * 1000000.0 / change.MicrosecondsPerQuarter);
    }

    /// <summary>取指定 tick 处的 BPM</summary>
    /// <param name="tick">tick</param>
    /// <returns>BPM，SMPTE 时返回 120</returns>
    public Double BpmAt(Int64 tick)
    {
        if (_isSmpte) return 120;

        return _changes[FindChange(tick)].Bpm;
    }

    /// <summary>取指定 tick 处的拍号</summary>
    /// <param name="tick">tick</param>
    /// <returns>拍号变化</returns>
    public MidiTimeSignatureChange TimeSignatureAt(Int64 tick)
    {
        var result = _signatures[0];
        foreach (var item in _signatures)
        {
            if (item.Tick > tick) break;
            result = item;
        }

        return result;
    }

    /// <summary>生成节拍线时间点</summary>
    /// <param name="endSeconds">结束秒</param>
    /// <returns>节拍线秒数列表，含小节线标识</returns>
    public IList<Tuple<Double, Boolean>> GetBeatLines(Double endSeconds)
    {
        var lines = new List<Tuple<Double, Boolean>>();
        if (endSeconds <= 0) return lines;

        if (_isSmpte)
        {
            // SMPTE 没有音乐意义上的小节，按 1 秒画线
            for (var t = 0.0; t <= endSeconds; t += 1.0) lines.Add(Tuple.Create(t, false));
            return lines;
        }

        foreach (var signature in _signatures)
        {
            var nextTick = Int64.MaxValue;
            foreach (var item in _signatures)
            {
                if (item.Tick > signature.Tick)
                {
                    nextTick = item.Tick;
                    break;
                }
            }

            var startSeconds = signature.Seconds;
            var end = nextTick == Int64.MaxValue ? endSeconds : Math.Min(endSeconds, TickToSeconds(nextTick));
            if (startSeconds >= end) continue;

            var beatTicks = _division * 4.0 / Math.Max(1, signature.Denominator);
            if (beatTicks <= 0) continue;

            var beatIndex = 0;
            var tick = signature.Tick;
            while (tick < nextTick)
            {
                var seconds = TickToSeconds(tick);
                if (seconds > endSeconds) break;
                if (seconds >= startSeconds) lines.Add(Tuple.Create(seconds, beatIndex % Math.Max(1, signature.Numerator) == 0));

                beatIndex++;
                tick = signature.Tick + (Int64)Math.Round(beatIndex * beatTicks);
            }
        }

        return lines;
    }

    private Int32 FindChange(Int64 tick)
    {
        var index = 0;
        for (var i = 1; i < _changes.Count; i++)
        {
            if (_changes[i].Tick > tick) break;
            index = i;
        }

        return index;
    }

    private Int32 FindChangeBySeconds(Double seconds)
    {
        var index = 0;
        for (var i = 1; i < _changes.Count; i++)
        {
            if (_changes[i].Seconds > seconds) break;
            index = i;
        }

        return index;
    }
}
