using System;
using System.IO;
using NewLife.Audio.DSP;
using NewLife.Data;

namespace NewLife.Audio.Containers;

/// <summary>CAF（Apple Core Audio Format）文件读取器</summary>
/// <remarks>
/// 解析 <c>caff</c> 头、<c>desc</c> 描述块与 <c>data</c> 数据块，支持未压缩 <c>lpcm</c>：
/// 8/16/24/32 位整型与 32/64 位浮点，按描述块中的大小端标记解码，统一转换为 16 位小端输出。
/// </remarks>
public class CafFileReader : IAudioContainerReader
{
    private readonly Stream _stream;
    private readonly AudioFormat _format;
    private readonly Int64 _dataOffset;
    private readonly Int64 _dataSize;
    private readonly Int64 _totalFrames;
    private readonly Int32 _bytesPerSample;
    private readonly Boolean _isFloat;
    private readonly Boolean _bigEndian;
    private Int64 _position;

    /// <summary>音频格式</summary>
    public AudioFormat Format => _format;

    /// <summary>编码类型</summary>
    public AVTypes CodecType => AVTypes.LPCM;

    /// <summary>总帧数</summary>
    public Int64 TotalFrames => _totalFrames;

    /// <summary>总时长（秒）</summary>
    public Double Duration => _format.SampleRate > 0 ? (Double)_totalFrames / _format.SampleRate : 0;

    /// <summary>元数据</summary>
    public AudioMetadata Metadata { get; } = new();

    /// <summary>从流读取 CAF 文件</summary>
    /// <param name="stream">输入流</param>
    /// <exception cref="InvalidDataException">不是有效的 CAF 数据或不是未压缩 PCM</exception>
    public CafFileReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));

        var header = new Byte[8];
        if (!ReadFully(header, 0, 8)) throw new InvalidDataException("Not a valid CAF file.");
        if (header[0] != 'c' || header[1] != 'a' || header[2] != 'f' || header[3] != 'f') throw new InvalidDataException("Not a valid CAF file.");

        Int32 sampleRate = 0;
        Int32 channels = 0;
        Int32 bits = 0;
        Int32 bytesPerPacket = 0;
        Int32 framesPerPacket = 1;
        var isFloat = false;
        var bigEndian = false;
        var haveDesc = false;
        _dataOffset = -1;
        _dataSize = -1;

        while (true)
        {
            var chunk = new Byte[12];
            if (!ReadFully(chunk, 0, 12)) break;

            var id = System.Text.Encoding.ASCII.GetString(chunk, 0, 4);
            var size = ReadInt64(chunk, 4);
            var body = size < 0 || size > Int32.MaxValue ? -1 : (Int32)size;

            switch (id)
            {
                case "desc":
                    {
                        var desc = new Byte[32];
                        var got = ReadFully(desc, 0, 32) ? 32 : 0;
                        if (got == 32)
                        {
                            sampleRate = (Int32)Math.Round(ReadDouble(desc, 0));
                            var formatId = System.Text.Encoding.ASCII.GetString(desc, 8, 4);
                            var flags = ReadInt32(desc, 12);
                            bytesPerPacket = ReadInt32(desc, 16);
                            framesPerPacket = Math.Max(1, ReadInt32(desc, 20));
                            channels = ReadInt32(desc, 24);
                            bits = ReadInt32(desc, 28);

                            if (formatId != "lpcm") throw new InvalidDataException($"Unsupported CAF codec: {formatId}");

                            isFloat = (flags & 0x01) != 0;
                            bigEndian = (flags & 0x02) != 0;
                            haveDesc = true;
                        }
                        Skip(body < 0 ? 0 : body - got);
                        break;
                    }

                case "data":
                    {
                        // 前 4 字节为 editCount
                        var head = new Byte[4];
                        var got = ReadFully(head, 0, 4) ? 4 : 0;
                        var start = _stream.CanSeek ? _stream.Position : 0;
                        _dataOffset = start;
                        _dataSize = body < 0 ? (_stream.CanSeek ? _stream.Length - start : -1) : body - 4;

                        if (!haveDesc) throw new InvalidDataException("Missing CAF desc chunk.");

                        _bytesPerSample = bytesPerPacket > 0 ? bytesPerPacket : bits / 8;
                        _isFloat = isFloat;
                        _bigEndian = bigEndian;
                        _format = new AudioFormat
                        {
                            SampleRate = sampleRate > 0 ? sampleRate : 44100,
                            Channels = channels > 0 ? channels : 1,
                            BitsPerSample = 16,
                            SamplesPerFrame = framesPerPacket,
                            Encoding = AVTypes.LPCM,
                        };
                        _totalFrames = _dataSize > 0 && _bytesPerSample > 0 && _format.Channels > 0
                            ? _dataSize / _bytesPerSample / _format.Channels
                            : 0;

                        if (_stream.CanSeek) _stream.Seek(start, SeekOrigin.Begin);

                        return;
                    }

                default:
                    if (body < 0) throw new InvalidDataException("Unsupported CAF chunk size.");
                    Skip(body);
                    break;
            }
        }

        throw new InvalidDataException("Missing CAF data chunk.");
    }

    /// <summary>读取下一帧 PCM（16 位小端交错）</summary>
    /// <returns>PCM 帧，末尾返回 null</returns>
    public IPacket ReadFrame()
    {
        var blockAlign = _bytesPerSample * _format.Channels;
        var frameBytes = Math.Max(1, _format.SamplesPerFrame) * blockAlign;
        var remaining = _dataSize - _position * blockAlign;
        if (remaining <= 0) return null;
        if (frameBytes > remaining) frameBytes = (Int32)remaining;

        var buffer = new Byte[frameBytes];
        if (!ReadFully(buffer, 0, frameBytes)) return null;

        _position += frameBytes / Math.Max(1, blockAlign);

        return new ArrayPacket(ConvertToPcm16(buffer));
    }

    /// <summary>定位到指定帧</summary>
    /// <param name="frameIndex">帧索引</param>
    public void SeekFrame(Int64 frameIndex)
    {
        if (frameIndex < 0) frameIndex = 0;
        if (frameIndex > _totalFrames) frameIndex = _totalFrames;

        _stream.Seek(_dataOffset + frameIndex * _bytesPerSample * _format.Channels, SeekOrigin.Begin);
        _position = frameIndex;
    }

    /// <summary>释放</summary>
    public void Dispose() => _stream?.Dispose();

    #region 辅助
    private Byte[] ConvertToPcm16(Byte[] data)
    {
        var samples = data.Length / _bytesPerSample;
        var result = new Byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var p = i * _bytesPerSample;
            Int32 v;
            if (_isFloat)
            {
                var value = _bytesPerSample == 8
                    ? (Single)ReadDoubleEndian(data, p)
                    : BitConverter.ToSingle(BitConverter.GetBytes(ReadInt32Endian(data, p)), 0);
                var scaled = (Int32)Math.Round(value * 32767);
                v = Clamp(scaled);
            }
            else
            {
                v = _bytesPerSample switch
                {
                    1 => (data[p] - 128) << 8,
                    3 => ReadInt24(data, p) >> 8,
                    4 => ReadInt32Endian(data, p) >> 16,
                    _ => ReadInt16(data, p),
                };
            }

            result[i * 2] = (Byte)v;
            result[i * 2 + 1] = (Byte)(v >> 8);
        }

        return result;
    }

    private Int32 Clamp(Int32 value)
    {
        if (value > Int16.MaxValue) return Int16.MaxValue;
        if (value < Int16.MinValue) return Int16.MinValue;

        return value;
    }

    private Int32 ReadInt16(Byte[] data, Int32 offset) => _bigEndian
        ? (Int16)(data[offset] << 8 | data[offset + 1])
        : (Int16)(data[offset] | data[offset + 1] << 8);

    private Int32 ReadInt24(Byte[] data, Int32 offset)
    {
        var v = _bigEndian
            ? data[offset] << 16 | data[offset + 1] << 8 | data[offset + 2]
            : data[offset + 2] << 16 | data[offset + 1] << 8 | data[offset];

        return (v & 0x800000) != 0 ? v | ~0xFFFFFF : v;
    }

    private Int32 ReadInt32Endian(Byte[] data, Int32 offset) => _bigEndian
        ? ReadInt32(data, offset)
        : data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24;

    private Double ReadDoubleEndian(Byte[] data, Int32 offset) => _bigEndian
        ? ReadDouble(data, offset)
        : BitConverter.Int64BitsToDouble((Int64)(UInt32)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24)
            | (Int64)ReadInt32(data, offset + 4) << 32);

    private static Int32 ReadInt32(Byte[] data, Int32 offset) =>
        data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3];

    private static Int64 ReadInt64(Byte[] data, Int32 offset) =>
        (Int64)ReadInt32(data, offset) << 32 | (UInt32)ReadInt32(data, offset + 4);

    private static Double ReadDouble(Byte[] data, Int32 offset) =>
        BitConverter.Int64BitsToDouble(ReadInt64(data, offset));

    private Boolean ReadFully(Byte[] buffer, Int32 offset, Int32 count)
    {
        var read = 0;
        while (read < count)
        {
            var n = _stream.Read(buffer, offset + read, count - read);
            if (n <= 0) return false;
            read += n;
        }

        return true;
    }

    private void Skip(Int64 count)
    {
        if (count <= 0) return;

        if (_stream.CanSeek)
            _stream.Seek(count, SeekOrigin.Current);
        else
        {
            var buffer = new Byte[Math.Min(count, 4096)];
            var left = count;
            while (left > 0)
            {
                var n = _stream.Read(buffer, 0, (Int32)Math.Min(left, buffer.Length));
                if (n <= 0) break;
                left -= n;
            }
        }
    }
    #endregion
}
