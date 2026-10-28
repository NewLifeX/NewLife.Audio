using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio;
using NewLife.Audio.Devices;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest;

/// <summary>播放门面：分块送入、音量缩放与文件回放</summary>
public class AudioPlayerTests
{
    /// <summary>假播放器：统计写入量；可选按真实时长节流，模拟声卡节奏</summary>
    private sealed class FakePlayer : IAudioPlayer
    {
        private readonly Object _lock = new();

        public Int64 Total { get; private set; }

        public Byte[] FirstChunk { get; private set; }

        public AudioFormat Format { get; private set; }

        public Boolean Throttle { get; set; }

        public Int32 BufferedBytes => 0;

        public event EventHandler PlaybackStopped;

        public void Init(AudioFormat format, String deviceId = null) => Format = format;

        public void Play() { }

        public void Pause() { }

        public void Stop() => PlaybackStopped?.Invoke(this, EventArgs.Empty);

        public void Write(Byte[] data)
        {
            lock (_lock)
            {
                Total += data.Length;
                if (FirstChunk == null && data.Length > 0) FirstChunk = data.ToArray();
            }

            // 按字节率节流，让播放真实占用一段时间
            if (Throttle && Format != null && Format.ByteRate > 0)
                Thread.Sleep(Math.Max(1, data.Length * 1000 / Format.ByteRate));
        }

        public void Dispose() { }
    }

    private sealed class FakeDriver : IAudioDriver
    {
        public FakePlayer Player { get; } = new();

        public String Name => "fake";

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices() => [];

        public AudioDeviceInfo GetDefaultOutputDevice() => new() { Id = "0", Name = "fake" };

        public AudioDeviceInfo GetDefaultInputDevice() => new() { Id = "0", Name = "fake", IsInput = true };

        public IAudioPlayer CreatePlayer() => Player;

        public IAudioRecorder CreateRecorder() => throw new NotSupportedException();
    }

    private static AudioPcm CreatePcm(Int32 milliseconds)
    {
        var format = new AudioFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 };
        var count = format.ByteRate * milliseconds / 1000;
        var data = new Byte[count];

        for (var i = 0; i + 1 < count; i += 2)
        {
            var sample = (Int16)8000;
            data[i] = (Byte)(sample & 0xFF);
            data[i + 1] = (Byte)((sample >> 8) & 0xFF);
        }

        return new AudioPcm { Format = format, Data = data };
    }

    [Fact(DisplayName = "播放：整段 PCM 全部送入播放器")]
    public void Play_WritesAllBytes()
    {
        var driver = new FakeDriver();
        using var player = new AudioPlayer(driver);
        var pcm = CreatePcm(200);

        player.Play(pcm);

        Assert.Equal(pcm.Data.Length, driver.Player.Total);
        Assert.Equal(16000, driver.Player.Format.SampleRate);
        Assert.InRange(player.Duration, 0.18, 0.22);
    }

    [Fact(DisplayName = "播放：音量 0.5 时样本幅度减半")]
    public void Play_AppliesVolume()
    {
        var driver = new FakeDriver();
        using var player = new AudioPlayer(driver) { Volume = 0.5 };

        player.Play(CreatePcm(50));

        var first = (Int16)(driver.Player.FirstChunk[0] | driver.Player.FirstChunk[1] << 8);
        Assert.InRange(first, 3999, 4001);
    }

    [Fact(DisplayName = "播放：文件回放送入的字节数与 PCM 一致")]
    public async Task PlayFile_WritesPcmBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"play-{Guid.NewGuid():N}.wav");
        var pcm = CreatePcm(200);
        AudioFile.WritePcm(path, pcm);

        try
        {
            var driver = new FakeDriver();
            using var player = new AudioPlayer(driver);

            await player.PlayFileAsync(path);

            Assert.InRange(driver.Player.Total, pcm.Data.Length * 0.95, pcm.Data.Length * 1.05);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact(DisplayName = "播放：无音频数据时直接返回不抛异常")]
    public void Play_EmptyPcm_Returns()
    {
        var driver = new FakeDriver();
        using var player = new AudioPlayer(driver);

        player.Play(new AudioPcm { Format = new AudioFormat(), Data = [] });

        Assert.Equal(0, driver.Player.Total);
    }

    [Fact(DisplayName = "播放：PCM 观察事件能拿到下发数据")]
    public void Play_RaisesPcmData()
    {
        var driver = new FakeDriver();
        using var player = new AudioPlayer(driver);

        var received = 0L;
        player.PcmDataAvailable += (s, pcm) => received += pcm.Length;

        var pcm = CreatePcm(100);
        player.Play(pcm);

        Assert.Equal(pcm.Data.Length, received);
    }

    [Fact(DisplayName = "播放：文件播放可跳转且位置随之前进")]
    public async Task PlayFile_SeekWorks()
    {
        var path = Path.Combine(Path.GetTempPath(), $"seek-{Guid.NewGuid():N}.wav");
        var pcm = CreatePcm(1000);
        AudioFile.WritePcm(path, pcm);

        try
        {
            var driver = new FakeDriver();
            driver.Player.Throttle = true;

            using var player = new AudioPlayer(driver);

            var task = player.PlayFileAsync(path);

            // 等播放确实开始后跳到 0.6 秒，应跳过前面的数据
            await Task.Delay(200);
            Assert.True(player.Seek(0.6));

            await task;

            Assert.InRange(player.Position, 0.55, 1.05);
            Assert.True(driver.Player.Total < pcm.Data.Length * 0.95, $"跳转后不应播完整个文件，实际 {driver.Player.Total}");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
