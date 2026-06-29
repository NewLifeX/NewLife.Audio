using NewLife.Buffers;
using NewLife.Data;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Containers;

/// <summary>WAV 文件读取器。解析 RIFF/WAV 容器格式</summary>
public class WaveFileReader : IAudioContainerReader
{
    private readonly Stream _stream;
    private readonly AudioFormat _format;
    private Int64 _dataOffset;
    private Int32 _dataSize;
    private Int64 _totalFrames;
    private Int64 _currentFrame;

    /// <summary>音频格式</summary>
    public AudioFormat Format => _format;

    /// <summary>WAV 文件通常包含 PCM 数据</summary>
    public AVTypes CodecType => Format?.Encoding ?? AVTypes.LPCM;

    /// <summary>总帧数</summary>
    public Int64 TotalFrames => _totalFrames;

    /// <summary>总时长（秒）</summary>
    public Double Duration => _totalFrames > 0 && _format.SampleRate > 0
        ? (Double)_totalFrames / _format.SampleRate * _format.SamplesPerFrame
        : 0;

    /// <summary>元数据</summary>
    public AudioMetadata Metadata { get; } = new();

    /// <summary>从流读取 WAV 文件</summary>
    /// <param name="stream">输入流</param>
    public WaveFileReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _format = ParseHeader();

        var frameBytes = _format.BytesPerSample * _format.Channels * _format.SamplesPerFrame;
        _totalFrames = frameBytes > 0 ? _dataSize / frameBytes : 0;
    }

    /// <summary>读取下一帧 PCM 数据</summary>
    public IPacket ReadFrame()
    {
        if (_currentFrame >= _totalFrames) return null;

        var frameBytes = _format.BytesPerSample * _format.Channels * _format.SamplesPerFrame;
        var buffer = new Byte[frameBytes];
        var read = _stream.Read(buffer, 0, frameBytes);
        if (read == 0) return null;

        _currentFrame++;
        return new ArrayPacket(buffer, 0, read);
    }

    /// <summary>定位到指定帧</summary>
    public void SeekFrame(Int64 frameIndex)
    {
        var frameBytes = _format.BytesPerSample * _format.Channels * _format.SamplesPerFrame;
        var offset = _dataOffset + frameIndex * frameBytes;
        _stream.Seek(offset, SeekOrigin.Begin);
        _currentFrame = frameIndex;
    }

    /// <summary>释放</summary>
    public void Dispose() => _stream?.Dispose();

    private AudioFormat ParseHeader()
    {
        var header = new Byte[44];
        _stream.Read(header, 0, 44);

        var reader = new SpanReader(header.AsSpan());

        // RIFF
        if (reader.ReadUInt32() != 0x46464952u)
            throw new InvalidDataException("不是有效的 WAV 文件");
        reader.Advance(4); // file size

        // WAVE
        if (reader.ReadUInt32() != 0x45564157u)
            throw new InvalidDataException("不是有效的 WAV 文件");

        // fmt sub-chunk: skip "fmt " and chunk size
        reader.Advance(8);

        var audioFormat = reader.ReadInt16();
        var channels = reader.ReadInt16();
        var sampleRate = reader.ReadInt32();
        reader.Advance(6); // byteRate(4) + blockAlign(2)
        var bitsPerSample = reader.ReadInt16();

        // data sub-chunk
        _dataOffset = 0;
        _dataSize = 0;
        while (reader.Available >= 8)
        {
            var pos = reader.Position;
            var chunkId = reader.ReadUInt32();
            var chunkSize = reader.ReadInt32();

            if (chunkId == 0x61746164u) // "data"
            {
                _dataOffset = pos + 8;
                _dataSize = chunkSize;
                break;
            }

            // 跳过 chunk 内容（不超出缓冲区边界）
            reader.Advance(Math.Min(chunkSize, reader.Available));
        }

        _stream.Seek(_dataOffset, SeekOrigin.Begin);

        return new AudioFormat
        {
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = bitsPerSample,
            SamplesPerFrame = sampleRate / 50, // 20ms
            Encoding = audioFormat == 1 ? AVTypes.LPCM : AVTypes.PCM_AUDIO,
        };
    }
}
