using NewLife;

namespace NewLife.Audio.Codecs;

/// <summary>Vorbis 位流读取器。Vorbis 使用字节内低位优先（LSB-first）打包</summary>
internal ref struct VorbisBitReader
{
    private readonly ReadOnlySpan<Byte> _data;
    private Int32 _bitPosition;

    /// <summary>使用编码数据初始化读取器</summary>
    /// <param name="data">Vorbis 包数据</param>
    public VorbisBitReader(ReadOnlySpan<Byte> data)
    {
        _data = data;
        _bitPosition = 0;
    }

    /// <summary>当前位位置</summary>
    public Int32 BitPosition => _bitPosition;

    /// <summary>剩余位数</summary>
    public Int32 BitsRemaining => _data.Length * 8 - _bitPosition;

    /// <summary>读取指定位数的无符号整数</summary>
    /// <param name="count">位数，0~32</param>
    /// <returns>读取到的值</returns>
    public UInt32 ReadBits(Int32 count)
    {
        if (count == 0) return 0;
        if (count < 0 || count > 32) throw new ArgumentOutOfRangeException(nameof(count));
        if (BitsRemaining < count) throw new InvalidDataException(L.T("The Vorbis bitstream ended unexpectedly."));

        var value = PeekBitsCore(count);
        _bitPosition += count;

        return value;
    }

    /// <summary>读取一位</summary>
    /// <returns>0 或 1</returns>
    public Int32 ReadBit() => (Int32)ReadBits(1);

    /// <summary>读取有符号整数</summary>
    /// <param name="count">位数，1~32</param>
    /// <returns>有符号值</returns>
    public Int32 ReadSigned(Int32 count)
    {
        var value = ReadBits(count);
        if (count == 32) return (Int32)value;

        return (Int32)(value << (32 - count)) >> (32 - count);
    }

    /// <summary>读取一个字节</summary>
    /// <returns>字节值</returns>
    public Byte ReadByte() => (Byte)ReadBits(8);

    /// <summary>读取 32 位无符号整数</summary>
    /// <returns>无符号值</returns>
    public UInt32 ReadUInt32() => ReadBits(32);

    /// <summary>读取指定长度的原始字节</summary>
    /// <param name="destination">目标缓冲区</param>
    /// <returns>是否读取成功</returns>
    public Boolean TryReadBytes(scoped Span<Byte> destination)
    {
        if (BitsRemaining < destination.Length * 8) return false;

        if ((_bitPosition & 7) == 0)
        {
            _data.Slice(_bitPosition >> 3, destination.Length).CopyTo(destination);
            _bitPosition += destination.Length * 8;
            return true;
        }

        for (var i = 0; i < destination.Length; i++)
            destination[i] = (Byte)ReadBits(8);

        return true;
    }

    /// <summary>窥视指定位数，不移动读取位置。不足时低位补零</summary>
    /// <param name="count">位数，0~32</param>
    /// <param name="available">实际可用位数</param>
    /// <returns>窥视到的值</returns>
    public UInt32 PeekBits(Int32 count, out Int32 available)
    {
        if (count == 0)
        {
            available = 0;
            return 0;
        }
        if (count < 0 || count > 32) throw new ArgumentOutOfRangeException(nameof(count));

        available = Math.Min(count, BitsRemaining);
        if (available <= 0) return 0;

        return PeekBitsCore(available);
    }

    /// <summary>跳过指定位数</summary>
    /// <param name="count">位数</param>
    public void SkipBits(Int32 count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (BitsRemaining < count) throw new InvalidDataException(L.T("The Vorbis bitstream ended unexpectedly."));

        _bitPosition += count;
    }

    private UInt32 PeekBitsCore(Int32 count)
    {
        var bytePosition = _bitPosition >> 3;
        var bitOffset = _bitPosition & 7;
        var remaining = _data.Length - bytePosition;
        var bytes = Math.Min(5, remaining);

        UInt64 value = 0;
        for (var i = 0; i < bytes; i++)
            value |= (UInt64)_data[bytePosition + i] << (i * 8);

        value >>= bitOffset;

        return (UInt32)(value & ((1UL << count) - 1));
    }
}
