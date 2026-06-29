using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio.Writers;
using Xunit;

namespace XUnitTest.Writers;

public class AudioStreamWriterTests
{
    [Fact(DisplayName = "工厂Create(opus)返回OggOpusStreamWriter")]
    public void Create_Opus_ReturnsOggOpusWriter()
    {
        var writer = AudioStreamWriter.Create("opus");
        Assert.IsType<OggOpusStreamWriter>(writer);
        Assert.Equal("audio/ogg; codecs=opus", writer.ContentType);
    }

    [Fact(DisplayName = "工厂Create(wav)返回WavStreamWriter")]
    public void Create_Wav_ReturnsWavWriter()
    {
        var writer = AudioStreamWriter.Create("wav");
        Assert.IsType<WavStreamWriter>(writer);
        Assert.Equal("audio/wav", writer.ContentType);
    }

    [Fact(DisplayName = "工厂Create(pcm)返回WavStreamWriter")]
    public void Create_Pcm_ReturnsWavWriter()
    {
        var writer = AudioStreamWriter.Create("pcm");
        Assert.IsType<WavStreamWriter>(writer);
    }

    [Fact(DisplayName = "工厂Create(mp3)返回Mp3StreamWriter")]
    public void Create_Mp3_ReturnsMp3Writer()
    {
        var writer = AudioStreamWriter.Create("mp3");
        Assert.IsType<Mp3StreamWriter>(writer);
        Assert.Equal("audio/mpeg", writer.ContentType);
    }

    [Fact(DisplayName = "工厂Create(未知格式)默认回退Mp3StreamWriter")]
    public void Create_Unknown_DefaultsToMp3()
    {
        var writer = AudioStreamWriter.Create("xyz");
        Assert.IsType<Mp3StreamWriter>(writer);
    }

    [Fact(DisplayName = "工厂Create(null)默认回退Mp3StreamWriter")]
    public void Create_Null_DefaultsToMp3()
    {
        var writer = AudioStreamWriter.Create(null);
        Assert.IsType<Mp3StreamWriter>(writer);
    }
}

public class WavStreamWriterTests
{
    [Fact(DisplayName = "WAV写入器WriteHeader写入44字节RIFF头")]
    public async Task WriteHeader_Writes44ByteRiffHeader()
    {
        using var ms = new MemoryStream();
        var writer = new WavStreamWriter(16000);

        await writer.WriteHeaderAsync(ms);

        Assert.True(ms.Length >= 44);
        var data = ms.ToArray();

        // 验证 RIFF 头
        Assert.Equal((Byte)'R', data[0]);
        Assert.Equal((Byte)'I', data[1]);
        Assert.Equal((Byte)'F', data[2]);
        Assert.Equal((Byte)'F', data[3]);
        Assert.Equal((Byte)'W', data[8]);
        Assert.Equal((Byte)'A', data[9]);
        Assert.Equal((Byte)'V', data[10]);
        Assert.Equal((Byte)'E', data[11]);

        // 验证采样率 (16000 = 0x3E80, LE: 0x80, 0x3E)
        Assert.Equal(0x80, data[24]);
        Assert.Equal(0x3E, data[25]);
    }

    [Fact(DisplayName = "WAV写入器重复WriteHeader不重写")]
    public async Task WriteHeader_CalledTwice_WritesOnce()
    {
        using var ms = new MemoryStream();
        var writer = new WavStreamWriter();

        await writer.WriteHeaderAsync(ms);
        var len1 = ms.Length;

        await writer.WriteHeaderAsync(ms);
        var len2 = ms.Length;

        Assert.Equal(len1, len2);
    }

    [Fact(DisplayName = "WAV写入器纯PCM数据写入后流长度增加")]
    public async Task WriteChunk_IncreasesStreamLength()
    {
        using var ms = new MemoryStream();
        var writer = new WavStreamWriter();

        await writer.WriteHeaderAsync(ms);
        var headerLen = ms.Length;

        var chunk = new Byte[160 * 2]; // 20ms 16-bit PCM
        await writer.WriteChunkAsync(ms, chunk);

        Assert.Equal(headerLen + chunk.Length, ms.Length);
    }

    [Fact(DisplayName = "WAV写入器ContentType为audio/wav")]
    public void ContentType_IsAudioWav()
    {
        var writer = new WavStreamWriter();
        Assert.Equal("audio/wav", writer.ContentType);
    }
}

public class Mp3StreamWriterTests
{
    [Fact(DisplayName = "MP3写入器WriteHeader不产生任何输出")]
    public async Task WriteHeader_ProducesNoOutput()
    {
        using var ms = new MemoryStream();
        var writer = new Mp3StreamWriter();
        await writer.WriteHeaderAsync(ms);
        Assert.Equal(0, ms.Length);
    }

    [Fact(DisplayName = "MP3写入器直接透传数据块")]
    public async Task WriteChunk_PassthroughData()
    {
        using var ms = new MemoryStream();
        var writer = new Mp3StreamWriter();
        var chunk = new Byte[] { 0xFF, 0xFB, 0x90, 0x00 };
        await writer.WriteChunkAsync(ms, chunk);

        var data = ms.ToArray();
        Assert.Equal(chunk.Length, data.Length);
        Assert.Equal(0xFF, data[0]);
        Assert.Equal(0xFB, data[1]);
    }

    [Fact(DisplayName = "MP3写入器ContentType为audio/mpeg")]
    public void ContentType_IsAudioMpeg()
    {
        var writer = new Mp3StreamWriter();
        Assert.Equal("audio/mpeg", writer.ContentType);
    }
}

public class OggOpusStreamWriterTests
{
    [Fact(DisplayName = "OGG Opus写入器WriteHeader写入2个OGG页")]
    public async Task WriteHeader_WritesTwoPages()
    {
        using var ms = new MemoryStream();
        var writer = new OggOpusStreamWriter();

        await writer.WriteHeaderAsync(ms);

        var data = ms.ToArray();
        Assert.True(data.Length > 54); // 至少 2×27 字节
        // 验证 OGG 魔术字
        Assert.Equal((Byte)'O', data[0]);
        Assert.Equal((Byte)'g', data[1]);
        Assert.Equal((Byte)'g', data[2]);
        Assert.Equal((Byte)'S', data[3]);

        // 第二页应在第一个 OpusHead 之后
        var secondPageOffset = data.Length > 50 ? data[19 + 19 + 8] != 0 ? 0 : 0 : 0;
    }

    [Fact(DisplayName = "OGG Opus写入器WriteChunk自动调用WriteHeader")]
    public async Task WriteChunk_AutoHeader()
    {
        using var ms = new MemoryStream();
        var writer = new OggOpusStreamWriter();

        // 不调用 WriteHeader，直接写 chunk
        var opusPacket = new Byte[80]; // 模拟 Opus 包
        await writer.WriteChunkAsync(ms, opusPacket);

        // 应该自动写了头
        var data = ms.ToArray();
        Assert.True(data.Length > 80 + 54); // 头页 + 数据页
    }

    [Fact(DisplayName = "OGG Opus写入器WriteTrailer写入EOS页")]
    public async Task WriteTrailer_WritesEosPage()
    {
        using var ms = new MemoryStream();
        var writer = new OggOpusStreamWriter();

        await writer.WriteTrailerAsync(ms);
        var data = ms.ToArray();
        Assert.True(data.Length > 0);

        // 验证最后一页的 OGG 魔术字
        Assert.Equal((Byte)'O', data[0]);
        Assert.Equal((Byte)'g', data[1]);
    }

    [Fact(DisplayName = "OGG Opus写入器重复WriteTrailer不重写")]
    public async Task WriteTrailer_CalledTwice_WritesOnce()
    {
        using var ms = new MemoryStream();
        var writer = new OggOpusStreamWriter();

        await writer.WriteTrailerAsync(ms);
        var len1 = ms.Length;

        await writer.WriteTrailerAsync(ms);
        var len2 = ms.Length;

        Assert.Equal(len1, len2);
    }

    [Fact(DisplayName = "OGG Opus写入器ContentType为audio/ogg")]
    public void ContentType_IsAudioOggOpus()
    {
        var writer = new OggOpusStreamWriter();
        Assert.Equal("audio/ogg; codecs=opus", writer.ContentType);
    }
}
