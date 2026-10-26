using System;
using NewLife.Audio;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest;

public class AudioFrameTests
{
    [Fact(DisplayName = "从 PCM 创建帧保留格式、时间戳与缓冲")]
    public void FromPcm_CopiesFormatAndKeepsBuffer()
    {
        var format = new AudioFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 };
        var pcm = new AudioPcm { Format = format, Data = new Byte[32000] };

        var frame = AudioFrame.FromPcm(pcm, TimeSpan.FromSeconds(1.5));

        Assert.NotSame(format, frame.Format);
        Assert.Equal(16000, frame.Format.SampleRate);
        Assert.Equal(TimeSpan.FromSeconds(1.5), frame.Timestamp);
        Assert.Same(pcm.Data, frame.Data);
        Assert.True(frame.OwnsBuffer);
        Assert.Equal(32000, frame.Length);
        Assert.Equal(1.0, frame.Duration.TotalSeconds, 3);
    }

    [Fact(DisplayName = "音频帧转换回 PCM 保留格式与编码")]
    public void ToPcm_RoundTripsFormatAndCodec()
    {
        var format = new AudioFormat { SampleRate = 8000, Channels = 1, BitsPerSample = 16, Encoding = AVTypes.G711U };
        var frame = new AudioFrame(format, TimeSpan.Zero, new Byte[] { 1, 2, 3, 4 });

        var pcm = frame.ToPcm();

        Assert.NotSame(format, pcm.Format);
        Assert.Equal(AVTypes.G711U, pcm.Codec);
        Assert.Equal(frame.Data, pcm.Data);
    }

    [Fact(DisplayName = "空帧与无效格式时长为零")]
    public void Duration_EmptyOrInvalidFormat_ReturnsZero()
    {
        Assert.Equal(TimeSpan.Zero, new AudioFrame().Duration);
        Assert.Equal(TimeSpan.Zero, new AudioFrame(new AudioFormat { SampleRate = 0 }, TimeSpan.Zero, [1, 2]).Duration);
    }

    [Fact(DisplayName = "格式克隆保留平面标记、有效位与声道掩码")]
    public void AudioFormat_Clone_CopiesExtendedProperties()
    {
        var format = new AudioFormat
        {
            SampleRate = 48000,
            Channels = 2,
            BitsPerSample = 24,
            IsPlanar = true,
            ValidBitsPerSample = 20,
            ChannelMask = 0x3,
        };

        var clone = format.Clone();

        Assert.True(clone.IsPlanar);
        Assert.Equal(20, clone.ValidBitsPerSample);
        Assert.Equal(0x3, clone.ChannelMask);
        Assert.Equal(20, clone.EffectiveValidBitsPerSample);
    }

    [Fact(DisplayName = "有效位未指定或超出容器位深时回退到位深")]
    public void EffectiveValidBits_FallsBackToBitsPerSample()
    {
        Assert.Equal(16, new AudioFormat { BitsPerSample = 16 }.EffectiveValidBitsPerSample);
        Assert.Equal(24, new AudioFormat { BitsPerSample = 24, ValidBitsPerSample = 32 }.EffectiveValidBitsPerSample);
    }
}
