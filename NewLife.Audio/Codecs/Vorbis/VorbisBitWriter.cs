namespace NewLife.Audio.Codecs;

/// <summary>Vorbis 位流写入器。按字节内低位优先（LSB-first）打包</summary>
internal sealed class VorbisBitWriter
{
    private readonly MemoryStream _stream = new();
    private UInt64 _buffer;
    private Int32 _bits;

    /// <summary>当前已写入位数</summary>
    public Int32 BitPosition { get; private set; }

    /// <summary>写入指定位数的无符号整数</summary>
    /// <param name="value">值</param>
    /// <param name="count">位数，0~32</param>
    public void WriteBits(UInt32 value, Int32 count)
    {
        if (count == 0) return;
        if (count < 0 || count > 32) throw new ArgumentOutOfRangeException(nameof(count));

        if (count < 32) value &= (1U << count) - 1;
        _buffer |= (UInt64)value << _bits;
        _bits += count;
        BitPosition += count;

        while (_bits >= 8)
        {
            _stream.WriteByte((Byte)_buffer);
            _buffer >>= 8;
            _bits -= 8;
        }
    }

    /// <summary>写入一位</summary>
    /// <param name="value">布尔值</param>
    public void WriteBit(Boolean value) => WriteBits(value ? 1U : 0U, 1);

    /// <summary>写入原始字节</summary>
    /// <param name="data">字节数据</param>
    public void WriteBytes(ReadOnlySpan<Byte> data)
    {
        for (var i = 0; i < data.Length; i++)
            WriteBits(data[i], 8);
    }

    /// <summary>结束写入并返回完整字节数组</summary>
    /// <returns>字节数组</returns>
    public Byte[] Finish()
    {
        if (_bits > 0)
        {
            _stream.WriteByte((Byte)_buffer);
            _buffer = 0;
            _bits = 0;
        }

        return _stream.ToArray();
    }
}
