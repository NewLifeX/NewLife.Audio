using NewLife.Buffers;

namespace NewLife.Audio.Writers;

/// <summary>OGG Opus 音频流写入器。将裸 Opus 包封装为标准 OGG 容器页（RFC 7845 + RFC 3533），使浏览器 MSE 可流式解码</summary>
/// <remarks>
/// 输出顺序: ID 头页(OpusHead) → 注释头页(OpusTags) → N 个音频数据页 → EOS 尾页。<br/>
/// 每页含一个 Opus 包（~80 字节 @32kbps），延迟优先。<br/>
/// 前置条件: 每个输入 chunk 即一个完整 Opus 包（如 DashScope CosyVoice WebSocket 流式返回的 binary frame）。
/// </remarks>
public sealed class OggOpusStreamWriter : AudioStreamWriter
{
    /// <summary>Opus 每帧采样数（20ms @48kHz）</summary>
    private const Int32 SamplesPerFrame = 960;

    /// <summary>解码器预跳采样数（80ms @48kHz），Opus 规范固定值</summary>
    private const Int32 PreSkip = 3840;

    /// <summary>OGG 流序列号（随机生成，标识一个逻辑流）</summary>
    private readonly UInt32 _serialNo;

    /// <summary>当前 OGG 页序号（从 0 递增）</summary>
    private UInt32 _pageSeq;

    /// <summary>已完成的 Opus 包计数（用于计算 granule position）</summary>
    private Int32 _packetCount;

    /// <summary>是否已写入头页</summary>
    private Boolean _headerWritten;

    /// <summary>是否已结束</summary>
    private Boolean _completed;

    /// <summary>厂商字符串（写入 OpusTags 注释包）</summary>
    private readonly String _vendor;

    /// <summary>线程安全的随机数生成器</summary>
    private static readonly Random _rnd = new();

    /// <summary>初始化 OGG Opus 写入器</summary>
    /// <param name="vendor">厂商字符串，默认 "NewLife.Audio"</param>
    public OggOpusStreamWriter(String vendor = "NewLife.Audio")
    {
        _vendor = vendor;
        _serialNo = (UInt32)_rnd.Next();
    }

    /// <summary>audio/ogg; codecs=opus</summary>
    public override String ContentType => "audio/ogg; codecs=opus";

    /// <summary>写入 OGG 标识头页（OpusHead）和注释头页（OpusTags）</summary>
    /// <inheritdoc />
    public override async ValueTask WriteHeaderAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (_headerWritten) return;

        // 第 0 页：OpusHead（BOS）
        var opusHead = BuildOpusHeadPacket();
        var page0 = BuildOggPage(opusHead, granulePosition: 0, headerType: 0x02); // BOS

        // 第 1 页：OpusTags
        var opusTags = BuildOpusTagsPacket(_vendor);
        var page1 = BuildOggPage(opusTags, granulePosition: 0, headerType: 0x00);

        // 两个 header 页合并为一次写入，确保前端 MSE 在同一个 appendBuffer 中收到完整初始化段
        var headerBytes = new Byte[page0.Length + page1.Length];
        page0.AsSpan().CopyTo(headerBytes.AsSpan());
        page1.AsSpan().CopyTo(headerBytes.AsSpan(page0.Length));
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        _headerWritten = true;
    }

    /// <summary>将一个裸 Opus 包封装为 OGG 音频数据页并写入流</summary>
    /// <inheritdoc />
    public override async ValueTask WriteChunkAsync(Stream stream, ReadOnlyMemory<Byte> chunk, CancellationToken cancellationToken = default)
    {
        if (_completed) return;

        if (!_headerWritten)
            await WriteHeaderAsync(stream, cancellationToken).ConfigureAwait(false);

        _packetCount++;
        // granule position = pre_skip + 已完成包数 × 每帧采样数
        var granule = PreSkip + (Int64)_packetCount * SamplesPerFrame;

        var page = BuildOggPage(chunk, granulePosition: granule, headerType: 0x00);
        await stream.WriteAsync(page, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>写入 EOS（End of Stream）尾页</summary>
    /// <inheritdoc />
    public override async ValueTask WriteTrailerAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (_completed) return;
        _completed = true;

        if (!_headerWritten)
            await WriteHeaderAsync(stream, cancellationToken).ConfigureAwait(false);

        // EOS 页：空负载，headerType = 0x04，granule 保持最后值
        var granule = PreSkip + (Int64)_packetCount * SamplesPerFrame;
        var eosPage = BuildOggPage(ReadOnlyMemory<Byte>.Empty, granulePosition: granule, headerType: 0x04);
        await stream.WriteAsync(eosPage, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    #region OGG 页构建

    /// <summary>构建一个 OGG 页</summary>
    /// <param name="payload">负载数据（OpusHead / OpusTags / 音频数据）</param>
    /// <param name="granulePosition">granule position（音频样本时间戳）</param>
    /// <param name="headerType">页类型标志: 0x00=普通, 0x02=BOS, 0x04=EOS</param>
    /// <returns>完整 OGG 页字节数组</returns>
    /// <remarks>
    /// OGG 页头格式（27 字节 + 段表）:<br/>
    /// [OggS][ver][flags][granule:8][serial:4][seq:4][crc:4][segs:1][seg_table:N][payload]
    /// </remarks>
    private Byte[] BuildOggPage(ReadOnlyMemory<Byte> payload, Int64 granulePosition, Byte headerType)
    {
        var payloadLen = payload.Length;
        // 计算段表：每段最多 255 字节
        var fullSegments = payloadLen / 255;
        var lastSegmentSize = payloadLen % 255;
        var segmentCount = fullSegments + (lastSegmentSize > 0 ? 1 : 0);

        // 边界情况：payload 为空时仍需 1 个段（长度为 0）
        if (segmentCount == 0) segmentCount = 1;

        var headerSize = 27 + segmentCount; // 27 字节固定头 + 段表
        var page = new Byte[headerSize + payloadLen];

        var writer = new SpanWriter(page.AsSpan()); // OGG 是小端序，SpanWriter 默认 LE

        // 0-3: 魔术字 "OggS"（0x5367674F LE → 'O','g','g','S'）
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
        // 如果 payload 为空，段表写入一个长度为 0 的段
        if (payloadLen == 0)
            writer.WriteByte(0);

        // --- 拷贝负载 ---
        if (payloadLen > 0)
            payload.CopyTo(page.AsMemory(writer.Position));

        // --- 计算并回填 CRC32 ---
        var crc = OggCrc32.Compute(page);
        var crcWriter = new SpanWriter(page.AsSpan(crcOffset));
        crcWriter.Write(crc);

        return page;
    }

    #endregion

    #region Opus 包构建

    /// <summary>构建 OpusHead 标识包（19 字节）</summary>
    private static Byte[] BuildOpusHeadPacket()
    {
        var packet = new Byte[19];
        var writer = new SpanWriter(packet.AsSpan());
        // "OpusHead"
        writer.Write(0x7375704Fu);       // "Opus"
        writer.Write(0x64616548u);       // "Head"
        // Version: 1
        writer.WriteByte(1);
        // Channel count: 1（单声道）
        writer.WriteByte(1);
        // Pre-skip: 3840 (2 bytes LE)
        writer.Write((Int16)PreSkip);
        // Input sample rate: 48000 (4 bytes LE)
        writer.Write(48000);
        // Output gain: 0 (2 bytes LE)
        writer.Write((Int16)0);
        // Channel mapping family: 0
        writer.WriteByte(0);
        return packet;
    }

    /// <summary>构建 OpusTags 注释包</summary>
    /// <param name="vendor">厂商字符串</param>
    private static Byte[] BuildOpusTagsPacket(String vendor)
    {
        var vendorBytes = System.Text.Encoding.UTF8.GetBytes(vendor);
        var packetLen = 8 + 4 + vendorBytes.Length + 4; // "OpusTags" + vendorLen + vendor + userCommentsLen(0)
        var packet = new Byte[packetLen];
        var writer = new SpanWriter(packet.AsSpan());
        // "OpusTags"
        writer.Write(0x7375704Fu);       // "Opus"
        writer.Write(0x73676154u);       // "Tags"
        // Vendor string length + vendor string
        writer.Write(vendorBytes.Length);
        vendorBytes.AsSpan().CopyTo(writer.GetSpan(vendorBytes.Length));
        writer.Advance(vendorBytes.Length);
        // User comment list length: 0
        writer.Write(0);
        return packet;
    }

    #endregion
}
