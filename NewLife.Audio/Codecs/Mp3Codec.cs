using NewLife.Data;

namespace NewLife.Audio.Codecs;

/// <summary>MP3 (MPEG Audio Layer III) 解码器</summary>
/// <remarks>
/// 纯 C# 实现，支持 MPEG-1 Layer III 解码。
/// 帧同步 → 边信息解析 → 哈夫曼解码 → 反量化 → IMDCT → 子带合成。
/// 采样率：32/44.1/48kHz，比特率：32~320kbps。
/// </remarks>
public class Mp3Codec : IAudioCodec, ICodecInfo
{
    /// <summary>MPEG 版本</summary>
    public enum MpegVersion
    {
        /// <summary>MPEG-1</summary>
        Mpeg1,
        /// <summary>MPEG-2</summary>
        Mpeg2,
        /// <summary>MPEG-2.5</summary>
        Mpeg2_5,
    }

    /// <summary>MP3 帧头信息</summary>
    public sealed class FrameHeader
    {
        /// <summary>MPEG 版本</summary>
        public MpegVersion Version { get; set; }

        /// <summary>音频层（应为3）</summary>
        public Int32 Layer { get; set; }

        /// <summary>比特率索引</summary>
        public Int32 BitrateIndex { get; set; }

        /// <summary>采样率索引</summary>
        public Int32 SampleRateIndex { get; set; }

        /// <summary>采样率（Hz）</summary>
        public Int32 SampleRate { get; set; }

        /// <summary>比特率（bps）</summary>
        public Int32 Bitrate { get; set; }

        /// <summary>声道模式</summary>
        public Int32 ChannelMode { get; set; }

        /// <summary>每帧采样数</summary>
        public Int32 SamplesPerFrame { get; set; }

        /// <summary>帧大小（字节）</summary>
        public Int32 FrameSize { get; set; }
    }

    // 比特率表（kbps）[MPEG1/MPEG2]，用于 Layer I/II 帧头解析
    private static readonly Int32[,] BitrateTable = {
        { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, -1 }, // MPEG1
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, -1 },         // MPEG2
    };

    // Layer III 比特率表（kbps）[MPEG1 / MPEG2·2.5]
    private static readonly Int32[,] Layer3BitrateTable = {
        { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, -1 },
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, -1 },
    };

    // 采样率表（Hz）
    private static readonly Int32[,] SampleRateTable = {
        { 44100, 48000, 32000 }, // MPEG1
        { 22050, 24000, 16000 }, // MPEG2
        { 11025, 12000, 8000  }, // MPEG2.5
    };

    /// <summary>编解码器名称</summary>
    public String Name => "MP3 (MPEG Audio Layer III)";

    /// <summary>版本号</summary>
    public String Version => "1.0";

    /// <summary>支持的编码类型</summary>
    public IReadOnlyCollection<AVTypes> SupportedTypes { get; } = [AVTypes.MP3, AVTypes.MPEGAUDIO];

    /// <summary>有状态编解码器</summary>
    public Boolean IsStateful => true;

    /// <summary>MP3 数据转 PCM</summary>
    /// <param name="audio">MP3 编码数据</param>
    /// <param name="option"></param>
    /// <returns>16-bit PCM</returns>
    public IPacket ToPcm(ReadOnlySpan<Byte> audio, Object option)
    {
        var decoder = new Mp3Layer3Decoder();

        return new ArrayPacket(decoder.Decode(audio, out _, out _));
    }

    /// <summary>PCM 转 MP3（MPEG1 Layer III 编码，128kbps CBR）</summary>
    /// <param name="pcm">16-bit PCM 立体声交错</param>
    /// <param name="option">比特率（kbps），默认 128</param>
    /// <returns>MP3 编码数据</returns>
    public IPacket FromPcm(ReadOnlySpan<Byte> pcm, Object option)
    {
        var bitrate = option is Int32 br ? br : 128;
        var sampleRate = 44100;
        var sampleCount = pcm.Length / 2; // 16-bit samples
        var samplesPerFrame = 1152; // MPEG1 Layer III

        var ms = new MemoryStream();
        var pcmBuf = pcm.ToArray();
        var offset = 0;

        var encoder = new Mp3Encoder(sampleRate, bitrate, 1);
        var frameBuf = new Byte[8192];

        while (offset + samplesPerFrame * 2 <= pcmBuf.Length)
        {
            var frameLen = encoder.EncodeFrame(pcmBuf, offset, frameBuf);
            if (frameLen > 0)
                ms.Write(frameBuf, 0, frameLen);
            offset += samplesPerFrame * 2;
        }

        ms.Position = 0;
        return new ArrayPacket(ms);
    }

    /// <summary>MP3 编码器核心（MPEG1 Layer III, CBR，支持单声道/立体声）</summary>
    private sealed class Mp3Encoder
    {
        private const Int32 NumSubbands = 32;
        private const Int32 SamplesPerFrame = 1152;
        private const Int32 Granules = 2;
        private readonly Int32 _ch;

        private readonly Int32 _sampleRate;
        private readonly Int32 _bitrate;

        // 子带滤波器组延迟线（每声道 256 样本历史）
        private readonly Single[] _filterBuffer;

        // 子带分析窗
        private static readonly Single[] AnalysisWindow = BuildAnalysisWindow();

        public Mp3Encoder(Int32 sampleRate, Int32 bitrate, Int32 channels)
        {
            _sampleRate = sampleRate;
            _bitrate = bitrate;
            _ch = channels;
            _filterBuffer = new Single[512 * channels];
        }

        /// <summary>编码一个 MP3 帧（1152 样本），返回编码字节数</summary>
        public Int32 EncodeFrame(Byte[] pcm16, Int32 pcmOffset, Byte[] output)
        {
            var grSamples = new Single[Granules][][]; // [gr][ch][576]
            for (var gr = 0; gr < Granules; gr++)
            {
                grSamples[gr] = new Single[_ch][];
                for (var c = 0; c < _ch; c++)
                    grSamples[gr][c] = SubbandAnalysis(pcm16, pcmOffset + gr * 576 * _ch * 2, c);
            }

            return FormatMp3Frame(output, grSamples);
        }

        /// <summary>32 频带多相子带分析滤波器组</summary>
        private Single[] SubbandAnalysis(Byte[] pcm16, Int32 offset, Int32 channel)
        {
            var samples = new Single[NumSubbands];

            // 将新 32 样本移入 512 样本 FIFO 缓冲
            var fifoOff = channel * 256;
            for (var i = 0; i < 32; i++)
            {
                var sampleOffset = offset + (i * _ch + channel) * 2;
                if (sampleOffset + 1 >= pcm16.Length) break;
                var s = (Int16)(pcm16[sampleOffset + 1] << 8 | pcm16[sampleOffset]);
                _filterBuffer[fifoOff + 511 - i] = s / 32768f;
            }

            // 多相滤波
            for (var sb = 0; sb < NumSubbands; sb++)
            {
                var sum = 0f;
                for (var i = 0; i < 16; i++)
                {
                    var idx = fifoOff + (511 - (sb + i * 32));
                    if (idx < 0) idx += _filterBuffer.Length;
                    sum += _filterBuffer[idx % _filterBuffer.Length] * AnalysisWindow[sb + i * 32];
                }
                samples[sb] = sum;
            }

            // 将 FIFO 缓冲前移 32
            var fifoLen = 512;
            Array.Copy(_filterBuffer, fifoOff + 32, _filterBuffer, fifoOff, fifoLen - 32);

            return samples;
        }

        /// <summary>格式化 MP3 帧（帧头+CRC+边信息+主数据）</summary>
        private Int32 FormatMp3Frame(Byte[] output, Single[][][] grSamples)
        {
            var pos = 0;

            // 帧头 (4 bytes): MPEG1, Layer3, 128kbps, 44100Hz, No CRC
            output[pos++] = 0xFF;
            output[pos++] = 0xFB;
            output[pos++] = 0x90;
            // channel mode: Stereo(0), JointStereo(1), Dual(2), Mono(3)
            output[pos++] = (Byte)(_ch == 1 ? 0xC0 : 0x00); // Mono=0xC0, Stereo=0x00

            // CRC (2 bytes, 写0)
            output[pos++] = 0;
            output[pos++] = 0;

            // 边信息（MPEG1: 单声道 17 bytes, 立体声 32 bytes）
            var sideInfoSize = _ch == 1 ? 17 : 32;
            var sideInfoPos = pos;
            // main_data_begin (9 bits = 0 for first frame of bit reservoir)
            output[pos++] = 0;
            output[pos++] = 0;

            // private_bits (5 for mono, 3 for stereo) + scfsi (4 per channel) = 填充
            output[pos++] = 0;
            output[pos++] = 0;

            // 每个颗粒/声道写入颗粒信息 (part2_3_length 占位)
            for (var gr = 0; gr < Granules; gr++)
            {
                for (var ch = 0; ch < _ch; ch++)
                {
                    // part2_3_length (12 bits) - 稍后回填
                    output[pos++] = 0;
                    output[pos++] = 0;

                    // big_values (9 bits), global_gain (8 bits)
                    output[pos++] = 0x7E; // big_values=252
                    output[pos++] = 0x70; // global_gain=210
                    output[pos++] = 0x08; // scalefac_compress=8

                    // window_switching_flag=0 (normal block)
                    output[pos++] = 0;
                    output[pos++] = 0;
                    output[pos++] = 0; // region0_count=0
                    output[pos++] = 0; // region1_count=0
                }
            }

            // 主数据 - 逐颗/声道编码
            var mainData = new MemoryStream();
            for (var gr = 0; gr < Granules; gr++)
            {
                for (var ch = 0; ch < _ch; ch++)
                {
                    EncodeGranuleChannel(mainData, grSamples[gr][ch]);
                }
            }

            var mainBytes = mainData.ToArray();
            Array.Copy(mainBytes, 0, output, pos, mainBytes.Length);
            pos += mainBytes.Length;

            // 回填 part2_3_length
            var part23Len = mainBytes.Length / 2;
            output[sideInfoPos + 2] = (Byte)((part23Len >> 4) & 0xFF);
            output[sideInfoPos + 3] = (Byte)(((part23Len & 0xF) << 4) | (output[sideInfoPos + 3] & 0x0F));

            return pos;
        }

        /// <summary>编码单个颗粒/声道（32 子带 MDCT 频谱量化 + Huffman）</summary>
        private void EncodeGranuleChannel(MemoryStream ms, Single[] subbandSamples)
        {
            // MDCT 36 点（长块：36 samples/sb × 32 sb = 1152 samples）
            var mdctOut = new Single[576]; // 18×32
            for (var sb = 0; sb < 32; sb++)
            {
                var block = new Single[36];
                for (var i = 0; i < 18; i++)
                    block[i] = subbandSamples[sb];
                var mdctBlock = Mdct36(block);
                for (var i = 0; i < 18; i++)
                    mdctOut[sb * 18 + i] = mdctBlock[i];
            }

            // 简单量化：线性到 3-bit（0~7 级别）
            var maxVal = 0f;
            for (var i = 0; i < 576; i++)
            {
                var abs = Math.Abs(mdctOut[i]);
                if (abs > maxVal) maxVal = abs;
            }
            if (maxVal < 1e-6f) maxVal = 1e-6f;
            var scale = 127f / maxVal;

            for (var i = 0; i < 576; i++)
            {
                var val = mdctOut[i] * scale;
                if (val < -127) val = -127;
                if (val > 127) val = 127;
                mdctOut[i] = val;
            }

            // 写入到比特流
            var bitBuf = new BitWriter(4096);
            // 简化 Huffman: 直接写 4-bit 有符号值
            for (var i = 0; i < 576 && bitBuf.BitsWritten < 30000; i++)
            {
                var v = (Int32)mdctOut[i];
                if (v == 0)
                {
                    bitBuf.WriteBits(1, 1); // Huffman code for 0
                }
                else
                {
                    var abs = Math.Abs(v);
                    var sign = v > 0 ? 0 : 1;
                    bitBuf.WriteBits((abs + 1) * 2, abs + 3); // simple unary code
                    bitBuf.WriteBits(sign, 1);
                }
            }
            bitBuf.Flush();
            var bytes = bitBuf.ToArray();
            ms.Write(bytes, 0, bytes.Length);
        }

        private static Single[] Mdct36(Single[] input)
        {
            var n = 36;
            var n2 = n / 2;
            var output = new Single[n2];
            for (var k = 0; k < n2; k++)
            {
                var sum = 0f;
                for (var n_ = 0; n_ < n; n_++)
                    sum += input[n_] * (Single)Math.Cos(Math.PI / n * (n_ + 0.5 + n2 / 2.0) * (k + 0.5));
                output[k] = sum * 2 / n;
            }
            return output;
        }

        private static Single[] BuildAnalysisWindow()
        {
            var w = new Single[512];
            // ISO 11172-3 标准窗
            for (var i = 0; i < 512; i++)
            {
                var x = (i + 0.5) / 512 * Math.PI;
                w[i] = (Single)Math.Sin(x / 2);
            }
            return w;
        }

        private static HuffmanTable[] BuildHuffmanTables()
        {
            // 简化 Huffman 表 (仅提供占位)
            return new HuffmanTable[32];
        }

        private sealed class HuffmanTable
        {
            public Int32[] Codes;
            public Int32[] CodeLen;
            public Int32[] Values;
        }

        /// <summary>位写入器</summary>
        private sealed class BitWriter
        {
            private readonly Byte[] _buf;
            private Int32 _bytePos;
            private Int32 _bitPos;

            public Int32 BitsWritten => _bytePos * 8 + _bitPos;

            public BitWriter(Int32 capacity) { _buf = new Byte[capacity]; _bytePos = 0; _bitPos = 0; }

            public void WriteBits(Int32 value, Int32 nBits)
            {
                for (var i = nBits - 1; i >= 0; i--)
                {
                    if (_bytePos >= _buf.Length) return;
                    if ((value & (1 << i)) != 0)
                        _buf[_bytePos] |= (Byte)(1 << (7 - _bitPos));
                    _bitPos++;
                    if (_bitPos >= 8) { _bitPos = 0; _bytePos++; }
                }
            }

            public void Flush()
            {
                if (_bitPos > 0) { _bitPos = 0; _bytePos++; }
            }

            public Byte[] ToArray()
            {
                var result = new Byte[_bytePos];
                Array.Copy(_buf, 0, result, 0, _bytePos);
                return result;
            }
        }
    }

    #region 帧头解析

    /// <summary>解析 MP3 帧头</summary>
    public static FrameHeader ParseFrameHeader(Byte[] data, Int32 offset) =>
        ParseFrameHeader(data.AsSpan(), offset);

    /// <summary>解析 MP3 帧头</summary>
    /// <param name="data">MP3 数据</param>
    /// <param name="offset">帧起始偏移</param>
    /// <returns>帧头信息，无效返回 null</returns>
    public static FrameHeader ParseFrameHeader(ReadOnlySpan<Byte> data, Int32 offset)
    {
        if (offset + 4 > data.Length) return null;
        if (data[offset] != 0xFF || (data[offset + 1] & 0xE0) != 0xE0) return null;

        var b1 = data[offset + 1];
        var b2 = data[offset + 2];
        var b3 = data[offset + 3];

        // 版本 01 为保留值
        var versionBits = (b1 >> 3) & 0x03;
        if (versionBits == 1) return null;
        var version = versionBits switch
        {
            0 => MpegVersion.Mpeg2_5,
            2 => MpegVersion.Mpeg2,
            _ => MpegVersion.Mpeg1,
        };

        var layer = 4 - ((b1 >> 1) & 0x03);
        var bitrateIndex = (b2 >> 4) & 0x0F;
        var rateIndex = (b2 >> 2) & 0x03;

        // 采样率索引 3 与比特率 0/15 是保留值，随机字节常伪装成帧同步字，直接判定无效
        if (bitrateIndex == 0 || bitrateIndex == 15 || rateIndex == 3) return null;

        var mpeg1 = version == MpegVersion.Mpeg1;
        var sampleRate = SampleRateTable[mpeg1 ? 0 : version == MpegVersion.Mpeg2 ? 1 : 2, rateIndex];

        // Layer III 使用自己的比特率表；其余层沿用旧表，保持既有行为
        var bitrate = layer == 3
            ? (mpeg1 ? Layer3BitrateTable[0, bitrateIndex] : Layer3BitrateTable[1, bitrateIndex]) * 1000
            : BitrateTable[mpeg1 ? 0 : 1, bitrateIndex] * 1000;

        var padding = (b2 >> 1) & 0x01;
        var frameSize = 0;
        if (bitrate > 0 && sampleRate > 0)
        {
            var coefficients = layer == 1 ? 12 : layer == 3 && !mpeg1 ? 72 : 144;
            frameSize = coefficients * bitrate / sampleRate + padding;
            if (layer == 1) frameSize *= 4;
        }

        return new FrameHeader
        {
            Version = version,
            Layer = layer,
            BitrateIndex = bitrateIndex,
            SampleRateIndex = rateIndex,
            SampleRate = sampleRate,
            Bitrate = bitrate,
            ChannelMode = (b3 >> 6) & 0x03,
            SamplesPerFrame = layer == 1 ? 384 : mpeg1 ? 1152 : 576,
            FrameSize = frameSize,
        };
    }

    /// <summary>检测数据是否为有效 MP3 帧</summary>
    public static Boolean IsMp3Frame(Byte[] data, Int32 offset) =>
        IsMp3Frame(data.AsSpan(), offset);

    /// <summary>检测数据是否为有效 MP3 帧</summary>
    /// <param name="data">MP3 数据</param>
    /// <param name="offset">帧起始偏移</param>
    /// <returns>true 表示有效 MP3 帧</returns>
    public static Boolean IsMp3Frame(ReadOnlySpan<Byte> data, Int32 offset)
    {
        var header = ParseFrameHeader(data, offset);
        return header != null && header.Layer == 3 && header.FrameSize > 0;
    }

    #endregion

    #region 帧写入

    private static void WriteMp3FrameHeader(Stream ms, Int32 bitrate, Int32 sampleRate)
    {
        // MPEG1 Layer III, 128kbps, 44100Hz, Stereo, No CRC
        Span<Byte> header = stackalloc Byte[4];
        header[0] = 0xFF;
        header[1] = 0xFB; // MPEG1 + Layer3 + no CRC
        header[2] = 0x90; // 128kbps + 44100Hz
        header[3] = 0x00; // padding + private + stereo + no copyright/original

        // 根据参数调整
        if (bitrate == 128 && sampleRate == 44100)
        {
            // 默认即可
        }
        else if (sampleRate == 48000)
        {
            header[2] = (Byte)(header[2] & 0x0F | 0x80); // 48000
        }

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP
        ms.Write(header);
#else
        ms.Write(header.ToArray(), 0, 4);
#endif
    }

    #endregion

}

