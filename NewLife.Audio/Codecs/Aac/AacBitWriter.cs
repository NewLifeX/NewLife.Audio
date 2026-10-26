namespace NewLife.Audio.Codecs;

/// <summary>AAC 位写入器（MSB first）</summary>
internal sealed class AacBitWriter
{
    private Byte[] _buffer = new Byte[256];
    private Int32 _byteLength;
    private UInt32 _currentByte;
    private Int32 _bitsInCurrentByte;

    /// <summary>当前位位置</summary>
    public Int32 BitPosition => _byteLength * 8 + _bitsInCurrentByte;

    /// <summary>写入指定位数</summary>
    /// <param name="value">值</param>
    /// <param name="bitCount">位数（0-32）</param>
    public void WriteBits(UInt32 value, Int32 bitCount)
    {
        if (bitCount <= 0) return;
        if (bitCount > 32) throw new ArgumentOutOfRangeException(nameof(bitCount));

        for (var i = bitCount - 1; i >= 0; i--)
        {
            var bit = (value >> i) & 1u;
            _currentByte = (_currentByte << 1) | bit;
            _bitsInCurrentByte++;

            if (_bitsInCurrentByte == 8)
            {
                AppendByte((Byte)_currentByte);
                _currentByte = 0;
                _bitsInCurrentByte = 0;
            }
        }
    }

    /// <summary>写入有符号值（补码截断）</summary>
    /// <param name="value">值</param>
    /// <param name="bitCount">位数</param>
    public void WriteSignedBits(Int32 value, Int32 bitCount)
    {
        var mask = bitCount == 32 ? UInt32.MaxValue : (1u << bitCount) - 1;
        WriteBits((UInt32)value & mask, bitCount);
    }

    /// <summary>字节对齐</summary>
    public void ByteAlign()
    {
        if (_bitsInCurrentByte == 0) return;

        WriteBits(0, 8 - _bitsInCurrentByte);
    }

    /// <summary>转为字节数组</summary>
    /// <returns>字节数组</returns>
    public Byte[] ToArray()
    {
        var length = _byteLength + (_bitsInCurrentByte > 0 ? 1 : 0);
        var result = new Byte[length];
        Array.Copy(_buffer, result, _byteLength);

        if (_bitsInCurrentByte > 0)
            result[_byteLength] = (Byte)(_currentByte << (8 - _bitsInCurrentByte));

        return result;
    }

    private void AppendByte(Byte value)
    {
        if (_byteLength == _buffer.Length)
            Array.Resize(ref _buffer, _buffer.Length * 2);

        _buffer[_byteLength++] = value;
    }
}
