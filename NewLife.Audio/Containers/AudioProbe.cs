using NewLife;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Containers;

/// <summary>音频容器类型</summary>
public enum AudioContainerKind
{
    /// <summary>无法识别</summary>
    Unknown = 0,

    /// <summary>RIFF/WAVE</summary>
    Wav = 1,

    /// <summary>RF64（64 位 WAVE）</summary>
    Rf64 = 2,

    /// <summary>Ogg（Opus/Vorbis）</summary>
    Ogg = 3,

    /// <summary>原生 FLAC</summary>
    Flac = 4,

    /// <summary>MP3 码流（含 ID3v2）</summary>
    Mp3 = 5,

    /// <summary>ADTS AAC 码流</summary>
    Adts = 6,

    /// <summary>MP4/M4A（ISO BMFF）</summary>
    Mp4 = 7,

    /// <summary>无头原始 PCM</summary>
    RawPcm = 8,
}

/// <summary>音频流探测结果</summary>
public class AudioStreamInfo
{
    /// <summary>容器类型</summary>
    public AudioContainerKind Container { get; set; }

    /// <summary>编码类型</summary>
    public AVTypes Codec { get; set; }

    /// <summary>音频格式（采样率/声道/位深）</summary>
    public AudioFormat Format { get; set; }

    /// <summary>总时长（秒）；无法确定时为 0</summary>
    public Double Duration { get; set; }

    /// <summary>平均码率（bps）；无法确定时为 0</summary>
    public Int64 Bitrate { get; set; }

    /// <summary>元数据</summary>
    public AudioMetadata Metadata { get; set; }

    /// <summary>数据流是否可定位</summary>
    public Boolean CanSeek { get; set; }

    /// <summary>数据流长度（字节）；不可定位时为 -1</summary>
    public Int64 Length { get; set; }

    /// <summary>识别置信度 0~1</summary>
    public Double Confidence { get; set; }

    /// <summary>识别依据说明</summary>
    public String Description { get; set; }
}

/// <summary>音频容器探测。按魔术字节识别容器与编码，不根据扩展名猜测</summary>
/// <remarks>
/// 支持 WAV/RF64、Ogg（Opus/Vorbis）、FLAC、MP3、ADTS AAC、MP4/M4A；无法识别时返回
/// <see cref="AudioContainerKind.Unknown"/> 且置信度为 0，而不是退化为"原始 PCM"。<br/>
/// 探测过程不会消费流：可定位流会复位到探测前位置；不可定位流只能做头部识别。
/// </remarks>
/// <example>
/// <code>
/// var info = AudioProbe.Probe("song.flac");
/// Console.WriteLine($"{info.Container} {info.Codec} {info.Duration:F1}s {info.Bitrate / 1000}kbps");
/// </code>
/// </example>
public static class AudioProbe
{
    #region 入口
    /// <summary>探测文件</summary>
    /// <param name="path">文件路径</param>
    /// <returns>探测结果</returns>
    public static AudioStreamInfo Probe(String path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));

        using var stream = File.OpenRead(path);
        return Probe(stream, false);
    }

    /// <summary>探测数据流</summary>
    /// <param name="stream">数据流</param>
    /// <param name="leaveOpen">是否保留流不关闭，默认 true</param>
    /// <returns>探测结果</returns>
    public static AudioStreamInfo Probe(Stream stream, Boolean leaveOpen = true)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));

        var start = stream.CanSeek ? stream.Position : 0;
        var header = new Byte[16];
        var read = ReadUpTo(stream, header, 0, header.Length);

        var info = new AudioStreamInfo
        {
            CanSeek = stream.CanSeek,
            Length = stream.CanSeek ? stream.Length - start : -1,
        };

        var kind = Detect(header, read, info);
        if (kind == AudioContainerKind.Unknown)
        {
            if (stream.CanSeek) stream.Seek(start, SeekOrigin.Begin);
            info.Metadata = new AudioMetadata();
            return info;
        }

        info.Container = kind;
        info.Confidence = 1.0;

        if (kind == AudioContainerKind.Adts)
        {
            FillAdts(stream, start, header, read, info);
            if (stream.CanSeek) stream.Seek(start, SeekOrigin.Begin);
            info.Metadata ??= new AudioMetadata();
            return info;
        }

        // 可定位流回到起点，供读取器从头解析
        if (stream.CanSeek) stream.Seek(start, SeekOrigin.Begin);

        IAudioContainerReader reader = null;
        try
        {
            reader = CreateReader(kind, stream, leaveOpen, header, read);
        }
        catch (Exception ex)
        {
            info.Confidence = 0.5;
            info.Description = ex.Message;
            info.Metadata = new AudioMetadata();
            if (stream.CanSeek) stream.Seek(start, SeekOrigin.Begin);
            return info;
        }

        if (reader != null)
        {
            try
            {
                info.Format = reader.Format?.Clone();
                info.Codec = reader.CodecType;
                info.Duration = reader.Duration;
                info.Metadata = reader.Metadata ?? new AudioMetadata();
            }
            catch (Exception ex)
            {
                info.Confidence = 0.5;
                info.Description = ex.Message;
                info.Metadata ??= new AudioMetadata();
            }
            finally
            {
                reader.Dispose();
            }
        }

        if (info.Duration > 0 && info.Length > 0) info.Bitrate = (Int64)(info.Length * 8 / info.Duration);

        if (stream.CanSeek) stream.Seek(start, SeekOrigin.Begin);

        info.Metadata ??= new AudioMetadata();
        return info;
    }
    #endregion

    #region 识别
    private static AudioContainerKind Detect(Byte[] header, Int32 read, AudioStreamInfo info)
    {
        if (read >= 12 && Is(header, 0, "RIFF") && Is(header, 8, "WAVE"))
        {
            info.Description = "RIFF/WAVE 标记";
            return AudioContainerKind.Wav;
        }

        if (read >= 12 && Is(header, 0, "RF64") && Is(header, 8, "WAVE"))
        {
            info.Description = "RF64/WAVE 标记";
            return AudioContainerKind.Rf64;
        }

        if (read >= 4 && Is(header, 0, "OggS"))
        {
            info.Description = "OggS 页标记";
            return AudioContainerKind.Ogg;
        }

        if (read >= 4 && Is(header, 0, "fLaC"))
        {
            info.Description = "fLaC 标记";
            return AudioContainerKind.Flac;
        }

        if (read >= 3 && Is(header, 0, "ID3"))
        {
            info.Description = "ID3v2 标签";
            return AudioContainerKind.Mp3;
        }

        if (read >= 8 && Is(header, 4, "ftyp"))
        {
            info.Description = "ISO BMFF ftyp";
            return AudioContainerKind.Mp4;
        }

        if (read >= 2 && header[0] == 0xFF)
        {
            // MPEG 音频帧同步：11 位 sync + layer=01(Layer III)
            if ((header[1] & 0xE0) == 0xE0 && (header[1] & 0x06) == 0x02)
            {
                info.Description = "MPEG 音频帧同步";
                return AudioContainerKind.Mp3;
            }

            // ADTS：12 位 sync + layer=00
            if ((header[1] & 0xF0) == 0xF0 && (header[1] & 0x06) == 0x00)
            {
                info.Description = "ADTS 帧同步";
                return AudioContainerKind.Adts;
            }
        }

        info.Description = "未识别的魔术字节";
        info.Confidence = 0;
        return AudioContainerKind.Unknown;
    }

    private static IAudioContainerReader CreateReader(AudioContainerKind kind, Stream stream, Boolean leaveOpen, Byte[] header, Int32 headerLength)
    {
        var source = leaveOpen && stream.CanSeek ? new NonClosingStream(stream) : stream;
        if (!stream.CanSeek)
        {
            // 不可定位流已经消费了头部字节，用前置缓冲还回去
            var prefix = new Byte[headerLength];
            Array.Copy(header, prefix, headerLength);
            source = new PrependStream(prefix, stream);
        }

        switch (kind)
        {
            case AudioContainerKind.Wav:
            case AudioContainerKind.Rf64:
                return new WaveFileReader(source);
            case AudioContainerKind.Ogg:
                return new OggFileReader(source);
            case AudioContainerKind.Flac:
                return new FlacContainerReader(source);
            case AudioContainerKind.Mp3:
                return new Mp3FileReader(source);
            case AudioContainerKind.Mp4:
                return new Mp4FileReader(source);
            default:
                return null;
        }
    }

    private static Int32 ReadUpTo(Stream stream, Byte[] buffer, Int32 offset, Int32 count)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, offset + total, count - total);
            if (read <= 0) break;
            total += read;
        }

        return total;
    }

    private static Boolean Is(Byte[] buffer, Int32 offset, String text)
    {
        if (offset + text.Length > buffer.Length) return false;

        for (var i = 0; i < text.Length; i++)
        {
            if (buffer[offset + i] != text[i]) return false;
        }

        return true;
    }

    /// <summary>解析 ADTS 头并统计帧数</summary>
    private static void FillAdts(Stream stream, Int64 start, Byte[] header, Int32 read, AudioStreamInfo info)
    {
        if (read < 7) return;

        var sampleRates = new[] { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };
        var sfIndex = (header[2] >> 2) & 0x0F;
        var channelConfig = ((header[2] & 0x01) << 2) | ((header[3] >> 6) & 0x03);
        var sampleRate = sfIndex < sampleRates.Length ? sampleRates[sfIndex] : 0;

        info.Codec = AVTypes.AAC;
        info.Format = new AudioFormat
        {
            SampleRate = sampleRate,
            Channels = channelConfig > 0 ? channelConfig : 2,
            BitsPerSample = 16,
            SamplesPerFrame = 1024,
            Encoding = AVTypes.AAC,
        };

        if (!stream.CanSeek) return;

        // 顺序遍历 ADTS 帧统计数量与总时长（上限 2 万帧，避免超大文件长时间探测）
        var position = start;
        var end = stream.Length;
        var frames = 0L;
        var buffer = new Byte[7];

        stream.Seek(start, SeekOrigin.Begin);
        while (position + 7 <= end && frames < 20000)
        {
            if (ReadUpTo(stream, buffer, 0, 7) < 7) break;
            if (buffer[0] != 0xFF || (buffer[1] & 0xF0) != 0xF0) break;

            var frameLength = ((buffer[3] & 0x03) << 11) | (buffer[4] << 3) | (buffer[5] >> 5);
            if (frameLength < 7 || position + frameLength > end) break;

            frames++;
            position += frameLength;
            stream.Seek(position, SeekOrigin.Begin);
        }

        if (frames > 0 && sampleRate > 0)
        {
            info.Duration = (Double)frames * 1024 / sampleRate;
            if (info.Length > 0) info.Bitrate = (Int64)(info.Length * 8 / info.Duration);
        }
    }
    #endregion

    #region 内部类型
    /// <summary>不关闭底层流的包装流</summary>
    private sealed class NonClosingStream : Stream
    {
        private readonly Stream _inner;

        public NonClosingStream(Stream inner) => _inner = inner;

        public override Boolean CanRead => _inner.CanRead;
        public override Boolean CanSeek => _inner.CanSeek;
        public override Boolean CanWrite => _inner.CanWrite;
        public override Int64 Length => _inner.Length;
        public override Int64 Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count) => _inner.Read(buffer, offset, count);
        public override Int64 Seek(Int64 offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(Int64 value) => _inner.SetLength(value);
        public override void Write(Byte[] buffer, Int32 offset, Int32 count) => _inner.Write(buffer, offset, count);
        protected override void Dispose(Boolean disposing) { }
    }

    /// <summary>把已读取的前缀字节重新拼回不可定位流的包装流</summary>
    private sealed class PrependStream : Stream
    {
        private readonly Byte[] _prefix;
        private readonly Stream _inner;
        private Int32 _offset;

        public PrependStream(Byte[] prefix, Stream inner)
        {
            _prefix = prefix;
            _inner = inner;
        }

        public override Boolean CanRead => true;
        public override Boolean CanSeek => false;
        public override Boolean CanWrite => false;
        public override Int64 Length => throw new NotSupportedException();
        public override Int64 Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Int64 Seek(Int64 offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(Int64 value) => throw new NotSupportedException();
        public override void Write(Byte[] buffer, Int32 offset, Int32 count) => throw new NotSupportedException();

        public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count)
        {
            if (_offset < _prefix.Length)
            {
                var n = Math.Min(count, _prefix.Length - _offset);
                Array.Copy(_prefix, _offset, buffer, offset, n);
                _offset += n;
                return n;
            }

            return _inner.Read(buffer, offset, count);
        }
    }
    #endregion
}
