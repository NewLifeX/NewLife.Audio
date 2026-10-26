using NewLife.Audio.DSP;

namespace NewLife.Audio;

partial class AudioPcm
{
    /// <summary>按样本数切分为帧序列，产出带时间戳的借用帧</summary>
    /// <param name="samplesPerFrame">每帧样本数，0 表示取 <see cref="AudioFormat.SamplesPerFrame"/>；非法值按 1 处理</param>
    /// <returns>帧序列。帧与 <see cref="Data"/> 共享缓冲，不复制，调用方不得修改或释放</returns>
    /// <remarks>用于把整段 PCM 交给按帧处理的下游（DSP、编码器、网络分包），省去自己算偏移与时间戳。</remarks>
    /// <example>
    /// <code>
    /// var pcm = AudioFile.ReadPcm("long.wav");
    /// foreach (var frame in pcm.Frames(960))
    ///     Console.WriteLine($"{frame.Timestamp.TotalMilliseconds}ms {frame.Length}B");
    /// </code>
    /// </example>
    public IEnumerable<AudioFrame> Frames(Int32 samplesPerFrame = 0)
    {
        var format = Format;
        var data = Data;
        if (format == null || data == null || data.Length == 0) yield break;

        var bytesPerSample = format.BytesPerSample * format.Channels;
        if (bytesPerSample <= 0) yield break;

        var samples = samplesPerFrame > 0 ? samplesPerFrame : samplesPerFrame == 0 ? format.SamplesPerFrame : 1;
        if (samples <= 0) samples = 1;

        var frameBytes = samples * bytesPerSample;
        for (var offset = 0; offset < data.Length; offset += frameBytes)
        {
            var count = Math.Min(frameBytes, data.Length - offset);
            var timestamp = SampleToTime(offset / bytesPerSample, format);

            yield return new AudioFrame(format, timestamp, data, offset, count);
        }
    }

    /// <summary>按样本序号换算时间戳</summary>
    /// <param name="sample">绝对样本序号</param>
    /// <param name="format">音频格式</param>
    /// <returns>相对流起点的时间</returns>
    private static TimeSpan SampleToTime(Int64 sample, AudioFormat format)
    {
        if (format.SampleRate <= 0) return TimeSpan.Zero;

        return TimeSpan.FromSeconds((Double)sample / format.SampleRate);
    }
}
