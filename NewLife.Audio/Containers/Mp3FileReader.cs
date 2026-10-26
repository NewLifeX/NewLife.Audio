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

    /// <summary>是否带 Xing/Info/VBRI 信息帧（VBR/CBR 头）</summary>
    public Boolean HasVbrHeader { get; private set; }

    /// <summary>信息帧记录的有效音频帧数（不含信息帧），未知为 0</summary>
    public Int64 VbrFrameCount { get; private set; }

    /// <summary>信息帧记录的文件字节数，未知为 0</summary>
    public Int64 VbrStreamBytes { get; private set; }
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

            if (_offsets.Count == 0 && !HasVbrHeader)
            {
                var frameHead = new Byte[Math.Min(info.FrameSize, 512)];
                _stream.Seek(pos, SeekOrigin.Begin);
                var got = 0;
                while (got < frameHead.Length)
                {
                    var n = _stream.Read(frameHead, got, frameHead.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
                ReadVbrHeader(frameHead, 0, info);
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
        var firstFrame = true;
        while (pos + 4 <= _buffer.Length)
        {
            var info = Mp3Codec.ParseFrameHeader(_buffer, pos);
            if (info == null || info.FrameSize <= 0 || pos + info.FrameSize > _buffer.Length)
            {
                if (pos + 3 <= _buffer.Length && _buffer[pos] == 'T' && _buffer[pos + 1] == 'A' && _buffer[pos + 2] == 'G') break;
                pos++;
                continue;
            }

            if (firstFrame)
            {
                ReadVbrHeader(_buffer, pos, info);
                firstFrame = false;
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

    /// <summary>解析 Xing/Info/VBRI 信息帧记录的帧数与字节数</summary>
    /// <param name="data">数据缓冲</param>
    /// <param name="offset">帧起始偏移</param>
    /// <param name="info">帧头</param>
    private void ReadVbrHeader(Byte[] data, Int32 offset, Mp3Codec.FrameHeader info)
    {
        var mpeg1 = info.Version == Mp3Codec.MpegVersion.Mpeg1;
        var mono = info.ChannelMode == 3;
        // 边信息长度：MPEG-1 单声道 17 / 立体声 32；MPEG-2/2.5 单声道 9 / 立体声 17
        var sideInfo = mpeg1 ? (mono ? 17 : 32) : (mono ? 9 : 17);
        // protection_bit 为 0 表示帧头后有 2 字节 CRC
        var hasCrc = offset + 1 < data.Length && (data[offset + 1] & 1) == 0;
        var mark = offset + 4 + (hasCrc ? 2 : 0) + sideInfo;

        if (mark + 8 > data.Length) return;

        var isXing = data[mark] == 'X' && data[mark + 1] == 'i' && data[mark + 2] == 'n' && data[mark + 3] == 'g';
        var isInfo = data[mark] == 'I' && data[mark + 1] == 'n' && data[mark + 2] == 'f' && data[mark + 3] == 'o';
        if (!isXing && !isInfo) return;

        HasVbrHeader = true;

        var flags = (UInt32)data[mark + 4] << 24 | (UInt32)data[mark + 5] << 16 | (UInt32)data[mark + 6] << 8 | data[mark + 7];
        var pos = mark + 8;

        if ((flags & 1) != 0 && pos + 4 <= data.Length)
        {
            VbrFrameCount = (Int64)((UInt32)data[pos] << 24 | (UInt32)data[pos + 1] << 16 | (UInt32)data[pos + 2] << 8 | data[pos + 3]);
            pos += 4;
        }

        if ((flags & 2) != 0 && pos + 4 <= data.Length)
        {
            VbrStreamBytes = (Int64)((UInt32)data[pos] << 24 | (UInt32)data[pos + 1] << 16 | (UInt32)data[pos + 2] << 8 | data[pos + 3]);
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
