using NewLife;
using NewLife.Audio.DSP;
using NewLife.Audio.Writers;
using NewLife.Audio.Codecs;
using NewLife.Buffers;

namespace NewLife.Audio.Containers;

/// <summary>OGG 文件写入器。实现 IAudioContainerWriter，将编码音频帧封装为 OGG 容器页（RFC 3533）</summary>
/// <remarks>
/// 支持 Opus 和 Vorbis 两种编码类型。<br/>
/// Opus 模式：自动写入 OpusHead + OpusTags 头页，每帧封装为独立 OGG 页。<br/>
/// Vorbis 模式：需通过 WriteVorbisHeaders 方法预先写入三个头包（Identification/Comment/Setup），再写入音频帧。
/// </remarks>
public class OggFileWriter : IAudioContainerWriter
{
    private readonly Stream _stream;
    private readonly AudioFormat _format;
    private readonly AVTypes _codecType;
    private readonly UInt32 _serialNo;
    private UInt32 _pageSeq;
    private Int64 _granulePosition;
    private Int32 _packetCount;
    private Boolean _headerWritten;
    private Boolean _completed;
    private Boolean _vorbis;
    private AudioMetadata _metadata;

    private static readonly Random _rnd = new();

    /// <summary>音频格式</summary>
    public AudioFormat Format => _format;

    /// <summary>Opus 解码器预跳采样数（48 kHz 时间基），默认 0；本地编码时建议设为 <see cref="NewLife.Audio.Codecs.OpusCodec.GetLookahead(NewLife.Audio.Codecs.OpusOptions)"/>，须在写入第一帧前设置</summary>
    public Int32 PreSkip { get; set; }

    /// <summary>已写入的音频帧数</summary>
    public Int32 PacketCount => _packetCount;

    /// <summary>初始化 OGG 写入器</summary>
    /// <param name="stream">输出流</param>
    /// <param name="format">音频格式</param>
    /// <param name="codecType">编码类型（Opus 或 Vorbis）</param>
    public OggFileWriter(Stream stream, AudioFormat format, AVTypes codecType)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _format = format ?? throw new ArgumentNullException(nameof(format));
        _codecType = codecType;
        _serialNo = (UInt32)_rnd.Next();
    }

    /// <summary>写入元数据（OGG 容器中映射到 VorbisComment / OpusTags）</summary>
    /// <param name="metadata">音频元数据，须在首帧写入前调用</param>
    public void WriteMetadata(AudioMetadata metadata)
    {
        // 头页延迟到首帧或 Flush 时写入，元数据可在构造后补充
        _metadata = metadata;
    }

    /// <summary>写入一帧编码数据</summary>
    /// <param name="frame">编码音频帧</param>
    public void WriteFrame(ReadOnlySpan<Byte> frame) => WriteFrame(frame, -1);

    /// <summary>写入一帧编码数据，并指定该帧解出的每声道样本数</summary>
    /// <param name="frame">编码音频帧</param>
    /// <param name="samplesPerFrame">每声道样本数。Vorbis 必填；Opus 传 0 时按 TOC 解析</param>
    public void WriteFrame(ReadOnlySpan<Byte> frame, Int32 samplesPerFrame)
    {
        if (_completed) return;

        if (!_headerWritten)
        {
            if (_codecType == AVTypes.Vorbis && !_vorbis)
                throw new InvalidOperationException(L.T("Vorbis headers must be written before audio frames."));
            WriteOggHeaders();
        }

        _packetCount++;
        var samples = samplesPerFrame >= 0
            ? samplesPerFrame
            : _vorbis
                ? 1024
                : (OpusPacketHeader.TryParse(frame, out var header) ? header.TotalSamples : 960);
        _granulePosition += samples;

        var page = BuildOggPage(frame, _granulePosition, headerType: 0x00);
        _stream.Write(page, 0, page.Length);
    }

    /// <summary>完成写入并刷新缓冲区</summary>
    public void Flush()
    {
        if (_completed) return;

        if (!_headerWritten) WriteOggHeaders();
        _completed = true;

        // 写入 EOS 页
        var eosPage = BuildOggPage([], _granulePosition, headerType: 0x04);
        _stream.Write(eosPage, 0, eosPage.Length);
        _stream.Flush();
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        if (!_completed)
        {
            try { Flush(); } catch { /* 流可能已关闭 */ }
        }
        _stream?.Dispose();
    }

    #region 头包

    private Boolean IsOpus => !_vorbis && (_codecType == AVTypes.Opus || _codecType == AVTypes.Transparent);

    private void WriteOggHeaders()
    {
        if (_headerWritten || _vorbis) return;

        if (IsOpus)
        {
            if (_format.Channels is not (1 or 2)) throw new NotSupportedException(L.T("Ogg Opus supports 1 or 2 channels."));
            if (PreSkip < 0 || PreSkip > 65535) throw new ArgumentOutOfRangeException(nameof(PreSkip), L.T("Opus pre-skip must be between 0 and 65535."));

            // Opus: OpusHead + OpusTags
            var opusHead = BuildOpusHeadPacket();
            var page0 = BuildOggPage(opusHead, granulePosition: 0, headerType: 0x02); // BOS
            _stream.Write(page0, 0, page0.Length);

            var opusTags = BuildOpusTagsPacket("NewLife.Audio", _metadata);
            var page1 = BuildOggPage(opusTags, granulePosition: 0, headerType: 0x00);
            _stream.Write(page1, 0, page1.Length);

            _granulePosition = PreSkip;
            _headerWritten = true;
        }
        else
        {
            // Vorbis: 需外部提供头包，调用方应使用 WriteVorbisHeaders
        }
    }

    /// <summary>写入 Vorbis 三个头包（Identification / Comment / Setup）</summary>
    /// <param name="identification">Vorbis 标识头包</param>
    /// <param name="comment">Vorbis 注释头包</param>
    /// <param name="setup">Vorbis 设置头包</param>
    public void WriteVorbisHeaders(Byte[] identification, Byte[] comment, Byte[] setup)
    {
        if (_headerWritten) return;

        _vorbis = true;

        // Identification 头页（BOS）
        var page0 = BuildOggPage(identification, granulePosition: 0, headerType: 0x02);
        _stream.Write(page0, 0, page0.Length);

        // Comment 与 Setup 各占一页，保证 OggFileReader 能把它们识别为两个独立头包
        var page1 = BuildOggPage(comment, granulePosition: 0, headerType: 0x00);
        _stream.Write(page1, 0, page1.Length);

        var page2 = BuildOggPage(setup, granulePosition: 0, headerType: 0x00);
        _stream.Write(page2, 0, page2.Length);

        _headerWritten = true;
    }

    #endregion

    #region OGG 页构建

    /// <summary>构建一个 OGG 页</summary>
    /// <param name="payload">负载数据</param>
    /// <param name="granulePosition">granule position（音频样本时间戳）</param>
    /// <param name="headerType">页类型标志: 0x00=普通, 0x02=BOS, 0x04=EOS</param>
    /// <returns>完整 OGG 页字节数组</returns>
    private Byte[] BuildOggPage(ReadOnlySpan<Byte> payload, Int64 granulePosition, Byte headerType)
    {
        var payloadLen = payload.Length;
        // 计算段表：每段最多 255 字节；整包长度是 255 的整数倍时，末尾仍需要一个 0 长度段作为包结束标记。
        // EOS 空页使用 0 个段（页面不含数据包），避免解码器把空包当成非法 Opus 包。
        var fullSegments = payloadLen / 255;
        var lastSegmentSize = payloadLen % 255;
        var segmentCount = payloadLen == 0 && headerType == 0x04 ? 0 : fullSegments + 1;

        var headerSize = 27 + segmentCount;
        var page = new Byte[headerSize + payloadLen];

        var writer = new SpanWriter(page.AsSpan());

        // 0-3: 魔术字 "OggS"
        writer.Write(0x5367674Fu);
        // 4: 版本 0
        writer.WriteByte(0);
        // 5: 头类型标志
        writer.WriteByte(headerType);
        // 6-13: granule position (Int64 LE)
        writer.Write(granulePosition);
        // 14-17: stream serial number (UInt32 LE)
        writer.Write(_serialNo);
        // 18-21: page sequence number (UInt32 LE)
        writer.Write(_pageSeq);
        _pageSeq++;
        // 22-25: CRC32 checksum（先填 0，后面回填）
        var crcOffset = writer.Position;
        writer.Write(0u);
        // 26: segment count
        writer.WriteByte((Byte)segmentCount);
        // 27+: segment table
        for (var i = 0; i < fullSegments; i++)
            writer.WriteByte(255);
        if (segmentCount > 0)
            writer.WriteByte((Byte)lastSegmentSize);

        // 拷贝负载
        if (payloadLen > 0)
            payload.CopyTo(page.AsSpan(writer.Position));

        // 计算并回填 CRC32
        var crc = OggCrc32.Compute(page);
        var crcWriter = new SpanWriter(page.AsSpan(crcOffset));
        crcWriter.Write(crc);

        return page;
    }

    #endregion

    #region Opus 包构建

    /// <summary>构建 OpusHead 标识包（19 字节）</summary>
    private Byte[] BuildOpusHeadPacket()
    {
        var packet = new Byte[19];
        var writer = new SpanWriter(packet.AsSpan());
        writer.Write(0x7375704Fu);              // "Opus"
        writer.Write(0x64616548u);              // "Head"
        writer.WriteByte(1);                    // Version: 1
        writer.WriteByte((Byte)_format.Channels); // Channels
        writer.Write((Int16)PreSkip);           // Pre-skip
        writer.Write(_format.SampleRate);       // Input sample rate
        writer.Write((Int16)0);                 // Output gain: 0
        writer.WriteByte(0);                    // Channel mapping family: 0
        return packet;
    }

    /// <summary>构建 OpusTags 注释包</summary>
    /// <param name="vendor">厂商字符串</param>
    /// <param name="metadata">音频元数据</param>
    private static Byte[] BuildOpusTagsPacket(String vendor, AudioMetadata metadata)
    {
        var comments = new List<String>();
        if (metadata != null)
        {
            if (!String.IsNullOrEmpty(metadata.Title)) comments.Add("TITLE=" + metadata.Title);
            if (!String.IsNullOrEmpty(metadata.Artist)) comments.Add("ARTIST=" + metadata.Artist);
            if (!String.IsNullOrEmpty(metadata.Album)) comments.Add("ALBUM=" + metadata.Album);
            if (!String.IsNullOrEmpty(metadata.Genre)) comments.Add("GENRE=" + metadata.Genre);
            if (metadata.TrackNumber > 0) comments.Add("TRACKNUMBER=" + metadata.TrackNumber);
            if (metadata.Year > 0) comments.Add("DATE=" + metadata.Year);
            if (!String.IsNullOrEmpty(metadata.Comment)) comments.Add("COMMENT=" + metadata.Comment);
        }

        var vendorBytes = System.Text.Encoding.UTF8.GetBytes(vendor);
        var commentBytes = new List<Byte[]>(comments.Count);
        var packetLen = 8 + 4 + vendorBytes.Length + 4;
        foreach (var comment in comments)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(comment);
            commentBytes.Add(bytes);
            packetLen += 4 + bytes.Length;
        }

        var packet = new Byte[packetLen];
        var writer = new SpanWriter(packet.AsSpan());
        writer.Write(0x7375704Fu);       // "Opus"
        writer.Write(0x73676154u);       // "Tags"
        writer.Write(vendorBytes.Length);
        vendorBytes.AsSpan().CopyTo(writer.GetSpan(vendorBytes.Length));
        writer.Advance(vendorBytes.Length);
        writer.Write(commentBytes.Count);
        foreach (var bytes in commentBytes)
        {
            writer.Write(bytes.Length);
            bytes.AsSpan().CopyTo(writer.GetSpan(bytes.Length));
            writer.Advance(bytes.Length);
        }
        return packet;
    }

    #endregion
}
