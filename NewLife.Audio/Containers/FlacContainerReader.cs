using NewLife.Data;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Containers;

/// <summary>FLAC 原生容器读取器（fLaC 元数据解析）</summary>
public class FlacContainerReader : IAudioContainerReader
{
    private readonly Stream _stream;
    private Int64 _audioStart;
    private Int64 _currentPos;

    public AudioFormat Format { get; }
    public AVTypes CodecType => AVTypes.LPCM;
    public Int64 TotalFrames { get; }
    public Double Duration { get; }
    public AudioMetadata Metadata { get; } = new();

    public FlacContainerReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        var magic = new Byte[4];
        _stream.Read(magic, 0, 4);
        if (magic[0] != 'f' || magic[1] != 'L' || magic[2] != 'a' || magic[3] != 'C')
            throw new InvalidDataException("不是有效的 FLAC 文件");

        var sampleRate = 44100u;
        var channels = (Byte)2;
        var bitsPerSample = (Byte)16;
        var totalSamples = 0UL;

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
            SamplesPerFrame = 4096,
        };

        TotalFrames = totalSamples > 0 ? (Int64)(totalSamples / 4096) : 0;
        Duration = sampleRate > 0 ? (Double)totalSamples / sampleRate : 0;
    }

    public IPacket ReadFrame()
    {
        if (_currentPos >= _stream.Length) return null;
        _stream.Seek(_currentPos, SeekOrigin.Begin);
        var frameBuffer = new Byte[1024];
        var read = _stream.Read(frameBuffer, 0, frameBuffer.Length);
        if (read == 0) return null;
        _currentPos = _stream.Position;
        return new ArrayPacket(frameBuffer, 0, read);
    }

    public void SeekFrame(Int64 frameIndex)
    {
        _currentPos = _audioStart + frameIndex * 10000;
        _stream.Seek(_currentPos, SeekOrigin.Begin);
    }

    public void Dispose() => _stream?.Dispose();
}
