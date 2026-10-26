using System;
using NewLife.Audio.DSP;

namespace NewLife.Audio;

partial class AudioPcm
{
    #region 裁剪
    /// <summary>按采样点裁剪</summary>
    /// <param name="startSample">起始采样点（每声道计数），负值按 0 处理</param>
    /// <param name="sampleCount">采样点数，小于等于 0 表示裁剪到末尾</param>
    /// <returns>裁剪后的音频，格式与原对象一致</returns>
    /// <remarks>越界范围会被夹取；裁剪只搬运 16 位 PCM 数据，不引入重采样或重编码损失。</remarks>
    public AudioPcm Trim(Int64 startSample, Int64 sampleCount)
    {
        var channels = Format?.Channels > 0 ? Format.Channels : 1;
        var total = Data.Length / 2 / channels;

        if (startSample < 0) startSample = 0;
        if (startSample > total) startSample = total;
        if (sampleCount <= 0 || startSample + sampleCount > total) sampleCount = total - startSample;

        var bytes = (Int32)(sampleCount * channels * 2);
        var start = (Int32)(startSample * channels * 2);
        var data = new Byte[bytes];
        if (bytes > 0) Array.Copy(Data, start, data, 0, bytes);

        return new AudioPcm { Format = Format, Codec = Codec, Data = data };
    }

    /// <summary>按时间裁剪</summary>
    /// <param name="start">起始时间</param>
    /// <param name="duration">时长，小于等于零表示裁剪到末尾</param>
    /// <returns>裁剪后的音频</returns>
    public AudioPcm Trim(TimeSpan start, TimeSpan duration)
    {
        var rate = Format?.SampleRate > 0 ? Format.SampleRate : 0;
        if (rate == 0) return Trim(0, 0);

        var startSample = (Int64)Math.Round(start.TotalSeconds * rate);
        var sampleCount = duration > TimeSpan.Zero ? (Int64)Math.Round(duration.TotalSeconds * rate) : 0;

        return Trim(startSample, sampleCount);
    }
    #endregion

    #region 拼接
    /// <summary>按顺序拼接多段音频</summary>
    /// <param name="others">其余音频，采样率与声道数必须与本对象一致</param>
    /// <returns>拼接结果</returns>
    /// <exception cref="ArgumentException">格式不一致</exception>
    public AudioPcm Concat(params AudioPcm[] others)
    {
        if (others == null || others.Length == 0) return Trim(0, 0);

        var length = Data.Length;
        foreach (var item in others)
        {
            if (item == null) continue;
            EnsureSameFormat(item);
            length += item.Data.Length;
        }

        var data = new Byte[length];
        Array.Copy(Data, 0, data, 0, Data.Length);
        var offset = Data.Length;
        foreach (var item in others)
        {
            if (item == null) continue;
            Array.Copy(item.Data, 0, data, offset, item.Data.Length);
            offset += item.Data.Length;
        }

        return new AudioPcm { Format = Format, Codec = Codec, Data = data };
    }

    private void EnsureSameFormat(AudioPcm other)
    {
        var rate = Format?.SampleRate;
        var channels = Format?.Channels;
        if (other.Format?.SampleRate != rate || other.Format?.Channels != channels)
            throw new ArgumentException($"Audio format mismatch: {rate}Hz/{channels}ch vs {other.Format?.SampleRate}Hz/{other.Format?.Channels}ch.", nameof(other));
    }
    #endregion

    #region 增益
    /// <summary>按分贝调整增益</summary>
    /// <param name="decibels">增益分贝值，正为放大、负为衰减</param>
    /// <returns>调整后的音频</returns>
    /// <remarks>结果按 16 位有符号范围硬限幅，避免溢出翻转。</remarks>
    public AudioPcm ApplyGain(Double decibels) => ApplyGainFactor(Math.Pow(10, decibels / 20));

    /// <summary>把峰值归一化到指定比例</summary>
    /// <param name="peak">目标峰值比例（0~1），默认 0.98</param>
    /// <returns>归一化后的音频；全静音时原样返回</returns>
    public AudioPcm NormalizePeak(Double peak = 0.98)
    {
        var max = 0;
        var samples = Data.Length / 2;
        for (var i = 0; i < samples; i++)
        {
            var v = Math.Abs((Int32)(Int16)(Data[i * 2] | Data[i * 2 + 1] << 8));
            if (v > max) max = v;
        }
        if (max == 0) return Trim(0, 0);

        if (peak <= 0 || peak > 1) peak = 0.98;

        return ApplyGainFactor(peak * Int16.MaxValue / max);
    }

    private AudioPcm ApplyGainFactor(Double factor)
    {
        var data = new Byte[Data.Length];
        var samples = Data.Length / 2;
        for (var i = 0; i < samples; i++)
        {
            var v = (Int16)(Data[i * 2] | Data[i * 2 + 1] << 8);
            var scaled = (Int32)Math.Round(v * factor);
            if (scaled > Int16.MaxValue) scaled = Int16.MaxValue;
            if (scaled < Int16.MinValue) scaled = Int16.MinValue;

            data[i * 2] = (Byte)(scaled & 0xFF);
            data[i * 2 + 1] = (Byte)((scaled >> 8) & 0xFF);
        }

        return new AudioPcm { Format = Format, Codec = Codec, Data = data };
    }
    #endregion

    #region 重采样
    /// <summary>转换采样率</summary>
    /// <param name="sampleRate">目标采样率</param>
    /// <param name="quality">重采样质量，默认加窗 Sinc</param>
    /// <returns>重采样后的音频；采样率未变化时原样返回</returns>
    public AudioPcm Resample(Int32 sampleRate, ResamplerQuality quality = ResamplerQuality.Sinc)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        var source = Format;
        if (source == null) throw new InvalidOperationException("Audio format is required.");
        if (source.SampleRate == sampleRate) return Trim(0, 0);

        var channels = source.Channels > 0 ? source.Channels : 1;
        var frames = Data.Length / 2 / channels;

        // 逐声道重采样后再交织，避免把多声道样本当作单声道流处理
        var planes = new Byte[channels][];
        var mono = new Byte[frames * 2];
        for (var ch = 0; ch < channels; ch++)
        {
            for (var i = 0; i < frames; i++)
            {
                var p = (i * channels + ch) * 2;
                mono[i * 2] = Data[p];
                mono[i * 2 + 1] = Data[p + 1];
            }
            planes[ch] = Resampler.ResamplePcm(mono, source.SampleRate, sampleRate, quality);
        }

        var outFrames = planes[0].Length / 2;
        for (var ch = 1; ch < channels; ch++)
        {
            var count = planes[ch].Length / 2;
            if (count < outFrames) outFrames = count;
        }

        var data = new Byte[outFrames * channels * 2];
        for (var ch = 0; ch < channels; ch++)
        {
            var plane = planes[ch];
            for (var i = 0; i < outFrames; i++)
            {
                var p = (i * channels + ch) * 2;
                data[p] = plane[i * 2];
                data[p + 1] = plane[i * 2 + 1];
            }
        }

        var format = new AudioFormat
        {
            SampleRate = sampleRate,
            Channels = source.Channels,
            BitsPerSample = source.BitsPerSample,
            SamplesPerFrame = source.SamplesPerFrame,
        };

        return new AudioPcm { Format = format, Codec = Codec, Data = data };
    }
    #endregion
}
