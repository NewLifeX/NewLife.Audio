using System;
using System.IO;
using System.Linq;
using NewLife.Audio.Codecs;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest.Containers;

/// <summary>FLAC 容器写入器测试：头部结构、往返解码、标签与流式写入</summary>
public class FlacFileWriterTests
{
    [Fact(DisplayName = "FLAC写入器生成合法文件头与STREAMINFO")]
    public void Writer_WritesValidHeader()
    {
        var format = new AudioFormat { SampleRate = 44100, Channels = 2, BitsPerSample = 16 };
        using var ms = new MemoryStream();
        using (var writer = new FlacFileWriter(ms, format))
        {
            writer.WriteFrame(new Byte[44100 * 4]); // 1 秒静音
            writer.Flush();
        }

        var data = ms.ToArray();
        Assert.Equal("fLaC", System.Text.Encoding.ASCII.GetString(data, 0, 4));

        // STREAMINFO 必须是第一个元数据块
        Assert.Equal(0, data[4] & 0x7F);
        var blockSize = (data[5] << 16) | (data[6] << 8) | data[7];
        Assert.Equal(34, blockSize);

        var rate = (data[18] << 12) | (data[19] << 4) | (data[20] >> 4);
        var channels = ((data[20] & 0x0E) >> 1) + 1;
        var bits = (((data[20] & 0x01) << 4) | (data[21] >> 4)) + 1;
        var total = ((UInt64)(data[21] & 0x0F) << 32) | ((UInt64)data[22] << 24) | ((UInt64)data[23] << 16) | ((UInt64)data[24] << 8) | data[25];

        Assert.Equal(44100, rate);
        Assert.Equal(2, channels);
        Assert.Equal(16, bits);
        Assert.Equal(44100UL, total);
    }

    [Fact(DisplayName = "FLAC写入器单声道往返解码保持一致")]
    public void Writer_Mono_RoundTrip()
    {
        var format = new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16 };
        var pcm = BuildSine(format, 440, 1.0);
        var data = WriteFile(pcm, format);

        var decoded = new FlacCodec().ToPcm(data, null).GetSpan().ToArray();

        Assert.True(decoded.Length >= pcm.Length - 16, $"解码长度不足：{decoded.Length} < {pcm.Length}");
        var snr = Snr(ToInt16(pcm), ToInt16(decoded));
        Assert.True(snr > 60, $"往返信噪比过低：{snr:F1} dB");
    }

    [Fact(DisplayName = "FLAC写入器立体声往返解码保持一致")]
    public void Writer_Stereo_RoundTrip()
    {
        var format = new AudioFormat { SampleRate = 44100, Channels = 2, BitsPerSample = 16 };
        var pcm = BuildSine(format, 1000, 0.6);
        var data = WriteFile(pcm, format);

        var decoded = new FlacCodec().ToPcm(data, null).GetSpan().ToArray();

        var snr = Snr(ToInt16(pcm), ToInt16(decoded));
        Assert.True(snr > 60, $"立体声往返信噪比过低：{snr:F1} dB");
    }

    [Fact(DisplayName = "FLAC写入器verbatim级别同样可解码")]
    public void Writer_Verbatim_RoundTrip()
    {
        var format = new AudioFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 };
        var pcm = BuildSine(format, 300, 0.5);

        var ms = new MemoryStream();
        using (var writer = new FlacFileWriter(ms, format, new FlacWriterOptions { CompressionLevel = 0, PaddingBytes = 0 }))
        {
            writer.WriteFrame(pcm);
            writer.Flush();
        }

        var decoded = new FlacCodec().ToPcm(ms.ToArray(), null).GetSpan().ToArray();
        var snr = Snr(ToInt16(pcm), ToInt16(decoded));
        Assert.True(snr > 60, $"verbatim 往返信噪比过低：{snr:F1} dB");
    }

    [Fact(DisplayName = "FLAC写入器元数据可被读取器解析")]
    public void Writer_Metadata_RoundTrip()
    {
        var format = new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16 };
        var pcm = BuildSine(format, 500, 0.3);

        var ms = new MemoryStream();
        using (var writer = new FlacFileWriter(ms, format))
        {
            writer.WriteMetadata(new AudioMetadata
            {
                Title = "测试标题",
                Artist = "新生命",
                Album = "专辑",
                Genre = "Speech",
                TrackNumber = 3,
                Year = 2026,
                Comment = "单元测试",
            });
            writer.WriteFrame(pcm);
            writer.Flush();
        }

        ms.Position = 0;
        using var reader = new FlacContainerReader(ms);

        Assert.Equal("测试标题", reader.Metadata.Title);
        Assert.Equal("新生命", reader.Metadata.Artist);
        Assert.Equal("专辑", reader.Metadata.Album);
        Assert.Equal("Speech", reader.Metadata.Genre);
        Assert.Equal(3, reader.Metadata.TrackNumber);
        Assert.Equal(2026, reader.Metadata.Year);
        Assert.Equal("单元测试", reader.Metadata.Comment);
        Assert.Equal(8000, reader.Format.SampleRate);
    }

    [Fact(DisplayName = "FLAC写入器分块写入与帧数正确")]
    public void Writer_ChunkedWrite_FrameCount()
    {
        var format = new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16 };
        var pcm = BuildSine(format, 200, 1.0);
        var blockSize = 1000;
        var samples = pcm.Length / 2;

        var ms = new MemoryStream();
        using (var writer = new FlacFileWriter(ms, format, new FlacWriterOptions { BlockSize = blockSize, PaddingBytes = 0 }))
        {
            for (var offset = 0; offset < pcm.Length; offset += 300)
            {
                var count = Math.Min(300, pcm.Length - offset);
                writer.WriteFrame(pcm.AsSpan(offset, count));
            }
            writer.Flush();
        }

        ms.Position = 0;
        using var reader = new FlacContainerReader(ms);
        var frames = 0;
        while (reader.ReadFrame() != null) frames++;

        Assert.Equal((samples + blockSize - 1) / blockSize, frames);
    }

    [Fact(DisplayName = "FLAC写入器不可定位流也能完成写入")]
    public void Writer_NonSeekable_Completes()
    {
        var format = new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16 };
        var pcm = BuildSine(format, 400, 0.5);

        using var sink = new NonSeekableWriteStream();
        using (var writer = new FlacFileWriter(sink, format))
        {
            writer.WriteFrame(pcm);
            writer.Flush();
        }

        Assert.True(sink.Length > 0);
        Assert.Equal("fLaC", System.Text.Encoding.ASCII.GetString(sink.Data, 0, 4));
    }

    [Fact(DisplayName = "FLAC写入器空流转储后仍是合法文件头")]
    public void Writer_EmptyStream_ValidHeader()
    {
        var format = new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16 };
        using var ms = new MemoryStream();
        using (var writer = new FlacFileWriter(ms, format, new FlacWriterOptions { PaddingBytes = 0 })) { }

        var data = ms.ToArray();
        Assert.Equal("fLaC", System.Text.Encoding.ASCII.GetString(data, 0, 4));
        Assert.True(data.Length >= 8 + 34);
    }

    [Fact(DisplayName = "FLAC写入器拒绝非法格式与晚到元数据")]
    public void Writer_RejectsInvalidUsage()
    {
        var format = new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 24 };
        Assert.Throws<NotSupportedException>(() => new FlacFileWriter(new MemoryStream(), format));

        var ok = new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16 };
        using var ms = new MemoryStream();
        using var writer = new FlacFileWriter(ms, ok);
        writer.WriteFrame(new Byte[320]);
        Assert.Throws<InvalidOperationException>(() => writer.WriteMetadata(new AudioMetadata { Title = "x" }));
    }

    [Fact(DisplayName = "FLAC帧残差按规范写入编码方法与分区阶")]
    public void Writer_ResidualHeader_FollowsSpec()
    {
        var format = new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16 };
        var pcm = BuildSine(format, 440, 0.01);

        Byte[] data;
        using (var ms = new MemoryStream())
        {
            using (var writer = new FlacFileWriter(ms, format, new FlacWriterOptions { PaddingBytes = 0, WriteSeekTable = false }))
            {
                writer.WriteFrame(pcm);
                writer.Flush();
            }
            data = ms.ToArray();
        }

        // 跳过元数据块，定位首个音频帧
        var pos = 4;
        while (true)
        {
            var header = data[pos];
            var length = data[pos + 1] << 16 | data[pos + 2] << 8 | data[pos + 3];
            pos += 4 + length;
            if ((header & 0x80) != 0) break;
        }

        var reader = new BitReader(data, pos * 8);
        Assert.Equal(0x3FFE, reader.Read(14));  // 同步码
        reader.Read(1);                         // reserved
        reader.Read(1);                         // blocking strategy
        var blockCode = reader.Read(4);
        var rateCode = reader.Read(4);
        reader.Read(4);                         // 声道分配
        reader.Read(3);                         // 样本位深
        reader.Read(1);                         // reserved

        // UTF-8 编码的帧号
        var first = reader.Read(8);
        if ((first & 0x80) != 0)
        {
            var count = 0;
            for (var mask = 0x80; (first & mask) != 0; mask >>= 1) count++;
            for (var i = 1; i < count; i++) reader.Read(8);
        }

        if (blockCode == 6) reader.Read(8);
        else if (blockCode == 7) reader.Read(16);
        if (rateCode == 12) reader.Read(8);
        else if (rateCode is 13 or 14) reader.Read(16);

        reader.Read(8);                         // 帧头 CRC-8

        Assert.Equal(0, reader.Read(1));        // 子帧零位
        var type = reader.Read(6);
        Assert.InRange(type, 8, 12);            // 固定预测子帧
        reader.Read(1);                         // wasted bits 标志

        var order = type - 8;
        for (var i = 0; i < order; i++) reader.Read(16);

        // 曾漏写分区阶：只写 2 位方法就紧跟 Rice 参数，VLC/libFLAC 无法解析整帧
        Assert.Equal(0, reader.Read(2));        // 残差编码方法：分区 Rice
        Assert.Equal(0, reader.Read(4));        // 分区阶
        Assert.InRange(reader.Read(4), 0, 14);  // Rice 参数（15 为逃逸）
    }

    #region 辅助
    /// <summary>按位读取 FLAC 帧头的辅助器（高位在前）</summary>
    private sealed class BitReader(Byte[] data, Int32 bitOffset)
    {
        private Int32 _pos = bitOffset;

        public Int32 Read(Int32 count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                value = (value << 1) | ((data[_pos >> 3] >> (7 - (_pos & 7))) & 1);
                _pos++;
            }

            return value;
        }
    }

    private static Byte[] WriteFile(Byte[] pcm, AudioFormat format)
    {
        var ms = new MemoryStream();
        using (var writer = new FlacFileWriter(ms, format))
        {
            writer.WriteFrame(pcm);
            writer.Flush();
        }

        return ms.ToArray();
    }

    private static Byte[] BuildSine(AudioFormat format, Double frequency, Double seconds)
    {
        var samples = (Int32)(format.SampleRate * seconds);
        var data = new Byte[samples * format.Channels * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (Int16)(Math.Sin(2 * Math.PI * frequency * i / format.SampleRate) * 0.5 * 32767);
            for (var ch = 0; ch < format.Channels; ch++)
            {
                var offset = (i * format.Channels + ch) * 2;
                data[offset] = (Byte)(v & 0xFF);
                data[offset + 1] = (Byte)((v >> 8) & 0xFF);
            }
        }

        return data;
    }

    private static Int16[] ToInt16(Byte[] data)
    {
        var pcm = new Int16[data.Length / 2];
        for (var i = 0; i < pcm.Length; i++) pcm[i] = (Int16)(data[i * 2] | data[i * 2 + 1] << 8);

        return pcm;
    }

    private static Double Snr(Int16[] source, Int16[] actual)
    {
        var count = Math.Min(source.Length, actual.Length);
        double signal = 0;
        double error = 0;
        for (var i = 0; i < count; i++)
        {
            double s = source[i];
            double d = actual[i] - s;
            signal += s * s;
            error += d * d;
        }

        return 10 * Math.Log10(signal / Math.Max(error, 1e-9));
    }

    private sealed class NonSeekableWriteStream : Stream
    {
        private readonly MemoryStream _inner = new();

        public Byte[] Data => _inner.ToArray();

        public override Boolean CanRead => false;
        public override Boolean CanSeek => false;
        public override Boolean CanWrite => true;
        public override Int64 Length => _inner.Length;
        public override Int64 Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count) => throw new NotSupportedException();
        public override Int64 Seek(Int64 offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(Int64 value) => throw new NotSupportedException();
        public override void Write(Byte[] buffer, Int32 offset, Int32 count) => _inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<Byte> buffer) => _inner.Write(buffer);
    }
    #endregion
}
