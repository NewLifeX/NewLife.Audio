using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using NewLife.Audio.Codecs;
using NewLife.Audio.Tts;
using Xunit;

namespace XUnitTest.Tts;

/// <summary>在线 HTTP 引擎的 MP3 响应解码</summary>
public class TtsHttpEngineMp3Tests
{
    private static HttpTtsEngine Create(StubHandler stub, Action<HttpTtsEngine> configure = null)
    {
        var engine = new HttpTtsEngine
        {
            Name = "stub",
            Url = "http://localhost/tts",
            Client = new HttpClient(stub),
        };
        configure?.Invoke(engine);
        return engine;
    }

    private static Byte[] AsciiData(Int32 size)
    {
        var data = new Byte[size];
        for (var i = 0; i < size; i++) data[i] = (Byte)('a' + i % 26);
        return data;
    }

    private static Byte[] BuildMp3()
    {
        var codec = new Mp3Codec();
        var packet = codec.FromPcm(new Byte[1152 * 2], null);

        return packet.GetSpan().ToArray();
    }

    /// <summary>构造标准 MPEG1 Layer III 静音帧（128kbps/44.1kHz/立体声）</summary>
    private static Byte[] BuildSilentMp3(Int32 frames = 4)
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

    [Fact(DisplayName = "MP3 响应走解码而不是把 MP3 字节当 PCM 透传")]
    public async Task Mp3_DecodesInsteadOfPassthrough()
    {
        var mp3 = BuildMp3();
        var stub = new StubHandler(mp3, "audio/mpeg");
        var engine = Create(stub, e => e.ResponseKind = TtsResponseKind.Mp3);

        var (pcm, _) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(mp3.Length, pcm.Length);
    }

    [Fact(DisplayName = "按响应头自动识别 MP3 并跳过前置垃圾字节")]
    public async Task Mp3_AutoByContentType()
    {
        var mp3 = BuildMp3();
        var data = new Byte[16 + mp3.Length];
        Buffer.BlockCopy(mp3, 0, data, 16, mp3.Length);
        var stub = new StubHandler(data, "audio/mpeg");
        var engine = Create(stub);

        var (pcm, _) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(data.Length, pcm.Length);
    }

    [Fact(DisplayName = "标准 MP3 响应解码为 PCM 并回填实际格式")]
    public async Task Mp3_StandardFrames_DecodesToPcm()
    {
        var stub = new StubHandler(BuildSilentMp3(), "audio/mpeg");
        var engine = Create(stub, e => e.ResponseKind = TtsResponseKind.Mp3);

        var (pcm, format) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        // 4 帧 × 1152 样本 × 2 声道 × 2 字节
        Assert.Equal(4 * 1152 * 2 * 2, pcm.Length);
        Assert.Equal(44100, format.SampleRate);
        Assert.Equal(2, format.Channels);
    }

    [Fact(DisplayName = "显式指定裸 PCM 时 MP3 字节原样透传")]
    public async Task RawPcmKind_PassesBytesThrough()
    {
        var mp3 = BuildMp3();
        var stub = new StubHandler(mp3, "audio/mpeg");
        var engine = Create(stub, e => e.ResponseKind = TtsResponseKind.RawPcm);

        var (pcm, _) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(mp3, pcm);
    }

    [Fact(DisplayName = "非 MP3 数据按 MP3 解码时没有输出")]
    public async Task Mp3_Garbage_YieldsNothing()
    {
        var stub = new StubHandler(AsciiData(256), "application/octet-stream");
        var engine = Create(stub, e => e.ResponseKind = TtsResponseKind.Mp3);

        var (pcm, _) = await TtsChunkHelper.CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(pcm);
    }
}
