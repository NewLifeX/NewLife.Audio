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

    [Fact(DisplayName = "容器工厂未知格式抛异常且不消费流")]
    public void CreateReader_Unknown_Throws()
    {
        var data = new Byte[20];
        var ms = new MemoryStream(data);

        Assert.Throws<NotSupportedException>(() => AudioContainerFactory.CreateReader(ms));
        Assert.Equal(0, ms.Position);
    }

    [Fact(DisplayName = "容器工厂空流抛异常而不是按裸PCM解码")]
    public void CreateReader_Empty_Throws()
    {
        var ms = new MemoryStream();

        Assert.Throws<NotSupportedException>(() => AudioContainerFactory.CreateReader(ms, ".pcm"));
    }

    [Fact(DisplayName = "容器工厂 null流抛ArgumentNullException")]
    public void CreateReader_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AudioContainerFactory.CreateReader((Stream)null));
    }

    [Fact(DisplayName = "容器工厂从不可定位流识别WAV且不丢文件头")]
    public void CreateReader_NonSeekableWav_KeepsHeader()
    {
        var wav = BuildWav(100);
        using var stream = new NonSeekableReadStream(new MemoryStream(wav));

        var reader = AudioContainerFactory.CreateReader(stream);

        Assert.IsType<WaveFileReader>(reader);
        Assert.Equal(16000, reader.Format.SampleRate);
        var frame = reader.ReadFrame();
        Assert.NotNull(frame);
        Assert.True(frame.Length > 0);
    }

    [Fact(DisplayName = "容器工厂不可定位未知流抛异常")]
    public void CreateReader_NonSeekableUnknown_Throws()
    {
        using var stream = new NonSeekableReadStream(new MemoryStream(new Byte[640]));

        Assert.Throws<NotSupportedException>(() => AudioContainerFactory.CreateReader(stream));
    }

    /// <summary>构造最小有效 WAV 数据</summary>
    /// <param name="dataSize">data 块字节数</param>
    /// <returns>WAV 字节</returns>
    private static Byte[] BuildWav(Int32 dataSize)
    {
        var data = new Byte[44 + dataSize];
        data[0] = (Byte)'R'; data[1] = (Byte)'I'; data[2] = (Byte)'F'; data[3] = (Byte)'F';
        var fileSize = data.Length - 8;
        data[4] = (Byte)(fileSize & 0xFF);
        data[5] = (Byte)((fileSize >> 8) & 0xFF);
        data[6] = (Byte)((fileSize >> 16) & 0xFF);
        data[7] = (Byte)((fileSize >> 24) & 0xFF);
        data[8] = (Byte)'W'; data[9] = (Byte)'A'; data[10] = (Byte)'V'; data[11] = (Byte)'E';
        data[12] = (Byte)'f'; data[13] = (Byte)'m'; data[14] = (Byte)'t'; data[15] = (Byte)' ';
        data[16] = 16;
        data[20] = 1;
        data[22] = 1;
        data[24] = 0x80; data[25] = 0x3E;
        data[34] = 16;
        data[36] = (Byte)'d'; data[37] = (Byte)'a'; data[38] = (Byte)'t'; data[39] = (Byte)'a';
        data[40] = (Byte)(dataSize & 0xFF);
        data[41] = (Byte)((dataSize >> 8) & 0xFF);

        return data;
    }

    /// <summary>不可定位的只读流</summary>
    private sealed class NonSeekableReadStream : Stream
    {
        private readonly Stream _inner;

        public NonSeekableReadStream(Stream inner) => _inner = inner;

        public override Boolean CanRead => true;
        public override Boolean CanSeek => false;
        public override Boolean CanWrite => false;
        public override Int64 Length => throw new NotSupportedException();
        public override Int64 Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Int64 Seek(Int64 offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(Int64 value) => throw new NotSupportedException();
        public override void Write(Byte[] buffer, Int32 offset, Int32 count) => throw new NotSupportedException();
        public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count) => _inner.Read(buffer, offset, count);

        protected override void Dispose(Boolean disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
