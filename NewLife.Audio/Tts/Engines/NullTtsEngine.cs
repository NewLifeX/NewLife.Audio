using System.Runtime.CompilerServices;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts;

/// <summary>静音占位引擎</summary>
/// <remarks>
/// 用于单元测试、无声卡环境与降级链末端，保证“永远有输出”。
/// 按文本长度估算时长，输出 16 位静音 PCM。
/// </remarks>
public class NullTtsEngine : TtsEngineBase
{
    /// <summary>每个字对应的秒数</summary>
    public Double SecondsPerChar { get; set; } = 0.18;

    /// <summary>分块大小（毫秒），用于模拟流式输出</summary>
    public Int32 ChunkMilliseconds { get; set; } = 40;

    /// <summary>输出采样率</summary>
    public Int32 SampleRate { get; set; } = 16000;

    /// <summary>初始化静音占位引擎</summary>
    public NullTtsEngine() : base("silence") { }

    /// <summary>合成静音</summary>
    /// <param name="text">待合成文本</param>
    /// <param name="options">合成选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>PCM 分块异步序列</returns>
    public override async IAsyncEnumerable<TtsChunk> SynthesizeAsync(String text, TtsOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var format = new AudioFormat
        {
            SampleRate = SampleRate > 0 ? SampleRate : 16000,
            Channels = 1,
            BitsPerSample = 16,
        };

        var chars = text?.Length ?? 0;
        var seconds = chars * SecondsPerChar;
        var total = (Int32)(seconds * format.ByteRate);
        total -= total % format.BytesPerSample;
        if (total < 0) total = 0;

        var chunk = Math.Max(format.ByteRate / 1000 * Math.Max(ChunkMilliseconds, 1), 2);
        for (var offset = 0; offset < total; offset += chunk)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = Math.Min(chunk, total - offset);
            yield return new TtsChunk { Format = format, Data = new Byte[count] };

            await Task.Yield();
        }
    }
}
