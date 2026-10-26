using System;
using System.Linq;
using NewLife.Audio;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest;

/// <summary>AudioPcm 分帧与 AudioFrame 借用视图</summary>
public class AudioPcmFramesTests
{
    private static AudioPcm CreatePcm(Int32 samples, Int32 channels = 1, Int32 sampleRate = 16000)
    {
        var format = new AudioFormat { SampleRate = sampleRate, Channels = channels, BitsPerSample = 16, SamplesPerFrame = 160 };
        return new AudioPcm { Format = format, Data = new Byte[samples * channels * 2] };
    }

    [Fact(DisplayName = "按样本数分帧并给出递增时间戳")]
    public void Frames_SplitsBySamples()
    {
        var pcm = CreatePcm(16000);

        var frames = pcm.Frames(160).ToList();

        Assert.Equal(100, frames.Count);
        Assert.All(frames, f => Assert.Equal(320, f.Length));
        Assert.Equal(TimeSpan.Zero, frames[0].Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(10), frames[1].Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(990), frames[99].Timestamp);
        Assert.All(frames, f => Assert.False(f.OwnsBuffer));
    }

    [Fact(DisplayName = "分帧默认取格式的每帧样本数")]
    public void Frames_UsesFormatSamplesPerFrame()
    {
        var pcm = CreatePcm(1600);

        var frames = pcm.Frames().ToList();

        Assert.Equal(10, frames.Count);
        Assert.All(frames, f => Assert.Equal(320, f.Length));
    }

    [Fact(DisplayName = "借用帧与源缓冲共享内存且视图正确")]
    public void Frames_BorrowsSourceBuffer()
    {
        var pcm = CreatePcm(320, 2);
        pcm.Data[2 * 2 * 100] = 0x5A;
        pcm.Data[2 * 2 * 100 + 1] = 0x1B;

        var frames = pcm.Frames(100).ToList();

        Assert.Equal(4, frames.Count);
        Assert.Same(pcm.Data, frames[0].Data);
        Assert.Equal(0, frames[0].Offset);
        Assert.Equal(400, frames[1].Offset);
        Assert.Equal(0x5A, frames[1].GetSpan()[0]);
        Assert.Equal(0x1B, frames[1].GetSpan()[1]);
        Assert.Equal(400, frames[1].Length);
    }

    [Fact(DisplayName = "尾帧不足一帧时按实际长度产出")]
    public void Frames_LastPartialFrame()
    {
        var pcm = CreatePcm(1000);

        var frames = pcm.Frames(300).ToList();

        Assert.Equal(4, frames.Count);
        Assert.Equal(600, frames[0].Length);
        Assert.Equal(600, frames[2].Length);
        Assert.Equal(200, frames[3].Length);
        Assert.Equal(TimeSpan.FromMilliseconds(56.25), frames[3].Timestamp);
    }

    [Fact(DisplayName = "空数据或缺少格式时不分帧")]
    public void Frames_EmptyData_YieldsNothing()
    {
        Assert.Empty(new AudioPcm { Format = new AudioFormat(), Data = [] }.Frames(160));
        Assert.Empty(new AudioPcm { Format = null, Data = new Byte[320] }.Frames(160));
    }

    [Fact(DisplayName = "非法样本数按 1 处理")]
    public void Frames_InvalidSamplesPerFrame_UsesOne()
    {
        var pcm = CreatePcm(4);

        var frames = pcm.Frames(-5).ToList();

        Assert.Equal(4, frames.Count);
        Assert.All(frames, f => Assert.Equal(2, f.Length));
    }

    [Fact(DisplayName = "借用构造参数越界时抛异常")]
    public void Frame_Borrow_OutOfRange_Throws()
    {
        var data = new Byte[16];
        var format = new AudioFormat();

        Assert.Throws<ArgumentNullException>(() => new AudioFrame(format, TimeSpan.Zero, (Byte[])null, 0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioFrame(format, TimeSpan.Zero, data, -1, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioFrame(format, TimeSpan.Zero, data, 8, 16));
    }

    [Fact(DisplayName = "偏移与计数缺省时按整段缓冲计算")]
    public void Frame_WholeBuffer_Defaults()
    {
        var frame = new AudioFrame(new AudioFormat(), TimeSpan.Zero, new Byte[64]);

        Assert.Equal(0, frame.Offset);
        Assert.Equal(0, frame.Count);
        Assert.Equal(64, frame.Length);
        Assert.Equal(64, frame.GetSpan().Length);
    }
}
