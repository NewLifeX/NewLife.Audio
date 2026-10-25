using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts;

/// <summary>语音合成引擎</summary>
/// <remarks>
/// 引擎只负责“文本 → PCM”，不关心缓存、预生成、降级与场景选路，这些由 <see cref="TtsService"/> 与 <see cref="TtsRouter"/> 完成。
/// 同一个接口同时覆盖四种实现形态：
/// <list type="bullet">
/// <item>离线神经模型（常驻推理引擎，通过适配器接入）</item>
/// <item>离线子进程（piper / espeak 等可执行文件，零原生依赖）</item>
/// <item>在线整段服务（HTTP 一次请求拿回整段音频）</item>
/// <item>在线流式服务（WebSocket 边收边播）</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// // 完整合成：sink 传 null，结果从 TtsResult.Pcm 取
/// var result = await engine.SynthesizeAsync(request, null, cancellationToken);
///
/// // 流式合成：sink 边收边播
/// var sink = new TtsDelegateSink(null, (buf, off, len) =&gt; player.Write(buf, off, len));
/// await engine.SynthesizeAsync(request, sink, cancellationToken);
/// </code>
/// </example>
public interface ITtsEngine : IDisposable
{
    /// <summary>引擎名称，用于日志、缓存键与降级链诊断</summary>
    String Name { get; }

    /// <summary>引擎能力</summary>
    TtsCapability Capabilities { get; }

    /// <summary>主管质量分（0~100）。路由在同级候选里按它排序，分数高者优先</summary>
    Int32 Quality { get; }

    /// <summary>合成时额外占用的内存峰值估算（字节）。0 表示可忽略</summary>
    Int64 RequiredMemoryBytes { get; }

    /// <summary>当前是否可用（模型文件存在、可执行文件存在、网络可达等）</summary>
    Boolean IsAvailable { get; }

    /// <summary>合成语音</summary>
    /// <param name="request">合成请求，文本应已完成规范化</param>
    /// <param name="sink">分块输出目标。为 null 时返回整段 PCM</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>合成结果</returns>
    Task<TtsResult> SynthesizeAsync(TtsRequest request, ITtsSink sink, CancellationToken cancellationToken);
}

/// <summary>语音合成引擎基类</summary>
/// <remarks>统一承载名称、能力、质量分与内存估算等只读属性，派生类只需实现 <see cref="SynthesizeAsync"/>。</remarks>
public abstract class TtsEngineBase : ITtsEngine
{
    /// <summary>引擎名称</summary>
    public String Name { get; set; }

    /// <summary>引擎能力</summary>
    public TtsCapability Capabilities { get; protected set; }

    /// <summary>主管质量分（0~100）</summary>
    public Int32 Quality { get; set; }

    /// <summary>合成时额外占用的内存峰值估算（字节）</summary>
    public Int64 RequiredMemoryBytes { get; set; }

    /// <summary>当前是否可用</summary>
    public virtual Boolean IsAvailable => true;

    /// <summary>初始化引擎基类</summary>
    protected TtsEngineBase() { }

    /// <summary>初始化引擎基类</summary>
    /// <param name="name">引擎名称</param>
    /// <param name="capabilities">引擎能力</param>
    /// <param name="quality">主管质量分</param>
    protected TtsEngineBase(String name, TtsCapability capabilities, Int32 quality)
    {
        Name = name;
        Capabilities = capabilities;
        Quality = quality;
    }

    /// <summary>合成语音</summary>
    /// <param name="request">合成请求</param>
    /// <param name="sink">分块输出目标，可为 null</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>合成结果</returns>
    public abstract Task<TtsResult> SynthesizeAsync(TtsRequest request, ITtsSink sink, CancellationToken cancellationToken);

    /// <summary>释放引擎占用的资源</summary>
    public virtual void Dispose() { }

    /// <summary>返回引擎描述</summary>
    /// <returns>名称与能力</returns>
    public override String ToString() => $"{Name}({Capabilities},Q{Quality})";

    /// <summary>判断能力是否满足要求</summary>
    /// <param name="required">要求的能力组合</param>
    /// <returns>是否全部具备</returns>
    public Boolean HasCapability(TtsCapability required) => (Capabilities & required) == required;

    /// <summary>把整段 PCM 写到输出目标</summary>
    /// <param name="sink">输出目标，可为 null</param>
    /// <param name="pcm">PCM 数据</param>
    /// <param name="format">音频格式</param>
    protected static void WriteAll(ITtsSink sink, Byte[] pcm, AudioFormat format)
    {
        if (sink == null || pcm == null || pcm.Length == 0) return;

        sink.Open(format);
        sink.Write(pcm, 0, pcm.Length);
        sink.Close();
    }
}
