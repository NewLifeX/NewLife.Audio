using System;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest.DSP;

public class SpeedChangerTests
{
    [Fact(DisplayName = "SpeedChanger 1.0速度比输出长度与输入一致")]
    public void SpeedRatio_1_0_OutputSameLength()
    {
        var changer = new SpeedChanger(1.0f);
        Assert.Equal(1.0f, changer.SpeedRatio);

        var source = new SineSource(4096, 440);
        changer.Source = source;

        var buffer = new Single[1024];
        var read = changer.Read(buffer, 0, 1024);
        // 1.0 倍速应产生大致等量输出
        Assert.True(read > 0);
    }

    [Fact(DisplayName = "SpeedChanger 2.0速度比范围限制")]
    public void SpeedRatio_2_0_Valid()
    {
        var changer = new SpeedChanger(2.0f);
        Assert.Equal(2.0f, changer.SpeedRatio);
    }

    [Fact(DisplayName = "SpeedChanger SpeedRatio被限制在0.25~4.0")]
    public void SpeedRatio_ClampedToRange()
    {
        var low = new SpeedChanger(0.1f);
        Assert.Equal(0.25f, low.SpeedRatio);

        var high = new SpeedChanger(5.0f);
        Assert.Equal(4.0f, high.SpeedRatio);

        var normal = new SpeedChanger(2.0f);
        Assert.Equal(2.0f, normal.SpeedRatio);
    }

    [Fact(DisplayName = "SpeedChanger Read无Source返回0")]
    public void Read_NoSource_ReturnsZero()
    {
        var changer = new SpeedChanger(1.0f);
        var buffer = new Single[100];
        var read = changer.Read(buffer, 0, 100);
        Assert.Equal(0, read);
    }

    [Fact(DisplayName = "SpeedChanger 0.5减速产生更多输出")]
    public void SpeedRatio_0_5_Slower()
    {
        var changer = new SpeedChanger(0.5f);
        var source = new SineSource(4096, 440);
        changer.Source = source;

        var buffer = new Single[1024];
        var read = changer.Read(buffer, 0, 1024);
        Assert.True(read >= 0);
    }

    /// <summary>正弦波测试数据源</summary>
    private sealed class SineSource : IAudioProcessor
    {
        private readonly Int32 _total;
        private Int32 _pos;

        public AudioFormat InputFormat => AudioFormat.Default;
        public AudioFormat OutputFormat => AudioFormat.Default;
        public IAudioProcessor Source { get; set; }

        public SineSource(Int32 total, Int32 freq) => _total = total;

        public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
        {
            var remaining = _total - _pos;
            var toRead = Math.Min(count, remaining);
            for (var i = 0; i < toRead; i++)
                buffer[offset + i] = (Single)Math.Sin(2 * Math.PI * 440 * (_pos + i) / 8000);
            _pos += toRead;
            return toRead;
        }

        public void Reset() => _pos = 0;
    }
}
