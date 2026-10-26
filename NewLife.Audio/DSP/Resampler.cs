namespace NewLife.Audio.DSP;

/// <summary>重采样质量档</summary>
public enum ResamplerQuality
{
    /// <summary>线性插值。低成本、低延迟，高频镜像与混叠抑制有限</summary>
    Linear = 0,

    /// <summary>加窗 Sinc 插值。带抗混叠的低通核，质量高，存在固定群延迟</summary>
    Sinc = 1,
}

/// <summary>重采样器。改变音频采样率</summary>
/// <remarks>
/// 支持整数倍与分数倍重采样，两种质量档：<br/>
/// 1. <see cref="ResamplerQuality.Linear"/>：相邻样本线性插值，无群延迟；<br/>
/// 2. <see cref="ResamplerQuality.Sinc"/>：Blackman 窗 sinc 低通核，支持抗混叠降采样，群延迟为 <see cref="Latency"/> 个输入样本。<br/>
/// 流式 <see cref="Read"/> 在多次调用间保留输入历史，保证插值连续；<see cref="ResamplePcm"/> 为一次性整段处理。
/// </remarks>
/// <example>
/// <code>
/// var resampler = new Resampler(16000, inputFormat, ResamplerQuality.Sinc)
/// {
///     Source = source,
/// };
/// var buffer = new Single[1600];
/// var count = resampler.Read(buffer, 0, buffer.Length);
/// </code>
/// </example>
public class Resampler : IAudioProcessor
{
    #region 常量
    /// <summary>sinc 核半长（输入样本），决定质量与延迟</summary>
    private const Int32 SincHalfTaps = 16;
    #endregion

    #region 字段
    private readonly AudioFormat _inputFormat;
    private readonly AudioFormat _outputFormat;
    private readonly Double _ratio;
    private readonly ResamplerQuality _quality;
    private Single[] _history = new Single[4096];
    private Int32 _historyCount;
    private Double _position;
    #endregion

    #region 属性
    /// <summary>输入格式</summary>
    public AudioFormat InputFormat => _inputFormat;

    /// <summary>输出格式</summary>
    public AudioFormat OutputFormat => _outputFormat;

    /// <summary>上游源</summary>
    public IAudioProcessor Source { get; set; }

    /// <summary>质量档</summary>
    public ResamplerQuality Quality => _quality;

    /// <summary>输入到输出的群延迟（输入样本数）</summary>
    /// <remarks>线性档为 0；sinc 档约为核半长乘以降采样倍数</remarks>
    public Int32 Latency => _quality == ResamplerQuality.Sinc ? (Int32)Math.Ceiling(SincHalfTaps * Math.Max(1, _ratio)) : 0;

    /// <summary>群延迟对应的毫秒数</summary>
    public Double LatencyMilliseconds => _inputFormat.SampleRate > 0 ? Latency * 1000.0 / _inputFormat.SampleRate : 0;
    #endregion

    #region 构造
    /// <summary>初始化重采样器（线性档）</summary>
    /// <param name="outputSampleRate">目标采样率（Hz）</param>
    /// <param name="inputFormat">输入格式</param>
    public Resampler(Int32 outputSampleRate, AudioFormat inputFormat = null)
        : this(outputSampleRate, inputFormat, ResamplerQuality.Linear) { }

    /// <summary>初始化重采样器</summary>
    /// <param name="outputSampleRate">目标采样率（Hz）</param>
    /// <param name="inputFormat">输入格式</param>
    /// <param name="quality">质量档</param>
    public Resampler(Int32 outputSampleRate, AudioFormat inputFormat, ResamplerQuality quality)
    {
        if (outputSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(outputSampleRate));

        _inputFormat = inputFormat?.Clone() ?? AudioFormat.Default;
        _outputFormat = _inputFormat.Clone();
        _outputFormat.SampleRate = outputSampleRate;
        _quality = quality;
        _ratio = (Double)_inputFormat.SampleRate / outputSampleRate;
    }
    #endregion

    #region 方法
    /// <summary>读取重采样后的数据</summary>
    /// <param name="buffer">输出缓冲区（单声道样本流）</param>
    /// <param name="offset">缓冲区起始偏移</param>
    /// <param name="count">要产出的采样数</param>
    /// <returns>实际产出的采样数，0 表示上游结束</returns>
    public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
    {
        if (Source == null) return 0;

        var produced = 0;
        var scale = _quality == ResamplerQuality.Sinc ? Math.Max(1.0, _ratio) : 1.0;
        var taps = (Int32)Math.Ceiling(SincHalfTaps * scale);

        while (produced < count)
        {
            var index = (Int32)Math.Floor(_position);
            var required = index + taps + 1;
            if (!Fill(required)) break;

            if (_quality == ResamplerQuality.Sinc)
                buffer[offset + produced] = Interpolate(_position, taps, scale);
            else
            {
                var frac = (Single)(_position - index);
                var x0 = _history[index];
                var x1 = index + 1 < _historyCount ? _history[index + 1] : x0;
                buffer[offset + produced] = x0 + (x1 - x0) * frac;
            }

            produced++;
            _position += _ratio;

            // 丢弃已经不再参与插值的历史样本
            var drop = (Int32)Math.Floor(_position) - taps;
            if (drop > 0) Drop(drop);
        }

        return produced;
    }

    /// <summary>重置</summary>
    public void Reset()
    {
        _historyCount = 0;
        _position = 0;
    }
    #endregion

    #region 辅助
    private Boolean Fill(Int32 required)
    {
        while (_historyCount < required)
        {
            if (_history.Length < required)
            {
                var capacity = _history.Length;
                while (capacity < required) capacity <<= 1;
                Array.Resize(ref _history, capacity);
            }

            var read = Source.Read(_history, _historyCount, _history.Length - _historyCount);
            if (read <= 0) return false;

            _historyCount += read;
        }

        return true;
    }

    private void Drop(Int32 count)
    {
        if (count <= 0) return;

        if (count >= _historyCount)
        {
            _historyCount = 0;
            _position -= count;
            if (_position < 0) _position = 0;
            return;
        }

        Array.Copy(_history, count, _history, 0, _historyCount - count);
        _historyCount -= count;
        _position -= count;
        if (_position < 0) _position = 0;
    }

    private Single Interpolate(Double position, Int32 taps, Double scale)
    {
        var center = (Int32)Math.Floor(position);
        var sum = 0.0;
        var weight = 0.0;

        for (var k = -taps; k <= taps; k++)
        {
            var index = center + k;
            if (index < 0 || index >= _historyCount) continue;

            var distance = (position - index) / scale;
            if (distance <= -SincHalfTaps || distance >= SincHalfTaps) continue;

            var w = Sinc(distance) * Blackman(distance / SincHalfTaps);
            sum += _history[index] * w;
            weight += w;
        }

        // 归一化保证直流增益为 1，避免边界处音量塌陷
        return weight == 0 ? 0 : (Single)(sum / weight);
    }

    private static Double Sinc(Double x)
    {
        if (Math.Abs(x) < 1e-12) return 1;

        var angle = Math.PI * x;
        return Math.Sin(angle) / angle;
    }

    private static Double Blackman(Double t)
    {
        if (t <= -1 || t >= 1) return 0;

        var x = (t + 1) / 2;
        return 0.42 - 0.5 * Math.Cos(2 * Math.PI * x) + 0.08 * Math.Cos(4 * Math.PI * x);
    }
    #endregion

    #region 静态方法
    /// <summary>静态方法：重采样 16-bit PCM 数据（线性插值）</summary>
    /// <param name="pcm">输入PCM</param>
    /// <param name="inputRate">输入采样率</param>
    /// <param name="outputRate">输出采样率</param>
    /// <returns>重采样后的PCM</returns>
    public static Byte[] ResamplePcm(Byte[] pcm, Int32 inputRate, Int32 outputRate) =>
        ResamplePcm(pcm, inputRate, outputRate, ResamplerQuality.Linear);

    /// <summary>静态方法：重采样 16-bit PCM 数据</summary>
    /// <param name="pcm">输入PCM（16 位小端，单声道样本流）</param>
    /// <param name="inputRate">输入采样率</param>
    /// <param name="outputRate">输出采样率</param>
    /// <param name="quality">质量档</param>
    /// <returns>重采样后的PCM</returns>
    public static Byte[] ResamplePcm(Byte[] pcm, Int32 inputRate, Int32 outputRate, ResamplerQuality quality)
    {
        if (pcm == null) throw new ArgumentNullException(nameof(pcm));
        if (inputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        if (outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(outputRate));
        if (pcm.Length < 2) return pcm ?? [];

        var inputSamples = pcm.Length / 2;
        var outputSamples = (Int32)((Int64)inputSamples * outputRate / inputRate);
        if (outputSamples <= 0) return [];

        var input = new Single[inputSamples];
        for (var i = 0; i < inputSamples; i++) input[i] = (Int16)(pcm[i * 2] | pcm[i * 2 + 1] << 8) / 32768f;

        var ratio = (Double)inputRate / outputRate;
        var scale = quality == ResamplerQuality.Sinc ? Math.Max(1.0, ratio) : 1.0;
        var taps = (Int32)Math.Ceiling(SincHalfTaps * scale);

        var output = new Byte[outputSamples * 2];
        for (var i = 0; i < outputSamples; i++)
        {
            var position = i * ratio;
            var center = (Int32)Math.Floor(position);

            var value = 0.0;
            if (quality == ResamplerQuality.Sinc)
            {
                var sum = 0.0;
                var weight = 0.0;
                for (var k = -taps; k <= taps; k++)
                {
                    var index = center + k;
                    if (index < 0 || index >= inputSamples) continue;

                    var distance = (position - index) / scale;
                    if (distance <= -SincHalfTaps || distance >= SincHalfTaps) continue;

                    var w = Sinc(distance) * Blackman(distance / SincHalfTaps);
                    sum += input[index] * w;
                    weight += w;
                }
                value = weight == 0 ? 0 : sum / weight;
            }
            else
            {
                var frac = position - center;
                var x0 = input[center];
                var x1 = center + 1 < inputSamples ? input[center + 1] : x0;
                value = x0 + (x1 - x0) * frac;
            }

            var sample = (Int32)Math.Round(value * 32767);
            if (sample > Int16.MaxValue) sample = Int16.MaxValue;
            if (sample < Int16.MinValue) sample = Int16.MinValue;

            output[i * 2] = (Byte)(sample & 0xFF);
            output[i * 2 + 1] = (Byte)((sample >> 8) & 0xFF);
        }

        return output;
    }
    #endregion
}
