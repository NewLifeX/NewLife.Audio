using NewLife.Buffers;
using NewLife.Data;
using NewLife.Audio.DSP;

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

    public AudioFormat Format { get; }
    public AVTypes CodecType => AVTypes.LPCM;
    public Int64 TotalFrames { get; }
    public Double Duration { get; }
    public AudioMetadata Metadata { get; } = new();

    public FlacContainerReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _readBuffer = new Byte[65536]; // 64KB 缓冲区

        var magic = new Byte[4];
        _stream.Read(magic, 0, 4);
        if (magic[0] != 'f' || magic[1] != 'L' || magic[2] != 'a' || magic[3] != 'C')
            throw new InvalidDataException("不是有效的 FLAC 文件");

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

    public void Dispose() => _stream?.Dispose();
}
