using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using NewLife.Audio.Codecs;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Containers;

/// <summary>接受 16 位交错 PCM 并自行完成编码的容器写入器</summary>
internal interface IAudioPcmSink
{
    /// <summary>写入一段 16 位小端交错 PCM</summary>
    /// <param name="pcm">PCM 数据</param>
    void WritePcm(ReadOnlySpan<Byte> pcm);
}

/// <summary>Ogg Vorbis 写入器（纯托管编码）</summary>
internal sealed class OggVorbisFileWriter : IAudioContainerWriter, IAudioPcmSink
{
    private readonly OggFileWriter _writer;
    private readonly VorbisEncoder _encoder;
    private readonly Int32 _channels;

    /// <summary>音频格式</summary>
    public AudioFormat Format { get; }

    /// <summary>构造 Ogg Vorbis 写入器</summary>
    /// <param name="stream">输出流</param>
    /// <param name="format">16 位 PCM 格式</param>
    /// <param name="quality">质量档位 0~10</param>
    public OggVorbisFileWriter(Stream stream, AudioFormat format, Int32 quality)
    {
        _channels = Math.Max(1, format.Channels);
        Format = new AudioFormat
        {
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            BitsPerSample = 16,
            Encoding = AVTypes.Vorbis,
        };

        var options = new VorbisOptions { SampleRate = format.SampleRate, Channels = _channels, Quality = quality }.Validate();
        _encoder = new VorbisEncoder(options);
        _writer = new OggFileWriter(stream, Format, AVTypes.Vorbis);

        var headers = _encoder.Headers;
        _writer.WriteVorbisHeaders(headers[0], headers[1], headers[2]);
    }

    /// <summary>写入元数据</summary>
    /// <param name="metadata">音频元数据</param>
    public void WriteMetadata(AudioMetadata metadata) => _writer.WriteMetadata(metadata);

    /// <summary>写入一段 PCM 并编码成 Vorbis 包</summary>
    /// <param name="pcm">16 位小端交错 PCM</param>
    public void WritePcm(ReadOnlySpan<Byte> pcm)
    {
        if (pcm.Length < 2) return;

        var samples = PcmConverter.ToFloat(pcm, SampleFormat.Int16);
        var frames = samples.Length / _channels;
        if (frames <= 0) return;

        var packet = _encoder.EncodePacket(samples, frames);
        if (packet.Length > 0) _writer.WriteFrame(packet, _encoder.LastPacketSamples);
    }

    /// <summary>写入一段 PCM（与 <see cref="WritePcm"/> 等价）</summary>
    /// <param name="frame">PCM 数据</param>
    public void WriteFrame(ReadOnlySpan<Byte> frame) => WritePcm(frame);

    /// <summary>冲刷编码器尾包</summary>
    public void Flush()
    {
        while (true)
        {
            var tail = _encoder.Flush();
            if (tail.Length == 0) break;

            _writer.WriteFrame(tail, _encoder.LastPacketSamples);
        }

        _writer.Flush();
    }

    /// <summary>释放写入器</summary>
    public void Dispose()
    {
        Flush();
        _writer.Dispose();
        _encoder.Dispose();
    }
}

/// <summary>Ogg Opus 写入器</summary>
internal sealed class OggOpusFileWriter : IAudioContainerWriter, IAudioPcmSink
{
    private readonly OggFileWriter _writer;
    private readonly OpusCodec _codec;
    private readonly OpusOptions _options;

    /// <summary>音频格式</summary>
    public AudioFormat Format { get; }

    /// <summary>构造 Ogg Opus 写入器</summary>
    /// <param name="stream">输出流</param>
    /// <param name="format">16 位 PCM 格式（采样率需为 8/12/16/24/48 kHz，管线负责重采样）</param>
    /// <param name="bitrate">目标码率（bps）</param>
    public OggOpusFileWriter(Stream stream, AudioFormat format, Int32 bitrate)
    {
        var sampleRate = OpusOptions.GetNearestSampleRate(format.SampleRate);
        _options = new OpusOptions
        {
            SampleRate = sampleRate,
            Channels = format.Channels,
            Bitrate = bitrate > 0 ? bitrate : 64000,
        };

        Format = new AudioFormat
        {
            SampleRate = sampleRate,
            Channels = format.Channels,
            BitsPerSample = 16,
            Encoding = AVTypes.Opus,
            SamplesPerFrame = _options.SamplesPerFrame,
        };

        _writer = new OggFileWriter(stream, Format, AVTypes.Opus) { PreSkip = OpusCodec.GetLookahead(_options) };
        _codec = new OpusCodec();
    }

    /// <summary>写入元数据</summary>
    /// <param name="metadata">音频元数据</param>
    public void WriteMetadata(AudioMetadata metadata) => _writer.WriteMetadata(metadata);

    /// <summary>写入一段 PCM 并编码成 Opus 包</summary>
    /// <param name="pcm">16 位小端交错 PCM</param>
    public void WritePcm(ReadOnlySpan<Byte> pcm)
    {
        if (pcm.Length < 2) return;

        foreach (var packet in _codec.Encode(pcm, _options))
        {
            if (packet.Total > 0) _writer.WriteFrame(packet.GetSpan());
        }
    }

    /// <summary>写入一段 PCM（与 <see cref="WritePcm"/> 等价）</summary>
    /// <param name="frame">PCM 数据</param>
    public void WriteFrame(ReadOnlySpan<Byte> frame) => WritePcm(frame);

    /// <summary>冲刷编码器尾包</summary>
    public void Flush()
    {
        foreach (var packet in _codec.Encode(ReadOnlySpan<Byte>.Empty, _options, true))
        {
            if (packet.Total > 0) _writer.WriteFrame(packet.GetSpan());
        }

        _writer.Flush();
    }

    /// <summary>释放写入器</summary>
    public void Dispose()
    {
        Flush();
        _writer.Dispose();
        _codec.Dispose();
    }
}

/// <summary>M4A（MP4 + AAC-LC）写入器</summary>
internal sealed class Mp4AacFileWriter : IAudioContainerWriter, IAudioPcmSink
{
    /// <summary>AAC 每帧样本数</summary>
    private const Int32 FrameSamples = 1024;

    private readonly Mp4FileWriter _writer;
    private readonly AacCodec _codec = new();
    private readonly AacEncoderOptions _options;
    private readonly Int32 _frameBytes;
    private readonly Byte[] _block;
    private Int32 _count;

    /// <summary>音频格式</summary>
    public AudioFormat Format { get; }

    /// <summary>构造 M4A 写入器</summary>
    /// <param name="stream">输出流</param>
    /// <param name="format">16 位 PCM 格式</param>
    /// <param name="bitrate">目标码率（bps）</param>
    public Mp4AacFileWriter(Stream stream, AudioFormat format, Int32 bitrate)
    {
        Format = new AudioFormat
        {
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            BitsPerSample = 16,
            Encoding = AVTypes.AAC,
            SamplesPerFrame = FrameSamples,
        };

        _options = new AacEncoderOptions
        {
            SampleRate = Format.SampleRate,
            Channels = Format.Channels,
            Bitrate = bitrate > 0 ? bitrate : 64000,
            Adts = false,
        };
        _options.Validate();

        _frameBytes = FrameSamples * Format.Channels * 2;
        _block = new Byte[_frameBytes];
        _writer = new Mp4FileWriter(stream, Format);
    }

    /// <summary>写入元数据</summary>
    /// <param name="metadata">音频元数据</param>
    public void WriteMetadata(AudioMetadata metadata) => _writer.WriteMetadata(metadata);

    /// <summary>写入一段 PCM，按 1024 样本对齐后编码</summary>
    /// <param name="pcm">16 位小端交错 PCM</param>
    public void WritePcm(ReadOnlySpan<Byte> pcm)
    {
        while (pcm.Length > 0)
        {
            var take = Math.Min(_frameBytes - _count, pcm.Length);
            pcm.Slice(0, take).CopyTo(_block.AsSpan(_count));
            _count += take;
            pcm = pcm.Slice(take);

            if (_count == _frameBytes) EncodeBlock();
        }
    }

    /// <summary>写入一段 PCM（与 <see cref="WritePcm"/> 等价）</summary>
    /// <param name="frame">PCM 数据</param>
    public void WriteFrame(ReadOnlySpan<Byte> frame) => WritePcm(frame);

    /// <summary>冲刷不足一帧的尾部并收尾</summary>
    public void Flush()
    {
        if (_count > 0)
        {
            Array.Clear(_block, _count, _frameBytes - _count);
            EncodeBlock();
        }

        _writer.Flush();
    }

    /// <summary>释放写入器</summary>
    public void Dispose()
    {
        Flush();
        _writer.Dispose();
    }

    private void EncodeBlock()
    {
        var frame = _codec.FromPcm(_block, _options).GetSpan().ToArray();
        if (frame.Length > 0) _writer.WriteFrame(frame);

        _count = 0;
    }
}

/// <summary>AAC ADTS 写入器（裸码流，无容器头）</summary>
internal sealed class AdtsAacFileWriter : IAudioContainerWriter, IAudioPcmSink
{
    /// <summary>AAC 每帧样本数</summary>
    private const Int32 FrameSamples = 1024;

    private readonly Stream _stream;
    private readonly AacCodec _codec = new();
    private readonly AacEncoderOptions _options;
    private readonly Int32 _frameBytes;
    private readonly Byte[] _block;
    private Int32 _count;

    /// <summary>音频格式</summary>
    public AudioFormat Format { get; }

    /// <summary>构造 ADTS 写入器</summary>
    /// <param name="stream">输出流（由写入器负责关闭）</param>
    /// <param name="format">16 位 PCM 格式</param>
    /// <param name="bitrate">目标码率（bps）</param>
    public AdtsAacFileWriter(Stream stream, AudioFormat format, Int32 bitrate)
    {
        _stream = stream;
        Format = new AudioFormat
        {
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            BitsPerSample = 16,
            Encoding = AVTypes.AACLC,
            SamplesPerFrame = FrameSamples,
        };

        _options = new AacEncoderOptions
        {
            SampleRate = Format.SampleRate,
            Channels = Format.Channels,
            Bitrate = bitrate > 0 ? bitrate : 64000,
            Adts = true,
        };
        _options.Validate();

        _frameBytes = FrameSamples * Format.Channels * 2;
        _block = new Byte[_frameBytes];
    }

    /// <summary>ADTS 无容器级元数据，忽略</summary>
    /// <param name="metadata">音频元数据</param>
    public void WriteMetadata(AudioMetadata metadata) { }

    /// <summary>写入一段 PCM，按 1024 样本对齐后编码</summary>
    /// <param name="pcm">16 位小端交错 PCM</param>
    public void WritePcm(ReadOnlySpan<Byte> pcm)
    {
        while (pcm.Length > 0)
        {
            var take = Math.Min(_frameBytes - _count, pcm.Length);
            pcm.Slice(0, take).CopyTo(_block.AsSpan(_count));
            _count += take;
            pcm = pcm.Slice(take);

            if (_count == _frameBytes) EncodeBlock();
        }
    }

    /// <summary>写入一段 PCM（与 <see cref="WritePcm"/> 等价）</summary>
    /// <param name="frame">PCM 数据</param>
    public void WriteFrame(ReadOnlySpan<Byte> frame) => WritePcm(frame);

    /// <summary>冲刷不足一帧的尾部并收尾</summary>
    public void Flush()
    {
        if (_count > 0)
        {
            Array.Clear(_block, _count, _frameBytes - _count);
            EncodeBlock();
        }

        _stream.Flush();
    }

    /// <summary>释放写入器</summary>
    public void Dispose()
    {
        Flush();
        _stream.Dispose();
    }

    private void EncodeBlock()
    {
        var frame = _codec.FromPcm(_block, _options).GetSpan().ToArray();
        if (frame.Length > 0) _stream.Write(frame, 0, frame.Length);

        _count = 0;
    }
}
