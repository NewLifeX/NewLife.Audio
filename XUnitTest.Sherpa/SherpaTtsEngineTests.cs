using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NewLife.Audio.Tts;
using NewLife.Audio.Tts.Sherpa;
using Xunit;

namespace XUnitTest.Sherpa;

/// <summary>常驻离线引擎契约</summary>
public class SherpaTtsEngineTests
{
    private static SherpaTtsModel MissingModel() => new()
    {
        Name = "missing",
        Kind = SherpaTtsKind.Vits,
        Model = Path.Combine(Path.GetTempPath(), "sherpa-missing-" + Guid.NewGuid().ToString("N") + ".onnx"),
    };

    [Fact(DisplayName = "sherpa：模型为空直接拒绝")]
    public void Ctor_NullModel_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SherpaTtsEngine(null));
    }

    [Fact(DisplayName = "sherpa：模型文件缺失时不可用")]
    public void IsAvailable_MissingFile_False()
    {
        using var engine = new SherpaTtsEngine(MissingModel());

        Assert.False(engine.IsAvailable);
        Assert.Empty(engine.GetVoices());
        Assert.Contains("missing", engine.Name);
    }

    [Fact(DisplayName = "sherpa：无文本前端时不可用并说明原因")]
    public void IsAvailable_Unsupported_False()
    {
        var model = MissingModel();
        model.Unsupported = "no lexicon or FST frontend";
        File.WriteAllBytes(model.Model, new Byte[16]);
        try
        {
            using var engine = new SherpaTtsEngine(model);
            Assert.False(engine.IsAvailable);
        }
        finally
        {
            try { File.Delete(model.Model); } catch { /* 忽略 */ }
        }
    }

    [Fact(DisplayName = "sherpa：不可用时合成抛 NotSupportedException")]
    public async Task Unavailable_Synthesize_Throws()
    {
        using var engine = new SherpaTtsEngine(MissingModel());

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => CollectAsync(engine, "你好"));

        Assert.Contains("missing", ex.Message);
    }

    [Fact(DisplayName = "sherpa：float 转 PCM 裁剪越界并输出小端")]
    public void ToPcm16_ClampsAndLittleEndian()
    {
        var pcm = SherpaTtsEngine.ToPcm16([0f, 1f, -1f, 2f, -2f]);

        Assert.Equal(10, pcm.Length);
        Assert.Equal(new Byte[] { 0x00, 0x00, 0xFF, 0x7F, 0x01, 0x80, 0xFF, 0x7F, 0x01, 0x80 }, pcm);
    }

    [Fact(DisplayName = "sherpa：空采样不产出字节")]
    public void ToPcm16_Empty()
    {
        Assert.Empty(SherpaTtsEngine.ToPcm16((Single[])null));
        Assert.Empty(SherpaTtsEngine.ToPcm16(Array.Empty<Single>()));
        Assert.Empty(SherpaTtsEngine.ToPcm16(IntPtr.Zero, 10));
        Assert.Empty(SherpaTtsEngine.ToPcm16(IntPtr.Zero, 0));
    }

    [Fact(DisplayName = "sherpa：Kokoro 音色无需加载模型即可枚举")]
    public void GetVoices_Kokoro_WithoutLoad()
    {
        var root = Path.Combine(Path.GetTempPath(), "sherpa-kokoro-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "kokoro-int8-multi-lang-v1_1");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "model.onnx"), new Byte[1024]);
        File.WriteAllText(Path.Combine(dir, "tokens.txt"), "a 0");
        File.WriteAllBytes(Path.Combine(dir, "voices.bin"), new Byte[128]);

        try
        {
            var model = Assert.Single(SherpaTtsCatalog.Discover(root));
            using var engine = new SherpaTtsEngine(model);

            var voices = engine.GetVoices();

            Assert.Equal(103, voices.Count);
            Assert.Equal("zf_001", voices[3].Name);
            Assert.False(engine.Loaded);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 忽略 */ }
        }
    }

    [Fact(DisplayName = "sherpa：磁盘模型可用时真实合成")]
    public async Task Synthesize_WhenModelInstalled()
    {
        // 模型体积较大不入库；本机设置 TTS_MODELS（或默认目录）后才执行真实合成
        var models = SherpaTtsCatalog.Discover(SherpaTtsCatalog.DefaultRoot).Where(e => e.CanLoad).ToList();
        if (models.Count == 0) return;

        using var engine = new SherpaTtsEngine(models[0], threads: 2);
        if (!engine.IsAvailable) return;

        var pcm = await CollectAsync(engine, "你好");

        Assert.NotEmpty(pcm);
        Assert.True(engine.Loaded);
        Assert.True(engine.LoadTime >= 0);
    }

    private static async Task<Byte[]> CollectAsync(ITtsEngine engine, String text)
    {
        using var stream = new MemoryStream();
        await foreach (var chunk in engine.SynthesizeAsync(text, null, TestContext.Current.CancellationToken))
            stream.Write(chunk.Data, 0, chunk.Data.Length);

        return stream.ToArray();
    }
}
