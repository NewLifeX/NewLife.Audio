using System;
using System.IO;
using System.Linq;
using NewLife.Audio.Containers;
using Xunit;

namespace XUnitTest.Containers;

public class OggFileReaderTests
{
    [Fact(DisplayName = "OGG读取器解析OpusHead识别为Opus类型")]
    public void Constructor_OpusHead_RecognizesOpus()
    {
        // 构造一个最小 OGG OpusHead 页
        var ms = new MemoryStream();
        // 第一页: OpusHead
        WriteOggPage(ms, "OpusHead".PadRight(19, '\0').ToCharArray().Select(c => (Byte)c).ToArray(), 0, false);

        ms.Seek(0, SeekOrigin.Begin);
        var reader = new OggFileReader(ms);

        Assert.NotNull(reader.Format);
        Assert.Equal(48000, reader.Format.SampleRate);
    }

    [Fact(DisplayName = "OGG读取器非OGG流抛InvalidDataException")]
    public void Constructor_NonOgg_Throws()
    {
        var data = new Byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var ms = new MemoryStream(data);
        Assert.Throws<InvalidDataException>(() => new OggFileReader(ms));
    }

    [Fact(DisplayName = "OGG读取器空流抛异常")]
    public void Constructor_EmptyStream_Throws()
    {
        var ms = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => new OggFileReader(ms));
    }

    [Fact(DisplayName = "OGG读取器ReadFrame返回数据")]
    public void ReadFrame_ReturnsData()
    {
        var ms = new MemoryStream();
        // 第一页: OpusHead
        WriteOggPage(ms, [(Byte)'O', (Byte)'p', (Byte)'u', (Byte)'s', (Byte)'H', (Byte)'e', (Byte)'a', (Byte)'d', 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0], 0, false);
        // 第二页: audio data
        var audioPayload = new Byte[100];
        for (var i = 0; i < 100; i++) audioPayload[i] = (Byte)(i + 1);
        WriteOggPage(ms, audioPayload, 1920, false);

        ms.Seek(0, SeekOrigin.Begin);
        var reader = new OggFileReader(ms);

        var frame = reader.ReadFrame();
        Assert.NotNull(frame);
    }

    [Fact(DisplayName = "OGG读取器SeekFrame跳转到指定帧")]
    public void SeekFrame_ResetsPosition()
    {
        var ms = new MemoryStream();
        WriteOggPage(ms, [(Byte)'O', (Byte)'p', (Byte)'u', (Byte)'s', (Byte)'H', (Byte)'e', (Byte)'a', (Byte)'d', 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0], 0, false);
        WriteOggPage(ms, [0x01, 0x02, 0x03], 1920, false);

        ms.Seek(0, SeekOrigin.Begin);
        var reader = new OggFileReader(ms);

        reader.SeekFrame(0);
        var frame = reader.ReadFrame();
        Assert.NotNull(frame);
    }

    /// <summary>写入一个 OGG 页</summary>
    private static void WriteOggPage(Stream stream, Byte[] packetData, Int64 granulePosition, Boolean isEos)
    {
        var segments = (packetData.Length + 254) / 255;
        var segmentTableSize = segments;

        // OggS header (27 bytes)
        stream.Write([(Byte)'O', (Byte)'g', (Byte)'g', (Byte)'S'], 0, 4); // magic
        stream.WriteByte(0); // version
        var flags = (Byte)(isEos ? 0x04 : 0x00);
        if (segmentTableSize == 1 && packetData.Length < 255) flags |= 0x01; // first page of stream
        stream.WriteByte(flags);
        var granule = BitConverter.GetBytes(granulePosition);
        stream.Write(granule, 0, 8);
        var serial = BitConverter.GetBytes(123456u);
        stream.Write(serial, 0, 4);
        var pageSeq = BitConverter.GetBytes((UInt32)0);
        stream.Write(pageSeq, 0, 4);
        // CRC (placeholder, 4 bytes of zero)
        stream.Write(new Byte[4], 0, 4);
        // segment table
        stream.WriteByte((Byte)segments);
        for (var i = 0; i < segments; i++)
        {
            var segLen = Math.Min(255, packetData.Length - i * 255);
            stream.WriteByte((Byte)segLen);
        }
        // packet data
        stream.Write(packetData, 0, packetData.Length);
    }
}
