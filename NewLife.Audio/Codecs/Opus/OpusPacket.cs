namespace NewLife.Audio.Codecs;

/// <summary>Opus 包结构解析器</summary>
/// <remarks>
/// 按 RFC 6716 第 3 章解析 TOC 字节、帧数、帧长度、VBR/CBR 与 Opus padding。<br/>
/// 解析过程不分配内存，帧数据以 <see cref="ReadOnlySpan{T}"/> 切片返回，可直接交给后续的 CELT/SILK 解码器。
/// </remarks>
public readonly ref struct OpusPacket
{
    /// <summary>Opus 包最大字节数</summary>
    public const Int32 MaxPacketBytes = 1275;

    /// <summary>单个 Opus 包最多包含的帧数</summary>
    public const Int32 MaxFrameCount = 48;

    /// <summary>Opus 内部时间基（Hz）</summary>
    public const Int32 SampleRate = 48000;

    /// <summary>单个 Opus 包最大时长（毫秒）</summary>
    public const Int32 MaxDurationMs = 120;

    private readonly ReadOnlySpan<Byte> _data;
    private readonly Int32 _payloadOffset;
    private readonly Int32 _payloadEnd;
    private readonly Int32 _lengthOffset;
    private readonly Int32 _frameLength;
    private readonly Byte _toc;

    private OpusPacket(ReadOnlySpan<Byte> data, Byte toc, Int32 frameCount, Int32 payloadOffset, Int32 payloadEnd, Int32 lengthOffset, Int32 frameLength)
    {
        _data = data;
        _toc = toc;
        _payloadOffset = payloadOffset;
        _payloadEnd = payloadEnd;
        _lengthOffset = lengthOffset;
        _frameLength = frameLength;
        FrameCount = frameCount;
    }

    /// <summary>TOC 字节</summary>
    public Byte Toc => _toc;

    /// <summary>TOC 中的配置编号（0~31）</summary>
    public Int32 Config => _toc >> 3;

    /// <summary>打包方式（0~3）</summary>
    public Int32 Code => _toc & 0x03;

    /// <summary>是否立体声</summary>
    public Boolean IsStereo => (_toc & 0x04) != 0;

    /// <summary>编码声道数</summary>
    public Int32 Channels => IsStereo ? 2 : 1;

    /// <summary>包内帧数</summary>
    public Int32 FrameCount { get; }

    /// <summary>编码模式</summary>
    public OpusMode Mode => Config switch
    {
        < 12 => OpusMode.SilkOnly,
        < 16 => OpusMode.Hybrid,
        _ => OpusMode.CeltOnly,
    };

    /// <summary>编码带宽</summary>
    public OpusBandwidth Bandwidth => Config switch
    {
        < 12 => (Config >> 2) switch
        {
            0 => OpusBandwidth.Narrowband,
            1 => OpusBandwidth.Mediumband,
            _ => OpusBandwidth.Wideband,
        },
        < 16 => Config < 14 ? OpusBandwidth.SuperWideband : OpusBandwidth.Fullband,
        _ => ((Config - 16) >> 2) switch
        {
            0 => OpusBandwidth.Narrowband,
            1 => OpusBandwidth.Wideband,
            2 => OpusBandwidth.SuperWideband,
            _ => OpusBandwidth.Fullband,
        },
    };

    /// <summary>单帧时长</summary>
    public OpusFrameDuration FrameDuration => GetFrameDuration(Config);

    /// <summary>每帧每声道样本数（48 kHz）</summary>
    public Int32 FrameSamplesPerChannel => GetFrameSamples(FrameDuration, SampleRate);

    /// <summary>整包每声道样本数（48 kHz）</summary>
    public Int32 TotalSamplesPerChannel => FrameSamplesPerChannel * FrameCount;

    /// <summary>整包时长（毫秒）</summary>
    public Double DurationMs => TotalSamplesPerChannel * 1000.0 / SampleRate;

    /// <summary>整包时长</summary>
    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);

    /// <summary>是否为 VBR 打包</summary>
    public Boolean IsVbr => _lengthOffset >= 0;

    /// <summary>包尾 padding 字节数</summary>
    public Int32 Padding => _data.Length - _payloadEnd;

    /// <summary>是否存在 Opus padding</summary>
    public Boolean HasPadding => Padding > 0;

    /// <summary>帧数据起始偏移</summary>
    public Int32 PayloadOffset => _payloadOffset;

    /// <summary>帧数据结束偏移（不含 padding）</summary>
    public Int32 PayloadEnd => _payloadEnd;

    /// <summary>帧数据总字节数</summary>
    public Int32 PayloadLength => _payloadEnd - _payloadOffset;

    /// <summary>CBR 模式下每帧字节数；VBR 模式返回 -1</summary>
    public Int32 FrameLength => _lengthOffset < 0 ? _frameLength : -1;

    /// <summary>获取指定帧的字节长度</summary>
    /// <param name="index">帧索引（0 起始）</param>
    /// <returns>帧字节长度</returns>
    /// <exception cref="ArgumentOutOfRangeException">索引超出帧数时抛出</exception>
    public Int32 GetFrameLength(Int32 index)
    {
        if ((UInt32)index >= (UInt32)FrameCount) throw new ArgumentOutOfRangeException(nameof(index));

        if (_lengthOffset < 0) return _frameLength;

        var pos = _lengthOffset;
        var used = 0;
        for (var i = 0; i < FrameCount - 1; i++)
        {
            if (!TryReadFrameLength(_data, ref pos, out var length)) return 0;
            if (i == index) return length;

            used += length;
        }

        return _payloadEnd - _payloadOffset - used;
    }

    /// <summary>获取指定帧的数据切片</summary>
    /// <param name="index">帧索引（0 起始）</param>
    /// <returns>帧数据</returns>
    /// <exception cref="ArgumentOutOfRangeException">索引超出帧数时抛出</exception>
    public ReadOnlySpan<Byte> GetFrame(Int32 index)
    {
        if ((UInt32)index >= (UInt32)FrameCount) throw new ArgumentOutOfRangeException(nameof(index));

        var start = _payloadOffset;
        for (var i = 0; i < index; i++)
        {
            start += GetFrameLength(i);
        }

        return _data.Slice(start, GetFrameLength(index));
    }

    /// <summary>尝试获取指定帧的数据切片</summary>
    /// <param name="index">帧索引（0 起始）</param>
    /// <param name="frame">帧数据</param>
    /// <returns>索引合法且长度有效时返回 true</returns>
    public Boolean TryGetFrame(Int32 index, out ReadOnlySpan<Byte> frame)
    {
        frame = default;
        if ((UInt32)index >= (UInt32)FrameCount) return false;

        var start = _payloadOffset;
        for (var i = 0; i < index; i++)
        {
            start += GetFrameLength(i);
        }

        var length = GetFrameLength(index);
        if (length < 0 || start + length > _payloadEnd) return false;

        frame = _data.Slice(start, length);
        return true;
    }

    /// <summary>尝试解析 Opus 包</summary>
    /// <param name="data">Opus 包数据（TOC + payload）</param>
    /// <param name="packet">解析结果</param>
    /// <returns>是否为合法 Opus 包</returns>
    public static Boolean TryParse(ReadOnlySpan<Byte> data, out OpusPacket packet)
    {
        packet = default;
        if (data.Length < 1 || data.Length > MaxPacketBytes) return false;

        var toc = data[0];
        var code = toc & 0x03;
        var payloadOffset = 1;
        var payloadEnd = data.Length;
        var frameCount = 1;
        var frameLength = 0;
        var lengthOffset = -1;

        switch (code)
        {
            case 0:
                frameLength = data.Length - 1;
                break;

            case 1:
                if (((data.Length - 1) & 1) != 0) return false;
                frameCount = 2;
                frameLength = (data.Length - 1) / 2;
                break;

            case 2:
                if (data.Length < 2) return false;
                if (!TryReadFrameLength(data, ref payloadOffset, out var firstLength)) return false;
                frameCount = 2;
                lengthOffset = 1;
                if (firstLength > payloadEnd - payloadOffset) return false;
                break;

            case 3:
                if (data.Length < 2) return false;

                var ch = data[1];
                frameCount = ch & 0x3F;
                if (frameCount == 0) return false;

                var padding = 0;
                var pos = 2;
                if ((ch & 0x40) != 0)
                {
                    while (true)
                    {
                        if (pos >= data.Length) return false;

                        var value = data[pos++];
                        padding += value == 255 ? 254 : value;
                        if (value != 255) break;
                    }
                }

                payloadEnd = data.Length - padding;
                if (payloadEnd < pos) return false;

                if ((ch & 0x80) != 0)
                {
                    lengthOffset = pos;

                    var used = 0;
                    for (var i = 0; i < frameCount - 1; i++)
                    {
                        if (!TryReadFrameLength(data, ref pos, out var length)) return false;
                        used += length;
                    }

                    payloadOffset = pos;
                    if (used > payloadEnd - payloadOffset) return false;
                }
                else
                {
                    payloadOffset = pos;
                    var payloadBytes = payloadEnd - payloadOffset;
                    if (payloadBytes % frameCount != 0) return false;

                    frameLength = payloadBytes / frameCount;
                }
                break;

            default:
                return false;
        }

        var samples = GetFrameSamples(GetFrameDuration(toc >> 3), SampleRate);
        if (samples <= 0 || samples * frameCount > SampleRate * MaxDurationMs / 1000) return false;

        packet = new OpusPacket(data, toc, frameCount, payloadOffset, payloadEnd, lengthOffset, frameLength);
        return true;
    }

    /// <summary>解析 Opus 包</summary>
    /// <param name="data">Opus 包数据（TOC + payload）</param>
    /// <returns>解析结果</returns>
    /// <exception cref="ArgumentException">包为空或格式非法时抛出</exception>
    public static OpusPacket Parse(ReadOnlySpan<Byte> data)
    {
        if (!TryParse(data, out var packet)) throw new ArgumentException(L.T("Not a valid Opus packet."), nameof(data));

        return packet;
    }

    /// <summary>获取指定 TOC 配置的帧时长</summary>
    /// <param name="config">配置编号（0~31）</param>
    /// <returns>帧时长</returns>
    public static OpusFrameDuration GetFrameDuration(Int32 config) => config switch
    {
        < 12 => (config & 0x03) switch
        {
            0 => OpusFrameDuration.Ms10,
            1 => OpusFrameDuration.Ms20,
            2 => OpusFrameDuration.Ms40,
            _ => OpusFrameDuration.Ms60,
        },
        < 16 => (config & 0x01) == 0 ? OpusFrameDuration.Ms10 : OpusFrameDuration.Ms20,
        _ => (config & 0x03) switch
        {
            0 => OpusFrameDuration.Ms2_5,
            1 => OpusFrameDuration.Ms5,
            2 => OpusFrameDuration.Ms10,
            _ => OpusFrameDuration.Ms20,
        },
    };

    /// <summary>获取指定帧时长在采样率下的每声道样本数</summary>
    /// <param name="duration">帧时长</param>
    /// <param name="sampleRate">采样率（Hz）</param>
    /// <returns>每声道样本数</returns>
    public static Int32 GetFrameSamples(OpusFrameDuration duration, Int32 sampleRate)
    {
        if (sampleRate <= 0) return 0;

        return (Int32)((Int64)sampleRate * (Int32)duration / 1_000_000);
    }

    private static Boolean TryReadFrameLength(ReadOnlySpan<Byte> data, ref Int32 pos, out Int32 length)
    {
        length = 0;
        if ((UInt32)pos >= (UInt32)data.Length) return false;

        var first = data[pos++];
        if (first < 252)
        {
            length = first;
            return true;
        }

        if ((UInt32)pos >= (UInt32)data.Length) return false;

        length = first + 4 * data[pos++];
        return true;
    }

    /// <summary>返回包结构摘要</summary>
    /// <returns>包含模式、带宽、帧数与帧长的字符串</returns>
    public override String ToString() => $"{Mode} {Bandwidth} {FrameDuration} x{FrameCount} ({PayloadLength} bytes)";
}
