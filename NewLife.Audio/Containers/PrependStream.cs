namespace NewLife.Audio.Containers;

/// <summary>把已读取的前缀字节重新拼回不可定位流的包装流</summary>
/// <remarks>魔术字节识别需要先读头部，不可定位流无法回退位置，只能把已读字节还给读取器，否则文件头会丢失。</remarks>
internal sealed class PrependStream : Stream
{
    private readonly Byte[] _prefix;
    private readonly Stream _inner;
    private Int32 _offset;

    /// <summary>用前缀字节与底层流初始化包装流</summary>
    /// <param name="prefix">已从底层流读取的前缀字节</param>
    /// <param name="inner">底层流，不可定位</param>
    public PrependStream(Byte[] prefix, Stream inner)
    {
        _prefix = prefix;
        _inner = inner;
    }

    /// <summary>是否可读</summary>
    public override Boolean CanRead => true;

    /// <summary>是否可定位。包装流恒为 false</summary>
    public override Boolean CanSeek => false;

    /// <summary>是否可写</summary>
    public override Boolean CanWrite => false;

    /// <summary>长度。包装流不支持</summary>
    public override Int64 Length => throw new NotSupportedException();

    /// <summary>位置。包装流不支持</summary>
    public override Int64 Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <summary>冲刷。无操作</summary>
    public override void Flush() { }

    /// <summary>定位。包装流不支持</summary>
    /// <param name="offset">偏移</param>
    /// <param name="origin">基准</param>
    /// <returns>不支持，恒抛异常</returns>
    public override Int64 Seek(Int64 offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <summary>设置长度。包装流不支持</summary>
    /// <param name="value">长度</param>
    public override void SetLength(Int64 value) => throw new NotSupportedException();

    /// <summary>写入。包装流不支持</summary>
    /// <param name="buffer">缓冲区</param>
    /// <param name="offset">偏移</param>
    /// <param name="count">字节数</param>
    public override void Write(Byte[] buffer, Int32 offset, Int32 count) => throw new NotSupportedException();

    /// <summary>读取。先吐出前缀字节，再读底层流</summary>
    /// <param name="buffer">缓冲区</param>
    /// <param name="offset">偏移</param>
    /// <param name="count">最大字节数</param>
    /// <returns>实际读取的字节数</returns>
    public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count)
    {
        if (_offset < _prefix.Length)
        {
            var n = Math.Min(count, _prefix.Length - _offset);
            Array.Copy(_prefix, _offset, buffer, offset, n);
            _offset += n;
            return n;
        }

        return _inner.Read(buffer, offset, count);
    }
}
