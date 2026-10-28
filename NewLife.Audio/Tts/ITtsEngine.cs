namespace NewLife.Audio.Tts;

/// <summary>语音合成引擎</summary>
/// <remarks>
/// 引擎只负责“文本 → PCM 块”，不关心文件封装、播放、参数合并与统计，这些由 <see cref="TtsClient"/> 完成。
/// 同一个接口覆盖系统语音、HTTP 服务、子进程合成器与自定义模型：
/// <list type="bullet">
/// <item>系统语音：<see cref="SystemSpeechEngine"/>、<see cref="MacSayTtsEngine"/>、<see cref="EspeakTtsEngine"/></item>
/// <item>在线服务：<see cref="HttpTtsEngine"/>、<see cref="WebSocketTtsEngine"/></item>
/// <item>子进程：<see cref="ProcessTtsEngine"/>（piper / espeak-ng 等）</item>
/// <item>测试与兜底：<see cref="NullTtsEngine"/></item>
/// </list>
/// <para>输出约定：每个 <see cref="TtsChunk"/> 必须携带 16 位有符号小端 PCM 与 <see cref="AudioFormat"/>。</para>
/// </remarks>
/// <example>
/// <code>
/// public sealed class MyEngine : TtsEngineBase
/// {
///     public MyEngine() : base("my") { }
///
///     public override async IAsyncEnumerable&lt;TtsChunk&gt; SynthesizeAsync(
///         String text, TtsOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
///     {
///         await foreach (var pcm in MyModel.RunAsync(text, cancellationToken))
///             yield return new TtsChunk { Format = AudioFormat.Default, Data = pcm };
///     }
/// }
/// </code>
/// </example>
public interface ITtsEngine : IDisposable
{
    /// <summary>引擎名称，用于日志与诊断</summary>
    String Name { get; }

    /// <summary>当前是否可用（模型文件存在、可执行文件存在、网络地址已配置等）</summary>
    Boolean IsAvailable { get; }

    /// <summary>枚举可用音色。不支持枚举时返回空列表</summary>
    /// <returns>音色列表</returns>
    IReadOnlyList<TtsVoice> GetVoices();

    /// <summary>合成语音，逐块产出 PCM</summary>
    /// <param name="text">待合成文本</param>
    /// <param name="options">合成选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>PCM 分块异步序列</returns>
    IAsyncEnumerable<TtsChunk> SynthesizeAsync(String text, TtsOptions options, CancellationToken cancellationToken);
}

/// <summary>支持直接播报的引擎</summary>
/// <remarks>
/// 系统语音通常自己管理音频设备，直接播报比“合成 PCM 再播放”延迟更低且不占额外内存。
/// 不支持直出的引擎由 <see cref="TtsClient"/> 走 PCM 播放回退。
/// </remarks>
public interface ITtsSpeaker
{
    /// <summary>直接播报。返回的任务在播完、取消或 <see cref="Stop"/> 时完成</summary>
    /// <param name="text">待播报文本</param>
    /// <param name="options">合成选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>播报任务</returns>
    Task SpeakAsync(String text, TtsOptions options, CancellationToken cancellationToken);

    /// <summary>停止当前播报</summary>
    void Stop();
}

/// <summary>语音合成引擎基类</summary>
/// <remarks>统一承载名称与可用性，派生类只需实现 <see cref="SynthesizeAsync"/>。</remarks>
public abstract class TtsEngineBase : ITtsEngine
{
    /// <summary>引擎名称</summary>
    public String Name { get; set; }

    /// <summary>当前是否可用</summary>
    public virtual Boolean IsAvailable => true;

    /// <summary>初始化引擎基类</summary>
    protected TtsEngineBase() { }

    /// <summary>初始化引擎基类</summary>
    /// <param name="name">引擎名称</param>
    protected TtsEngineBase(String name) => Name = name;

    /// <summary>枚举可用音色。默认不支持枚举</summary>
    /// <returns>空列表</returns>
    public virtual IReadOnlyList<TtsVoice> GetVoices() => Array.Empty<TtsVoice>();

    /// <summary>合成语音，逐块产出 PCM</summary>
    /// <param name="text">待合成文本</param>
    /// <param name="options">合成选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>PCM 分块异步序列</returns>
    public abstract IAsyncEnumerable<TtsChunk> SynthesizeAsync(String text, TtsOptions options, CancellationToken cancellationToken);

    /// <summary>释放引擎占用的资源</summary>
    public virtual void Dispose() { }

    /// <summary>返回引擎描述</summary>
    /// <returns>引擎名称</returns>
    public override String ToString() => Name ?? GetType().Name;
}
