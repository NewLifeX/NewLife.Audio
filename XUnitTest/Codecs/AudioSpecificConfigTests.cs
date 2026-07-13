using System;
using NewLife.Audio.Codecs;
using Xunit;

namespace XUnitTest.Codecs;

public class AudioSpecificConfigTests
{
    #region 构造测试

    [Fact(DisplayName = "FromParameters 生成 AAC-LC 标准 2 字节配置")]
    public void FromParameters_AacLc_44100Hz_Stereo()
    {
        // AAC-LC (AOT=2), 44100Hz, 立体声
        var asc = AudioSpecificConfig.FromParameters(2, 44100, 2);
        Assert.Equal(2, asc.AudioObjectType);
        Assert.Equal(4, asc.SamplingFrequencyIndex);
        Assert.Equal(44100, asc.SamplingFrequency);
        Assert.Equal(2, asc.ChannelConfiguration);
        Assert.False(asc.FrameLengthFlag);
        Assert.False(asc.ExtensionFlag);

        // 验证 2 字节编码
        var data = asc.ToByteArray();
        Assert.Equal(2, data.Length);

        // 手动计算预期值:
        // b0 = (2 << 3) | (4 >> 1) = 16 | 2 = 0x12
        // b1 = ((4 & 1) << 7) | (2 << 3) = 0 | 16 = 0x10
        Assert.Equal(0x12, data[0]);
        Assert.Equal(0x10, data[1]);
    }

    [Fact(DisplayName = "FromParameters 支持 8kHz 单声道")]
    public void FromParameters_8kHz_Mono()
    {
        var asc = AudioSpecificConfig.FromParameters(2, 8000, 1);
        Assert.Equal(11, asc.SamplingFrequencyIndex);
        Assert.Equal(8000, asc.SamplingFrequency);
        Assert.Equal(1, asc.ChannelConfiguration);
    }

    [Fact(DisplayName = "FromParameters 未知采样率使用逃逸")]
    public void FromParameters_EscapeSamplingRate()
    {
        // 11502Hz 不在标准表中，应使用逃逸(SFI=15)
        var asc = AudioSpecificConfig.FromParameters(2, 11502, 1);
        Assert.Equal(15, asc.SamplingFrequencyIndex);
        Assert.Equal(11502, asc.SamplingFrequency);

        // 序列化后应有 2 + 3 = 5 字节（含 24bit 逃逸频率）
        var data = asc.ToByteArray();
        Assert.Equal(5, data.Length);

        // 反序列化验证 round-trip
        var parsed = AudioSpecificConfig.Parse(data);
        Assert.Equal(15, parsed.SamplingFrequencyIndex);
        Assert.Equal(11502, parsed.SamplingFrequency);
    }

    #endregion

    #region AdtsInfo 互转

    [Fact(DisplayName = "FromAdts 正确转换 ADTS 帧头")]
    public void FromAdts_ValidAdts()
    {
        // ADTS: AAC-LC, 44100Hz, 立体声, MPEG4
        var adts = new AacCodec.AdtsInfo
        {
            Profile = 1,          // LC
            SampleRateIndex = 4,  // 44100
            SampleRate = 44100,
            Channels = 2,
            FrameLength = 100,
            SamplesPerFrame = 1024,
            ProtectionAbsent = true,
            MpegVersion = 4,
        };

        var asc = AudioSpecificConfig.FromAdts(adts);
        Assert.Equal(2, asc.AudioObjectType);  // Profile(1) + 1 = AOT(2)
        Assert.Equal(4, asc.SamplingFrequencyIndex);
        Assert.Equal(44100, asc.SamplingFrequency);
        Assert.Equal(2, asc.ChannelConfiguration);
        Assert.False(asc.FrameLengthFlag);     // 1024, not 960
    }

    [Fact(DisplayName = "AdtsInfo round-trip 一致")]
    public void ToAdtsInfo_RoundTrip()
    {
        var asc = AudioSpecificConfig.FromParameters(2, 44100, 2);
        var adts = asc.ToAdtsInfo(100, true);

        Assert.Equal(1, adts.Profile);         // AOT(2) - 1 = 1
        Assert.Equal(4, adts.SampleRateIndex);
        Assert.Equal(44100, adts.SampleRate);
        Assert.Equal(2, adts.Channels);
        Assert.Equal(100, adts.FrameLength);
        Assert.True(adts.ProtectionAbsent);
        Assert.Equal(1024, adts.SamplesPerFrame);
    }

    [Fact(DisplayName = "FromAdts → ToAdtsInfo 往返一致")]
    public void AdtsInfo_FullRoundTrip()
    {
        var original = new AacCodec.AdtsInfo
        {
            Profile = 1,
            SampleRateIndex = 4,
            SampleRate = 44100,
            Channels = 2,
            FrameLength = 200,
            SamplesPerFrame = 1024,
            ProtectionAbsent = true,
            MpegVersion = 4,
        };

        var asc = AudioSpecificConfig.FromAdts(original);
        var roundTrip = asc.ToAdtsInfo(200, true);

        Assert.Equal(original.Profile, roundTrip.Profile);
        Assert.Equal(original.SampleRateIndex, roundTrip.SampleRateIndex);
        Assert.Equal(original.SampleRate, roundTrip.SampleRate);
        Assert.Equal(original.Channels, roundTrip.Channels);
        Assert.Equal(original.FrameLength, roundTrip.FrameLength);
        Assert.Equal(original.ProtectionAbsent, roundTrip.ProtectionAbsent);
        Assert.Equal(original.SamplesPerFrame, roundTrip.SamplesPerFrame);
    }

    #endregion

    #region 字节序列化/反序列化

    [Fact(DisplayName = "ToByteArray → Parse round-trip")]
    public void ByteArray_RoundTrip()
    {
        var asc = AudioSpecificConfig.FromParameters(2, 48000, 2);
        var data = asc.ToByteArray();
        var parsed = AudioSpecificConfig.Parse(data);

        Assert.Equal(asc.AudioObjectType, parsed.AudioObjectType);
        Assert.Equal(asc.SamplingFrequencyIndex, parsed.SamplingFrequencyIndex);
        Assert.Equal(asc.SamplingFrequency, parsed.SamplingFrequency);
        Assert.Equal(asc.ChannelConfiguration, parsed.ChannelConfiguration);
        Assert.Equal(asc.FrameLengthFlag, parsed.FrameLengthFlag);
    }

    [Fact(DisplayName = "Parse 解析标准 2 字节 AAC-LC 配置")]
    public void Parse_Standard2Byte()
    {
        // 已知正确的 AAC-LC 44100Hz 立体声配置
        var data = new Byte[] { 0x12, 0x10 };
        var asc = AudioSpecificConfig.Parse(data);

        Assert.Equal(2, asc.AudioObjectType);
        Assert.Equal(4, asc.SamplingFrequencyIndex);
        Assert.Equal(44100, asc.SamplingFrequency);
        Assert.Equal(2, asc.ChannelConfiguration);
        Assert.False(asc.ExtensionFlag);
    }

    [Fact(DisplayName = "Parse 多组采样率索引")]
    public void Parse_VariousSampleRates()
    {
        // 测试所有标准采样率
        var testCases = new (Byte[], Int32)[]
        {
            ([0x10, 0x08], 96000),
            ([0x10, 0x88], 88200),
            ([0x11, 0x08], 64000),
            ([0x11, 0x88], 48000),
            ([0x12, 0x08], 44100),
            ([0x12, 0x88], 32000),
            ([0x13, 0x08], 24000),
            ([0x13, 0x88], 22050),
            ([0x14, 0x08], 16000),
            ([0x14, 0x88], 12000),
            ([0x15, 0x08], 11025),
            ([0x15, 0x88], 8000),
            ([0x16, 0x08], 7350),
        };

        foreach (var (bytes, expectedRate) in testCases)
        {
            // 使用 AOT=2, CC=1, 同步字 = 0xFFF
            var asc = AudioSpecificConfig.Parse(bytes);
            Assert.Equal(expectedRate, asc.SamplingFrequency);
        }
    }

    [Fact(DisplayName = "Parse 数据不足抛异常")]
    public void Parse_TooShort_Throws()
    {
        Assert.Throws<ArgumentException>(() => AudioSpecificConfig.Parse([0x12]));
        Assert.Throws<ArgumentException>(() => AudioSpecificConfig.Parse(ReadOnlySpan<Byte>.Empty));
    }

    #endregion

    #region SBR 扩展

    [Fact(DisplayName = "HE-AAC (AAC-LC + SBR) 扩展配置")]
    public void HeAac_WithSbr()
    {
        // HE-AAC: core=AAC-LC(AOT=2), extension=SBR(AOT=5)
        var asc = AudioSpecificConfig.FromParameters(2, 44100, 2);
        asc.ExtensionFlag = true;
        asc.ExtensionAudioObjectType = 5; // SBR
        asc.SbrPresentFlag = true;
        asc.ExtensionSamplingFrequencyIndex = 4; // 44100

        var data = asc.ToByteArray();
        // 基础 2 字节 + 扩展至少 5+1+4 = 10 位 → 2 字节，合计 4 字节
        Assert.True(data.Length >= 4);

        // 反序列化验证
        var parsed = AudioSpecificConfig.Parse(data);
        Assert.True(parsed.ExtensionFlag);
        Assert.Equal(5, parsed.ExtensionAudioObjectType);
        Assert.True(parsed.SbrPresentFlag);
        Assert.Equal(4, parsed.ExtensionSamplingFrequencyIndex);
    }

    [Fact(DisplayName = "HE-AACv2 (AAC-LC + SBR + PS) 扩展配置")]
    public void HeAacV2_WithSbrAndPs()
    {
        // HE-AACv2: core=AAC-LC(AOT=2), extension=SBR(AOT=5) + PS
        var asc = AudioSpecificConfig.FromParameters(2, 44100, 2);
        asc.ExtensionFlag = true;
        asc.ExtensionAudioObjectType = 5; // SBR
        asc.SbrPresentFlag = true;
        asc.ExtensionSamplingFrequencyIndex = 4;
        asc.PsPresentFlag = true;

        var data = asc.ToByteArray();
        Assert.True(data.Length >= 4);

        var parsed = AudioSpecificConfig.Parse(data);
        Assert.True(parsed.ExtensionFlag);
        Assert.Equal(5, parsed.ExtensionAudioObjectType);
        Assert.True(parsed.SbrPresentFlag);
        Assert.Equal(4, parsed.ExtensionSamplingFrequencyIndex);
        Assert.True(parsed.PsPresentFlag);
    }

    #endregion

    #region 边界值

    [Fact(DisplayName = "AAC Main profile (AOT=1)")]
    public void AacMainProfile()
    {
        var asc = AudioSpecificConfig.FromParameters(1, 44100, 2);
        var data = asc.ToByteArray();
        var parsed = AudioSpecificConfig.Parse(data);

        Assert.Equal(1, parsed.AudioObjectType);
        Assert.Equal(2, parsed.ChannelConfiguration);
    }

    [Fact(DisplayName = "单声道 (CC=1) 配置")]
    public void MonoChannel()
    {
        var asc = AudioSpecificConfig.FromParameters(2, 44100, 1);
        Assert.Equal(1, asc.ChannelConfiguration);

        var data = asc.ToByteArray();
        var parsed = AudioSpecificConfig.Parse(data);
        Assert.Equal(1, parsed.ChannelConfiguration);
    }

    [Fact(DisplayName = "5.1 声道 (CC=6) 配置")]
    public void FiveOneChannels()
    {
        var asc = AudioSpecificConfig.FromParameters(2, 48000, 6);
        Assert.Equal(6, asc.ChannelConfiguration);

        var data = asc.ToByteArray();
        var parsed = AudioSpecificConfig.Parse(data);
        Assert.Equal(6, parsed.ChannelConfiguration);
    }

    [Fact(DisplayName = "FrameLengthFlag=960 正确设置")]
    public void FrameLength960()
    {
        var asc = AudioSpecificConfig.FromParameters(2, 44100, 2);
        asc.FrameLengthFlag = true;

        var data = asc.ToByteArray();
        var parsed = AudioSpecificConfig.Parse(data);
        Assert.True(parsed.FrameLengthFlag);
    }

    [Fact(DisplayName = "GetSampleRate 方法正确返回")]
    public void GetSampleRate_ValidIndices()
    {
        Assert.Equal(96000, AudioSpecificConfig.GetSampleRate(0));
        Assert.Equal(44100, AudioSpecificConfig.GetSampleRate(4));
        Assert.Equal(8000, AudioSpecificConfig.GetSampleRate(11));
        Assert.Equal(7350, AudioSpecificConfig.GetSampleRate(12));
        Assert.Equal(0, AudioSpecificConfig.GetSampleRate(13));
        Assert.Equal(0, AudioSpecificConfig.GetSampleRate(-1));
    }

    [Fact(DisplayName = "GetSamplingFrequencyIndex 方法正确转换")]
    public void GetSamplingFrequencyIndex_ValidRates()
    {
        Assert.Equal(0, AudioSpecificConfig.GetSamplingFrequencyIndex(96000));
        Assert.Equal(4, AudioSpecificConfig.GetSamplingFrequencyIndex(44100));
        Assert.Equal(11, AudioSpecificConfig.GetSamplingFrequencyIndex(8000));
        Assert.Equal(15, AudioSpecificConfig.GetSamplingFrequencyIndex(11400)); // 逃逸
    }

    #endregion

    #region Mp4FileWriter 兼容性

    [Fact(DisplayName = "Mp4FileWriter 替换后输出一致")]
    public void Mp4FileWriter_Compatibility()
    {
        // 验证 AudioSpecificConfig 生成的 2 字节与 Mp4FileWriter 原逻辑一致
        // 原逻辑: AOT=2, SFI, CC, frameLength=0, depends=0, extension=0
        // 新逻辑: AudioSpecificConfig.FromParameters(2, sr, ch).ToByteArray()

        var testCases = new[]
        {
            (44100, 2, new Byte[] { 0x12, 0x10 }),
            (44100, 1, [0x12, 0x08]),
            (8000, 1, [0x15, 0x88]),
            (48000, 2, [0x11, 0x90]),
            (16000, 1, [0x14, 0x08]),
        };

        foreach (var (sampleRate, channels, expected) in testCases)
        {
            var asc = AudioSpecificConfig.FromParameters(2, sampleRate, channels);
            var data = asc.ToByteArray();
            Assert.Equal(expected, data);
        }
    }

    #endregion
}
