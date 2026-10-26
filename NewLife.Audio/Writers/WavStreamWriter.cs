using NewLife.Buffers;

namespace NewLife.Audio.Writers;

/// <summary>WAV 音频流写入器。写标准 RIFF/WAVE 头，后续透传 PCM 数据</summary>
/// <remarks>
/// 默认按流式场景写入未知长度（data chunk size = 0x7FFFFFFF），允许边合成边发送、无需事后回填。<br/>
/// 当输出流可 seek 且 <see cref="PatchSize"/> 为 true 时，在 <see cref="WriteTrailerAsync"/> 中回填真实长度，
/// 适合保存为本地文件。
/// </remarks>
public sealed class WavStreamWriter : AudioStreamWriter
{
    private static readonly Byte[] _pcmGuid = [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];
    private static readonly Byte[] _floatGuid = [0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

    private readonly Int32 _sampleRate;
    private Int64 _dataSize;
    private Boolean _headerWritten;
    private Int32 _fmtSize = 16;

    /// <summary>初始化 WAV 写入器</summary>
    /// <param name="sampleRate">采样率（Hz），默认 24000</param>
    public WavStreamWriter(Int32 sampleRate = 24000) => _sampleRate = sampleRate > 0 ? sampleRate : 24000;

    /// <summary>声道数，默认 1</summary>
    public Int32 Channels { get; set; } = 1;

    /// <summary>每样本位数，默认 16</summary>
    public Int32 BitsPerSample { get; set; } = 16;

    /// <summary>有效位深。0 或等于 <see cref="BitsPerSample"/> 时不写；小于容器位深时改用 WAVEFORMATEXTENSIBLE</summary>
    public Int32 ValidBitsPerSample { get; set; }

    /// <summary>声道掩码（WAVE dwChannelMask）。非 0 时改用 WAVEFORMATEXTENSIBLE</summary>
    public Int32 ChannelMask { get; set; }

    /// <summary>是否为 IEEE 浮点采样。决定 SubFormat GUID</summary>
    public Boolean IsFloat { get; set; }

    /// <summary>是否在结束时回填长度。仅当输出流可 seek 时生效</summary>
    public Boolean PatchSize { get; set; }

    /// <summary>可选的 LIST/INFO 块（含块头），在 fmt 之后、data 之前写入</summary>
    public Byte[] ListInfo { get; set; }

    /// <summary>audio/wav</summary>
    public override String ContentType => "audio/wav";

    /// <summary>是否需要 WAVEFORMATEXTENSIBLE 头（指定了掩码，或有效位小于容器位深）</summary>
    private Boolean UseExtensible => ChannelMask != 0 || (ValidBitsPerSample > 0 && ValidBitsPerSample != BitsPerSample);

    /// <summary>写入 WAV 头（RIFF + fmt + 可选 LIST/INFO + data chunk）</summary>
    /// <param name="stream">输出流</param>
    /// <param name="cancellationToken">取消令牌</param>
    public override async ValueTask WriteHeaderAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (_headerWritten) return;

        var channels = Channels > 0 ? Channels : 1;
        var bitsPerSample = BitsPerSample > 0 ? BitsPerSample : 16;
        var byteRate = _sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;
        var extensible = UseExtensible;
        _fmtSize = extensible ? 40 : 16;

        // 流式场景无法预知总长，先用最大 Int32 正值占位
        var maxDataSize = 0x7FFFFFFF;
        var riffSize = maxDataSize - 8;

        var info = ListInfo;
        var infoLength = info?.Length ?? 0;
        var header = new Byte[20 + _fmtSize + infoLength + 8];
        var writer = new SpanWriter(header.AsSpan());

        // RIFF chunk
        writer.Write(0x46464952u);       // "RIFF"
        writer.Write(riffSize);
        writer.Write(0x45564157u);       // "WAVE"

        // fmt sub-chunk
        writer.Write(0x20746D66u);       // "fmt "
        writer.Write(_fmtSize);          // sub-chunk size (PCM = 16, EXTENSIBLE = 40)
        writer.Write((Int16)(extensible ? 0xFFFE : IsFloat ? 3 : 1));
        writer.Write((Int16)channels);
        writer.Write(_sampleRate);
        writer.Write(byteRate);
        writer.Write((Int16)blockAlign);
        writer.Write((Int16)bitsPerSample);

        if (extensible)
        {
            // WAVEFORMATEXTENSIBLE：cbSize + 有效位 + 声道掩码 + SubFormat GUID
            writer.Write((Int16)22);
            writer.Write((Int16)(ValidBitsPerSample > 0 && ValidBitsPerSample < bitsPerSample ? ValidBitsPerSample : bitsPerSample));
            writer.Write(ChannelMask);

            var guid = IsFloat ? _floatGuid : _pcmGuid;
            Array.Copy(guid, 0, header, 44, guid.Length);
        }

        // 可选的 LIST/INFO 标签块
        var fmtEnd = 20 + _fmtSize;
        if (infoLength > 0) Array.Copy(info, 0, header, fmtEnd, infoLength);

        // data sub-chunk
        var dataHeader = fmtEnd + infoLength;
        writer = new SpanWriter(header.AsSpan(dataHeader));
        writer.Write(0x61746164u);       // "data"
        writer.Write(maxDataSize);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        _headerWritten = true;
    }

    /// <summary>在 data 块之前插入 LIST/INFO 标签块（要求可定位且尚未写入音频数据）</summary>
    /// <param name="stream">输出流</param>
    /// <param name="listInfo">LIST/INFO 块字节（含块头）</param>
    /// <returns>插入成功返回 true；流不可定位或已有数据返回 false</returns>
    /// <remarks>头部已按 44 字节写出，这里把其中的 data 块头后移，为标签块腾出空间。</remarks>
    public Boolean InsertListInfo(Stream stream, Byte[] listInfo)
    {
        if (listInfo == null || listInfo.Length == 0) return false;
        if (stream == null || !stream.CanSeek || _dataSize > 0) return false;

        var fmtEnd = 20 + _fmtSize;
        var end = stream.Position;
        try
        {
            ListInfo = listInfo;

            stream.Position = fmtEnd;
            stream.Write(listInfo, 0, listInfo.Length);

            var header = new Byte[8];
            var writer = new SpanWriter(header.AsSpan());
            writer.Write(0x61746164u);       // "data"
            writer.Write(0x7FFFFFFF);

            stream.Write(header, 0, header.Length);
            stream.Position = fmtEnd + listInfo.Length + header.Length;
            stream.Flush();
        }
        catch
        {
            ListInfo = null;
            stream.Position = end;
            return false;
        }

        return true;
    }

    /// <summary>直接写入 PCM 数据（16-bit 有符号整数小端）</summary>
    /// <param name="stream">输出流</param>
    /// <param name="chunk">PCM 数据块</param>
    /// <param name="cancellationToken">取消令牌</param>
    public override async ValueTask WriteChunkAsync(Stream stream, ReadOnlyMemory<Byte> chunk, CancellationToken cancellationToken = default)
    {
        await stream.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        _dataSize += chunk.Length;
    }

    /// <summary>写入尾部。可 seek 且启用 <see cref="PatchSize"/> 时回填 RIFF/data 长度</summary>
    /// <param name="stream">输出流</param>
    /// <param name="cancellationToken">取消令牌</param>
    public override async ValueTask WriteTrailerAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (!PatchSize || stream == null || !stream.CanSeek) return;

        var position = stream.Position;
        try
        {
            var infoLength = ListInfo?.Length ?? 0;
            var fmtEnd = 20 + _fmtSize;
            var dataSize = (Int32)Math.Min(_dataSize, 0x7FFFFFFF - fmtEnd - infoLength - 4);
            var riffSize = dataSize + fmtEnd + infoLength;

            var buffer = new Byte[4];
            var writer = new SpanWriter(buffer.AsSpan());

            stream.Position = 4;
            writer.Write(riffSize);
            await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);

            writer = new SpanWriter(buffer.AsSpan());
            stream.Position = fmtEnd + infoLength + 4;
            writer.Write(dataSize);
            await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);

            stream.Position = position;
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 回填失败不影响已经写出的音频
            stream.Position = position;
        }
    }
}
