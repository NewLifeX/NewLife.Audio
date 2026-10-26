using System;

namespace NewLife.Audio.Codecs;

/// <summary>按位读取的比特流读取器</summary>
/// <remarks>
/// 以「起始位 + 结束位」描述窗口，允许读取位置越界（越界后补零），
/// 由调用方按格式约束停止，避免在解析损坏码流时抛异常。
/// </remarks>
internal sealed class Mp3BitReader
{
    private Byte[] _data = [];
    private Int32 _end;
    private Int32 _pos;

    /// <summary>当前读取位置（位）</summary>
    public Int32 Position => _pos;

    /// <summary>窗口结束位置（位）</summary>
    public Int32 Length => _end;

    /// <summary>初始化读取窗口</summary>
    /// <param name="data">底层数据</param>
    /// <param name="startBit">起始位</param>
    /// <param name="endBit">结束位（不含）</param>
    public void Init(Byte[] data, Int32 startBit, Int32 endBit)
    {
        _data = data;
        _pos = startBit;
        _end = Math.Min(endBit, data.Length * 8);
    }

    /// <summary>跳到指定位置</summary>
    /// <param name="bitPos">目标位位置</param>
    public void Seek(Int32 bitPos) => _pos = bitPos;

    /// <summary>跳过若干位</summary>
    /// <param name="count">位数</param>
    public void Skip(Int32 count) => _pos += count;

    /// <summary>查看接下来的若干位但不前进</summary>
    /// <param name="count">位数，最大 32</param>
    /// <returns>取到的值，越界部分补零</returns>
    public UInt32 Peek(Int32 count)
    {
        if (count <= 0) return 0;
        if (count > 32) count = 32;

        var index = _pos >> 3;
        var shift = _pos & 7;
        UInt64 buffer = 0;
        for (var i = 0; i < 5; i++)
        {
            buffer <<= 8;
            if (index + i >= 0 && index + i < _data.Length) buffer |= _data[index + i];
        }

        var total = shift + count;
        var mask = count >= 64 ? UInt64.MaxValue : (1UL << count) - 1;

        return (UInt32)((buffer >> (40 - total)) & mask);
    }

    /// <summary>读取若干位并前进</summary>
    /// <param name="count">位数，最大 32</param>
    /// <returns>取到的值</returns>
    public UInt32 Read(Int32 count)
    {
        var value = Peek(count);
        _pos += count;

        return value;
    }

    /// <summary>读取一位</summary>
    /// <returns>0 或 1</returns>
    public Int32 ReadBit() => (Int32)Read(1);

    /// <summary>是否还有足够的可读位</summary>
    /// <param name="count">位数</param>
    /// <returns></returns>
    public Boolean HasBits(Int32 count) => _pos + count <= _end;
}
