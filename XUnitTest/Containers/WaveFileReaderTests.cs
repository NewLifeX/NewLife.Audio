using System;
using System.IO;
using System.Linq;
using System.Text;
using NewLife.Audio;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using NewLife.Data;
using Xunit;

namespace XUnitTest.Containers;

/// <summary>WAV RIFF 块解析测试。覆盖扩展格式、前置块、RF64 与流式数据</summary>
public class WaveRiffReaderTests
{
    [Fact(DisplayName = "标准44字节WAV头解析正确")]
    public void Reader_StandardPcm_ParsesHeaderAndData()
    {
        var format = new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16 };
        var data = new Byte[320 * 10]; // 10 帧
        var wav = BuildWav("RIFF", Chunk("fmt ", Fmt(16, 1, 1, 8000, 16)), Chunk("data", data));

        using var reader = new WaveFileReader(new MemoryStream(wav));

        Assert.Equal(8000, reader.Format.SampleRate);
        Assert.Equal(1, reader.Format.Channels);
        Assert.Equal(16, reader.Format.BitsPerSample);
        Assert.False(reader.Format.IsFloat);
        Assert.Equal(10L, reader.TotalFrames);
    }

    [Fact(DisplayName = "18字节fmt块解析正确")]
    public void Reader_Fmt18_Parses()
    {
        var data = new Byte[320];
        var wav = BuildWav("RIFF", Chunk("fmt ", Fmt(18, 1, 1, 8000, 16)), Chunk("data", data));

        using var reader = new WaveFileReader(new MemoryStream(wav));

        Assert.Equal(8000, reader.Format.SampleRate);
        Assert.Equal(16, reader.Format.BitsPerSample);
    }

    [Fact(DisplayName = "EXTENSIBLE 24位立体声解析正确")]
    public void Reader_Extensible24Bit_Parses()
    {
        var blockAlign = 2 * 3;
        var data = new Byte[blockAlign * 48000];
        var wav = BuildWav("RIFF", Chunk("fmt ", Fmt(40, 0xFFFE, 2, 48000, 24, 1)), Chunk("data", data));

        using var reader = new WaveFileReader(new MemoryStream(wav));

        Assert.Equal(48000, reader.Format.SampleRate);
        Assert.Equal(2, reader.Format.Channels);
        Assert.Equal(24, reader.Format.BitsPerSample);
        Assert.Equal(AVTypes.LPCM, reader.Format.Encoding);
        Assert.False(reader.Format.IsFloat);
        Assert.Equal(50L, reader.TotalFrames);
    }

    [Fact(DisplayName = "data前存在LIST块与奇数长度块时仍能定位数据")]
    public void Reader_PrecedingChunks_LocatesData()
    {
        var fmt = Fmt(16, 1, 2, 44100, 16);
        var list = new Byte[] { 1, 2, 3, 4, 5 }; // 奇数长度，需要 1 字节对齐填充
        var data = new Byte[44100 * 4 / 50];
        var wav = BuildWav("RIFF", Chunk("fmt ", fmt), Chunk("LIST", list), Chunk("fact", new Byte[4]), Chunk("data", data));

        using var reader = new WaveFileReader(new MemoryStream(wav));

        Assert.Equal(44100, reader.Format.SampleRate);
        Assert.Equal(2, reader.Format.Channels);
        Assert.Equal(data.Length, (Int32)(reader.TotalFrames * reader.Format.BytesPerFrame));
    }

    [Fact(DisplayName = "IEEE浮点WAV标记为浮点格式")]
    public void Reader_Float32_SetsIsFloat()
    {
        var data = new Byte[4 * 2 * 1000];
        var wav = BuildWav("RIFF", Chunk("fmt ", Fmt(16, 3, 2, 48000, 32)), Chunk("data", data));

        using var reader = new WaveFileReader(new MemoryStream(wav));

        Assert.True(reader.Format.IsFloat);
        Assert.Equal(32, reader.Format.BitsPerSample);
    }

    [Fact(DisplayName = "G711 A律WAV映射为G711A编码")]
    public void Reader_Alaw_MapsEncoding()
    {
        var data = new Byte[8000];
        var wav = BuildWav("RIFF", Chunk("fmt ", Fmt(18, 6, 1, 8000, 8)), Chunk("data", data));

        using var reader = new WaveFileReader(new MemoryStream(wav));

        Assert.Equal(AVTypes.G711A, reader.Format.Encoding);
        Assert.Equal(AVTypes.G711A, reader.CodecType);
    }

    [Fact(DisplayName = "RF64用ds64中的64位数据长度")]
    public void Reader_Rf64_UsesDs64Size()
    {
        var fmt = Fmt(16, 1, 2, 48000, 16);
        var data = new Byte[48000 * 4]; // 1 秒
        var ds64 = new Byte[28];
        BitConverter.GetBytes((UInt64)data.Length).CopyTo(ds64, 8);
        BitConverter.GetBytes((UInt64)(data.Length / 4)).CopyTo(ds64, 16);

        var wav = BuildWav("RF64", Chunk("ds64", ds64), Chunk("fmt ", fmt), Chunk("data", data, 0xFFFFFFFFu));

        using var reader = new WaveFileReader(new MemoryStream(wav));

        Assert.Equal(48000, reader.Format.SampleRate);
        Assert.Equal((Int64)(data.Length / reader.Format.BytesPerFrame), reader.TotalFrames);
    }

    [Fact(DisplayName = "流式WAV长度未知时读取到流末尾")]
    public void Reader_StreamingUnknownLength_ReadsToEnd()
    {
        var fmt = Fmt(16, 1, 1, 8000, 16);
        var data = new Byte[320 * 3];
        var wav = BuildWav("RIFF", Chunk("fmt ", fmt), Chunk("data", data, 0));

        using var reader = new WaveFileReader(new NonSeekableStream(wav));

        var frames = 0;
        while (reader.ReadFrame() != null) frames++;

        Assert.Equal(3, frames);
    }

    [Fact(DisplayName = "非法WAV头抛出InvalidDataException")]
    public void Reader_InvalidHeader_Throws()
    {
        Assert.Throws<InvalidDataException>(() => new WaveFileReader(new MemoryStream(new Byte[64])));
        Assert.Throws<InvalidDataException>(() => new WaveFileReader(new MemoryStream(Encoding.ASCII.GetBytes("RIFFxxxxJUNK"))));
        Assert.Throws<ArgumentNullException>(() => new WaveFileReader(null));
    }

    [Fact(DisplayName = "缺少fmt块时解析失败")]
    public void Reader_MissingFmt_Throws()
    {
        var wav = BuildWav("RIFF", Chunk("data", new Byte[320]));

        Assert.Throws<InvalidDataException>(() => new WaveFileReader(new MemoryStream(wav)));
    }

    [Fact(DisplayName = "SeekFrame定位到指定帧后读取正确")]
    public void Reader_SeekFrame_ReadsCorrectly()
    {
        var fmt = Fmt(16, 1, 1, 8000, 16);
        var data = new Byte[320 * 5];
        for (var i = 0; i < data.Length; i++) data[i] = (Byte)i;

        var wav = BuildWav("RIFF", Chunk("fmt ", fmt), Chunk("data", data));

        using var reader = new WaveFileReader(new MemoryStream(wav));
        reader.SeekFrame(3);

        var frame = reader.ReadFrame();
        Assert.NotNull(frame);
        Assert.Equal(320, frame.Total);
        Assert.Equal(unchecked((Byte)(3 * 320)), frame.ToArray()[0]);
    }

    #region 辅助
    private static Byte[] BuildWav(String magic, params Byte[][] chunks)
    {
        var body = chunks.SelectMany(e => e).ToArray();
        var bytes = new Byte[12 + body.Length];
        Encoding.ASCII.GetBytes(magic).CopyTo(bytes, 0);
        BitConverter.GetBytes(body.Length + 4).CopyTo(bytes, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(bytes, 8);
        body.CopyTo(bytes, 12);
        return bytes;
    }

    private static Byte[] Chunk(String id, Byte[] payload, UInt32? size = null)
    {
        var padding = payload.Length % 2;
        var bytes = new Byte[8 + payload.Length + padding];
        Encoding.ASCII.GetBytes(id).CopyTo(bytes, 0);
        BitConverter.GetBytes(size ?? (UInt32)payload.Length).CopyTo(bytes, 4);
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    private static Byte[] Fmt(Int32 size, UInt16 tag, Int32 channels, Int32 rate, Int32 bits, UInt16 subFormat = 0)
    {
        var fmt = new Byte[size];
        BitConverter.GetBytes(tag).CopyTo(fmt, 0);
        BitConverter.GetBytes((Int16)channels).CopyTo(fmt, 2);
        BitConverter.GetBytes(rate).CopyTo(fmt, 4);
        BitConverter.GetBytes(rate * channels * bits / 8).CopyTo(fmt, 8);
        BitConverter.GetBytes((Int16)(channels * bits / 8)).CopyTo(fmt, 12);
        BitConverter.GetBytes((Int16)bits).CopyTo(fmt, 14);
        if (size >= 18) BitConverter.GetBytes((Int16)(size - 18)).CopyTo(fmt, 16);
        if (size >= 40)
        {
            BitConverter.GetBytes((Int16)bits).CopyTo(fmt, 18);
            BitConverter.GetBytes(channels == 2 ? 3 : 4).CopyTo(fmt, 20);
            BitConverter.GetBytes((Int32)subFormat).CopyTo(fmt, 24);
        }
        return fmt;
    }

    private sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;
        public NonSeekableStream(Byte[] data) => _inner = new MemoryStream(data);

        public override Boolean CanRead => true;
        public override Boolean CanSeek => false;
        public override Boolean CanWrite => false;
        public override Int64 Length => throw new NotSupportedException();
        public override Int64 Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count) => _inner.Read(buffer, offset, count);
        public override Int64 Seek(Int64 offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(Int64 value) => throw new NotSupportedException();
        public override void Write(Byte[] buffer, Int32 offset, Int32 count) => throw new NotSupportedException();
    }
    #endregion
}
