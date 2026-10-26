using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest;

/// <summary>音频文件门面测试：解码链、写入链与格式转换</summary>
public class AudioFileTests
{
    private static readonly AudioFormat Format = new() { SampleRate = 16000, Channels = 1, BitsPerSample = 16 };

    [Fact(DisplayName = "WAV写入后经门面读取数据一致")]
    public void Wav_RoundTrip()
    {
        var pcm = BuildSine(Format, 0.5, 0.5);
        var path = TempPath(".wav");
        try
        {
            AudioFile.WritePcm(path, Format, pcm);

            var info = AudioFile.Detect(path);
            Assert.Equal(AudioContainerKind.Wav, info.Container);

            var audio = AudioFile.ReadPcm(path);
            Assert.Equal(16000, audio.Format.SampleRate);
            Assert.Equal(1, audio.Format.Channels);
            Assert.Equal(pcm.Length, audio.Data.Length);
            Assert.Equal(pcm, audio.Data);
            Assert.True(audio.Duration > 0.45);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "FLAC写入后经门面读取信噪比达标")]
    public void Flac_RoundTrip()
    {
        var pcm = BuildSine(Format, 0.5, 0.7);
        var path = TempPath(".flac");
        try
        {
            AudioFile.WritePcm(path, new AudioPcm { Format = Format, Data = pcm }, new AudioWriteOptions
            {
                Metadata = new AudioMetadata { Title = "门面测试" },
            });

            var info = AudioFile.Detect(path);
            Assert.Equal(AudioContainerKind.Flac, info.Container);
            Assert.Equal("门面测试", info.Metadata.Title);

            var audio = AudioFile.ReadPcm(path);
            Assert.True(Snr(pcm, audio.Data) > 60);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "MP3写入后经门面读取可还原波形")]
    public void Mp3_RoundTrip()
    {
        var pcm = BuildSine(Format, 0.5, 1.0);
        var path = TempPath(".mp3");
        try
        {
            AudioFile.WritePcm(path, Format, pcm, new AudioWriteOptions { Mp3BitrateKbps = 128 });

            var info = AudioFile.Detect(path);
            Assert.Equal(AudioContainerKind.Mp3, info.Container);

            var audio = AudioFile.ReadPcm(path);
            Assert.Equal(AVTypes.MP3, audio.Codec);
            Assert.True(Snr(pcm, audio.Data) > 10);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "SaveAs按扩展名转换容器")]
    public void SaveAs_ConvertsContainer()
    {
        var pcm = BuildSine(Format, 0.5, 0.6);
        var wav = TempPath(".wav");
        var flac = TempPath(".flac");
        try
        {
            AudioFile.WritePcm(wav, Format, pcm);

            var file = AudioFile.Open(wav);
            file.SaveAs(flac, new AudioWriteOptions { Metadata = new AudioMetadata { Title = "转换" } });

            var converted = AudioFile.ReadPcm(flac);
            Assert.True(Snr(pcm, converted.Data) > 60);
            Assert.Equal("转换", AudioFile.Detect(flac).Metadata.Title);
        }
        finally
        {
            TryDelete(wav);
            TryDelete(flac);
        }
    }

    [Fact(DisplayName = "不支持的目标扩展名明确报错")]
    public void WritePcm_UnsupportedExtension_Throws()
    {
        var path = TempPath(".ogg");
        try
        {
            Assert.Throws<NotSupportedException>(() => AudioFile.WritePcm(path, Format, new Byte[320]));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "24位WAV读取降为16位")]
    public void ReadPcm_24BitWav_Downconverts()
    {
        var path = TempPath(".wav");
        try
        {
            var wav = BuildWav(24, 2, 16000, 100);
            File.WriteAllBytes(path, wav);

            var info = AudioFile.Detect(path);
            Assert.Equal(24, info.Format.BitsPerSample);

            var audio = AudioFile.ReadPcm(path);
            Assert.Equal(16, audio.Format.BitsPerSample);
            Assert.Equal(400, audio.Data.Length);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "32位浮点WAV读取降为16位")]
    public void ReadPcm_FloatWav_Downconverts()
    {
        var path = TempPath(".wav");
        try
        {
            var wav = BuildFloatWav(48000, 1, 64);
            File.WriteAllBytes(path, wav);

            var audio = AudioFile.ReadPcm(path);
            Assert.Equal(128, audio.Data.Length);
            Assert.Equal(48000, audio.Format.SampleRate);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "未知数据解码明确报不支持")]
    public void ReadPcm_Unknown_Throws()
    {
        using var ms = new MemoryStream(new Byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 1, 2 });

        Assert.Throws<NotSupportedException>(() => AudioFile.ReadPcm(ms, null));
    }

    [Fact(DisplayName = "元数据读取：WAV 无标签返回空")]
    public void ReadMetadata_Wav_ReturnsNull()
    {
        var pcm = BuildSine(Format, 0.1, 0.5);
        var path = TempPath(".wav");
        try
        {
            AudioFile.WritePcm(path, Format, pcm);

            Assert.Null(AudioFile.Open(path).ReadMetadata());
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "WAV 标签写入后可读回且音频不受影响")]
    public void Wav_Metadata_RoundTrip()
    {
        var pcm = BuildSine(Format, 0.2, 0.5);
        var path = TempPath(".wav");
        try
        {
            AudioFile.WritePcm(path, Format, pcm, new AudioWriteOptions
            {
                Metadata = new AudioMetadata { Title = "标题", Artist = "作者", Album = "专辑", Genre = "流派", Comment = "注释", Year = 2024, TrackNumber = 7 },
            });

            var metadata = AudioFile.Open(path).ReadMetadata();
            Assert.NotNull(metadata);
            Assert.Equal("标题", metadata.Title);
            Assert.Equal("作者", metadata.Artist);
            Assert.Equal("专辑", metadata.Album);
            Assert.Equal("流派", metadata.Genre);
            Assert.Equal("注释", metadata.Comment);
            Assert.Equal(2024, metadata.Year);
            Assert.Equal(7, metadata.TrackNumber);

            Assert.Equal(pcm, AudioFile.ReadPcm(path).Data);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "WAV 标签写入后 RIFF 长度一致")]
    public void Wav_Metadata_RiffSizeConsistent()
    {
        var pcm = BuildSine(Format, 0.1, 0.5);
        var path = TempPath(".wav");
        try
        {
            AudioFile.WritePcm(path, Format, pcm, new AudioWriteOptions { Metadata = new AudioMetadata { Title = "标题" } });

            var bytes = File.ReadAllBytes(path);
            var riffSize = BitConverter.ToInt32(bytes, 4);
            var dataSize = BitConverter.ToInt32(bytes, bytes.Length - pcm.Length - 4);

            Assert.Equal(bytes.Length - 8, riffSize);
            Assert.Equal(pcm.Length, dataSize);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "另存为默认沿用源文件标签")]
    public void SaveAs_PreservesMetadata()
    {
        var pcm = BuildSine(Format, 0.2, 0.5);
        var source = TempPath(".mp3");
        var target = TempPath(".flac");
        try
        {
            AudioFile.WritePcm(source, Format, pcm, new AudioWriteOptions
            {
                Metadata = new AudioMetadata { Title = "标题", Artist = "作者", Album = "专辑", TrackNumber = 3, Year = 2024 },
            });

            var metadata = AudioFile.Open(source).ReadMetadata();
            Assert.NotNull(metadata);
            Assert.Equal("标题", metadata.Title);

            AudioFile.Open(source).SaveAs(target);

            var copied = AudioFile.Open(target).ReadMetadata();
            Assert.NotNull(copied);
            Assert.Equal("标题", copied.Title);
            Assert.Equal("作者", copied.Artist);
            Assert.Equal("专辑", copied.Album);
            Assert.Equal(3, copied.TrackNumber);
            Assert.Equal(2024, copied.Year);
        }
        finally
        {
            TryDelete(source);
            TryDelete(target);
        }
    }

    [Fact(DisplayName = "另存为可显式放弃源标签")]
    public void SaveAs_CanDropMetadata()
    {
        var pcm = BuildSine(Format, 0.2, 0.5);
        var source = TempPath(".mp3");
        var target = TempPath(".flac");
        try
        {
            AudioFile.WritePcm(source, Format, pcm, new AudioWriteOptions { Metadata = new AudioMetadata { Title = "旧标题" } });

            AudioFile.Open(source).SaveAs(target, new AudioWriteOptions { PreserveMetadata = false });

            Assert.Null(AudioFile.Open(target).ReadMetadata());
        }
        finally
        {
            TryDelete(source);
            TryDelete(target);
        }
    }

    [Fact(DisplayName = "MP3 仅改标签：音频数据不变")]
    public void UpdateMetadata_Mp3_KeepsAudio()
    {
        var pcm = BuildSine(new AudioFormat { SampleRate = 44100, Channels = 1, BitsPerSample = 16 }, 0.3, 0.5);
        var path = TempPath(".mp3");
        try
        {
            AudioFile.WritePcm(path, new AudioFormat { SampleRate = 44100, Channels = 1, BitsPerSample = 16 }, pcm, new AudioWriteOptions
            {
                Metadata = new AudioMetadata { Title = "旧标题", Artist = "旧作者" },
            });

            var before = AudioFile.ReadPcm(path).Data;

            AudioFile.UpdateMetadata(path, new AudioMetadata { Title = "新标题", Artist = "新作者", Genre = "测试" });

            var metadata = AudioFile.Open(path).ReadMetadata();
            Assert.NotNull(metadata);
            Assert.Equal("新标题", metadata.Title);
            Assert.Equal("新作者", metadata.Artist);
            Assert.Equal("测试", metadata.Genre);

            var after = AudioFile.ReadPcm(path).Data;
            Assert.Equal(before, after);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "FLAC 仅改标签：音频数据不变")]
    public void UpdateMetadata_Flac_KeepsAudio()
    {
        var pcm = BuildSine(Format, 0.3, 0.5);
        var path = TempPath(".flac");
        try
        {
            AudioFile.WritePcm(path, Format, pcm, new AudioWriteOptions { Metadata = new AudioMetadata { Title = "旧标题" } });

            var before = AudioFile.ReadPcm(path).Data;

            AudioFile.UpdateMetadata(path, new AudioMetadata { Title = "新标题", Album = "新专辑" });

            var metadata = AudioFile.Open(path).ReadMetadata();
            Assert.NotNull(metadata);
            Assert.Equal("新标题", metadata.Title);
            Assert.Equal("新专辑", metadata.Album);

            var after = AudioFile.ReadPcm(path).Data;
            Assert.Equal(before, after);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "仅改标签对不支持的容器明确报错")]
    public void UpdateMetadata_Unsupported_Throws()
    {
        var pcm = BuildSine(Format, 0.1, 0.5);
        var path = TempPath(".wav");
        try
        {
            AudioFile.WritePcm(path, Format, pcm);

            Assert.Throws<NotSupportedException>(() => AudioFile.UpdateMetadata(path, new AudioMetadata { Title = "标题" }));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "保存失败时目标文件保持原状")]
    public void WritePcm_Unsupported_KeepsExistingFile()
    {
        var pcm = BuildSine(Format, 0.1, 0.5);
        var path = TempPath(".wav");
        var unsupported = TempPath(".xyz");
        try
        {
            AudioFile.WritePcm(path, Format, pcm);
            var before = File.ReadAllBytes(path);

            File.Copy(path, unsupported, true);
            var unsupportedBefore = File.ReadAllBytes(unsupported);

            Assert.Throws<NotSupportedException>(() => AudioFile.WritePcm(unsupported, Format, pcm));

            Assert.Equal(unsupportedBefore, File.ReadAllBytes(unsupported));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            TryDelete(path);
            TryDelete(unsupported);
        }
    }

    [Fact(DisplayName = "同编码另存为走帧级复制")]
    public void SaveAs_SameCodec_UsesFrameCopy()
    {
        var format = new AudioFormat { SampleRate = 44100, Channels = 1, BitsPerSample = 16 };
        var pcm = BuildSine(format, 0.3, 0.5);
        var source = TempPath(".mp3");
        var target = TempPath(".mp3");
        try
        {
            AudioFile.WritePcm(source, format, pcm, new AudioWriteOptions { Metadata = new AudioMetadata { Title = "源标题" } });
            var before = AudioFile.ReadPcm(source).Data;

            var result = AudioFile.Open(source).SaveAs(target, new AudioWriteOptions { Metadata = new AudioMetadata { Title = "新标题" } });

            Assert.True(result.FrameCopied);
            Assert.False(result.Reencoded);
            Assert.Equal(AudioContainerKind.Mp3, result.Container);

            var metadata = AudioFile.Open(target).ReadMetadata();
            Assert.Equal("新标题", metadata.Title);

            // 音频数据逐位一致：真正没有重编码
            Assert.Equal(before, AudioFile.ReadPcm(target).Data);
        }
        finally
        {
            TryDelete(source);
            TryDelete(target);
        }
    }

    [Fact(DisplayName = "关闭帧级复制后强制重编码")]
    public void SaveAs_PreferFrameCopyOff_Reencodes()
    {
        var format = new AudioFormat { SampleRate = 44100, Channels = 1, BitsPerSample = 16 };
        var pcm = BuildSine(format, 0.2, 0.5);
        var source = TempPath(".mp3");
        var target = TempPath(".mp3");
        try
        {
            AudioFile.WritePcm(source, format, pcm, new AudioWriteOptions { Metadata = new AudioMetadata { Title = "标题" } });

            var result = AudioFile.Open(source).SaveAs(target, new AudioWriteOptions { PreferFrameCopy = false });

            Assert.False(result.FrameCopied);
            Assert.True(result.Reencoded);
            Assert.Equal("标题", AudioFile.Open(target).ReadMetadata().Title);
        }
        finally
        {
            TryDelete(source);
            TryDelete(target);
        }
    }

    [Fact(DisplayName = "跨编码另存为回退重编码")]
    public void SaveAs_DifferentCodec_Reencodes()
    {
        var pcm = BuildSine(Format, 0.2, 0.5);
        var source = TempPath(".wav");
        var target = TempPath(".flac");
        try
        {
            AudioFile.WritePcm(source, Format, pcm);

            var result = AudioFile.Open(source).SaveAs(target);

            Assert.False(result.FrameCopied);
            Assert.True(result.Reencoded);
            Assert.Equal(AudioContainerKind.Flac, result.Container);
            Assert.Equal(pcm.Length, AudioFile.ReadPcm(target).Data.Length);
        }
        finally
        {
            TryDelete(source);
            TryDelete(target);
        }
    }

    [Fact(DisplayName = "WAV 同容器另存为保持逐位一致")]
    public void SaveAs_SameWav_FrameCopy()
    {
        var pcm = BuildSine(Format, 0.1, 0.5);
        var source = TempPath(".wav");
        var target = TempPath(".wav");
        try
        {
            AudioFile.WritePcm(source, Format, pcm);

            var result = AudioFile.Open(source).SaveAs(target);

            Assert.True(result.FrameCopied);
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
        }
        finally
        {
            TryDelete(source);
            TryDelete(target);
        }
    }

    #region 流式解码
    [Fact(DisplayName = "WAV 流式解码与整段解码一致")]
    public void StreamPcm_Wav_MatchesReadPcm()
    {
        var pcm = BuildSine(Format, 0.5, 0.5);
        var path = TempPath(".wav");
        try
        {
            AudioFile.WritePcm(path, Format, pcm);

            var chunks = AudioFile.Open(path).StreamPcm(4096).ToList();

            Assert.True(chunks.Count > 1);
            Assert.All(chunks, c => Assert.True(c.Data.Length > 0));
            Assert.All(chunks, c => Assert.True(c.Data.Length <= 4096 + 4096));
            Assert.All(chunks, c => Assert.Equal(16000, c.Format.SampleRate));
            Assert.All(chunks, c => Assert.Equal(16, c.Format.BitsPerSample));
            Assert.Equal(pcm, Concat(chunks));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "分块参数过小时按最小块处理")]
    public void StreamPcm_ChunkBytesClamped()
    {
        var pcm = BuildSine(Format, 0.5, 0.5);
        var path = TempPath(".wav");
        try
        {
            AudioFile.WritePcm(path, Format, pcm);

            var chunks = AudioFile.Open(path).StreamPcm(16).ToList();

            Assert.Equal(pcm, Concat(chunks));
            Assert.All(chunks, c => Assert.True(c.Data.Length <= 1024 + 4096));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "AIFF 流式解码与整段解码一致")]
    public void StreamPcm_Aiff_MatchesReadPcm()
    {
        var pcm = BuildSine(Format, 0.3, 0.6);
        var path = TempPath(".aiff");
        try
        {
            AudioFile.WritePcm(path, Format, pcm);

            var bulk = AudioFile.ReadPcm(path);
            var chunks = AudioFile.Open(path).StreamPcm(4096).ToList();

            Assert.Equal(bulk.Data, Concat(chunks));
            Assert.Equal(16000, chunks[0].Format.SampleRate);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "M4A 流式解码与整段解码一致")]
    public void StreamPcm_M4a_MatchesReadPcm()
    {
        var format = new AudioFormat { SampleRate = 44100, Channels = 1, BitsPerSample = 16, Encoding = AVTypes.AAC, SamplesPerFrame = 1024 };
        var codec = new AacCodec();
        var options = new AacEncoderOptions { SampleRate = 44100, Channels = 1, Bitrate = 64000 };
        var source = BuildSine(new AudioFormat { SampleRate = 44100, Channels = 1 }, 0.2, 0.5);
        var path = TempPath(".m4a");
        try
        {
            using (var fs = File.Create(path))
            using (var writer = new Mp4FileWriter(fs, format))
            {
                for (var offset = 0; offset + 4096 <= source.Length; offset += 4096)
                {
                    var block = new Byte[4096];
                    Buffer.BlockCopy(source, offset, block, 0, block.Length);

                    var adts = codec.FromPcm(block, options).GetSpan().ToArray();
                    var header = AacCodec.ParseAdtsHeader(adts, 0);
                    Assert.NotNull(header);
                    var headerSize = header.ProtectionAbsent ? 7 : 9;
                    writer.WriteFrame(adts[headerSize..(Int32)header.FrameLength]);
                }
            }

            var bulk = AudioFile.ReadPcm(path);
            var chunks = AudioFile.Open(path).StreamPcm(4096).ToList();

            Assert.True(chunks.Count > 0);
            Assert.Equal(bulk.Data, Concat(chunks));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "FLAC 流式解码退化为整段解码后分块")]
    public void StreamPcm_Flac_MatchesReadPcm()
    {
        var pcm = BuildSine(Format, 0.3, 0.6);
        var path = TempPath(".flac");
        try
        {
            AudioFile.WritePcm(path, Format, pcm);

            var bulk = AudioFile.ReadPcm(path);
            var chunks = AudioFile.Open(path).StreamPcm(4096).ToList();

            Assert.True(chunks.Count > 1);
            Assert.Equal(bulk.Data, Concat(chunks));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "空音频文件流式解码不产出分块")]
    public void StreamPcm_EmptyWav_YieldsNothing()
    {
        var path = TempPath(".wav");
        try
        {
            AudioFile.WritePcm(path, Format, []);

            Assert.Empty(AudioFile.Open(path).StreamPcm(4096));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "流式解码未知数据明确报不支持")]
    public void StreamPcm_Unknown_Throws()
    {
        using var ms = new MemoryStream(new Byte[512]);

        Assert.Throws<NotSupportedException>(() => AudioFile.StreamPcm(ms));
    }

    [Fact(DisplayName = "流式解码不可定位流明确报不支持")]
    public void StreamPcm_NonSeekable_Throws()
    {
        using var stream = new NonSeekableStream(new MemoryStream(BuildWav(16, 1, 16000, 64)));

        Assert.Throws<NotSupportedException>(() => AudioFile.StreamPcm(stream));
    }

    private static Byte[] Concat(IEnumerable<AudioPcm> chunks)
    {
        using var ms = new MemoryStream();
        foreach (var chunk in chunks)
        {
            if (chunk?.Data != null) ms.Write(chunk.Data, 0, chunk.Data.Length);
        }

        return ms.ToArray();
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override Boolean CanRead => true;
        public override Boolean CanSeek => false;
        public override Boolean CanWrite => false;
        public override Int64 Length => throw new NotSupportedException();
        public override Int64 Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Int64 Seek(Int64 offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(Int64 value) => throw new NotSupportedException();
        public override void Write(Byte[] buffer, Int32 offset, Int32 count) => throw new NotSupportedException();
        public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count) => inner.Read(buffer, offset, count);

        protected override void Dispose(Boolean disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
    #endregion

    #region 辅助
    private static String TempPath(String extension) => Path.Combine(Path.GetTempPath(), "nlaudio_" + Guid.NewGuid().ToString("N") + extension);
    private static void TryDelete(String path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 临时文件清理失败不影响测试结论
        }
    }

    private static Byte[] BuildSine(AudioFormat format, Double seconds, Double amplitude)
    {
        var samples = (Int32)(format.SampleRate * seconds);
        var data = new Byte[samples * format.Channels * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (Int16)(Math.Sin(2 * Math.PI * 440 * i / format.SampleRate) * amplitude * 32767);
            for (var ch = 0; ch < format.Channels; ch++)
            {
                var offset = (i * format.Channels + ch) * 2;
                data[offset] = (Byte)(v & 0xFF);
                data[offset + 1] = (Byte)((v >> 8) & 0xFF);
            }
        }

        return data;
    }

    private static Double Snr(Byte[] source, Byte[] actual)
    {
        // 编码器/解码器存在固定延迟，搜索最佳整数对齐后再算信噪比
        var best = -999.0;
        for (var shift = 0; shift < 2000; shift++)
        {
            var count = Math.Min(source.Length, actual.Length - shift * 2) / 2;
            if (count < 2000) break;

            double signal = 0;
            double error = 0;
            for (var i = 0; i < count; i++)
            {
                double s = (Int16)(source[i * 2] | source[i * 2 + 1] << 8);
                double d = (Int16)(actual[(i + shift) * 2] | actual[(i + shift) * 2 + 1] << 8) - s;
                signal += s * s;
                error += d * d;
            }

            var snr = 10 * Math.Log10(signal / Math.Max(error, 1e-9));
            if (snr > best) best = snr;
        }

        return best;
    }

    private static Byte[] BuildWav(Int32 bits, Int32 channels, Int32 rate, Int32 samples)
    {
        var blockAlign = channels * bits / 8;
        var data = new Byte[samples * blockAlign];
        var body = new MemoryStream();

        var fmt = new Byte[16];
        BitConverter.GetBytes((UInt16)1).CopyTo(fmt, 0);
        BitConverter.GetBytes((Int16)channels).CopyTo(fmt, 2);
        BitConverter.GetBytes(rate).CopyTo(fmt, 4);
        BitConverter.GetBytes(rate * blockAlign).CopyTo(fmt, 8);
        BitConverter.GetBytes((Int16)blockAlign).CopyTo(fmt, 12);
        BitConverter.GetBytes((Int16)bits).CopyTo(fmt, 14);

        WriteChunk(body, "fmt ", fmt);
        WriteChunk(body, "data", data);

        var content = body.ToArray();
        var wav = new Byte[12 + content.Length];
        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(content.Length + 4).CopyTo(wav, 4);
        System.Text.Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        content.CopyTo(wav, 12);

        return wav;
    }

    private static Byte[] BuildFloatWav(Int32 rate, Int32 channels, Int32 samples)
    {
        var blockAlign = channels * 4;
        var data = new Byte[samples * blockAlign];
        var body = new MemoryStream();

        var fmt = new Byte[16];
        BitConverter.GetBytes((UInt16)3).CopyTo(fmt, 0);
        BitConverter.GetBytes((Int16)channels).CopyTo(fmt, 2);
        BitConverter.GetBytes(rate).CopyTo(fmt, 4);
        BitConverter.GetBytes(rate * blockAlign).CopyTo(fmt, 8);
        BitConverter.GetBytes((Int16)blockAlign).CopyTo(fmt, 12);
        BitConverter.GetBytes((Int16)32).CopyTo(fmt, 14);

        WriteChunk(body, "fmt ", fmt);
        WriteChunk(body, "data", data);

        var content = body.ToArray();
        var wav = new Byte[12 + content.Length];
        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(content.Length + 4).CopyTo(wav, 4);
        System.Text.Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        content.CopyTo(wav, 12);

        return wav;
    }

    private static void WriteChunk(Stream stream, String id, Byte[] payload)
    {
        var header = new Byte[8];
        System.Text.Encoding.ASCII.GetBytes(id).CopyTo(header, 0);
        BitConverter.GetBytes(payload.Length).CopyTo(header, 4);

        stream.Write(header, 0, header.Length);
        stream.Write(payload, 0, payload.Length);
        if ((payload.Length & 1) != 0) stream.WriteByte(0);
    }
    #endregion
}
