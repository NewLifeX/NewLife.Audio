using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NewLife.Audio.Midi;

/// <summary>MIDI 输出序列器</summary>
/// <remarks>按 tempo map 把 MIDI 事件实时发送到 <see cref="IMidiOutput"/>；支持加速播放与取消。</remarks>
/// <example>
/// <code>
/// var midi = MidiFile.Read("song.mid");
/// using var output = MidiOutputDevice.OpenDefault();
/// var player = new MidiOutputSequencer(midi, output);
/// await player.PlayAsync();
/// </code>
/// </example>
public sealed class MidiOutputSequencer
{
    private CancellationTokenSource _cts;

    /// <summary>是否因停止而取消</summary>
    public Boolean IsCancelled { get; private set; }

    /// <summary>MIDI 文件</summary>
    public MidiFile File { get; }

    /// <summary>MIDI 输出</summary>
    public IMidiOutput Output { get; }

    /// <summary>序列索引（Format 2）</summary>
    public Int32 SequenceIndex { get; }

    /// <summary>播放速度倍率（1 为正常速度）</summary>
    public Double Speed { get; set; } = 1.0;

    /// <summary>停止时发送 All Notes Off</summary>
    public Boolean SendAllNotesOffOnStop { get; set; } = true;

    /// <summary>创建序列器</summary>
    /// <param name="file">MIDI 文件</param>
    /// <param name="output">MIDI 输出</param>
    /// <param name="sequenceIndex">Format 2 序列索引</param>
    public MidiOutputSequencer(MidiFile file, IMidiOutput output, Int32 sequenceIndex = 0)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));
        Output = output ?? throw new ArgumentNullException(nameof(output));
        SequenceIndex = sequenceIndex;
    }

    /// <summary>阻塞播放</summary>
    /// <param name="cancellationToken">取消令牌</param>
    public void Play(CancellationToken cancellationToken = default)
    {
        IsCancelled = false;

        var midi = File.GetSequence(SequenceIndex);
        var tempo = midi.GetTempoMap();
        var events = new List<TimedEvent>();
        foreach (var track in midi.Tracks)
        {
            foreach (var item in track.Events)
            {
                if (item.Type == MidiEventType.Meta) continue;
                events.Add(new TimedEvent(tempo.TickToSeconds(item.Tick), item));
            }
        }
        events.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));

        if (events.Count == 0) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;
        var speed = Speed > 0 ? Speed : 1.0;
        var watch = Stopwatch.StartNew();

        try
        {
            foreach (var item in events)
            {
                token.ThrowIfCancellationRequested();

                var target = item.Seconds / speed * 1000.0;
                while (watch.Elapsed.TotalMilliseconds < target)
                {
                    token.ThrowIfCancellationRequested();
                    var remain = target - watch.Elapsed.TotalMilliseconds;
                    if (remain > 20) token.WaitHandle.WaitOne(20);
                    else if (remain > 1) Thread.Sleep(1);
                    else break;
                }

                Output.Send(item.Event);
            }
        }
        catch (OperationCanceledException)
        {
            IsCancelled = true;
        }
        finally
        {
            if (SendAllNotesOffOnStop)
            {
                try { Output.Reset(); } catch { /* 设备可能已关闭 */ }
            }
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>异步播放</summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>播放任务</returns>
    public Task PlayAsync(CancellationToken cancellationToken = default) => Task.Run(() => Play(cancellationToken), cancellationToken);

    /// <summary>停止播放</summary>
    public void Stop()
    {
        _cts?.Cancel();
    }

    private readonly struct TimedEvent
    {
        public Double Seconds { get; }

        public MidiEvent Event { get; }

        public TimedEvent(Double seconds, MidiEvent e)
        {
            Seconds = seconds;
            Event = e;
        }
    }
}
