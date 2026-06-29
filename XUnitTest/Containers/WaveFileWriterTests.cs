using System;
using System.IO;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest.Containers;

public class WaveFileWriterTests
{
    [Fact(DisplayName = "WaveFileWriter构造后自动写入WAV头")]
    public void Constructor_WritesWavHeader()
    {
        using var ms = new MemoryStream();
        var format = new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16 };
        using var writer = new WaveFileWriter(ms, format);

        Assert.True(ms.Length >= 44);
        var data = ms.ToArray();
        Assert.Equal((Byte)'R', data[0]);
        Assert.Equal((Byte)'I', data[1]);
        Assert.Equal((Byte)'F', data[2]);
        Assert.Equal((Byte)'F', data[3]);
    }

    [Fact(DisplayName = "WaveFileWriter WriteFrame累加数据")]
    public void WriteFrame_AccumulatesData()
    {
        using var ms = new MemoryStream();
        var format = new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16 };
        using var writer = new WaveFileWriter(ms, format);

        var before = ms.Length;
        var frame = new Byte[160 * 2]; // 10ms @8kHz 16-bit
        writer.WriteFrame(frame);
        var after = ms.Length;

        Assert.Equal(before + frame.Length, after);
    }

    [Fact(DisplayName = "WaveFileWriter WriteMetadata不抛异常")]
    public void WriteMetadata_DoesNotThrow()
    {
        using var ms = new MemoryStream();
        var format = AudioFormat.Default;
        using var writer = new WaveFileWriter(ms, format);

        var metadata = new AudioMetadata { Title = "Test", Artist = "Tester" };
        writer.WriteMetadata(metadata);
        // 不抛异常即通过
    }

    [Fact(DisplayName = "WaveFileWriter Flush不抛异常")]
    public void Flush_DoesNotThrow()
    {
        using var ms = new MemoryStream();
        var format = AudioFormat.Default;
        using var writer = new WaveFileWriter(ms, format);
        writer.Flush();
    }

    [Fact(DisplayName = "WaveFileWriter构造传null stream抛异常")]
    public void Constructor_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new WaveFileWriter(null, AudioFormat.Default));
    }

    [Fact(DisplayName = "WaveFileWriter构造传null format抛异常")]
    public void Constructor_NullFormat_Throws()
    {
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentNullException>(() => new WaveFileWriter(ms, null));
    }

    [Fact(DisplayName = "WaveFileWriter Format属性与构造时一致")]
    public void Format_MatchesConstructorArg()
    {
        using var ms = new MemoryStream();
        var format = new AudioFormat { SampleRate = 44100, Channels = 2, BitsPerSample = 16 };
        using var writer = new WaveFileWriter(ms, format);

        Assert.Equal(44100, writer.Format.SampleRate);
        Assert.Equal(2, writer.Format.Channels);
    }
}
