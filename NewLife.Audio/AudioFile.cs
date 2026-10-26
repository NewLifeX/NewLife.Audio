using NewLife;
using NewLife.Audio.Codecs;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using NewLife.Data;

namespace NewLife.Audio;

/// <summary>解码后的 PCM 音频</summary>
public class AudioPcm
{
    /// <summary>音频格式（16 位小端交错）</summary>
    public AudioFormat Format { get; set; }

    /// <summary>原始编码类型</summary>
    public AVTypes Codec { get; set; }

    /// <summary>PCM 数据（16 位小端交错）</summary>
    public Byte[] Data { get; set; } = [];

    /// <summary>时长（秒）</summary>
    public Double Duration
    {
        get
        {
            if (Format == null || Format.SampleRate <= 0 || Format.Channels <= 0) return 0;

            return (Double)(Data.Length / 2 / Format.Channels) / Format.SampleRate;
        }
    }
}

/// <summary>音频写入选项</summary>
public class AudioWriteOptions
{
    /// <summary>MP3 编码码率（kbps），默认 128</summary>
    public Int32 Mp3BitrateKbps { get; set; } = 128;

    /// <summary>FLAC 压缩级别 0~8，默认 5</summary>
    public Int32 FlacCompressionLevel { get; set; } = 5;

    /// <summary>元数据（写入支持标签的容器）</summary>
    public AudioMetadata Metadata { get; set; }
}

/// <summary>音频文件门面。自动识别容器与编码，一条链完成"打开 → 解码 → 处理 → 保存"</summary>
/// <remarks>
/// 读取支持 WAV/RF64（PCM/浮点/G.711，24 位降为 16 位）、FLAC、MP3、ADTS AAC、MP4/M4A（AAC）、
/// Ogg（Opus 逐包 / Vorbis 整文件）。<br/>
/// 写入按扩展名选择容器：`.wav` 直写 PCM、`.flac` 无损压缩、`.mp3` 调用托管编码器；
/// 其余容器（AAC/Opus 编码容器）尚未提供托管编码路径，会明确抛出 <see cref="NotSupportedException"/>。
/// </remarks>
/// <example>
/// <code>
/// var audio = AudioFile.Open("input.m4a").ReadPcm();
/// var normalized = LoudnessNormalizer.Normalize(audio.Data, audio.Format);
/// AudioFile.WritePcm("output.wav", new AudioPcm { Format = audio.Format, Data = normalized });
/// </code>
/// </example>
public class AudioFile
{
    #region 属性
    /// <summary>文件路径</summary>
    public String Path { get; }

    /// <summary>探测信息</summary>
    public AudioStreamInfo Info { get; private set; }
    #endregion

    #region 构造
    /// <summary>打开音频文件（仅探测，不解码）</summary>
    /// <param name="path">文件路径</param>
    public AudioFile(String path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));

        Path = path;
        Info = AudioProbe.Probe(path);
    }
    #endregion

    #region 读取
    /// <summary>打开音频文件</summary>
    /// <param name="path">文件路径</param>
    /// <returns>音频文件门面</returns>
    public static AudioFile Open(String path) => new(path);

    /// <summary>探测文件</summary>
    /// <param name="path">文件路径</param>
    /// <returns>探测结果</returns>
    public static AudioStreamInfo Detect(String path) => AudioProbe.Probe(path);

    /// <summary>解码当前文件为 PCM</summary>
    /// <returns>解码结果</returns>
    public AudioPcm ReadPcm()
    {
        using var stream = File.OpenRead(Path);
        return ReadPcm(stream, Info);
    }

    /// <summary>读取并解码文件</summary>
    /// <param name="path">文件路径</param>
    /// <returns>解码结果</returns>
    public static AudioPcm ReadPcm(String path) => Open(path).ReadPcm();

    /// <summary>从数据流解码为 PCM</summary>
    /// <param name="stream">数据流</param>
    /// <param name="info">探测信息；为空时自动探测</param>
    /// <returns>解码结果</returns>
    /// <exception cref="NotSupportedException">容器或编码暂不支持解码</exception>
    public static AudioPcm ReadPcm(Stream stream, AudioStreamInfo info = null)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));

        info ??= AudioProbe.Probe(stream);
        if (stream.CanSeek) stream.Seek(0, SeekOrigin.Begin);

        return info.Container switch
        {
            AudioContainerKind.Wav or AudioContainerKind.Rf64 => ReadWave(stream, info),
            AudioContainerKind.Flac => ReadFlac(stream, info),
            AudioContainerKind.Mp3 => ReadMp3(stream, info),
            AudioContainerKind.Mp4 => ReadMp4(stream, info),
            AudioContainerKind.Adts => ReadAdts(stream, info),
            AudioContainerKind.Ogg => ReadOgg(stream, info),
            _ => throw new NotSupportedException(L.T("Unsupported container format: {0}.", info.Container)),
        };
    }

    /// <summary>解码当前文件并另存为</summary>
    /// <param name="path">目标路径（按扩展名选择容器）</param>
    /// <param name="options">写入选项</param>
    public void SaveAs(String path, AudioWriteOptions options = null) => WritePcm(path, ReadPcm(), options);
    #endregion

    #region 写入
    /// <summary>把 PCM 写入文件（按扩展名选择容器与编码）</summary>
    /// <param name="path">目标路径</param>
    /// <param name="audio">PCM 音频</param>
    /// <param name="options">写入选项</param>
    /// <exception cref="NotSupportedException">扩展名对应的容器没有可用编码路径</exception>
    public static void WritePcm(String path, AudioPcm audio, AudioWriteOptions options = null)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        if (audio == null) throw new ArgumentNullException(nameof(audio));

        options ??= new AudioWriteOptions();

        var format = audio.Format ?? throw new ArgumentException("Audio format is required.", nameof(audio));
        var extension = System.IO.Path.GetExtension(path)?.ToLowerInvariant();

        using var stream = File.Create(path);
        switch (extension)
        {
            case ".wav":
                {
                    using var writer = new WaveFileWriter(stream, format);
                    writer.WriteFrame(audio.Data);
                    writer.Flush();
                    break;
                }

            case ".flac":
                {
                    using var writer = new FlacFileWriter(stream, format, new FlacWriterOptions { CompressionLevel = options.FlacCompressionLevel });
                    if (options.Metadata != null) writer.WriteMetadata(options.Metadata);
                    writer.WriteFrame(audio.Data);
                    writer.Flush();
                    break;
                }

            case ".mp3":
                {
                    using var writer = new Mp3FileWriter(stream, format, options.Mp3BitrateKbps);
                    if (options.Metadata != null) writer.WriteMetadata(options.Metadata);
                    writer.WritePcm(audio.Data);
                    writer.Flush();
                    break;
                }

            default:
                throw new NotSupportedException(L.T("Unsupported container format: {0}.", extension));
        }
    }

    /// <summary>把 PCM 写入文件</summary>
    /// <param name="path">目标路径</param>
    /// <param name="pcm">16 位小端交错 PCM</param>
    /// <param name="format">音频格式</param>
    /// <param name="options">写入选项</param>
    public static void WritePcm(String path, AudioFormat format, Byte[] pcm, AudioWriteOptions options = null) =>
        WritePcm(path, new AudioPcm { Format = format, Data = pcm }, options);
    #endregion

    #region 各容器解码
    private static AudioPcm ReadWave(Stream stream, AudioStreamInfo info)
    {
        using var reader = new WaveFileReader(stream);
        var format = reader.Format;
        if (format.Encoding is not (AVTypes.LPCM or AVTypes.PCM_AUDIO or AVTypes.G711A or AVTypes.G711U))
            throw new NotSupportedException(L.T("Unsupported container format: {0}.", format.Encoding));

        using var output = new MemoryStream();
        while (reader.ReadFrame() is { } frame)
        {
            var data = ConvertWaveFrame(frame.GetSpan(), format, output);
            if (!data) throw new NotSupportedException(L.T("The requested audio format is not supported: {0}.", format.BitsPerSample));
        }

        return new AudioPcm
        {
            Format = new AudioFormat
            {
                SampleRate = format.SampleRate,
                Channels = format.Channels,
                BitsPerSample = 16,
                SamplesPerFrame = format.SamplesPerFrame,
            },
            Codec = format.Encoding,
            Data = output.ToArray(),
        };
    }

    private static Boolean ConvertWaveFrame(ReadOnlySpan<Byte> frame, AudioFormat format, Stream output)
    {
        switch (format.Encoding)
        {
            case AVTypes.G711A:
                {
                    var pcm = new G711ACodec().ToPcm(frame, null).GetSpan();
                    WriteSpan(output, pcm);
                    return true;
                }
            case AVTypes.G711U:
                {
                    var pcm = new G711UCodec().ToPcm(frame, null).GetSpan();
                    WriteSpan(output, pcm);
                    return true;
                }
        }

        if (format.IsFloat && format.BitsPerSample == 32)
        {
            for (var i = 0; i + 3 < frame.Length; i += 4)
            {
                var value = BitConverter.ToSingle(frame.Slice(i, 4).ToArray(), 0);
                var sample = (Int32)Math.Round(value * 32767);
                if (sample > Int16.MaxValue) sample = Int16.MaxValue;
                if (sample < Int16.MinValue) sample = Int16.MinValue;

                output.WriteByte((Byte)(sample & 0xFF));
                output.WriteByte((Byte)((sample >> 8) & 0xFF));
            }
            return true;
        }

        switch (format.BitsPerSample)
        {
            case 8:
                foreach (var b in frame)
                {
                    var sample = (b - 128) << 8;
                    output.WriteByte((Byte)(sample & 0xFF));
                    output.WriteByte((Byte)((sample >> 8) & 0xFF));
                }
                return true;

            case 16:
                WriteSpan(output, frame);
                return true;

            case 24:
                for (var i = 0; i + 2 < frame.Length; i += 3)
                {
                    output.WriteByte(frame[i + 1]);
                    output.WriteByte(frame[i + 2]);
                }
                return true;

            case 32:
                for (var i = 0; i + 3 < frame.Length; i += 4)
                {
                    output.WriteByte(frame[i + 2]);
                    output.WriteByte(frame[i + 3]);
                }
                return true;

            default:
                return false;
        }
    }

    private static AudioPcm ReadFlac(Stream stream, AudioStreamInfo info)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        var codec = new FlacCodec();
        var pcm = codec.ToPcm(memory.ToArray(), null).GetSpan().ToArray();

        return new AudioPcm
        {
            Format = new AudioFormat
            {
                SampleRate = info.Format?.SampleRate ?? (Int32)(codec.Info?.SampleRate ?? 0),
                Channels = info.Format?.Channels ?? codec.Info?.Channels ?? 1,
                BitsPerSample = 16,
            },
            Codec = AVTypes.LPCM,
            Data = pcm,
        };
    }

    private static AudioPcm ReadMp3(Stream stream, AudioStreamInfo info)
    {
        // 整文件解码，保证比特池（main_data_begin）跨帧可用
        var data = ReadAll(stream);
        var pcm = new Mp3Codec().ToPcm(data, null).GetSpan().ToArray();

        return new AudioPcm
        {
            Format = BuildFormat(info, 16),
            Codec = AVTypes.MP3,
            Data = pcm,
        };
    }

    private static AudioPcm ReadMp4(Stream stream, AudioStreamInfo info)
    {
        using var reader = new Mp4FileReader(stream);
        var codec = new AacCodec();
        using var output = new MemoryStream();

        while (reader.ReadFrame() is { } frame)
        {
            var pcm = codec.ToPcm(frame.GetSpan(), reader.AacConfig).GetSpan();
            WriteSpan(output, pcm);
        }

        return new AudioPcm
        {
            Format = BuildFormat(info, 16),
            Codec = AVTypes.AAC,
            Data = output.ToArray(),
        };
    }

    private static AudioPcm ReadOgg(Stream stream, AudioStreamInfo info)
    {
        if (info.Codec == AVTypes.Vorbis)
        {
            using var memory = new MemoryStream();
            stream.CopyTo(memory);

            using var vorbis = new VorbisCodec();
            var pcm = vorbis.ToPcm(memory.ToArray(), null).GetSpan().ToArray();
            return new AudioPcm
            {
                Format = BuildFormat(info, 16),
                Codec = AVTypes.Vorbis,
                Data = pcm,
            };
        }

        if (info.Codec != AVTypes.Opus) throw new NotSupportedException(L.T("Unsupported container format: {0}.", info.Codec));

        using var reader = new OggFileReader(stream);
        using var codec = new OpusCodec();
        using var output = new MemoryStream();

        while (reader.ReadFrame() is { } frame)
        {
            WriteSpan(output, codec.ToPcm(frame.GetSpan(), null).GetSpan());
        }

        return new AudioPcm
        {
            Format = BuildFormat(info, 16),
            Codec = AVTypes.Opus,
            Data = output.ToArray(),
        };
    }

    private static AudioPcm ReadAdts(Stream stream, AudioStreamInfo info)
    {
        var data = ReadAll(stream);
        var codec = new AacCodec();
        using var output = new MemoryStream();

        var offset = 0;
        while (offset + 7 <= data.Length)
        {
            if (data[offset] != 0xFF || (data[offset + 1] & 0xF0) != 0xF0) break;

            var frameLength = ((data[offset + 3] & 0x03) << 11) | (data[offset + 4] << 3) | (data[offset + 5] >> 5);
            if (frameLength < 7 || offset + frameLength > data.Length) break;

            var pcm = codec.ToPcm(data.AsSpan(offset, frameLength), null).GetSpan();
            WriteSpan(output, pcm);
            offset += frameLength;
        }

        return new AudioPcm
        {
            Format = BuildFormat(info, 16),
            Codec = AVTypes.AAC,
            Data = output.ToArray(),
        };
    }

    private static AudioFormat BuildFormat(AudioStreamInfo info, Int32 bitsPerSample) => new()
    {
        SampleRate = info.Format?.SampleRate > 0 ? info.Format.SampleRate : 44100,
        Channels = info.Format?.Channels > 0 ? info.Format.Channels : 1,
        BitsPerSample = bitsPerSample,
        SamplesPerFrame = info.Format?.SamplesPerFrame > 0 ? info.Format.SamplesPerFrame : 0,
    };

    private static Byte[] ReadAll(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return memory.ToArray();
    }

    /// <summary>跨目标框架写入 Span（netstandard2.0 无 Stream.Write(ReadOnlySpan)）</summary>
    private static void WriteSpan(Stream stream, ReadOnlySpan<Byte> data)
    {
        var bytes = data.ToArray();
        stream.Write(bytes, 0, bytes.Length);
    }
    #endregion
}

