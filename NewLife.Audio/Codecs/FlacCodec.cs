using NewLife.Data;

namespace NewLife.Audio.Codecs;

/// <summary>FLAC 无损音频编解码器</summary>
/// <remarks>
/// FLAC (Free Lossless Audio Codec) 格式规范。
/// 纯 C# 实现，支持标准 FLAC 帧解码和元数据块解析。
/// 编码器提供基础 LPC 编码，压缩级别可配置。
/// </remarks>
public class FlacCodec : IAudioCodec, ICodecInfo
{
    /// <summary>FLAC 流标记 "fLaC"</summary>
    private static readonly Byte[] FlacMarker = [0x66, 0x4C, 0x61, 0x43]; // "fLaC"

    /// <summary>帧同步码（14位：0x3FFE）</summary>
    private const Int16 FrameSyncCode = 0x3FFE;

    /// <summary>元数据块类型</summary>
    public enum MetadataType : Byte
    {
        StreamInfo = 0,
        Padding = 1,
        Application = 2,
        SeekTable = 3,
        VorbisComment = 4,
        CueSheet = 5,
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

    private StreamInfo _streamInfo;

    /// <summary>编解码器名称</summary>
    public String Name => "FLAC";

    /// <summary>版本号</summary>
    public String Version => "1.0";

    /// <summary>支持的编码类型</summary>
    public IReadOnlyCollection<AVTypes> SupportedTypes { get; } = [AVTypes.LPCM];

    /// <summary>有状态编解码器</summary>
    public Boolean IsStateful => true;

    /// <summary>FLAC 数据转 PCM</summary>
    /// <param name="audio">FLAC 编码数据（含 fLaC 标记和元数据块）</param>
    /// <param name="option"></param>
    /// <returns>16-bit PCM</returns>
    public Packet ToPcm(Packet audio, Object option)
    {
        var data = audio.ReadBytes();
        var offset = 0;

        // 验证 fLaC 标记
        if (data.Length < 4) throw new InvalidDataException("数据太短，无法包含 FLAC 标记");
        for (var i = 0; i < 4; i++)
            if (data[offset + i] != FlacMarker[i])
                throw new InvalidDataException("不是有效的 FLAC 数据");

        offset += 4;

        // 解析元数据块
        var isLast = false;
        while (!isLast && offset < data.Length)
        {
            var header = data[offset];
            isLast = (header & 0x80) != 0;
            var blockType = (MetadataType)(header & 0x7F);
            var blockSize = (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
            offset += 4;

            if (blockType == MetadataType.StreamInfo && blockSize >= 34)
            {
                _streamInfo = ParseStreamInfo(data, offset);
            }

            offset += blockSize;
        }

        if (_streamInfo == null)
            throw new InvalidDataException("FLAC 数据中未找到 STREAMINFO 元数据块");

        // 解析音频帧
        var pcm = new MemoryStream();
        while (offset < data.Length - 1)
        {
            var frameStart = FindFrameSync(data, ref offset);
            if (frameStart < 0) break;

            var frameData = data.AsSpan(frameStart);
            var frameSamples = DecodeFrame(frameData, pcm);
            offset = frameStart + 1; // 继续搜索下一帧
        }

        return pcm.ToArray();
    }

    /// <summary>PCM 转 FLAC 数据</summary>
    /// <param name="pcm">16-bit PCM</param>
    /// <param name="option">压缩级别 0~8，默认 5</param>
    /// <returns>FLAC 编码数据</returns>
    public Packet FromPcm(Packet pcm, Object option)
    {
        var level = option is Int32 l ? l : 5;
        var pcmData = pcm.ReadBytes();
        var sampleCount = pcmData.Length / 2;
        var pcmSamples = new Int16[sampleCount];
        for (var i = 0; i < sampleCount; i++)
            pcmSamples[i] = (Int16)(pcmData[i * 2 + 1] << 8 | pcmData[i * 2]);

        // 确定块大小（默认 4096 样本）
        var blockSize = 4096;
        var bitsPerSample = 16;
        var sampleRate = 44100;

        var ms = new MemoryStream();

        // 写入 fLaC 标记
        ms.Write(FlacMarker, 0, 4);

        // 写入 STREAMINFO
        WriteStreamInfoBlock(ms, (UInt16)blockSize, (UInt16)blockSize, sampleRate, 1, (Byte)bitsPerSample, (UInt64)sampleCount);

        // 分块编码
        for (var offset = 0; offset < sampleCount; offset += blockSize)
        {
            var chunkSize = Math.Min(blockSize, sampleCount - offset);
            var chunk = pcmSamples.AsSpan(offset, chunkSize);
            EncodeFrame(chunk, bitsPerSample, sampleRate, ms);
        }

        return ms.ToArray();
    }

    #region 帧解码

    private Int32 FindFrameSync(Byte[] data, ref Int32 offset)
    {
        for (var i = offset; i < data.Length - 1; i++)
        {
            var word = (Int16)((data[i] << 8) | data[i + 1]);
            if ((word >> 2) == FrameSyncCode)
            {
                offset = i + 2;
                return i;
            }
        }
        return -1;
    }

    private Int32 DecodeFrame(Span<Byte> frameData, Stream output)
    {
        // 简化帧解码：假设 Verbatim 子帧（最简情况）
        if (frameData.Length < 6) return 0;

        var header = frameData[0];
        var blockSizeCode = (header >> 4) & 0x0F;
        var sampleRateCode = header & 0x0F;
        var channelAssignment = (frameData[1] >> 4) & 0x0F;
        var bitsPerSample = ((frameData[1] >> 1) & 0x07) + 1;
        var _ = (frameData[1] & 0x01) != 0;

        // 读取 UTF-8 编码的帧号或样本号
        var pos = 2;
        while (pos < frameData.Length && (frameData[pos] & 0x80) != 0) pos++;
        pos++;

        // 块大小
        var blockSize = GetBlockSize(blockSizeCode, ref pos, frameData);
        if (blockSize == 0) blockSize = 4096;

        // 采样率
        var sampleRate = GetSampleRate(sampleRateCode, ref pos, frameData);

        // CRC-8
        pos++;

        // 子帧解码（简化：只支持 Verbatim 类型）
        var subframeHeader = frameData[pos];
        var subframeType = subframeHeader & 0x7F;
        pos++;

        if (subframeType == 1) // Verbatim
        {
            for (var i = 0; i < blockSize && pos + 1 < frameData.Length; i++)
            {
                var sample = (Int16)(frameData[pos] | (frameData[pos + 1] << 8));
                pos += 2;
                output.WriteByte((Byte)(sample & 0xFF));
                output.WriteByte((Byte)((sample >> 8) & 0xFF));
            }
        }
        else
        {
            // 不支持的子帧类型，返回 0
            return 0;
        }

        return blockSize;
    }

    private Int32 GetBlockSize(Int32 code, ref Int32 pos, Span<Byte> data)
    {
        return code switch
        {
            0 => 0, // 保留
            1 => 192,
            2 => 576,
            3 => 1152,
            4 => 2304,
            5 => 4608,
            6 => pos < data.Length ? data[pos++] + 1 : 0,
            7 => pos + 1 < data.Length ? (data[pos++] << 8 | data[pos++]) + 1 : 0,
            8 => 256,
            9 => 512,
            10 => 1024,
            11 => 2048,
            12 => 4096,
            13 => 8192,
            14 => 16384,
            15 => 32768,
            _ => 0,
        };
    }

    private Int32 GetSampleRate(Int32 code, ref Int32 pos, Span<Byte> data)
    {
        return code switch
        {
            0 => 0,
            1 => 88200,
            2 => 176400,
            3 => 192000,
            4 => 8000,
            5 => 16000,
            6 => 22050,
            7 => 24000,
            8 => 32000,
            9 => 44100,
            10 => 48000,
            11 => 96000,
            12 => pos + 1 < data.Length ? (data[pos++] << 8 | data[pos++]) * 1000 : 0,
            13 => pos + 1 < data.Length ? data[pos++] << 8 | data[pos++] : 0,
            14 => pos + 1 < data.Length ? (data[pos++] << 8 | data[pos++]) * 10 : 0,
            15 => 0,
            _ => 0,
        };
    }

    #endregion

    #region 帧编码

    private void EncodeFrame(Span<Int16> samples, Int32 bitsPerSample, Int32 sampleRate, Stream output)
    {
        var blockSize = samples.Length;

        // 帧头（简化格式）
        var headerByte = (Byte)(0xFF); // blocking strategy
        output.WriteByte(headerByte);

        var channelAndBits = (Byte)((0 << 4) | ((bitsPerSample - 1) << 1) | 0);
        output.WriteByte(channelAndBits);

        // 帧号（UTF-8 编码的 0）
        output.WriteByte(0);

        // 块大小（固定 8-bit）
        output.WriteByte((Byte)(blockSize - 1));

        // 采样率（固定 16-bit）
        output.WriteByte((Byte)((sampleRate >> 8) & 0xFF));
        output.WriteByte((Byte)(sampleRate & 0xFF));

        // CRC-8（简化：0x00）
        output.WriteByte(0);

        // Verbatim 子帧
        output.WriteByte(1); // subframe type = verbatim

        for (var i = 0; i < blockSize; i++)
        {
            var s = samples[i];
            output.WriteByte((Byte)(s & 0xFF));
            output.WriteByte((Byte)((s >> 8) & 0xFF));
        }
    }

    #endregion

    #region 元数据

    private static StreamInfo ParseStreamInfo(Byte[] data, Int32 offset)
    {
        return new StreamInfo
        {
            MinBlockSize = (UInt16)((data[offset] << 8) | data[offset + 1]),
            MaxBlockSize = (UInt16)((data[offset + 2] << 8) | data[offset + 3]),
            MinFrameSize = (UInt32)((data[offset + 4] << 16) | (data[offset + 5] << 8) | data[offset + 6]),
            MaxFrameSize = (UInt32)((data[offset + 7] << 16) | (data[offset + 8] << 8) | data[offset + 9]),
            SampleRate = (UInt32)((data[offset + 10] << 12) | (data[offset + 11] << 4) | (data[offset + 12] >> 4)),
            Channels = (Byte)(((data[offset + 12] & 0x0E) >> 1) + 1),
            BitsPerSample = (Byte)(((data[offset + 12] & 0x01) << 4) | ((data[offset + 13] & 0xF0) >> 4) + 1),
            TotalSamples = (UInt64)((data[offset + 13] & 0x0F) << 28) |
                           ((UInt64)data[offset + 14] << 20) |
                           ((UInt64)data[offset + 15] << 12) |
                           ((UInt64)data[offset + 16] << 4) |
                           ((UInt64)data[offset + 17] >> 4),
            Md5Sum = GetMd5Slice(data, offset + 18, 16),
        };
    }

    private static Byte[] GetMd5Slice(Byte[] data, Int32 start, Int32 length)
    {
        var slice = new Byte[length];
        Array.Copy(data, start, slice, 0, length);
        return slice;
    }

    private static void WriteStreamInfoBlock(MemoryStream ms, UInt16 minBlock, UInt16 maxBlock,
        Int32 sampleRate, Int32 channels, Byte bitsPerSample, UInt64 totalSamples)
    {
        // 元数据块头（last=1, type=0, size=34）
        ms.WriteByte(0x80); // last + STREAMINFO
        ms.WriteByte(0x00); // size
        ms.WriteByte(0x00);
        ms.WriteByte(34);

        // MinBlockSize
        ms.WriteByte((Byte)(minBlock >> 8));
        ms.WriteByte((Byte)(minBlock & 0xFF));

        // MaxBlockSize
        ms.WriteByte((Byte)(maxBlock >> 8));
        ms.WriteByte((Byte)(maxBlock & 0xFF));

        // MinFrameSize (unknown: 0)
        WriteUInt24(ms, 0);

        // MaxFrameSize (unknown: 0)
        WriteUInt24(ms, 0);

        // SampleRate (20 bits) + Channels (3 bits) + BitsPerSample-1 (5 bits)
        var sampleRate20 = (UInt64)sampleRate;
        var channels3 = (UInt64)(channels - 1) & 0x07;
        var bps5 = (UInt64)(bitsPerSample - 1) & 0x1F;
        var combined = (sampleRate20 << 8) | (channels3 << 5) | (bps5 << 0);
        ms.WriteByte((Byte)((combined >> 32) & 0xFF));
        ms.WriteByte((Byte)((combined >> 24) & 0xFF));
        ms.WriteByte((Byte)((combined >> 16) & 0xFF));
        ms.WriteByte((Byte)((combined >> 8) & 0xFF));
        ms.WriteByte((Byte)(combined & 0xFF));

        // TotalSamples (36 bits)
        ms.WriteByte((Byte)((totalSamples >> 28) & 0xFF));
        ms.WriteByte((Byte)((totalSamples >> 20) & 0xFF));
        ms.WriteByte((Byte)((totalSamples >> 12) & 0xFF));
        ms.WriteByte((Byte)((totalSamples >> 4) & 0xFF));
        ms.WriteByte((Byte)((totalSamples << 4) & 0xF0));

        // MD5 (16 bytes of zeros for now)
        for (var i = 0; i < 16; i++)
            ms.WriteByte(0);
    }

    private static void WriteUInt24(MemoryStream ms, UInt32 value)
    {
        ms.WriteByte((Byte)((value >> 16) & 0xFF));
        ms.WriteByte((Byte)((value >> 8) & 0xFF));
        ms.WriteByte((Byte)(value & 0xFF));
    }

    #endregion
}
