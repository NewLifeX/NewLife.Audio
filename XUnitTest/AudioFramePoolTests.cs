using System;
using NewLife.Audio;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest;

/// <summary>AudioFrame 池化租用与归还</summary>
public class AudioFramePoolTests
{
    private static AudioFormat Format => new() { SampleRate = 16000, Channels = 1, BitsPerSample = 16 };

    [Fact(DisplayName = "租用帧的容量不小于请求字节数")]
    public void Rent_ProvidesCapacity()
    {
        using var frame = AudioFrame.Rent(Format, 1000, TimeSpan.FromMilliseconds(20));

        Assert.True(frame.IsPooled);
        Assert.True(frame.OwnsBuffer);
        Assert.True(frame.Data.Length >= 1000);
        Assert.Equal(1000, frame.Length);
        Assert.Equal(1000, frame.GetSpan().Length);
        Assert.Equal(TimeSpan.FromMilliseconds(20), frame.Timestamp);
    }

    [Fact(DisplayName = "归还后帧不再持有缓冲且可重复释放")]
    public void Dispose_ReleasesBuffer()
    {
        var frame = AudioFrame.Rent(Format, 512);
        frame.Data[0] = 1;

        frame.Dispose();

        Assert.False(frame.IsPooled);
        Assert.Empty(frame.Data);
        Assert.Equal(0, frame.Length);
        Assert.Equal(0, frame.GetSpan().Length);

        frame.Dispose();
        Assert.False(frame.IsPooled);
    }

    [Fact(DisplayName = "归还后可再次租到同规格缓冲")]
    public void Rent_AfterDispose_ReusesPool()
    {
        var frame = AudioFrame.Rent(Format, 4096);
        var capacity = frame.Data.Length;
        frame.Dispose();

        using var again = AudioFrame.Rent(Format, 4096);

        Assert.True(again.IsPooled);
        Assert.True(again.Data.Length >= 4096);
        Assert.True(again.Data.Length <= capacity);
    }

    [Fact(DisplayName = "借用帧释放无副作用")]
    public void Dispose_BorrowedFrame_KeepsData()
    {
        var data = new Byte[] { 1, 2, 3, 4 };
        var frame = new AudioFrame(Format, TimeSpan.Zero, data, 0, data.Length);

        frame.Dispose();

        Assert.False(frame.IsPooled);
        Assert.Equal(4, frame.Length);
        Assert.Equal(data, frame.Data);
        Assert.Equal(new Byte[] { 1, 2, 3, 4 }, frame.GetSpan().ToArray());
    }

    [Fact(DisplayName = "租用零字节仍可用且无需归还")]
    public void Rent_ZeroBytes()
    {
        using var frame = AudioFrame.Rent(Format, 0);

        Assert.Equal(0, frame.Length);
        Assert.False(frame.IsPooled);
    }

    [Fact(DisplayName = "非法租用参数抛异常")]
    public void Rent_InvalidArgs_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AudioFrame.Rent(null, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioFrame.Rent(Format, -1));
    }
}
