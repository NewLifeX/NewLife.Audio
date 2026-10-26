using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using NewLife.Audio.Writers;
using NewLife.Data;
using Xunit;

namespace XUnitTest.Containers;

public class OggOpusTests
{
    [Fact(DisplayName = "OGG Opus流写入器往返保留包边界")]
    public async Task StreamWriter_RoundTrip()
    {
        if (!OpusProcessCodec.IsAvailable) return;

        using var encoder = new OpusCodec();
        var packets = encoder.Encode(CreateSine(960 * 3));

        using var ms = new MemoryStream();
        var writer = new OggOpusStreamWriter("NewLife.Test", 1, 48000, 312);
        await writer.WriteHeaderAsync(ms, TestContext.Current.CancellationToken);
        foreach (var packet in packets)
            await writer.WriteChunkAsync(ms, packet.GetMemory(), TestContext.Current.CancellationToken);
        await writer.WriteTrailerAsync(ms, TestContext.Current.CancellationToken);

        ms.Position = 0;
        var reader = new OggFileReader(ms) { ValidateCrc = true };
        reader.SeekFrame(0);

        Assert.Equal(AVTypes.Opus, reader.CodecType);
        Assert.Equal(1, reader.Format.Channels);
        Assert.Equal(312, reader.PreSkip);
        Assert.Equal("NewLife.Test", reader.Vendor);
        Assert.Equal(3, reader.TotalFrames);
        Assert.Equal(2880, reader.TotalSamples);
        Assert.Equal(0.06, reader.Duration, 3);

        using var decoder = new OpusCodec();
        var count = 0;
        var decodedBytes = 0;
        IPacket frame;
        while ((frame = reader.ReadFrame()) != null)
        {
            var decoded = decoder.ToPcm(frame.GetSpan(), null);
            decodedBytes += decoded.Total;
            count++;
        }

        Assert.Equal(3, count);
        Assert.Equal(960 * 3 * 2, decodedBytes);
    }

    [Fact(DisplayName = "OGG Opus文件写入器写入双声道与元数据")]
    public void FileWriter_StereoAndMetadata()
    {
        var format = new AudioFormat { SampleRate = 48000, Channels = 2, BitsPerSample = 16, Encoding = AVTypes.Opus };

        using var ms = new MemoryStream();
        using (var writer = new OggFileWriter(ms, format, AVTypes.Opus) { PreSkip = 312 })
        {
            writer.WriteMetadata(new AudioMetadata { Title = "Title", Artist = "Artist", Album = "Album" });

            // config 15 + stereo：Hybrid Fullband 20 ms，避免测试依赖外部 ffmpeg
            var packet = new Byte[41];
            packet[0] = 0x7C;
            writer.WriteFrame(packet);
            writer.Flush();
        }

        var data = ms.ToArray();
        using var input = new MemoryStream(data);
        var reader = new OggFileReader(input) { ValidateCrc = true };
        reader.SeekFrame(0);

        Assert.Equal(2, reader.Format.Channels);
        Assert.Equal(312, reader.PreSkip);
        Assert.Equal("Title", reader.Metadata.Title);
        Assert.Equal("Artist", reader.Metadata.Artist);
        Assert.Equal("Album", reader.Metadata.Album);
        Assert.NotNull(reader.ReadFrame());
    }

    [Fact(DisplayName = "OGG Opus写入器255字节包正确终止lacing")]
    public async Task StreamWriter_255BytePacket_EndsLacing()
    {
        using var ms = new MemoryStream();
        var writer = new OggOpusStreamWriter("NewLife.Test", 1, 48000, 0);
        await writer.WriteHeaderAsync(ms, TestContext.Current.CancellationToken);
        await writer.WriteChunkAsync(ms, new Byte[255], TestContext.Current.CancellationToken);
        await writer.WriteChunkAsync(ms, new Byte[80], TestContext.Current.CancellationToken);
        await writer.WriteTrailerAsync(ms, TestContext.Current.CancellationToken);

        ms.Position = 0;
        var reader = new OggFileReader(ms);
        var first = reader.ReadFrame();
        var second = reader.ReadFrame();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(255, first.Total);
        Assert.Equal(80, second.Total);
    }

    [Fact(DisplayName = "OGG Opus读取器重组同一页多lacing包")]
    public void Reader_ReassemblesMultiLacingPacket()
    {
        var payload = new Byte[600];
        for (var i = 0; i < payload.Length; i++) payload[i] = (Byte)(i & 0xFF);

        using var ms = new MemoryStream();
        WriteOggPage(ms, [(Byte)'O', (Byte)'p', (Byte)'u', (Byte)'s', (Byte)'H', (Byte)'e', (Byte)'a', (Byte)'d', 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0], 0, 0x02);
        WriteOggPage(ms, OpusTags(), 0, 0x00);
        WriteOggPage(ms, payload, 1920, 0x04);

        ms.Position = 0;
        var reader = new OggFileReader(ms);
        var frame = reader.ReadFrame();

        Assert.NotNull(frame);
        Assert.Equal(payload.Length, frame.Total);
        for (var i = 0; i < payload.Length; i++)
            Assert.Equal(payload[i], frame[i]);
    }

    [Fact(DisplayName = "OGG Opus读取器首帧granule包含pre-skip")]
    public void FileWriter_FirstGranuleIncludesPreSkip()
    {
        var format = new AudioFormat { SampleRate = 48000, Channels = 1, BitsPerSample = 16, Encoding = AVTypes.Opus };

        using var ms = new MemoryStream();
        using (var writer = new OggFileWriter(ms, format, AVTypes.Opus) { PreSkip = 312 })
        {
            // config 15：Hybrid Fullband 20 ms（960 样本），避免测试依赖外部 ffmpeg
            var packet = new Byte[41];
            packet[0] = 0x78;
            writer.WriteFrame(packet);
            writer.Flush();
        }

        var data = ms.ToArray();
        var pages = FindPageOffsets(data);
        Assert.True(pages.Count >= 3);

        var audioPage = pages[2];
        var granule = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(audioPage + 6));
        Assert.Equal(312 + 960, granule);

        // EOS 空页必须使用 0 个段，避免解码器把空包当成非法 Opus 包
        var eosPage = pages[^1];
        Assert.Equal(0, data[eosPage + 26]);
    }

    private static Byte[] OpusTags()
    {
        var vendor = Encoding.UTF8.GetBytes("Test");
        var packet = new Byte[8 + 4 + vendor.Length + 4];
        Encoding.ASCII.GetBytes("OpusTags").CopyTo(packet, 0);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), vendor.Length);
        vendor.CopyTo(packet, 12);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12 + vendor.Length), 0);
        return packet;
    }

    private static void WriteOggPage(Stream stream, Byte[] payload, Int64 granule, Byte flags)
    {
        var fullSegments = payload.Length / 255;
        var last = payload.Length % 255;
        var segmentCount = fullSegments + 1;

        stream.Write([(Byte)'O', (Byte)'g', (Byte)'g', (Byte)'S'], 0, 4);
        stream.WriteByte(0);
        stream.WriteByte(flags);
        var buffer = new Byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, granule);
        stream.Write(buffer, 0, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 123456u);
        stream.Write(buffer, 0, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0u);
        stream.Write(buffer, 0, 4);
        stream.Write(new Byte[4], 0, 4);
        stream.WriteByte((Byte)segmentCount);
        for (var i = 0; i < fullSegments; i++) stream.WriteByte(255);
        stream.WriteByte((Byte)last);
        stream.Write(payload, 0, payload.Length);
    }

    private static List<Int32> FindPageOffsets(Byte[] data)
    {
        var offsets = new List<Int32>();
        for (var i = 0; i + 4 <= data.Length; i++)
        {
            if (data[i] == 'O' && data[i + 1] == 'g' && data[i + 2] == 'g' && data[i + 3] == 'S')
                offsets.Add(i);
        }
        return offsets;
    }

    private static Byte[] CreateSine(Int32 samples, Int32 sampleRate = 48000, Int32 channels = 1)
    {
        var pcm = new Byte[samples * channels * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (Int16)(Math.Sin(2 * Math.PI * 1000 * i / sampleRate) * 16000);
            for (var ch = 0; ch < channels; ch++)
            {
                pcm[(i * channels + ch) * 2] = (Byte)(value & 0xFF);
                pcm[(i * channels + ch) * 2 + 1] = (Byte)((value >> 8) & 0xFF);
            }
        }
        return pcm;
    }
}