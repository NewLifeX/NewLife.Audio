using NewLife;
using NewLife.Buffers;
using NewLife.Audio.Codecs;

namespace NewLife.Audio.Writers;

/// <summary>OGG Opus 音频流写入器。将裸 Opus 包封装为标准 OGG 容器页（RFC 7845 + RFC 3533），使浏览器 MSE 可流式解码</summary>
/// <remarks>
/// 输出顺序: ID 头页(OpusHead) → 注释头页(OpusTags) → N 个音频数据页 → EOS 尾页。<br/>
/// 每页含一个 Opus 包（~80 字节 @32kbps），延迟优先。<br/>
/// 前置条件: 每个输入 chunk 即一个完整 Opus 包（如 DashScope CosyVoice WebSocket 流式返回的 binary frame）。
/// </remarks>
public sealed class OggOpusStreamWriter : AudioStreamWriter
{
    /// <summary>Opus 每帧采样数（20ms @48kHz），仅用于无法解析 TOC 时的兜底</summary>
    private const Int32 SamplesPerFrame = 960;

    /// <summary>解码器预跳采样数默认值。外部已编码流未知编码器延迟时取 0；本地编码时使用 <see cref="NewLife.Audio.Codecs.OpusCodec.GetLookahead(NewLife.Audio.Codecs.OpusOptions)"/></summary>
    private const Int32 DefaultPreSkip = 0;

    /// <summary>OGG 流序列号（随机生成，标识一个逻辑流）</summary>
    private readonly UInt32 _serialNo;

    /// <summary>声道数</summary>
    private readonly Int32 _channels;

    /// <summary>原始输入采样率（写入 OpusHead，仅作信息）</summary>
    private readonly Int32 _sampleRate;

    /// <summary>解码器预跳采样数</summary>
    private readonly Int32 _preSkip;

    /// <summary>当前 OGG 页序号（从 0 递增）</summary>
    private UInt32 _pageSeq;

    /// <summary>已完成的 Opus 包计数</summary>
    private Int32 _packetCount;

    /// <summary>累计已写入的每声道样本数（48 kHz 时间基）</summary>
    private Int64 _totalSamples;

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
    public OggOpusStreamWriter(String vendor = "NewLife.Audio") : this(vendor, 1, 48000, DefaultPreSkip) { }

    /// <summary>初始化 OGG Opus 写入器</summary>
    /// <param name="vendor">厂商字符串</param>
    /// <param name="channels">声道数（1 或 2）</param>
    /// <param name="sampleRate">原始输入采样率（写入 OpusHead，仅作信息）</param>
    /// <param name="preSkip">解码器预跳采样数（48 kHz 时间基）。外部已编码流未知时取 0；本地编码时使用 <see cref="NewLife.Audio.Codecs.OpusCodec.GetLookahead(NewLife.Audio.Codecs.OpusOptions)"/></param>
    public OggOpusStreamWriter(String vendor, Int32 channels, Int32 sampleRate = 48000, Int32 preSkip = DefaultPreSkip)
    {
        if (channels is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(channels), L.T("Opus supports 1 or 2 channels."));
        if (sampleRate is not (8000 or 12000 or 16000 or 24000 or 48000))
            throw new ArgumentOutOfRangeException(nameof(sampleRate), L.T("Opus sample rate must be 8000, 12000, 16000, 24000, or 48000."));
        if (preSkip < 0 || preSkip > 65535) throw new ArgumentOutOfRangeException(nameof(preSkip), L.T("Opus pre-skip must be between 0 and 65535."));

        _vendor = vendor;
        _channels = channels;
        _sampleRate = sampleRate;
        _preSkip = preSkip;
        _serialNo = (UInt32)_rnd.Next();
    }

    /// <summary>声道数</summary>
    public Int32 Channels => _channels;

    /// <summary>原始输入采样率</summary>
    public Int32 SampleRate => _sampleRate;

    /// <summary>解码器预跳采样数</summary>
    public Int32 PreSkip => _preSkip;

    /// <summary>已写入的 Opus 包数</summary>
    public Int32 PacketCount => _packetCount;

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
        if (chunk.Length == 0) return;

        if (!_headerWritten)
            await WriteHeaderAsync(stream, cancellationToken).ConfigureAwait(false);

        _packetCount++;
        // granule position = pre_skip + 累计样本数，样本数从 TOC 解析，兼容 2.5~60 ms 帧
        var samples = OpusPacketHeader.TryParse(chunk.Span, out var header) ? header.TotalSamples : SamplesPerFrame;
        _totalSamples += samples;
        var granule = _preSkip + _totalSamples;

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
        var granule = _preSkip + _totalSamples;
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
        // 计算段表：每段最多 255 字节；整包长度是 255 的整数倍时，末尾仍需要一个 0 长度段作为包结束标记。
        // EOS 空页使用 0 个段（页面不含数据包），避免解码器把空包当成非法 Opus 包。
        var fullSegments = payloadLen / 255;
        var lastSegmentSize = payloadLen % 255;
        var segmentCount = payloadLen == 0 && headerType == 0x04 ? 0 : fullSegments + 1;

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
        if (segmentCount > 0)
            writer.WriteByte((Byte)lastSegmentSize);

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
    private Byte[] BuildOpusHeadPacket()
    {
        var packet = new Byte[19];
        var writer = new SpanWriter(packet.AsSpan());
        // "OpusHead"
        writer.Write(0x7375704Fu);       // "Opus"
        writer.Write(0x64616548u);       // "Head"
        // Version: 1
        writer.WriteByte(1);
        // Channel count
        writer.WriteByte((Byte)_channels);
        // Pre-skip
        writer.Write((Int16)_preSkip);
        // Input sample rate
        writer.Write(_sampleRate);
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
