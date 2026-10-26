using NewLife;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Containers;

/// <summary>回调式数据流。把读取/写入/定位委托给调用方，便于接入网络流、加密流或自定义缓冲</summary>
/// <remarks>
/// 所有能力都必须显式声明：<see cref="CanRead"/> 由是否提供读回调决定，
/// <see cref="CanWrite"/> 由是否提供写回调决定，<see cref="CanSeek"/> 必须同时提供
/// 定位回调、长度回调与当前位置回调。未声明能力时调用对应方法会抛 <see cref="NotSupportedException"/>。
/// </remarks>
/// <example>
/// <code>
/// var stream = new CallbackStream(
///     read: (buffer, offset, count) => socket.Read(buffer, offset, count),
///     canSeek: false);
/// var reader = new WaveFileReader(stream);
/// </code>
/// </example>
public sealed class CallbackStream : Stream
{
    private readonly Func<Byte[], Int32, Int32, Int32> _read;
    private readonly Action<Byte[], Int32, Int32> _write;
    private readonly Func<Int64> _length;
    private readonly Func<Int64> _position;
    private readonly Action<Int64> _seek;

    /// <summary>创建回调式数据流</summary>
    /// <param name="read">读取回调 (buffer, offset, count) → 实际读取字节数</param>
    /// <param name="write">写入回调 (buffer, offset, count)</param>
    /// <param name="length">长度回调（字节）</param>
    /// <param name="position">当前位置回调（字节）</param>
    /// <param name="seek">定位回调（目标绝对位置）</param>
    public CallbackStream(
        Func<Byte[], Int32, Int32, Int32> read = null,
        Action<Byte[], Int32, Int32> write = null,
        Func<Int64> length = null,
        Func<Int64> position = null,
        Action<Int64> seek = null)
    {
        _read = read;
        _write = write;
        _length = length;
        _position = position;
        _seek = seek;
    }

    /// <summary>是否可读（提供读回调即可读）</summary>
    public override Boolean CanRead => _read != null;

    /// <summary>是否可写（提供写回调即可写）</summary>
    public override Boolean CanWrite => _write != null;

    /// <summary>是否可定位（需同时提供长度、位置与定位回调）</summary>
    public override Boolean CanSeek => _seek != null && _position != null && _length != null;

    /// <summary>数据长度（字节）</summary>
    /// <exception cref="NotSupportedException">未提供长度回调</exception>
    public override Int64 Length => _length != null
        ? _length()
        : throw new NotSupportedException(L.T("The stream does not support seeking."));

    /// <summary>当前位置（字节）</summary>
    /// <exception cref="NotSupportedException">未提供位置回调</exception>
    public override Int64 Position
    {
        get => _position != null ? _position() : throw new NotSupportedException(L.T("The stream does not support seeking."));
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <summary>读取数据</summary>
    /// <param name="buffer">目标缓冲区</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="count">最大字节数</param>
    /// <returns>实际读取字节数</returns>
    public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count)
    {
        if (_read == null) throw new NotSupportedException(L.T("The stream does not support reading."));

        return _read(buffer, offset, count);
    }

    /// <summary>写入数据</summary>
    /// <param name="buffer">源缓冲区</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="count">字节数</param>
    public override void Write(Byte[] buffer, Int32 offset, Int32 count)
    {
        if (_write == null) throw new NotSupportedException(L.T("The stream does not support writing."));

        _write(buffer, offset, count);
    }

    /// <summary>定位</summary>
    /// <param name="offset">偏移量</param>
    /// <param name="origin">参考点</param>
    /// <returns>定位后的绝对位置</returns>
    public override Int64 Seek(Int64 offset, SeekOrigin origin)
    {
        if (_seek == null || _position == null) throw new NotSupportedException(L.T("The stream does not support seeking."));

        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position() + offset,
            _ => Length + offset,
        };

        _seek(target);
        return _position();
    }

    /// <summary>设置长度（不支持）</summary>
    /// <param name="value">新长度</param>
    public override void SetLength(Int64 value) => throw new NotSupportedException(L.T("The stream does not support writing."));

    /// <summary>刷新（转发到写入回调所有权方，默认空实现）</summary>
    public override void Flush() { }
}

/// <summary>音频数据流工厂。统一路径、字节数组与回调三种数据源/目标，并显式暴露能力</summary>
public static class AudioStreamFactory
{
    /// <summary>按路径打开只读流</summary>
    /// <param name="path">文件路径</param>
    /// <returns>可定位只读流</returns>
    public static Stream OpenRead(String path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));

        return File.OpenRead(path);
    }

    /// <summary>按路径创建只写流</summary>
    /// <param name="path">文件路径</param>
    /// <param name="append">是否追加，默认 false（覆盖）</param>
    /// <returns>可定位只写流</returns>
    public static Stream Create(String path, Boolean append = false)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));

        return append ? new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read) : File.Create(path);
    }

    /// <summary>从字节数组创建只读可定位流（不复制数据）</summary>
    /// <param name="data">数据</param>
    /// <returns>只读流</returns>
    public static Stream FromBytes(Byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));

        return new MemoryStream(data, false);
    }

    /// <summary>从读回调创建不可定位数据源</summary>
    /// <param name="read">读取回调</param>
    /// <returns>只读流，<see cref="Stream.CanSeek"/> 为 false</returns>
    public static Stream FromCallback(Func<Byte[], Int32, Int32, Int32> read)
    {
        if (read == null) throw new ArgumentNullException(nameof(read));

        return new CallbackStream(read: read);
    }

    /// <summary>从读回调创建可定位数据源</summary>
    /// <param name="read">读取回调</param>
    /// <param name="length">长度回调</param>
    /// <param name="position">当前位置回调</param>
    /// <param name="seek">定位回调（接收绝对位置）</param>
    /// <returns>可定位只读流</returns>
    public static Stream FromCallback(Func<Byte[], Int32, Int32, Int32> read, Func<Int64> length, Func<Int64> position, Action<Int64> seek)
    {
        if (read == null) throw new ArgumentNullException(nameof(read));
        if (length == null) throw new ArgumentNullException(nameof(length));
        if (position == null) throw new ArgumentNullException(nameof(position));
        if (seek == null) throw new ArgumentNullException(nameof(seek));

        return new CallbackStream(read: read, length: length, position: position, seek: seek);
    }

    /// <summary>从写回调创建只写目标</summary>
    /// <param name="write">写入回调</param>
    /// <returns>只写流，<see cref="Stream.CanSeek"/> 为 false</returns>
    public static Stream CreateCallback(Action<Byte[], Int32, Int32> write)
    {
        if (write == null) throw new ArgumentNullException(nameof(write));

        return new CallbackStream(write: write);
    }

    /// <summary>探测任意数据源</summary>
    /// <param name="stream">数据源</param>
    /// <param name="leaveOpen">是否保留流</param>
    /// <returns>探测结果</returns>
    public static AudioStreamInfo Probe(Stream stream, Boolean leaveOpen = true) => AudioProbe.Probe(stream, leaveOpen);

    /// <summary>以 PCM 内容创建内存流（可定位）</summary>
    /// <param name="pcm">PCM 数据</param>
    /// <returns>可定位读写流</returns>
    public static Stream FromPcm(Byte[] pcm) => new MemoryStream(pcm ?? [], true);
}
