using NewLife.Audio.Codecs;

namespace NewLife.Audio;

/// <summary>AudioSpecificConfig（ISO 14496-3）。描述 AAC 音频编码器配置，用于 FLV/MP4 等容器中的编解码器特定数据</summary>
/// <remarks>
/// AudioSpecificConfig 是 ISO 14496-3 定义的音频编码器配置结构，包含：
/// - AudioObjectType（音频对象类型，如 AAC-LC/HE-AAC）
/// - SamplingFrequency（采样率）
/// - ChannelConfiguration（声道配置）
/// 
/// 在 FLV 容器中作为 AAC Sequence Header 的 DataTags 数据；
/// 在 MP4 容器中作为 esds/stsd box 的编解码器配置。
/// 
/// 编码格式（2字节或5字节）：
///   Byte 0: [ audioObjectType (5 bits) | samplingFrequencyIndex (3 bits) ]
///   Byte 1: [ samplingFrequencyIndex (1 bit) | channelConfiguration (4 bits) | GASpecificConfig start (3 bits) ]
///   当 samplingFrequencyIndex == 0x0F 时，扩展为 24 位采样率
/// </remarks>
public class AudioSpecificConfig
{
    /// <summary>音频对象类型</summary>
    public AudioObjectType AudioType { get; set; } = AudioObjectType.AAC_LC;

    /// <summary>采样率</summary>
    public Int32 SampleRate { get; set; } = 44100;

    /// <summary>声道数</summary>
    public Int32 Channels { get; set; } = 2;

    #region 静态采样率表
    private static readonly Int32[] SampleRates = [
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
        16000, 12000, 11025, 8000, 7350, 0, 0, 0,
    ];
    #endregion

    /// <summary>获取采样率索引</summary>
    private Int32 GetSampleRateIndex()
    {
        for (var i = 0; i < SampleRates.Length; i++)
        {
            if (SampleRates[i] == SampleRate) return i;
        }
        return 11; // 默认 8000
    }

    /// <summary>编码为字节数组（AudioSpecificConfig，ISO 14496-3）</summary>
    /// <returns>最小 2 字节的 AudioSpecificConfig</returns>
    public Byte[] ToArray()
    {
        var audioType = (Int32)AudioType;
        var rateIdx = GetSampleRateIndex();

        // audioObjectType (5 bits) | samplingFrequencyIndex (4 bits) | channelConfiguration (4 bits)
        // = 13 位，填充到 16 位（2字节）
        var value = (audioType << 11) | (rateIdx << 7) | (Channels << 3);
        return [(Byte)(value >> 8), (Byte)(value & 0xFF)];
    }

    /// <summary>从字节数组解析 AudioSpecificConfig</summary>
    /// <param name="data">AudioSpecificConfig 字节数据</param>
    /// <param name="offset">起始偏移</param>
    /// <returns>AudioSpecificConfig 实例，解析失败返回 null</returns>
    public static AudioSpecificConfig Parse(Byte[] data, Int32 offset = 0)
    {
        if (data == null || offset + 1 >= data.Length) return null;

        var config = new AudioSpecificConfig();

        // 前 2 字节：audioObjectType(5) | samplingFrequencyIndex(4) | channelConfiguration(4) | 剩余
        var high = data[offset];
        var low = data[offset + 1];

        var audioType = (high >> 3) & 0x1F; // bits 0-4 (0-based: 7-3)
        config.AudioType = (AudioObjectType)audioType;

        var rateIdx = ((high & 0x07) << 1) | ((low >> 7) & 0x01); // bits 5-8 (0-based: 2-6 → 2+4 bits)
        if (rateIdx >= 0 && rateIdx < SampleRates.Length && SampleRates[rateIdx] > 0)
            config.SampleRate = SampleRates[rateIdx];
        else
            config.SampleRate = 44100;

        config.Channels = (low >> 3) & 0x0F; // bits 9-12

        return config;
    }

    /// <summary>从 AacCodec.AdtsInfo 创建 AudioSpecificConfig</summary>
    /// <param name="adts">ADTS 帧头信息</param>
    /// <returns>AudioSpecificConfig 实例</returns>
    public static AudioSpecificConfig FromAdtsInfo(AacCodec.AdtsInfo adts)
    {
        if (adts == null) throw new ArgumentNullException(nameof(adts));

        // ADTS profile → AudioObjectType: ADTS profile 0=Main→1, 1=LC→2, 2=SSR→3, 3=LTP→4
        var audioType = adts.Profile + 1;
        if (audioType < 1 || audioType > 4) audioType = 2; // default AAC-LC

        return new AudioSpecificConfig
        {
            AudioType = (AudioObjectType)audioType,
            SampleRate = adts.SampleRate > 0 ? adts.SampleRate : 44100,
            Channels = adts.Channels > 0 ? adts.Channels : 2,
        };
    }

    /// <summary>转换为 ADTS 帧头信息</summary>
    /// <returns>AdtsInfo 实例</returns>
    public AacCodec.AdtsInfo ToAdtsInfo()
    {
        var rateIdx = GetSampleRateIndex();
        var profile = (Int32)AudioType - 1; // AudioObjectType → ADTS profile
        if (profile < 0) profile = 0;
        if (profile > 3) profile = 1; // AAC-LC

        return new AacCodec.AdtsInfo
        {
            Profile = profile,
            SampleRateIndex = rateIdx,
            SampleRate = SampleRate,
            Channels = Channels,
            ProtectionAbsent = true,
        };
    }
}

/// <summary>AAC 音频对象类型（ISO 14496-3）</summary>
public enum AudioObjectType
{
    /// <summary>AAC Main</summary>
    AAC_MAIN = 1,

    /// <summary>AAC LC（Low Complexity）</summary>
    AAC_LC = 2,

    /// <summary>AAC SSR（Scalable Sample Rate）</summary>
    AAC_SSR = 3,

    /// <summary>AAC LTP（Long Term Prediction）</summary>
    AAC_LTP = 4,

    /// <summary>SBR（Spectral Band Replication）</summary>
    SBR = 5,

    /// <summary>AAC Scalable</summary>
    AAC_SCALABLE = 6,
}
