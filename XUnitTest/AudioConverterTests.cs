using System;
using System.Collections.Generic;
using System.IO;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using NewLife.Audio.Streaming;
using Xunit;

namespace XUnitTest;

/// <summary>转码门面：全格式矩阵、参数调整与进度回调</summary>
public class AudioConverterTests
{
    /// <summary>生成 0.5 秒 440 Hz 正弦 WAV（44100 Hz 单声道 16 位）</summary>
    private static String CreateSource()
    {
        var format = new AudioFormat { SampleRate = 44100, Channels = 1, BitsPerSample = 16 };
        var count = (Int32)(format.ByteRate * 0.5);
        var data = new Byte[count];

        for (var i = 0; i + 1 < count; i += 2)
        {
            var index = i / 2;
            var sample = (Int16)(Math.Sin(2 * Math.PI * 440 * index / format.SampleRate) * 12000);
            data[i] = (Byte)(sample & 0xFF);
            data[i + 1] = (Byte)((sample >> 8) & 0xFF);
        }

        var path = Path.Combine(Path.GetTempPath(), $"src-{Guid.NewGuid():N}.wav");
        AudioFile.WritePcm(path, new AudioPcm { Format = format, Data = data });

        return path;
    }

    private static Double GetRms(Byte[] pcm)
    {
        if (pcm.Length < 2) return 0;

        var sum = 0.0;
        var count = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (Int16)(pcm[i] | pcm[i + 1] << 8);
            sum += (Double)sample * sample;
            count++;
        }

        return count > 0 ? Math.Sqrt(sum / count) : 0;
    }

    [Theory(DisplayName = "转码：各目标容器都能产出可回读的音频")]
    [InlineData(".wav")]
    [InlineData(".flac")]
    [InlineData(".mp3")]
    [InlineData(".aiff")]
    [InlineData(".m4a")]
    [InlineData(".aac")]
    [InlineData(".ogg")]
    public void Convert_AllTargets(String extension)
    {
        var source = CreateSource();
        var target = Path.Combine(Path.GetTempPath(), $"dst-{Guid.NewGuid():N}{extension}");
        try
        {
            var result = AudioConverter.Convert(source, target);

            Assert.True(File.Exists(target));
            Assert.True(new FileInfo(target).Length > 0);
            Assert.Equal(AudioProbe.ProbeKind(target), result.Container);

            var info = AudioFile.Detect(target);
            Assert.NotEqual(AudioContainerKind.Unknown, info.Container);

            var pcm = AudioFile.ReadPcm(target);
            Assert.InRange(pcm.Duration, 0.4, 0.65);
            Assert.True(GetRms(pcm.Data) > 100);
        }
        finally
        {
            if (File.Exists(source)) File.Delete(source);
            if (File.Exists(target)) File.Delete(target);
        }
    }

    [Fact(DisplayName = "转码：Opus 在编码后端可用时可用")]
    public void Convert_Opus()
    {
        if (!OpusCodec.IsAvailable) return;

        var source = CreateSource();
        var target = Path.ChangeExtension(source, ".opus");
        try
        {
            AudioConverter.Convert(source, target);

            var pcm = AudioFile.ReadPcm(target);
            Assert.InRange(pcm.Duration, 0.4, 0.7);
            Assert.Equal(48000, pcm.Format.SampleRate);
        }
        finally
        {
            if (File.Exists(source)) File.Delete(source);
            if (File.Exists(target)) File.Delete(target);
        }
    }

    [Fact(DisplayName = "转码：可同时改采样率并汇报进度")]
    public void Convert_ChangesSampleRate()
    {
        var source = CreateSource();
        var target = Path.Combine(Path.GetTempPath(), $"dst-{Guid.NewGuid():N}.flac");
        var reported = new List<Double>();
        try
        {
            AudioConverter.Convert(source, target, new AudioEncodeOptions { SampleRate = 8000, Channels = 1 }, new Progress<Double>(reported.Add));

            var pcm = AudioFile.ReadPcm(target);
            Assert.Equal(8000, pcm.Format.SampleRate);
            Assert.InRange(pcm.Duration, 0.4, 0.65);
            Assert.NotEmpty(reported);
        }
        finally
        {
            if (File.Exists(source)) File.Delete(source);
            if (File.Exists(target)) File.Delete(target);
        }
    }

    [Fact(DisplayName = "转码：源文件不存在时明确报错")]
    public void Convert_MissingSource_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => AudioConverter.Convert(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.wav"), Path.Combine(Path.GetTempPath(), "out.wav")));
    }

    [Fact(DisplayName = "写入：WritePcm 支持全部有编码器的容器")]
    public void WritePcm_SupportsAllContainers()
    {
        var format = new AudioFormat { SampleRate = 44100, Channels = 1, BitsPerSample = 16 };
        var data = new Byte[format.ByteRate / 2];
        for (var i = 0; i + 1 < data.Length; i += 2)
        {
            var sample = (Int16)6000;
            data[i] = (Byte)(sample & 0xFF);
            data[i + 1] = (Byte)((sample >> 8) & 0xFF);
        }

        var audio = new AudioPcm { Format = format, Data = data };
        foreach (var extension in new[] { ".wav", ".flac", ".mp3", ".aiff", ".m4a", ".aac", ".ogg" })
        {
            var path = Path.Combine(Path.GetTempPath(), $"write-{Guid.NewGuid():N}{extension}");
            try
            {
                AudioFile.WritePcm(path, audio);

                var pcm = AudioFile.ReadPcm(path);
                Assert.InRange(pcm.Duration, 0.4, 0.65);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
