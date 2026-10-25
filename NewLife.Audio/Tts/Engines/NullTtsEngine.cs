using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts;

/// <summary>静音占位引擎</summary>
/// <remarks>
/// 用途有三：作为降级链的最后一环保证“永远有输出”，在无声卡环境跑通流程，以及供单元测试构造可控的合成结果。
/// 输出按字长估算时长的静音，行为与真实引擎完全一致（同样支持流式分块）。
/// </remarks>
/// <example>
/// <code>
/// var router = new TtsRouter();
/// router.Register(new NullTtsEngine());
/// var result = await router.SynthesizeAsync(new TtsRequest { Text = "测试" }, null, default);
/// </code>
/// </example>
public class NullTtsEngine : TtsEngineBase
{
    /// <summary>每个字对应的静音时长（秒）</summary>
    public Double SecondsPerChar { get; set; } = 0.18;

    /// <summary>分块大小（毫秒），用于模拟流式输出</summary>
    public Int32 ChunkMilliseconds { get; set; } = 40;

    /// <summary>输出采样率</summary>
    public Int32 SampleRate { get; set; } = 16000;

    /// <summary>初始化静音占位引擎</summary>
    public NullTtsEngine()
    {
        Name = "silence";
        Capabilities = TtsCapability.Offline | TtsCapability.Streaming;
        Quality = 0;
        RequiredMemoryBytes = 0;
    }

    /// <summary>合成静音</summary>
    /// <param name="request">合成请求</param>
    /// <param name="sink">分块输出目标，可为 null</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>合成结果</returns>
    public override Task<TtsResult> SynthesizeAsync(TtsRequest request, ITtsSink sink, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        var format = new AudioFormat
        {
            SampleRate = request.Format?.SampleRate > 0 ? request.Format.SampleRate : SampleRate,
            Channels = 1,
            BitsPerSample = 16,
        };

        var chars = request.Text?.Length ?? 0;
        var seconds = chars * SecondsPerChar;
        var total = (Int32)(seconds * format.ByteRate);
        // 对齐到完整采样，避免最后一个样本被截断
        total -= total % format.BytesPerSample;

        var pcm = new Byte[total < 0 ? 0 : total];
        var chunk = Math.Max(format.ByteRate / 1000 * Math.Max(ChunkMilliseconds, 1), 2);

        var result = new TtsResult
        {
            Source = TtsSource.Engine,
            Engine = Name,
            Text = request.Text,
            Format = format,
        };

        if (sink == null)
        {
            result.Pcm = pcm;
            result.PcmBytes = pcm.Length;
            return Task.FromResult(result);
        }

        sink.Open(format);
        for (var offset = 0; offset < pcm.Length; offset += chunk)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = Math.Min(chunk, pcm.Length - offset);
            sink.Write(pcm, offset, count);
            result.PcmBytes += count;
        }
        sink.Close();

        return Task.FromResult(result);
    }
}
