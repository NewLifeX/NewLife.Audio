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
    /// <summary>假播放器：只统计写入量，缓冲即时清空，等价于“理想声卡”</summary>
    private sealed class FakePlayer : IAudioPlayer
    {
        private readonly Object _lock = new();

        public Int64 Total { get; private set; }

        public Byte[] FirstChunk { get; private set; }

        public AudioFormat Format { get; private set; }

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
}
