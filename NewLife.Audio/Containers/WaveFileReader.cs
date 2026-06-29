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
    public Packet ReadFrame()
    {
        if (_currentFrame >= _totalFrames) return null;

        var frameBytes = _format.BytesPerSample * _format.Channels * _format.SamplesPerFrame;
        var buffer = new Byte[frameBytes];
        var read = _stream.Read(buffer, 0, frameBytes);
        if (read == 0) return null;

        _currentFrame++;
        return new Packet(buffer, 0, read);
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

        // RIFF
        if (header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != 'F')
            throw new InvalidDataException("不是有效的 WAV 文件");

        // WAVE
        if (header[8] != 'W' || header[9] != 'A' || header[10] != 'V' || header[11] != 'E')
            throw new InvalidDataException("不是有效的 WAV 文件");

        // fmt chunk
        var audioFormat = (Int16)(header[20] | header[21] << 8);
        var channels = (Int16)(header[22] | header[23] << 8);
        var sampleRate = header[24] | header[25] << 8 | header[26] << 16 | header[27] << 24;
        var bitsPerSample = (Int16)(header[34] | header[35] << 8);

        // data chunk
        var pos = 36;
        while (pos < header.Length - 8 && pos < 1000)
        {
            var chunkId = new String([(Char)header[pos], (Char)header[pos + 1], (Char)header[pos + 2], (Char)header[pos + 3]]);
            var chunkSize = header[pos + 4] | header[pos + 5] << 8 | header[pos + 6] << 16 | header[pos + 7] << 24;

            if (chunkId == "data")
            {
                _dataOffset = pos + 8;
                _dataSize = chunkSize;
                break;
            }

            pos += 8 + chunkSize;
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
