using System.IO;
using NewLife;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using NewLife.Data;

namespace NewLife.Audio.Codecs;

/// <summary>FLAC 无损音频编解码器</summary>
/// <remarks>
/// 纯托管实现，按 FLAC 规范（RFC 9639 / xiph.org 格式定义）解析与生成码流：<br/>
/// 1. 解码支持 CONSTANT / VERBATIM / FIXED(0~4) / LPC(1~32) 子帧、分区 Rice 残差（含 escape 分区）、
/// 左/右/中侧立体声去相关与 8/12/16/20/24/32 位样本，输出 16 位 PCM；<br/>
/// 2. 编码由 <see cref="FlacFileWriter"/> 完成（STREAMINFO + SEEKTABLE + VORBIS_COMMENT + 固定预测/Rice 帧）。<br/>
/// 有状态编解码器，每个流应使用独立实例。
/// </remarks>
public class FlacCodec : IAudioCodec, ICodecInfo
{
    #region 常量
    /// <summary>FLAC 流标记 "fLaC"</summary>
    private static readonly Byte[] FlacMarker = [0x66, 0x4C, 0x61, 0x43];

    /// <summary>帧同步码（14 位：0x3FFE）</summary>
    private const Int32 FrameSyncCode = 0x3FFE;
    #endregion

    #region 类型
    /// <summary>元数据块类型</summary>
    public enum MetadataType : Byte
    {
        /// <summary>流信息（采样率、声道、位深、总采样数）</summary>
        StreamInfo = 0,
        /// <summary>填充块</summary>
        Padding = 1,
        /// <summary>应用自定义数据</summary>
        Application = 2,
        /// <summary>跳转表</summary>
        SeekTable = 3,
        /// <summary>Vorbis 风格注释（标题、艺术家等）</summary>
        VorbisComment = 4,
        /// <summary>CUE 曲目表</summary>
        CueSheet = 5,
        /// <summary>封面图片</summary>
        Picture = 6,
    }

    /// <summary>流信息</summary>
    public sealed class StreamInfo
    {
        /// <summary>最小块大小（样本数）</summary>
        public UInt16 MinBlockSize { get; set; }

        /// <summary>最大块大小（样本数）</summary>
        public UInt16 MaxBlockSize { get; set; }

        /// <summary>最小帧大小（字节）</summary>
        public UInt32 MinFrameSize { get; set; }

        /// <summary>最大帧大小（字节）</summary>
        public UInt32 MaxFrameSize { get; set; }

        /// <summary>采样率（Hz）</summary>
        public UInt32 SampleRate { get; set; }

        /// <summary>声道数</summary>
        public Byte Channels { get; set; }

        /// <summary>每样本位数</summary>
        public Byte BitsPerSample { get; set; }

        /// <summary>总采样数</summary>
        public UInt64 TotalSamples { get; set; }

        /// <summary>MD5校验和</summary>
        public Byte[] Md5Sum { get; set; }
    }
    #endregion

    #region 属性
    private StreamInfo _streamInfo;

    /// <summary>编解码器名称</summary>
    public String Name => "FLAC";

    /// <summary>版本号</summary>
    public String Version => "2.0";

    /// <summary>支持的编码类型</summary>
    public IReadOnlyCollection<AVTypes> SupportedTypes { get; } = [AVTypes.LPCM];

    /// <summary>有状态编解码器</summary>
    public Boolean IsStateful => true;

    /// <summary>最近一次解析得到的 STREAMINFO</summary>
    public StreamInfo Info => _streamInfo;
    #endregion

    #region 解码
    /// <summary>FLAC 数据转 PCM</summary>
    /// <param name="audio">FLAC 编码数据（含 fLaC 标记和元数据块）</param>
    /// <param name="option">保留参数</param>
    /// <returns>16 位小端交错 PCM</returns>
    /// <exception cref="InvalidDataException">缺少 fLaC 标记或 STREAMINFO</exception>
    public IPacket ToPcm(ReadOnlySpan<Byte> audio, Object option)
    {
        if (audio.Length < 8) throw new InvalidDataException(L.T("The data is too short to contain a FLAC marker."));

        for (var i = 0; i < 4; i++)
        {
            if (audio[i] != FlacMarker[i]) throw new InvalidDataException(L.T("Not valid FLAC data."));
        }

        var offset = 4;
        var isLast = false;
        while (!isLast)
        {
            if (offset + 4 > audio.Length) throw new InvalidDataException(L.T("No STREAMINFO metadata block was found in the FLAC data."));

            var header = audio[offset];
            isLast = (header & 0x80) != 0;
            var blockType = (Int32)(header & 0x7F);
            var blockSize = (audio[offset + 1] << 16) | (audio[offset + 2] << 8) | audio[offset + 3];
            offset += 4;

            if (offset + blockSize > audio.Length) throw new InvalidDataException(L.T("Not valid FLAC data."));

            if (blockType == (Int32)MetadataType.StreamInfo && blockSize >= 34)
            {
                _streamInfo = ParseStreamInfo(audio.Slice(offset, blockSize));
            }

            offset += blockSize;
        }

        if (_streamInfo == null) throw new InvalidDataException(L.T("No STREAMINFO metadata block was found in the FLAC data."));

        var data = audio.ToArray();
        var reader = new FlacBitReader(data, offset);
        var pcm = new MemoryStream(data.Length);

        var frames = 0;
        while (reader.BitPosition + 16 <= reader.LengthBits)
        {
            var start = reader.BitPosition;
            if (reader.PeekBits(14) != FrameSyncCode)
            {
                reader.SeekBit(start + 8);
                continue;
            }

            if (TryDecodeFrame(reader, pcm)) frames++;
            else reader.SeekBit(start + 8);
        }

        if (frames == 0 && _streamInfo.TotalSamples > 0) throw new InvalidDataException(L.T("Not valid FLAC data."));

        return new ArrayPacket(pcm.ToArray());
    }

    private static StreamInfo ParseStreamInfo(ReadOnlySpan<Byte> data)
    {
        var block = data.Slice(0, 34);

        return new StreamInfo
        {
            MinBlockSize = (UInt16)((block[0] << 8) | block[1]),
            MaxBlockSize = (UInt16)((block[2] << 8) | block[3]),
            MinFrameSize = (UInt32)((block[4] << 16) | (block[5] << 8) | block[6]),
            MaxFrameSize = (UInt32)((block[7] << 16) | (block[8] << 8) | block[9]),
            SampleRate = (UInt32)((block[10] << 12) | (block[11] << 4) | (block[12] >> 4)),
            Channels = (Byte)(((block[12] & 0x0E) >> 1) + 1),
            BitsPerSample = (Byte)((((block[12] & 0x01) << 4) | (block[13] >> 4)) + 1),
            TotalSamples = ((UInt64)(block[13] & 0x0F) << 32) | ((UInt64)block[14] << 24) |
                           ((UInt64)block[15] << 16) | ((UInt64)block[16] << 8) | block[17],
            Md5Sum = block.Slice(18, 16).ToArray(),
        };
    }

    private Boolean TryDecodeFrame(FlacBitReader reader, Stream output)
    {
        reader.ReadBits(14);                        // sync
        if (reader.ReadBit() != 0) return false;    // reserved
        reader.ReadBit();                           // blocking strategy

        var blockSizeCode = (Int32)reader.ReadBits(4);
        var sampleRateCode = (Int32)reader.ReadBits(4);
        var channelAssignment = (Int32)reader.ReadBits(4);
        var sampleSizeCode = (Int32)reader.ReadBits(3);
        if (reader.ReadBit() != 0) return false;    // reserved

        if (!TryReadUtf8Number(reader, out _)) return false;

        var blockSize = blockSizeCode switch
        {
            0 => 0,
            1 => 192,
            >= 2 and <= 5 => 576 << (blockSizeCode - 2),
            6 => (Int32)reader.ReadBits(8) + 1,
            7 => (Int32)reader.ReadBits(16) + 1,
            _ => 256 << (blockSizeCode - 8),
        };
        if (blockSize <= 0 || blockSize > 65535) return false;

        if (sampleRateCode == 12) reader.ReadBits(8);
        else if (sampleRateCode is 13 or 14) reader.ReadBits(16);

        reader.ReadBits(8);                          // CRC-8

        var bitsPerSample = sampleSizeCode switch
        {
            1 => 8,
            2 => 12,
            4 => 16,
            5 => 20,
            6 => 24,
            7 => 32,
            _ => (Int32)_streamInfo.BitsPerSample,
        };
        if (bitsPerSample is <= 0 or > 32) return false;

        var channels = channelAssignment <= 7 ? channelAssignment + 1 : 2;

        var channelData = new Int32[channels][];
        for (var ch = 0; ch < channels; ch++)
        {
            var bps = bitsPerSample;
            if (ch == 1 && channelAssignment is 8 or 9 or 10) bps++;

            var samples = DecodeSubframe(reader, blockSize, bps);
            if (samples == null) return false;
            channelData[ch] = samples;
        }

        // 立体声去相关
        switch (channelAssignment)
        {
            case 8:     // left/side
                for (var i = 0; i < blockSize; i++) channelData[1][i] = channelData[0][i] - channelData[1][i];
                break;
            case 9:     // side/right
                for (var i = 0; i < blockSize; i++) channelData[0][i] += channelData[1][i];
                break;
            case 10:    // mid/side
                for (var i = 0; i < blockSize; i++)
                {
                    var side = channelData[1][i];
                    var mid = (channelData[0][i] << 1) | (side & 1);
                    channelData[0][i] = (mid + side) >> 1;
                    channelData[1][i] = (mid - side) >> 1;
                }
                break;
        }

        var shift = bitsPerSample - 16;
        for (var i = 0; i < blockSize; i++)
        {
            for (var ch = 0; ch < channels; ch++)
            {
                var sample = channelData[ch][i];
                if (shift > 0) sample >>= shift;
                else if (shift < 0) sample <<= -shift;

                if (sample < Int16.MinValue) sample = Int16.MinValue;
                if (sample > Int16.MaxValue) sample = Int16.MaxValue;

                output.WriteByte((Byte)(sample & 0xFF));
                output.WriteByte((Byte)((sample >> 8) & 0xFF));
            }
        }

        // 帧尾按字节对齐后是 CRC-16
        var remain = reader.BitPosition & 7;
        if (remain != 0) reader.SeekBit(reader.BitPosition + (8 - remain));
        reader.ReadBits(16);

        return true;
    }

    /// <summary>解码一个子帧；失败返回 null</summary>
    private static Int32[] DecodeSubframe(FlacBitReader reader, Int32 blockSize, Int32 bitsPerSample)
    {
        if (reader.ReadBit() != 0) return null;      // 子帧头填充位

        var type = (Int32)reader.ReadBits(6);
        var wasted = 0;
        if (reader.ReadBit() != 0)
        {
            var zeros = 0;
            while (reader.ReadBit() == 0)
            {
                if (++zeros > 32) return null;
            }
            wasted = zeros + 1;
        }

        var bps = bitsPerSample - wasted;
        if (bps <= 0) return null;

        var samples = new Int32[blockSize];

        switch (type)
        {
            case 0:     // CONSTANT
                {
                    var value = (Int32)reader.ReadSigned(bps);
                    for (var i = 0; i < blockSize; i++) samples[i] = value;
                    break;
                }

            case 1:     // VERBATIM
                for (var i = 0; i < blockSize; i++) samples[i] = (Int32)reader.ReadSigned(bps);
                break;

            case >= 8 and <= 12:    // FIXED
                {
                    var order = type - 8;
                    if (order > blockSize) return null;

                    for (var i = 0; i < order; i++) samples[i] = (Int32)reader.ReadSigned(bps);

                    var residual = new Int32[blockSize - order];
                    if (!DecodeResidual(reader, residual, blockSize, order)) return null;

                    for (var i = order; i < blockSize; i++)
                    {
                        var prediction = order switch
                        {
                            0 => 0,
                            1 => samples[i - 1],
                            2 => 2 * samples[i - 1] - samples[i - 2],
                            3 => 3 * samples[i - 1] - 3 * samples[i - 2] + samples[i - 3],
                            _ => 4 * samples[i - 1] - 6 * samples[i - 2] + 4 * samples[i - 3] - samples[i - 4],
                        };
                        samples[i] = prediction + residual[i - order];
                    }
                    break;
                }

            case >= 32:             // LPC
                {
                    var order = type - 31;
                    if (order > blockSize) return null;

                    for (var i = 0; i < order; i++) samples[i] = (Int32)reader.ReadSigned(bps);

                    var precision = (Int32)reader.ReadBits(4) + 1;
                    var shift = (Int32)reader.ReadSigned(5);

                    var coefficients = new Int32[order];
                    for (var i = 0; i < order; i++) coefficients[i] = (Int32)reader.ReadSigned(precision);

                    var residual = new Int32[blockSize - order];
                    if (!DecodeResidual(reader, residual, blockSize, order)) return null;

                    for (var i = order; i < blockSize; i++)
                    {
                        var prediction = 0L;
                        for (var j = 0; j < order; j++) prediction += (Int64)coefficients[j] * samples[i - 1 - j];

                        samples[i] = (Int32)(prediction >> shift) + residual[i - order];
                    }
                    break;
                }

            default:
                return null;
        }

        if (wasted > 0)
        {
            for (var i = 0; i < blockSize; i++) samples[i] <<= wasted;
        }

        return samples;
    }

    private static Boolean DecodeResidual(FlacBitReader reader, Int32[] residual, Int32 blockSize, Int32 predictorOrder)
    {
        var partitionOrder = (Int32)reader.ReadBits(2);
        var partitions = 1 << partitionOrder;
        var partitionSamples = blockSize >> partitionOrder;
        if (partitionSamples <= 0 || (partitionSamples < predictorOrder && partitionOrder > 0)) return false;

        var index = 0;
        for (var p = 0; p < partitions; p++)
        {
            var count = partitionSamples - (p == 0 ? predictorOrder : 0);
            if (count < 0) return false;

            var riceParam = (Int32)reader.ReadBits(4);

            if (riceParam == 15)
            {
                var rawBits = (Int32)reader.ReadBits(5);
                for (var i = 0; i < count; i++)
                {
                    residual[index++] = rawBits == 0 ? 0 : (Int32)reader.ReadSigned(rawBits);
                }
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    var quotient = 0;
                    while (reader.ReadBit() == 0)
                    {
                        if (++quotient > 1 << 20) return false;
                    }

                    var remainder = riceParam > 0 ? (Int32)reader.ReadBits(riceParam) : 0;
                    var value = (quotient << riceParam) | remainder;
                    residual[index++] = (value >> 1) ^ -(value & 1);
                }
            }
        }

        return index <= residual.Length;
    }

    private static Boolean TryReadUtf8Number(FlacBitReader reader, out Int64 value)
    {
        value = 0;
        var first = (Int32)reader.ReadBits(8);
        if ((first & 0x80) == 0)
        {
            value = first;
            return true;
        }

        var count = 0;
        var mask = 0x80;
        while ((first & mask) != 0 && count < 6)
        {
            count++;
            mask >>= 1;
        }

        if (count is < 2 or > 6) return false;

        var result = (Int64)(first & ((1 << (7 - count)) - 1));

        for (var i = 1; i < count; i++)
        {
            var next = (Int32)reader.ReadBits(8);
            if ((next & 0xC0) != 0x80) return false;

            result = (result << 6) | (Int64)(next & 0x3F);
        }

        value = result;
        return true;
    }
    #endregion

    #region 编码
    /// <summary>PCM 转 FLAC 数据</summary>
    /// <param name="pcm">16 位小端 PCM（单声道，44100 Hz，兼容旧签名）</param>
    /// <param name="option">压缩级别 0~8，默认 5</param>
    /// <returns>完整 FLAC 文件字节</returns>
    public IPacket FromPcm(ReadOnlySpan<Byte> pcm, Object option) => FromPcm(pcm, 44100, 1, option is Int32 level ? level : 5);

    /// <summary>PCM 转 FLAC 数据</summary>
    /// <param name="pcm">16 位小端交错 PCM</param>
    /// <param name="sampleRate">采样率（Hz）</param>
    /// <param name="channels">声道数（1~8）</param>
    /// <param name="level">压缩级别 0~8</param>
    /// <returns>完整 FLAC 文件字节</returns>
    public IPacket FromPcm(ReadOnlySpan<Byte> pcm, Int32 sampleRate, Int32 channels, Int32 level = 5)
    {
        var format = new AudioFormat
        {
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = 16,
        };

        using var stream = new MemoryStream();
        using (var writer = new FlacFileWriter(stream, format, new FlacWriterOptions { CompressionLevel = level }))
        {
            writer.WriteFrame(pcm);
            writer.Flush();
        }

        return new ArrayPacket(stream.ToArray());
    }
    #endregion

    #region 内部类型
    /// <summary>FLAC 比特读取器（高位在前）</summary>
    private sealed class FlacBitReader
    {
        private readonly Byte[] _data;

        public FlacBitReader(Byte[] data, Int32 byteOffset)
        {
            _data = data;
            BitPosition = byteOffset * 8;
        }

        public Int32 BitPosition { get; set; }

        public Int32 LengthBits => _data.Length * 8;

        public void SeekBit(Int32 position) => BitPosition = position;

        public UInt32 PeekBits(Int32 count)
        {
            var position = BitPosition;
            var value = ReadBits(count);
            BitPosition = position;

            return value;
        }

        public Int32 ReadBit()
        {
            if (BitPosition >= LengthBits) return 0;

            var value = (_data[BitPosition >> 3] >> (7 - (BitPosition & 7))) & 1;
            BitPosition++;

            return value;
        }

        public UInt32 ReadBits(Int32 count)
        {
            var value = 0u;
            for (var i = 0; i < count; i++) value = (value << 1) | (UInt32)ReadBit();

            return value;
        }

        public Int64 ReadSigned(Int32 count)
        {
            if (count <= 0) return 0;

            var value = (Int64)ReadBits(count);
            if (count < 64 && (value & (1L << (count - 1))) != 0) value -= 1L << count;

            return value;
        }
    }
    #endregion
}
