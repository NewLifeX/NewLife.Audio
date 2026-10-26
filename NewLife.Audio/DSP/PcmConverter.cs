using System;
using System.Buffers.Binary;

namespace NewLife.Audio.DSP;

/// <summary>采样格式</summary>
/// <remarks>
/// 对应音频帧里单个样本的存储形态：8 位无符号整数（WAV 惯例）、16/24/32 位有符号整数小端、32/64 位浮点。
/// 与 <see cref="AudioFormat.BitsPerSample"/> + <see cref="AudioFormat.IsFloat"/> 组合一一对应。
/// </remarks>
public enum SampleFormat
{
    /// <summary>未知</summary>
    Unknown = 0,

    /// <summary>8 位无符号整数（WAV 惯例，静音为 128）</summary>
    UInt8 = 8,

    /// <summary>16 位有符号整数小端</summary>
    Int16 = 16,

    /// <summary>24 位有符号整数小端</summary>
    Int24 = 24,

    /// <summary>32 位有符号整数小端</summary>
    Int32 = 32,

    /// <summary>32 位 IEEE 浮点（范围 ±1.0）</summary>
    Float32 = 132,

    /// <summary>64 位 IEEE 浮点（范围 ±1.0）</summary>
    Float64 = 164,
}

/// <summary>采样格式与交错/平面数据转换</summary>
/// <remarks>
/// 内部统一以 ±1.0 的 <see cref="Single"/> 表示样本，这里负责与各存储格式互转，以及
/// 交错（interleaved，LRLR…）与平面（planar，LLLL…RRRR…）布局之间的搬运。
/// </remarks>
/// <example>
/// <code>
/// var samples = PcmConverter.ToFloat(pcm16, SampleFormat.Int16);
/// var planar = PcmConverter.Deinterleave(samples, 2);
/// var back = PcmConverter.Interleave(planar);
/// var pcm = PcmConverter.FromFloat(back, SampleFormat.Int16);
/// </code>
/// </example>
public static class PcmConverter
{
    #region 属性
    /// <summary>取单个样本占用的字节数</summary>
    /// <param name="format">采样格式</param>
    /// <returns>字节数，未知格式返回 0</returns>
    public static Int32 GetBytesPerSample(SampleFormat format) => format switch
    {
        SampleFormat.UInt8 => 1,
        SampleFormat.Int16 => 2,
        SampleFormat.Int24 => 3,
        SampleFormat.Int32 => 4,
        SampleFormat.Float32 => 4,
        SampleFormat.Float64 => 8,
        _ => 0,
    };

    /// <summary>是否为浮点格式</summary>
    /// <param name="format">采样格式</param>
    /// <returns>浮点返回 true</returns>
    public static Boolean IsFloat(SampleFormat format) => format is SampleFormat.Float32 or SampleFormat.Float64;

    /// <summary>由位深与是否浮点推导采样格式</summary>
    /// <param name="bitsPerSample">位深</param>
    /// <param name="isFloat">是否浮点</param>
    /// <returns>采样格式</returns>
    public static SampleFormat FromBits(Int32 bitsPerSample, Boolean isFloat)
    {
        if (isFloat) return bitsPerSample > 32 ? SampleFormat.Float64 : SampleFormat.Float32;

        return bitsPerSample switch
        {
            8 => SampleFormat.UInt8,
            24 => SampleFormat.Int24,
            32 => SampleFormat.Int32,
            _ => SampleFormat.Int16,
        };
    }
    #endregion

    #region 字节 → 浮点
    /// <summary>把 PCM 字节流转换为 ±1.0 的浮点样本</summary>
    /// <param name="pcm">PCM 数据（小端）</param>
    /// <param name="format">采样格式</param>
    /// <returns>浮点样本数组</returns>
    /// <exception cref="ArgumentOutOfRangeException">不支持的采样格式</exception>
    public static Single[] ToFloat(ReadOnlySpan<Byte> pcm, SampleFormat format)
    {
        var bytesPerSample = GetBytesPerSample(format);
        if (bytesPerSample == 0) throw new ArgumentOutOfRangeException(nameof(format), "Unsupported sample format: " + format);

        var count = pcm.Length / bytesPerSample;
        var samples = new Single[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = ReadOne(pcm.Slice(i * bytesPerSample, bytesPerSample), format);
        }

        return samples;
    }

    /// <summary>读取单个样本</summary>
    /// <param name="data">样本字节</param>
    /// <param name="format">采样格式</param>
    /// <returns>±1.0 的样本值</returns>
    public static Single ReadOne(ReadOnlySpan<Byte> data, SampleFormat format) => format switch
    {
        SampleFormat.UInt8 => (data[0] - 128) / 128f,
        SampleFormat.Int16 => (Int16)(data[0] | data[1] << 8) / 32768f,
        SampleFormat.Int24 => ReadInt24(data) / 8388608f,
        SampleFormat.Int32 => BinaryPrimitives.ReadInt32LittleEndian(data) / 2147483648f,
        SampleFormat.Float32 => BitConverter.ToSingle(BitConverter.GetBytes(BinaryPrimitives.ReadInt32LittleEndian(data)), 0),
        SampleFormat.Float64 => (Single)BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(data)),
        _ => 0f,
    };
    #endregion

    #region 浮点 → 字节
    /// <summary>把 ±1.0 的浮点样本转换为 PCM 字节流</summary>
    /// <param name="samples">浮点样本</param>
    /// <param name="format">采样格式</param>
    /// <returns>PCM 字节数组（小端）</returns>
    /// <exception cref="ArgumentOutOfRangeException">不支持的采样格式</exception>
    /// <remarks>整型格式按目标范围硬限幅，避免溢出翻转。</remarks>
    public static Byte[] FromFloat(ReadOnlySpan<Single> samples, SampleFormat format)
    {
        var bytesPerSample = GetBytesPerSample(format);
        if (bytesPerSample == 0) throw new ArgumentOutOfRangeException(nameof(format), "Unsupported sample format: " + format);

        var pcm = new Byte[samples.Length * bytesPerSample];
        for (var i = 0; i < samples.Length; i++)
        {
            WriteOne(pcm.AsSpan(i * bytesPerSample, bytesPerSample), samples[i], format);
        }

        return pcm;
    }

    /// <summary>写入单个样本</summary>
    /// <param name="data">目标字节</param>
    /// <param name="value">±1.0 的样本值</param>
    /// <param name="format">采样格式</param>
    public static void WriteOne(Span<Byte> data, Single value, SampleFormat format)
    {
        switch (format)
        {
            case SampleFormat.UInt8:
                {
                    var v = (Int32)Math.Round(value * 128 + 128);
                    if (v < 0) v = 0;
                    if (v > 255) v = 255;
                    data[0] = (Byte)v;
                    break;
                }
            case SampleFormat.Int16:
                {
                    var v = Clamp((Int32)Math.Round(value * 32768), -32768, 32767);
                    data[0] = (Byte)v;
                    data[1] = (Byte)(v >> 8);
                    break;
                }
            case SampleFormat.Int24:
                {
                    var v = Clamp((Int32)Math.Round(value * 8388608), -8388608, 8388607);
                    data[0] = (Byte)v;
                    data[1] = (Byte)(v >> 8);
                    data[2] = (Byte)(v >> 16);
                    break;
                }
            case SampleFormat.Int32:
                {
                    var scaled = Math.Round(value * 2147483648.0);
                    var v = scaled >= Int32.MaxValue ? Int32.MaxValue : scaled <= Int32.MinValue ? Int32.MinValue : (Int32)scaled;
                    BinaryPrimitives.WriteInt32LittleEndian(data, v);
                    break;
                }
            case SampleFormat.Float32:
                BinaryPrimitives.WriteInt32LittleEndian(data, BitConverter.ToInt32(BitConverter.GetBytes(value), 0));
                break;
            case SampleFormat.Float64:
                BinaryPrimitives.WriteInt64LittleEndian(data, BitConverter.DoubleToInt64Bits(value));
                break;
        }
    }
    #endregion

    #region 交错 ↔ 平面
    /// <summary>交错样本拆成逐声道平面</summary>
    /// <param name="interleaved">交错样本（LRLR…）</param>
    /// <param name="channels">声道数</param>
    /// <returns>每声道一个数组</returns>
    public static Single[][] Deinterleave(ReadOnlySpan<Single> interleaved, Int32 channels)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        var frames = interleaved.Length / channels;
        var planes = new Single[channels][];
        for (var ch = 0; ch < channels; ch++)
        {
            var plane = new Single[frames];
            for (var i = 0; i < frames; i++)
            {
                plane[i] = interleaved[i * channels + ch];
            }
            planes[ch] = plane;
        }

        return planes;
    }

    /// <summary>逐声道平面合并为交错样本</summary>
    /// <param name="planes">每声道一个数组，长度应一致</param>
    /// <returns>交错样本（LRLR…）</returns>
    public static Single[] Interleave(Single[][] planes)
    {
        if (planes == null) throw new ArgumentNullException(nameof(planes));
        if (planes.Length == 0) return [];

        var frames = Int32.MaxValue;
        foreach (var plane in planes)
        {
            if (plane == null) continue;
            if (plane.Length < frames) frames = plane.Length;
        }
        if (frames == Int32.MaxValue) frames = 0;

        var channels = planes.Length;
        var interleaved = new Single[frames * channels];
        for (var ch = 0; ch < channels; ch++)
        {
            var plane = planes[ch];
            if (plane == null) continue;
            for (var i = 0; i < frames; i++)
            {
                interleaved[i * channels + ch] = plane[i];
            }
        }

        return interleaved;
    }

    /// <summary>把交错 PCM 字节拆成逐声道平面</summary>
    /// <param name="pcm">交错 PCM 字节（LRLR…），尾部不足一个样本的字节忽略</param>
    /// <param name="format">音频格式，取 <see cref="AudioFormat.BitsPerSample"/> 与 <see cref="AudioFormat.Channels"/></param>
    /// <returns>每声道一个数组；格式非法或数据不足一个样本时返回空数组</returns>
    /// <remarks>按字节搬运，8/16/24/32 位与浮点都适用，不做位深转换。</remarks>
    public static Byte[][] Deinterleave(ReadOnlySpan<Byte> pcm, AudioFormat format)
    {
        if (format == null) throw new ArgumentNullException(nameof(format));

        var bytes = format.BytesPerSample;
        var channels = format.Channels;
        if (bytes <= 0 || channels <= 0) return [];

        var sampleBytes = bytes * channels;
        var samples = pcm.Length / sampleBytes;
        if (samples <= 0) return [];

        var planes = new Byte[channels][];
        for (var ch = 0; ch < channels; ch++)
        {
            var plane = new Byte[samples * bytes];
            for (var i = 0; i < samples; i++)
            {
                pcm.Slice(i * sampleBytes + ch * bytes, bytes).CopyTo(plane.AsSpan(i * bytes, bytes));
            }
            planes[ch] = plane;
        }

        return planes;
    }

    /// <summary>把逐声道平面合并成交错 PCM 字节</summary>
    /// <param name="planes">每声道一个数组，长度以最短声道为准</param>
    /// <param name="format">音频格式，取 <see cref="AudioFormat.BitsPerSample"/>；声道数取 <paramref name="planes"/> 的长度</param>
    /// <returns>交错 PCM 字节；没有可用声道时返回空数组</returns>
    /// <remarks>缺失的声道按静音（全零）补齐，与 <see cref="Interleave(Single[][])"/> 语义一致。</remarks>
    public static Byte[] Interleave(Byte[][] planes, AudioFormat format)
    {
        if (planes == null) throw new ArgumentNullException(nameof(planes));
        if (format == null) throw new ArgumentNullException(nameof(format));

        var bytes = format.BytesPerSample;
        var channels = planes.Length;
        if (bytes <= 0 || channels <= 0) return [];

        var samples = Int32.MaxValue;
        foreach (var plane in planes)
        {
            if (plane == null) continue;
            var count = plane.Length / bytes;
            if (count < samples) samples = count;
        }
        if (samples == Int32.MaxValue || samples <= 0) return [];

        var output = new Byte[samples * channels * bytes];
        for (var i = 0; i < samples; i++)
        {
            for (var ch = 0; ch < channels; ch++)
            {
                var plane = planes[ch];
                if (plane == null) continue;

                Array.Copy(plane, i * bytes, output, (i * channels + ch) * bytes, bytes);
            }
        }

        return output;
    }
    #endregion

    #region 辅助
    private static Int32 ReadInt24(ReadOnlySpan<Byte> data)
    {
        var v = data[0] | data[1] << 8 | data[2] << 16;

        // 符号扩展
        return (v & 0x800000) != 0 ? v | ~0xFFFFFF : v;
    }

    private static Int32 Clamp(Int32 value, Int32 min, Int32 max)
    {
        if (value < min) return min;
        if (value > max) return max;

        return value;
    }
    #endregion
}

