using System.Diagnostics;
using System.Text;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts;

/// <summary>输出类型</summary>
public enum TtsOutputKind
{
    /// <summary>裸 PCM，16 位有符号整数</summary>
    RawPcm = 1,

    /// <summary>WAV 容器，自动跳过文件头</summary>
    Wav = 2,
}

/// <summary>子进程语音合成引擎</summary>
/// <remarks>
/// 把 piper、espeak-ng 这类命令行合成器当作可替换的外部引擎。
/// 好处是零原生依赖、不引模型推理库，坏处是每次合成都要起进程——所以只适合中低频播报，
/// 高频场景应在外面套 <see cref="TtsPreGenerated"/> 或 <see cref="TtsCache"/>。
/// <para>
/// 进程的标准输出会被边读边送进 <see cref="ITtsSink"/>，因此支持真正的流式播放；
/// 单句文本建议控制在 40 字以内，否则首包延迟会变大。
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // piper：从标准输入读文本，输出 22.05kHz 裸 PCM
/// var piper = new ProcessTtsEngine
/// {
///     Name = "piper",
///     FileName = "piper",
///     Arguments = "--model zh_CN-huayan-medium.onnx --output-raw --sentence_silence 0.08",
///     SampleRate = 22050,
/// };
///
/// // espeak-ng：输出 WAV
/// var espeak = new ProcessTtsEngine
/// {
///     Name = "espeak",
///     FileName = "espeak-ng",
///     Arguments = "-v cmn --stdout {text}",
///     WriteTextToStdin = false,
///     OutputKind = TtsOutputKind.Wav,
/// };
/// </code>
/// </example>
public class ProcessTtsEngine : TtsEngineBase
{
    /// <summary>可执行文件路径或名称</summary>
    public String FileName { get; set; }

    /// <summary>命令行参数模板。支持 {text} {voice} {speed} {rate} {length_scale} 占位</summary>
    public String Arguments { get; set; } = "";

    /// <summary>是否把文本写入标准输入（为 true 时参数模板里不需要 {text}）</summary>
    public Boolean WriteTextToStdin { get; set; } = true;

    /// <summary>输出类型</summary>
    public TtsOutputKind OutputKind { get; set; } = TtsOutputKind.RawPcm;

    /// <summary>输出采样率。裸 PCM 时必须以实际输出为准</summary>
    public Int32 SampleRate { get; set; } = 22050;

    /// <summary>输出声道数</summary>
    public Int32 Channels { get; set; } = 1;

    /// <summary>是否把逗号类标点统一成句号</summary>
    /// <remarks>只认句末标点的引擎（典型如 piper）需要开启，否则中文逗号处不会有停顿。</remarks>
    public Boolean UnifyPunctuation { get; set; } = true;

    /// <summary>单次合成超时时间（秒）</summary>
    public Int32 TimeoutSeconds { get; set; } = 60;

    /// <summary>工作目录，可为 null</summary>
    public String WorkingDirectory { get; set; }

    /// <summary>初始化子进程合成引擎</summary>
    public ProcessTtsEngine()
    {
        Name = "process";
        Capabilities = TtsCapability.Offline | TtsCapability.Streaming;
        Quality = 40;
        RequiredMemoryBytes = 32L * 1024 * 1024;
    }

    /// <summary>初始化子进程合成引擎</summary>
    /// <param name="name">引擎名</param>
    /// <param name="fileName">可执行文件</param>
    /// <param name="arguments">参数模板</param>
    /// <param name="sampleRate">输出采样率</param>
    public ProcessTtsEngine(String name, String fileName, String arguments, Int32 sampleRate = 22050) : this()
    {
        Name = name;
        FileName = fileName;
        Arguments = arguments;
        SampleRate = sampleRate;
    }

    /// <summary>是否可用。可执行文件必须能找到</summary>
    public override Boolean IsAvailable => !FileName.IsNullOrEmpty() && ResolveExecutable(FileName) != null;

    /// <summary>合成语音</summary>
    /// <param name="request">合成请求</param>
    /// <param name="sink">分块输出目标，可为 null</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>合成结果</returns>
    public override async Task<TtsResult> SynthesizeAsync(TtsRequest request, ITtsSink sink, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        var exe = IsAvailable ? ResolveExecutable(FileName) : null;
        if (exe == null) throw new InvalidOperationException(L.T("Executable not found: {0}.", FileName));

        var format = new AudioFormat
        {
            SampleRate = request.Format?.SampleRate > 0 ? request.Format.SampleRate : SampleRate,
            Channels = Channels,
            BitsPerSample = 16,
        };

        var text = request.Text ?? "";
        if (UnifyPunctuation) text = TtsText.UnifyPunctuation(text);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = BuildArguments(request, text),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = WriteTextToStdin,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!WorkingDirectory.IsNullOrEmpty()) psi.WorkingDirectory = WorkingDirectory;

        var watch = Stopwatch.StartNew();
        var result = new TtsResult
        {
            Source = TtsSource.Engine,
            Engine = Name,
            Text = text,
            Format = format,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var buffer = sink == null ? null : new Byte[4096];
        var collected = sink == null ? new MemoryStream() : null;

        try
        {
            if (WriteTextToStdin)
            {
                var input = Encoding.UTF8.GetBytes(text + "\n");
                await process.StandardInput.BaseStream.WriteAsync(input, 0, input.Length, cancellationToken).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            // 错误输出必须并行读走，否则管道写满会让子进程卡死
            var errorTask = process.StandardError.ReadToEndAsync();

            if (sink != null)
            {
                sink.Open(format);
                if (OutputKind == TtsOutputKind.Wav)
                    await TtsWav.SkipHeaderAsync(process.StandardOutput.BaseStream, cancellationToken).ConfigureAwait(false);

                var stream = process.StandardOutput.BaseStream;
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    if (read <= 0) break;

                    sink.Write(buffer, 0, read);
                    result.PcmBytes += read;
                }
                sink.Close();
            }
            else
            {
                var stream = process.StandardOutput.BaseStream;
                if (OutputKind == TtsOutputKind.Wav)
                    await TtsWav.SkipHeaderAsync(stream, cancellationToken).ConfigureAwait(false);

                await stream.CopyToAsync(collected, 81920, cancellationToken).ConfigureAwait(false);
                result.Pcm = collected.ToArray();
                result.PcmBytes = result.Pcm.Length;
            }

            watch.Stop();
            result.ElapsedTime = watch.Elapsed.TotalSeconds;
            result.FirstChunkTime = result.ElapsedTime;

            var error = await errorTask.ConfigureAwait(false);
            if (result.PcmBytes == 0)
                throw new InvalidOperationException(L.T("TTS engine {0} produced no audio: {1}", Name, error));

            return result;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        catch
        {
            Kill(process);
            throw;
        }
        finally
        {
            collected?.Dispose();
        }
    }

    private String BuildArguments(TtsRequest request, String text)
    {
        var args = Arguments ?? "";
        var lengthScale = request.Speed > 0 ? (1.0 / request.Speed) : 1.0;

        args = args.Replace("{text}", Quote(text));
        args = args.Replace("{voice}", request.Voice ?? "");
        args = args.Replace("{speed}", request.Speed.ToString("0.###"));
        args = args.Replace("{rate}", SampleRate.ToString());
        args = args.Replace("{length_scale}", lengthScale.ToString("0.###"));

        return args;
    }

    private static String Quote(String text)
    {
        // 参数模板里的文本一律加引号，避免空格与标点被 shell 拆开
        var escaped = (text ?? "").Replace("\"", "\\\"");
        return "\"" + escaped + "\"";
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch
        {
            // 进程可能已退出，忽略
        }
    }

    /// <summary>在 PATH 与常见目录里查找可执行文件</summary>
    private static String ResolveExecutable(String fileName)
    {
        if (fileName.IsNullOrEmpty()) return null;
        if (File.Exists(fileName)) return fileName;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var sep = Path.PathSeparator;
        foreach (var dir in path.Split(sep))
        {
            if (dir.IsNullOrEmpty()) continue;

            var full = Path.Combine(dir, fileName);
            if (File.Exists(full)) return full;
        }

        return null;
    }
}
