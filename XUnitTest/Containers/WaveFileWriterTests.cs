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

    [Fact(DisplayName = "默认格式仍写 16 字节 fmt 与 44 字节头")]
    public void DefaultFormat_KeepsPlainFmt()
    {
        using var ms = new MemoryStream();
        using var writer = new WaveFileWriter(ms, new AudioFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 });
        writer.WriteFrame(new Byte[320]);
        writer.Flush();

        var data = ms.ToArray();
        Assert.Equal(16, BitConverter.ToInt32(data, 16));
        Assert.Equal((Byte)'d', data[36]);
    }

    [Fact(DisplayName = "声道掩码写 WAVEFORMATEXTENSIBLE 后可读回")]
    public void ChannelMask_WritesExtensible()
    {
        var format = new AudioFormat { SampleRate = 48000, Channels = 4, BitsPerSample = 16, ChannelMask = 0x33 };
        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(ms, format))
        {
            writer.WriteFrame(new Byte[4 * 2 * 50]);
            writer.Flush();
        }

        var data = ms.ToArray();
        Assert.Equal(40, BitConverter.ToInt32(data, 16));
        Assert.Equal(0xFFFE, BitConverter.ToUInt16(data, 20));
        Assert.Equal(0x33, BitConverter.ToInt32(data, 40));

        using var reader = new WaveFileReader(new MemoryStream(data));
        Assert.Equal(4, reader.Format.Channels);
        Assert.Equal(0x33, reader.Format.ChannelMask);
        Assert.Equal(0, reader.Format.ValidBitsPerSample);
        Assert.Equal(16, reader.Format.BitsPerSample);
    }

    [Fact(DisplayName = "24 位有效装入 32 位容器写 EXTENSIBLE 并可读回")]
    public void ValidBits_WritesExtensible()
    {
        var format = new AudioFormat { SampleRate = 48000, Channels = 2, BitsPerSample = 32, ValidBitsPerSample = 24 };
        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(ms, format))
        {
            writer.WriteFrame(new Byte[2 * 4 * 40]);
            writer.Flush();
        }

        var data = ms.ToArray();
        Assert.Equal(40, BitConverter.ToInt32(data, 16));
        Assert.Equal(24, BitConverter.ToUInt16(data, 38));

        using var reader = new WaveFileReader(new MemoryStream(data));
        Assert.Equal(32, reader.Format.BitsPerSample);
        Assert.Equal(24, reader.Format.ValidBitsPerSample);
        Assert.Equal(24, reader.Format.EffectiveValidBitsPerSample);
        Assert.Equal(4, reader.Format.BytesPerSample);
    }

    [Fact(DisplayName = "EXTENSIBLE 头带标签时读写位置正确")]
    public void Extensible_WithMetadata_RoundTrips()
    {
        var format = new AudioFormat { SampleRate = 48000, Channels = 6, BitsPerSample = 16, ChannelMask = 0x3F };
        var pcm = new Byte[6 * 2 * 960 * 2];
        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(ms, format))
        {
            writer.WriteMetadata(new AudioMetadata { Title = "多声道", Artist = "测试" });
            writer.WriteFrame(pcm);
            writer.Flush();
        }

        var data = ms.ToArray();
        using var reader = new WaveFileReader(new MemoryStream(data));
        Assert.Equal(6, reader.Format.Channels);
        Assert.Equal(0x3F, reader.Format.ChannelMask);
        Assert.Equal("多声道", reader.Metadata.Title);
        Assert.Equal(pcm.Length, (Int32)reader.TotalFrames * reader.Format.BytesPerFrame);
    }
}
