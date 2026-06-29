using NewLife.Audio.DSP;
using NewLife.Audio.Writers;
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

    private static readonly Random _rnd = new();

    /// <summary>音频格式</summary>
    public AudioFormat Format => _format;

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

        WriteOggHeaders();
    }

    /// <summary>写入元数据（OGG 容器中映射到 VorbisComment / OpusTags）</summary>
    /// <param name="metadata">音频元数据</param>
    public void WriteMetadata(AudioMetadata metadata)
    {
        // OGG 容器元数据已通过头包写入，此处可扩展为追加 VorbisComment
    }

    /// <summary>写入一帧编码数据</summary>
    /// <param name="frame">编码音频帧</param>
    public void WriteFrame(ReadOnlySpan<Byte> frame)
    {
        if (_completed) return;

        if (!_headerWritten)
            WriteOggHeaders();

        _packetCount++;
        var page = BuildOggPage(frame.ToArray(), _granulePosition, headerType: 0x00);
        _stream.Write(page, 0, page.Length);

        // 更新 granule position（每帧增加 samplesPerFrame）
        var samplesPerFrame = _codecType == AVTypes.Transparent ? 960 : 1024; // Opus: 20ms@48k, Vorbis: 默认
        _granulePosition += samplesPerFrame;
    }

    /// <summary>完成写入并刷新缓冲区</summary>
    public void Flush()
    {
        if (_completed) return;
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

    private void WriteOggHeaders()
    {
        if (_headerWritten) return;

        if (_codecType == AVTypes.Transparent)
        {
            // Opus: OpusHead + OpusTags
            var opusHead = BuildOpusHeadPacket();
            var page0 = BuildOggPage(opusHead, granulePosition: 0, headerType: 0x02); // BOS
            _stream.Write(page0, 0, page0.Length);

            var opusTags = BuildOpusTagsPacket("NewLife.Audio");
            var page1 = BuildOggPage(opusTags, granulePosition: 0, headerType: 0x00);
            _stream.Write(page1, 0, page1.Length);
        }
        else
        {
            // Vorbis: 需外部提供头包，此处写入占位
            // 调用方应使用 WriteVorbisHeaders 方法写入实际头包
        }

        _headerWritten = true;
    }

    /// <summary>写入 Vorbis 三个头包（Identification / Comment / Setup）</summary>
    /// <param name="identification">Vorbis 标识头包</param>
    /// <param name="comment">Vorbis 注释头包</param>
    /// <param name="setup">Vorbis 设置头包</param>
    public void WriteVorbisHeaders(Byte[] identification, Byte[] comment, Byte[] setup)
    {
        if (_headerWritten) return;

        // Identification 头页（BOS）
        var page0 = BuildOggPage(identification, granulePosition: 0, headerType: 0x02);
        _stream.Write(page0, 0, page0.Length);

        // Comment + Setup 合并在一个页中（推荐做法）
        var combined = new Byte[comment.Length + setup.Length];
        comment.CopyTo(combined, 0);
        setup.CopyTo(combined, comment.Length);
        var page1 = BuildOggPage(combined, granulePosition: 0, headerType: 0x00);
        _stream.Write(page1, 0, page1.Length);

        _headerWritten = true;
    }

    #endregion

    #region OGG 页构建

    /// <summary>构建一个 OGG 页</summary>
    /// <param name="payload">负载数据</param>
    /// <param name="granulePosition">granule position（音频样本时间戳）</param>
    /// <param name="headerType">页类型标志: 0x00=普通, 0x02=BOS, 0x04=EOS</param>
    /// <returns>完整 OGG 页字节数组</returns>
    private Byte[] BuildOggPage(Byte[] payload, Int64 granulePosition, Byte headerType)
    {
        var payloadLen = payload.Length;
        // 计算段表：每段最多 255 字节
        var fullSegments = payloadLen / 255;
        var lastSegmentSize = payloadLen % 255;
        var segmentCount = fullSegments + (lastSegmentSize > 0 ? 1 : 0);

        // 边界情况：payload 为空时仍需 1 个段（长度为 0）
        if (segmentCount == 0) segmentCount = 1;

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
        if (lastSegmentSize > 0)
            writer.WriteByte((Byte)lastSegmentSize);
        if (payloadLen == 0)
            writer.WriteByte(0);

        // 拷贝负载
        if (payloadLen > 0)
            Array.Copy(payload, 0, page, writer.Position, payloadLen);

        // 计算并回填 CRC32
        var crc = OggCrc32.Compute(page);
        var crcWriter = new SpanWriter(page.AsSpan(crcOffset));
        crcWriter.Write(crc);

        return page;
    }

    #endregion

    #region Opus 包构建

    private const Int32 OpusSamplesPerFrame = 960;
    private const Int32 OpusPreSkip = 3840;

    /// <summary>构建 OpusHead 标识包（19 字节）</summary>
    private static Byte[] BuildOpusHeadPacket()
    {
        var packet = new Byte[19];
        var writer = new SpanWriter(packet.AsSpan());
        writer.Write(0x7375704Fu);       // "Opus"
        writer.Write(0x64616548u);       // "Head"
        writer.WriteByte(1);             // Version: 1
        writer.WriteByte(1);             // Channels: 1
        writer.Write((Int16)OpusPreSkip); // Pre-skip: 3840
        writer.Write(48000);             // Input sample rate: 48000
        writer.Write((Int16)0);          // Output gain: 0
        writer.WriteByte(0);             // Channel mapping family: 0
        return packet;
    }

    /// <summary>构建 OpusTags 注释包</summary>
    /// <param name="vendor">厂商字符串</param>
    private static Byte[] BuildOpusTagsPacket(String vendor)
    {
        var vendorBytes = System.Text.Encoding.UTF8.GetBytes(vendor);
        var packetLen = 8 + 4 + vendorBytes.Length + 4;
        var packet = new Byte[packetLen];
        var writer = new SpanWriter(packet.AsSpan());
        writer.Write(0x7375704Fu);       // "Opus"
        writer.Write(0x73676154u);       // "Tags"
        writer.Write(vendorBytes.Length);
        vendorBytes.AsSpan().CopyTo(writer.GetSpan(vendorBytes.Length));
        writer.Advance(vendorBytes.Length);
        writer.Write(0);                 // User comment list length: 0
        return packet;
    }

    #endregion
}
