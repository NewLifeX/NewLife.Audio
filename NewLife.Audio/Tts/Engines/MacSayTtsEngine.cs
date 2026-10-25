using System.Diagnostics;
using System.Runtime.CompilerServices;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts;

/// <summary>macOS 系统语音引擎（say）</summary>
/// <remarks>
/// 直接播报交给 <c>/usr/bin/say</c>；合成时先写到临时 WAV，再读出 PCM 块。
/// macOS 的 <c>say</c> 不支持标准输出音频，因此流式场景是“整段生成后产出”，不是实时增量。
/// </remarks>
public sealed class MacSayTtsEngine : TtsEngineBase, ITtsSpeaker
{
    private const String SayPath = "/usr/bin/say";

    private readonly Object _sync = new();
    private Process _current;

    /// <summary>默认语速（词/分钟），默认 175</summary>
    public Int32 WordsPerMinute { get; set; } = 175;

    /// <summary>输出采样率</summary>
    public Int32 SampleRate { get; set; } = 22050;

    /// <summary>初始化 macOS say 引擎</summary>
    public MacSayTtsEngine() : base("say") { }

    /// <summary>当前是否可用</summary>
    public override Boolean IsAvailable => TtsPlatform.IsMacOS && File.Exists(SayPath);

    /// <summary>枚举系统音色</summary>
    /// <returns>音色列表</returns>
    public override IReadOnlyList<TtsVoice> GetVoices()
    {
        if (!IsAvailable) return Array.Empty<TtsVoice>();

        var list = new List<TtsVoice>();
        try
        {
            using var process = new Process
            {
                StartInfo = TtsProcess.CreateStartInfo(SayPath, "-v ?", false),
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            foreach (var line in output.Split('\n'))
            {
                if (line.IsNullOrEmpty()) continue;

                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                if (parts[0].StartsWith("#")) continue;
                if (parts[0].Equals("Name", StringComparison.OrdinalIgnoreCase)) continue;

                list.Add(new TtsVoice
                {
                    Name = parts[0],
                    Culture = parts[1].Replace('_', '-'),
                });
            }
        }
        catch
        {
            // 枚举失败按无音色处理
        }

        return list;
    }

    /// <summary>直接播报</summary>
    /// <param name="text">待播报文本</param>
    /// <param name="options">合成选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>播报任务</returns>
    public async Task SpeakAsync(String text, TtsOptions options, CancellationToken cancellationToken)
    {
        if (!IsAvailable) throw new PlatformNotSupportedException(L.T("The macOS say command is not available."));

        using var process = new Process
        {
            StartInfo = TtsProcess.CreateStartInfo(SayPath, BuildArguments(options, null), true),
        };

        lock (_sync) _current = process;
        try
        {
            process.Start();
            await TtsProcess.WriteInputAsync(process, text, cancellationToken).ConfigureAwait(false);

            var errorTask = process.StandardError.ReadToEndAsync();
            await TtsProcess.WaitForExitAsync(process, cancellationToken).ConfigureAwait(false);

            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(L.T("say failed: {0}", error));
        }
        finally
        {
            lock (_sync) _current = null;
        }
    }

    /// <summary>合成语音</summary>
    /// <param name="text">待合成文本</param>
    /// <param name="options">合成选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>PCM 分块异步序列</returns>
    public override async IAsyncEnumerable<TtsChunk> SynthesizeAsync(String text, TtsOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!IsAvailable) throw new PlatformNotSupportedException(L.T("The macOS say command is not available."));

        var file = Path.Combine(Path.GetTempPath(), "NewLife.Audio.tts." + Guid.NewGuid().ToString("N") + ".wav");
        using var process = new Process
        {
            StartInfo = TtsProcess.CreateStartInfo(SayPath, BuildArguments(options, file), true),
        };

        lock (_sync) _current = process;
        try
        {
            process.Start();
            await TtsProcess.WriteInputAsync(process, text, cancellationToken).ConfigureAwait(false);

            var errorTask = process.StandardError.ReadToEndAsync();
            await TtsProcess.WaitForExitAsync(process, cancellationToken).ConfigureAwait(false);

            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(L.T("say failed: {0}", error));

            var data = File.ReadAllBytes(file);
            var info = TtsWav.TryParse(data, out var wav) ? wav : null;
            var format = new AudioFormat
            {
                SampleRate = info?.SampleRate > 0 ? info.SampleRate : SampleRate,
                Channels = info?.Channels > 0 ? info.Channels : 1,
                BitsPerSample = info?.BitsPerSample > 0 ? info.BitsPerSample : 16,
            };
            var pcm = info != null && wav.DataSize > 0 && wav.DataOffset + wav.DataSize <= data.Length
                ? new ReadOnlySpan<Byte>(data, wav.DataOffset, wav.DataSize).ToArray()
                : data;

            if (pcm.Length > 0) yield return new TtsChunk { Format = format, Data = pcm };
        }
        finally
        {
            lock (_sync) _current = null;
            try { File.Delete(file); } catch { /* 临时文件可能已删除 */ }
        }
    }

    /// <summary>停止当前合成</summary>
    public void Stop()
    {
        Process process;
        lock (_sync) process = _current;
        if (process != null) TtsProcess.Kill(process);
    }

    private String BuildArguments(TtsOptions options, String outputFile)
    {
        var args = "";

        if (options != null && !options.Voice.IsNullOrEmpty()) args += "-v " + TtsProcess.Quote(options.Voice) + " ";

        var rate = options != null && options.Rate > 0 ? options.Rate : 1;
        var wpm = (Int32)Math.Round(WordsPerMinute * rate);
        if (wpm < 80) wpm = 80;
        if (wpm > 500) wpm = 500;
        args += "-r " + wpm + " ";

        if (!outputFile.IsNullOrEmpty())
        {
            args += "-o " + TtsProcess.Quote(outputFile);
            args += " --file-format=WAVE --data-format=LEI16@" + (SampleRate > 0 ? SampleRate : 22050);
        }

        args += " -f -";
        return args;
    }
}
