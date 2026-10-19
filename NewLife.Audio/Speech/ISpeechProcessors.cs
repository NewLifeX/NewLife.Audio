namespace NewLife.Audio.Speech;

/// <summary>声学回声消除器接口</summary>
public interface IAcousticEchoCanceller
{
    /// <summary>输入远端参考信号（扬声器播放的 PCM）</summary>
    /// <param name="farEnd">远端参考 PCM 数据</param>
    void ProcessFarEnd(Byte[] farEnd);

    /// <summary>处理近端采集信号并返回消除回声后的 PCM</summary>
    /// <param name="nearEnd">近端麦克风 PCM 数据</param>
    /// <returns>消除回声后的 PCM 数据</returns>
    Byte[] ProcessNearEnd(Byte[] nearEnd);

    /// <summary>自适应滤波器长度（采样点数）</summary>
    Int32 FilterLength { get; }

    /// <summary>重置滤波器状态</summary>
    void Reset();
}

/// <summary>噪声抑制器接口</summary>
public interface INoiseSuppressor
{
    /// <summary>对 PCM 数据执行噪声抑制</summary>
    /// <param name="audio">输入 PCM 数据</param>
    /// <returns>抑制噪声后的 PCM 数据</returns>
    Byte[] Suppress(Byte[] audio);

    /// <summary>噪声估计学习率，范围 0~1</summary>
    Single LearningRate { get; set; }

    /// <summary>抑制强度（dB），值越大抑制越强</summary>
    Single SuppressionLevelDB { get; set; }

    /// <summary>重置噪声估计状态</summary>
    void Reset();
}
