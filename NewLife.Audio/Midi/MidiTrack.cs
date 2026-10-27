using System;
using System.Collections.Generic;

namespace NewLife.Audio.Midi;

/// <summary>MIDI 轨道</summary>
public sealed class MidiTrack
{
    /// <summary>轨道名（Meta 0x03）</summary>
    public String Name { get; set; }

    /// <summary>事件列表（按 tick 升序）</summary>
    public List<MidiEvent> Events { get; } = [];

    /// <summary>轨道结束 tick</summary>
    public Int64 EndTick
    {
        get
        {
            var tick = 0L;
            foreach (var item in Events)
            {
                if (item.Tick > tick) tick = item.Tick;
            }

            return tick;
        }
    }

    /// <summary>添加事件</summary>
    /// <param name="e">MIDI 事件</param>
    public void Add(MidiEvent e)
    {
        if (e == null) throw new ArgumentNullException(nameof(e));

        Events.Add(e);
    }

    /// <summary>配对 Note On/Note Off，返回音符列表</summary>
    /// <returns>音符列表，按起始 tick 排序</returns>
    /// <remarks>同通道同音高的重叠音符按先进先出配对；未闭合音符在轨道结束时截断。</remarks>
    public IList<MidiNote> GetNotes()
    {
        var sorted = new List<IndexedEvent>(Events.Count);
        for (var i = 0; i < Events.Count; i++) sorted.Add(new IndexedEvent(Events[i], i));
        sorted.Sort(CompareEvent);

        var pending = new Dictionary<Int32, Queue<MidiEvent>>();
        var notes = new List<MidiNote>();
        var endTick = EndTick;

        foreach (var indexed in sorted)
        {
            var item = indexed.Event;
            var key = (item.Channel << 8) | (item.Data1 & 0x7F);

            if (item.Type == MidiEventType.NoteOn && item.Data2 > 0)
            {
                if (!pending.TryGetValue(key, out var queue))
                {
                    queue = new Queue<MidiEvent>();
                    pending[key] = queue;
                }
                queue.Enqueue(item);
            }
            else if (item.Type == MidiEventType.NoteOff || (item.Type == MidiEventType.NoteOn && item.Data2 == 0))
            {
                if (pending.TryGetValue(key, out var queue) && queue.Count > 0)
                {
                    var on = queue.Dequeue();
                    notes.Add(CreateNote(on, item.Tick));
                }
            }
        }

        foreach (var item in pending)
        {
            while (item.Value.Count > 0)
            {
                var on = item.Value.Dequeue();
                notes.Add(CreateNote(on, Math.Max(on.Tick, endTick)));
            }
        }

        notes.Sort((a, b) => a.StartTick.CompareTo(b.StartTick));
        return notes;
    }

    private static MidiNote CreateNote(MidiEvent on, Int64 endTick)
    {
        if (endTick <= on.Tick) endTick = on.Tick + 1;

        return new MidiNote
        {
            Channel = on.Channel,
            Number = on.Data1,
            Velocity = on.Data2,
            StartTick = on.Tick,
            EndTick = endTick,
        };
    }

    private static Int32 CompareEvent(IndexedEvent a, IndexedEvent b)
    {
        var result = a.Event.Tick.CompareTo(b.Event.Tick);
        if (result != 0) return result;

        // 同一 tick 让 Note Off 先于 Note On，避免同音重复时配对错位
        var aOff = a.Event.Type == MidiEventType.NoteOff || (a.Event.Type == MidiEventType.NoteOn && a.Event.Data2 == 0);
        var bOff = b.Event.Type == MidiEventType.NoteOff || (b.Event.Type == MidiEventType.NoteOn && b.Event.Data2 == 0);
        if (aOff != bOff) return aOff ? -1 : 1;

        return a.Index.CompareTo(b.Index);
    }

    private readonly struct IndexedEvent
    {
        public MidiEvent Event { get; }

        public Int32 Index { get; }

        public IndexedEvent(MidiEvent e, Int32 index)
        {
            Event = e;
            Index = index;
        }
    }
}
