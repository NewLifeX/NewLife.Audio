using NewLife.Data;

namespace NewLife.Audio.Speech;

/// <summary>语音活动检测器接口</summary>
public interface IVoiceActivityDetector
{
    /// <summary>检测灵敏度（0~3，3最激进）</summary>
    Int32 Aggressiveness { get; set; }

    /// <summary>判断是否为语音帧</summary>
    /// <param name="frame">音频帧数据（16-bit PCM）</param>
    /// <returns>true=语音，false=静音</returns>
    Boolean IsSpeech(Packet frame);

    /// <summary>获取语音概率（0.0~1.0）</summary>
    /// <param name="frame">音频帧数据</param>
    /// <returns>语音概率</returns>
    Single GetSpeechProbability(Packet frame);

    /// <summary>重置检测器状态</summary>
    void Reset();
}
