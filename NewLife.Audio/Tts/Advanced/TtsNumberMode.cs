namespace NewLife.Audio.Tts.Advanced;

/// <summary>数字读法</summary>
public enum TtsNumberMode
{
    /// <summary>自动。四位年份与长号码逐位读，其余按数值读</summary>
    Auto = 0,

    /// <summary>按数值读。如 2024 读“二千零二十四”</summary>
    Cardinal = 1,

    /// <summary>逐位读。如 2024 读“二零二四”</summary>
    Digit = 2,
}
