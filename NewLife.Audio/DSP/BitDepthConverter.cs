namespace NewLife.Audio.DSP;

/// <summary>位深转换器。在 8/16/24/32-bit 整数与 32/64-bit 浮点之间转换</summary>
/// <remarks>
/// 管线内部统一使用 ±1.0 的 <see cref="Single"/> 缓冲，因此作为 <see cref="IAudioProcessor"/> 时
/// <see cref="Read"/> 只需转发；真正的位深转换发生在与字节流交界处，由
/// <see cref="PcmToFloat"/> / <see cref="FloatToPcm"/> 按 <see cref="SampleFormat"/> 完成，
/// 具体实现复用 <see cref="PcmConverter"/>。
/// </remarks>
public class BitDepthConverter : IAudioProcessor
{
    private readonly AudioFormat _inputFormat;
    private readonly AudioFormat _outputFormat;

    /// <summary>输入格式</summary>
    public AudioFormat InputFormat => _inputFormat;

    /// <summary>输出格式</summary>
    public AudioFormat OutputFormat => _outputFormat;

    /// <summary>上游源</summary>
    public IAudioProcessor Source { get; set; }

    /// <summary>初始化位深转换器</summary>
    /// <param name="bitsPerSample">目标位深（8/16/24/32）</param>
    /// <param name="inputFormat">输入格式</param>
    public BitDepthConverter(Int32 bitsPerSample, AudioFormat inputFormat = null)
    {
        _inputFormat = inputFormat?.Clone() ?? AudioFormat.Default;
        _outputFormat = _inputFormat.Clone();
        _outputFormat.BitsPerSample = bitsPerSample;
    }

    /// <summary>读取转换后的采样数据</summary>
    /// <param name="buffer">输出缓冲</param>
    /// <param name="offset">输出起始位置</param>
    /// <param name="count">期望采样数</param>
    /// <returns>实际写入的采样数</returns>
    public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
    {
        if (Source == null) return 0;

        return Source.Read(buffer, offset, count);
    }

    /// <summary>重置</summary>
    public void Reset() => Source?.Reset();

    /// <summary>按采样格式把 PCM 字节转为 ±1.0 浮点采样</summary>
    /// <param name="pcm">PCM 字节（小端）</param>
    /// <param name="format">采样格式</param>
    /// <param name="samples">输出浮点采样（为空时自动分配）</param>
    /// <returns>转换的采样数</returns>
    public static Int32 PcmToFloat(Byte[] pcm, SampleFormat format, out Single[] samples)
    {
        samples = PcmConverter.ToFloat(pcm, format);

        return samples.Length;
    }

    /// <summary>按采样格式把浮点采样转为 PCM 字节</summary>
    /// <param name="samples">浮点采样</param>
    /// <param name="format">采样格式</param>
    /// <param name="count">采样数</param>
    /// <returns>PCM 字节（小端）</returns>
    public static Byte[] FloatToPcm(Single[] samples, SampleFormat format, Int32 count)
    {
        var length = Math.Min(count, samples.Length);

        return PcmConverter.FromFloat(samples.AsSpan(0, length), format);
    }

    /// <summary>将16位PCM字节数组转为浮点采样</summary>
    /// <param name="pcm">PCM字节数据（小端）</param>
    /// <param name="samples">输出浮点采样</param>
    /// <returns>转换的采样数</returns>
    public static Int32 Pcm16ToFloat(Byte[] pcm, Single[] samples)
    {
        var converted = PcmConverter.ToFloat(pcm, SampleFormat.Int16);
        var count = Math.Min(converted.Length, samples.Length);
        Array.Copy(converted, samples, count);

        return count;
    }

    /// <summary>将浮点采样转为16位PCM字节数组</summary>
    /// <param name="samples">浮点采样数据</param>
    /// <param name="pcm">输出PCM字节</param>
    /// <param name="count">采样数</param>
    /// <returns>写入的字节数</returns>
    public static Int32 FloatToPcm16(Single[] samples, Byte[] pcm, Int32 count)
    {
        var len = Math.Min(count, samples.Length);
        for (var i = 0; i < len; i++)
        {
            var val = (Int16)Math.Max(-32768, Math.Min(32767, samples[i] * 32767f));
            pcm[i * 2] = (Byte)(val & 0xFF);
            pcm[i * 2 + 1] = (Byte)((val >> 8) & 0xFF);
        }
        return len * 2;
    }

    /// <summary>将8位无符号PCM转为浮点采样</summary>
    public static Int32 Pcm8ToFloat(Byte[] pcm, Single[] samples)
    {
        var count = Math.Min(pcm.Length, samples.Length);
        for (var i = 0; i < count; i++)
            samples[i] = (pcm[i] - 128f) / 128f;
        return count;
    }

    /// <summary>将浮点采样转为8位无符号PCM</summary>
    public static Int32 FloatToPcm8(Single[] samples, Byte[] pcm, Int32 count)
    {
        var len = Math.Min(count, samples.Length);
        for (var i = 0; i < len; i++)
            pcm[i] = (Byte)Math.Max(0, Math.Min(255, (samples[i] + 1f) * 127.5f));
        return len;
    }
}
