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

    /// <summary>PCM 转 MP3（MPEG Layer III 编码，CBR）</summary>
    /// <param name="pcm">16 位小端交错 PCM</param>
    /// <param name="option"><see cref="Mp3EncoderOptions"/> 编码参数；也可直接传 Int32 比特率（kbps），其余参数取默认值（44.1kHz 单声道）</param>
    /// <returns>MP3 编码数据</returns>
    /// <remarks>
    /// 采样率由 <see cref="Mp3EncoderOptions.SampleRate"/> 决定，兼容 8k～48k 共 9 档标准采样率；
    /// 输入样本不足一帧时补零成帧，保证输出始终是完整的 MPEG 音频帧序列。
    /// </remarks>
    public IPacket FromPcm(ReadOnlySpan<Byte> pcm, Object option)
    {
        var options = option as Mp3EncoderOptions;
        if (options == null)
        {
            options = new Mp3EncoderOptions();
            if (option is Int32 bitrate) options.Bitrate = bitrate;
        }

        var encoder = new Mp3Layer3Encoder(options.SampleRate, options.Channels, options.Bitrate);
        var data = pcm.ToArray();
        var frameBytes = encoder.FrameSamples * options.Channels * 2;
        var frameBuf = new Byte[encoder.MaxFrameBytes];
        var ms = new MemoryStream(data.Length + 1024);

        var offset = 0;
        while (offset + frameBytes <= data.Length)
        {
            var len = encoder.EncodeFrame(data, offset, frameBuf);
            ms.Write(frameBuf, 0, len);
            offset += frameBytes;
        }

        if (offset < data.Length)
        {
            var tail = new Byte[frameBytes];
            Array.Copy(data, offset, tail, 0, data.Length - offset);
            var len = encoder.EncodeFrame(tail, 0, frameBuf);
            ms.Write(frameBuf, 0, len);
        }

        return new ArrayPacket(ms.ToArray());
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

