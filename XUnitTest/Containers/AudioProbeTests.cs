using System;
using System.IO;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest.Containers;

/// <summary>容器探测与虚拟流 I/O 测试</summary>
public class AudioProbeTests
{
    [Fact(DisplayName = "探测WAV返回容器格式与时长")]
    public void Probe_Wav()
    {
        var format = new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16 };
        Byte[] wav;
        using (var buffer = new MemoryStream())
        {
            using (var writer = new WaveFileWriter(buffer, format))
            {
                writer.WriteFrame(new Byte[320 * 25]); // 0.5 秒
                writer.Flush();
            }
            wav = buffer.ToArray();
        }

        using var ms = new MemoryStream(wav);
        var info = AudioProbe.Probe(ms);

        Assert.Equal(AudioContainerKind.Wav, info.Container);
        Assert.Equal(AVTypes.LPCM, info.Codec);
        Assert.NotNull(info.Format);
        Assert.Equal(8000, info.Format.SampleRate);
        Assert.True(info.Duration > 0.4 && info.Duration < 0.6, $"时长异常：{info.Duration}");
        Assert.True(info.Bitrate > 0);
        Assert.Equal(1.0, info.Confidence);
        Assert.Equal(0, ms.Position);
    }

    [Fact(DisplayName = "探测FLAC返回容器与Vorbis标签")]
    public void Probe_Flac()
    {
        var format = new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16 };
        using var ms = new MemoryStream();
        using (var writer = new FlacFileWriter(ms, format))
        {
            writer.WriteMetadata(new AudioMetadata { Title = "探测目标", Artist = "NewLife" });
            writer.WriteFrame(new Byte[16000]);
            writer.Flush();
        }

        ms.Position = 0;
        var info = AudioProbe.Probe(ms);

        Assert.Equal(AudioContainerKind.Flac, info.Container);        Assert.Equal("探测目标", info.Metadata.Title);
        Assert.Equal(8000, info.Format.SampleRate);
        Assert.True(info.Duration > 0.9);
    }

    [Fact(DisplayName = "探测MP3返回容器与编码")]
    public void Probe_Mp3()
    {
        var format = new AudioFormat { SampleRate = 44100, Channels = 1, BitsPerSample = 16 };
        var pcm = BuildSine(format, 500, 0.5);
        var encoded = new Mp3Codec().FromPcm(pcm, new Mp3EncoderOptions { SampleRate = 44100, Channels = 1, Bitrate = 128 }).GetSpan().ToArray();

        using var ms = new MemoryStream(encoded);
        var info = AudioProbe.Probe(ms);

        Assert.Equal(AudioContainerKind.Mp3, info.Container);
        Assert.Equal(AVTypes.MP3, info.Codec);
        Assert.Equal(44100, info.Format.SampleRate);
        Assert.True(info.Duration > 0);
    }

    [Fact(DisplayName = "探测ADTS解析采样率声道与帧数")]
    public void Probe_Adts()
    {
        var data = BuildAdts(44100, 2, 3);

        using var ms = new MemoryStream(data);
        var info = AudioProbe.Probe(ms);

        Assert.Equal(AudioContainerKind.Adts, info.Container);
        Assert.Equal(AVTypes.AAC, info.Codec);
        Assert.Equal(44100, info.Format.SampleRate);
        Assert.Equal(2, info.Format.Channels);
        Assert.True(info.Duration > 0.05, $"时长异常：{info.Duration}");
    }

    [Fact(DisplayName = "探测未知数据返回Unknown且置信度为0")]
    public void Probe_Unknown()
    {
        using var ms = new MemoryStream(new Byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
        var info = AudioProbe.Probe(ms);

        Assert.Equal(AudioContainerKind.Unknown, info.Container);
        Assert.Equal(0, info.Confidence);
    }

    [Fact(DisplayName = "探测不可定位流不抛异常")]
    public void Probe_NonSeekable()
    {
        var format = new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16 };
        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(ms, format))
        {
            writer.WriteFrame(new Byte[3200]);
            writer.Flush();
        }

        using var source = new ForwardOnlyStream(ms.ToArray());
        var info = AudioProbe.Probe(source);

        Assert.Equal(AudioContainerKind.Wav, info.Container);
        Assert.False(info.CanSeek);
        Assert.Equal(-1, info.Length);
    }

    [Fact(DisplayName = "回调流按能力声明读写定位")]
    public void CallbackStream_RespectsCapabilities()
    {
        var data = new Byte[100];
        for (var i = 0; i < data.Length; i++) data[i] = (Byte)i;
        var position = 0;

        using var stream = new CallbackStream(
            read: (buffer, offset, count) =>
            {
                var n = Math.Min(count, data.Length - position);
                Array.Copy(data, position, buffer, offset, n);
                position += n;
                return n;
            },
            write: (buffer, offset, count) => { },
            length: () => data.Length,
            position: () => position,
            seek: target => position = (Int32)target);

        Assert.True(stream.CanRead);
        Assert.True(stream.CanWrite);
        Assert.True(stream.CanSeek);
        Assert.Equal(100, stream.Length);

        var buffer = new Byte[10];
        Assert.Equal(10, stream.Read(buffer, 0, buffer.Length));
        Assert.Equal(10, stream.Position);

        stream.Seek(50, SeekOrigin.Begin);
        Assert.Equal(50, stream.Position);
        stream.Position = 0;
        Assert.Equal(0, stream.Position);

        using var readOnly = new CallbackStream(read: (b, o, c) => 0);
        Assert.False(readOnly.CanWrite);
        Assert.False(readOnly.CanSeek);
        Assert.Throws<NotSupportedException>(() => readOnly.Write(buffer, 0, 1));
        Assert.Throws<NotSupportedException>(() => readOnly.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => readOnly.Length);
    }

    [Fact(DisplayName = "回调数据源可驱动WAV读取器")]
    public void CallbackStream_DrivesWaveReader()
    {
        var format = new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16 };
        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(ms, format))
        {
            writer.WriteFrame(new Byte[320 * 5]);
            writer.Flush();
        }

        var data = ms.ToArray();
        var position = 0L;

        using var stream = AudioStreamFactory.FromCallback(
            (buffer, offset, count) =>
            {
                var n = (Int32)Math.Min(count, data.Length - position);
                Array.Copy(data, (Int32)position, buffer, offset, n);
                position += n;
                return n;
            },
            () => data.Length,
            () => position,
            target => position = target);

        using var reader = new WaveFileReader(stream);

        Assert.Equal(8000, reader.Format.SampleRate);
        var frame = reader.ReadFrame();
        Assert.NotNull(frame);
        Assert.Equal(320, frame.Total);
    }

    #region 辅助
    private static Byte[] BuildSine(AudioFormat format, Int32 samples, Double amplitude)
    {
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

    /// <summary>构造 n 个 ADTS 帧：44.1k/立体声/AAC-LC，帧长 7 字节头 + 4 字节载荷</summary>
    private static Byte[] BuildAdts(Int32 sampleRate, Int32 channels, Int32 frames)
    {
        var rates = new[] { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };
        var sfIndex = Array.IndexOf(rates, sampleRate);
        var frameLength = 11;

        var data = new Byte[frames * frameLength];
        for (var f = 0; f < frames; f++)
        {
            var offset = f * frameLength;
            data[offset] = 0xFF;
            data[offset + 1] = 0xF1;                                        // sync + MPEG-4 + layer 00 + no CRC
            data[offset + 2] = (Byte)((1 << 6) | (sfIndex << 2) | ((channels >> 2) & 1));
            data[offset + 3] = (Byte)(((channels & 3) << 6) | ((frameLength >> 11) & 3));
            data[offset + 4] = (Byte)((frameLength >> 3) & 0xFF);
            data[offset + 5] = (Byte)(((frameLength & 7) << 5) | 0x1F);
            data[offset + 6] = 0xFC;
        }

        return data;
    }

    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream _inner;

        public ForwardOnlyStream(Byte[] data) => _inner = new MemoryStream(data);

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
