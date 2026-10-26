using System;
using System.IO;
using System.Threading.Tasks;
using NewLife.Audio;
using NewLife.Audio.Tts;
using Xunit;

namespace XUnitTest.Tts;

/// <summary>TTS 压缩输出（MP3/FLAC）门禁</summary>
/// <remarks>
/// 用静音占位引擎产出确定时长的 PCM，走 SaveAsync 落盘后再按容器解回来比对时长与字节数，
/// 同时校验容器魔数，确保压缩路径没有写坏文件头。
/// </remarks>
public class TtsCompressedOutputTests
{
    private static TtsClient CreateClient()
    {
        var client = new TtsClient(new NullTtsEngine { SecondsPerChar = 0.18, SampleRate = 16000 });
        return client;
    }

    [Theory(DisplayName = "保存为 MP3/FLAC 后可按容器解回相同时长")]
    [InlineData(".mp3")]
    [InlineData(".flac")]
    public async Task Save_Compressed_ProducesDecodableFile(String extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nlaudio-tts-{Guid.NewGuid():N}{extension}");
        try
        {
            const String text = "设备A2温度过高";
            using var client = CreateClient();
            var result = await client.SaveAsync(text, path);

            Assert.True(result.Bytes > 0);
            Assert.Equal(path, result.OutputPath);
            var file = new FileInfo(path);
            Assert.True(file.Exists && file.Length > 0, "输出文件为空");

            // 容器魔数：MP3 以 ID3v2 或帧同步开头，FLAC 以 fLaC 开头
            using (var fs = File.OpenRead(path))
            {
                var head = new Byte[4];
                fs.ReadExactly(head);
                if (extension == ".flac")
                    Assert.Equal("fLaC", System.Text.Encoding.ASCII.GetString(head));
                else
                {
                    // 有标签时以 ID3v2 开头，无标签时直接以 MPEG 帧同步（11 位全 1）开头
                    var id3 = head[0] == 0x49 && head[1] == 0x44 && head[2] == 0x33;
                    var sync = head[0] == 0xFF && (head[1] & 0xE0) == 0xE0;
                    Assert.True(id3 || sync, $"非预期 MP3 文件头 {head[0]:X2}{head[1]:X2}{head[2]:X2}");
                }
            }

            // 解码回来验证时长：每秒字符数 × 字数，允许一个编码帧的误差
            var pcm = AudioFile.Open(path).ReadPcm();
            var expected = text.Length * 0.18;
            Assert.True(Math.Abs(pcm.Duration - expected) < 0.1, $"时长偏差过大：实际 {pcm.Duration:F3}s，期望 {expected:F3}s");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact(DisplayName = "保存为压缩格式时输出流保持打开")]
    public async Task Save_Compressed_KeepsStreamOpen()
    {
        using var client = CreateClient();
        using var ms = new MemoryStream();
        var result = await client.SaveAsync("测试", ms, TtsFormat.Mp3);

        Assert.True(result.Bytes > 0);
        Assert.True(ms.CanWrite);
        Assert.True(ms.Length > 0);
    }

    [Fact(DisplayName = "不支持的扩展名给出明确异常")]
    public async Task Save_UnsupportedExtension_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nlaudio-tts-{Guid.NewGuid():N}.ogg");
        using var client = CreateClient();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => client.SaveAsync("测试", path));
        Assert.Contains(".mp3", ex.Message);
        Assert.False(File.Exists(path));
    }
}
