using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio;
using NewLife.Audio.Containers;
using NewLife.Audio.Devices;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest;

/// <summary>录音：驱动基类的队列缓冲、事件/读取双通道与门面便捷方法</summary>
public class AudioRecorderTests
{
    /// <summary>假硬件：按真实时间放出 440 Hz 正弦波，模拟麦克风节奏</summary>
    private sealed class SineRecorder : AudioRecorderBase
    {
        private readonly System.Diagnostics.Stopwatch _watch = new();
        private Int64 _emitted;

        /// <summary>模拟设备启动延迟：该时间内原生读取返回 0</summary>
        public Int32 StartupDelayMilliseconds { get; set; }

        protected override void Open()
        {
            _emitted = 0;
            _watch.Restart();
        }

        protected override void Close() => _watch.Stop();

        protected override Int32 ReadNative(Span<Byte> buffer, Int32 timeoutMilliseconds, CancellationToken cancellationToken)
        {
            if (_watch.ElapsedMilliseconds < StartupDelayMilliseconds)
            {
                Thread.Sleep(Math.Min(timeoutMilliseconds, 20));
                return 0;
            }

            var format = Format;
            var frameBytes = Math.Max(1, format.Channels * format.BytesPerSample);
            var count = buffer.Length / frameBytes * frameBytes;
            if (count <= 0) return 0;

            var frames = count / frameBytes;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 按墙钟放出数据：消费快于实时时这里会等待，与真实硬件一致
                var available = (Int64)(_watch.Elapsed.TotalSeconds * format.SampleRate) - _emitted;
                if (available >= frames) break;

                var wait = (Int32)((frames - available) * 1000 / Math.Max(1, format.SampleRate)) + 1;
                Thread.Sleep(Math.Min(wait, 20));
            }

            for (var i = 0; i < frames; i++)
            {
                var sample = (Int16)(Math.Sin(2 * Math.PI * 440 * (_emitted + i) / format.SampleRate) * 12000);
                for (var ch = 0; ch < format.Channels; ch++)
                {
                    var offset = i * frameBytes + ch * 2;
                    buffer[offset] = (Byte)(sample & 0xFF);
                    buffer[offset + 1] = (Byte)((sample >> 8) & 0xFF);
                }
            }

            _emitted += frames;

            return count;
        }
    }

    /// <summary>假硬件：推送式（SDK 回调），由测试主动 Push</summary>
    private sealed class PushRecorder : AudioRecorderBase
    {
        public PushRecorder() : base(pushMode: true) { }

        protected override void Open() { }

        protected override void Close() { }

        public void Emit(ReadOnlySpan<Byte> pcm) => Push(pcm);
    }

    private sealed class FakeDriver : IAudioDriver
    {
        private readonly AudioFormat _format = new() { SampleRate = 16000, Channels = 1, BitsPerSample = 16 };

        public IAudioRecorder Recorder { get; set; }

        public String Name => "fake";

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices() => [];

        public AudioDeviceInfo GetDefaultOutputDevice() => new() { Id = "0", Name = "fake" };

        public AudioDeviceInfo GetDefaultInputDevice() => new() { Id = "0", Name = "fake", IsInput = true };

        public IAudioPlayer CreatePlayer() => throw new NotSupportedException();

        public IAudioRecorder CreateRecorder() => Recorder ??= new SineRecorder();

        public AudioFormat Format => _format;
    }

    private static (AudioRecorder Facade, FakeDriver Driver) CreateRecorder(Int32 bufferSeconds = 10)
    {
        var driver = new FakeDriver();
        var facade = new AudioRecorder(new AudioRecordOptions
        {
            Format = new AudioFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 },
            BufferSeconds = bufferSeconds,
        }, driver);

        return (facade, driver);
    }

    [Fact(DisplayName = "录音：按指定时长效录制并给出电平")]
    public void Capture_ByDuration()
    {
        var (recorder, _) = CreateRecorder();
        using var _recorder = recorder;

        var pcm = recorder.Capture(TimeSpan.FromMilliseconds(200));

        var expected = 16000 * 2 * 0.2;
        Assert.InRange(pcm.Data.Length, (Int32)(expected * 0.9), (Int32)(expected * 1.1));
        Assert.Equal(16000, pcm.Format.SampleRate);
        Assert.InRange(pcm.Duration, 0.18, 0.22);
        Assert.True(recorder.Peak > 0.1);
        Assert.True(recorder.Rms > 0.05);
    }

    [Fact(DisplayName = "录音：设备启动慢于首读超时仍能录到数据")]
    public void Capture_ToleratesStartupDelay()
    {
        var driver = new FakeDriver { Recorder = new SineRecorder { StartupDelayMilliseconds = 500 } };
        using var recorder = new AudioRecorder(new AudioRecordOptions
        {
            Format = new AudioFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 },
        }, driver);

        var pcm = recorder.Capture(TimeSpan.FromMilliseconds(300));

        Assert.True(pcm.Data.Length > 0, "设备启动延迟后应继续等待并录到数据");
    }

    [Fact(DisplayName = "录音：异步录制与同步录制结果一致")]
    public async Task CaptureAsync_MatchesSync()
    {
        var (recorder, _) = CreateRecorder();
        using var _recorder = recorder;

        var pcm = await recorder.CaptureAsync(TimeSpan.FromMilliseconds(100));

        Assert.InRange(pcm.Data.Length, 16000 * 2 / 10 * 0.9, 16000 * 2 / 10 * 1.1);
    }

    [Fact(DisplayName = "录音：边录边存产出可回读的 WAV")]
    public void RecordToFile_ProducesReadableWav()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rec-{Guid.NewGuid():N}.wav");
        try
        {
            var (recorder, _) = CreateRecorder();
            using var _recorder = recorder;

            var bytes = recorder.RecordToFile(path, TimeSpan.FromMilliseconds(200));

            Assert.True(bytes > 0);
            Assert.True(File.Exists(path));

            var info = AudioFile.Detect(path);
            Assert.Equal(AudioContainerKind.Wav, info.Container);

            var pcm = AudioFile.ReadPcm(path);
            Assert.InRange(pcm.Duration, 0.18, 0.24);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact(DisplayName = "录音：流式枚举按时间递增且帧来自池化缓冲")]
    public async Task Stream_ProducesFrames()
    {
        var (recorder, _) = CreateRecorder();
        using var _recorder = recorder;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var total = 0L;
        var timestamps = new List<TimeSpan>();
        var pooled = false;

        await foreach (var frame in recorder.StreamAsync().WithCancellation(cts.Token))
        {
            using (frame)
            {
                total += frame.Length;
                timestamps.Add(frame.Timestamp);
                pooled |= frame.IsPooled;
            }
        }

        Assert.True(total > 0);
        Assert.True(timestamps.Count > 0);
        Assert.True(pooled);
        for (var i = 1; i < timestamps.Count; i++) Assert.True(timestamps[i] >= timestamps[i - 1]);
    }

    [Fact(DisplayName = "录音：取消令牌能中断录制")]
    public void Capture_HonorsCancellation()
    {
        var (recorder, _) = CreateRecorder();
        using var _recorder = recorder;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => recorder.Capture(TimeSpan.FromSeconds(5), cts.Token));
    }

    [Fact(DisplayName = "录音：缓冲期内消费变慢不丢数据")]
    public void SlowConsumer_KeepsDataWithinBuffer()
    {
        var (recorder, _) = CreateRecorder(2);
        using var _recorder = recorder;
        var source = recorder.Source;

        source.StartRecording();

        // 消费方停 600 毫秒（远慢于实时），队列应把数据缓冲下来
        Thread.Sleep(600);

        var expected = 16000 * 2 * 400 / 1000;
        var buffer = new Byte[16000 * 2 / 50];
        var total = 0;
        while (total < expected)
        {
            var read = source.Read(buffer, 200);
            if (read <= 0) break;

            total += read;
        }

        source.StopRecording();

        Assert.True(total >= expected * 0.9, $"只读到 {total} 字节，期望约 {expected}");
        Assert.Equal(0, source.DroppedBytes);
    }

    [Fact(DisplayName = "录音：超出缓冲容量时丢弃最旧数据并计数")]
    public void Overflow_DropsOldest()
    {
        var (recorder, _) = CreateRecorder(0);
        using var _recorder = recorder;
        var source = recorder.Source;

        source.StartRecording();

        // 最小缓冲只留两块，停 300 毫秒必然溢出
        Thread.Sleep(300);

        var buffer = new Byte[16000 * 2 / 50];
        source.Read(buffer, 200);
        source.StopRecording();

        Assert.True(source.DroppedBytes > 0);
    }

    [Fact(DisplayName = "录音：推送式硬件无需采集线程也能读")]
    public void PushMode_WorksWithoutThread()
    {
        var recorder = new PushRecorder();
        using var _recorder = recorder;
        recorder.Init(new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16 });

        recorder.StartRecording();

        var chunk = new Byte[8000 * 2 / 50];
        for (var i = 0; i < 5; i++) recorder.Emit(chunk);

        var target = new Byte[chunk.Length * 5];
        var total = 0;
        while (total < target.Length)
        {
            var read = recorder.Read(target.AsSpan(total), 1000);
            if (read <= 0) break;

            total += read;
        }
        recorder.StopRecording();

        Assert.Equal(target.Length, total);
        Assert.Equal(0, recorder.DroppedBytes);
    }

    [Fact(DisplayName = "录音：事件旁路拿到副本且不影响主读取")]
    public void Event_ReceivesCopy()
    {
        var recorder = new PushRecorder();
        using var _recorder = recorder;
        recorder.Init(new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16 });

        var received = 0;
        recorder.DataAvailable += (s, pcm) => received += pcm.Length;

        recorder.StartRecording();

        var chunk = new Byte[8000 * 2 / 50];
        recorder.Emit(chunk);

        var read = recorder.Read(new Byte[chunk.Length], 1000);
        recorder.StopRecording();

        Assert.Equal(chunk.Length, received);
        Assert.Equal(chunk.Length, read);
    }
}
