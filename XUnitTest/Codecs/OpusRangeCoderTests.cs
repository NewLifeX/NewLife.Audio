using System;
using NewLife.Audio.Codecs;
using Xunit;

namespace XUnitTest.Codecs;

public class OpusRangeCoderTests
{
    [Fact(DisplayName = "Opus范围编码器符号往返一致")]
    public void RoundTrip_Symbols()
    {
        // PDF {1,2,3,4}/10
        var cdf = new[] { 0, 1, 3, 6, 10 };
        var symbols = new[] { 0, 1, 2, 3, 2, 1, 0, 3, 3, 2, 1, 0, 2, 3, 1, 0 };
        var encoder = new OpusRangeEncoder();
        foreach (var symbol in symbols)
        {
            encoder.Encode(cdf[symbol], cdf[symbol + 1], 10);
        }

        var data = encoder.Finish();
        var decoder = new OpusRangeDecoder(data);
        foreach (var expected in symbols)
        {
            var fs = decoder.Decode(10);
            var actual = 0;
            while (actual < 4 && fs >= (UInt32)cdf[actual + 1]) actual++;

            decoder.Update(cdf[actual], cdf[actual + 1], 10);
            Assert.Equal(expected, actual);
        }

        Assert.False(decoder.Error);
    }

    [Fact(DisplayName = "Opus范围编码器二进制符号往返一致")]
    public void RoundTrip_BitLogp()
    {
        var bits = new[] { 1, 0, 1, 1, 0, 0, 1, 0, 1, 1, 1, 0, 0, 1, 0, 1 };
        var logp = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 1, 2, 3, 4, 5, 6, 7, 8 };
        var encoder = new OpusRangeEncoder();
        for (var i = 0; i < bits.Length; i++)
        {
            encoder.EncodeBitLogp(bits[i], logp[i]);
        }

        var decoder = new OpusRangeDecoder(encoder.Finish());
        for (var i = 0; i < bits.Length; i++)
        {
            Assert.Equal(bits[i], decoder.DecodeBitLogp(logp[i]));
        }

        Assert.False(decoder.Error);
    }

    [Fact(DisplayName = "Opus范围编码器ICDF往返一致")]
    public void RoundTrip_ICdf()
    {
        var icdf = new Byte[] { 255, 200, 100, 0 };
        var symbols = new[] { 0, 1, 2, 3, 0, 2, 1, 3, 3, 0, 1, 2 };
        var encoder = new OpusRangeEncoder();
        foreach (var symbol in symbols)
        {
            encoder.EncodeICdf(symbol, icdf, 8);
        }

        var decoder = new OpusRangeDecoder(encoder.Finish());
        foreach (var expected in symbols)
        {
            Assert.Equal(expected, decoder.DecodeICdf(icdf, 8));
        }

        Assert.False(decoder.Error);
    }

    [Fact(DisplayName = "Opus范围编码器整数往返一致")]
    public void RoundTrip_UInt()
    {
        var values = new UInt32[] { 0, 1, 5, 255, 256, 300, 70000, 99999 };
        var encoder = new OpusRangeEncoder();
        foreach (var value in values)
        {
            encoder.EncodeUInt(value, 100000);
        }

        var decoder = new OpusRangeDecoder(encoder.Finish());
        foreach (var expected in values)
        {
            Assert.Equal(expected, decoder.DecodeUInt(100000));
        }

        Assert.False(decoder.Error);
    }

    [Fact(DisplayName = "Opus范围编码器原始比特往返一致")]
    public void RoundTrip_RawBits()
    {
        var encoder = new OpusRangeEncoder();
        encoder.Encode(0, 1, 2);
        encoder.EncodeBits(0x1ABC, 13);
        encoder.Encode(1, 2, 2);

        var decoder = new OpusRangeDecoder(encoder.Finish());
        Assert.Equal(0U, decoder.Decode(2));
        decoder.Update(0, 1, 2);
        Assert.Equal(0x1ABCU, decoder.DecodeBits(13));
        Assert.Equal(1U, decoder.Decode(2));
        decoder.Update(1, 2, 2);

        Assert.False(decoder.Error);
    }

    [Fact(DisplayName = "Opus范围编码器空流可初始化")]
    public void EmptyStream()
    {
        var encoder = new OpusRangeEncoder();
        var data = encoder.Finish();

        var decoder = new OpusRangeDecoder(data);
        Assert.Equal(0U, decoder.Decode(2));
        decoder.Update(0, 1, 2);
    }

    [Fact(DisplayName = "Opus范围编码器拒绝非法参数")]
    public void InvalidArguments()
    {
        var encoder = new OpusRangeEncoder();
        encoder.Encode(2, 1, 10);
        Assert.True(encoder.Error);
    }
}
