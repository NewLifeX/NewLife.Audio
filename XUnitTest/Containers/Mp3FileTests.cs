using System;
using System.IO;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest.Containers;

/// <summary>MP3 文件读写测试</summary>
public class Mp3FileTests
{
    private static String AssetPath(String name) => Path.Combine(AppContext.BaseDirectory, "Assets", "mp3", name);

    [Fact(DisplayName = "MP3 文件读取器解析格式与帧索引")]
    public void Reader_ParsesFormatAndFrames()
    {
        var path = AssetPath("m1-mono-44k-320.mp3");
        using var reader = new Mp3FileReader(File.OpenRead(path));

        Assert.Equal(AVTypes.MP3, reader.CodecType);
        Assert.Equal(44100, reader.Format.SampleRate);
        Assert.Equal(1, reader.Format.Channels);
        Assert.InRange(reader.AverageBitrate, 310000, 325000);
        Assert.True(reader.TotalFrames > 20);
        Assert.InRange(reader.Duration, 0.5, 0.7);

        var total = 0L;
        var frames = 0;
        while (true)
        {
            var packet = reader.ReadFrame();
            if (packet == null) break;

            total += packet.Length;
            frames++;
        }

        Assert.Equal(reader.TotalFrames, frames);
        Assert.Equal(new FileInfo(path).Length, total);
    }

    [Fact(DisplayName = "MP3 文件读取器支持随机定位")]
    public void Reader_SeekFrame()
    {
        using var reader = new Mp3FileReader(File.OpenRead(AssetPath("m1-mono-44k-320.mp3")));
        Assert.True(reader.TotalFrames > 2);

        reader.SeekFrame(1);
        var second = reader.ReadFrame();
        Assert.NotNull(second);

        reader.SeekFrame(0);
        var first = reader.ReadFrame();
        Assert.NotNull(first);
        Assert.True(first.Length > 0);

        reader.SeekFrame(1);
        var again = reader.ReadFrame();
        Assert.Equal(second.Length, again.Length);
        Assert.Equal(second[0], again[0]);
    }

    [Fact(DisplayName = "MP3 文件读取器支持不可寻址流")]
    public void Reader_NonSeekableStream()
    {
        var data = File.ReadAllBytes(AssetPath("m1-mono-44k-320.mp3"));
        using var stream = new NonSeekableStream(new MemoryStream(data));
        using var reader = new Mp3FileReader(stream);

        Assert.Equal(44100, reader.Format.SampleRate);
        Assert.True(reader.TotalFrames > 20);
        Assert.NotNull(reader.ReadFrame());
    }

    [Fact(DisplayName = "MP3 写入器落盘 ID3v2 标签并可回读")]
    public void Writer_WritesId3Tag()
    {
        using var ms = new MemoryStream();
        var format = new AudioFormat { SampleRate = 44100, Channels = 1, BitsPerSample = 16 };

        var metadata = new AudioMetadata
        {
            Title = "设备告警",
            Artist = "新生命",
            Album = "IoT",
            Genre = "Speech",
            Year = 2026,
            TrackNumber = 3,
            Comment = "单元测试",
        };

        var writer = new Mp3FileWriter(ms, format, 128);
        writer.WriteMetadata(metadata);
        writer.WritePcm(BuildTone(44100, 1, 44100, 1000, 0.3));
        writer.Flush();

        ms.Position = 0;
        using var reader = new Mp3FileReader(ms);

        Assert.Equal("设备告警", reader.Metadata.Title);
        Assert.Equal("新生命", reader.Metadata.Artist);
        Assert.Equal("IoT", reader.Metadata.Album);
        Assert.Equal("Speech", reader.Metadata.Genre);
        Assert.Equal(2026, reader.Metadata.Year);
        Assert.Equal(3, reader.Metadata.TrackNumber);
        Assert.Equal("单元测试", reader.Metadata.Comment);
        Assert.True(reader.TotalFrames > 20);
    }

    [Fact(DisplayName = "MP3 写入器追加 ID3v1 尾标签并可回读")]
    public void Writer_WritesId3v1Tag()
    {
        using var ms = new MemoryStream();
        var format = new AudioFormat { SampleRate = 44100, Channels = 1, BitsPerSample = 16 };

        var writer = new Mp3FileWriter(ms, format, 128);
        writer.WriteMetadata(new AudioMetadata
        {
            Title = "Alarm",
            Artist = "NewLife",
            Album = "IoT",
            Genre = "Speech",
            Year = 2026,
            TrackNumber = 7,
            Comment = "v1 tag",
        });
        writer.WritePcm(BuildTone(44100, 1, 22050, 1000, 0.3));
        writer.Flush();

        var data = ms.ToArray();

        // 末尾必须是 128 字节 ID3v1 标签（TAG 起始），且不影响帧索引
        const Int32 v1Size = 128;
        Assert.True(data.Length > v1Size);
        var tailStart = data.Length - v1Size;
        Assert.Equal((Byte)'T', data[tailStart]);
        Assert.Equal((Byte)'A', data[tailStart + 1]);
        Assert.Equal((Byte)'G', data[tailStart + 2]);

        ms.Position = 0;
        using var reader = new Mp3FileReader(ms);
        Assert.True(reader.TotalFrames > 10);
        Assert.Equal(44100, reader.Format.SampleRate);
    }

    [Fact(DisplayName = "MP3 读取器在无 ID3v2 时回退到 ID3v1 标签")]
    public void Reader_FallsBackToId3v1()
    {
        using var ms = new MemoryStream();
        var format = new AudioFormat { SampleRate = 44100, Channels = 1, BitsPerSample = 16 };

        // 无元数据写入 → 无 ID3v2、无 ID3v1
        var writer = new Mp3FileWriter(ms, format, 128);
        writer.WritePcm(BuildTone(44100, 1, 22050, 1000, 0.3));
        writer.Flush();

        var data = ms.ToArray();
        const Int32 v1Size = 128;
        Assert.NotEqual((Byte)'T', data[data.Length - v1Size]);

        // 手工追加一条 ID3v1（Latin-1 固定布局）
        var tag = new Byte[v1Size];
        tag[0] = (Byte)'T';
        tag[1] = (Byte)'A';
        tag[2] = (Byte)'G';
        var title = "V1 Title";
        for (var i = 0; i < title.Length; i++) tag[3 + i] = (Byte)title[i];
        var artist = "V1 Artist";
        for (var i = 0; i < artist.Length; i++) tag[33 + i] = (Byte)artist[i];
        tag[93] = (Byte)'2';
        tag[94] = (Byte)'0';
        tag[95] = (Byte)'1';
        tag[96] = (Byte)'9';
        var comment = "note";
        for (var i = 0; i < comment.Length; i++) tag[97 + i] = (Byte)comment[i];
        tag[125] = 0;
        tag[126] = 9;
        tag[127] = 17; // Rock

        var file = new MemoryStream();
        file.Write(data, 0, data.Length);
        file.Write(tag, 0, tag.Length);
        file.Position = 0;

        using var reader = new Mp3FileReader(file);
        Assert.Equal("V1 Title", reader.Metadata.Title);
        Assert.Equal("V1 Artist", reader.Metadata.Artist);
        Assert.Equal(2019, reader.Metadata.Year);
        Assert.Equal("note", reader.Metadata.Comment);
        Assert.Equal(9, reader.Metadata.TrackNumber);
        Assert.Equal("Rock", reader.Metadata.Genre);
    }

    [Fact(DisplayName = "ID3v2 字段优先于 ID3v1")]
    public void Reader_PrefersId3v2Fields()
    {
        using var ms = new MemoryStream();
        var format = new AudioFormat { SampleRate = 44100, Channels = 1, BitsPerSample = 16 };

        var writer = new Mp3FileWriter(ms, format, 128);
        writer.WriteMetadata(new AudioMetadata { Title = "V2 Title", Artist = "V2 Artist" });
        writer.WritePcm(BuildTone(44100, 1, 22050, 1000, 0.3));
        writer.Flush();

        var data = ms.ToArray();
        const Int32 v1Size = 128;
        var start = data.Length - v1Size;
        Assert.Equal((Byte)'T', data[start]);

        ms.Position = 0;
        using var reader = new Mp3FileReader(ms);
        Assert.Equal("V2 Title", reader.Metadata.Title);
        Assert.Equal("V2 Artist", reader.Metadata.Artist);
    }

    [Fact(DisplayName = "MP3 写入 PCM 后解码可还原信号")]
    public void Writer_WritePcm_RoundTrip()
    {
        var sampleRate = 44100;
        using var ms = new MemoryStream();
        var format = new AudioFormat { SampleRate = sampleRate, Channels = 1, BitsPerSample = 16, Encoding = AVTypes.LPCM };
        var pcm = BuildTone(sampleRate, 1, sampleRate, 1000, 0.3);

        var writer = new Mp3FileWriter(ms, format, 192);
        // 分块写入，验证跨调用的半帧缓冲
        writer.WritePcm(pcm.AsSpan(0, 1000));
        writer.WritePcm(pcm.AsSpan(1000));
        writer.Flush();

        ms.Position = 0;
        using var reader = new Mp3FileReader(ms);
        Assert.Equal(sampleRate, reader.Format.SampleRate);
        Assert.True(reader.TotalFrames > 20);

        var encoded = ms.ToArray();
        var codec = new Mp3Codec();
        var decoded = codec.ToPcm(encoded, null).GetSpan().ToArray();
        Assert.True(decoded.Length >= pcm.Length);

        var snr = BestSnr(ToInt16(pcm), ToInt16(decoded));
        Assert.True(snr > 10, $"写入并解码后的信噪比过低：{snr:F1} dB");
    }

    [Fact(DisplayName = "MP3 写入器透传帧数据")]
    public void Writer_WritesRawFrames()
    {
        var frames = File.ReadAllBytes(AssetPath("m1-mono-44k-320.mp3"));
        using var ms = new MemoryStream();
        var format = new AudioFormat { SampleRate = 44100, Channels = 1, BitsPerSample = 16 };

        var writer = new Mp3FileWriter(ms, format);
        // 逐帧写入
        var offset = 0;
        while (offset + 4 <= frames.Length)
        {
            var header = Mp3Codec.ParseFrameHeader(frames, offset);
            if (header == null) break;

            writer.WriteFrame(frames.AsSpan(offset, header.FrameSize));
            offset += header.FrameSize;
        }
        writer.Flush();

        Assert.Equal(frames.Length, ms.Length);
    }

    [Fact(DisplayName = "容器工厂按扩展名与魔术字节识别 MP3")]
    public void Factory_DetectsMp3()
    {
        var path = AssetPath("m1-mono-44k-320.mp3");

        using (var reader = AudioContainerFactory.CreateReader(path))
        {
            Assert.IsType<Mp3FileReader>(reader);
            Assert.Equal(AVTypes.MP3, reader.CodecType);
        }

        // ID3v2 开头
        var data = File.ReadAllBytes(path);
        var tagged = new Byte[10 + data.Length];
        tagged[0] = (Byte)'I';
        tagged[1] = (Byte)'D';
        tagged[2] = (Byte)'3';
        tagged[3] = 4;
        Buffer.BlockCopy(data, 0, tagged, 10, data.Length);
        using (var reader = AudioContainerFactory.CreateReader(new MemoryStream(tagged)))
        {
            Assert.IsType<Mp3FileReader>(reader);
        }

        // 裸帧同步字开头
        using (var reader = AudioContainerFactory.CreateReader(new MemoryStream(data)))
        {
            Assert.IsType<Mp3FileReader>(reader);
        }
    }

    [Fact(DisplayName = "容器工厂按扩展名创建 MP3 写入器")]
    public void Factory_CreatesMp3Writer()
    {
        using var ms = new MemoryStream();
        using var writer = AudioContainerFactory.CreateWriter(ms, ".mp3", new AudioFormat { SampleRate = 44100, Channels = 1 });

        Assert.IsType<Mp3FileWriter>(writer);
    }

    private static Byte[] BuildTone(Int32 sampleRate, Int32 channels, Int32 samples, Double frequency, Double amplitude)
    {
        var data = new Byte[samples * channels * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (Int16)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * amplitude * 32000);
            for (var ch = 0; ch < channels; ch++)
            {
                var p = (i * channels + ch) * 2;
                data[p] = (Byte)(v & 0xFF);
                data[p + 1] = (Byte)((v >> 8) & 0xFF);
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

    private static Double BestSnr(Int16[] source, Int16[] actual)
    {
        var best = -999.0;
        for (var shift = 0; shift < Math.Min(2000, actual.Length - 2000); shift++)
        {
            var sig = 0.0;
            var dot = 0.0;
            var pow = 0.0;
            var count = Math.Min(source.Length, actual.Length - shift) - shift;
            for (var i = 0; i < count; i++)
            {
                var x = (Double)source[i];
                var y = (Double)actual[i + shift];
                sig += x * x;
                dot += x * y;
                pow += y * y;
            }
            if (sig <= 0 || pow <= 0) continue;

            var gain = dot / pow;
            var error = 0.0;
            for (var i = 0; i < count; i++)
            {
                var e = source[i] - gain * actual[i + shift];
                error += e * e;
            }
            var snr = 10 * Math.Log10(sig / Math.Max(error, 1e-9));
            if (snr > best) best = snr;
        }

        return best;
    }

    /// <summary>不可寻址的只读流包装</summary>
    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override Boolean CanRead => true;
        public override Boolean CanSeek => false;
        public override Boolean CanWrite => false;
        public override Int64 Length => throw new NotSupportedException();
        public override Int64 Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count) => inner.Read(buffer, offset, count);
        public override Int64 Seek(Int64 offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(Int64 value) => throw new NotSupportedException();
        public override void Write(Byte[] buffer, Int32 offset, Int32 count) => throw new NotSupportedException();
    }
}
