using NewLife.Data;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Containers;

/// <summary>原始 PCM 数据解析器。需由调用方显式指定采样率、位深、声道与帧长</summary>
/// <remarks>
/// 无头 PCM 缺少可用于可靠推断的信息（长度可被任意帧长整除，静音与低电平信号也无法区分采样率），
/// 因此本类不做启发式推断；需要自动识别容器与编码时请使用容器探测（CONT-8）或 WAV/AIFF 等带头部格式。
/// </remarks>
public class RawPcmReader : IAudioContainerReader
{
    private readonly Stream _stream;
    private readonly Int64 _totalFrames;
    private Int64 _currentFrame;

    /// <summary>音频格式</summary>
    public AudioFormat Format { get; }

    /// <summary>PCM 编码类型</summary>
    public AVTypes CodecType => AVTypes.LPCM;

    /// <summary>总帧数</summary>
    public Int64 TotalFrames => _totalFrames;

    /// <summary>总时长</summary>
    public Double Duration => _totalFrames > 0 && Format.SampleRate > 0
        ? (Double)_totalFrames * Format.SamplesPerFrame / Format.SampleRate
        : 0;

    /// <summary>元数据</summary>
    public AudioMetadata Metadata { get; } = new();

    /// <summary>初始化原始 PCM 读取器</summary>
    /// <param name="stream">输入流</param>
    /// <param name="sampleRate">采样率（Hz），默认 8000</param>
    /// <param name="bitsPerSample">位深，默认 16</param>
    /// <param name="channels">声道数，默认 1</param>
    /// <param name="frameMs">帧长（ms），默认 20</param>
    public RawPcmReader(Stream stream, Int32 sampleRate = 8000, Int32 bitsPerSample = 16, Int32 channels = 1, Int32 frameMs = 20)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));

        Format = new AudioFormat
        {
            SampleRate = sampleRate,
            BitsPerSample = bitsPerSample,
            Channels = channels,
            SamplesPerFrame = sampleRate * frameMs / 1000,
        };

        var frameBytes = Format.BytesPerFrame;
        var totalBytes = _stream.Length - _stream.Position;
        _totalFrames = frameBytes > 0 ? totalBytes / frameBytes : 0;
    }

    /// <summary>读取下一帧</summary>
    public IPacket ReadFrame()
    {
        if (_currentFrame >= _totalFrames) return null;

        var frameBytes = Format.BytesPerFrame;
        var buffer = new Byte[frameBytes];
        var read = _stream.Read(buffer, 0, frameBytes);
        if (read == 0) return null;

        _currentFrame++;
        return new ArrayPacket(buffer, 0, read);
    }

    /// <summary>定位</summary>
    public void SeekFrame(Int64 frameIndex)
    {
        var offset = frameIndex * Format.BytesPerFrame;
        _stream.Seek(offset, SeekOrigin.Begin);
        _currentFrame = frameIndex;
    }

    /// <summary>释放</summary>
    public void Dispose() => _stream?.Dispose();
}
