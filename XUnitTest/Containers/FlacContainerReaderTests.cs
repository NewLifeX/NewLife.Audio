using System;
using System.Buffers.Binary;
using System.IO;
using NewLife.Audio.Containers;
using Xunit;

namespace XUnitTest.Containers;

public class FlacContainerReaderTests
{
    [Fact(DisplayName = "FLAC容器解析非FLAC文件抛异常")]
    public void FlacContainer_InvalidData_Throws()
    {
        var ms = new MemoryStream([0x00, 0x01, 0x02, 0x03, 0x04]);
        Assert.Throws<InvalidDataException>(() => new FlacContainerReader(ms));
    }

    [Fact(DisplayName = "FLAC容器解析有效fLaC标记不抛异常")]
    public void FlacContainer_ValidMarker_ParsesInfo()
    {
        var ms = new MemoryStream();
        ms.Write([(Byte)'f', (Byte)'L', (Byte)'a', (Byte)'C'], 0, 4);
        ms.WriteByte(0x80);
        ms.WriteByte(0x00); ms.WriteByte(0x00); ms.WriteByte(34);

        var streamInfo = new Byte[34];
        // 填充最小有效数据
        ms.Write(streamInfo, 0, 34);
        ms.Seek(0, SeekOrigin.Begin);

        var reader = new FlacContainerReader(ms);
        Assert.NotNull(reader.Format);
        Assert.True(reader.Format.SampleRate >= 0);
    }

    [Fact(DisplayName = "FLAC容器解析32位深与36位总采样数")]
    public void FlacContainer_HighBitStreamInfo_ParsesCorrectly()
    {
        const UInt32 sampleRate = 44100;
        const Byte channels = 2;
        const Byte bitsPerSample = 32;
        const UInt64 totalSamples = 1UL << 35;

        var streamInfo = new Byte[34];
        // 最小/最大块大小均为 4096
        streamInfo[0] = 0x10; streamInfo[1] = 0x00;
        streamInfo[2] = 0x10; streamInfo[3] = 0x00;
        var packed = ((UInt64)sampleRate << 44) |
                     ((UInt64)(channels - 1) << 41) |
                     ((UInt64)(bitsPerSample - 1) << 36) |
                     totalSamples;
        BinaryPrimitives.WriteUInt64BigEndian(streamInfo.AsSpan(10), packed);

        var ms = new MemoryStream();
        ms.Write([(Byte)'f', (Byte)'L', (Byte)'a', (Byte)'C'], 0, 4);
        ms.WriteByte(0x80);
        ms.WriteByte(0x00); ms.WriteByte(0x00); ms.WriteByte(34);
        ms.Write(streamInfo, 0, 34);
        ms.Seek(0, SeekOrigin.Begin);

        var reader = new FlacContainerReader(ms);
        Assert.Equal(sampleRate, (UInt32)reader.Format.SampleRate);
        Assert.Equal(channels, reader.Format.Channels);
        Assert.Equal(bitsPerSample, reader.Format.BitsPerSample);
        Assert.True(reader.TotalFrames > 0);
        Assert.True(reader.Duration > 0);
    }
}
