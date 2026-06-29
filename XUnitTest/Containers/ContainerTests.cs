using System;
using System.IO;
using NewLife.Audio.Containers;
using Xunit;

namespace XUnitTest.Containers;

public class WaveFileReaderTests
{
    [Fact(DisplayName = "WAV读取器解析有效WAV头")]
    public void Parse_ValidWav_ReturnsFormat()
    {
        // 构建最小有效 WAV 文件 (44 字节头 + 一些数据)
        var ms = new MemoryStream();
        var header = new Byte[44 + 100];
        // RIFF
        header[0] = (Byte)'R'; header[1] = (Byte)'I'; header[2] = (Byte)'F'; header[3] = (Byte)'F';
        var fileSize = 44 + 100 - 8;
        header[4] = (Byte)(fileSize & 0xFF);
        header[5] = (Byte)((fileSize >> 8) & 0xFF);
        header[6] = (Byte)((fileSize >> 16) & 0xFF);
        header[7] = (Byte)((fileSize >> 24) & 0xFF);
        // WAVE
        header[8] = (Byte)'W'; header[9] = (Byte)'A'; header[10] = (Byte)'V'; header[11] = (Byte)'E';
        // fmt
        header[12] = (Byte)'f'; header[13] = (Byte)'m'; header[14] = (Byte)'t'; header[15] = (Byte)' ';
        header[16] = 16; // chunk size
        header[20] = 1; // PCM
        header[22] = 1; // mono
        header[24] = 0x80; header[25] = 0x3E; // 16000 Hz
        header[34] = 16; // 16-bit
        // data
        header[36] = (Byte)'d'; header[37] = (Byte)'a'; header[38] = (Byte)'t'; header[39] = (Byte)'a';
        header[40] = 100; // data size

        ms.Write(header, 0, header.Length);
        ms.Seek(0, SeekOrigin.Begin);

        var reader = new WaveFileReader(ms);
        Assert.Equal(16000, reader.Format.SampleRate);
        Assert.Equal(1, reader.Format.Channels);
        Assert.Equal(16, reader.Format.BitsPerSample);
    }

    [Fact(DisplayName = "WAV读取器非WAV文件抛异常")]
    public void Parse_InvalidData_Throws()
    {
        var ms = new MemoryStream(new Byte[100]);
        Assert.Throws<InvalidDataException>(() => new WaveFileReader(ms));
    }
}

public class RawPcmReaderTests
{
    [Fact(DisplayName = "RawPcmReader按帧长读取PCM数据")]
    public void ReadFrame_ReturnsCorrectData()
    {
        var data = new Byte[160 * 2 * 5]; // 5 frames @8kHz 20ms
        var random = new Random(42);
        random.NextBytes(data);

        var ms = new MemoryStream(data);
        var reader = new RawPcmReader(ms, 8000, 16, 1, 20);

        Assert.Equal(5, reader.TotalFrames);

        var frame = reader.ReadFrame();
        Assert.NotNull(frame);
        Assert.Equal(160 * 2, frame.Total);
    }
}

public class ContainerFactoryTests
{
    [Fact(DisplayName = "容器工厂按魔术字节识别WAV")]
    public void CreateReader_WavMagic_ReturnsWaveReader()
    {
        // 构造完整最小 WAV 头（44字节）
        var wavData = new Byte[44];
        wavData[0] = (Byte)'R'; wavData[1] = (Byte)'I'; wavData[2] = (Byte)'F'; wavData[3] = (Byte)'F';
        wavData[8] = (Byte)'W'; wavData[9] = (Byte)'A'; wavData[10] = (Byte)'V'; wavData[11] = (Byte)'E';
        // fmt chunk
        wavData[12] = (Byte)'f'; wavData[13] = (Byte)'m'; wavData[14] = (Byte)'t'; wavData[15] = (Byte)' ';
        wavData[16] = 16;
        wavData[20] = 1; // PCM
        wavData[22] = 1; // mono
        wavData[24] = 0x80; wavData[25] = 0x3E; // 16000
        wavData[34] = 16;
        // data chunk
        wavData[36] = (Byte)'d'; wavData[37] = (Byte)'a'; wavData[38] = (Byte)'t'; wavData[39] = (Byte)'a';
        wavData[40] = 4;

        var ms = new MemoryStream(wavData);

        var reader = AudioContainerFactory.CreateReader(ms);
        Assert.IsType<WaveFileReader>(reader);
    }

    [Fact(DisplayName = "容器工厂未知格式回退RawPcmReader")]
    public void CreateReader_Unknown_ReturnsRawPcm()
    {
        var data = new Byte[20];
        var ms = new MemoryStream(data);

        var reader = AudioContainerFactory.CreateReader(ms);
        Assert.IsType<RawPcmReader>(reader);
    }
}
