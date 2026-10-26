using System;
using System.IO;
using NewLife.Audio.DSP;
using NewLife.Data;

namespace NewLife.Audio.Containers;

/// <summary>AIFF/AIFF-C 文件读取器</summary>
/// <remarks>
/// 解析 FORM/AIFF 容器：<c>COMM</c> 给出声道数、采样帧数、位深与 80 位扩展浮点采样率，
/// <c>SSND</c> 承载大端 PCM 数据（AIFF-C 的 'sowt' 为小端）。样本统一转换为 16 位小端输出。
/// </remarks>
public class AiffFileReader : IAudioContainerReader
{
    private readonly Stream _stream;
    private readonly AudioFormat _format;
    private readonly Int64 _dataOffset;
    private readonly Int64 _dataSize;
    private readonly Int64 _totalFrames;
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

    /// <summary>元数据（AIFF 不含标签，保持空对象）</summary>
    public AudioMetadata Metadata { get; } = new();

    /// <summary>从流读取 AIFF 文件</summary>
    /// <param name="stream">输入流</param>
    /// <exception cref="InvalidDataException">不是有效的 AIFF 数据</exception>
    public AiffFileReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));

        var header = new Byte[12];
        if (!ReadFully(header, 0, 12)) throw new InvalidDataException("Not a valid AIFF file.");
        if (header[0] != 'F' || header[1] != 'O' || header[2] != 'R' || header[3] != 'M') throw new InvalidDataException("Not a valid AIFF file.");

        var isAifc = header[8] == 'A' && header[9] == 'I' && header[10] == 'F' && header[11] == 'C';
        if (!isAifc && !(header[8] == 'A' && header[9] == 'I' && header[10] == 'F' && header[11] == 'F'))
            throw new InvalidDataException("Not a valid AIFF file.");

        Int32 channels = 0;
        Int32 bitsPerSample = 0;
        Int32 sampleRate = 0;
        Int64 sampleFrames = 0;
        _bigEndian = true;
        _dataOffset = -1;
        _dataSize = -1;

        while (true)
        {
            var chunk = new Byte[8];
            if (!ReadFully(chunk, 0, 8)) break;

            var size = (Int64)(chunk[4] << 24 | chunk[5] << 16 | chunk[6] << 8 | chunk[7]);
            var id = System.Text.Encoding.ASCII.GetString(chunk, 0, 4);

            switch (id)
            {
                case "COMM":
                    {
                        var comm = new Byte[Math.Min(size, 64)];
                        var got = ReadFully(comm, 0, comm.Length) ? comm.Length : 0;
                        if (got >= 18)
                        {
                            channels = comm[0] << 8 | comm[1];
                            sampleFrames = (Int64)(comm[2] << 24 | comm[3] << 16 | comm[4] << 8 | comm[5]);
                            bitsPerSample = comm[6] << 8 | comm[7];
                            sampleRate = (Int32)Math.Round(ReadExtended(comm, 8));
                            if (got >= 22 && comm[18] == 's' && comm[19] == 'o' && comm[20] == 'w' && comm[21] == 't') _bigEndian = false;
                        }
                        Skip(size - got + (size & 1));
                        break;
                    }

                case "SSND":
                    {
                        var ssnd = new Byte[8];
                        var got = ReadFully(ssnd, 0, 8) ? 8 : 0;
                        var offset = got >= 4 ? (Int64)(ssnd[0] << 24 | ssnd[1] << 16 | ssnd[2] << 8 | ssnd[3]) : 0;
                        Skip(offset);

                        var start = _stream.CanSeek ? _stream.Position : 0;
                        _dataOffset = start;
                        _dataSize = size - 8 - offset;
                        if (!_stream.CanSeek) return;

                        _stream.Seek(start, SeekOrigin.Begin);
                        if (channels <= 0) throw new InvalidDataException("Missing AIFF COMM chunk.");
                        _format = BuildFormat(sampleRate, channels, bitsPerSample);
                        _totalFrames = sampleFrames > 0 ? sampleFrames : _dataSize / _format.BytesPerSample / channels;

                        return;
                    }

                default:
                    Skip(size + (size & 1));
                    break;
            }
        }

        throw new InvalidDataException("Missing AIFF SSND chunk.");
    }

    /// <summary>读取下一帧 PCM（16 位小端交错）</summary>
    /// <returns>PCM 帧，末尾返回 null</returns>
    public IPacket ReadFrame()
    {
        var blockAlign = _format.BytesPerSample * _format.Channels;
        var frameBytes = Math.Max(1, _format.SamplesPerFrame) * blockAlign;
        var remaining = _dataSize - _position * blockAlign;
        if (remaining <= 0) return null;
        if (frameBytes > remaining) frameBytes = (Int32)remaining;

        var buffer = new Byte[frameBytes];
        if (!ReadFully(buffer, 0, frameBytes)) return null;

        var pcm = ConvertToPcm16(buffer, _format.BytesPerSample, _format.Channels, _bigEndian);
        _position += frameBytes / blockAlign;

        return new ArrayPacket(pcm);
    }

    /// <summary>定位到指定帧</summary>
    /// <param name="frameIndex">帧索引</param>
    public void SeekFrame(Int64 frameIndex)
    {
        if (frameIndex < 0) frameIndex = 0;
        if (frameIndex > _totalFrames) frameIndex = _totalFrames;

        var blockAlign = _format.BytesPerSample * _format.Channels;
        _stream.Seek(_dataOffset + frameIndex * blockAlign, SeekOrigin.Begin);
        _position = frameIndex;
    }

    /// <summary>释放</summary>
    public void Dispose() => _stream?.Dispose();

    #region 辅助
    private static AudioFormat BuildFormat(Int32 sampleRate, Int32 channels, Int32 bitsPerSample) => new()
    {
        SampleRate = sampleRate > 0 ? sampleRate : 44100,
        Channels = channels > 0 ? channels : 1,
        BitsPerSample = 16,
        SamplesPerFrame = 1,
        Encoding = AVTypes.LPCM,
    };

    /// <summary>把大端/小端整型 PCM 转换为 16 位小端</summary>
    private static Byte[] ConvertToPcm16(Byte[] data, Int32 bytesPerSample, Int32 channels, Boolean bigEndian)
    {
        if (bytesPerSample == 2)
        {
            var pcm = new Byte[data.Length];
            for (var i = 0; i < data.Length; i += 2)
            {
                if (bigEndian)
                {
                    pcm[i] = data[i + 1];
                    pcm[i + 1] = data[i];
                }
                else
                {
                    pcm[i] = data[i];
                    pcm[i + 1] = data[i + 1];
                }
            }

            return pcm;
        }

        var samples = data.Length / bytesPerSample;
        var result = new Byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            Int32 v;
            var p = i * bytesPerSample;
            if (bytesPerSample == 1)
                v = (data[p] - 128) << 8;
            else if (bytesPerSample == 3)
                v = bigEndian
                    ? (data[p] << 24 | data[p + 1] << 16 | data[p + 2] << 8) >> 16
                    : (data[p + 2] << 24 | data[p + 1] << 16 | data[p] << 8) >> 16;
            else
                v = bigEndian
                    ? data[p] << 8 | data[p + 1]
                    : data[p + 2] << 8 | data[p + 3];

            if (v > Int16.MaxValue) v = Int16.MaxValue;
            if (v < Int16.MinValue) v = Int16.MinValue;

            result[i * 2] = (Byte)v;
            result[i * 2 + 1] = (Byte)(v >> 8);
        }

        return result;
    }

    /// <summary>解析 80 位扩展浮点（IEEE 754 extended，大端）</summary>
    private static Double ReadExtended(Byte[] data, Int32 offset)
    {
        var exponent = (data[offset] << 8 | data[offset + 1]) & 0x7FFF;
        var mantissa = 0.0;
        for (var i = 0; i < 8; i++)
        {
            mantissa = mantissa * 256 + data[offset + 2 + i];
        }
        if (exponent == 0) return 0;

        return mantissa * Math.Pow(2, exponent - 16383 - 63);
    }

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
