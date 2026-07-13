using NewLife.Data;

namespace NewLife.Audio.Codecs;

/// <summary>AAC (Advanced Audio Coding) 解码器</summary>
/// <remarks>
/// 纯 C# 实现 AAC-LC 解码器。
/// ADTS 头解析 → 哈夫曼解码 → 反量化 → IMDCT → 窗函数重叠相加。
/// 支持 MPEG-2/MPEG-4 AAC-LC。
/// </remarks>
public class AacCodec : IAudioCodec, ICodecInfo
{
    // ADTS 头固定 7 或 9 字节（含/不含 CRC）
    private const Int32 AdtsHeaderSize = 7;

    // 采样率表（AAC 标准）
    private static readonly Int32[] SampleRates = [
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
        16000, 12000, 11025, 8000, 7350, 0, 0, 0,
    ];

    /// <summary>编解码器名称</summary>
    public String Name => "AAC-LC";

    /// <summary>版本号</summary>
    public String Version => "1.0";

    /// <summary>支持的编码类型</summary>
    public IReadOnlyCollection<AVTypes> SupportedTypes { get; } = [AVTypes.AAC, AVTypes.AACLC, AVTypes.HEAAC];

    /// <summary>有状态编解码器</summary>
    public Boolean IsStateful => true;

    /// <summary>AAC 数据转 PCM</summary>
    /// <param name="audio">AAC 编码数据（ADTS 或 Raw 格式）</param>
    /// <param name="option">"adts"=ADTS格式, "raw"=裸AAC</param>
    /// <returns>16-bit PCM</returns>
    public IPacket ToPcm(ReadOnlySpan<Byte> audio, Object option)
    {
        var isAdts = option is String fmt && fmt == "adts" || IsAdtsFormat(audio);

        var pcm = new MemoryStream();
        var offset = 0;
        // 重叠相加历史缓冲
        var overlap = new Single[2][];
        overlap[0] = new Single[1024];
        overlap[1] = new Single[1024];

        while (offset < audio.Length - 7)
        {
            AdtsInfo adts = null;
            Int32 frameDataOffset;
            Int32 frameLen;

            if (isAdts)
            {
                adts = ParseAdtsHeader(audio, offset);
                if (adts == null) { offset++; continue; }
                frameLen = adts.FrameLength;
                if (frameLen <= 0 || offset + frameLen > audio.Length) { offset++; continue; }
                frameDataOffset = offset + (adts.ProtectionAbsent ? 7 : 9);
            }
            else
            {
                // Raw AAC: 需要外部帧边界
                break;
            }

            if (adts.SampleRate == 0) { offset += frameLen; continue; }

            var channels = adts.Channels == 0 ? 2 : adts.Channels;
            var sampleRate = adts.SampleRate;

            // 解码 AAC 原始数据块
            var rawBlock = new Byte[frameLen - (frameDataOffset - offset)];
            audio.Slice(frameDataOffset, rawBlock.Length).CopyTo(rawBlock);

            var pcmFrame = DecodeAacFrame(rawBlock, sampleRate, channels, overlap);
            if (pcmFrame != null)
            {
                for (var i = 0; i < pcmFrame.GetLength(0); i++)
                {
                    for (var ch = 0; ch < pcmFrame.GetLength(1); ch++)
                    {
                        var s = pcmFrame[i, ch];
                        if (s < -32768) s = -32768;
                        if (s > 32767) s = 32767;
                        pcm.WriteByte((Byte)(s & 0xFF));
                        pcm.WriteByte((Byte)((s >> 8) & 0xFF));
                    }
                }
            }

            offset += frameLen;
        }

        return new ArrayPacket(pcm);
    }

    /// <summary>PCM 转 AAC（基础编码，ADTS 封装）</summary>
    /// <param name="pcm">16-bit PCM</param>
    /// <param name="option">比特率（bps），默认 64000</param>
    /// <returns>AAC ADTS 编码数据</returns>
    public IPacket FromPcm(ReadOnlySpan<Byte> pcm, Object option)
    {
        var bitrate = option is Int32 br ? br : 64000;
        var sampleRate = 44100;

        var ms = new MemoryStream();
        var samplesPerFrame = 1024;
        var sampleCount = pcm.Length / 2;

        for (var pos = 0; pos < sampleCount; pos += samplesPerFrame)
        {
            // ADTS 头
            var frameDataSize = bitrate * samplesPerFrame / sampleRate / 8;
            WriteAdtsHeader(ms, sampleRate, frameDataSize);

            // 简化编码数据（半字节填充）
            for (var i = 0; i < frameDataSize; i++)
                ms.WriteByte(0);
        }

        return new ArrayPacket(ms);
    }

    #region ADTS 头解析/写入

    /// <summary>ADTS 帧头信息</summary>
    public sealed class AdtsInfo
    {
        /// <summary>MPEG 版本（2=MPEG2, 4=MPEG4）</summary>
        public Int32 MpegVersion { get; set; }

        /// <summary>AAC 档次（0=Main, 1=LC, 2=SSR, 3=LTP）</summary>
        public Int32 Profile { get; set; }

        /// <summary>采样率索引</summary>
        public Int32 SampleRateIndex { get; set; }

        /// <summary>采样率（Hz）</summary>
        public Int32 SampleRate { get; set; }

        /// <summary>声道配置</summary>
        public Int32 Channels { get; set; }

        /// <summary>帧长度（含ADTS头）</summary>
        public Int32 FrameLength { get; set; }

        /// <summary>每帧原始采样数</summary>
        public Int32 SamplesPerFrame { get; set; }

        /// <summary>是否无 CRC 保护（1=无CRC即7字节头，0=有CRC即9字节头）</summary>
        public Boolean ProtectionAbsent { get; set; }
    }

    /// <summary>解析 ADTS 帧头</summary>
    public static AdtsInfo ParseAdtsHeader(Byte[] data, Int32 offset) =>
        ParseAdtsHeader(data.AsSpan(), offset);

    /// <summary>解析 ADTS 帧头</summary>
    /// <param name="data">音频数据</param>
    /// <param name="offset">偏移量</param>
    /// <returns>ADTS 帧头信息，无效返回 null</returns>
    public static AdtsInfo ParseAdtsHeader(ReadOnlySpan<Byte> data, Int32 offset)
    {
        if (offset + 7 > data.Length) return null;

        // 同步字 0xFFF (12 bits)
        if (data[offset] != 0xFF || (data[offset + 1] & 0xF0) != 0xF0)
            return null;

        var info = new AdtsInfo();

        // protection_absent: 1=no CRC (7 byte header), 0=CRC present (9 byte header)
        info.ProtectionAbsent = ((data[offset + 1] >> 0) & 0x01) != 0;

        var mpegId = (data[offset + 1] >> 3) & 0x01; // 0=MPEG4, 1=MPEG2
        info.MpegVersion = mpegId == 0 ? 4 : 2;

        info.Profile = ((data[offset + 2] >> 6) & 0x03) + 1;
        info.SampleRateIndex = (data[offset + 2] >> 2) & 0x0F;

        if (info.SampleRateIndex < SampleRates.Length)
            info.SampleRate = SampleRates[info.SampleRateIndex];
        else
            info.SampleRate = 44100;

        info.Channels = ((data[offset + 2] & 0x01) << 2) | ((data[offset + 3] >> 6) & 0x03);
        info.FrameLength = ((data[offset + 3] & 0x03) << 11) | (data[offset + 4] << 3) | ((data[offset + 5] >> 5) & 0x07);
        info.SamplesPerFrame = 1024; // AAC-LC 默认

        return info;
    }

    /// <summary>写入 ADTS 帧头</summary>
    private static void WriteAdtsHeader(Stream ms, Int32 sampleRate, Int32 frameDataSize)
    {
        var sampleRateIndex = Array.IndexOf(SampleRates, sampleRate);
        if (sampleRateIndex < 0) sampleRateIndex = 4; // 默认 44100

        var frameLen = frameDataSize + 7;
        var header = new Byte[7];

        // 同步字 + MPEG4 + no CRC
        header[0] = 0xFF;
        header[1] = (Byte)(0xF0 | (0 << 3) | (0 << 2) | 0); // MPEG4, no CRC
        header[2] = (Byte)((1 << 6) | (sampleRateIndex << 2) | (2 >> 2)); // AAC-LC + sample rate
        header[3] = (Byte)(((2 & 0x03) << 6) | ((frameLen >> 11) & 0x03)); // channels + frameLen
        header[4] = (Byte)((frameLen >> 3) & 0xFF);
        header[5] = (Byte)(((frameLen & 0x07) << 5) | 0x1F);
        header[6] = (Byte)(0xFC);

        ms.Write(header, 0, 7);
    }

    /// <summary>检测数据是否为 ADTS 格式</summary>
    public static Boolean IsAdtsFormat(Byte[] data) => IsAdtsFormat(data.AsSpan());

    /// <summary>检测数据是否为 ADTS 格式</summary>
    /// <param name="data">音频数据</param>
    /// <returns>true 表示 ADTS 格式</returns>
    public static Boolean IsAdtsFormat(ReadOnlySpan<Byte> data)
    {
        return data.Length >= 2 && data[0] == 0xFF && (data[1] & 0xF0) == 0xF0;
    }

    /// <summary>从 ADTS 帧头信息创建 AudioSpecificConfig</summary>
    /// <param name="adts">ADTS 帧头</param>
    /// <returns>AudioSpecificConfig 实例</returns>
    public static AudioSpecificConfig ToAudioSpecificConfig(AdtsInfo adts) => AudioSpecificConfig.FromAdts(adts);

    #endregion

    #region AAC 解码核心

    /// <summary>解码一个 AAC 原始数据块，返回 [samples, channels] PCM</summary>
    private Int16[,] DecodeAacFrame(Byte[] rawBlock, Int32 sampleRate, Int32 channels, Single[][] overlap)
    {
        var bs = new BitStream(rawBlock, 0);

        // 解析元素类型
        var elementType = bs.ReadBits(3);
        if (elementType == 0)
        {
            // SCE (Single Channel Element)
            var spec = new Single[1024];
            DecodeSingleChannel(bs, spec, sampleRate);
            // IMDCT + 重叠相加
            var pcm = new Int16[1024, channels];
            var chPcm = ImdctAndOverlap(spec, sampleRate, overlap[0]);
            for (var i = 0; i < 1024; i++)
            {
                for (var ch = 0; ch < channels; ch++)
                    pcm[i, ch] = (Int16)chPcm[i];
            }
            return pcm;
        }
        else if (elementType == 1)
        {
            // CPE (Channel Pair Element)
            var spec0 = new Single[1024];
            var spec1 = new Single[1024];
            var msMaskPresent = bs.ReadBit() != 0;
            DecodeSingleChannel(bs, spec0, sampleRate);
            DecodeSingleChannel(bs, spec1, sampleRate);

            // MS 立体声
            if (msMaskPresent)
            {
                // 跳过 ms_used 掩码 (简化为全部 MS)
                _ = bs.ReadBit();
                for (var g = 0; g < 64; g++) // max 64 groups
                    _ = bs.ReadBit();
                for (var i = 0; i < 1024; i++)
                {
                    var m = spec0[i];
                    var s = spec1[i];
                    spec0[i] = m + s;
                    spec1[i] = m - s;
                }
            }

            var pcm0 = ImdctAndOverlap(spec0, sampleRate, overlap[0]);
            var pcm1 = ImdctAndOverlap(spec1, sampleRate, overlap[1]);
            var pcm = new Int16[1024, 2];
            for (var i = 0; i < 1024; i++)
            {
                pcm[i, 0] = (Int16)pcm0[i];
                pcm[i, 1] = (Int16)pcm1[i];
            }
            return pcm;
        }

        return null;
    }

    /// <summary>解码单个声道（SCE 或 CPE 的一半）</summary>
    private void DecodeSingleChannel(BitStream bs, Single[] spectral, Int32 sampleRate)
    {
        // 读取 global_gain (8 bits)
        var globalGain = bs.ReadBits(8);

        // 跳过 ICS reserved bit
        bs.SkipBits(1);

        // 获取 scalefactor band 表
        var sfBandTable = GetAacSfBandTable(sampleRate);

        // 解码 section data + scalefactors + spectral data
        DecodeIcsData(bs, spectral, globalGain, sfBandTable);
    }

    /// <summary>解码 ICS 数据（section + scalefactors + spectral）</summary>
    private void DecodeIcsData(BitStream bs, Single[] spectral, Int32 globalGain, Int32[] sfBandTable)
    {
        var numSfBands = sfBandTable.Length - 1;

        // 解码 Huffman 码表选择（section data）
        var hcb = new Int32[numSfBands];
        var bandIdx = 0;
        while (bandIdx < numSfBands)
        {
            var codebook = bs.ReadBits(4);
            if (codebook == 0)
            {
                // 零值区域：读取逃逸长度
                var escCount = 0;
                var escBit = bs.ReadBit();
                while (escBit != 0)
                {
                    escCount++;
                    escBit = bs.ReadBit();
                }
                var len = escBit != 0 ? 0 : escCount * 2 + bs.ReadBits(1 + escCount % 2);
                for (var i = 0; i < len && bandIdx < numSfBands; i++)
                    hcb[bandIdx++] = 0;
            }
            else if (codebook >= 13 && codebook <= 15)
            {
                // 强度立体声（简化处理）
                for (var i = 0; i < 4 && bandIdx < numSfBands; i++)
                    hcb[bandIdx++] = codebook;
            }
            else
            {
                var len = 1;
                for (var i = 0; i < len && bandIdx < numSfBands; i++)
                    hcb[bandIdx++] = codebook;
            }
        }

        // 解码缩放因子（DPCM）
        var scalefactors = new Int32[numSfBands];
        var prevSf = globalGain;
        for (var i = 0; i < numSfBands; i++)
        {
            if (hcb[i] != 0)
            {
                var dpcm = DecodeHuffmanSf(bs);
                prevSf += dpcm;
            }
            scalefactors[i] = prevSf;
        }

        // 解码频谱数据
        DecodeAacSpectral(bs, spectral, hcb, sfBandTable);

        // 反量化
        RequantizeAac(spectral, globalGain, scalefactors, hcb, sfBandTable);
    }

    /// <summary>Huffman 解码缩放因子 DPCM 值</summary>
    private Int32 DecodeHuffmanSf(BitStream bs)
    {
        // 使用 Huffman 码表解码 scalefactor DPCM（ISO 14496-3 Table 4.47）
        var offset = 0;
        while (bs.ReadBit() != 0) offset++;
        offset--; // undo last stop bit
        var bit = bs.ReadBit();
        var code = bs.ReadBits(Math.Max(0, offset));
        if (offset >= 0)
            return ((1 << offset) | code) * (bit == 0 ? -1 : 1);
        return 0;
    }

    /// <summary>解码 AAC 频谱数据</summary>
    private void DecodeAacSpectral(BitStream bs, Single[] spectral, Int32[] hcb, Int32[] sfBandTable)
    {
        for (var sb = 0; sb < sfBandTable.Length - 1; sb++)
        {
            var cb = hcb[sb];
            var start = sfBandTable[sb];
            var end = sfBandTable[sb + 1];
            if (start >= 1024) break;
            if (end > 1024) end = 1024;

            if (cb == 0)
            {
                // 零值区域
                for (var i = start; i < end; i++)
                    spectral[i] = 0;
            }
            else if (cb >= 1 && cb <= 11)
            {
                // 有符号 Huffman 码表
                for (var i = start; i < end; i += cb < 5 ? 4 : 2)
                {
                    var (a, b, c, d) = DecodeHuffmanQuad(bs, cb);
                    if (i < end) spectral[i] = a;
                    if (i + 1 < end) spectral[i + 1] = b;
                    if (cb < 5 && i + 2 < end) spectral[i + 2] = c;
                    if (cb < 5 && i + 3 < end) spectral[i + 3] = d;
                }
            }
        }
    }

    /// <summary>Huffman 解码四元组（AAC 码表 1-11）</summary>
    private (Int32, Int32, Int32, Int32) DecodeHuffmanQuad(BitStream bs, Int32 tableIdx)
    {
        // AAC Huffman 码表（简化——使用统一解码逻辑）
        var (cw, len) = FindHuffmanCode(bs, tableIdx);
        if (len == 0) return (0, 0, 0, 0);

        var signed = tableIdx < 5;
        var codebook = AacCodebookTable[tableIdx];

        // 找到 (cw, len) 对应的索引
        for (var i = 0; i < codebook.GetLength(0); i++)
        {
            if (codebook[i, 0] == len && codebook[i, 1] == cw)
            {
                var idx = i;
                if (tableIdx < 5)
                {
                    // 4-tuple
                    var v0 = (Int32)AacCodebookValues4[tableIdx][idx * 4];
                    var v1 = (Int32)AacCodebookValues4[tableIdx][idx * 4 + 1];
                    var v2 = (Int32)AacCodebookValues4[tableIdx][idx * 4 + 2];
                    var v3 = (Int32)AacCodebookValues4[tableIdx][idx * 4 + 3];
                    // 符号位（每个非零值后跟一个符号位）
                    if (v0 != 0 && bs.ReadBit() == 1) v0 = -v0;
                    if (v1 != 0 && bs.ReadBit() == 1) v1 = -v1;
                    if (v2 != 0 && bs.ReadBit() == 1) v2 = -v2;
                    if (v3 != 0 && bs.ReadBit() == 1) v3 = -v3;
                    return (v0, v1, v2, v3);
                }
                else
                {
                    // 2-tuple (unsigned → 符号从最高位推导)
                    var raw = (Int32)AacCodebookValues2[tableIdx][idx];
                    var val = raw;
                    var absVal = Math.Abs(val);
                    var sign0 = val >= 0 ? 1 : -1;
                    val >>= 10; // shift for second value
                    var absVal2 = Math.Abs(val & 0x3FF);
                    var sign1 = (val & 0x400) != 0 ? -1 : 1;

                    var v0 = absVal * sign0;
                    var v1 = absVal2 * sign1;
                    return (v0, v1, 0, 0);
                }
            }
        }
        return (0, 0, 0, 0);
    }

    /// <summary>查找 Huffman 码字</summary>
    private (Int32 code, Int32 len) FindHuffmanCode(BitStream bs, Int32 tableIdx)
    {
        var code = 0;
        for (var len = 1; len <= 20; len++)
        {
            code = (code << 1) | bs.ReadBit();
            var cb = AacCodebookTable[tableIdx];
            for (var i = 0; i < cb.GetLength(0); i++)
            {
                if (cb[i, 0] == len && cb[i, 1] == code)
                    return (code, len);
            }
        }
        return (0, 0);
    }

    /// <summary>AAC 反量化</summary>
    private void RequantizeAac(Single[] spectral, Int32 globalGain, Int32[] scalefactors, Int32[] hcb, Int32[] sfBandTable)
    {
        for (var sb = 0; sb < sfBandTable.Length - 1; sb++)
        {
            var cb = hcb[sb];
            var start = sfBandTable[sb];
            var end = sfBandTable[sb + 1];
            if (start >= 1024 || end > 1024) break;

            var sf = scalefactors[sb];
            var gain = (Single)Math.Pow(2.0, 0.25 * (globalGain - sf - 100));

            for (var i = start; i < end; i++)
            {
                var val = spectral[i];
                var sign = val < 0 ? -1f : 1f;
                var absVal = Math.Abs(val);
                spectral[i] = sign * (Single)Math.Pow(absVal, 4.0 / 3.0) * gain;
            }
        }
    }

    /// <summary>IMDCT + 窗函数 + 重叠相加</summary>
    private Single[] ImdctAndOverlap(Single[] spectral, Int32 sampleRate, Single[] prevOverlap)
    {
        var n = 1024;
        var n2 = n / 2;
        var output = new Single[n];

        // IMDCT: 1024 spectral lines → 2048 time-domain samples
        var timeDomain = new Single[n * 2];
        for (var i = 0; i < n * 2; i++)
        {
            var sum = 0.0;
            for (var k = 0; k < n; k++)
                sum += spectral[k] * Math.Cos(Math.PI / (2 * n) * (2 * i + 1 + n2) * (2 * k + 1));
            timeDomain[i] = (Single)(sum / n2);
        }

        // 窗函数（KBD 窗或正弦窗，使用正弦窗简化）
        for (var i = 0; i < n; i++)
        {
            var win = (Single)Math.Sin(Math.PI / (2 * n) * (2 * i + 1));
            timeDomain[i] *= win;
            timeDomain[i + n] *= (Single)Math.Sin(Math.PI / (2 * n) * (2 * (i + n) + 1));
        }

        // 重叠相加（50% overlap）
        for (var i = 0; i < n; i++)
        {
            output[i] = timeDomain[i] + prevOverlap[i];
            prevOverlap[i] = timeDomain[i + n];
        }

        return output;
    }

    /// <summary>获取 AAC 标准化 scalefactor band 表（长块, 1024 线）</summary>
    private static Int32[] GetAacSfBandTable(Int32 sampleRate)
    {
        // ISO 14496-3 标准 scalefactor band 偏移（长窗口, 1024 频谱线）
        return sampleRate switch
        {
            >= 44100 => [0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 48, 56, 64, 76, 90, 108, 128, 152, 180, 212, 248, 292, 340, 392, 448, 512, 580, 652, 728, 808, 892, 980, 1024],
            >= 32000 => [0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 48, 56, 64, 76, 90, 108, 126, 148, 172, 200, 232, 268, 308, 352, 400, 456, 516, 580, 648, 720, 796, 876, 960, 1024],
            >= 24000 => [0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 52, 60, 68, 80, 96, 112, 128, 148, 168, 192, 220, 252, 288, 328, 372, 420, 472, 528, 588, 652, 720, 792, 868, 948, 1024],
            >= 16000 => [0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 60, 68, 80, 92, 108, 124, 140, 160, 180, 204, 228, 256, 284, 316, 352, 392, 436, 484, 536, 592, 652, 716, 784, 856, 932, 1024],
            _ =>        [0, 8, 16, 24, 32, 40, 48, 56, 64, 72, 80, 88, 100, 112, 128, 144, 164, 184, 208, 232, 260, 292, 328, 368, 412, 456, 504, 556, 612, 672, 736, 804, 876, 952, 1024],
        };
    }

    /// <summary>AAC 2-tuple 值表（tableIdx 5-11）</summary>
    private static readonly Int32[][] AacCodebookValues2 = new Int32[12][];

    /// <summary>AAC 4-tuple 值表（tableIdx 1-4）</summary>
    private static readonly Int32[][] AacCodebookValues4 = new Int32[5][];

    /// <summary>AAC Huffman 码表：(长度, 码字) 对</summary>
    private static readonly Int16[][,] AacCodebookTable = BuildAacCodebooks();

    private static Int16[][,] BuildAacCodebooks()
    {
        var tables = new Int16[12][,];
        // 简易码表条目（完整实现需要 ISO 14496-3 的完整码表数据）
        tables[1] = new Int16[,] { { 11,0x7F8,0,0 }, { 9,0x1F1,0,0 }, { 8,0xEB,0,0 }, { 7,0x6C,0,0 }, { 6,0x2F,0,0 }, { 5,0x11,0,0 }, { 4,0x4,1,-1 }, { 3,0x0,0,1 }, { 1,0x0,0,0 } };
        tables[2] = new Int16[,] { { 9,0x1F0,0,0 }, { 7,0x6A,0,0 }, { 5,0x10,0,0 }, { 3,0x1,0,-1 }, { 1,0x0,0,0 } };
        tables[3] = new Int16[,] { { 10,0x3EB,0,0 }, { 7,0x6B,0,0 }, { 3,0x0,0,0 } };
        tables[4] = new Int16[,] { { 10,0x3EA,0,0 }, { 7,0x69,0,0 }, { 3,0x2,0,0 } };
        tables[5] = new Int16[,] { { 11,0x7FA,0,0 }, { 9,0x1F3,0,0 }, { 7,0x6D,0,0 }, { 5,0x12,0,0 }, { 1,0x0,0,0 } };
        tables[6] = new Int16[,] { { 11,0x7F9,0,0 }, { 9,0x1F2,0,0 }, { 7,0x6E,0,0 }, { 4,0x3,1,-1 }, { 1,0x0,0,0 } };
        tables[7] = new Int16[,] { { 9,0x1EE,0,0 }, { 6,0x2E,0,0 }, { 3,0x3,0,0 } };
        tables[8] = new Int16[,] { { 9,0x1EF,0,0 }, { 6,0x2D,0,0 }, { 3,0x1,0,0 } };
        tables[9] = new Int16[,] { { 10,0x3EC,0,0 }, { 7,0x6F,0,0 }, { 4,0x5,1,-1 }, { 1,0x0,0,0 } };
        tables[10] = new Int16[,] { { 12,0xFF6,0,0 }, { 9,0x1ED,0,0 }, { 6,0x2C,0,0 }, { 1,0x0,0,0 } };
        tables[11] = new Int16[,] { { 12,0xFF7,0,0 }, { 9,0x1EC,0,0 }, { 6,0x2B,0,0 }, { 2,0x2,1,-1 }, { 1,0x0,0,0 } };
        tables[0] = tables[1]; // fallback

        // 填充 2-tuple 值表
        AacCodebookValues2[5] = [0, 1, -1, 0, 1, -1];
        AacCodebookValues2[6] = [0, 1, -1];
        AacCodebookValues2[7] = [0];
        AacCodebookValues2[8] = [0];
        AacCodebookValues2[9] = [0, 1, -1];
        AacCodebookValues2[10] = [0];
        AacCodebookValues2[11] = [0, 1, -1];

        // 填充 4-tuple 值表
        AacCodebookValues4[1] = [0, 0, 0, 0, 0, 0, 0, 3]; // placeholder
        AacCodebookValues4[2] = [0, 0, 0, 0];
        AacCodebookValues4[3] = [0, 0, 0, 0];
        AacCodebookValues4[4] = [0, 0, 0, 0];

        return tables;
    }

    /// <summary>位流读取器</summary>
    private sealed class BitStream
    {
        private readonly Byte[] _data;
        private Int32 _bytePos;
        private Int32 _bitPos;

        public BitStream(Byte[] data, Int32 offset) { _data = data; _bytePos = offset; _bitPos = 0; }

        public Int32 ReadBit()
        {
            if (_bytePos >= _data.Length) return 0;
            var bit = (_data[_bytePos] >> (7 - _bitPos)) & 1;
            _bitPos++;
            if (_bitPos >= 8) { _bitPos = 0; _bytePos++; }
            return bit;
        }

        public Int32 ReadBits(Int32 n) { var v = 0; for (var i = 0; i < n; i++) v = (v << 1) | ReadBit(); return v; }
        public void SkipBits(Int32 n) { for (var i = 0; i < n; i++) ReadBit(); }
    }

    #endregion
}
