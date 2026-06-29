using NewLife.Audio.DSP;

namespace NewLife.Audio.Speech;

/// <summary>频谱减法噪声抑制器。基于 FFT 的频谱减法算法，抑制稳态背景噪声</summary>
/// <remarks>
/// 算法流程：分帧 → 加窗 → FFT → 噪声谱估计（前N帧指数平均）→ 幅度谱减法 → IFFT → OLA合成。<br/>
/// 实现 INoiseSuppressor 接口，纯 C# 实现，复用 FftAnalyzer 的 FFT/IFFT 能力。<br/>
/// 适用于 16-bit PCM 单声道语音降噪场景（8k/16kHz）。
/// </remarks>
public class SpectralNoiseSuppressor : INoiseSuppressor
{
    private readonly Int32 _fftSize;
    private readonly Int32 _hopSize;
    private readonly FftAnalyzer _fft;
    private Single _learningRate;
    private Single _suppressionLevelDB;

    // 噪声谱估计（幅度）
    private Single[] _noiseMagnitude;

    // 重叠相加缓冲区
    private Single[] _olaBuffer;
    private Int32 _olaWritePos;

    // 前几帧用于初始噪声估计
    private Int32 _initFrames;
    private const Int32 InitFrameCount = 10;

    // 窗函数（用于 OLA 合成时的叠加窗）
    private readonly Single[] _synthesisWindow;

    /// <summary>噪声学习率（0~1，控制噪声谱估计更新速度）</summary>
    public Single LearningRate
    {
        get => _learningRate;
        set => _learningRate = Math.Max(0f, Math.Min(1f, value));
    }

    /// <summary>抑制强度（dB），默认 12dB</summary>
    public Single SuppressionLevelDB
    {
        get => _suppressionLevelDB;
        set => _suppressionLevelDB = Math.Max(0f, Math.Min(40f, value));
    }

    /// <summary>初始化频谱减法噪声抑制器</summary>
    /// <param name="sampleRate">采样率（Hz），默认 16000</param>
    /// <param name="fftSize">FFT 点数（2的幂），默认 512（~32ms @16kHz）</param>
    public SpectralNoiseSuppressor(Int32 sampleRate = 16000, Int32 fftSize = 512)
    {
        _fftSize = fftSize;
        _hopSize = fftSize / 2; // 50% 重叠
        _learningRate = 0.1f;
        _suppressionLevelDB = 12f;

        var format = new AudioFormat { SampleRate = sampleRate, Channels = 1, BitsPerSample = 16 };
        _fft = new FftAnalyzer(fftSize, FftAnalyzer.WindowType.Hann, format);

        _noiseMagnitude = new Single[fftSize / 2 + 1];
        _olaBuffer = new Single[fftSize * 2];
        _olaWritePos = 0;

        // 合成窗（Hann 窗用于 OLA）
        _synthesisWindow = new Single[fftSize];
        for (var i = 0; i < fftSize; i++)
            _synthesisWindow[i] = 0.5f * (1f - (Single)Math.Cos(2f * Math.PI * i / (fftSize - 1)));
    }

    /// <summary>执行噪声抑制</summary>
    /// <param name="audio">PCM 音频数据（Int16 字节数组）</param>
    /// <returns>降噪后的 PCM 音频数据</returns>
    public Byte[] Suppress(Byte[] audio)
    {
        if (audio == null || audio.Length < _fftSize * 2) return audio;

        // 转换 Int16 → Float
        var sampleCount = audio.Length / 2;
        var floatSamples = new Single[sampleCount];
        for (var i = 0; i < sampleCount; i++)
            floatSamples[i] = (Int16)(audio[i * 2] | (audio[i * 2 + 1] << 8)) / 32768f;

        var output = new Single[sampleCount];
        var outputPos = 0;

        // 分帧处理
        for (var frameStart = 0; frameStart + _fftSize <= sampleCount; frameStart += _hopSize)
        {
            var frame = new Single[_fftSize];
            Array.Copy(floatSamples, frameStart, frame, 0, _fftSize);

            ProcessFrame(frame, output, outputPos);
            outputPos += _hopSize;
        }

        // 转换 Float → Int16
        var result = new Byte[sampleCount * 2];
        for (var i = 0; i < sampleCount; i++)
        {
            var val = (Int16)(Math.Max(-1f, Math.Min(1f, output[i])) * 32767f);
            result[i * 2] = (Byte)(val & 0xFF);
            result[i * 2 + 1] = (Byte)((val >> 8) & 0xFF);
        }

        return result;
    }

    /// <summary>重置噪声估计</summary>
    public void Reset()
    {
        Array.Clear(_noiseMagnitude, 0, _noiseMagnitude.Length);
        Array.Clear(_olaBuffer, 0, _olaBuffer.Length);
        _olaWritePos = 0;
        _initFrames = 0;
    }

    private void ProcessFrame(Single[] frame, Single[] output, Int32 outputPos)
    {
        // 正向 FFT → 复数频谱
        _fft.ComputeComplexSpectrum(frame, out var real, out var imag);

        // 计算幅度谱
        var magnitude = new Single[_fftSize / 2 + 1];
        for (var i = 0; i < magnitude.Length; i++)
            magnitude[i] = (Single)Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]);

        // 初始噪声估计（前 InitFrameCount 帧取平均）
        if (_initFrames < InitFrameCount)
        {
            for (var i = 0; i < magnitude.Length; i++)
                _noiseMagnitude[i] = (_noiseMagnitude[i] * _initFrames + magnitude[i]) / (_initFrames + 1);
            _initFrames++;
        }
        else
        {
            // 更新噪声估计（指数平均，仅当当前帧能量较低时更新）
            var frameEnergy = 0f;
            for (var i = 0; i < magnitude.Length; i++)
                frameEnergy += magnitude[i];

            var noiseEnergy = 0f;
            for (var i = 0; i < _noiseMagnitude.Length; i++)
                noiseEnergy += _noiseMagnitude[i];

            // 若当前帧能量接近噪声能量，更新噪声估计
            if (frameEnergy < noiseEnergy * 2f)
            {
                var rate = _learningRate;
                for (var i = 0; i < magnitude.Length; i++)
                    _noiseMagnitude[i] = (1f - rate) * _noiseMagnitude[i] + rate * magnitude[i];
            }
        }

        // 频谱减法
        var suppressionFactor = (Single)Math.Pow(10, -_suppressionLevelDB / 20f);
        for (var i = 0; i < magnitude.Length; i++)
        {
            var cleanMag = magnitude[i] - _noiseMagnitude[i] * suppressionFactor;
            if (cleanMag < 0f) cleanMag = magnitude[i] * 0.01f; // 底噪

            // 保持原始相位，修改幅度
            if (magnitude[i] > 1e-10f)
            {
                var scale = cleanMag / magnitude[i];
                real[i] *= scale;
                imag[i] *= scale;
            }

            // 共轭对称填充后半部分（用于 IFFT）
            if (i > 0 && i < _fftSize / 2)
            {
                var conjIdx = _fftSize - i;
                real[conjIdx] = real[i];
                imag[conjIdx] = -imag[i];
            }
        }

        // 逆 FFT → 时域
        var timeDomain = _fft.ComputeInverseFft(real, imag);

        // OLA 重叠相加合成
        for (var i = 0; i < _fftSize; i++)
        {
            var outIdx = outputPos + i;
            if (outIdx < output.Length)
                output[outIdx] += timeDomain[i] * _synthesisWindow[i];
        }
    }
}
