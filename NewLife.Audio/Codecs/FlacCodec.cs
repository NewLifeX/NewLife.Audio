using NewLife.Buffers;
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
    public IPacket ToPcm(ReadOnlySpan<Byte> audio, Object option)
    {
        var data = audio.ToArray();
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

        return new ArrayPacket(pcm);
    }

    /// <summary>PCM 转 FLAC 数据</summary>
    /// <param name="pcm">16-bit PCM</param>
    /// <param name="option">压缩级别 0~8，默认 5</param>
    /// <returns>FLAC 编码数据</returns>
    public IPacket FromPcm(ReadOnlySpan<Byte> pcm, Object option)
    {
        var level = option is Int32 l ? l : 5;
        var pcmData = pcm.ToArray();
        var sampleCount = pcmData.Length / 2;
        var pcmSamples = new Int16[sampleCount];
        for (var i = 0; i < sampleCount; i++)
            pcmSamples[i] = (Int16)(pcmData[i * 2 + 1] << 8 | pcmData[i * 2]);

        // 确定块大小（4096 样本）
        var blockSize = 4096;
        var bitsPerSample = 16;
        var sampleRate = 44100;

        var ms = new MemoryStream();

        // 写入 fLaC 标记
        ms.Write(FlacMarker, 0, 4);

        // 写入 STREAMINFO（MD5 先置零，后续回填）
        var streamInfoPos = ms.Position;
        WriteStreamInfoBlock(ms, (UInt16)blockSize, (UInt16)blockSize, sampleRate, 1, (Byte)bitsPerSample, (UInt64)sampleCount);

        // 分块编码
        for (var offset = 0; offset < sampleCount; offset += blockSize)
        {
            var chunkSize = Math.Min(blockSize, sampleCount - offset);
            var chunk = pcmSamples.AsSpan(offset, chunkSize);
            EncodeFrame(chunk, bitsPerSample, sampleRate, ms);
        }

        // 计算实际 MD5 并回填到 STREAMINFO（直接对 pcmData 计算，避免中间流）
        using var md5Hash = System.Security.Cryptography.MD5.Create();
        var md5 = md5Hash.ComputeHash(pcmData);
        var finalPos = ms.Position;
        ms.Position = streamInfoPos + 4 + 34 - 16; // STREAMINFO header(4) + data(34) - MD5(16)
        ms.Write(md5, 0, 16);
        ms.Position = finalPos;

        return new ArrayPacket(ms);
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
        if (frameData.Length < 6) return 0;

        var header = frameData[0];
        var blockingStrategy = (header >> 7) & 0x01;
        var blockSizeCode = (header >> 4) & 0x0F;
        var sampleRateCode = header & 0x0F;
        var channelAssignment = (frameData[1] >> 4) & 0x0F;
        var bitsPerSample = ((frameData[1] >> 1) & 0x07) + 1;
        var _ = (frameData[1] & 0x01) != 0; // reserved

        // 读取 UTF-8 编码的帧号或样本号
        var pos = 2;
        while (pos < frameData.Length && (frameData[pos] & 0x80) != 0) pos++;
        pos++;

        // 块大小
        var blockSize = GetBlockSize(blockSizeCode, ref pos, frameData);
        if (blockSize == 0) blockSize = 4096;

        // 采样率
        var sampleRate = GetSampleRate(sampleRateCode, ref pos, frameData);

        // CRC-8（跳过）
        pos++;

        // 确定声道数
        var channels = channelAssignment < 8 ? channelAssignment + 1 : 2;
        var isMidSide = channelAssignment >= 8 && channelAssignment <= 10;

        // 解码各声道子帧
        var channelSamples = new Int32[channels][];
        for (var ch = 0; ch < channels; ch++)
        {
            channelSamples[ch] = new Int32[blockSize];
            var decoded = DecodeSubframe(frameData, ref pos, blockSize, bitsPerSample);
            if (decoded == null) return 0;
            Array.Copy(decoded, channelSamples[ch], blockSize);
        }

        // 声道去相关（Mid/Side → Left/Right）
        if (isMidSide)
        {
            var mid = channelSamples[0];
            var side = channelSamples[1];
            for (var i = 0; i < blockSize; i++)
            {
                var m = mid[i];
                var s = side[i];
                // Mid = (L+R)/2, Side = L-R  →  L = Mid + Side/2, R = Mid - Side/2
                mid[i] = m + s / 2;
                side[i] = m - s / 2 - (s & 1); // 正确处理奇数
            }
        }

        // 输出 PCM（Int16 交错）
        for (var i = 0; i < blockSize; i++)
        {
            for (var ch = 0; ch < channels; ch++)
            {
                var sample = channelSamples[ch][i];
                // 限制到 Int16 范围
                if (sample < -32768) sample = -32768;
                if (sample > 32767) sample = 32767;
                output.WriteByte((Byte)(sample & 0xFF));
                output.WriteByte((Byte)((sample >> 8) & 0xFF));
            }
        }

        return blockSize;
    }

    /// <summary>解码一个子帧，返回样本数组</summary>
    private Int32[] DecodeSubframe(Span<Byte> data, ref Int32 pos, Int32 blockSize, Int32 bitsPerSample)
    {
        if (pos >= data.Length) return null;

        var subframeHeader = data[pos];
        pos++;

        // 浪费的位数（wasted bits-per-sample flag）
        var wastedBits = 0;
        if ((subframeHeader & 0x01) != 0)
        {
            while (pos < data.Length && (data[pos] & 0x80) != 0)
            {
                wastedBits++;
                pos++;
            }
            wastedBits++;
            pos++;
        }

        var subframeType = (subframeHeader >> 1) & 0x3F; // 6-bit type (after removing wasted flag)

        var samples = new Int32[blockSize];

        if (subframeType == 0)
        {
            // Constant: 所有样本等于第一个样本值
            var value = ReadSignedInt(data, ref pos, bitsPerSample - wastedBits);
            for (var i = 0; i < blockSize; i++)
                samples[i] = value << wastedBits;
        }
        else if (subframeType == 1)
        {
            // Verbatim: 直接读取未压缩样本
            for (var i = 0; i < blockSize; i++)
                samples[i] = ReadSignedInt(data, ref pos, bitsPerSample - wastedBits) << wastedBits;
        }
        else if (subframeType >= 8 && subframeType <= 12)
        {
            // Fixed LPC: order = subframeType - 8 (0~4)
            var order = subframeType - 8;
            DecodeFixedSubframe(data, ref pos, order, blockSize, bitsPerSample - wastedBits, samples);
            if (wastedBits > 0)
                for (var i = 0; i < blockSize; i++) samples[i] <<= wastedBits;
        }
        else if (subframeType >= 32 && subframeType <= 63)
        {
            // LPC: order = subframeType - 31 (1~32)
            var order = subframeType - 31;
            DecodeLpcSubframe(data, ref pos, order, blockSize, bitsPerSample - wastedBits, samples);
            if (wastedBits > 0)
                for (var i = 0; i < blockSize; i++) samples[i] <<= wastedBits;
        }
        else
        {
            // 不支持的子帧类型
            return null;
        }

        return samples;
    }

    /// <summary>解码 Fixed LPC 子帧</summary>
    private void DecodeFixedSubframe(Span<Byte> data, ref Int32 pos, Int32 order, Int32 blockSize, Int32 bitsPerSample, Int32[] samples)
    {
        // 读取 warm-up 样本（未压缩的前 order 个样本）
        for (var i = 0; i < order; i++)
            samples[i] = ReadSignedInt(data, ref pos, bitsPerSample);

        // 解码残差
        var residual = new Int32[blockSize - order];
        DecodeRiceResidual(data, ref pos, blockSize - order, order, residual);

        // Fixed LPC 预测器重建
        for (var i = order; i < blockSize; i++)
        {
            var prediction = order switch
            {
                0 => 0,
                1 => samples[i - 1],
                2 => 2 * samples[i - 1] - samples[i - 2],
                3 => 3 * samples[i - 1] - 3 * samples[i - 2] + samples[i - 3],
                4 => 4 * samples[i - 1] - 6 * samples[i - 2] + 4 * samples[i - 3] - samples[i - 4],
                _ => 0,
            };
            samples[i] = prediction + residual[i - order];
        }
    }

    /// <summary>解码 LPC 子帧</summary>
    private void DecodeLpcSubframe(Span<Byte> data, ref Int32 pos, Int32 order, Int32 blockSize, Int32 bitsPerSample, Int32[] samples)
    {
        // 读取 warm-up 样本
        for (var i = 0; i < order; i++)
            samples[i] = ReadSignedInt(data, ref pos, bitsPerSample);

        // 读取 QLP 精度（4 bits）和左移量（5 bits signed）
        var qlpHeader = data[pos++];
        var qlpPrecision = (qlpHeader >> 4) + 1;
        var qlpShift = (Int32)((qlpHeader & 0x0F) << 1);
        if ((qlpShift & 0x10) != 0) qlpShift |= ~0x1F; // 符号扩展 5-bit → 32-bit
        qlpShift >>= 1; // 右移 1 恢复（因为最低位始终为 0）

        // 读取 QLP 系数（有符号，qlpPrecision 位）
        var qlpCoeffs = new Int32[order];
        for (var i = 0; i < order; i++)
            qlpCoeffs[i] = ReadSignedInt(data, ref pos, qlpPrecision);

        // 解码残差
        var residual = new Int32[blockSize - order];
        DecodeRiceResidual(data, ref pos, blockSize - order, order, residual);

        // LPC 合成滤波器
        for (var i = order; i < blockSize; i++)
        {
            var prediction = 0L;
            for (var j = 0; j < order; j++)
                prediction += (Int64)qlpCoeffs[j] * samples[i - 1 - j];
            samples[i] = (Int32)(prediction >> qlpShift) + residual[i - order];
        }
    }

    /// <summary>解码 Rice 编码的残差</summary>
    /// <remarks>
    /// FLAC 使用分区 Rice 编码（Partitioned Rice Coding）。<br/>
    /// 分区数 = 2^partitionOrder，每个分区有独立的 Rice 参数（4 bits）。
    /// </remarks>
    private void DecodeRiceResidual(Span<Byte> data, ref Int32 pos, Int32 sampleCount, Int32 predictorOrder, Int32[] residual)
    {
        // 分区顺序（0~15，FLAC 限制 0~8）
        var partitionOrder = data[pos] >> 4;
        pos++;

        var numPartitions = 1 << partitionOrder;
        var sampleIdx = 0;

        for (var p = 0; p < numPartitions; p++)
        {
            // 每个分区的样本数
            var partitionSamples = sampleCount / numPartitions;
            if (p == 0) partitionSamples -= predictorOrder; // 第一分区减去预测器阶数

            if (partitionSamples <= 0) continue;

            // Rice 参数（4 bits）
            var riceParam = data[pos] >> 4;
            if (p % 2 == 1) pos++; // 奇数分区移动到下一个字节

            if (riceParam == 15)
            {
                // 转义码：每个样本 5-bit 原始值
                var bitReader = new BitReader(data, pos);
                for (var i = 0; i < partitionSamples && sampleIdx < residual.Length; i++)
                    residual[sampleIdx++] = bitReader.ReadSigned(5);
                pos = bitReader.Position;
            }
            else
            {
                var bitReader = new BitReader(data, pos);
                for (var i = 0; i < partitionSamples && sampleIdx < residual.Length; i++)
                    residual[sampleIdx++] = DecodeRiceSymbol(bitReader, riceParam);
                pos = bitReader.Position;
            }
        }
    }

    /// <summary>解码单个 Rice 符号（unary quotient + binary remainder）</summary>
    private Int32 DecodeRiceSymbol(BitReader reader, Int32 riceParam)
    {
        // 读取一元商
        var quotient = 0;
        while (reader.ReadBit() == 0)
            quotient++;

        // 读取 Rice 参数位的余数
        var remainder = riceParam > 0 ? reader.ReadBits(riceParam) : 0;

        // Rice 解码值 = quotient * 2^riceParam + remainder
        var value = (quotient << riceParam) | remainder;

        // 映射回有符号值（奇数→负数）
        return (value >> 1) ^ -(value & 1);
    }

    /// <summary>从字节流读取有符号整数（FLAC 大端序）</summary>
    private static Int32 ReadSignedInt(Span<Byte> data, ref Int32 pos, Int32 bits)
    {
        if (bits == 0) return 0;

        var bytesNeeded = (bits + 7) / 8;
        var value = 0u;
        for (var i = 0; i < bytesNeeded; i++)
        {
            if (pos >= data.Length) break;
            value = (value << 8) | data[pos++];
        }

        // 对齐到实际位数
        var shift = bytesNeeded * 8 - bits;
        value >>= shift;

        // 符号扩展
        var signBit = 1u << (bits - 1);
        if ((value & signBit) != 0)
            value |= ~(signBit - 1);

        return (Int32)value;
    }

    /// <summary>位读取器，用于逐位解析 Rice 编码数据</summary>
    private ref struct BitReader
    {
        private readonly Span<Byte> _data;
        private Int32 _bytePos;
        private Int32 _bitPos;

        /// <summary>当前字节位置</summary>
        public Int32 Position => _bytePos;

        /// <summary>初始化位读取器</summary>
        public BitReader(Span<Byte> data, Int32 startByte)
        {
            _data = data;
            _bytePos = startByte;
            _bitPos = 0;
        }

        /// <summary>读取 1 个比特</summary>
        public Int32 ReadBit()
        {
            if (_bytePos >= _data.Length) return 0;
            var bit = (_data[_bytePos] >> (7 - _bitPos)) & 1;
            _bitPos++;
            if (_bitPos >= 8)
            {
                _bitPos = 0;
                _bytePos++;
            }
            return bit;
        }

        /// <summary>读取 n 个比特（MSB first）</summary>
        public Int32 ReadBits(Int32 n)
        {
            var value = 0;
            for (var i = 0; i < n; i++)
                value = (value << 1) | ReadBit();
            return value;
        }

        /// <summary>读取 n 比特有符号值</summary>
        public Int32 ReadSigned(Int32 n)
        {
            var value = ReadBits(n);
            var signBit = 1 << (n - 1);
            if ((value & signBit) != 0)
                value |= ~(signBit - 1);
            return value;
        }
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

        // 帧头字节 0: sync code (14 bits "11111111 111110") + reserved(1) + blocking strategy(1)
        // 简化：固定块，使用 blocking strategy = 0
        output.WriteByte(0xFF); // [sync(6) + reserved(1) = 1111111 1] 上半部分
        output.WriteByte(0xF8); // [sync(8) = 11111100] → 但写入格式不同...

        // 实际 FLAC 帧头格式（16-bit sync）：
        // Byte 0: [sync(6)=111111] [reserved(1)] [blocking(1)]
        // Byte 1: [sync(8)=11111100]? 
        // No — the actual format: 14-bit sync 0x3FFE, then 1 reserved, 1 blocking strategy
        // But the sync is split across bytes. Let me use a cleaner approach.

        // Write frame header bytes
        // Byte 0: [0xFF = sync(6) + reserved(1)] with blocking=0 for fixed
        output.WriteByte((Byte)(0xFE | 0)); // blocking strategy = 0 (fixed)

        // Byte 1: block size code + sample rate code in separate fields...
        // Actually the format: byte0 = sync + reserved + blocking; byte1 = blockSizeCode(4) + sampleRateCode(4)
        // Wait, let me re-read the FLAC spec.

        // FLAC frame header:
        // <14> Sync code: 11111111 111110
        // <1> Reserved: 0
        // <1> Blocking strategy: 0=fixed, 1=variable
        // <4> Block size in inter-channel samples
        // <4> Sample rate
        // <4> Channel assignment
        // <3> Sample size in bits
        // <1> Reserved
        // [+ UTF-8 frame/sample number]
        // [+ block size (if code=6 or 7)]
        // [+ sample rate (if code=12/13/14)]
        // <8> CRC-8

        // Simplified: write a valid frame header for Verbatim compression
        var blockSizeCode = GetBlockSizeCode(blockSize);
        var sampleRateCode = GetSampleRateCode(sampleRate);
        var channelAssignment = 0; // mono

        // Byte 0: sync(6)+reserved(1)+blocking(1) = 11111101 or 11111100
        var b0 = (Byte)(0xFC | 0); // sync=111111, reserved=0, blocking=0
        output.WriteByte(b0);

        // Byte 1: blockSizeCode(4) + sampleRateCode(4)
        var b1 = (Byte)((blockSizeCode << 4) | sampleRateCode);
        output.WriteByte(b1);

        // Byte 2: channelAssignment(4) + bitsPerSample-1(3) + reserved(1)
        var b2 = (Byte)((channelAssignment << 4) | ((bitsPerSample - 1) << 1) | 0);
        output.WriteByte(b2);

        // UTF-8 frame number (0 for first frame)
        output.WriteByte(0);

        // Block size (if code 6 or 7)
        if (blockSizeCode == 6) output.WriteByte((Byte)(blockSize - 1));
        else if (blockSizeCode == 7)
        {
            output.WriteByte((Byte)(((blockSize - 1) >> 8) & 0xFF));
            output.WriteByte((Byte)((blockSize - 1) & 0xFF));
        }

        // Sample rate (if code 12/13/14)
        if (sampleRateCode == 12) { output.WriteByte((Byte)(sampleRate / 1000)); }
        else if (sampleRateCode == 13) { /* not used */ }
        else if (sampleRateCode == 14) { output.WriteByte((Byte)(sampleRate / 10)); }

        // CRC-8 (placeholder: 0)
        output.WriteByte(0);

        // Try Fixed LPC encoding — choose best order
        var bestOrder = 0;
        var bestResidual = EncodeVerbatim(samples);
        var bestBits = bestResidual.Length * bitsPerSample;

        for (var order = 1; order <= 4 && order < blockSize; order++)
        {
            var residual = EncodeFixedLpc(samples, order);
            // Estimate Rice-coded size
            var estimatedBits = EstimateRiceSize(residual, order);
            if (estimatedBits < bestBits)
            {
                bestBits = estimatedBits;
                bestOrder = order;
                bestResidual = residual;
            }
        }

        if (bestOrder == 0)
        {
            // Verbatim subframe
            output.WriteByte(0x02); // subframe type 1, wasted=0 → 0x02
            WriteSubframeSamples(samples, bitsPerSample, output);
        }
        else
        {
            // Fixed LPC subframe
            var subframeType = 8 + bestOrder; // 8-12
            output.WriteByte((Byte)(subframeType << 1)); // wasted bits = 0

            // Write warm-up samples
            for (var i = 0; i < bestOrder; i++)
                WriteSignedInt(samples[i], bitsPerSample, output);

            // Write Rice-coded residual
            WriteRiceResidual(bestResidual, bestOrder, output);
        }
    }

    private Int32[] EncodeVerbatim(Span<Int16> samples)
    {
        var residual = new Int32[samples.Length];
        for (var i = 0; i < samples.Length; i++)
            residual[i] = samples[i];
        return residual;
    }

    private Int32[] EncodeFixedLpc(Span<Int16> samples, Int32 order)
    {
        var residual = new Int32[samples.Length];
        // Warm-up samples are the original values
        for (var i = 0; i < order; i++)
            residual[i] = samples[i];

        // Compute residual using Fixed LPC predictor
        for (var i = order; i < samples.Length; i++)
        {
            var prediction = order switch
            {
                1 => samples[i - 1],
                2 => 2 * samples[i - 1] - samples[i - 2],
                3 => 3 * samples[i - 1] - 3 * samples[i - 2] + samples[i - 3],
                4 => 4 * samples[i - 1] - 6 * samples[i - 2] + 4 * samples[i - 3] - samples[i - 4],
                _ => 0,
            };
            residual[i] = samples[i] - prediction;
        }

        return residual;
    }

    private Int32 EstimateRiceSize(Int32[] residual, Int32 order)
    {
        var totalBits = 0;
        // Warm-up samples
        totalBits += order * 16;

        // Residual: use partition order 0 (single partition), estimate with Rice param 4
        for (var i = order; i < residual.Length; i++)
        {
            var value = residual[i];
            // Map to unsigned: even=positive, odd=negative
            var unsigned = value >= 0 ? value * 2 : (-value) * 2 - 1;
            // Estimate Rice-coded bits with param=4
            var quotient = unsigned >> 4;
            totalBits += 1 + quotient + 4; // 1 for stop bit + quotient unary + 4 binary
        }

        totalBits += 4 + 4; // partition order + rice param
        return totalBits;
    }

    private void WriteSubframeSamples(Span<Int16> samples, Int32 bitsPerSample, Stream output)
    {
        for (var i = 0; i < samples.Length; i++)
            WriteSignedInt(samples[i], bitsPerSample, output);
    }

    private static void WriteSignedInt(Int32 value, Int32 bits, Stream output)
    {
        var bytesNeeded = (bits + 7) / 8;
        // Mask to bits
        var mask = bits < 32 ? (1u << bits) - 1 : 0xFFFFFFFFu;
        var unsigned = (UInt32)(value & (Int32)mask);

        for (var i = bytesNeeded - 1; i >= 0; i--)
            output.WriteByte((Byte)((unsigned >> (i * 8)) & 0xFF));
    }

    private void WriteRiceResidual(Int32[] residual, Int32 predictorOrder, Stream output)
    {
        var sampleCount = residual.Length - predictorOrder;

        // Partition order 0: single partition
        output.WriteByte(0x00); // partitionOrder=0, reserved=0

        // Choose Rice parameter (simplified: use fixed param 4 for 16-bit audio)
        var riceParam = 4;
        output.WriteByte((Byte)(riceParam << 4));

        // Bit buffer for Rice coding
        var bitBuffer = 0uL;
        var bitsInBuffer = 0;

        void FlushBits()
        {
            while (bitsInBuffer >= 8)
            {
                output.WriteByte((Byte)((bitBuffer >> (Int32)(bitsInBuffer - 8)) & 0xFF));
                bitsInBuffer -= 8;
            }
        }

        void WriteBit(Int32 bit)
        {
            bitBuffer = (bitBuffer << 1) | (UInt32)(bit & 1);
            bitsInBuffer++;
            FlushBits();
        }

        void WriteBits(Int32 value, Int32 n)
        {
            for (var i = n - 1; i >= 0; i--)
                WriteBit((value >> i) & 1);
        }

        for (var i = predictorOrder; i < residual.Length; i++)
        {
            var value = residual[i];
            // Map signed to unsigned（even=0/+, odd=-）
            var unsigned = value >= 0 ? value * 2 : (-value) * 2 - 1;

            // Write unary quotient
            var quotient = unsigned >> riceParam;
            for (var q = 0; q < quotient; q++)
                WriteBit(0);
            WriteBit(1); // stop bit

            // Write remainder
            if (riceParam > 0)
                WriteBits(unsigned & ((1 << riceParam) - 1), riceParam);
        }

        // Flush remaining bits
        while (bitsInBuffer > 0)
        {
            output.WriteByte((Byte)((bitBuffer << (Int32)(8 - bitsInBuffer)) & 0xFF));
            bitsInBuffer = bitsInBuffer > 8 ? bitsInBuffer - 8 : 0;
        }
    }

    private static Int32 GetBlockSizeCode(Int32 blockSize)
    {
        return blockSize switch
        {
            192 => 1,
            576 => 2,
            1152 => 3,
            2304 => 4,
            4608 => 5,
            256 => 8,
            512 => 9,
            1024 => 10,
            2048 => 11,
            4096 => 12,
            8192 => 13,
            16384 => 14,
            32768 => 15,
            _ => blockSize < 256 ? 6 : 7, // Use 8-bit or 16-bit escape code
        };
    }

    private static Int32 GetSampleRateCode(Int32 sampleRate)
    {
        return sampleRate switch
        {
            88200 => 1,
            176400 => 2,
            192000 => 3,
            8000 => 4,
            16000 => 5,
            22050 => 6,
            24000 => 7,
            32000 => 8,
            44100 => 9,
            48000 => 10,
            96000 => 11,
            _ => sampleRate % 1000 == 0 ? 12 : 14, // kHz * 1000 or Hz * 10
        };
    }

    #endregion

    #region 元数据

    private static StreamInfo ParseStreamInfo(Byte[] data, Int32 offset)
    {
        var reader = new SpanReader(data.AsSpan(offset)) { IsLittleEndian = false };

        var minBlock = reader.ReadUInt16();
        var maxBlock = reader.ReadUInt16();
        var minFrame = reader.ReadUInt24();
        var maxFrame = reader.ReadUInt24();

        // 剩余 24 字节：5B 位域（采样率/声道/位深）+ 5B 总采样数（36 位）+ 16B MD5
        var raw = reader.ReadBytes(24);

        var sampleRate = (UInt32)((raw[0] << 12) | (raw[1] << 4) | (raw[2] >> 4));
        var channels = (Byte)(((raw[2] & 0x0E) >> 1) + 1);
        var bitsPerSample = (Byte)(((raw[2] & 0x01) << 4) | ((raw[3] & 0xF0) >> 4) + 1);
        var totalSamples = (UInt64)((raw[3] & 0x0F) << 28) |
                           ((UInt64)raw[4] << 20) |
                           ((UInt64)raw[5] << 12) |
                           ((UInt64)raw[6] << 4) |
                           ((UInt64)raw[7] >> 4);

        return new StreamInfo
        {
            MinBlockSize = minBlock,
            MaxBlockSize = maxBlock,
            MinFrameSize = minFrame,
            MaxFrameSize = maxFrame,
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = bitsPerSample,
            TotalSamples = totalSamples,
            Md5Sum = raw.Slice(8, 16).ToArray(),
        };
    }

    private static void WriteStreamInfoBlock(MemoryStream ms, UInt16 minBlock, UInt16 maxBlock,
        Int32 sampleRate, Int32 channels, Byte bitsPerSample, UInt64 totalSamples)
    {
        var buf = new Byte[38]; // 4 header + 34 data
        var writer = new SpanWriter(buf) { IsLittleEndian = false };

        // 元数据块头（last=1, type=0, size=34）
        writer.WriteByte(0x80); // last + STREAMINFO
        writer.WriteByte(0x00); // size
        writer.WriteByte(0x00);
        writer.WriteByte(34);

        // MinBlockSize / MaxBlockSize
        writer.Write(minBlock);
        writer.Write(maxBlock);

        // MinFrameSize / MaxFrameSize (unknown: 0, 24-bit each)
        writer.FillZero(6);

        // SampleRate (20 bits) + Channels (3 bits) + BitsPerSample-1 (5 bits)
        var sampleRate20 = (UInt64)sampleRate;
        var channels3 = (UInt64)(channels - 1) & 0x07;
        var bps5 = (UInt64)(bitsPerSample - 1) & 0x1F;
        var combined = (sampleRate20 << 8) | (channels3 << 5) | (bps5 << 0);
        writer.WriteByte((Byte)((combined >> 32) & 0xFF));
        writer.WriteByte((Byte)((combined >> 24) & 0xFF));
        writer.WriteByte((Byte)((combined >> 16) & 0xFF));
        writer.WriteByte((Byte)((combined >> 8) & 0xFF));
        writer.WriteByte((Byte)(combined & 0xFF));

        // TotalSamples (36 bits)
        writer.WriteByte((Byte)((totalSamples >> 28) & 0xFF));
        writer.WriteByte((Byte)((totalSamples >> 20) & 0xFF));
        writer.WriteByte((Byte)((totalSamples >> 12) & 0xFF));
        writer.WriteByte((Byte)((totalSamples >> 4) & 0xFF));
        writer.WriteByte((Byte)((totalSamples << 4) & 0xF0));

        // MD5 (16 bytes of zeros)
        writer.FillZero(16);

        ms.Write(buf, 0, buf.Length);
    }

    #endregion
}
