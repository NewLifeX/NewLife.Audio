namespace NewLife.Audio.Speech;

/// <summary>声学回声消除器（纯托管 NLMS 自适应滤波器实现）</summary>
/// <remarks>
/// 基于归一化最小均方（NLMS）算法的回声消除。
/// 维护一个自适应 FIR 滤波器，估计扬声器到麦克风的回声路径。
/// 处理流程：远端正播 → 滤波器估计回声 → 近端减去回声估计。
/// 
/// 包含双讲检测：当近远端同时有语音时暂停滤波器更新，防止发散。
/// 滤波器长度默认 256 taps（@16kHz ≈ 16ms 回声尾）。
/// </remarks>
public class AcousticEchoCanceller : IAcousticEchoCanceller
{
    #region 属性
    private readonly Int32 _filterLength;
    private readonly Single[] _filter;
    private readonly Single[] _farEndBuffer;
    private Int32 _bufferPos;
    private Single _stepSize;
    private Single _energyThreshold;
    private readonly Random _rng;

    /// <summary>滤波器长度（taps 数）</summary>
    public Int32 FilterLength => _filterLength;

    /// <summary>自适应步长（0-1），默认 0.1</summary>
    public Single StepSize
    {
        get => _stepSize;
        set => _stepSize = value < 0.001f ? 0.001f : value > 1.0f ? 1.0f : value;
    }

    /// <summary>双讲检测阈值（远/近端能量比）</summary>
    public Single DoubleTalkThreshold { get; set; } = 0.5f;

    /// <summary>回声返回损耗增强（ERLE）估计（dB）</summary>
    public Single ErleEstimate { get; private set; }
    #endregion

    #region 构造
    /// <summary>初始化回声消除器</summary>
    /// <param name="filterLength">滤波器长度（taps），默认 256</param>
    /// <param name="stepSize">自适应步长，默认 0.1</param>
    public AcousticEchoCanceller(Int32 filterLength = 256, Single stepSize = 0.1f)
    {
        _filterLength = filterLength;
        _filter = new Single[filterLength];
        _farEndBuffer = new Single[filterLength * 2];
        _stepSize = stepSize < 0.001f ? 0.001f : stepSize > 1.0f ? 1.0f : stepSize;
        _rng = new Random(12345);

        // 初始化滤波器中心为 1
        _filter[0] = 0.5f;
        _filter[1] = 0.3f;
        _filter[2] = 0.15f;

        ErleEstimate = 0;
    }
    #endregion

    #region IAcousticEchoCanceller
    /// <summary>输入远端信号（扬声器播放的音频）</summary>
    /// <param name="farEnd">远端 PCM 16-bit 样本</param>
    public void ProcessFarEnd(Byte[] farEnd)
    {
        var samples = new Single[farEnd.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (Int16)(farEnd[i * 2] | farEnd[i * 2 + 1] << 8) / 32768.0f;

        ProcessFarEndFloat(samples);
    }

    /// <summary>处理近端信号（麦克风采集的音频，含回声），返回消除回声后的信号</summary>
    /// <param name="nearEnd">近端 PCM 16-bit 样本</param>
    /// <returns>消除回声后的 PCM 16-bit 样本</returns>
    public Byte[] ProcessNearEnd(Byte[] nearEnd)
    {
        var samples = new Single[nearEnd.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (Int16)(nearEnd[i * 2] | nearEnd[i * 2 + 1] << 8) / 32768.0f;

        var result = ProcessNearEndFloat(samples);

        var output = new Byte[result.Length * 2];
        for (var i = 0; i < result.Length; i++)
        {
            var s = (Int32)(result[i] * 32767);
            if (s < -32768) s = -32768;
            if (s > 32767) s = 32767;
            output[i * 2] = (Byte)(s & 0xFF);
            output[i * 2 + 1] = (Byte)((s >> 8) & 0xFF);
        }
        return output;
    }

    /// <summary>重置滤波器状态</summary>
    public void Reset()
    {
        Array.Clear(_filter, 0, _filter.Length);
        Array.Clear(_farEndBuffer, 0, _farEndBuffer.Length);
        _bufferPos = 0;
        _filter[0] = 0.5f;
        _filter[1] = 0.3f;
        _filter[2] = 0.15f;
        ErleEstimate = 0;
    }
    #endregion

    #region 核心算法
    /// <summary>浮点远端处理</summary>
    private void ProcessFarEndFloat(ReadOnlySpan<Single> farEnd)
    {
        // 环形缓冲远端信号
        for (var i = 0; i < farEnd.Length; i++)
        {
            _farEndBuffer[_bufferPos] = farEnd[i];
            _bufferPos = (_bufferPos + 1) % _farEndBuffer.Length;
        }
    }

    /// <summary>浮点近端处理（NLMS + 双讲检测）</summary>
    private Single[] ProcessNearEndFloat(ReadOnlySpan<Single> nearEnd)
    {
        var n = nearEnd.Length;
        var output = new Single[n];
        var farEnergy = 0.0f;
        var nearEnergy = 0.0f;
        var errEnergy = 0.0f;

        for (var i = 0; i < n; i++)
        {
            // 1. 计算回声估计（FIR 滤波）
            var echoEstimate = 0.0f;
            for (var k = 0; k < _filterLength; k++)
            {
                var bufIdx = (_bufferPos - 1 - i - k + _farEndBuffer.Length * 2) % _farEndBuffer.Length;
                echoEstimate += _filter[k] * _farEndBuffer[bufIdx];
            }

            // 2. 误差 = 近端 - 回声估计
            var error = nearEnd[i] - echoEstimate;
            output[i] = error;

            // 3. 能量累积（用于双讲检测和 NLMS 归一化）
            var fe = 0.0f;
            for (var k = 0; k < _filterLength; k++)
            {
                var bufIdx = (_bufferPos - 1 - i - k + _farEndBuffer.Length * 2) % _farEndBuffer.Length;
                var val = _farEndBuffer[bufIdx];
                fe += val * val;
            }
            farEnergy += fe;
            nearEnergy += nearEnd[i] * nearEnd[i];
            errEnergy += error * error;

            // 4. 双讲检测
            var isDoubleTalk = nearEnergy > farEnergy * DoubleTalkThreshold && errEnergy > farEnergy * 0.1f;

            // 5. NLMS 滤波器更新（仅在无双讲时更新）
            if (!isDoubleTalk && fe > 1e-6f)
            {
                var mu = _stepSize / (fe + 1e-6f);
                for (var k = 0; k < _filterLength; k++)
                {
                    var bufIdx = (_bufferPos - 1 - i - k + _farEndBuffer.Length * 2) % _farEndBuffer.Length;
                    _filter[k] += mu * error * _farEndBuffer[bufIdx];
                }
            }

            // 6. 舒适噪声注入（回声消除后填补静音间隙）
            if (Math.Abs(error) < 0.001f && Math.Abs(nearEnd[i]) < 0.001f)
            {
                output[i] += (Single)(_rng.NextDouble() * 2 - 1) * 0.0005f;
            }
        }

        // 更新 ERLE 估计
        if (nearEnergy > 1e-6f)
        {
            var erle = 10 * Math.Log10(Math.Max(nearEnergy / Math.Max(errEnergy, 1e-10f), 1.0));
            ErleEstimate = (Single)(ErleEstimate * 0.9 + erle * 0.1);
        }

        return output;
    }
    #endregion
}
