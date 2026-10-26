using System;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using Xunit;

namespace XUnitTest.Codecs;

public class OpusCodecTests
{
    [Fact(DisplayName = "Opus编解码器注册信息正确")]
    public void CodecInfo_IsCorrect()
    {
        using var codec = new OpusCodec();
        Assert.Contains("Opus", codec.Name);
        Assert.True(codec.IsStateful);
        Assert.Contains(AVTypes.Opus, codec.SupportedTypes);
        Assert.Contains(AVTypes.Transparent, codec.SupportedTypes);
    }

    [Fact(DisplayName = "Opus帧大小计算正确")]
    public void GetFrameSize_20ms()
    {
        Assert.Equal(960, OpusCodec.GetFrameSize(7));
        Assert.Equal(120, OpusCodec.GetFrameSize(0));
        Assert.Equal(480, OpusCodec.GetFrameSize(5));

        Assert.Equal(120, OpusCodec.GetFrameSize(OpusFrameDuration.Ms2_5));
        Assert.Equal(240, OpusCodec.GetFrameSize(OpusFrameDuration.Ms5));
        Assert.Equal(480, OpusCodec.GetFrameSize(OpusFrameDuration.Ms10));
        Assert.Equal(960, OpusCodec.GetFrameSize(OpusFrameDuration.Ms20));
        Assert.Equal(1920, OpusCodec.GetFrameSize(OpusFrameDuration.Ms40));
        Assert.Equal(2880, OpusCodec.GetFrameSize(OpusFrameDuration.Ms60));
    }

    [Fact(DisplayName = "Opus解码空数据返回空包")]
    public void ToPcm_EmptyData_ReturnsEmpty()
    {
        using var codec = new OpusCodec();
        var decoded = codec.ToPcm(Array.Empty<Byte>(), null);
        Assert.NotNull(decoded);
        Assert.True(decoded.Total == 0);
    }

    [Fact(DisplayName = "Opus非法包返回空")]
    public void ToPcm_InvalidPacket_ReturnsEmpty()
    {
        using var codec = new OpusCodec();
        var decoded = codec.ToPcm(new Byte[] { 0x03, 0x00, 0x00 }, null);
        Assert.True(decoded.Total == 0);
    }

    [Fact(DisplayName = "Opus非法选项抛出参数异常")]
    public void Options_Invalid_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusOptions { SampleRate = 44100 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusOptions { Channels = 3 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusOptions { Bitrate = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusOptions { Complexity = 11 }.Validate());
    }

    [Fact(DisplayName = "Opus采样率辅助方法正确")]
    public void SampleRateHelpers()
    {
        Assert.True(OpusOptions.IsSupportedSampleRate(48000));
        Assert.False(OpusOptions.IsSupportedSampleRate(44100));
        Assert.Equal(48000, OpusOptions.GetNearestSampleRate(44100));
        Assert.Equal(24000, OpusOptions.GetNearestSampleRate(22050));
        Assert.Equal(8000, OpusOptions.GetNearestSampleRate(4000));

        Assert.Equal(312, OpusCodec.GetLookahead(new OpusOptions { SampleRate = 48000 }));
        Assert.Equal(104, OpusCodec.GetLookahead(new OpusOptions { SampleRate = 16000 }));
    }

    [Fact(DisplayName = "Opus TOC配置映射到正确的模式带宽与帧长")]
    public void PacketParser_MapsConfig()
    {
        Assert.Equal(OpusMode.SilkOnly, ParseConfig(0).Mode);
        Assert.Equal(OpusBandwidth.Narrowband, ParseConfig(0).Bandwidth);
        Assert.Equal(OpusFrameDuration.Ms10, ParseConfig(0).FrameDuration);
        Assert.Equal(OpusFrameDuration.Ms60, ParseConfig(3).FrameDuration);

        Assert.Equal(OpusMode.Hybrid, ParseConfig(12).Mode);
        Assert.Equal(OpusBandwidth.SuperWideband, ParseConfig(12).Bandwidth);
        Assert.Equal(OpusMode.Hybrid, ParseConfig(15).Mode);
        Assert.Equal(OpusBandwidth.Fullband, ParseConfig(15).Bandwidth);

        Assert.Equal(OpusMode.CeltOnly, ParseConfig(16).Mode);
        Assert.Equal(OpusBandwidth.Narrowband, ParseConfig(16).Bandwidth);
        Assert.Equal(OpusFrameDuration.Ms2_5, ParseConfig(16).FrameDuration);
        Assert.Equal(OpusFrameDuration.Ms20, ParseConfig(31).FrameDuration);
        Assert.Equal(OpusBandwidth.Fullband, ParseConfig(31).Bandwidth);
    }

    [Fact(DisplayName = "Opus Code0单帧包解析正确")]
    public void PacketParser_Code0()
    {
        var data = new Byte[] { 0x00, 0xAA, 0xBB };
        Assert.True(OpusPacket.TryParse(data, out var packet));
        Assert.Equal(0, packet.Code);
        Assert.Equal(1, packet.FrameCount);
        Assert.Equal(2, packet.PayloadLength);
        Assert.Equal(2, packet.GetFrameLength(0));
        Assert.Equal(480, packet.TotalSamplesPerChannel);
        Assert.Equal(10, packet.DurationMs);
        Assert.Equal(new Byte[] { 0xAA, 0xBB }, packet.GetFrame(0).ToArray());
    }

    [Fact(DisplayName = "Opus Code1双帧CBR包解析正确")]
    public void PacketParser_Code1()
    {
        var data = new Byte[] { 0x01, 1, 2, 3, 4 };
        Assert.True(OpusPacket.TryParse(data, out var packet));
        Assert.Equal(1, packet.Code);
        Assert.Equal(2, packet.FrameCount);
        Assert.False(packet.IsVbr);
        Assert.Equal(2, packet.FrameLength);
        Assert.Equal(new Byte[] { 1, 2 }, packet.GetFrame(0).ToArray());
        Assert.Equal(new Byte[] { 3, 4 }, packet.GetFrame(1).ToArray());
    }

    [Fact(DisplayName = "Opus Code2双帧VBR包解析正确")]
    public void PacketParser_Code2()
    {
        var data = new Byte[] { 0x02, 2, 1, 2, 3, 4 };
        Assert.True(OpusPacket.TryParse(data, out var packet));
        Assert.Equal(2, packet.Code);
        Assert.Equal(2, packet.FrameCount);
        Assert.True(packet.IsVbr);
        Assert.Equal(2, packet.GetFrameLength(0));
        Assert.Equal(2, packet.GetFrameLength(1));
        Assert.Equal(new Byte[] { 1, 2 }, packet.GetFrame(0).ToArray());
        Assert.Equal(new Byte[] { 3, 4 }, packet.GetFrame(1).ToArray());
    }

    [Fact(DisplayName = "Opus Code3双帧CBR包解析正确")]
    public void PacketParser_Code3_Cbr()
    {
        var data = new Byte[] { 0x03, 0x02, 1, 2, 3, 4 };
        Assert.True(OpusPacket.TryParse(data, out var packet));
        Assert.Equal(3, packet.Code);
        Assert.Equal(2, packet.FrameCount);
        Assert.False(packet.IsVbr);
        Assert.Equal(2, packet.FrameLength);
        Assert.False(packet.HasPadding);
        Assert.Equal(new Byte[] { 3, 4 }, packet.GetFrame(1).ToArray());
    }

    [Fact(DisplayName = "Opus Code3 padding与VBR解析正确")]
    public void PacketParser_Code3_PaddingAndVbr()
    {
        var padded = new Byte[] { 0x03, 0x42, 0x01, 1, 2, 3, 4, 0 };
        Assert.True(OpusPacket.TryParse(padded, out var withPadding));
        Assert.True(withPadding.HasPadding);
        Assert.Equal(1, withPadding.Padding);
        Assert.Equal(4, withPadding.PayloadLength);
        Assert.Equal(new Byte[] { 3, 4 }, withPadding.GetFrame(1).ToArray());

        var vbr = new Byte[] { 0x03, 0x82, 0x01, 0xAA, 0xBB, 0xCC };
        Assert.True(OpusPacket.TryParse(vbr, out var variable));
        Assert.True(variable.IsVbr);
        Assert.Equal(1, variable.GetFrameLength(0));
        Assert.Equal(2, variable.GetFrameLength(1));
        Assert.Equal(new Byte[] { 0xAA }, variable.GetFrame(0).ToArray());
        Assert.Equal(new Byte[] { 0xBB, 0xCC }, variable.GetFrame(1).ToArray());
    }

    [Fact(DisplayName = "Opus包解析拒绝非法结构")]
    public void PacketParser_RejectsInvalid()
    {
        Assert.False(OpusPacket.TryParse(Array.Empty<Byte>(), out _));
        Assert.False(OpusPacket.TryParse(new Byte[1276], out _));
        Assert.False(OpusPacket.TryParse(new Byte[] { 0x03 }, out _));
        Assert.False(OpusPacket.TryParse(new Byte[] { 0x01, 1, 2, 3 }, out _));
        Assert.False(OpusPacket.TryParse(new Byte[] { 0x03, 0x00, 0x00 }, out _));
        Assert.False(OpusPacket.TryParse(new Byte[] { 0x02, 0xFF, 0x01 }, out _));
    }

    [Fact(DisplayName = "Opus包解析拒绝超过120毫秒的组合")]
    public void PacketParser_RejectsOver120Ms()
    {
        // config 3 = SILK NB 60ms，Code3 三帧 = 180ms
        Assert.False(OpusPacket.TryParse(new Byte[] { 0x1B, 0x03 }, out _));
    }

    [Fact(DisplayName = "Opus进程编解码往返保留PCM长度")]
    public void ProcessRoundTrip_Mono()
    {
        if (!OpusProcessCodec.IsAvailable) return;

        using var codec = new OpusCodec();
        var pcm = CreateSine(960);

        var encoded = codec.FromPcm(pcm, new OpusOptions { Bitrate = 64000 });
        Assert.True(encoded.Total > 0);
        Assert.True(OpusPacketHeader.TryParse(encoded.GetSpan(), out var header));
        Assert.Equal(960, header.TotalSamples);

        using var decoder = new OpusCodec();
        var decoded = decoder.ToPcm(encoded.GetSpan(), null);
        Assert.Equal(1920, decoded.Total);
    }

    [Fact(DisplayName = "Opus进程编解码立体声输出双声道")]
    public void ProcessRoundTrip_Stereo()
    {
        if (!OpusProcessCodec.IsAvailable) return;

        using var codec = new OpusCodec();
        var options = new OpusOptions { Channels = 2, Bitrate = 96000 };
        var pcm = CreateSine(960, channels: 2);

        var encoded = codec.FromPcm(pcm, options);
        Assert.True(OpusPacketHeader.TryParse(encoded.GetSpan(), out var header));
        Assert.Equal(2, header.Channels);

        var decoded = codec.ToPcm(encoded.GetSpan(), options);
        Assert.Equal(960 * 2 * 2, decoded.Total);
    }

    [Theory(DisplayName = "Opus进程编码支持全部合法帧长")]
    [InlineData(OpusFrameDuration.Ms2_5)]
    [InlineData(OpusFrameDuration.Ms5)]
    [InlineData(OpusFrameDuration.Ms10)]
    [InlineData(OpusFrameDuration.Ms20)]
    [InlineData(OpusFrameDuration.Ms40)]
    [InlineData(OpusFrameDuration.Ms60)]
    public void ProcessEncode_AllFrameDurations(OpusFrameDuration duration)
    {
        if (!OpusProcessCodec.IsAvailable) return;

        using var codec = new OpusCodec();
        var options = new OpusOptions { FrameDuration = duration };
        var pcm = CreateSine(options.SamplesPerFrame);

        var encoded = codec.FromPcm(pcm, options);
        Assert.True(OpusPacketHeader.TryParse(encoded.GetSpan(), out var header));
        Assert.Equal(options.SamplesPerFrame, header.TotalSamples);

        var decoded = codec.ToPcm(encoded.GetSpan(), options);
        Assert.Equal(options.SamplesPerFrame * 2, decoded.Total);
    }

    [Theory(DisplayName = "Opus进程编码支持全部合法采样率")]
    [InlineData(8000)]
    [InlineData(12000)]
    [InlineData(16000)]
    [InlineData(24000)]
    [InlineData(48000)]
    public void ProcessEncode_AllSampleRates(Int32 sampleRate)
    {
        if (!OpusProcessCodec.IsAvailable) return;

        using var codec = new OpusCodec();
        var options = new OpusOptions { SampleRate = sampleRate };
        var pcm = CreateSine(options.SamplesPerFrame, sampleRate);

        var encoded = codec.FromPcm(pcm, options);
        Assert.True(OpusPacketHeader.TryParse(encoded.GetSpan(), out var header));
        Assert.Equal(960, header.TotalSamples);

        var decoded = codec.ToPcm(encoded.GetSpan(), options);
        Assert.Equal(options.SamplesPerFrame * 2, decoded.Total);
    }

    [Fact(DisplayName = "Opus进程编码连续PCM返回多包")]
    public void ProcessEncode_MultipleFrames_ReturnsPackets()
    {
        if (!OpusProcessCodec.IsAvailable) return;

        using var codec = new OpusCodec();
        var pcm = CreateSine(960 * 3);

        var packets = codec.Encode(pcm);
        Assert.Equal(3, packets.Count);

        foreach (var packet in packets)
        {
            Assert.True(OpusPacketHeader.TryParse(packet.GetSpan(), out var header));
            Assert.Equal(960, header.TotalSamples);
        }
    }

    [Fact(DisplayName = "Opus进程编码不足一帧时可选择flush补静音")]
    public void ProcessEncode_PartialFrame_FlushPads()
    {
        if (!OpusProcessCodec.IsAvailable) return;

        using var codec = new OpusCodec();
        var pcm = CreateSine(960 + 480);

        var packets = codec.Encode(pcm);
        Assert.Single(packets);

        var flushed = codec.Encode(Array.Empty<Byte>(), flush: true);
        Assert.Single(flushed);
    }

    [Fact(Skip = "M4: 托管 SILK 解码器完成后启用 FEC 测试")]
    public void DecodeFec_RecoversLostFrame() { }

    [Fact(Skip = "M5: 托管 SILK 编码器完成后启用 DTX 测试")]
    public void Encode_Dtx_ProducesSmallPacket() { }

    [Fact(Skip = "M4: 托管 SILK 解码器完成后启用 PLC 测试")]
    public void DecodePacketLoss_ProducesFrame() { }

    private static OpusPacket ParseConfig(Int32 config) => OpusPacket.Parse(new Byte[] { (Byte)(config << 3), 0x00 });

    private static Byte[] CreateSine(Int32 samples, Int32 sampleRate = 48000, Int32 channels = 1, Double frequency = 1000)
    {
        var pcm = new Byte[samples * channels * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (Int16)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * 16000);
            for (var ch = 0; ch < channels; ch++)
            {
                pcm[(i * channels + ch) * 2] = (Byte)(value & 0xFF);
                pcm[(i * channels + ch) * 2 + 1] = (Byte)((value >> 8) & 0xFF);
            }
        }

        return pcm;
    }
}