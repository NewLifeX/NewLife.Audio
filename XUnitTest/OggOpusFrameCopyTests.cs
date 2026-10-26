using System;
using System.Collections.Generic;
using System.IO;
using NewLife.Audio;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest;

public class OggOpusFrameCopyTests
{
    [Fact(DisplayName = "Ogg Opus 另存为走帧级复制并保留包与标签")]
    public void SaveAs_OggOpus_FrameCopy()
    {
        var format = new AudioFormat { SampleRate = 48000, Channels = 2, BitsPerSample = 16, Encoding = AVTypes.Opus };
        var source = TempPath(".opus");
        var target = TempPath(".opus");
        try
        {
            using (var fs = File.Create(source))
            using (var writer = new OggFileWriter(fs, format, AVTypes.Opus) { PreSkip = 312 })
            {
                writer.WriteFrame([0x78, 0x01, 0x02, 0x03]);
                writer.WriteFrame([0x78, 0x04, 0x05, 0x06]);
                writer.Flush();
            }

            var result = AudioFile.Open(source).SaveAs(target, new AudioWriteOptions
            {
                Metadata = new AudioMetadata { Title = "Frame Copy" },
            });

            Assert.True(result.FrameCopied);
            Assert.False(result.Reencoded);
            Assert.Equal(AVTypes.Opus, AudioProbe.Probe(target).Codec);
            Assert.Equal("Frame Copy", AudioFile.Open(target).ReadMetadata()?.Title);

            var before = ReadPackets(source);
            var after = ReadPackets(target);
            Assert.Equal(2, before.Count);
            Assert.Equal(before.Count, after.Count);
            for (var i = 0; i < before.Count; i++) Assert.Equal(before[i], after[i]);
        }
        finally
        {
            TryDelete(source);
            TryDelete(target);
        }
    }

    private static List<Byte[]> ReadPackets(String path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new OggFileReader(stream) { ValidateCrc = true };
        var list = new List<Byte[]>();
        while (reader.ReadFrame() is { } frame) list.Add(frame.GetSpan().ToArray());

        return list;
    }

    private static String TempPath(String extension) => Path.Combine(Path.GetTempPath(), "nl-audio-" + Guid.NewGuid().ToString("N") + extension);

    private static void TryDelete(String path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
