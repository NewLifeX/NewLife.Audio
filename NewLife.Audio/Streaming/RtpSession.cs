namespace NewLife.Audio.Streaming;

/// <summary>RTP 会话管理。维护 SSRC、序列号、时间戳状态</summary>
/// <remarks>
/// 每个 RTP 流一个会话实例。SSRC 随机生成，序列号随机起始（RFC 3550 建议），时间戳随机起始。
/// </remarks>
public class RtpSession
{
    private static readonly Random _rnd = new();

    private UInt16 _sequenceNumber;
    private UInt32 _timestamp;
    private readonly UInt32 _ssrc;

    /// <summary>同步源标识符（随机生成）</summary>
    public UInt32 Ssrc => _ssrc;

    /// <summary>当前序列号</summary>
    public UInt16 CurrentSequenceNumber => _sequenceNumber;

    /// <summary>当前时间戳</summary>
    public UInt32 CurrentTimestamp => _timestamp;

    /// <summary>初始化 RTP 会话</summary>
    public RtpSession()
    {
        _ssrc = (UInt32)_rnd.Next();
        _sequenceNumber = (UInt16)_rnd.Next();
        _timestamp = (UInt32)_rnd.Next();
    }

    /// <summary>获取并递增序列号</summary>
    /// <returns>当前序列号</returns>
    public UInt16 NextSequenceNumber()
    {
        var seq = _sequenceNumber;
        _sequenceNumber++;
        return seq;
    }

    /// <summary>获取并递增时间戳</summary>
    /// <param name="increment">时间戳增量（采样数）</param>
    /// <returns>当前时间戳</returns>
    public UInt32 NextTimestamp(UInt32 increment)
    {
        var ts = _timestamp;
        _timestamp += increment;
        return ts;
    }

    /// <summary>重置会话状态</summary>
    public void Reset()
    {
        _sequenceNumber = (UInt16)_rnd.Next();
        _timestamp = (UInt32)_rnd.Next();
    }
}
