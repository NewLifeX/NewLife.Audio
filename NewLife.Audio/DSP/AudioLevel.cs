using System;

namespace NewLife.Audio.DSP;

/// <summary>电平测量结果</summary>
/// <remarks>峰值与均方根均为 0~1 的线性幅度（1 对应满量程），并附 dBFS 换算值。</remarks>
public readonly struct AudioLevelInfo
{
    /// <summary>以线性幅度表示峰值</summary>
    public Double Peak { get; init; }

    /// <summary>以线性幅度表示均方根</summary>
    public Double Rms { get; init; }

    /// <summary>峰值对应的 dBFS，静音为负无穷</summary>
    public Double PeakDb => AudioLevel.ToDecibels(Peak);

    /// <summary>均方根对应的 dBFS，静音为负无穷</summary>
    public Double RmsDb => AudioLevel.ToDecibels(Rms);

    /// <summary>是否静音（峰值低于千分之一满量程，约 -60 dBFS）</summary>
    public Boolean IsSilent => Peak < 0.001;
}

/// <summary>PCM 电平测量</summary>
/// <remarks>
/// 面向电平表、静音判断与录制监视的轻量测量：一次遍历算出峰值与均方根，
/// 不保存状态、不做积分（EBU R128 综合响度请用 <see cref="LoudnessMeter"/>）。
/// 样本按 <see cref="SampleFormat"/> 解析，多声道时对所有声道一起统计。
/// </remarks>
/// <example>
/// <code>
/// var level = AudioLevel.Measure(pcm16, SampleFormat.Int16);
/// Console.WriteLine($"峰值 {level.PeakDb:F1} dBFS，均方根 {level.RmsDb:F1} dBFS");
/// </code>
/// </example>
public static class AudioLevel
{
    #region 方法
    /// <summary>测量一段 PCM 的峰值与均方根</summary>
    /// <param name="pcm">PCM 数据，按 <paramref name="format"/> 交错存放</param>
    /// <param name="format">采样格式，默认 16 位有符号整数</param>
    /// <param name="channels">声道数，仅为可读性保留；统计时所有声道一并计入</param>
    /// <returns>电平测量结果，空数据返回全零</returns>
    public static AudioLevelInfo Measure(ReadOnlySpan<Byte> pcm, SampleFormat format = SampleFormat.Int16, Int32 channels = 1)
    {
        if (pcm.Length == 0 || format == SampleFormat.Unknown) return default;

        var samples = PcmConverter.ToFloat(pcm, format);
        if (samples.Length == 0) return default;

        Double peak = 0, sum = 0;
        foreach (var sample in samples)
        {
            // 浮点数据可能出现超出 ±1 的样本，按绝对值统计但不截断，便于发现削波
            var absolute = Math.Abs((Double)sample);
            if (absolute > peak) peak = absolute;
            sum += absolute * absolute;
        }

        return new AudioLevelInfo
        {
            Peak = peak,
            Rms = Math.Sqrt(sum / samples.Length),
        };
    }

    /// <summary>线性幅度换算为 dBFS</summary>
    /// <param name="amplitude">0~1 的线性幅度</param>
    /// <returns>dBFS 值，零与负值返回负无穷</returns>
    public static Double ToDecibels(Double amplitude) => amplitude > 0 ? 20 * Math.Log10(amplitude) : Double.NegativeInfinity;
    #endregion
}
