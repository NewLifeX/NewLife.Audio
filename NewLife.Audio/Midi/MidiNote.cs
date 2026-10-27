using System;

namespace NewLife.Audio.Midi;

/// <summary>配对后的 MIDI 音符</summary>
/// <remarks>音符由 Note On/Note Off 事件配对产生；秒数由 <see cref="MidiTempoMap"/> 换算。</remarks>
public sealed class MidiNote
{
    /// <summary>轨道索引</summary>
    public Int32 Track { get; set; }

    /// <summary>通道号（0~15）</summary>
    public Int32 Channel { get; set; }

    /// <summary>音符号（0~127）</summary>
    public Int32 Number { get; set; }

    /// <summary>力度（0~127）</summary>
    public Int32 Velocity { get; set; }

    /// <summary>起始 tick</summary>
    public Int64 StartTick { get; set; }

    /// <summary>结束 tick</summary>
    public Int64 EndTick { get; set; }

    /// <summary>起始秒</summary>
    public Double StartSeconds { get; set; }

    /// <summary>持续秒数</summary>
    public Double DurationSeconds { get; set; }

    /// <summary>结束秒</summary>
    public Double EndSeconds => StartSeconds + DurationSeconds;

    /// <summary>频率（Hz），按十二平均律计算</summary>
    public Double Frequency => 440.0 * Math.Pow(2.0, (Number - 69) / 12.0);

    /// <summary>音名（如 C4、A#3）</summary>
    public String Name
    {
        get
        {
            var names = new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            var note = Number;
            if (note < 0) note = 0;
            if (note > 127) note = 127;
            return names[note % 12] + (note / 12 - 1);
        }
    }
}
