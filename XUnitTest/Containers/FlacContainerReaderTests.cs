using System;
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
}
