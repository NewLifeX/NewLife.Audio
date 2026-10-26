using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Audio.DSP;
using NewLife.Audio.Tts;
using Xunit;

namespace XUnitTest.Tts;

/// <summary>构造跨平台的“输出 stdin/stdout”子进程，用于真实执行子进程引擎</summary>
internal static class ShellTts
{
    public static Boolean IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static String Quote(String text) => "\"" + text + "\"";

    /// <summary>把文件内容原样写到标准输出的引擎</summary>
    public static ProcessTtsEngine FileEcho(String file, Int32 sampleRate = 16000, TtsOutputKind kind = TtsOutputKind.RawPcm)
    {
        var engine = IsWindows
            ? new ProcessTtsEngine("echo", "cmd", "/c type " + Quote(file))
            : new ProcessTtsEngine("echo", "cat", Quote(file));
        engine.WriteTextToStdin = false;
        engine.SampleRate = sampleRate;
        engine.OutputKind = kind;
        return engine;
    }

    /// <summary>回显标准输入的引擎，用于验证文本规范化后的实际输入</summary>
    public static ProcessTtsEngine StdinEcho()
    {
        // 管道里的中文必须字节透传：cmd/type/more 会按控制台代码页转换，PowerShell 原样读写流
        var engine = IsWindows
            ? new ProcessTtsEngine("echo", "powershell", "-NoProfile -NonInteractive -EncodedCommand " + ByteEchoCommand())
            : new ProcessTtsEngine("echo", "cat", "");
        engine.SampleRate = 16000;
        return engine;
    }

    private static String ByteEchoCommand()
    {
        const String script = "$b=[byte[]]::new(4096);$s=[Console]::OpenStandardInput();$o=[Console]::OpenStandardOutput();"
            + "while(($n=$s.Read($b,0,4096)) -gt 0){$o.Write($b,0,$n)}";

        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    /// <summary>执行指定脚本的引擎，两种平台脚本分别给出</summary>
    public static ProcessTtsEngine Command(String windows, String unix)
    {
        var engine = IsWindows
            ? new ProcessTtsEngine("cmd", "cmd", "/c " + windows)
            : new ProcessTtsEngine("sh", "sh", "-c " + Quote(unix));
        engine.WriteTextToStdin = false;
        return engine;
    }

    /// <summary>长时间不产出、不退出，用于取消与超时</summary>
    public static ProcessTtsEngine Block() => IsWindows
        ? Command("ping -n 6 127.0.0.1 >nul", "")
        : Command("", "sleep 5");
}

/// <summary>子进程引擎真实执行标准输出解析</summary>
public class ProcessTtsEngineExecutionTests
{
    private static Byte[] AsciiData(Int32 size)
    {
        var data = new Byte[size];
        for (var i = 0; i < size; i++) data[i] = (Byte)('a' + i % 26);
        return data;
    }

    private static async Task<(Byte[] Pcm, AudioFormat Format)> CollectAsync(ITtsEngine engine, String text, TtsOptions options = null, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream();
        AudioFormat format = null;
        await foreach (var chunk in engine.SynthesizeAsync(text, options, cancellationToken))
        {
            format ??= chunk.Format;
            stream.Write(chunk.Data, 0, chunk.Data.Length);
        }

        return (stream.ToArray(), format);
    }

    [Fact(DisplayName = "裸 PCM 输出按配置采样率逐块读回")]
    public async Task RawPcm_ReadsStdout()
    {
        using var dir = new TempDirectory();
        var file = Path.Combine(dir.Path, "voice.pcm");
        var data = AsciiData(640);
        File.WriteAllBytes(file, data);

        var engine = ShellTts.FileEcho(file, 22050);

        Assert.True(engine.IsAvailable);

        var (pcm, format) = await CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(data, pcm);
        Assert.Equal(22050, format.SampleRate);
        Assert.Equal(1, format.Channels);
        Assert.Equal(16, format.BitsPerSample);
    }

    [Fact(DisplayName = "WAV 输出自动剥离文件头并沿用头内格式")]
    public async Task Wav_SkipsHeader()
    {
        using var dir = new TempDirectory();
        var file = Path.Combine(dir.Path, "voice.wav");
        File.WriteAllBytes(file, TtsTestHelper.BuildWav(16000, 1600));

        var engine = ShellTts.FileEcho(file, 8000, TtsOutputKind.Wav);

        var (pcm, format) = await CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1600, pcm.Length);
        Assert.Equal(16000, format.SampleRate);
    }

    [Fact(DisplayName = "子进程没有输出时报错")]
    public async Task NoOutput_Throws()
    {
        var engine = ShellTts.Command("exit 3", "exit 3");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "子进程非零退出码时报错")]
    public async Task NonZeroExit_Throws()
    {
        var engine = ShellTts.Command("echo hi & exit /b 2", "echo hi; exit 2");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "参数模板占位符按选项替换")]
    public async Task Arguments_Placeholders()
    {
        var engine = ShellTts.IsWindows
            ? new ProcessTtsEngine("echo", "cmd", "/c echo {speed} {voice} {rate} {length_scale}") { WriteTextToStdin = false }
            : new ProcessTtsEngine("echo", "echo", "{speed} {voice} {rate} {length_scale}") { WriteTextToStdin = false };
        engine.SampleRate = 16000;

        var (pcm, _) = await CollectAsync(engine, "任意", new TtsOptions { Voice = "v1", Rate = 2f }, TestContext.Current.CancellationToken);

        Assert.Equal("2 v1 16000 0.5", Encoding.UTF8.GetString(pcm).Trim());
    }

    [Fact(DisplayName = "文本占位符带引号传入命令行")]
    public async Task Arguments_TextQuoted()
    {
        var engine = ShellTts.IsWindows
            ? new ProcessTtsEngine("echo", "cmd", "/c echo {text}") { WriteTextToStdin = false }
            : new ProcessTtsEngine("echo", "echo", "{text}") { WriteTextToStdin = false };

        var (pcm, _) = await CollectAsync(engine, "hello world", cancellationToken: TestContext.Current.CancellationToken);

        // cmd 的 echo 会把引号一起输出，Unix echo 直接输出参数
        Assert.Contains("hello world", Encoding.UTF8.GetString(pcm));
    }

    [Fact(DisplayName = "写标准输入前先做文本规范化")]
    public async Task TextToStdin_Normalized()
    {
        var engine = ShellTts.StdinEcho();
        engine.Normalize = true;
        engine.UnifyPunctuation = true;

        var (pcm, _) = await CollectAsync(engine, "A2，好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("诶二。好", Encoding.UTF8.GetString(pcm));
    }

    [Fact(DisplayName = "关闭规范化时文本原样写入标准输入")]
    public async Task TextToStdin_Raw()
    {
        var engine = ShellTts.StdinEcho();
        engine.Normalize = false;
        engine.UnifyPunctuation = false;

        var (pcm, _) = await CollectAsync(engine, "A2,好", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("A2,好", Encoding.UTF8.GetString(pcm));
    }

    [Fact(DisplayName = "工作目录传给子进程")]
    public async Task WorkingDirectory_Used()
    {
        var engine = ShellTts.IsWindows
            ? new ProcessTtsEngine("pwd", "cmd", "/c cd") { WriteTextToStdin = false }
            : new ProcessTtsEngine("pwd", "pwd", "") { WriteTextToStdin = false };

        using var dir = new TempDirectory("tts-cwd-");
        engine.WorkingDirectory = dir.Path;

        var (pcm, _) = await CollectAsync(engine, "任意", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(Path.GetFileName(dir.Path), Encoding.UTF8.GetString(pcm));
    }

    [Fact(DisplayName = "用户取消时结束子进程并抛取消异常")]
    public async Task Cancellation_KillsProcess()
    {
        var engine = ShellTts.Block();
        using var cts = new CancellationTokenSource(300);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CollectAsync(engine, "你好", cancellationToken: cts.Token));
    }

    [Fact(DisplayName = "超过单次合成超时时间抛取消异常")]
    public async Task Timeout_KillsProcess()
    {
        var engine = ShellTts.Block();
        engine.TimeoutSeconds = 1;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CollectAsync(engine, "你好", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "可执行文件名为空时引擎不可用")]
    public void EmptyFileName_Unavailable()
    {
        Assert.False(new ProcessTtsEngine().IsAvailable);
        Assert.False(new ProcessTtsEngine { FileName = "" }.IsAvailable);
    }
}
