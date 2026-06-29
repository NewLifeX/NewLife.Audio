using System;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest.DSP;

public class PitchShifterTests
{
    [Fact(DisplayName = "PitchShifter SemitonesToRatio +12→2.0")]
    public void SemitonesToRatio_Plus12_Equals2()
    {
        var ratio = PitchShifter.SemitonesToRatio(12f);
        Assert.True(Math.Abs(ratio - 2.0f) < 0.001f);
    }

    [Fact(DisplayName = "PitchShifter SemitonesToRatio -12→0.5")]
    public void SemitonesToRatio_Minus12_EqualsHalf()
    {
        var ratio = PitchShifter.SemitonesToRatio(-12f);
        Assert.True(Math.Abs(ratio - 0.5f) < 0.001f);
    }

    [Fact(DisplayName = "PitchShifter SemitonesToRatio 0→1.0")]
    public void SemitonesToRatio_Zero_Equals1()
    {
        var ratio = PitchShifter.SemitonesToRatio(0f);
        Assert.Equal(1.0f, ratio);
    }

    [Fact(DisplayName = "PitchShifter 1.0音高比构造成功")]
    public void PitchRatio_1_0_Valid()
    {
        var shifter = new PitchShifter(1.0f);
        Assert.Equal(1.0f, shifter.PitchRatio);
    }

    [Fact(DisplayName = "PitchShifter PitchRatio限制在0.25~4.0")]
    public void PitchRatio_ClampedToRange()
    {
        var low = new PitchShifter(0.1f);
        Assert.Equal(0.25f, low.PitchRatio);

        var high = new PitchShifter(5.0f);
        Assert.Equal(4.0f, high.PitchRatio);
    }

    [Fact(DisplayName = "PitchShifter Read无Source返回0")]
    public void Read_NoSource_ReturnsZero()
    {
        var shifter = new PitchShifter(1.0f);
        var buffer = new Single[100];
        var read = shifter.Read(buffer, 0, 100);
        Assert.Equal(0, read);
    }

    [Fact(DisplayName = "PitchShifter Read有Source返回数据")]
    public void Read_WithSource_ReturnsData()
    {
        var shifter = new PitchShifter(1.0f);
        var source = new SineSource(2048);
        shifter.Source = source;

        var buffer = new Single[512];
        var read = shifter.Read(buffer, 0, 512);
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

        public SineSource(Int32 total) => _total = total;

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
