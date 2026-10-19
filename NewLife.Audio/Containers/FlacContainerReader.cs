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
                bitsPerSample = (Byte)(((info[12] & 0x01) << 4) | ((info[13] & 0xF0) >> 4) + 1);
                totalSamples = ((UInt64)(info[13] & 0x0F) << 32) | ((UInt64)info[14] << 24) |
                               ((UInt64)info[15] << 16) | ((UInt64)info[16] << 8) | info[17];
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

        TotalFrames = totalSamples > 0 ? (Int64)(totalSamples / blockSizeMax) : 0;
        Duration = sampleRate > 0 ? (Double)totalSamples / sampleRate : 0;
    }

    /// <summary>读取下一帧编码数据（FLAC 帧边界识别）</summary>
    public IPacket ReadFrame()
    {
        if (_currentPos >= _stream.Length) return null;

        _stream.Seek(_currentPos, SeekOrigin.Begin);

        // 读取足够的字节以找到帧边界
        var read = _stream.Read(_readBuffer, 0, _readBuffer.Length);
        if (read < 6) return null;

        // 搜索帧同步码
        var frameStart = -1;
        for (var i = 0; i < read - 1; i++)
        {
            var word = (Int16)((_readBuffer[i] << 8) | _readBuffer[i + 1]);
            if ((word >> 2) == FrameSyncCode)
            {
                frameStart = i;
                break;
            }
        }

        if (frameStart < 0)
        {
            _currentPos += read;
            return new ArrayPacket(_readBuffer, 0, read);
        }

        // 搜索下一帧同步码以确定当前帧长度
        var nextFrameStart = -1;
        for (var i = frameStart + 6; i < read - 1; i++)
        {
            var word = (Int16)((_readBuffer[i] << 8) | _readBuffer[i + 1]);
            if ((word >> 2) == FrameSyncCode)
            {
                nextFrameStart = i;
                break;
            }
        }

        if (nextFrameStart > frameStart)
        {
            // 找到完整帧边界
            var frameLen = nextFrameStart - frameStart;
            _currentPos += nextFrameStart;
            return new ArrayPacket(_readBuffer, frameStart, frameLen);
        }

        // 未找到下一帧，返回从当前帧开始的所有数据
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
}
