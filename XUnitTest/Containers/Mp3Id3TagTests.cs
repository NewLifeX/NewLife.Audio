using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest.Containers;

public class Mp3Id3TagTests
{
    [Fact(DisplayName = "解析 ID3v2.2 三字符帧与 PIC 封面")]
    public void Reader_ParsesId3v22()
    {
        var frames = new MemoryStream();
        AddV22Text(frames, "TT2", "v22 标题");
        AddV22Text(frames, "TP1", "v22 作者");
        AddV22Text(frames, "TAL", "v22 专辑");
        AddV22Text(frames, "TYE", "2024");
        AddV22Text(frames, "TRK", "7");
        AddV22Text(frames, "TCO", "Speech");
        AddV22Frame(frames, "COM", [3, (Byte)'e', (Byte)'n', (Byte)'g', 0, .. Encoding.UTF8.GetBytes("v22 备注")]);
        AddV22Frame(frames, "PIC", [0, (Byte)'J', (Byte)'P', (Byte)'G', 3, 0, 1, 2, 3]);

        var data = JoinTag(2, 0, frames.ToArray(), BuildAudio());
        using var reader = new Mp3FileReader(new MemoryStream(data));

        Assert.Equal("v22 标题", reader.Metadata.Title);
        Assert.Equal("v22 作者", reader.Metadata.Artist);
        Assert.Equal("v22 专辑", reader.Metadata.Album);
        Assert.Equal(2024, reader.Metadata.Year);
        Assert.Equal(7, reader.Metadata.TrackNumber);
        Assert.Equal("Speech", reader.Metadata.Genre);
        Assert.Equal("v22 备注", reader.Metadata.Comment);
        Assert.Equal(new Byte[] { 1, 2, 3 }, reader.Metadata.CoverImage);
    }

    [Fact(DisplayName = "解析 ID3v2.3 标签级反同步")]
    public void Reader_DeunsyncsTag()
    {
        var payload = new Byte[] { 0, (Byte)'A', 0xFF, 0, (Byte)'B' };
        var frames = new MemoryStream();
        AddFrame(frames, "TIT2", payload, 0, 0);

        var data = JoinTag(3, 0x80, frames.ToArray(), BuildAudio());
        using var reader = new Mp3FileReader(new MemoryStream(data));

        Assert.Equal("AÿB", reader.Metadata.Title);
    }

    [Fact(DisplayName = "解析 ID3v2.3 zlib 压缩帧")]
    public void Reader_DecompressesFrame()
    {
        Byte[] raw = [3, .. Encoding.UTF8.GetBytes("压缩标题")];
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionMode.Compress, true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        var z = compressed.ToArray();
        var payload = new Byte[4 + z.Length];
        payload[0] = (Byte)(raw.Length >> 24);
        payload[1] = (Byte)(raw.Length >> 16);
        payload[2] = (Byte)(raw.Length >> 8);
        payload[3] = (Byte)raw.Length;
        Array.Copy(z, 0, payload, 4, z.Length);

        var frames = new MemoryStream();
        AddFrame(frames, "TIT2", payload, 0, 0x80);

        var data = JoinTag(3, 0, frames.ToArray(), BuildAudio());
        using var reader = new Mp3FileReader(new MemoryStream(data));

        Assert.Equal("压缩标题", reader.Metadata.Title);
    }

    [Fact(DisplayName = "解析 ID3v2.4 zlib 压缩帧")]
    public void Reader_DecompressesV24Frame()
    {
        Byte[] raw = [3, .. Encoding.UTF8.GetBytes("v24 压缩")];
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionMode.Compress, true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        var z = compressed.ToArray();
        var payload = new Byte[4 + z.Length];
        payload[0] = (Byte)(raw.Length >> 24);
        payload[1] = (Byte)(raw.Length >> 16);
        payload[2] = (Byte)(raw.Length >> 8);
        payload[3] = (Byte)raw.Length;
        Array.Copy(z, 0, payload, 4, z.Length);

        var frames = new MemoryStream();
        AddFrameV24(frames, "TIT2", payload, 0x09);

        var data = JoinTag(4, 0, frames.ToArray(), BuildAudio());
        using var reader = new Mp3FileReader(new MemoryStream(data));

        Assert.Equal("v24 压缩", reader.Metadata.Title);
    }

    [Fact(DisplayName = "标签变小时原地重写且音频偏移不变")]
    public void UpdateMetadata_RewritesTagInPlace()
    {
        var pcm = BuildTone(44100, 1, 4410, 1000, 0.3);
        var path = Path.Combine(Path.GetTempPath(), "nl-audio-id3-" + Guid.NewGuid().ToString("N") + ".mp3");
        try
        {
            AudioFile.WritePcm(path, new AudioFormat { SampleRate = 44100, Channels = 1, BitsPerSample = 16 }, pcm,
                new AudioWriteOptions { Metadata = new AudioMetadata { Title = "旧标题", Artist = "旧作者", Album = "旧专辑" } });

            var before = File.ReadAllBytes(path);
            var oldStart = TagLength(before);
            var beforeEnd = V1Start(before);
            var audioBefore = before[oldStart..beforeEnd];

            AudioFile.UpdateMetadata(path, new AudioMetadata { Title = "新" });

            var after = File.ReadAllBytes(path);
            var newStart = TagLength(after);
            var afterEnd = V1Start(after);

            Assert.Equal(beforeEnd, after.Length);
            Assert.Equal(beforeEnd, afterEnd);
            Assert.Equal(oldStart, newStart);
            Assert.Equal(audioBefore, after[newStart..afterEnd]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static Byte[] BuildAudio()
    {
        var pcm = BuildTone(44100, 1, 4410, 1000, 0.3);
        return new Mp3Codec().FromPcm(pcm, new Mp3EncoderOptions { SampleRate = 44100, Channels = 1, Bitrate = 128 }).GetSpan().ToArray();
    }

    private static Byte[] JoinTag(Int32 version, Int32 flags, Byte[] body, Byte[] audio)
    {
        var tag = new Byte[10 + body.Length];
        tag[0] = (Byte)'I';
        tag[1] = (Byte)'D';
        tag[2] = (Byte)'3';
        tag[3] = (Byte)version;
        tag[4] = 0;
        tag[5] = (Byte)flags;
        tag[6] = (Byte)((body.Length >> 21) & 0x7F);
        tag[7] = (Byte)((body.Length >> 14) & 0x7F);
        tag[8] = (Byte)((body.Length >> 7) & 0x7F);
        tag[9] = (Byte)(body.Length & 0x7F);
        Array.Copy(body, 0, tag, 10, body.Length);

        var result = new Byte[tag.Length + audio.Length];
        Array.Copy(tag, result, tag.Length);
        Array.Copy(audio, 0, result, tag.Length, audio.Length);

        return result;
    }

    private static void AddV22Text(MemoryStream frames, String id, String value)
    {
        var text = Encoding.UTF8.GetBytes(value);
        AddV22Frame(frames, id, [3, .. text]);
    }

    private static void AddV22Frame(MemoryStream frames, String id, Byte[] payload)
    {
        var header = new Byte[6];
        var ascii = Encoding.ASCII.GetBytes(id);
        Array.Copy(ascii, header, 3);
        header[3] = (Byte)(payload.Length >> 16);
        header[4] = (Byte)(payload.Length >> 8);
        header[5] = (Byte)payload.Length;
        frames.Write(header);
        frames.Write(payload);
    }

    private static void AddFrame(MemoryStream frames, String id, Byte[] payload, Int32 flags, Int32 format)
    {
        var header = new Byte[10];
        var ascii = Encoding.ASCII.GetBytes(id);
        Array.Copy(ascii, header, 4);
        header[4] = (Byte)(payload.Length >> 24);
        header[5] = (Byte)(payload.Length >> 16);
        header[6] = (Byte)(payload.Length >> 8);
        header[7] = (Byte)payload.Length;
        header[8] = (Byte)flags;
        header[9] = (Byte)format;
        frames.Write(header);
        frames.Write(payload);
    }

    private static void AddFrameV24(MemoryStream frames, String id, Byte[] payload, Int32 format)
    {
        var header = new Byte[10];
        var ascii = Encoding.ASCII.GetBytes(id);
        Array.Copy(ascii, header, 4);
        var size = payload.Length;
        header[4] = (Byte)((size >> 21) & 0x7F);
        header[5] = (Byte)((size >> 14) & 0x7F);
        header[6] = (Byte)((size >> 7) & 0x7F);
        header[7] = (Byte)(size & 0x7F);
        header[9] = (Byte)format;
        frames.Write(header);
        frames.Write(payload);
    }

    private static Int32 TagLength(Byte[] data)
    {
        if (data.Length < 10 || data[0] != 'I' || data[1] != 'D' || data[2] != '3') return 0;
        var size = (data[6] & 0x7F) << 21 | (data[7] & 0x7F) << 14 | (data[8] & 0x7F) << 7 | (data[9] & 0x7F);
        return 10 + size;
    }

    private static Int32 V1Start(Byte[] data)
    {
        const Int32 v1Size = 128;
        if (data.Length < v1Size) return data.Length;
        var start = data.Length - v1Size;
        return data[start] == 'T' && data[start + 1] == 'A' && data[start + 2] == 'G' ? start : data.Length;
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
}
