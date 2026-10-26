namespace NewLife.Audio.Codecs;

/// <summary>Opus 范围解码器（RFC 6716 §4.1）</summary>
/// <remarks>
/// 与 Opus 码流逐位兼容的熵解码器，供 CELT/SILK 层使用。<br/>
/// 所有运算使用 32 位整数，解码状态保存在栈上（ref struct），不产生堆分配。<br/>
/// 原始比特（raw bits）从帧尾反向读取，允许与范围编码数据重叠。
/// </remarks>
public ref struct OpusRangeDecoder
{
    private const UInt32 CodeTop = 1U << 31;
    private const UInt32 CodeBot = 1U << 23;
    private const Int32 SymBits = 8;
    private const Int32 CodeExtra = 7;

    private readonly ReadOnlySpan<Byte> _data;
    private Int32 _offset;
    private Int32 _endOffset;
    private UInt32 _rng;
    private UInt32 _val;
    private Int32 _rem;
    private UInt32 _ext;
    private Int32 _nbitsTotal;
    private UInt64 _endWindow;
    private Int32 _endBits;
    private Boolean _error;

    /// <summary>使用 Opus 帧数据初始化范围解码器</summary>
    /// <param name="data">帧数据（不含 TOC）</param>
    public OpusRangeDecoder(ReadOnlySpan<Byte> data)
    {
        _data = data;
        _offset = 0;
        _endOffset = 0;
        _rng = 1U << (SymBits - 1);
        _rem = ReadByte();
        _val = _rng - 1 - (UInt32)(_rem >> (SymBits - CodeExtra));
        _rem = (_rem << CodeExtra) & 0xFF;
        _ext = 0;
        _nbitsTotal = 32 + 1 - (32 - SymBits) / SymBits * SymBits;
        _endWindow = 0;
        _endBits = 0;
        _error = false;

        Normalize();
    }

    /// <summary>是否检测到码流错误</summary>
    public readonly Boolean Error => _error;

    /// <summary>已从帧首读取的字节数</summary>
    public readonly Int32 Position => _offset;

    /// <summary>已解码的比特数（保守上界）</summary>
    public readonly Int32 BitsRead => _nbitsTotal - ILog(_rng);

    /// <summary>解码一个总频率为 ft 的符号，返回 fs（需再调用 <see cref="Update"/>）</summary>
    /// <param name="ft">总频率（1~65535）</param>
    /// <returns>用于查找符号的 fs 值</returns>
    public UInt32 Decode(Int32 ft)
    {
        if (ft <= 0)
        {
            _error = true;
            _ext = _rng;
            return 0;
        }

        _ext = _rng / (UInt32)ft;
        if (_ext == 0)
        {
            _error = true;
            _ext = 1;
        }

        var s = _val / _ext + 1;
        if (s > (UInt32)ft) s = (UInt32)ft;

        return (UInt32)ft - s;
    }

    /// <summary>按三元组更新范围解码状态</summary>
    /// <param name="fl">符号下界</param>
    /// <param name="fh">符号上界</param>
    /// <param name="ft">总频率</param>
    public void Update(Int32 fl, Int32 fh, Int32 ft)
    {
        if (_ext == 0)
        {
            _error = true;
            return;
        }

        if (fl < 0 || fh <= fl || fh > ft || ft <= 0)
        {
            _error = true;
            return;
        }

        var s = _ext * (UInt32)(ft - fh);
        _val -= s;
        if (fl > 0)
            _rng = _ext * (UInt32)(fh - fl);
        else
            _rng -= s;

        Normalize();
    }

    /// <summary>解码一个二进制符号（1 的概率为 1/2^logp）</summary>
    /// <param name="logp">概率的以 2 为底对数的绝对值（0~31）</param>
    /// <returns>0 或 1</returns>
    public Int32 DecodeBitLogp(Int32 logp)
    {
        if (logp < 0 || logp > 31)
        {
            _error = true;
            return 0;
        }

        var s = _rng >> logp;
        Int32 value;
        if (_val < s)
        {
            _rng = s;
            value = 1;
        }
        else
        {
            _val -= s;
            _rng -= s;
            value = 0;
        }

        Normalize();
        return value;
    }

    /// <summary>使用逆累积分布表解码符号</summary>
    /// <param name="icdf">逆 CDF 表，末尾以 0 结束</param>
    /// <param name="ftb">总频率的以 2 为底对数（0~8）</param>
    /// <returns>符号索引</returns>
    public Int32 DecodeICdf(ReadOnlySpan<Byte> icdf, Int32 ftb)
    {
        if (ftb < 0 || ftb > 8)
        {
            _error = true;
            return 0;
        }

        var ft = 1 << ftb;
        var fs = Decode(ft);
        var fl = 0;
        for (var k = 0; k < icdf.Length; k++)
        {
            var fh = ft - icdf[k];
            if (fs < (UInt32)fh)
            {
                Update(fl, fh, ft);
                return k;
            }

            fl = fh;
            if (icdf[k] == 0) break;
        }

        _error = true;
        return 0;
    }

    /// <summary>解码 0~ft-1 的均匀分布整数</summary>
    /// <param name="ft">取值范围（1~UInt32.MaxValue）</param>
    /// <returns>解码值</returns>
    public UInt32 DecodeUInt(UInt32 ft)
    {
        if (ft <= 1) return 0;

        var ftb = ILog(ft - 1);
        if (ftb <= 8)
        {
            var value = Decode((Int32)ft);
            Update((Int32)value, (Int32)value + 1, (Int32)ft);
            return value;
        }

        var shift = ftb - 8;
        var top = ((ft - 1) >> shift) + 1;
        var t = Decode((Int32)top);
        Update((Int32)t, (Int32)t + 1, (Int32)top);

        var result = (t << shift) | DecodeBits(shift);
        if (result >= ft)
        {
            _error = true;
            return ft - 1;
        }

        return result;
    }

    /// <summary>读取原始比特（LSB 优先，从帧尾反向读取）</summary>
    /// <param name="bits">比特数（0~32）</param>
    /// <returns>原始比特值</returns>
    public UInt32 DecodeBits(Int32 bits)
    {
        if (bits <= 0) return 0;
        if (bits > 32)
        {
            _error = true;
            return 0;
        }

        while (_endBits < bits)
        {
            _endWindow |= (UInt64)ReadByteFromEnd() << _endBits;
            _endBits += 8;
        }

        var mask = bits == 32 ? UInt32.MaxValue : (1U << bits) - 1;
        var value = (UInt32)_endWindow & mask;
        _endWindow >>= bits;
        _endBits -= bits;
        _nbitsTotal += bits;

        return value;
    }

    private void Normalize()
    {
        while (_rng <= CodeBot)
        {
            _nbitsTotal += SymBits;
            _rng <<= SymBits;

            var sym = _rem;
            _rem = ReadByte();
            sym |= _rem >> 1;
            _val = ((_val << SymBits) + (255U - (UInt32)sym)) & (CodeTop - 1);
            _rem = (_rem << CodeExtra) & 0xFF;
        }
    }

    private Int32 ReadByte()
    {
        if (_offset < _data.Length) return _data[_offset++];

        // RFC 6716 §4.1.2.1：范围解码器读尽帧数据后必须继续使用零，而非报错
        return 0;
    }

    private Int32 ReadByteFromEnd()
    {
        if (_endOffset < _data.Length) return _data[_data.Length - ++_endOffset];

        return 0;
    }

    /// <summary>计算无符号整数的有效位数</summary>
    /// <param name="value">输入值</param>
    /// <returns>位数；0 返回 0</returns>
    public static Int32 ILog(UInt32 value)
    {
        var bits = 0;
        while (value > 0)
        {
            bits++;
            value >>= 1;
        }

        return bits;
    }
}

/// <summary>Opus 范围编码器（RFC 6716 §5.1）</summary>
/// <remarks>
/// 与 <see cref="OpusRangeDecoder"/> 对称的熵编码器，供 CELT/SILK 编码器使用。<br/>
/// 范围编码数据从缓冲区头部写入，原始比特从尾部反向写入，二者在中间相遇。
/// </remarks>
public sealed class OpusRangeEncoder
{
    private const UInt32 CodeTop = 1U << 31;
    private const UInt32 CodeBot = 1U << 23;
    private const Int32 SymBits = 8;

    private readonly Byte[] _buffer;
    private Int32 _offset;
    private Int32 _endOffset;
    private UInt32 _val;
    private UInt32 _rng;
    private Int32 _rem;
    private UInt32 _ext;
    private Int32 _nbitsTotal;
    private UInt64 _endWindow;
    private Int32 _endBits;
    private Boolean _error;
    private Boolean _finished;

    /// <summary>初始化范围编码器</summary>
    /// <param name="capacity">输出缓冲区容量（Opus 单包最大 1275 字节）</param>
    public OpusRangeEncoder(Int32 capacity = 1275)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));

        _buffer = new Byte[capacity];
        _val = 0;
        _rng = CodeTop;
        _rem = -1;
        _ext = 0;
        _nbitsTotal = 32 + 1 - (32 - SymBits) / SymBits * SymBits;
    }

    /// <summary>是否发生缓冲区溢出或参数错误</summary>
    public Boolean Error => _error;

    /// <summary>编码一个符号</summary>
    /// <param name="fl">符号下界</param>
    /// <param name="fh">符号上界</param>
    /// <param name="ft">总频率</param>
    public void Encode(Int32 fl, Int32 fh, Int32 ft)
    {
        if (_finished) throw new InvalidOperationException(L.T("The range encoder has already been finished."));
        if (ft <= 0 || fl < 0 || fh <= fl || fh > ft)
        {
            _error = true;
            return;
        }

        var ext = _rng / (UInt32)ft;
        if (fl > 0)
        {
            _val += _rng - ext * (UInt32)(ft - fl);
            _rng = ext * (UInt32)(fh - fl);
        }
        else
        {
            _rng -= ext * (UInt32)(ft - fh);
        }

        Normalize();
    }

    /// <summary>编码一个二进制符号（1 的概率为 1/2^logp）</summary>
    /// <param name="bit">0 或 1</param>
    /// <param name="logp">概率的以 2 为底对数的绝对值（0~31）</param>
    public void EncodeBitLogp(Int32 bit, Int32 logp)
    {
        if (_finished) throw new InvalidOperationException(L.T("The range encoder has already been finished."));
        if (logp < 0 || logp > 31)
        {
            _error = true;
            return;
        }

        var s = _rng >> logp;
        if (bit != 0)
        {
            _val += _rng - s;
            _rng = s;
        }
        else
        {
            _rng -= s;
        }

        Normalize();
    }

    /// <summary>使用逆累积分布表编码符号</summary>
    /// <param name="symbol">符号索引</param>
    /// <param name="icdf">逆 CDF 表，末尾以 0 结束</param>
    /// <param name="ftb">总频率的以 2 为底对数（0~8）</param>
    public void EncodeICdf(Int32 symbol, ReadOnlySpan<Byte> icdf, Int32 ftb)
    {
        if (_finished) throw new InvalidOperationException(L.T("The range encoder has already been finished."));
        if (ftb < 0 || ftb > 8 || symbol < 0 || symbol >= icdf.Length)
        {
            _error = true;
            return;
        }

        var ft = 1 << ftb;
        var fl = symbol == 0 ? 0 : ft - icdf[symbol - 1];
        var fh = ft - icdf[symbol];
        Encode(fl, fh, ft);
    }

    /// <summary>编码 0~ft-1 的均匀分布整数</summary>
    /// <param name="value">要编码的值</param>
    /// <param name="ft">取值范围（1~UInt32.MaxValue）</param>
    public void EncodeUInt(UInt32 value, UInt32 ft)
    {
        if (_finished) throw new InvalidOperationException(L.T("The range encoder has already been finished."));
        if (ft <= 1) return;
        if (value >= ft)
        {
            _error = true;
            return;
        }

        var ftb = OpusRangeDecoder.ILog(ft - 1);
        if (ftb <= 8)
        {
            Encode((Int32)value, (Int32)value + 1, (Int32)ft);
            return;
        }

        var shift = ftb - 8;
        var top = ((ft - 1) >> shift) + 1;
        var high = value >> shift;
        Encode((Int32)high, (Int32)high + 1, (Int32)top);
        EncodeBits(value & ((1U << shift) - 1), shift);
    }

    /// <summary>写入原始比特（LSB 优先，从缓冲区尾部反向写入）</summary>
    /// <param name="value">比特值</param>
    /// <param name="bits">比特数（0~32）</param>
    public void EncodeBits(UInt32 value, Int32 bits)
    {
        if (_finished) throw new InvalidOperationException(L.T("The range encoder has already been finished."));
        if (bits <= 0) return;
        if (bits > 32)
        {
            _error = true;
            return;
        }

        if (bits < 32) value &= (1U << bits) - 1;

        _endWindow |= (UInt64)value << _endBits;
        _endBits += bits;
        while (_endBits >= SymBits)
        {
            WriteByteAtEnd((Byte)_endWindow);
            _endWindow >>= SymBits;
            _endBits -= SymBits;
        }

        _nbitsTotal += bits;
    }

    /// <summary>完成编码并返回 Opus 帧数据</summary>
    /// <returns>范围编码数据 + 原始比特数据</returns>
    public Byte[] Finish()
    {
        if (_finished) throw new InvalidOperationException(L.T("The range encoder has already been finished."));
        _finished = true;

        var l = 32 - OpusRangeDecoder.ILog(_rng);
        var mask = (CodeTop - 1) >> l;
        var end = (_val + mask) & ~mask;
        if ((end | mask) >= _val + _rng)
        {
            l++;
            mask >>= 1;
            end = (_val + mask) & ~mask;
        }

        while (l > 0)
        {
            CarryOut(end >> 23);
            end = (end << SymBits) & (CodeTop - 1);
            l -= SymBits;
        }

        if (_rem != -1 || _ext > 0) CarryOut(0);

        if (_endBits > 0)
        {
            var index = _buffer.Length - _endOffset - 1;
            if (index < _offset)
            {
                _error = true;
            }
            else
            {
                _buffer[index] = (Byte)_endWindow;
                _endOffset++;
            }

            _endWindow = 0;
            _endBits = 0;
        }

        var total = _offset + _endOffset;
        if (total > _buffer.Length || _error) throw new InvalidOperationException(L.T("The range encoder buffer overflowed."));

        var result = new Byte[total];
        Array.Copy(_buffer, 0, result, 0, _offset);
        if (_endOffset > 0) Array.Copy(_buffer, _buffer.Length - _endOffset, result, _offset, _endOffset);

        return result;
    }

    private void Normalize()
    {
        while (_rng <= CodeBot)
        {
            _nbitsTotal += SymBits;
            CarryOut(_val >> 23);
            _val = (_val << SymBits) & (CodeTop - 1);
            _rng <<= SymBits;
        }
    }

    private void CarryOut(UInt32 c)
    {
        if (c == 255)
        {
            _ext++;
            return;
        }

        var carry = c >> 8;
        if (_rem != -1) WriteByteAtStart((Byte)(_rem + (Int32)carry));

        while (_ext > 0)
        {
            WriteByteAtStart((Byte)(carry != 0 ? 0 : 255));
            _ext--;
        }

        _rem = (Int32)(c & 255);
    }

    private void WriteByteAtStart(Byte value)
    {
        if (_offset >= _buffer.Length || _offset + _endOffset >= _buffer.Length)
        {
            _error = true;
            return;
        }

        _buffer[_offset++] = value;
    }

    private void WriteByteAtEnd(Byte value)
    {
        if (_endOffset >= _buffer.Length || _offset + _endOffset >= _buffer.Length)
        {
            _error = true;
            return;
        }

        _buffer[_buffer.Length - ++_endOffset] = value;
    }
}
