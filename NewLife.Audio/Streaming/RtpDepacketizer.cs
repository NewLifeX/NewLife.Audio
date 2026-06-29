using NewLife.Data;

namespace NewLife.Audio.Streaming;

/// <summary>RTP 解包器。将 RTP 包组装为完整编码音频帧</summary>
/// <remarks>
/// 按序列号排序接收，自动检测丢包。支持单包帧和多包分片帧的重组。<br/>
/// Marker 位标记帧尾，用于触发帧输出。
/// </remarks>
public class RtpDepacketizer
{
    private readonly Dictionary<UInt16, RtpPacket> _pendingPackets = [];
    private UInt16 _expectedSeq;
    private Boolean _initialized;

    /// <summary>丢包率（丢失包数 / 期望接收数）</summary>
    public Single LossRate { get; private set; }

    /// <summary>总接收包数</summary>
    public Int64 TotalReceived { get; private set; }

    /// <summary>总丢失包数</summary>
    public Int64 TotalLost { get; private set; }

    /// <summary>提交一个 RTP 包，若凑齐完整帧则输出</summary>
    /// <param name="packet">RTP 包</param>
    /// <param name="completeFrame">输出的完整编码帧（若有）</param>
    /// <returns>是否产生了完整帧</returns>
    public Boolean Submit(RtpPacket packet, out Byte[] completeFrame)
    {
        completeFrame = null;
        TotalReceived++;

        var seq = packet.SequenceNumber;

        // 初始化期望序列号
        if (!_initialized)
        {
            _expectedSeq = seq;
            _initialized = true;
        }

        // 检测丢包
        if (seq != _expectedSeq)
        {
            var lost = (UInt16)(seq - _expectedSeq);
            TotalLost += lost;
            _expectedSeq = (UInt16)(seq + 1);
        }
        else
        {
            _expectedSeq++;
        }

        // 更新丢包率
        if (TotalReceived > 0)
            LossRate = (Single)TotalLost / (TotalReceived + TotalLost);

        // 缓存包
        _pendingPackets[seq] = packet;

        // 检查是否 Marker 位（帧尾）
        if (packet.Marker)
        {
            // 收集从起始序列号到当前序列号的所有包
            var startSeq = FindFrameStart(seq);
            if (startSeq <= seq)
            {
                var totalLen = 0;
                for (var s = startSeq; s <= seq; s++)
                {
                    if (_pendingPackets.TryGetValue(s, out var p))
                        totalLen += p.Payload?.Length ?? 0;
                }

                if (totalLen > 0)
                {
                    completeFrame = new Byte[totalLen];
                    var offset = 0;
                    for (var s = startSeq; s <= seq; s++)
                    {
                        if (_pendingPackets.TryGetValue(s, out var p) && p.Payload != null)
                        {
                            Array.Copy(p.Payload, 0, completeFrame, offset, p.Payload.Length);
                            offset += p.Payload.Length;
                        }
                        _pendingPackets.Remove(s);
                    }
                    return true;
                }
            }

            // 清理
            for (var s = startSeq; s <= seq; s++)
                _pendingPackets.Remove(s);
        }

        return false;
    }

    /// <summary>重置解包器状态</summary>
    public void Reset()
    {
        _pendingPackets.Clear();
        _initialized = false;
        TotalReceived = 0;
        TotalLost = 0;
        LossRate = 0;
    }

    /// <summary>查找帧起始序列号（向前搜索非 Marker 包的开头）</summary>
    private UInt16 FindFrameStart(UInt16 markerSeq)
    {
        var start = markerSeq;
        // 向前搜索直到找不到连续的非Marker包
        for (var s = (UInt16)(markerSeq - 1); s != markerSeq; s--)
        {
            if (!_pendingPackets.TryGetValue(s, out var p)) break;
            if (p.Marker) break; // 遇到上一个帧的 Marker
            start = s;
        }
        return start;
    }
}
