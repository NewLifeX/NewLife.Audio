namespace NewLife.Audio.Codecs;

/// <summary>MP3 编码码率模式</summary>
public enum Mp3RateMode
{
    /// <summary>固定比特率，每帧码率相同，不写信息帧</summary>
    Cbr = 0,

    /// <summary>可变比特率，按每帧信号复杂度自适应（同时写入 Xing 信息帧）</summary>
    Vbr = 1,

    /// <summary>平均比特率，围绕目标码率浮动（同时写入 Xing 信息帧）</summary>
    Abr = 2,
}
