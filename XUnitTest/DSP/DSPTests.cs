using System;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest.DSP;

public class AudioFormatTests
{
    [Fact(DisplayName = "AudioFormat默认值为8kHz单声道16bit")]
    public void Default_Is8kMono16()
    {
        var fmt = AudioFormat.Default;
        Assert.Equal(8000, fmt.SampleRate);
        Assert.Equal(1, fmt.Channels);
        Assert.Equal(16, fmt.BitsPerSample);
    }

    [Fact(DisplayName = "AudioFormat计算ByteRate和BytesPerFrame正确")]
    public void Calculate_ByteRate_IsCorrect()
    {
        var fmt = new AudioFormat
        {
            SampleRate = 16000,
            Channels = 1,
            BitsPerSample = 16,
            SamplesPerFrame = 160,
        };

        Assert.Equal(32000, fmt.ByteRate); // 16000*1*2
        Assert.Equal(2, fmt.BytesPerSample);
        Assert.Equal(320, fmt.BytesPerFrame); // 160*1*2
    }

    [Fact(DisplayName = "AudioFormat.Clone生成独立副本")]
    public void Clone_GeneratesIndependentCopy()
    {
        var fmt = AudioFormat.Default;
        var clone = fmt.Clone();
        clone.SampleRate = 44100;

        Assert.Equal(8000, fmt.SampleRate);
        Assert.Equal(44100, clone.SampleRate);
    }
}

public class BitDepthConverterTests
{
    [Fact(DisplayName = "Pcm16ToFloat零值转为0")]
    public void Pcm16ToFloat_Zero_ReturnsZero()
    {
        var pcm = new Byte[] { 0, 0, 0, 0 };
        var samples = new Single[2];
        var count = BitDepthConverter.Pcm16ToFloat(pcm, samples);
        Assert.Equal(2, count);
        Assert.Equal(0, samples[0]);
        Assert.Equal(0, samples[1]);
    }

    [Fact(DisplayName = "Pcm16ToFloat全量程正负值正确")]
    public void Pcm16ToFloat_FullScale()
    {
        // 32767 → ~0.99997, -32768 → -1.0
        var pcm = new Byte[] { 0xFF, 0x7F, 0x00, 0x80 }; // 32767, -32768 (LE)
        var samples = new Single[2];
        BitDepthConverter.Pcm16ToFloat(pcm, samples);

        Assert.True(samples[0] > 0.99f);
        Assert.True(samples[1] < -0.99f);
    }

    [Fact(DisplayName = "FloatToPcm16往返转换一致")]
    public void FloatPcm16_RoundTrip()
    {
        var original = new Byte[200];
        var random = new Random(42);
        random.NextBytes(original);

        var samples = new Single[100];
        BitDepthConverter.Pcm16ToFloat(original, samples);

        var recovered = new Byte[200];
        BitDepthConverter.FloatToPcm16(samples, recovered, 100);

        // 浮点量化有误差，允许 ±1 差异
        for (var i = 0; i < 200; i++)
        {
            var diff = Math.Abs(original[i] - recovered[i]);
            Assert.True(diff <= 1, $"index {i}: diff={diff}");
        }
    }
}

public class ChannelConverterTests
{
    [Fact(DisplayName = "MonoToStereo复制到左右声道")]
    public void MonoToStereo_DuplicatesChannel()
    {
        var mono = new Single[] { 0.5f, -0.3f, 0.8f };
        var stereo = new Single[6];
        ChannelConverter.MonoToStereo(mono, stereo, 3);

        Assert.Equal(0.5f, stereo[0]); // L
        Assert.Equal(0.5f, stereo[1]); // R
        Assert.Equal(-0.3f, stereo[2]);
        Assert.Equal(-0.3f, stereo[3]);
        Assert.Equal(0.8f, stereo[4]);
        Assert.Equal(0.8f, stereo[5]);
    }

    [Fact(DisplayName = "StereoToMono平均左右声道")]
    public void StereoToMono_AveragesChannels()
    {
        var stereo = new Single[] { 1.0f, 0.0f, 0.5f, 0.5f };
        var mono = new Single[2];
        ChannelConverter.StereoToMono(stereo, mono, 2);

        Assert.Equal(0.5f, mono[0]); // (1+0)/2
        Assert.Equal(0.5f, mono[1]); // (0.5+0.5)/2
    }
}

public class VolumeControlTests
{
    [Fact(DisplayName = "VolumeControl增益为1时不变")]
    public void Volume_Gain1_NoChange()
    {
        var vc = new VolumeControl(1.0f);
        // 直接测试增益逻辑
        vc.GainDB = 0; // 0dB = 1.0
        Assert.True(Math.Abs(vc.Gain - 1.0f) < 0.001f);
    }

    [Fact(DisplayName = "VolumeControl增益-6dB约等于0.5")]
    public void Volume_GainMinus6dB()
    {
        var vc = new VolumeControl();
        vc.GainDB = -6.0f;
        Assert.True(Math.Abs(vc.Gain - 0.5f) < 0.01f);
    }

    [Fact(DisplayName = "VolumeControl静音后增益不变但输出为0")]
    public void Volume_Mute_PeakLevelZero()
    {
        var vc = new VolumeControl(2.0f);
        vc.Muted = true;
        Assert.True(vc.Muted);
        Assert.Equal(0f, vc.PeakLevel);
    }
}

public class ResamplerTests
{
    [Fact(DisplayName = "Resampler 8k→16k整数倍重采样输出长度正确")]
    public void ResamplePcm_8kTo16k_OutputDoubled()
    {
        var input = new Byte[160 * 2]; // 10ms @8kHz
        var output = Resampler.ResamplePcm(input, 8000, 16000);

        // 输出采样数 = 160 * 16000 / 8000 = 320
        Assert.Equal(320 * 2, output.Length);
    }

    [Fact(DisplayName = "Resampler相同采样率输入输出等长")]
    public void ResamplePcm_SameRate_OutputSameLength()
    {
        var input = new Byte[100 * 2];
        var random = new Random(42);
        random.NextBytes(input);

        var output = Resampler.ResamplePcm(input, 8000, 8000);
        Assert.Equal(input.Length, output.Length);
    }

    [Fact(DisplayName = "Resampler 48k→8k降采样输出更短")]
    public void ResamplePcm_48kTo8k_OutputShorter()
    {
        var input = new Byte[480 * 2]; // 10ms @48kHz
        var output = Resampler.ResamplePcm(input, 48000, 8000);
        Assert.True(output.Length < input.Length);
    }
}
