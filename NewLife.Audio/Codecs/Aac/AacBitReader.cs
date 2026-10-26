namespace NewLife.Audio.Codecs;

/// <summary>AAC 位读取器（MSB first）</summary>
internal sealed class AacBitReader
{
    private readonly Byte[] _data;
    private Int32 _position;

    public AacBitReader(Byte[] data) => _data = data;

    /// <summary>剩余位数</summary>
    public Int32 BitsLeft => _data.Length * 8 - _position;

    /// <summary>读取指定位数</summary>
    /// <param name="count">位数（0-32）</param>
    /// <returns>读取到的无符号值</returns>
    public UInt32 ReadBits(Int32 count)
    {
        if (count < 0 || count > 32) throw new ArgumentOutOfRangeException(nameof(count));
        if (count > BitsLeft) throw new InvalidDataException("LATM bitstream overread.");

        UInt32 value = 0;
        for (var i = 0; i < count; i++)
        {
            var index = _position >> 3;
            var bit = (_data[index] >> (7 - (_position & 7))) & 1;
            value = (value << 1) | (UInt32)bit;
            _position++;
        }
        return value;
    }

    /// <summary>读取布尔值</summary>
    public Boolean ReadBool() => ReadBits(1) != 0;

    /// <summary>查看后续位但不移动位置</summary>
    /// <param name="count">位数</param>
    /// <returns>无符号值</returns>
    public UInt32 PeekBits(Int32 count)
    {
        var position = _position;
        try
        {
            return ReadBits(count);
        }
        finally
        {
            _position = position;
        }
    }

    /// <summary>跳过指定位数</summary>
    /// <param name="count">位数</param>
    public void SkipBits(Int32 count)
    {
        if (count < 0 || count > BitsLeft) throw new InvalidDataException("LATM bitstream overread.");
        _position += count;
    }

    /// <summary>读取字节</summary>
    /// <param name="count">字节数</param>
    /// <returns>字节数组</returns>
    public Byte[] ReadBytes(Int32 count)
    {
        if (count < 0 || count * 8 > BitsLeft) throw new InvalidDataException("LATM bitstream overread.");

        var result = new Byte[count];
        for (var i = 0; i < count; i++) result[i] = (Byte)ReadBits(8);
        return result;
    }

    /// <summary>字节对齐</summary>
    public void Align() => _position = (_position + 7) & ~7;
}
