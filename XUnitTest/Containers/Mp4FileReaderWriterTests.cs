using System;
using System.IO;
using NewLife.Audio;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest.Containers;

public class Mp4FileReaderWriterTests
{
    [Fact(DisplayName = "MP4写入器创建不抛异常")]
    public void Writer_Constructor_DoesNotThrow()
    {
        var format = new AudioFormat { SampleRate = 44100, Channels = 2, BitsPerSample = 16, Encoding = AVTypes.AAC, SamplesPerFrame = 1024 };
        using var ms = new MemoryStream();
        using var writer = new Mp4FileWriter(ms, format);
        Assert.NotNull(writer);
    }

    [Fact(DisplayName = "MP4写入AAC帧后Flush生成有效文件")]
    public void Writer_WriteFrame_Flush_ProducesValidFile()
    {
        var format = new AudioFormat { SampleRate = 44100, Channels = 2, BitsPerSample = 16, Encoding = AVTypes.AAC, SamplesPerFrame = 1024 };
        using var ms = new MemoryStream();
        using (var writer = new Mp4FileWriter(ms, format))
        {
            // 写入模拟 AAC 帧
            var aacFrame = new Byte[200];
            new Random(42).NextBytes(aacFrame);
            writer.WriteFrame(aacFrame);
            writer.WriteFrame(aacFrame);
            writer.Flush();
        }

        var data = ms.ToArray();
        Assert.True(data.Length > 100);

        // 验证 ftyp box
        Assert.Equal('f', (Char)data[4]);
        Assert.Equal('t', (Char)data[5]);
        Assert.Equal('y', (Char)data[6]);
        Assert.Equal('p', (Char)data[7]);
    }

    [Fact(DisplayName = "MP4写入器Dispose自动Flush")]
    public void Writer_Dispose_AutoFlush()
    {
        var format = new AudioFormat { SampleRate = 44100, Channels = 2, BitsPerSample = 16, Encoding = AVTypes.AAC, SamplesPerFrame = 1024 };
        using var ms = new MemoryStream();
        var writer = new Mp4FileWriter(ms, format);
        writer.WriteFrame(new Byte[200]);
        writer.Dispose(); // should auto-Flush

        Assert.True(ms.Length > 0);
    }

    [Fact(DisplayName = "MP4读取器打开有效流不抛异常")]
    public void Reader_OpenValidStream_DoesNotThrow()
    {
        // 创建最小 MP4 文件
        var format = new AudioFormat { SampleRate = 44100, Channels = 2, BitsPerSample = 16, Encoding = AVTypes.AAC, SamplesPerFrame = 1024 };
        using var ms = new MemoryStream();
        using (var writer = new Mp4FileWriter(ms, format))
        {
            writer.WriteFrame(new Byte[200]);
        }
        ms.Position = 0;

        using var reader = new Mp4FileReader(ms);
        Assert.NotNull(reader);
        Assert.True(reader.TotalFrames > 0);
    }

    [Fact(DisplayName = "MP4读取器读取帧返回数据")]
    public void Reader_ReadFrame_ReturnsData()
    {
        var format = new AudioFormat { SampleRate = 44100, Channels = 2, BitsPerSample = 16, Encoding = AVTypes.AAC, SamplesPerFrame = 1024 };
        using var ms = new MemoryStream();
        var frameData = new Byte[200];
        new Random(42).NextBytes(frameData);

        using (var writer = new Mp4FileWriter(ms, format))
        {
            writer.WriteFrame(frameData);
        }
        ms.Position = 0;

        using var reader = new Mp4FileReader(ms);
        Assert.Equal(1, reader.TotalFrames);

        var frame = reader.ReadFrame();
        Assert.NotNull(frame);
        Assert.Equal(200, frame.Total);
    }

    [Fact(DisplayName = "MP4容器工厂识别mp4扩展名")]
    public void Factory_RecognizeMp4()
    {
        var format = new AudioFormat { SampleRate = 44100, Channels = 2, Encoding = AVTypes.AAC, SamplesPerFrame = 1024 };
        using var ms = new MemoryStream();
        var writer = AudioContainerFactory.CreateWriter(ms, ".m4a", format);
        Assert.IsType<Mp4FileWriter>(writer);
        writer.Dispose();
    }
}
