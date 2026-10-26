using System.Text;
using NewLife.Buffers;
using NewLife.Data;
using NewLife.Audio.DSP;
using NewLife.Audio.Writers;

namespace NewLife.Audio.Containers;

/// <summary>OGG 文件读取器。解析 OGG 容器格式（RFC 3533）与 Ogg Opus（RFC 7845）</summary>
/// <remarks>
/// 自动识别 Opus / Vorbis 头包，跳过 OpusHead+OpusTags 或 Vorbis 三头包，
/// 按 OGG lacing 规则重组跨页数据包。Opus 解码输出固定 48 kHz。
/// </remarks>
public class OggFileReader : IAudioContainerReader
{
    private readonly Stream _stream;
    private readonly Queue<Byte[]> _packetQueue = new();
    private readonly MemoryStream _packetParts = new();
    private Int64 _currentFrame;
    private Int64 _lastGranule = -1;
    private UInt32 _serialNo;
    private String _vendor;
    private Byte[] _vorbisIdentification;
    private Byte[] _vorbisComment;
    private Byte[] _vorbisSetup;

    /// <summary>音频格式</summary>
    public AudioFormat Format { get; private set; }

    /// <summary>编码类型</summary>
    public AVTypes CodecType { get; private set; }

    /// <summary>总帧数。可定位流在构造时扫描得到；不可定位流在读到末尾后得到</summary>
    public Int64 TotalFrames { get; private set; }

    /// <summary>总时长（秒）。由最后一个 granule position 与 pre-skip 计算</summary>
    public Double Duration => _lastGranule > 0 && Format != null && Format.SampleRate > 0
        ? (Double)Math.Max(0, _lastGranule - PreSkip) / Format.SampleRate
        : 0;

    /// <summary>最后一个 granule position。未读完时可能为 -1 或当前进度</summary>
    public Int64 LastGranule => _lastGranule;

    /// <summary>按 granule 与 pre-skip 计算的总 PCM 样本数（每声道）</summary>
    public Int64 TotalSamples => _lastGranule > 0 ? Math.Max(0, _lastGranule - PreSkip) : 0;

    /// <summary>元数据</summary>
    public AudioMetadata Metadata { get; } = new();

    /// <summary>Opus 解码器预跳采样数（48 kHz 时间基）</summary>
    public Int32 PreSkip { get; private set; }

    /// <summary>OpusHead 中记录的原始输入采样率（仅作信息）</summary>
    public Int32 InputSampleRate { get; private set; }

    /// <summary>Opus 输出增益（Q7.8 dB）</summary>
    public Int32 OutputGain { get; private set; }

    /// <summary>Opus 声道映射族。0=单/双声道，1=Vorbis 映射，255=自定义</summary>
    public Int32 ChannelMappingFamily { get; private set; }

    /// <summary>OpusTags 厂商字符串</summary>
    public String Vendor => _vendor;

    /// <summary>Vorbis 标识头包（仅 Vorbis 流）</summary>
    public Byte[] VorbisIdentification => _vorbisIdentification;

    /// <summary>Vorbis 注释头包（仅 Vorbis 流）</summary>
    public Byte[] VorbisComment => _vorbisComment;

    /// <summary>Vorbis 设置头包（仅 Vorbis 流）</summary>
    public Byte[] VorbisSetup => _vorbisSetup;

    /// <summary>OGG 逻辑流序列号</summary>
    public UInt32 SerialNumber => _serialNo;

    /// <summary>当前已读取帧序号</summary>
    public Int64 CurrentFrame => _currentFrame;

    /// <summary>是否校验 OGG 页 CRC32。默认关闭，兼容历史流数据</summary>
    public Boolean ValidateCrc { get; set; }

    /// <summary>初始化 OGG 读取器</summary>
    /// <param name="stream">输入流</param>
    public OggFileReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        ReadHeaders();
        ScanTotal();
    }

    /// <summary>读取下一帧</summary>
    /// <returns>编码数据帧，null 表示结束</returns>
    public IPacket ReadFrame()
    {
        var packet = DequeuePacket();
        if (packet == null)
        {
            TotalFrames = _currentFrame;
            return null;
        }

        _currentFrame++;
        return new ArrayPacket(packet);
    }

    /// <summary>定位到指定帧</summary>
    /// <param name="frameIndex">帧索引</param>
    public void SeekFrame(Int64 frameIndex)
    {
        if (frameIndex < 0) throw new ArgumentOutOfRangeException(nameof(frameIndex));
        if (!_stream.CanSeek) throw new NotSupportedException(L.T("The OGG stream does not support seeking."));

        _stream.Seek(0, SeekOrigin.Begin);
        ResetState();
        ReadHeaders();

        for (var i = 0; i < frameIndex; i++)
        {
            if (ReadFrame() == null) break;
        }
    }

    /// <summary>释放</summary>
    public void Dispose() => _stream?.Dispose();

    #region 头包解析
    private void ReadHeaders()
    {
        var first = PeekPacket();
        if (first == null) throw new InvalidDataException(L.T("Unable to read an OGG page."));

        if (IsOpusHead(first))
        {
            CodecType = AVTypes.Opus;
            ParseOpusHead(first);
            DequeuePacket();

            // 第二个包通常是 OpusTags；若不是，则按音频数据保留
            if (PeekPacket() is Byte[] tags && IsOpusTags(tags))
            {
                ParseOpusTags(tags);
                DequeuePacket();
            }

            return;
        }

        if (IsVorbisIdentification(first))
        {
            CodecType = AVTypes.Vorbis;
            _vorbisIdentification = first;
            ParseVorbisIdentification(first);
            DequeuePacket();

            if (PeekPacket() is Byte[] comment && IsVorbisComment(comment))
            {
                _vorbisComment = comment;
                DequeuePacket();
            }
            if (PeekPacket() is Byte[] setup && IsVorbisSetup(setup))
            {
                _vorbisSetup = setup;
                DequeuePacket();
            }

            return;
        }

        // 未知 OGG：所有包按音频数据返回
        CodecType = AVTypes.Transparent;
        Format = AudioFormat.Default;
    }

    private void ParseOpusHead(Byte[] head)
    {
        if (head.Length < 19) throw new InvalidDataException(L.T("The OpusHead packet is too short."));

        var channels = head[9];
        if (channels < 1) channels = 1; // 兼容历史/最小测试头，缺省为单声道

        PreSkip = head[10] | head[11] << 8;
        InputSampleRate = (Int32)(head[12] | (UInt32)head[13] << 8 | (UInt32)head[14] << 16 | (UInt32)head[15] << 24);
        OutputGain = (Int16)(head[16] | head[17] << 8);
        ChannelMappingFamily = head[18];

        if (ChannelMappingFamily != 0 && head.Length < 21 + channels)
            throw new InvalidDataException(L.T("The OpusHead channel mapping table is incomplete."));

        Format = new AudioFormat
        {
            SampleRate = 48000,
            Channels = channels,
            BitsPerSample = 16,
            Encoding = AVTypes.Opus,
            SamplesPerFrame = 960,
        };
    }

    private void ParseOpusTags(Byte[] tags)
    {
        if (tags.Length < 16) return;

        var offset = 12;
        var vendorLength = ReadUInt32(tags, 8);
        if (vendorLength > tags.Length - offset) return;

        _vendor = Encoding.UTF8.GetString(tags, offset, (Int32)vendorLength);
        offset += (Int32)vendorLength;

        if (offset + 4 > tags.Length) return;

        var count = ReadUInt32(tags, offset);
        offset += 4;
        for (var i = 0; i < count && offset + 4 <= tags.Length; i++)
        {
            var length = ReadUInt32(tags, offset);
            offset += 4;
            if (length > tags.Length - offset) break;

            var comment = Encoding.UTF8.GetString(tags, offset, (Int32)length);
            offset += (Int32)length;

            var index = comment.IndexOf('=');
            if (index <= 0) continue;

            var key = comment.Substring(0, index).ToUpperInvariant();
            var value = comment.Substring(index + 1);
            switch (key)
            {
                case "TITLE": Metadata.Title = value; break;
                case "ARTIST": Metadata.Artist = value; break;
                case "ALBUM": Metadata.Album = value; break;
                case "GENRE": Metadata.Genre = value; break;
                case "TRACKNUMBER": if (Int32.TryParse(value, out var track)) Metadata.TrackNumber = track; break;
                case "DATE": if (Int32.TryParse(value.Length >= 4 ? value.Substring(0, 4) : value, out var year)) Metadata.Year = year; break;
                case "COMMENT": Metadata.Comment = value; break;
            }
        }
    }

    private void ParseVorbisIdentification(Byte[] id)
    {
        if (id.Length < 30) return;

        var channels = id[11];
        var sampleRate = (Int32)(id[12] | (UInt32)id[13] << 8 | (UInt32)id[14] << 16 | (UInt32)id[15] << 24);

        Format = new AudioFormat
        {
            SampleRate = sampleRate > 0 ? sampleRate : 8000,
            Channels = channels > 0 ? channels : 1,
            BitsPerSample = 16,
            Encoding = AVTypes.Vorbis,
            SamplesPerFrame = 0,
        };
    }
    #endregion

    #region 包读取
    private Byte[] PeekPacket()
    {
        while (_packetQueue.Count == 0)
        {
            if (!ReadPagePackets()) return null;
        }

        return _packetQueue.Peek();
    }

    private Byte[] DequeuePacket()
    {
        var packet = PeekPacket();
        if (packet != null) _packetQueue.Dequeue();
        return packet;
    }

    private Boolean ReadPagePackets()
    {
        var page = ReadOggPage();
        if (page == null) return false;

        foreach (var packet in page.Value.Packets)
        {
            if (packet.Length > 0) _packetQueue.Enqueue(packet);
        }

        return true;
    }

    private void ResetState()
    {
        _packetQueue.Clear();
        _packetParts.SetLength(0);
        _serialNo = 0;
        _currentFrame = 0;
    }

    /// <summary>可定位流扫描全文件，统计音频包数与最后 granule，供时长/进度/定位使用</summary>
    private void ScanTotal()
    {
        if (_stream == null || !_stream.CanSeek) return;

        var packetCount = _packetQueue.Count;
        while (true)
        {
            var page = ReadOggPage();
            if (page == null) break;

            foreach (var packet in page.Value.Packets)
            {
                if (packet.Length > 0) packetCount++;
            }
        }

        TotalFrames = packetCount;

        _stream.Seek(0, SeekOrigin.Begin);
        ResetState();
        ReadHeaders();
    }

    private (Byte[][] Packets, Int64 Granule, Byte Flags)? ReadOggPage()
    {
        // 读取 27 字节 OGG 页头
        var header = new Byte[27];
        if (!ReadExactly(header)) return null;

        var reader = new SpanReader(header.AsSpan()); // OGG 是小端序，SpanReader 默认 LE
        var magic = reader.ReadUInt32();
        if (magic != 0x5367674F) return null; // "OggS"

        var version = reader.ReadByte();
        var flags = reader.ReadByte();
        var granule = reader.ReadInt64();
        var serial = reader.ReadUInt32();
        _ = reader.ReadUInt32(); // page sequence
        var crc = reader.ReadUInt32();
        var segmentCount = reader.ReadByte();

        if (version != 0) return null;

        if (_serialNo == 0) _serialNo = serial;
        if (granule > _lastGranule) _lastGranule = granule;

        var segmentTable = new Byte[segmentCount];
        if (!ReadExactly(segmentTable)) return null;

        var payloadSize = 0;
        for (var i = 0; i < segmentCount; i++)
            payloadSize += segmentTable[i];

        var payload = new Byte[payloadSize];
        if (!ReadExactly(payload)) return null;

        if (ValidateCrc)
        {
            var page = new Byte[27 + segmentCount + payloadSize];
            header.CopyTo(page, 0);
            segmentTable.CopyTo(page, 27);
            payload.CopyTo(page, 27 + segmentCount);
            page[22] = page[23] = page[24] = page[25] = 0;

            if (OggCrc32.Compute(page) != crc) return null;
        }

        // 按 lacing 规则重组数据包：255 表示包未结束，<255 表示包结束
        var packets = new List<Byte[]>();
        var offset = 0;
        foreach (var segment in segmentTable)
        {
            if (segment > 0) _packetParts.Write(payload, offset, segment);
            offset += segment;

            if (segment < 255)
            {
                packets.Add(_packetParts.ToArray());
                _packetParts.SetLength(0);
            }
        }

        return (packets.ToArray(), granule, flags);
    }

    private Boolean ReadExactly(Byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = _stream.Read(buffer, offset, buffer.Length - offset);
            if (count <= 0) return false;
            offset += count;
        }

        return true;
    }
    #endregion

    #region 辅助
    private static UInt32 ReadUInt32(Byte[] data, Int32 offset) =>
        (UInt32)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);

    private static Boolean StartsWith(Byte[] data, Int32 offset, String text)
    {
        if (data.Length < offset + text.Length) return false;

        for (var i = 0; i < text.Length; i++)
        {
            if (data[offset + i] != (Byte)text[i]) return false;
        }

        return true;
    }

    private static Boolean IsOpusHead(Byte[] packet) => StartsWith(packet, 0, "OpusHead");

    private static Boolean IsOpusTags(Byte[] packet) => StartsWith(packet, 0, "OpusTags");

    private static Boolean IsVorbisIdentification(Byte[] packet) => packet.Length >= 7 && packet[0] == 1 && StartsWith(packet, 1, "vorbis");

    private static Boolean IsVorbisComment(Byte[] packet) => packet.Length >= 7 && packet[0] == 3 && StartsWith(packet, 1, "vorbis");

    private static Boolean IsVorbisSetup(Byte[] packet) => packet.Length >= 7 && packet[0] == 5 && StartsWith(packet, 1, "vorbis");
    #endregion
}