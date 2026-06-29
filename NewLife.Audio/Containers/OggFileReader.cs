using NewLife.Data;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Containers;

/// <summary>OGG 文件读取器。解析 OGG 容器格式（RFC 3533）</summary>
public class OggFileReader : IAudioContainerReader
{
    private readonly Stream _stream;
    private Int64 _totalFrames;
    private Int64 _currentFrame;
    private UInt32 _serialNo;
    private UInt32 _expectedPageSeq;
    private readonly List<Byte[]> _pendingPackets = [];

    /// <summary>音频格式</summary>
    public AudioFormat Format { get; }

    /// <summary>编码类型</summary>
    public AVTypes CodecType { get; }

    /// <summary>总帧数</summary>
    public Int64 TotalFrames => _totalFrames;

    /// <summary>总时长</summary>
    public Double Duration { get; }

    /// <summary>元数据</summary>
    public AudioMetadata Metadata { get; } = new();

    /// <summary>初始化 OGG 读取器</summary>
    public OggFileReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));

        // 尝试解析第一页识别编码类型
        var firstPage = ReadOggPage();
        if (firstPage == null) throw new InvalidDataException("无法读取 OGG 页");

        var headPacket = firstPage.Value.Packets[0];
        var headStr = System.Text.Encoding.ASCII.GetString(headPacket, 0, Math.Min(headPacket.Length, 8));

        if (headStr.StartsWith("OpusHead"))
        {
            CodecType = AVTypes.Transparent; // Opus
            Format = new AudioFormat { SampleRate = 48000, Channels = 1, BitsPerSample = 16 };
        }
        else
        {
            CodecType = AVTypes.Transparent;
            Format = AudioFormat.Default;
        }
    }

    /// <summary>读取下一帧</summary>
    public Packet ReadFrame()
    {
        if (_currentFrame >= _totalFrames && _totalFrames > 0) return null;

        var page = ReadOggPage();
        if (page == null) return null;

        _currentFrame++;
        if (page.Value.Packets.Length > 0)
            return page.Value.Packets[0];

        return new Byte[0];
    }

    /// <summary>定位</summary>
    public void SeekFrame(Int64 frameIndex)
    {
        _stream.Seek(0, SeekOrigin.Begin);
        _currentFrame = 0;
        _expectedPageSeq = 0;

        for (var i = 0; i < frameIndex; i++)
            ReadFrame();
    }

    /// <summary>释放</summary>
    public void Dispose() => _stream?.Dispose();

    private (Byte[][] Packets, Int64 Granule)? ReadOggPage()
    {
        // 读取 27 字节 OGG 页头
        var header = new Byte[27];
        if (_stream.Read(header, 0, 27) < 27) return null;

        if (header[0] != 'O' || header[1] != 'g' || header[2] != 'g' || header[3] != 'S')
            return null;

        var segmentCount = header[26];
        var segmentTable = new Byte[segmentCount];
        _stream.Read(segmentTable, 0, segmentCount);

        var payloadSize = 0;
        for (var i = 0; i < segmentCount; i++)
            payloadSize += segmentTable[i];

        var payload = new Byte[payloadSize];
        if (payloadSize > 0) _stream.Read(payload, 0, payloadSize);

        // 解析 granule position
        var granule = (Int64)header[6] | ((Int64)header[7] << 8) | ((Int64)header[8] << 16) |
                      ((Int64)header[9] << 24) | ((Int64)header[10] << 32) | ((Int64)header[11] << 40) |
                      ((Int64)header[12] << 48) | ((Int64)header[13] << 56);

        // 解析段表，分割为多个包
        var packets = new List<Byte[]>();
        var offset = 0;
        for (var i = 0; i < segmentCount; i++)
        {
            var segSize = segmentTable[i];
            if (offset + segSize <= payloadSize)
            {
                var seg = new Byte[segSize];
                Array.Copy(payload, offset, seg, 0, segSize);
                packets.Add(seg);
                offset += segSize;
            }
        }

        return (packets.ToArray(), granule);
    }
}
