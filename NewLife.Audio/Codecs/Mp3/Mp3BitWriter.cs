using System;

namespace NewLife.Audio.Codecs;

/// <summary>按位写入的比特流写入器（高位在前）</summary>
/// <remarks>
/// MPEG 音频码流按大端比特序组织，本类提供定长比特写入、跨实例比特搬运与字节对齐复制，
/// 供编码器拼接帧头、边信息与主数据使用。
/// </remarks>
internal sealed class Mp3BitWriter
{
    #region 字段
    private Byte[] _data;
    private Int32 _pos;
    #endregion

    #region 属性
    /// <summary>当前写入位置（位）</summary>
    public Int32 Position => _pos;

    /// <summary>已占用的字节数（向上取整）</summary>
    public Int32 ByteLength => (_pos + 7) >> 3;
    #endregion

    #region 构造
    /// <summary>初始化写入器</summary>
    /// <param name="capacity">初始容量（字节）</param>
    public Mp3BitWriter(Int32 capacity = 1024)
    {
        _data = new Byte[capacity > 0 ? capacity : 1024];
    }
    #endregion

    #region 方法
    /// <summary>复位到起始位置并清空缓冲区</summary>
    public void Reset()
    {
        Array.Clear(_data, 0, _data.Length);
        _pos = 0;
    }

    /// <summary>写入指定宽度的无符号整数（高位在前）</summary>
    /// <param name="value">待写入的值，仅取低 <paramref name="count"/> 位</param>
    /// <param name="count">位宽</param>
    public void Write(Int32 value, Int32 count)
    {
        if (count <= 0) return;

        var need = (_pos + count + 7) >> 3;
        if (need > _data.Length) Grow(need);

        for (var i = count - 1; i >= 0; i--)
        {
            if (((value >> i) & 1) != 0) _data[_pos >> 3] |= (Byte)(0x80 >> (_pos & 7));
            _pos++;
        }
    }

    /// <summary>写入一位</summary>
    /// <param name="value">0 或非 0</param>
    public void WriteBit(Boolean value) => Write(value ? 1 : 0, 1);

    /// <summary>读取指定位置的位，越界返回 false</summary>
    /// <param name="index">位下标</param>
    /// <returns>位值</returns>
    public Boolean ReadBitAt(Int32 index)
    {
        if (index < 0 || index >= _pos) return false;

        return (_data[index >> 3] & (0x80 >> (index & 7))) != 0;
    }

    /// <summary>把另一个写入器的指定比特段追加到末尾</summary>
    /// <param name="source">来源写入器</param>
    /// <param name="startBit">起始位（含）</param>
    /// <param name="bitCount">比特数</param>
    public void AppendFrom(Mp3BitWriter source, Int32 startBit, Int32 bitCount)
    {
        for (var i = 0; i < bitCount; i++)
        {
            WriteBit(source.ReadBitAt(startBit + i));
        }
    }

    /// <summary>把当前内容复制到目标数组，不足部分补零</summary>
    /// <param name="target">目标数组</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="byteCount">写入字节数</param>
    public void CopyTo(Byte[] target, Int32 offset, Int32 byteCount)
    {
        var count = Math.Min(ByteLength, byteCount);
        Array.Copy(_data, 0, target, offset, count);
        for (var i = count; i < byteCount; i++)
        {
            target[offset + i] = 0;
        }
    }

    /// <summary>导出为字节数组</summary>
    /// <returns>按字节对齐的副本</returns>
    public Byte[] ToArray()
    {
        var bytes = new Byte[ByteLength];
        Array.Copy(_data, 0, bytes, 0, bytes.Length);

        return bytes;
    }

    private void Grow(Int32 need)
    {
        var size = _data.Length;
        while (size < need) size <<= 1;

        var data = new Byte[size];
        Array.Copy(_data, 0, data, 0, _data.Length);
        _data = data;
    }
    #endregion
}
