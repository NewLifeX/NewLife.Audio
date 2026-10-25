using System.Diagnostics;
using System.Runtime.CompilerServices;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts;

/// <summary>Linux/macOS 命令行合成引擎（espeak-ng）</summary>
/// <remarks>
/// <c>espeak-ng</c> 通常由系统包管理器安装。直接播报时由 espeak 自己输出到声卡；
/// 合成时使用 <c>--stdout</c> 输出 WAV，本引擎剥掉文件头后逐块产出 PCM。
/// </remarks>
public sealed class EspeakTtsEngine : TtsEngineBase, ITtsSpeaker
{
    private readonly Object _sync = new();
    private Process _current;

    /// <summary>可执行文件名。默认依次查找 espeak-ng、espeak</summary>
    public String FileName { get; set; }

    /// <summary>默认语速（词/分钟），默认 175</summary>
    public Int32 WordsPerMinute { get; set; } = 175;

    /// <summary>默认音量（0~200），默认 100</summary>
    public Int32 Amplitude { get; set; } = 100;

    /// <summary>初始化 espeak 引擎</summary>
    public EspeakTtsEngine() : base("espeak") { }

    /// <summary>当前是否可用。找到可执行文件即可用</summary>
    public override Boolean IsAvailable => ResolveExe() != null;

    /// <summary>枚举 espeak 音色</summary>
    /// <returns>音色列表</returns>
    public override IReadOnlyList<TtsVoice> GetVoices()
    {
        var exe = ResolveExe();
        if (exe == null) return Array.Empty<TtsVoice>();

        var list = new List<TtsVoice>();
        try
        {
            using var process = new Process
            {
                StartInfo = TtsProcess.CreateStartInfo(exe, "--voices", false),
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            foreach (var line in output.Split('\n'))
            {
                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;
                if (parts[0] == "Pty") continue;

                list.Add(new TtsVoice
                {
                    Name = parts[3],
                    Culture = parts[1],
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
        var exe = ResolveExe() ?? throw new InvalidOperationException(L.T("espeak-ng was not found. Install it with: apt install espeak-ng"));

        using var process = new Process
        {
            StartInfo = TtsProcess.CreateStartInfo(exe, BuildArguments(options, false), true),
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
                throw new InvalidOperationException(L.T("espeak-ng failed: {0}", error));
        }
        finally
        {
            lock (_sync) _current = null;
        }
    }

    /// <summary>合成语音，逐块读取标准输出</summary>
    /// <param name="text">待合成文本</param>
    /// <param name="options">合成选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>PCM 分块异步序列</returns>
    public override async IAsyncEnumerable<TtsChunk> SynthesizeAsync(String text, TtsOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var exe = ResolveExe() ?? throw new InvalidOperationException(L.T("espeak-ng was not found. Install it with: apt install espeak-ng"));

        using var process = new Process
        {
            StartInfo = TtsProcess.CreateStartInfo(exe, BuildArguments(options, true), true),
        };

        lock (_sync) _current = process;
        try
        {
            process.Start();

            var errorTask = process.StandardError.ReadToEndAsync();
            var inputTask = TtsProcess.WriteInputAsync(process, text, cancellationToken);
            var output = process.StandardOutput.BaseStream;

            var info = await TtsWav.SkipHeaderAsync(output, cancellationToken).ConfigureAwait(false);
            var format = new AudioFormat
            {
                SampleRate = info?.SampleRate > 0 ? info.SampleRate : 22050,
                Channels = info?.Channels > 0 ? info.Channels : 1,
                BitsPerSample = info?.BitsPerSample > 0 ? info.BitsPerSample : 16,
            };

            var buffer = new Byte[8192];
            var total = 0L;
            while (true)
            {
                var read = await output.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;

                var data = new Byte[read];
                Buffer.BlockCopy(buffer, 0, data, 0, read);
                total += read;

                yield return new TtsChunk { Format = format, Data = data };
            }

            await inputTask.ConfigureAwait(false);
            await TtsProcess.WaitForExitAsync(process, cancellationToken).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (total <= 0)
                throw new InvalidOperationException(L.T("espeak-ng produced no audio: {0}", error));
        }
        finally
        {
            lock (_sync) _current = null;
            TtsProcess.Kill(process);
        }
    }

    /// <summary>停止当前合成</summary>
    public void Stop()
    {
        Process process;
        lock (_sync) process = _current;
        if (process != null) TtsProcess.Kill(process);
    }

    private String ResolveExe()
    {
        if (!FileName.IsNullOrEmpty()) return TtsProcess.FindExecutable(FileName);

        return TtsProcess.FindExecutable("espeak-ng") ?? TtsProcess.FindExecutable("espeak");
    }

    private String BuildArguments(TtsOptions options, Boolean toStdout)
    {
        var args = toStdout ? "--stdout --stdin" : "--stdin";

        if (options != null && !options.Voice.IsNullOrEmpty())
        {
            // espeak 允许 "语言+变体"，也允许直接传音色名
            args += " -v " + TtsProcess.Quote(options.Voice);
        }

        var rate = options != null && options.Rate > 0 ? options.Rate : 1;
        var wpm = (Int32)Math.Round(WordsPerMinute * rate);
        if (wpm < 80) wpm = 80;
        if (wpm > 500) wpm = 500;
        args += " -s " + wpm;

        var volume = options?.Volume ?? 1;
        if (volume < 0) volume = 0;
        if (volume > 1) volume = 1;
        var amplitude = (Int32)Math.Round(Amplitude * volume);
        if (amplitude < 0) amplitude = 0;
        if (amplitude > 200) amplitude = 200;
        args += " -a " + amplitude;

        return args;
    }
}
