using NewLife.Buffers;
using NewLife.Data;
using NewLife.Audio.DSP;
using NewLife;

namespace NewLife.Audio.Containers;

/// <summary>FLAC 原生容器读取器（fLaC 元数据解析 + 帧边界读取）</summary>
public class FlacContainerReader : IAudioContainerReader
{
    private readonly Stream _stream;
    private Int64 _audioStart;
    private Int64 _currentPos;
    private readonly Byte[] _readBuffer;

    /// <summary>FLAC 帧同步码（14-bit: 0x3FFE）</summary>
    private const Int16 FrameSyncCode = 0x3FFE;

    /// <summary>音频格式（采样率、声道数、位深、每帧采样数）</summary>
    public AudioFormat Format { get; }

    /// <summary>编解码类型，FLAC 容器统一按 LPCM 暴露</summary>
    public AVTypes CodecType => AVTypes.LPCM;

    /// <summary>总帧数；STREAMINFO 缺失总采样数时为 0</summary>
    public Int64 TotalFrames { get; }

    /// <summary>音频总时长（秒）</summary>
    public Double Duration { get; }

    /// <summary>FLAC 元数据（标题、艺术家、专辑等）</summary>
    public AudioMetadata Metadata { get; } = new();

    /// <summary>从流中解析 fLaC 标记与 STREAMINFO 元数据</summary>
    /// <param name="stream">FLAC 数据流，读取后定位到音频帧起始位置</param>
    public FlacContainerReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _readBuffer = new Byte[65536]; // 64KB 缓冲区

        var magic = new Byte[4];
        _stream.Read(magic, 0, 4);
        if (magic[0] != 'f' || magic[1] != 'L' || magic[2] != 'a' || magic[3] != 'C')
            throw new InvalidDataException(L.T("Not a valid FLAC file."));

        var sampleRate = 44100u;
        var channels = (Byte)2;
        var bitsPerSample = (Byte)16;
        var totalSamples = 0UL;
        var blockSizeMax = 4096u;

        var isLast = false;
        while (!isLast)
        {
            var header = _stream.ReadByte();
            if (header < 0) break;
            isLast = (header & 0x80) != 0;
            var blockType = header & 0x7F;
            var sizeBytes = new Byte[3];
            _stream.Read(sizeBytes, 0, 3);
            var blockSize = (sizeBytes[0] << 16) | (sizeBytes[1] << 8) | sizeBytes[2];

            if (blockType == 0 && blockSize >= 34)
            {
                var info = new Byte[34];
                _stream.Read(info, 0, 34);
                blockSizeMax = (UInt32)((info[2] << 8) | info[3]);
                sampleRate = (UInt32)((info[10] << 12) | (info[11] << 4) | (info[12] >> 4));
                channels = (Byte)(((info[12] & 0x0E) >> 1) + 1);
                bitsPerSample = (Byte)((((info[12] & 0x01) << 4) | ((info[13] & 0xF0) >> 4)) + 1);
                totalSamples = ((UInt64)(info[13] & 0x0F) << 32) | ((UInt64)info[14] << 24) |
                               ((UInt64)info[15] << 16) | ((UInt64)info[16] << 8) | info[17];
            }
            else if (blockType == 4 && blockSize > 0)
            {
                var comment = new Byte[blockSize];
                var read = _stream.Read(comment, 0, blockSize);
                if (read == blockSize) ReadVorbisComment(comment);
            }
            else
            {
                _stream.Seek(blockSize, SeekOrigin.Current);
            }
        }

        _audioStart = _stream.Position;
        _currentPos = _audioStart;

        Format = new AudioFormat
        {
            SampleRate = (Int32)sampleRate,
            Channels = channels,
            BitsPerSample = bitsPerSample,
            SamplesPerFrame = (Int32)blockSizeMax,
        };

        TotalFrames = totalSamples > 0 && blockSizeMax > 0 ? (Int64)(totalSamples / blockSizeMax) : 0;
        Duration = sampleRate > 0 ? (Double)totalSamples / sampleRate : 0;
    }

    /// <summary>读取下一帧编码数据（FLAC 帧边界识别）</summary>
    public IPacket ReadFrame()
    {
        if (_currentPos >= _stream.Length) return null;

        _stream.Seek(_currentPos, SeekOrigin.Begin);

        var read = _stream.Read(_readBuffer, 0, _readBuffer.Length);
        if (read < 6) return null;

        if (!TryFindFrame(_readBuffer, 0, read, out var frameStart)) return null;

        // 搜索下一帧以确定当前帧长度（用帧头 CRC-8 校验，避免把音频数据中的伪同步当作帧）
        if (TryFindFrame(_readBuffer, frameStart + 6, read, out var nextFrameStart))
        {
            _currentPos += nextFrameStart;
            return new ArrayPacket(_readBuffer, frameStart, nextFrameStart - frameStart);
        }

        _currentPos += read;
        return new ArrayPacket(_readBuffer, frameStart, read - frameStart);
    }

    /// <summary>定位到指定帧索引</summary>
    /// <param name="frameIndex">从 0 开始的帧索引</param>
    public void SeekFrame(Int64 frameIndex)
    {
        _currentPos = _audioStart;
        // 简化：顺序跳过 frameIndex 帧
        for (var i = 0; i < frameIndex; i++)
        {
            var frame = ReadFrame();
            if (frame == null) break;
        }
    }

    /// <summary>释放底层数据流</summary>
    public void Dispose() => _stream?.Dispose();

    private void ReadVorbisComment(Byte[] data)
    {        var offset = 0;
        if (!TryReadLE32(data, ref offset, out var vendorLength) || offset + vendorLength > data.Length) return;
        offset += vendorLength;

        if (!TryReadLE32(data, ref offset, out var count)) return;

        for (var i = 0; i < count && offset + 4 <= data.Length; i++)
        {
            if (!TryReadLE32(data, ref offset, out var length)) return;
            if (length < 0 || offset + length > data.Length) return;

            var text = System.Text.Encoding.UTF8.GetString(data, offset, length);
            offset += length;

            var index = text.IndexOf('=');
            if (index <= 0) continue;

            var key = text.Substring(0, index).ToUpperInvariant();
            var value = text.Substring(index + 1);

            switch (key)
            {
                case "TITLE": Metadata.Title = value; break;
                case "ARTIST": Metadata.Artist = value; break;
                case "ALBUM": Metadata.Album = value; break;
                case "GENRE": Metadata.Genre = value; break;
                case "COMMENT": Metadata.Comment = value; break;
                case "TRACKNUMBER":
                    if (Int32.TryParse(value, out var track)) Metadata.TrackNumber = track;
                    break;
                case "DATE":
                    if (Int32.TryParse(value.Length >= 4 ? value.Substring(0, 4) : value, out var year)) Metadata.Year = year;
                    break;
            }
        }
    }

    private static Boolean TryReadLE32(Byte[] data, ref Int32 offset, out Int32 value)
    {
        value = 0;
        if (offset + 4 > data.Length) return false;

        value = data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24;
        offset += 4;
        return true;
    }

    /// <summary>在缓冲区中查找下一个带合法帧头 CRC-8 的 FLAC 帧</summary>
    private static Boolean TryFindFrame(Byte[] buffer, Int32 start, Int32 end, out Int32 index)
    {
        for (var i = Math.Max(0, start); i + 6 <= end; i++)
        {
            if (buffer[i] != 0xFF || (buffer[i + 1] & 0xFC) != 0xF8) continue;
            if ((buffer[i + 3] & 0x01) != 0) continue;   // reserved 位必须为 0

            var pos = i + 4;
            var first = buffer[pos];

            // UTF-8 编码的帧号/样本号
            var count = 1;
            if ((first & 0x80) != 0)
            {
                var mask = 0x80;
                count = 0;
                while ((first & mask) != 0 && count < 6)
                {
                    count++;
                    mask >>= 1;
                }
                if (count < 2) continue;
            }
            pos += count;

            var blockCode = buffer[i + 2] >> 4;
            var rateCode = buffer[i + 2] & 0x0F;
            pos += blockCode switch { 6 => 1, 7 => 2, _ => 0 };
            pos += rateCode switch { 12 => 1, 13 or 14 => 2, _ => 0 };

            if (pos >= end) continue;
            if (Crc8(buffer, i, pos - i) != buffer[pos]) continue;

            index = i;
            return true;
        }

        index = -1;
        return false;
    }

    private static Byte Crc8(Byte[] data, Int32 offset, Int32 count)
    {
        Byte crc = 0;
        for (var i = 0; i < count; i++)
        {
            crc ^= data[offset + i];
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (Byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
            }
        }
        return crc;
    }
}
