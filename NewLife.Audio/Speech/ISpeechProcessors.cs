namespace NewLife.Audio.Speech;

/// <summary>声学回声消除器接口（远期规划）</summary>
public interface IAcousticEchoCanceller
{
    void ProcessFarEnd(Byte[] farEnd);
    Byte[] ProcessNearEnd(Byte[] nearEnd);
    Int32 FilterLength { get; }
    void Reset();
}

/// <summary>噪声抑制器接口（远期规划）</summary>
public interface INoiseSuppressor
{
    Byte[] Suppress(Byte[] audio);
    Single LearningRate { get; set; }
    Single SuppressionLevelDB { get; set; }
    void Reset();
}
