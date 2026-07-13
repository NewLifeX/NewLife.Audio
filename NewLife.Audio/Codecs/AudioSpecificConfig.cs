using NewLife.Audio.Codecs;

namespace NewLife.Audio.Codecs;

/// <summary>AAC AudioSpecificConfig (ISO 14496-3)</summary>
/// <remarks>
/// 表示 AAC 编解码器需要的音频特定配置，用于 MP4/FLV 等容器中的序列头。
/// 支持基础 2 字节 AAC-LC 配置、逃逸采样率（24bit）、SBR/PS 扩展。
/// 
/// 布局（基础 16 位）：
/// [audioObjectType:5][samplingFrequencyIndex:4][channelConfiguration:4][frameLengthFlag:1][dependsOnCoreCoder:1][extensionFlag:1]
/// 
/// 参考：ISO 14496-3:2009, 1.6.2.1 AudioSpecificConfig
/// </remarks>
public sealed class AudioSpecificConfig
{
    #region 属性

    /// <summary>AAC 音频对象类型（1=Main, 2=LC, 5=SBR, 29=PS）</summary>
    public Int32 AudioObjectType { get; set; }

    /// <summary>采样率索引（0-12, 15=逃逸）</summary>
    public Int32 SamplingFrequencyIndex { get; set; }

    /// <summary>采样率（Hz）。仅当 <see cref="SamplingFrequencyIndex"/> 为 15（逃逸）时从 24bit 字段读取，否则从索引表获取</summary>
    public Int32 SamplingFrequency { get; set; }

    /// <summary>声道配置（1=单声道, 2=立体声, 3-7 多声道）</summary>
    public Int32 ChannelConfiguration { get; set; }

    /// <summary>帧长度标志。true=960/帧, false=1024/帧</summary>
    public Boolean FrameLengthFlag { get; set; }

    /// <summary>是否依赖核心编码器</summary>
    public Boolean DependsOnCoreCoder { get; set; }

    /// <summary>核心编码器延迟（14 位），仅当 <see cref="DependsOnCoreCoder"/> 为 true 时有效</summary>
    public Int32 CoreCoderDelay { get; set; }

    /// <summary>是否存在扩展（SBR/PS）</summary>
    public Boolean ExtensionFlag { get; set; }

    // -- 扩展字段 --

    /// <summary>扩展音频对象类型（5=SBR, 29=PS）</summary>
    public Int32 ExtensionAudioObjectType { get; set; }

    /// <summary>SBR 是否存在</summary>
    public Boolean SbrPresentFlag { get; set; }

    /// <summary>扩展采样率索引</summary>
    public Int32 ExtensionSamplingFrequencyIndex { get; set; }

    /// <summary>扩展采样率（Hz），逃逸时使用</summary>
    public Int32 ExtensionSamplingFrequency { get; set; }

    /// <summary>参数立体声（PS）是否存在</summary>
    public Boolean PsPresentFlag { get; set; }

    /// <summary>有效采样率（Hz）。优先使用逃逸字段，否则查索引表</summary>
    public Int32 EffectiveSampleRate => SamplingFrequencyIndex < 15 ? GetSampleRate(SamplingFrequencyIndex) : SamplingFrequency;

    /// <summary>有效扩展采样率（Hz）</summary>
    public Int32 EffectiveExtensionSampleRate => ExtensionSamplingFrequencyIndex < 15 ? GetSampleRate(ExtensionSamplingFrequencyIndex) : ExtensionSamplingFrequency;

    #endregion

    #region 静态工厂

    /// <summary>从基本参数创建 AudioSpecificConfig（AAC-LC）</summary>
    /// <param name="audioObjectType">音频对象类型（2=AAC-LC）</param>
    /// <param name="sampleRate">采样率（Hz）</param>
    /// <param name="channels">声道数</param>
    /// <returns>AudioSpecificConfig 实例</returns>
    public static AudioSpecificConfig FromParameters(Int32 audioObjectType, Int32 sampleRate, Int32 channels)
    {
        var sfi = GetSamplingFrequencyIndex(sampleRate);

        return new AudioSpecificConfig
        {
            AudioObjectType = audioObjectType,
            SamplingFrequencyIndex = sfi,
            SamplingFrequency = sfi == 15 ? sampleRate : GetSampleRate(sfi),
            ChannelConfiguration = channels,
            FrameLengthFlag = false,
            DependsOnCoreCoder = false,
            CoreCoderDelay = 0,
            ExtensionFlag = false,
        };
    }

    /// <summary>从 ADTS 帧头信息创建 AudioSpecificConfig</summary>
    /// <param name="adts">ADTS 帧头</param>
    /// <returns>AudioSpecificConfig 实例</returns>
    public static AudioSpecificConfig FromAdts(AacCodec.AdtsInfo adts)
    {
        if (adts == null) throw new ArgumentNullException(nameof(adts));

        // ADTS profile 是 0-based（0=Main,1=LC,2=SSR,3=LTP）
        // AOT 是 1-based（1=Main,2=LC,3=SSR,4=LTP）
        var aot = adts.Profile + 1;

        return new AudioSpecificConfig
        {
            AudioObjectType = aot,
            SamplingFrequencyIndex = adts.SampleRateIndex,
            SamplingFrequency = adts.SampleRate,
            ChannelConfiguration = adts.Channels,
            FrameLengthFlag = adts.SamplesPerFrame != 1024,
            DependsOnCoreCoder = false,
            CoreCoderDelay = 0,
            ExtensionFlag = false,
        };
    }

    /// <summary>从字节数据解析 AudioSpecificConfig</summary>
    /// <param name="data">AudioSpecificConfig 字节数据</param>
    /// <returns>AudioSpecificConfig 实例</returns>
    public static AudioSpecificConfig Parse(ReadOnlySpan<Byte> data)
    {
        if (data.Length < 2) throw new ArgumentException("数据长度不足 2 字节", nameof(data));

        var reader = new BitReader(data);

        var config = new AudioSpecificConfig
        {
            AudioObjectType = reader.Read(5),
            SamplingFrequencyIndex = reader.Read(4),
            ChannelConfiguration = reader.Read(4),
            FrameLengthFlag = reader.Read(1) == 1,
            DependsOnCoreCoder = reader.Read(1) == 1,
            ExtensionFlag = reader.Read(1) == 1,
        };

        // 依赖核心编码器延迟
        if (config.DependsOnCoreCoder)
            config.CoreCoderDelay = reader.Read(14);

        // 逃逸采样率
        if (config.SamplingFrequencyIndex == 15)
            config.SamplingFrequency = reader.Read(24);
        else
            config.SamplingFrequency = GetSampleRate(config.SamplingFrequencyIndex);

        // 扩展
        if (config.ExtensionFlag && reader.RemainingBits > 0)
        {
            ParseExtension(config, reader);
        }

        return config;
    }

    /// <summary>解析扩展部分</summary>
    private static void ParseExtension(AudioSpecificConfig config, BitReader reader)
    {
        // 扩展 AOT（5 位，值 1-31）
        config.ExtensionAudioObjectType = reader.Read(5);

        if (config.ExtensionAudioObjectType == 5) // SBR
        {
            config.SbrPresentFlag = reader.Read(1) == 1;
            if (config.SbrPresentFlag && reader.RemainingBits > 0)
            {
                config.ExtensionSamplingFrequencyIndex = reader.Read(4);
                if (config.ExtensionSamplingFrequencyIndex == 15 && reader.RemainingBits >= 24)
                    config.ExtensionSamplingFrequency = reader.Read(24);
                else
                    config.ExtensionSamplingFrequency = GetSampleRate(config.ExtensionSamplingFrequencyIndex);

                // PS 作为 SBR 子扩展
                if (reader.RemainingBits > 0)
                    config.PsPresentFlag = reader.Read(1) == 1;
            }
        }
        else if (config.ExtensionAudioObjectType == 29) // PS（独立）
        {
            if (reader.RemainingBits > 0)
                config.PsPresentFlag = reader.Read(1) == 1;
        }
    }

    #endregion

    #region 序列化

    /// <summary>序列化为字节数组（自动计算扩展长度）</summary>
    /// <returns>AudioSpecificConfig 字节数据</returns>
    public Byte[] ToByteArray()
    {
        var writer = new BitWriter();

        // 基础配置（16 位）
        writer.Write(AudioObjectType, 5);
        writer.Write(SamplingFrequencyIndex, 4);
        writer.Write(ChannelConfiguration, 4);
        writer.Write(FrameLengthFlag ? 1 : 0, 1);
        writer.Write(DependsOnCoreCoder ? 1 : 0, 1);
        writer.Write(ExtensionFlag ? 1 : 0, 1);

        // 核心编码器延迟
        if (DependsOnCoreCoder)
            writer.Write(CoreCoderDelay, 14);

        // 逃逸采样率
        if (SamplingFrequencyIndex == 15)
            writer.Write(SamplingFrequency, 24);

        // 扩展
        if (ExtensionFlag && ExtensionAudioObjectType > 0)
            WriteExtension(ref writer);

        return writer.ToArray();
    }

    /// <summary>写入扩展部分</summary>
    private void WriteExtension(ref BitWriter writer)
    {
        writer.Write(ExtensionAudioObjectType, 5);

        if (ExtensionAudioObjectType == 5) // SBR
        {
            writer.Write(SbrPresentFlag ? 1 : 0, 1);
            if (SbrPresentFlag)
            {
                writer.Write(ExtensionSamplingFrequencyIndex, 4);
                if (ExtensionSamplingFrequencyIndex == 15)
                    writer.Write(ExtensionSamplingFrequency, 24);

                // PS 作为 SBR 子扩展
                writer.Write(PsPresentFlag ? 1 : 0, 1);
            }
        }
        else if (ExtensionAudioObjectType == 29) // PS（独立）
        {
            writer.Write(PsPresentFlag ? 1 : 0, 1);
        }
    }

    /// <summary>转换为 ADTS 帧头信息</summary>
    /// <param name="frameLength">ADTS 帧总长度（含头），0 表示不设置</param>
    /// <param name="protectionAbsent">是否无 CRC（true=7字节头）</param>
    /// <returns>AdtsInfo 实例</returns>
    public AacCodec.AdtsInfo ToAdtsInfo(Int32 frameLength = 0, Boolean protectionAbsent = true)
    {
        // AOT → ADTS profile: AOT-1（Main=0, LC=1, SSR=2, LTP=3）
        var profile = AudioObjectType - 1;
        if (profile < 0) profile = 0;

        return new AacCodec.AdtsInfo
        {
            Profile = profile,
            SampleRateIndex = SamplingFrequencyIndex,
            SampleRate = EffectiveSampleRate,
            Channels = ChannelConfiguration,
            FrameLength = frameLength,
            SamplesPerFrame = FrameLengthFlag ? 960 : 1024,
            ProtectionAbsent = protectionAbsent,
            MpegVersion = 4, // 默认 MPEG4
        };
    }

    #endregion

    #region 辅助

    /// <summary>AAC 标准采样率表（16 项）</summary>
    private static readonly Int32[] SampleRates = [
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
        16000, 12000, 11025, 8000, 7350, 0, 0, 0,
    ];

    /// <summary>根据采样率索引获取采样率值</summary>
    /// <param name="index">采样率索引（0-15）</param>
    /// <returns>采样率（Hz），无效索引返回 0</returns>
    public static Int32 GetSampleRate(Int32 index)
    {
        if (index < 0 || index >= SampleRates.Length) return 0;
        return SampleRates[index];
    }

    /// <summary>根据采样率获取采样率索引</summary>
    /// <param name="sampleRate">采样率（Hz）</param>
    /// <returns>采样率索引（0-12），未知返回 4（44100）</returns>
    public static Int32 GetSamplingFrequencyIndex(Int32 sampleRate)
    {
        return sampleRate switch
        {
            96000 => 0,
            88200 => 1,
            64000 => 2,
            48000 => 3,
            44100 => 4,
            32000 => 5,
            24000 => 6,
            22050 => 7,
            16000 => 8,
            12000 => 9,
            11025 => 10,
            8000 => 11,
            7350 => 12,
            _ => 15, // 使用逃逸表示未知采样率
        };
    }

    #endregion

    #region 位写入器/读取器

    /// <summary>位写入器（大端序）</summary>
    private ref struct BitWriter
    {
        private Byte[] _buffer;
        private Int32 _position; // 以位为单位

        public BitWriter()
        {
            _buffer = new Byte[32];
            _position = 0;
        }

        /// <summary>写入指定位数的值（大端序）</summary>
        public void Write(Int32 value, Int32 bitCount)
        {
            if (bitCount <= 0 || bitCount > 32) return;

            // 确保缓冲区足够
            var neededBytes = (_position + bitCount + 7) / 8;
            if (neededBytes > _buffer.Length)
                Array.Resize(ref _buffer, Math.Max(neededBytes, _buffer.Length * 2));

            for (var i = bitCount - 1; i >= 0; i--)
            {
                var bit = (value >> i) & 1;
                var byteIndex = _position / 8;
                var bitIndex = 7 - (_position % 8);
                if (bit == 1)
                    _buffer[byteIndex] |= (Byte)(1 << bitIndex);
                // else: bit is already 0
                _position++;
            }
        }

        /// <summary>补齐到字节边界</summary>
        public void Flush()
        {
            // 已对齐的无需操作
        }

        /// <summary>获取写入的字节数组</summary>
        public Byte[] ToArray()
        {
            var len = (_position + 7) / 8;
            var result = new Byte[len];
            Array.Copy(_buffer, 0, result, 0, len);
            return result;
        }
    }

    /// <summary>位读取器（大端序）</summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<Byte> _data;
        private Int32 _position; // 以位为单位

        public BitReader(ReadOnlySpan<Byte> data)
        {
            _data = data;
            _position = 0;
        }

        /// <summary>剩余位数</summary>
        public readonly Int32 RemainingBits => _data.Length * 8 - _position;

        /// <summary>读取指定位数的值（大端序）</summary>
        public Int32 Read(Int32 bitCount)
        {
            if (bitCount <= 0 || bitCount > 32 || _position + bitCount > _data.Length * 8)
                return 0;

            var value = 0;
            for (var i = 0; i < bitCount; i++)
            {
                var byteIndex = _position / 8;
                var bitIndex = 7 - (_position % 8);
                var bit = (_data[byteIndex] >> bitIndex) & 1;
                value = (value << 1) | bit;
                _position++;
            }
            return value;
        }
    }

    #endregion
}
