using System;
using NewLife.Audio.Codecs;
using Xunit;

namespace XUnitTest.Codecs;

public class Mp3CrcTests
{
    [Fact(DisplayName = "开启 CRC 后帧头保护位为 0 且可正常解码")]
    public void Encode_UseCrc_Decodes()
    {
        var pcm = BuildSine(44100, 1, 0.2);
        var options = new Mp3EncoderOptions { SampleRate = 44100, Channels = 1, Bitrate = 128, UseCrc = true };
        var encoded = new Mp3Codec().FromPcm(pcm, options).GetSpan().ToArray();

        var offset = FindFrame(encoded, 0);
        Assert.True(offset >= 0);
        while (offset + 4 <= encoded.Length)
        {
            var header = Mp3Codec.ParseFrameHeader(encoded, offset);
            if (header == null) break;
            Assert.Equal(0, encoded[offset + 1] & 1);
            offset += header.FrameSize;
        }

        var decoded = new Mp3Codec().ToPcm(encoded, null).GetSpan().ToArray();
        Assert.True(decoded.Length > 0);
        Assert.True(MaxAbs(decoded) > 1000, "合法 CRC 码流被整体隐藏");
    }

    [Fact(DisplayName = "CRC 不匹配的帧被错误隐藏而不是中断解码")]
    public void Decode_CorruptCrc_Conceals()
    {
        var pcm = BuildSine(44100, 1, 0.2);
        var options = new Mp3EncoderOptions { SampleRate = 44100, Channels = 1, Bitrate = 128, UseCrc = true };
        var encoded = new Mp3Codec().FromPcm(pcm, options).GetSpan().ToArray();

        var first = FindFrame(encoded, 0);
        Assert.True(first >= 0);
        var header = Mp3Codec.ParseFrameHeader(encoded, first);
        Assert.NotNull(header);
        var second = FindFrame(encoded, first + header.FrameSize);
        Assert.True(second > first);

        var clean = new Mp3Codec().ToPcm(encoded, null).GetSpan().ToArray();
        encoded[second + 4] ^= 0xFF;

        var decoded = new Mp3Codec().ToPcm(encoded, null).GetSpan().ToArray();
        Assert.Equal(clean.Length, decoded.Length);
    }

    [Fact(DisplayName = "主数据损坏的帧被错误隐藏而不是中断解码")]
    public void Decode_CorruptPayload_Conceals()
    {
        var pcm = BuildSine(44100, 1, 0.2);
        var options = new Mp3EncoderOptions { SampleRate = 44100, Channels = 1, Bitrate = 128 };
        var encoded = new Mp3Codec().FromPcm(pcm, options).GetSpan().ToArray();

        var first = FindFrame(encoded, 0);
        var header = Mp3Codec.ParseFrameHeader(encoded, first);
        var second = FindFrame(encoded, first + header.FrameSize);
        Assert.True(second > first);

        var clean = new Mp3Codec().ToPcm(encoded, null).GetSpan().ToArray();
        // 破坏第二帧边信息里的 main_data_begin，触发比特池下溢
        encoded[second + 4] = 0xFF;

        var decoded = new Mp3Codec().ToPcm(encoded, null).GetSpan().ToArray();
        Assert.Equal(clean.Length, decoded.Length);
        Assert.True(MaxAbs(decoded) > 0, "坏帧被整体静音，未使用上一好帧隐藏");

        // 第二帧（MPEG-1 单声道 1152 样本 × 2 字节）应与未损坏解码不同
        var frameBytes = 1152 * 2;
        var cleanFrame = clean.AsSpan(frameBytes, frameBytes);
        var badFrame = decoded.AsSpan(frameBytes, frameBytes);
        Assert.False(cleanFrame.SequenceEqual(badFrame));
    }

    [Fact(DisplayName = "复杂帧借用前一帧比特池且往返解码正常")]
    public void Encode_Reservoir_UsesPriorSlackAndDecodes()
    {
        var pcm = BuildSine(44100, 1, 0.2);
        var options = new Mp3EncoderOptions { SampleRate = 44100, Channels = 1, Bitrate = 128 };
        var encoded = new Mp3Codec().FromPcm(pcm, options).GetSpan().ToArray();

        var offset = 0;
        var used = false;
        while (offset + 4 <= encoded.Length)
        {
            var header = Mp3Codec.ParseFrameHeader(encoded, offset);
            if (header == null) break;

            var sideOffset = offset + 4;
            var begin = (encoded[sideOffset] << 1) | (encoded[sideOffset + 1] >> 7);
            if (begin > 0) used = true;

            offset += header.FrameSize;
        }

        Assert.True(used, "编码器未使用比特池");
        var decoded = new Mp3Codec().ToPcm(encoded, null).GetSpan().ToArray();
        Assert.True(decoded.Length > 0);
    }

    [Fact(DisplayName = "MPEG-2 LSF 编码也能借用比特池")]
    public void Encode_LsfReservoir_Decodes()
    {
        var pcm = BuildSine(22050, 1, 0.3);
        var options = new Mp3EncoderOptions { SampleRate = 22050, Channels = 1, Bitrate = 64 };
        var encoded = new Mp3Codec().FromPcm(pcm, options).GetSpan().ToArray();

        var offset = 0;
        var used = false;
        while (offset + 4 <= encoded.Length)
        {
            var header = Mp3Codec.ParseFrameHeader(encoded, offset);
            if (header == null) break;

            if (encoded[offset + 4] > 0) used = true;
            offset += header.FrameSize;
        }

        Assert.True(used, "LSF 编码器未使用比特池");
        Assert.True(new Mp3Codec().ToPcm(encoded, null).GetSpan().Length > 0);
    }

    private static Int32 MaxAbs(Byte[] pcm)
    {
        var max = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var v = Math.Abs((Int16)(pcm[i] | pcm[i + 1] << 8));
            if (v > max) max = v;
        }

        return max;
    }

    private static Int32 FindFrame(Byte[] data, Int32 start)
    {
        for (var i = start; i + 4 <= data.Length; i++)
        {
            if (Mp3Codec.IsMp3Frame(data, i)) return i;
        }

        return -1;
    }

    private static Byte[] BuildSine(Int32 sampleRate, Int32 channels, Double seconds)
    {
        var count = (Int32)(sampleRate * seconds);
        var data = new Byte[count * channels * 2];
        for (var i = 0; i < count; i++)
        {
            var value = (Int16)(Math.Sin(2 * Math.PI * 1000 * i / sampleRate) * 12000);
            for (var ch = 0; ch < channels; ch++)
            {
                data[(i * channels + ch) * 2] = (Byte)(value & 0xFF);
                data[(i * channels + ch) * 2 + 1] = (Byte)((value >> 8) & 0xFF);
            }
        }

        return data;
    }
}
