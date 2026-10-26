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

        /// <summary>模式扩展（联合立体声：bit1 = M/S，bit0 = 强度立体声）</summary>
        public Int32 ModeExtension { get; set; }

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

    /// <summary>MP3 数据转 PCM，并按信息帧中的延迟/填充做 gapless 裁剪</summary>
    /// <param name="audio">MP3 编码数据</param>
    /// <param name="sampleRate">输出采样率</param>
    /// <param name="channels">输出声道数</param>
    /// <returns>16 位小端交错 PCM，长度等于原始样本数（信息帧标注可用时）</returns>
    /// <remarks>
    /// 码流首个信息帧（Xing/Info）若带 LAME 兼容标签，则跳过编码器延迟与尾部填充，
    /// 使连续多段解码后可以直接拼接播放，不出现缝隙或重复样本。缺少标签时退化为普通解码。
    /// </remarks>
    public Byte[] ToPcmGapless(ReadOnlySpan<Byte> audio, out Int32 sampleRate, out Int32 channels)
    {
        var decoder = new Mp3Layer3Decoder();
        var pcm = decoder.Decode(audio, out sampleRate, out channels);

        var info = Mp3GaplessInfo.TryParse(audio, out var infoSamples);
        if (info == null) return pcm;

        var frameBytes = channels * 2;
        var skipHead = (infoSamples + info.EncoderDelay) * frameBytes;
        var skipTail = info.EncoderPadding * frameBytes;
        var length = pcm.Length - skipHead - skipTail;
        if (length <= 0 || length > pcm.Length) return pcm;

        var result = new Byte[length];
        Array.Copy(pcm, skipHead, result, 0, length);

        return result;
    }

    /// <summary>PCM 转 MP3（MPEG Layer III 编码，支持 CBR/VBR/ABR）</summary>
    /// <param name="pcm">16 位小端交错 PCM</param>
    /// <param name="option"><see cref="Mp3EncoderOptions"/> 编码参数；也可直接传 Int32 比特率（kbps），其余参数取默认值（44.1kHz 单声道）</param>
    /// <returns>MP3 编码数据</returns>
    /// <remarks>
    /// 采样率由 <see cref="Mp3EncoderOptions.SampleRate"/> 决定，兼容 8k～48k 共 9 档标准采样率；
    /// 输入样本不足一帧时补零成帧，保证输出始终是完整的 MPEG 音频帧序列。
    /// <para>VBR/ABR 模式下逐帧按信号复杂度选择码率档位，并在码流开头写入 Xing 信息帧（含总帧数、总字节数与 TOC），
    /// 播放器无需全扫描即可得到时长与 seek 位置。</para>
    /// </remarks>
    public IPacket FromPcm(ReadOnlySpan<Byte> pcm, Object option)
    {
        var options = option as Mp3EncoderOptions;
        if (options == null)
        {
            options = new Mp3EncoderOptions();
            if (option is Int32 bitrate) options.Bitrate = bitrate;
        }

        var encoder = new Mp3Layer3Encoder(options.SampleRate, options.Channels, options.Bitrate) { UseCrc = options.UseCrc };
        var data = pcm.ToArray();
        var frameBytes = encoder.FrameSamples * options.Channels * 2;
        var frameBuf = new Byte[encoder.MaxFrameBytes];

        var vbr = options.Mode != Mp3RateMode.Cbr;
        var writeInfo = vbr || options.WriteInfoFrame;
        var sizes = writeInfo ? new List<Int32>() : null;
        var frames = new List<EncodedFrame>();
        var mpeg1 = options.SampleRate >= 32000;
        var sideInfoBytes = mpeg1 ? (options.Channels == 1 ? 17 : 32) : (options.Channels == 1 ? 9 : 17);
        var mainOffset = 4 + (options.UseCrc ? 2 : 0) + sideInfoBytes;
        var reservoir = 0;

        // 末尾补零让帧数覆盖「原始样本 + 编码器延迟」，gapless 尾部填充才不为负
        var delay = writeInfo ? encoder.EncoderDelay : 0;
        if (delay > 0)
        {
            var extra = delay * options.Channels * 2;
            Array.Resize(ref data, data.Length + extra);
        }

        // ABR 的每帧目标比特数与累计漂移
        var targetBits = (Double)options.Bitrate * 1000 * encoder.FrameSamples / options.SampleRate;
        var drift = 0.0;
        var demandScale = ReferenceScale(options.VbrQuality);
        var minRate = options.MinBitrate * 1000;
        var maxRate = options.MaxBitrate * 1000;

        Int32 EncodeOne(Byte[] source, Int32 sourceOffset)
        {
            encoder.ReservoirBytes = reservoir;
            var len = vbr
                ? EncodeVariableFrame(encoder, source, sourceOffset, frameBuf, options.Mode, demandScale, targetBits, minRate, maxRate, reservoir, ref drift, out _)
                : encoder.EncodeFrame(source, sourceOffset, frameBuf);

            var capacity = len - mainOffset;
            if (capacity < 0) capacity = 0;
            var mainBits = encoder.LastMainDataBits;
            var mainBytes = (mainBits + 7) >> 3;
            if (mainBytes > capacity + reservoir) mainBytes = capacity + reservoir;
            if (mainBytes < 0) mainBytes = 0;

            var header = new Byte[mainOffset];
            Array.Copy(frameBuf, header, Math.Min(mainOffset, len));
            var mainData = new Byte[mainBytes];
            if (mainBytes > 0) encoder.CopyLastMainData(mainData, 0, mainBytes);

            frames.Add(new EncodedFrame
            {
                Header = header,
                MainData = mainData,
                Capacity = capacity,
                MainDataBegin = Math.Max(0, mainBytes - capacity),
            });
            sizes?.Add(len);

            // 只有本帧末尾的空闲区能被下一帧向前引用；更早的空闲区已被本帧数据隔断
            reservoir = Math.Min(encoder.MaxReservoirBytes, Math.Max(0, capacity - mainBytes));
            return len;
        }

        var offset = 0;
        while (offset + frameBytes <= data.Length)
        {
            EncodeOne(data, offset);
            offset += frameBytes;
        }

        if (offset < data.Length)
        {
            var tail = new Byte[frameBytes];
            Array.Copy(data, offset, tail, 0, data.Length - offset);
            EncodeOne(tail, 0);
        }

        var audio = BuildReservoirFrames(frames, mpeg1, sideInfoBytes, options.UseCrc);
        if (!writeInfo || sizes == null || sizes.Count == 0) return new ArrayPacket(audio);

        // 前置 Xing/Info 信息帧，附带 gapless 裁剪信息
        var quality = options.Mode == Mp3RateMode.Vbr ? Clamp(options.VbrQuality, 0, 9) * 10 : 100;
        var totalSamples = data.Length / 2 / options.Channels - delay;
        var padding = sizes.Count * encoder.FrameSamples - totalSamples - delay;
        if (padding < 0) padding = 0;

        var magic = Mp3XingHeader.Build(options.SampleRate, options.Channels, sizes.Count, sizes, vbr, quality, delay, padding);
        var output = new MemoryStream(magic.Length + audio.Length);
        output.Write(magic, 0, magic.Length);
        output.Write(audio, 0, audio.Length);

        return new ArrayPacket(output.ToArray());
    }

    /// <summary>已编码帧，主数据与帧头分离以便按比特池重排</summary>
    private sealed class EncodedFrame
    {
        /// <summary>帧头、可选 CRC 与边信息</summary>
        public Byte[] Header { get; set; }

        /// <summary>本帧主数据</summary>
        public Byte[] MainData { get; set; }

        /// <summary>本帧主数据区容量（字节）</summary>
        public Int32 Capacity { get; set; }

        /// <summary>主数据起始位置相对本帧主数据区向前回溯的字节数</summary>
        public Int32 MainDataBegin { get; set; }
    }

    /// <summary>按比特池把各帧主数据重排为连续码流</summary>
    /// <param name="frames">已编码帧</param>
    /// <param name="mpeg1">是否为 MPEG-1（main_data_begin 为 9 位）</param>
    /// <param name="sideInfoBytes">边信息字节数</param>
    /// <param name="useCrc">是否带 CRC</param>
    /// <returns>重排后的完整 MP3 帧序列</returns>
    private static Byte[] BuildReservoirFrames(List<EncodedFrame> frames, Boolean mpeg1, Int32 sideInfoBytes, Boolean useCrc)
    {
        if (frames.Count == 0) return [];

        var total = 0;
        foreach (var frame in frames) total += frame.Capacity;

        // 先把所有主数据按比特池规则铺到连续区域：当前帧可向前借用上一帧未用完的尾部
        var stream = new Byte[total];
        var capacityBefore = 0;
        foreach (var frame in frames)
        {
            var begin = Math.Min(frame.MainDataBegin, capacityBefore);
            frame.MainDataBegin = begin;
            var start = capacityBefore - begin;
            if (frame.MainData.Length > 0)
            {
                var count = Math.Min(frame.MainData.Length, stream.Length - start);
                Array.Copy(frame.MainData, 0, stream, start, count);
            }
            capacityBefore += frame.Capacity;
        }

        var output = new MemoryStream(total + frames.Count * 8);
        capacityBefore = 0;
        foreach (var frame in frames)
        {
            var sideOffset = frame.Header.Length - sideInfoBytes;
            if (mpeg1)
            {
                // 9 位 main_data_begin：边信息前 8 位 + 第 9 位在下一字节最高位
                var value = frame.MainDataBegin;
                frame.Header[sideOffset] = (Byte)(value >> 1);
                frame.Header[sideOffset + 1] = (Byte)((frame.Header[sideOffset + 1] & 0x7F) | ((value & 1) << 7));
            }
            else
            {
                frame.Header[sideOffset] = (Byte)frame.MainDataBegin;
            }

            if (useCrc)
            {
                var crc = ComputeCrc(frame.Header, sideOffset, sideInfoBytes);
                frame.Header[4] = (Byte)(crc >> 8);
                frame.Header[5] = (Byte)(crc & 0xFF);
            }

            output.Write(frame.Header, 0, frame.Header.Length);
            output.Write(stream, capacityBefore, frame.Capacity);
            capacityBefore += frame.Capacity;
        }

        return output.ToArray();
    }

    /// <summary>计算 MP3 帧 CRC-16（覆盖帧头第 3~4 字节与边信息）</summary>
    /// <param name="frame">帧数据</param>
    /// <param name="sideOffset">边信息起始偏移</param>
    /// <param name="sideBytes">边信息字节数</param>
    /// <returns>CRC-16 值</returns>
    private static Int32 ComputeCrc(Byte[] frame, Int32 sideOffset, Int32 sideBytes)
    {
        var crc = 0xFFFF;
        for (var b = 2; b < 4; b++)
        {
            for (var bit = 7; bit >= 0; bit--) crc = UpdateCrc(crc, (frame[b] & (1 << bit)) != 0);
        }
        for (var b = 0; b < sideBytes; b++)
        {
            for (var bit = 7; bit >= 0; bit--) crc = UpdateCrc(crc, (frame[sideOffset + b] & (1 << bit)) != 0);
        }

        return crc;
    }

    private static Int32 UpdateCrc(Int32 crc, Boolean bit)
    {
        crc ^= bit ? 0x8000 : 0;
        for (var i = 0; i < 8; i++)
        {
            crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x8005) & 0xFFFF : (crc << 1) & 0xFFFF;
        }

        return crc;
    }

    /// <summary>编码一帧并返回本帧码率</summary>
    private static Int32 EncodeVariableFrame(Mp3Layer3Encoder encoder, Byte[] pcm, Int32 offset, Byte[] frameBuf, Mp3RateMode mode, Double demandScale, Double targetBits, Int32 minRate, Int32 maxRate, Int32 reservoirBytes, ref Double drift, out Int32 bitrate)
    {
        var demand = encoder.PrepareFrame(pcm, offset, true, (Single)demandScale);

        var desired = mode == Mp3RateMode.Vbr
            ? demand
            : demand * targetBits / (drift > 0 ? drift : demand);
        if (desired < 0) desired = 0;

        encoder.ReservoirBytes = reservoirBytes;
        encoder.Bitrate = encoder.SelectBitrate((Int32)desired, minRate, maxRate, reservoirBytes);
        bitrate = encoder.Bitrate;

        var len = encoder.EncodePrepared(frameBuf);

        // 主数据超容量会被截断：换更高码率重编（分析已完成，重编只重做量化与写位）
        for (var i = 0; i < 4 && encoder.LastFrameTruncated; i++)
        {
            var next = encoder.NextBitrate(encoder.Bitrate);
            if (next == encoder.Bitrate) break;
            if (maxRate > 0 && next > maxRate) break;

            encoder.Bitrate = next;
            bitrate = next;
            len = encoder.EncodePrepared(frameBuf);
        }

        // ABR 用需求移动平均把平均码率拉向目标：drift 存放需求均值
        if (mode == Mp3RateMode.Abr)
        {
            drift = drift > 0 ? drift * 0.9 + demand * 0.1 : demand;
        }

        return len;
    }

    /// <summary>VBR 质量档位对应的参考量化尺度（相对帧峰值的量化步长比例），档位越低尺度越小（质量越高）</summary>
    private static Double ReferenceScale(Int32 quality)
    {
        // 0~9 档按等比递增，约每档 1.8 倍；q2 对 1 kHz 正弦约 128 kbps
        Double[] scales = [0.00005, 0.0001, 0.0002, 0.00035, 0.0007, 0.0015, 0.004, 0.01, 0.025, 0.06];

        return scales[Clamp(quality, 0, 9)];
    }

    private static Int32 Clamp(Int32 value, Int32 min, Int32 max)
    {
        if (value < min) return min;
        if (value > max) return max;

        return value;
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
            ModeExtension = (b3 >> 4) & 0x03,
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



