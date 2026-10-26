using System;
using NewLife.Audio;
using NewLife.Audio.Codecs;
using NewLife.Data;
using Xunit;

namespace XUnitTest.Codecs;

public class Mp3CodecTests
{
    private readonly Mp3Codec _codec = new();

    [Fact(DisplayName = "MP3编解码器注册信息正确")]
    public void CodecInfo_IsCorrect()
    {
        Assert.Equal("MP3 (MPEG Audio Layer III)", _codec.Name);
        Assert.Contains(AVTypes.MP3, _codec.SupportedTypes);
        Assert.Contains(AVTypes.MPEGAUDIO, _codec.SupportedTypes);
    }

    [Fact(DisplayName = "MP3编码数据以0xFF同步字开始")]
    public void Encode_StartsWithSyncWord()
    {
        var pcm = new Byte[1152 * 2];
        var encoded = _codec.FromPcm(pcm, null);
        Assert.True(encoded.Total >= 4);
        Assert.Equal(0xFF, encoded[0]);
        Assert.True((encoded[1] & 0xE0) == 0xE0);
    }

    [Fact(DisplayName = "Xing/Info 信息帧解码为静音而非杂音")]
    public void Decode_InfoFrame_IsSilent()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "mp3", "m1-mono-44k-320.mp3");
        var data = System.IO.File.ReadAllBytes(path);

        var pcm = _codec.ToPcm(data, null);
        var bytes = pcm.GetSpan().ToArray();
        Assert.True(bytes.Length >= 1152 * 2);

        // 该文件的 LAME Info 帧不承载音频，解码结果首帧必须是静音（且总采样数不变）
        for (var i = 0; i < 1152; i++)
        {
            var v = (Int16)(bytes[i * 2] | bytes[i * 2 + 1] << 8);
            Assert.Equal(0, v);
        }
    }

    [Fact(DisplayName = "MP3帧头解析MPEG1 Layer3 128kbps 44100Hz")]
    public void ParseHeader_DefaultFrame()
    {
        var header = new Byte[] { 0xFF, 0xFB, 0x90, 0x00 };
        var info = Mp3Codec.ParseFrameHeader(header, 0);
        Assert.NotNull(info);
        Assert.Equal(Mp3Codec.MpegVersion.Mpeg1, info.Version);
        Assert.Equal(3, info.Layer);
        Assert.Equal(44100, info.SampleRate);
    }

    [Fact(DisplayName = "MP3 IsMp3Frame有效帧返回true")]
    public void IsMp3Frame_ValidFrame_ReturnsTrue()
    {
        var frame = new Byte[] { 0xFF, 0xFB, 0x90, 0x00 };
        Assert.True(Mp3Codec.IsMp3Frame(frame, 0));
    }

    [Fact(DisplayName = "MP3非帧数据IsMp3Frame返回false")]
    public void IsMp3Frame_Invalid_ReturnsFalse()
    {
        var data = new Byte[] { 0x00, 0x01, 0x02, 0x03 };
        Assert.False(Mp3Codec.IsMp3Frame(data, 0));
    }

    [Fact(DisplayName = "MP3帧头保留采样率索引判定无效不抛异常")]
    public void ParseHeader_ReservedSampleRate_ReturnsNull()
    {
        // 0x9C 的采样率索引为 3（保留值），随机字节常命中该组合
        var frame = new Byte[] { 0xFF, 0xFB, 0x9C, 0x00, 0x00, 0x00 };

        Assert.Null(Mp3Codec.ParseFrameHeader(frame, 0));
        Assert.False(Mp3Codec.IsMp3Frame(frame, 0));
    }

    [Fact(DisplayName = "MP3标准静音帧解码为PCM")]
    public void Decode_SilentFrames_ReturnsPcm()
    {
        var codec = new Mp3Codec();
        var data = BuildSilentFrames(4);

        var pcm = codec.ToPcm(data, null);

        // 4 帧 × 1152 样本 × 2 声道 × 2 字节
        Assert.Equal(4 * 1152 * 2 * 2, pcm.Length);
    }

    [Fact(DisplayName = "MP3非音频数据解码返回空且不抛异常")]
    public void Decode_Garbage_ReturnsEmpty()
    {
        var codec = new Mp3Codec();
        var pcm = codec.ToPcm(new Byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }, null);

        Assert.Equal(0, pcm.Length);
    }

    /// <summary>构造标准 MPEG1 Layer III 静音帧（128kbps/44.1kHz/立体声，每帧 417 字节）</summary>
    private static Byte[] BuildSilentFrames(Int32 frames)
    {
        const Int32 frameSize = 417;
        var data = new Byte[frameSize * frames];
        for (var i = 0; i < frames; i++)
        {
            var offset = i * frameSize;
            data[offset] = 0xFF;
            data[offset + 1] = 0xFB;
            data[offset + 2] = 0x90;
            data[offset + 3] = 0x00;
        }

        return data;
    }
}
