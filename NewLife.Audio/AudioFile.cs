using NewLife;
using NewLife.Audio.Codecs;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using NewLife.Data;

namespace NewLife.Audio;

/// <summary>解码后的 PCM 音频</summary>
public partial class AudioPcm
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

    /// <summary>未显式指定 <see cref="Metadata"/> 时，是否沿用源文件标签，默认 true</summary>
    public Boolean PreserveMetadata { get; set; } = true;

    /// <summary>编码参数一致时优先帧级复制（免重编码），默认 true</summary>
    /// <remarks>目标容器与源编码一致且不需要改变采样率/声道数时，直接搬运已编码帧；
    /// 需要改变码率或做格式转换时应设为 false 强制重编码。</remarks>
    public Boolean PreferFrameCopy { get; set; } = true;
}

/// <summary>音频保存结果</summary>
public class AudioSaveResult
{
    /// <summary>是否为帧级复制（免重编码）</summary>
    public Boolean FrameCopied { get; set; }

    /// <summary>是否重新编码</summary>
    public Boolean Reencoded { get; set; }

    /// <summary>目标容器</summary>
    public AudioContainerKind Container { get; set; }

    /// <summary>说明文字（走了哪条链路）</summary>
    public String Detail { get; set; }
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
            AudioContainerKind.Aiff => ReadAiff(stream, info),
            AudioContainerKind.Caf => ReadCaf(stream, info),
            _ => throw new NotSupportedException(L.T("Unsupported container format: {0}.", info.Container)),
        };
    }

    /// <summary>解码当前文件并另存为</summary>
    /// <param name="path">目标路径（按扩展名选择容器）</param>
    /// <param name="options">写入选项；未指定 <see cref="AudioWriteOptions.Metadata"/> 时默认沿用源文件标签</param>
    /// <returns>保存结果，说明走的是帧级复制还是重编码</returns>
    /// <remarks>目标容器与源编码一致且无需改变采样率/声道数时优先帧级复制（免重编码），
    /// 否则解码后重新编码；<paramref name="options"/> 的 <see cref="AudioWriteOptions.PreferFrameCopy"/> 可关闭该优化。
    /// 目标容器不支持标签（如 WAV）时标签被忽略。</remarks>
    public AudioSaveResult SaveAs(String path, AudioWriteOptions options = null)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));

        options ??= new AudioWriteOptions();
        if (options.Metadata == null && options.PreserveMetadata) options.Metadata = ReadMetadata();

        var target = AudioProbe.ProbeKind(path);
        if (options.PreferFrameCopy && TryCopyFrames(path, target, options))
            return new AudioSaveResult { FrameCopied = true, Container = target, Detail = "Frame copy" };

        WritePcm(path, ReadPcm(), options);

        return new AudioSaveResult { Reencoded = true, Container = target, Detail = "Decode and re-encode" };
    }

    /// <summary>帧级复制：容器与编码一致时直接搬运已编码数据，只替换标签</summary>
    /// <param name="path">目标路径</param>
    /// <param name="target">目标容器</param>
    /// <param name="options">写入选项</param>
    /// <returns>完成复制返回 true；不满足条件返回 false，由调用方回退重编码</returns>
    private Boolean TryCopyFrames(String path, AudioContainerKind target, AudioWriteOptions options)
    {
        if (target == AudioContainerKind.Unknown || Info.Container != target) return false;

        switch (target)
        {
            case AudioContainerKind.Mp3:
                {
                    var source = File.ReadAllBytes(Path);
                    var start = Mp3Id3Tag.Skip(source);
                    var end = source.Length;
                    if (end - start >= Mp3Id3Tag.V1Size
                        && source[end - Mp3Id3Tag.V1Size] == 'T' && source[end - Mp3Id3Tag.V1Size + 1] == 'A' && source[end - Mp3Id3Tag.V1Size + 2] == 'G')
                        end -= Mp3Id3Tag.V1Size;

                    var tag = options.Metadata == null ? [] : Mp3Id3Tag.Write(options.Metadata, 2048);
                    var output = new Byte[tag.Length + end - start];
                    Array.Copy(tag, 0, output, 0, tag.Length);
                    Array.Copy(source, start, output, tag.Length, end - start);

                    WriteAllBytesAtomic(path, output);
                    return true;
                }

            case AudioContainerKind.Wav:
                {
                    // 同为 16 位 PCM 且无需转换时直接整文件复制
                    if (Info.Codec is not (AVTypes.LPCM or AVTypes.PCM_AUDIO)) return false;
                    if (Info.Format?.BitsPerSample > 16) return false;
                    if (!String.Equals(System.IO.Path.GetFullPath(Path), System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                        WriteAllBytesAtomic(path, File.ReadAllBytes(Path));
                    return true;
                }

            case AudioContainerKind.Adts:
                {
                    WriteAllBytesAtomic(path, File.ReadAllBytes(Path));
                    return true;
                }

            default:
                return false;
        }
    }

    /// <summary>读取当前文件的标签（不解码音频）</summary>
    /// <returns>元数据；容器无标签或解析失败时返回 null</returns>
    public AudioMetadata ReadMetadata()
    {
        using var stream = File.OpenRead(Path);
        switch (Info.Container)
        {
            case AudioContainerKind.Wav:
            case AudioContainerKind.Rf64:
                {
                    using var reader = new WaveFileReader(stream);
                    return HasTags(reader.Metadata) ? reader.Metadata : null;
                }
            case AudioContainerKind.Mp3:
                {
                    using var reader = new Mp3FileReader(stream);
                    return HasTags(reader.Metadata) ? reader.Metadata : null;
                }
            case AudioContainerKind.Flac:
                {
                    using var reader = new FlacContainerReader(stream);
                    return HasTags(reader.Metadata) ? reader.Metadata : null;
                }
            case AudioContainerKind.Ogg:
                {
                    using var reader = new OggFileReader(stream);
                    return HasTags(reader.Metadata) ? reader.Metadata : null;
                }
            default:
                return null;
        }
    }

    /// <summary>只改标签不重编码（MP3/FLAC）</summary>
    /// <param name="path">目标文件路径</param>
    /// <param name="metadata">新标签</param>
    /// <exception cref="NotSupportedException">容器不支持仅改标签</exception>
    /// <remarks>MP3 重建前置 ID3v2 标签并丢弃尾部 ID3v1，FLAC 替换 VORBIS_COMMENT 块；音频帧原样保留。
    /// 写入采用临时文件 + 原子替换，失败时目标文件保持原状。</remarks>
    public static void UpdateMetadata(String path, AudioMetadata metadata)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));

        var info = AudioProbe.Probe(path);
        var data = File.ReadAllBytes(path);
        Byte[] output;
        switch (info.Container)
        {
            case AudioContainerKind.Mp3:
                {
                    var start = Mp3Id3Tag.Skip(data);
                    var end = data.Length;
                    if (end - start >= Mp3Id3Tag.V1Size && data[end - Mp3Id3Tag.V1Size] == 'T' && data[end - Mp3Id3Tag.V1Size + 1] == 'A' && data[end - Mp3Id3Tag.V1Size + 2] == 'G') end -= Mp3Id3Tag.V1Size;

                    var tag = metadata == null ? [] : Mp3Id3Tag.Write(metadata, 2048);
                    output = new Byte[tag.Length + end - start];
                    Array.Copy(tag, 0, output, 0, tag.Length);
                    Array.Copy(data, start, output, tag.Length, end - start);
                    break;
                }
            case AudioContainerKind.Flac:
                output = FlacMetadataBlocks.RewriteTag(data, metadata) ?? throw new InvalidOperationException("Invalid FLAC stream: " + path);
                break;
            default:
                throw new NotSupportedException(L.T("Container {0} does not support tag-only update.", info.Container));
        }

        WriteAllBytesAtomic(path, output);
    }
    #endregion

    #region 写入
    /// <summary>把 PCM 写入文件（按扩展名选择容器与编码）</summary>
    /// <param name="path">目标路径</param>
    /// <param name="audio">PCM 音频</param>
    /// <param name="options">写入选项</param>
    /// <exception cref="NotSupportedException">扩展名对应的容器没有可用编码路径</exception>
    /// <remarks>先写同目录临时文件，成功后再原子替换目标文件，避免中途失败留下损坏的半成品。</remarks>
    public static void WritePcm(String path, AudioPcm audio, AudioWriteOptions options = null)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        if (audio == null) throw new ArgumentNullException(nameof(audio));

        options ??= new AudioWriteOptions();

        var format = audio.Format ?? throw new ArgumentException("Audio format is required.", nameof(audio));
        var extension = System.IO.Path.GetExtension(path)?.ToLowerInvariant();

        WriteAtomic(path, stream =>
        {
            switch (extension)
            {
                case ".wav":
                    {
                        using var writer = new WaveFileWriter(stream, format);
                        if (options.Metadata != null) writer.WriteMetadata(options.Metadata);
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

                case ".aiff":
                case ".aif":
                case ".aifc":
                    {
                        using var writer = new AiffFileWriter(stream, format);
                        writer.WriteFrame(audio.Data);
                        writer.Flush();
                        break;
                    }

                default:
                    throw new NotSupportedException(L.T("Unsupported container format: {0}.", extension));
            }
        });
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

    private static AudioPcm ReadAiff(Stream stream, AudioStreamInfo info)
    {
        using var reader = new AiffFileReader(stream);
        using var output = new MemoryStream();
        while (true)
        {
            var packet = reader.ReadFrame();
            if (packet == null) break;
            WriteSpan(output, packet.GetSpan());
        }

        return new AudioPcm
        {
            Format = reader.Format,
            Codec = AVTypes.LPCM,
            Data = output.ToArray(),
        };
    }

    private static AudioPcm ReadCaf(Stream stream, AudioStreamInfo info)
    {
        using var reader = new CafFileReader(stream);
        using var output = new MemoryStream();
        while (true)
        {
            var packet = reader.ReadFrame();
            if (packet == null) break;
            WriteSpan(output, packet.GetSpan());
        }

        return new AudioPcm
        {
            Format = reader.Format,
            Codec = AVTypes.LPCM,
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

    /// <summary>写同目录临时文件后原子替换目标文件</summary>
    private static void WriteAtomic(String path, Action<Stream> write)
    {
        var full = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(full);
        var name = System.IO.Path.GetFileName(full);
        var temp = System.IO.Path.Combine(directory ?? ".", "." + name + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            using (var stream = File.Create(temp)) write(stream);
            ReplaceFile(temp, full);
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
    }

    /// <summary>原子写入整块字节</summary>
    private static void WriteAllBytesAtomic(String path, Byte[] data)
    {
        var full = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(full);
        var name = System.IO.Path.GetFileName(full);
        var temp = System.IO.Path.Combine(directory ?? ".", "." + name + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            File.WriteAllBytes(temp, data);
            ReplaceFile(temp, full);
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
    }

    private static void ReplaceFile(String temp, String target)
    {
        if (!File.Exists(target))
        {
            File.Move(temp, target);
            return;
        }

        // 已有目标文件时用 Replace 保证原子性（同卷 NTFS 上是单次元数据操作）
        try
        {
            File.Replace(temp, target, null);
        }
        catch (PlatformNotSupportedException)
        {
            File.Delete(target);
            File.Move(temp, target);
        }
    }

    private static void TryDeleteFile(String path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清理失败不影响原始异常
        }
    }

    private static Boolean HasTags(AudioMetadata metadata)
    {
        if (metadata == null) return false;

        return !metadata.Title.IsNullOrEmpty()
            || !metadata.Artist.IsNullOrEmpty()
            || !metadata.Album.IsNullOrEmpty()
            || !metadata.Genre.IsNullOrEmpty()
            || !metadata.Comment.IsNullOrEmpty()
            || metadata.TrackNumber > 0
            || metadata.Year > 0
            || metadata.CoverImage is { Length: > 0 };
    }
    #endregion
}


