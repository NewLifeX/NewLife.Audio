using NewLife.Audio.Codecs;
using NewLife.Audio.DSP;
using NewLife.Data;

namespace NewLife.Audio.Containers;

/// <summary>MP3 文件读取器</summary>
/// <remarks>
/// 解析可选的 ID3v2 标签，扫描全部 MPEG 音频帧建立帧索引，据此给出精确的总帧数与时长（CBR/VBR 均适用）。
/// <para>可寻址流使用「读帧头 + 跳帧」方式扫描，不把整段文件读入内存；不可寻址流（管道、网络流）回退为整段缓存后再扫描。
/// 遇到 ID3v1 尾标签、截断帧或厂商附加数据时停止扫描，已识别的帧仍然可用。</para>
/// </remarks>
public class Mp3FileReader : IAudioContainerReader
{
    #region 字段
    private readonly Stream _stream;
    private readonly Byte[] _buffer;
    private readonly List<Int64> _offsets = [];
    private AudioFormat _format;
    private Int32 _current;
    #endregion

    #region 属性
    /// <summary>音频格式</summary>
    public AudioFormat Format => _format;

    /// <summary>编码类型，固定为 MP3</summary>
    public AVTypes CodecType => AVTypes.MP3;

    /// <summary>总帧数（MPEG 音频帧）</summary>
    public Int64 TotalFrames => _offsets.Count;

    /// <summary>总时长（秒）</summary>
    public Double Duration => _format != null && _format.SampleRate > 0
        ? (Double)TotalFrames * _format.SamplesPerFrame / _format.SampleRate
        : 0;

    /// <summary>元数据（来自 ID3v2 标签）</summary>
    public AudioMetadata Metadata { get; } = new();

    /// <summary>平均比特率（bps），无帧时为 0</summary>
    public Int32 AverageBitrate { get; private set; }
    #endregion

    #region 构造
    /// <summary>从流读取 MP3 文件</summary>
    /// <param name="stream">输入流，建议可寻址以获得更低的内存占用</param>
    public Mp3FileReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));

        if (_stream.CanSeek)
        {
            _buffer = null;
            ScanSeekable();
        }
        else
        {
            using var ms = new MemoryStream();
            _stream.CopyTo(ms);
            _buffer = ms.ToArray();
            ScanBuffer();
        }

        _format ??= new AudioFormat { SampleRate = 44100, Channels = 2, BitsPerSample = 16, Encoding = AVTypes.MP3 };
    }
    #endregion

    #region 方法
    /// <summary>读取下一帧编码数据</summary>
    /// <returns>MP3 帧数据，null 表示结束</returns>
    public IPacket ReadFrame()
    {
        if (_current >= _offsets.Count) return null;

        var offset = _offsets[_current++];
        if (_buffer != null)
        {
            // 帧长度由帧头决定，读满一帧
            var header = Mp3Codec.ParseFrameHeader(_buffer, (Int32)offset);
            var size = header?.FrameSize ?? 0;
            if (size <= 0 || offset + size > _buffer.Length) return null;

            return new ArrayPacket(_buffer, (Int32)offset, size);
        }

        _stream.Seek(offset, SeekOrigin.Begin);
        var head = new Byte[4];
        if (_stream.Read(head, 0, 4) < 4) return null;

        var info = Mp3Codec.ParseFrameHeader(head, 0);
        var frameSize = info?.FrameSize ?? 0;
        if (frameSize <= 0) return null;

        var data = new Byte[frameSize];
        Array.Copy(head, 0, data, 0, 4);
        var read = 4;
        while (read < frameSize)
        {
            var n = _stream.Read(data, read, frameSize - read);
            if (n <= 0) break;
            read += n;
        }

        return read > 0 ? (IPacket)new ArrayPacket(data, 0, read) : null;
    }

    /// <summary>定位到指定帧</summary>
    /// <param name="frameIndex">帧索引</param>
    public void SeekFrame(Int64 frameIndex)
    {
        if (frameIndex < 0) frameIndex = 0;
        if (frameIndex > _offsets.Count) frameIndex = _offsets.Count;

        _current = (Int32)frameIndex;
        if (_buffer == null && frameIndex < _offsets.Count) _stream.Seek(_offsets[(Int32)frameIndex], SeekOrigin.Begin);
    }

    /// <summary>释放</summary>
    public void Dispose() => _stream?.Dispose();
    #endregion

    #region 扫描
    private void ScanSeekable()
    {
        var start = _stream.Position;
        _stream.Seek(0, SeekOrigin.Begin);

        // ID3v2 标签
        var head = new Byte[10];
        if (_stream.Read(head, 0, head.Length) == head.Length)
        {
            var tagLength = 0;
            if (head[0] == 'I' && head[1] == 'D' && head[2] == '3')
            {
                var size = (head[6] & 0x7F) << 21 | (head[7] & 0x7F) << 14 | (head[8] & 0x7F) << 7 | (head[9] & 0x7F);
                tagLength = 10 + size + ((head[5] & 0x10) != 0 ? 10 : 0);
            }

            if (tagLength > 0 && tagLength <= _stream.Length)
            {
                var tag = new Byte[tagLength];
                _stream.Seek(0, SeekOrigin.Begin);
                var read = 0;
                while (read < tag.Length)
                {
                    var n = _stream.Read(tag, read, tag.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read > 0) Mp3Id3Tag.TryRead(tag, Metadata, out _);
            }

            _stream.Seek(tagLength, SeekOrigin.Begin);
        }

        var length = _stream.Length;
        var pos = _stream.Position;
        var header = new Byte[4];

        while (pos + 4 <= length)
        {
            _stream.Seek(pos, SeekOrigin.Begin);
            if (_stream.Read(header, 0, 4) < 4) break;

            var info = Mp3Codec.ParseFrameHeader(header, 0);
            if (info == null || info.FrameSize <= 0 || pos + info.FrameSize > length)
            {
                // 尾部可能是 ID3v1 标签或附加数据，尝试寻找下一个同步字
                if (header[0] == 'T' && header[1] == 'A' && header[2] == 'G') break;
                pos++;
                continue;
            }

            _offsets.Add(pos);
            _format ??= CreateFormat(info);
            pos += info.FrameSize;
        }

        if (_offsets.Count > 0 && _format != null)
        {
            var bytes = pos - (_offsets.Count > 0 ? _offsets[0] : 0);
            AverageBitrate = (Int32)(bytes * 8 / Math.Max(Duration, 1e-6));
        }

        // 尾部 ID3v1 标签（ID3v2 已填的字段不会被覆盖）
        if (length >= Mp3Id3Tag.V1Size)
        {
            var tag = new Byte[Mp3Id3Tag.V1Size];
            _stream.Seek(length - Mp3Id3Tag.V1Size, SeekOrigin.Begin);
            var read = 0;
            while (read < tag.Length)
            {
                var n = _stream.Read(tag, read, tag.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read == tag.Length) Mp3Id3Tag.TryReadV1(tag, 0, Metadata);
        }

        if (start == 0) _stream.Seek(0, SeekOrigin.Begin);
    }

    private void ScanBuffer()
    {
        var pos = Mp3Id3Tag.Skip(_buffer);
        while (pos + 4 <= _buffer.Length)
        {
            var info = Mp3Codec.ParseFrameHeader(_buffer, pos);
            if (info == null || info.FrameSize <= 0 || pos + info.FrameSize > _buffer.Length)
            {
                if (pos + 3 <= _buffer.Length && _buffer[pos] == 'T' && _buffer[pos + 1] == 'A' && _buffer[pos + 2] == 'G') break;
                pos++;
                continue;
            }

            _offsets.Add(pos);
            _format ??= CreateFormat(info);
            pos += info.FrameSize;
        }

        if (_buffer.Length >= Mp3Id3Tag.HeaderSize) Mp3Id3Tag.TryRead(_buffer, Metadata, out _);
        if (_buffer.Length >= Mp3Id3Tag.V1Size) Mp3Id3Tag.TryReadV1(_buffer, _buffer.Length - Mp3Id3Tag.V1Size, Metadata);

        if (_offsets.Count > 0 && _format != null)
        {
            var bytes = pos - _offsets[0];
            AverageBitrate = (Int32)(bytes * 8 / Math.Max(Duration, 1e-6));
        }
    }

    private static AudioFormat CreateFormat(Mp3Codec.FrameHeader info) => new()
    {
        SampleRate = info.SampleRate,
        Channels = info.ChannelMode == 3 ? 1 : 2,
        BitsPerSample = 16,
        SamplesPerFrame = info.SamplesPerFrame,
        Encoding = AVTypes.MP3,
    };
    #endregion
}
