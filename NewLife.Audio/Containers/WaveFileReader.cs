using NewLife;
using NewLife.Audio.DSP;
using NewLife.Data;

namespace NewLife.Audio.Containers;

/// <summary>WAV 文件读取器。按 RIFF 块遍历解析 WAV/RF64 容器格式</summary>
/// <remarks>
/// 顺序读取 chunk 头并跳过未知块，因此能正确处理 <c>fmt </c> 为 16/18/40 字节、<c>WAVE_FORMAT_EXTENSIBLE</c>、
/// IEEE 浮点、24/32 位，以及 <c>data</c> 之前存在 <c>LIST</c>/<c>fact</c>/<c>bext</c> 等块的文件；
/// RF64 的 64 位数据长度由 <c>ds64</c> 块提供。数据长度未知（流式写入）时读取到流末尾。
/// </remarks>
public class WaveFileReader : IAudioContainerReader
{
    private const UInt32 RiffMagic = 0x46464952;   // "RIFF"
    private const UInt32 Rf64Magic = 0x34364652;   // "RF64"
    private const UInt32 WaveMagic = 0x45564157;   // "WAVE"
    private const UInt32 FmtMagic = 0x20746D66;    // "fmt "
    private const UInt32 DataMagic = 0x61746164;   // "data"
    private const UInt32 Ds64Magic = 0x34367364;   // "ds64"
    private const UInt32 ListMagic = 0x5453494C;   // "LIST"

    private readonly Stream _stream;
    private readonly AudioFormat _format;
    private Int64 _dataOffset;
    private Int64 _dataSize;
    private Int64 _totalFrames;
    private Int64 _currentFrame;
    private Int64 _ds64DataSize = -1;

    /// <summary>音频格式</summary>
    public AudioFormat Format => _format;

    /// <summary>WAV 文件通常包含 PCM 数据</summary>
    public AVTypes CodecType => Format?.Encoding ?? AVTypes.LPCM;

    /// <summary>总帧数。数据长度未知时为 0</summary>
    public Int64 TotalFrames => _totalFrames;

    /// <summary>总时长（秒）</summary>
    public Double Duration => _totalFrames > 0 && _format.SampleRate > 0
        ? (Double)_totalFrames / _format.SampleRate * _format.SamplesPerFrame
        : 0;

    /// <summary>元数据</summary>
    public AudioMetadata Metadata { get; } = new();

    /// <summary>从流读取 WAV 文件</summary>
    /// <param name="stream">输入流（需位于文件开头）</param>
    /// <exception cref="InvalidDataException">不是有效的 RIFF/WAVE 数据，或缺少 fmt 块</exception>
    public WaveFileReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _format = ParseHeader();

        var frameBytes = FrameBytes;
        _totalFrames = _dataSize > 0 && frameBytes > 0 ? _dataSize / frameBytes : 0;
    }

    private Int32 FrameBytes => _format.BytesPerSample * _format.Channels * _format.SamplesPerFrame;

    /// <summary>读取下一帧 PCM 数据</summary>
    /// <returns>PCM 帧；到达数据末尾返回 null</returns>
    public IPacket ReadFrame()
    {
        var frameBytes = FrameBytes;
        if (frameBytes <= 0) return null;

        if (_dataSize >= 0)
        {
            var consumed = _stream.CanSeek ? _stream.Position - _dataOffset : _currentFrame * (Int64)frameBytes;
            var remaining = _dataSize - consumed;
            if (remaining <= 0) return null;
            if (remaining < frameBytes) frameBytes = (Int32)remaining;
        }

        var buffer = new Byte[frameBytes];
        var read = ReadFully(buffer, 0, frameBytes);
        if (read <= 0) return null;

        _currentFrame++;
        return new ArrayPacket(buffer, 0, read);
    }

    /// <summary>定位到指定帧</summary>
    /// <param name="frameIndex">帧索引</param>
    public void SeekFrame(Int64 frameIndex)
    {
        if (frameIndex < 0) frameIndex = 0;

        var frameBytes = FrameBytes;
        var offset = _dataOffset + frameIndex * frameBytes;
        if (_dataSize >= 0 && offset > _dataOffset + _dataSize) offset = _dataOffset + _dataSize;

        _stream.Seek(offset, SeekOrigin.Begin);
        _currentFrame = frameIndex;
    }

    /// <summary>释放</summary>
    public void Dispose() => _stream?.Dispose();

    private AudioFormat ParseHeader()
    {
        var header = new Byte[12];
        if (ReadFully(header, 0, 12) < 12) throw new InvalidDataException(L.T("Not a valid WAV file."));

        var riff = BitConverter.ToUInt32(header, 0);
        if (riff != RiffMagic && riff != Rf64Magic) throw new InvalidDataException(L.T("Not a valid WAV file."));
        if (BitConverter.ToUInt32(header, 8) != WaveMagic) throw new InvalidDataException(L.T("Not a valid WAV file."));

        UInt16 tag = 0;
        Int32 channels = 0;
        Int32 sampleRate = 0;
        Int32 bitsPerSample = 0;
        var haveFmt = false;

        _dataOffset = -1;
        _dataSize = -1;

        while (true)
        {
            var pos = _stream.CanSeek ? _stream.Position : -1;
            var chunk = new Byte[8];
            if (ReadFully(chunk, 0, 8) < 8) break;

            var id = BitConverter.ToUInt32(chunk, 0);
            var size = (Int64)BitConverter.ToUInt32(chunk, 4);

            switch (id)
            {
                case FmtMagic:
                    {
                        // fmt 块至少 16 字节；EXTENSIBLE 为 40 字节
                        var read = size < 64 ? (Int32)size : 64;
                        var fmt = new Byte[read];
                        var got = ReadFully(fmt, 0, read);

                        if (got >= 16)
                        {
                            tag = BitConverter.ToUInt16(fmt, 0);
                            channels = BitConverter.ToUInt16(fmt, 2);
                            sampleRate = BitConverter.ToInt32(fmt, 4);
                            bitsPerSample = BitConverter.ToUInt16(fmt, 14);
                            haveFmt = true;

                            // WAVEFORMATEXTENSIBLE：真实格式在 SubFormat GUID 起始 4 字节
                            if (tag == 0xFFFE && got >= 40) tag = (UInt16)BitConverter.ToUInt32(fmt, 24);
                        }

                        Skip(size - got + (size & 1));
                        break;
                    }

                case Ds64Magic:
                    {
                        // RF64：riffSize(8) dataSize(8) sampleCount(8)
                        var read = size < 28 ? (Int32)size : 28;
                        var ds = new Byte[read];
                        var got = ReadFully(ds, 0, read);
                        if (got >= 16) _ds64DataSize = (Int64)BitConverter.ToUInt64(ds, 8);

                        Skip(size - got + (size & 1));
                        break;
                    }

                case ListMagic:
                    {
                        // LIST/INFO：常见标签（INAM/IART/IPRD/…），解析后继续查找 data
                        var read = size < 65536 ? (Int32)size : 65536;
                        var list = new Byte[read];
                        var got = ReadFully(list, 0, read);
                        if (got >= 4) WaveInfoChunk.TryParse(list, 0, got, Metadata);

                        Skip(size - got + (size & 1));
                        break;
                    }

                case DataMagic:
                    {
                        if (!haveFmt)
                        {
                            // 非规范文件：数据块出现在 fmt 之前，跳过并继续查找 fmt
                            Skip(size + (size & 1));
                            break;
                        }

                        _dataOffset = _stream.CanSeek ? _stream.Position : 0;
                        _dataSize = ResolveDataSize(size);
                        if (_stream.CanSeek) _stream.Seek(_dataOffset, SeekOrigin.Begin);

                        return BuildFormat(tag, channels, sampleRate, bitsPerSample);
                    }

                default:
                    Skip(size + (size & 1));
                    break;
            }
        }

        if (!haveFmt) throw new InvalidDataException(L.T("Not a valid WAV file."));

        // 没有找到 data 块：视为空数据
        _dataOffset = _stream.CanSeek ? _stream.Position : 0;
        _dataSize = 0;

        return BuildFormat(tag, channels, sampleRate, bitsPerSample);
    }

    private Int64 ResolveDataSize(Int64 size)
    {
        // RF64 用 0xFFFFFFFF 占位，真实长度在 ds64 中
        if (size == 0xFFFFFFFF && _ds64DataSize >= 0) size = _ds64DataSize;

        if (size > 0) return size;

        // 流式写入（未知长度）或流可定位时按剩余长度计算
        if (_stream.CanSeek) return Math.Max(0, _stream.Length - _stream.Position);

        return -1;
    }

    private static AudioFormat BuildFormat(UInt16 tag, Int32 channels, Int32 sampleRate, Int32 bitsPerSample)
    {
        var encoding = tag switch
        {
            0x0006 => AVTypes.G711A,
            0x0007 => AVTypes.G711U,
            0x0011 => AVTypes.ADPCMA,
            _ => AVTypes.LPCM,
        };

        return new AudioFormat
        {
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = bitsPerSample,
            SamplesPerFrame = sampleRate / 50, // 20ms
            Encoding = encoding,
            IsFloat = tag == 0x0003,
        };
    }

    private Int32 ReadFully(Byte[] buffer, Int32 offset, Int32 count)
    {
        var total = 0;
        while (total < count)
        {
            var read = _stream.Read(buffer, offset + total, count - total);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    private void Skip(Int64 count)
    {
        if (count <= 0) return;

        if (_stream.CanSeek)
        {
            var target = _stream.Position + count;
            if (target > _stream.Length) target = _stream.Length;
            _stream.Seek(target, SeekOrigin.Begin);
            return;
        }

        var buffer = new Byte[4096];
        var remaining = count;
        while (remaining > 0)
        {
            var read = _stream.Read(buffer, 0, (Int32)Math.Min(buffer.Length, remaining));
            if (read <= 0) return;
            remaining -= read;
        }
    }
}
