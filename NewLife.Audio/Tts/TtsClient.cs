using System.Diagnostics;
using System.Runtime.CompilerServices;
using NewLife.Audio.Devices;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts;

/// <summary>语音合成客户端。极简播报、文件保存与流式输出的统一入口</summary>
/// <remarks>
/// 默认按平台自动选择系统语音：Windows 用 SAPI，macOS 用 <c>say</c>，Linux 用 <c>espeak-ng</c>。
/// 需要接入在线服务、piper 或自定义模型时，把对应的 <see cref="ITtsEngine"/> 通过构造函数注入即可。
/// <para>三个主入口都是异步方法，最后一个参数统一为 <see cref="CancellationToken"/>：</para>
/// <list type="bullet">
/// <item><see cref="SayAsync"/>：直接播报，播完/取消/<see cref="Stop"/> 时任务完成。</item>
/// <item><see cref="SaveAsync(String, String, TtsOptions, CancellationToken)"/>：保存为 WAV/PCM/MP3/FLAC 文件。</item>
/// <item><see cref="StreamAsync"/>：逐块产出 PCM，适合 HTTP/WebSocket 边合成边发送。</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// var tts = new TtsClient();
/// await tts.SayAsync("设备A2温度过高，请检查");
///
/// await tts.SaveAsync("设备A2温度过高", "alert.wav");
///
/// await foreach (var chunk in tts.StreamAsync("系统已启动"))
///     await response.Body.WriteAsync(chunk.Data.AsMemory());
/// </code>
/// </example>
public sealed class TtsClient : IDisposable
{
    private static readonly Lazy<TtsClient> _default = new(() => new TtsClient());
    private CancellationTokenSource _speakCts;
    private ITtsSpeaker _speaker;
    private IAudioPlayer _player;
    private Boolean _disposed;

    /// <summary>全局默认客户端。首次访问时按平台自动选择系统语音</summary>
    /// <remarks>由进程生命周期持有，不建议调用 <see cref="Dispose"/>；需要独立生命周期请使用 <c>new TtsClient()</c>。</remarks>
    public static TtsClient Default => _default.Value;

    /// <summary>当前合成引擎。可替换为任意 <see cref="ITtsEngine"/> 实现</summary>
    public ITtsEngine Engine { get; set; }

    /// <summary>默认合成选项（音色、语速、音量）</summary>
    public TtsOptions Options { get; set; } = new();

    /// <summary>播放器工厂。默认使用当前平台音频驱动；测试可替换为假播放器</summary>
    public Func<IAudioPlayer> CreatePlayer { get; set; }

    /// <summary>初始化客户端，自动选择当前平台系统语音</summary>
    public TtsClient() : this(null) { }

    /// <summary>初始化客户端并指定引擎</summary>
    /// <param name="engine">合成引擎，为 null 时自动选择系统语音</param>
    public TtsClient(ITtsEngine engine) : this(engine, null) { }

    /// <summary>初始化客户端并指定引擎与默认选项</summary>
    /// <param name="engine">合成引擎，为 null 时自动选择系统语音</param>
    /// <param name="options">默认合成选项，为 null 时使用默认值</param>
    public TtsClient(ITtsEngine engine, TtsOptions options)
    {
        Engine = engine ?? CreateDefaultEngine();
        if (options != null) Options = options;
        CreatePlayer = CreateDefaultPlayer;
    }

    /// <summary>枚举当前引擎的可用音色</summary>
    /// <returns>音色列表，不支持枚举时返回空列表</returns>
    public IReadOnlyList<TtsVoice> GetVoices()
    {
        var engine = Engine;
        if (engine == null) return Array.Empty<TtsVoice>();

        try { return engine.GetVoices(); } catch { return Array.Empty<TtsVoice>(); }
    }

    /// <summary>直接播报</summary>
    /// <remarks>
    /// 引擎支持直接播报时走系统音频设备（延迟最低）；否则合成 PCM 后交给 <see cref="CreatePlayer"/> 播放。
    /// 新的调用会打断上一句；取消令牌或 <see cref="Stop"/> 都会停止当前播报。
    /// </remarks>
    /// <param name="text">待播报文本</param>
    /// <param name="options">本次合成选项，为 null 时使用 <see cref="Options"/></param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>播报完成、取消或停止时完成的任务</returns>
    public async Task SayAsync(String text, TtsOptions options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var opt = BuildOptions(text, options);
        var engine = EnsureEngine();

        // 后来者优先：打断上一句
        Stop();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _speakCts = cts;

        try
        {
            if (engine is ITtsSpeaker speaker)
            {
                _speaker = speaker;
                await speaker.SpeakAsync(text, opt, cts.Token).ConfigureAwait(false);
                return;
            }

            await SpeakByPcmAsync(engine, text, opt, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_speakCts, cts)) _speakCts = null;
            _speaker = null;
        }
    }

    /// <summary>停止当前播报</summary>
    public void Stop()
    {
        try { _speakCts?.Cancel(); } catch { /* 已释放 */ }
        try { _speaker?.Stop(); } catch { /* 已停止 */ }
        try { _player?.Stop(); } catch { /* 已停止 */ }
        _speaker = null;
    }

    /// <summary>保存为音频文件。扩展名决定格式：<c>.wav</c>、<c>.pcm</c>、<c>.mp3</c>、<c>.flac</c></summary>
    /// <param name="text">待合成文本</param>
    /// <param name="path">输出路径</param>
    /// <param name="options">本次合成选项，为 null 时使用 <see cref="Options"/></param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>合成结果</returns>
    public async Task<TtsResult> SaveAsync(String text, String path, TtsOptions options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (path.IsNullOrEmpty()) throw new ArgumentNullException(nameof(path));
        if (text.IsNullOrWhiteSpace()) throw new ArgumentException(L.T("The TTS text is empty."), nameof(text));

        var format = TtsFormatHelper.FromPath(path);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var result = await SaveAsync(text, stream, format, options, cancellationToken).ConfigureAwait(false);
        result.OutputPath = path;
        return result;
    }

    /// <summary>保存到输出流。WAV 写入标准 RIFF/WAVE 头，PCM 直接写裸数据，MP3/FLAC 走压缩编码管线</summary>
    /// <param name="text">待合成文本</param>
    /// <param name="output">输出流</param>
    /// <param name="format">输出格式</param>
    /// <param name="options">本次合成选项，为 null 时使用 <see cref="Options"/></param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>合成结果</returns>
    public async Task<TtsResult> SaveAsync(String text, Stream output, TtsFormat format = TtsFormat.Wav, TtsOptions options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (output == null) throw new ArgumentNullException(nameof(output));

        var opt = BuildOptions(text, options);
        var engine = EnsureEngine();
        var watch = Stopwatch.StartNew();
        var result = new TtsResult { Engine = engine.Name };

        using var writer = new TtsOutputWriter(output, format);
        await foreach (var chunk in engine.SynthesizeAsync(text, opt, cancellationToken).ConfigureAwait(false))
        {
            if (chunk?.Data == null || chunk.Data.Length == 0) continue;

            result.Format ??= chunk.Format ?? AudioFormat.Default;
            if (result.FirstChunkTime <= 0) result.FirstChunkTime = watch.Elapsed.TotalSeconds;

            writer.Write(chunk);
            result.Bytes += chunk.Data.Length;
        }

        if (result.Bytes <= 0)
            throw new InvalidOperationException(L.T("TTS engine {0} produced no audio.", engine.Name));

        writer.Complete();
        watch.Stop();
        result.Format ??= writer.Format;
        result.ElapsedTime = watch.Elapsed.TotalSeconds;
        if (result.FirstChunkTime <= 0) result.FirstChunkTime = result.ElapsedTime;

        return result;
    }

    /// <summary>流式合成。逐块产出裸 PCM（16 位小端），适合 HTTP/WebSocket 边合成边发送</summary>
    /// <param name="text">待合成文本</param>
    /// <param name="options">本次合成选项，为 null 时使用 <see cref="Options"/></param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>PCM 分块异步序列</returns>
    public async IAsyncEnumerable<TtsChunk> StreamAsync(String text, TtsOptions options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var opt = BuildOptions(text, options);
        var engine = EnsureEngine();

        await foreach (var chunk in engine.SynthesizeAsync(text, opt, cancellationToken).ConfigureAwait(false))
        {
            if (chunk?.Data != null && chunk.Data.Length > 0) yield return chunk;
        }
    }

    private async Task SpeakByPcmAsync(ITtsEngine engine, String text, TtsOptions options, CancellationToken cancellationToken)
    {
        var factory = CreatePlayer;
        if (factory == null) throw new NotSupportedException(L.T("No audio backend is available."));

        var player = factory() ?? throw new NotSupportedException(L.T("No audio backend is available."));
        var playback = new TtsPlayback(player);
        _player = player;
        try
        {
            await foreach (var chunk in engine.SynthesizeAsync(text, options, cancellationToken).ConfigureAwait(false))
                playback.Write(chunk);

            if (playback.BytesWritten <= 0)
                throw new InvalidOperationException(L.T("TTS engine {0} produced no audio.", engine.Name));

            await playback.DrainAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _player = null;
            playback.Dispose();
        }
    }

    private TtsOptions BuildOptions(String text, TtsOptions options)
    {
        if (text.IsNullOrWhiteSpace()) throw new ArgumentException(L.T("The TTS text is empty."), nameof(text));

        var source = options ?? Options ?? new TtsOptions();
        var rate = source.Rate > 0 ? source.Rate : 1;
        var volume = source.Volume < 0 ? 0 : source.Volume > 1 ? 1 : source.Volume;

        return new TtsOptions
        {
            Voice = source.Voice,
            Rate = rate,
            Volume = volume,
        };
    }

    private ITtsEngine EnsureEngine()
    {
        var engine = Engine ??= CreateDefaultEngine();
        if (!engine.IsAvailable)
            throw new NotSupportedException(L.T("No TTS engine is available. On Windows .NET Core/.NET 5+ add the System.Speech NuGet package; on Linux install espeak-ng; or configure a custom Engine."));

        return engine;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TtsClient));
    }

    /// <summary>释放引擎与播放资源</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        try { Engine?.Dispose(); } catch { /* 忽略释放异常 */ }
    }

    internal static ITtsEngine CreateDefaultEngine()
    {
        if (TtsPlatform.IsWindows)
        {
            var sapi = new SystemSpeechEngine();
            if (sapi.IsAvailable) return sapi;

            var espeak = new EspeakTtsEngine();
            return espeak.IsAvailable ? espeak : sapi;
        }

        return TtsPlatform.IsMacOS ? new MacSayTtsEngine() : new EspeakTtsEngine();
    }

    private static IAudioPlayer CreateDefaultPlayer()
    {
        if (!TtsPlatform.IsWindows)
            throw new PlatformNotSupportedException(L.T("Audio playback is only supported on Windows. Use SaveAsync or a native system engine instead."));

        return AudioDriverFactory.Create().CreatePlayer();
    }
}
