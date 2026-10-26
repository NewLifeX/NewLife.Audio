using System;
using System.IO;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using NewLife.Audio.Streaming;
using Xunit;

namespace XUnitTest;

/// <summary>压缩音频输出管线测试：重采样+声道转换+编码落盘</summary>
public class AudioEncodePipelineTests
{
    private static readonly AudioFormat Input16k = new() { SampleRate = 16000, Channels = 1, BitsPerSample = 16 };

    [Fact(DisplayName = "WAV 管线：重采样与上混后格式与时长正确")]
    public void Pipeline_Wav_ResampleAndUpmix()
    {
        var pcm = BuildSine(Input16k, 0.5, 440, 0.5);
        using var ms = new MemoryStream();

        using (var pipeline = new AudioEncodePipeline(ms, Input16k, new AudioEncodeOptions
        {
            Container = ".wav",
            LeaveOpen = true,
            SampleRate = 32000,
            Channels = 2,
        }))
        {
            foreach (var chunk in Chunks(pcm, 4096)) pipeline.Write(chunk);
            pipeline.Complete();

            Assert.Equal(32000, pipeline.OutputFormat.SampleRate);
            Assert.Equal(2, pipeline.OutputFormat.Channels);
            Assert.InRange(pipeline.OutputFrames, 15990, 16010);
            Assert.True(pipeline.FirstChunkLatency > TimeSpan.Zero);
        }

        ms.Position = 0;
        var audio = AudioFile.ReadPcm(ms, ResetAndProbe(ms));
        Assert.Equal(32000, audio.Format.SampleRate);
        Assert.Equal(2, audio.Format.Channels);
        Assert.InRange(audio.Duration, 0.48, 0.52);
    }

    [Fact(DisplayName = "MP3 管线：分块写入后可解码且时长一致")]
    public void Pipeline_Mp3_Streaming()
    {
        var pcm = BuildSine(new AudioFormat { SampleRate = 24000, Channels = 1, BitsPerSample = 16 }, 1.0, 440, 0.5);
        using var ms = new MemoryStream();

        using (var pipeline = new AudioEncodePipeline(ms, new AudioFormat { SampleRate = 24000, Channels = 1, BitsPerSample = 16 }, new AudioEncodeOptions
        {
            Container = ".mp3",
            LeaveOpen = true,
            Mp3BitrateKbps = 64,
            Metadata = new AudioMetadata { Title = "TTS", Artist = "NewLife" },
        }))
        {
            foreach (var chunk in Chunks(pcm, 2048)) pipeline.Write(chunk);
            pipeline.Complete();
        }

        var bytes = ms.ToArray();

        using (var meta = new MemoryStream(bytes))
        {
            var reader = new Mp3FileReader(meta);
            Assert.Equal("TTS", reader.Metadata.Title);
            Assert.Equal(24000, reader.Format.SampleRate);
        }

        var decoded = new Mp3Codec().ToPcm(bytes, null).GetSpan().ToArray();
        var duration = decoded.Length / 2.0 / 24000;
        Assert.InRange(duration, 0.95, 1.15);
    }

    [Fact(DisplayName = "FLAC 管线：无损往返并保留标签")]
    public void Pipeline_Flac_Lossless()
    {
        var pcm = BuildSine(Input16k, 0.3, 660, 0.4);
        using var ms = new MemoryStream();

        using (var pipeline = new AudioEncodePipeline(ms, Input16k, new AudioEncodeOptions
        {
            Container = ".flac",
            LeaveOpen = true,
            Metadata = new AudioMetadata { Title = "无损" },
        }))
        {
            pipeline.Write(pcm);
            pipeline.Complete();
        }

        var bytes = ms.ToArray();
        using var stream = new MemoryStream(bytes);
        var info = AudioProbe.Probe(stream);
        stream.Position = 0;

        var audio = AudioFile.ReadPcm(stream, info);
        Assert.Equal(pcm, audio.Data);
        using (var meta = new MemoryStream(bytes))
        {
            var flac = new FlacContainerReader(meta);
            Assert.Equal("无损", flac.Metadata.Title);
        }
    }

    [Fact(DisplayName = "管线完成后继续写入明确报错")]
    public void Pipeline_WriteAfterComplete_Throws()
    {
        using var ms = new MemoryStream();
        var pipeline = new AudioEncodePipeline(ms, Input16k, new AudioEncodeOptions { Container = ".wav" });
        pipeline.Write(BuildSine(Input16k, 0.05, 440, 0.3));
        pipeline.Complete();

        Assert.True(pipeline.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => pipeline.Write(new Byte[320]));
    }

    [Fact(DisplayName = "管线可按目标帧数精确输出（不重采样时不补零）")]
    public void Pipeline_ExactFrames()
    {
        var pcm = BuildSine(Input16k, 0.25, 440, 0.3);
        using var ms = new MemoryStream();

        using (var pipeline = new AudioEncodePipeline(ms, Input16k, new AudioEncodeOptions { Container = ".wav" }))
        {
            pipeline.Write(pcm);
            pipeline.Complete();

            Assert.Equal(4000, pipeline.OutputFrames);
        }
    }

    #region 辅助
    private static Byte[] BuildSine(AudioFormat format, Double seconds, Double frequency, Double amplitude)
    {
        var samples = (Int32)(format.SampleRate * seconds);
        var data = new Byte[samples * format.Channels * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (Int16)(Math.Sin(2 * Math.PI * frequency * i / format.SampleRate) * amplitude * 32000);
            for (var ch = 0; ch < format.Channels; ch++)
            {
                var p = (i * format.Channels + ch) * 2;
                data[p] = (Byte)(v & 0xFF);
                data[p + 1] = (Byte)((v >> 8) & 0xFF);
            }
        }

        return data;
    }

    private static System.Collections.Generic.IEnumerable<Byte[]> Chunks(Byte[] data, Int32 size)
    {
        for (var offset = 0; offset < data.Length; offset += size)
        {
            var count = Math.Min(size, data.Length - offset);
            var chunk = new Byte[count];
            Array.Copy(data, offset, chunk, 0, count);
            yield return chunk;
        }
    }

    private static AudioStreamInfo ResetAndProbe(MemoryStream ms)
    {
        ms.Position = 0;
        var info = AudioProbe.Probe(ms);
        ms.Position = 0;

        return info;
    }

    #endregion
}






