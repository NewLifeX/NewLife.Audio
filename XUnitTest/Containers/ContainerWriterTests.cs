using System;
using System.IO;
using NewLife.Audio;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using NewLife.Data;
using Xunit;

namespace XUnitTest.Containers;

public class RawPcmReaderExtraTests
{
    [Fact(DisplayName = "RawPcmReader顺序读取全部帧后返回null")]
    public void ReadFrame_AllFrames_ThenNull()
    {
        var data = new Byte[320 * 3];
        var reader = new RawPcmReader(new MemoryStream(data), 8000, 16, 1, 20);

        for (var i = 0; i < 3; i++)
            Assert.NotNull(reader.ReadFrame());

        Assert.Null(reader.ReadFrame());
    }

    [Fact(DisplayName = "RawPcmReader SeekFrame定位到指定帧")]
    public void SeekFrame_ReadsFromGivenFrame()
    {
        var data = new Byte[320 * 3];
        data[320] = 0x2A;
        data[640] = 0x5A;
        var reader = new RawPcmReader(new MemoryStream(data), 8000, 16, 1, 20);

        reader.SeekFrame(1);
        var frame = reader.ReadFrame();

        Assert.NotNull(frame);
        Assert.Equal(0x2A, frame.ReadBytes()[0]);
    }

    [Fact(DisplayName = "RawPcmReader按帧数计算总时长")]
    public void Duration_ComputedFromFrames()
    {
        var reader = new RawPcmReader(new MemoryStream(new Byte[320 * 5]), 8000, 16, 1, 20);

        Assert.Equal(5, reader.TotalFrames);
        Assert.Equal(0.1d, reader.Duration, 6);
    }

    [Fact(DisplayName = "RawPcmReader空流总帧数为零")]
    public void EmptyStream_ZeroFrames()
    {
        var reader = new RawPcmReader(new MemoryStream(), 8000, 16, 1, 20);

        Assert.Equal(0, reader.TotalFrames);
        Assert.Equal(0d, reader.Duration);
        Assert.Null(reader.ReadFrame());
    }

    [Fact(DisplayName = "RawPcmReader null流抛ArgumentNullException")]
    public void Constructor_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RawPcmReader(null));
    }

    [Fact(DisplayName = "容器工厂未知格式回退RawPcm且不吞掉流首字节")]
    public void Factory_UnknownFormat_PreservesStreamPosition()
    {
        var data = new Byte[640];
        data[0] = 0x11;
        var ms = new MemoryStream(data);

        var reader = AudioContainerFactory.CreateReader(ms);

        Assert.IsType<RawPcmReader>(reader);
        Assert.Equal(2, reader.TotalFrames);

        var frame = reader.ReadFrame();
        Assert.NotNull(frame);
        Assert.Equal(0x11, frame.ReadBytes()[0]);
    }
}

public class ContainerWriterFactoryTests
{
    [Theory(DisplayName = "容器工厂按扩展名创建写入器")]
    [InlineData(".wav", typeof(WaveFileWriter))]
    [InlineData(".mp3", typeof(Mp3FileWriter))]
    [InlineData(".ogg", typeof(OggFileWriter))]
    [InlineData(".opus", typeof(OggFileWriter))]
    public void CreateWriter_ByExtension_ReturnsExpectedType(String ext, Type expected)
    {
        using var ms = new MemoryStream();
        using var writer = AudioContainerFactory.CreateWriter(ms, ext, AudioFormat.Default);

        Assert.IsType(expected, writer);
    }

    [Fact(DisplayName = "容器工厂不支持的扩展名抛NotSupportedException")]
    public void CreateWriter_Unsupported_Throws()
    {
        using var ms = new MemoryStream();

        Assert.Throws<NotSupportedException>(() => AudioContainerFactory.CreateWriter(ms, ".xyz", AudioFormat.Default));
    }

    [Fact(DisplayName = "Mp3FileWriter透传帧数据并在Flush后保留")]
    public void Mp3FileWriter_WritesFrame()
    {
        using var ms = new MemoryStream();
        var frame = new Byte[] { 0xFF, 0xFB, 0x90, 0x64 };

        using (var writer = new Mp3FileWriter(ms, AudioFormat.Default))
        {
            writer.WriteFrame(frame);
            writer.Flush();
        }

        Assert.Equal(frame, ms.ToArray());
    }

    [Fact(DisplayName = "OggFileWriter写入头页、数据页与EOS页")]
    public void OggFileWriter_WritesPages()
    {
        using var ms = new MemoryStream();

        using (var writer = new OggFileWriter(ms, AudioFormat.Default, AVTypes.Transparent))
        {
            writer.WriteFrame(new Byte[80]);
            writer.Flush();
        }

        var data = ms.ToArray();
        var first = data.AsSpan().IndexOf("OggS"u8);
        var second = first >= 0 ? data.AsSpan(first + 4).IndexOf("OggS"u8) : -1;
        var third = second >= 0 ? data.AsSpan(first + 4 + second + 4).IndexOf("OggS"u8) : -1;

        Assert.True(first >= 0);
        Assert.True(second >= 0);
        Assert.True(third >= 0);
    }
}
