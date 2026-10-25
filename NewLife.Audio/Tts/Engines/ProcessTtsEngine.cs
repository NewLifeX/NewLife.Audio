using System.Diagnostics;
using System.Runtime.CompilerServices;
using NewLife.Audio.DSP;
using NewLife.Audio.Tts.Advanced;

namespace NewLife.Audio.Tts;

/// <summary>子进程输出类型</summary>
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
/// 好处是零原生依赖、不引模型推理库，坏处是每次合成都要起进程——适合中低频播报，
/// 高频场景应在外层套 <see cref="Advanced.CachedTtsEngine"/>。
/// <para>标准输出会被边读边产出，因此支持真正的流式输出。</para>
/// </remarks>
/// <example>
/// <code>
/// var piper = new ProcessTtsEngine
/// {
///     Name = "piper",
///     FileName = "piper",
///     Arguments = "--model zh_CN-huayan-medium.onnx --output-raw --sentence_silence 0.08",
///     SampleRate = 22050,
///     Normalize = true,
/// };
/// var tts = new TtsClient(piper);
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

    /// <summary>是否在合成前做文本规范化</summary>
    /// <remarks>中文离线模型通常需要开启；英文模型建议关闭，避免空格被去掉。</remarks>
    public Boolean Normalize { get; set; }

    /// <summary>是否把逗号类标点统一成句号</summary>
    /// <remarks>只认句末标点的引擎（典型如 piper）需要开启，否则中文逗号处不会有停顿。</remarks>
    public Boolean UnifyPunctuation { get; set; } = true;

    /// <summary>单次合成超时时间（秒）</summary>
    public Int32 TimeoutSeconds { get; set; } = 60;

    /// <summary>工作目录，可为 null</summary>
    public String WorkingDirectory { get; set; }

    /// <summary>初始化子进程合成引擎</summary>
    public ProcessTtsEngine() : base("process") { }

    /// <summary>初始化子进程合成引擎</summary>
    /// <param name="name">引擎名</param>
    /// <param name="fileName">可执行文件</param>
    /// <param name="arguments">参数模板</param>
    /// <param name="sampleRate">输出采样率</param>
    public ProcessTtsEngine(String name, String fileName, String arguments, Int32 sampleRate = 22050) : base(name)
    {
        FileName = fileName;
        Arguments = arguments;
        SampleRate = sampleRate;
    }

    /// <summary>是否可用。可执行文件必须能找到</summary>
    public override Boolean IsAvailable => !FileName.IsNullOrEmpty() && TtsProcess.FindExecutable(FileName) != null;

    /// <summary>合成语音，逐块读出子进程标准输出</summary>
    /// <param name="text">待合成文本</param>
    /// <param name="options">合成选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>PCM 分块异步序列</returns>
    public override async IAsyncEnumerable<TtsChunk> SynthesizeAsync(String text, TtsOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var exe = IsAvailable ? TtsProcess.FindExecutable(FileName) : null;
        if (exe == null) throw new InvalidOperationException(L.T("Executable not found: {0}.", FileName));

        var content = text ?? "";
        if (Normalize) content = TtsText.Default.Normalize(content);
        if (UnifyPunctuation) content = TtsText.UnifyPunctuation(content);

        var psi = TtsProcess.CreateStartInfo(exe, BuildArguments(options, content), WriteTextToStdin, WorkingDirectory);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (TimeoutSeconds > 0) timeout.CancelAfter(TimeoutSeconds * 1000);
        var token = timeout.Token;

        using var process = new Process { StartInfo = psi };
        process.Start();
        try
        {
            if (WriteTextToStdin) await TtsProcess.WriteInputAsync(process, content, token).ConfigureAwait(false);

            // 错误输出必须并行读走，否则管道写满会让子进程卡死
            var errorTask = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.BaseStream;

            var format = new AudioFormat
            {
                SampleRate = SampleRate > 0 ? SampleRate : 22050,
                Channels = Channels > 0 ? Channels : 1,
                BitsPerSample = 16,
            };

            if (OutputKind == TtsOutputKind.Wav)
            {
                var info = await TtsWav.SkipHeaderAsync(output, token).ConfigureAwait(false);
                if (info != null)
                {
                    if (info.SampleRate > 0) format.SampleRate = info.SampleRate;
                    if (info.Channels > 0) format.Channels = info.Channels;
                    if (info.BitsPerSample > 0) format.BitsPerSample = info.BitsPerSample;
                }
            }

            var buffer = new Byte[8192];
            var total = 0L;
            while (true)
            {
                var read = await output.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                if (read <= 0) break;

                var data = new Byte[read];
                Buffer.BlockCopy(buffer, 0, data, 0, read);
                total += read;

                yield return new TtsChunk { Format = format, Data = data };
            }

            await TtsProcess.WaitForExitAsync(process, token).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (total <= 0)
                throw new InvalidOperationException(L.T("TTS engine {0} produced no audio: {1}", Name, error));
            if (process.ExitCode != 0)
                throw new InvalidOperationException(L.T("TTS engine {0} failed: {1}", Name, error));
        }
        finally
        {
            TtsProcess.Kill(process);
        }
    }

    private String BuildArguments(TtsOptions options, String text)
    {
        var args = Arguments ?? "";
        var rate = options?.Rate > 0 ? options.Rate : 1;
        var lengthScale = 1.0 / rate;

        args = args.Replace("{text}", TtsProcess.Quote(text));
        args = args.Replace("{voice}", options?.Voice ?? "");
        args = args.Replace("{speed}", rate.ToString("0.###"));
        args = args.Replace("{rate}", SampleRate.ToString());
        args = args.Replace("{length_scale}", lengthScale.ToString("0.###"));

        return args;
    }
}
