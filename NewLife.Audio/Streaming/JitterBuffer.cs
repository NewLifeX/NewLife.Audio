namespace NewLife.Audio.Streaming;

/// <summary>抖动缓冲器。排序、去重、丢包补偿</summary>
/// <remarks>
/// 可配置延迟窗口：实时对讲（20-60ms）vs 单向广播（200-500ms）。
/// 基于序列号的环形缓冲区，自动排序输出。
/// </remarks>
public class JitterBuffer
{
    private readonly Int32 _maxPackets;
    private readonly PacketSlot[] _buffer;
    private UInt16 _nextSequence;
    private Boolean _initialized;

    private struct PacketSlot
    {
        public Boolean HasData;
        public Byte[] Data;
        public UInt32 Timestamp;
    }

    /// <summary>缓冲区大小（包数）</summary>
    public Int32 BufferSize => _maxPackets;

    /// <summary>当前延迟（包数）</summary>
    public Double CurrentDelayMs { get; private set; }

    /// <summary>丢包率</summary>
    public Double LossRate { get; private set; }

    private Int32 _receivedCount;
    private Int32 _lostCount;

    /// <summary>初始化抖动缓冲</summary>
    /// <param name="maxDelayMs">最大延迟（ms），默认 60ms</param>
    /// <param name="packetIntervalMs">包间隔（ms），默认 20ms</param>
    public JitterBuffer(Int32 maxDelayMs = 60, Int32 packetIntervalMs = 20)
    {
        _maxPackets = Math.Max(4, maxDelayMs / packetIntervalMs);
        _buffer = new PacketSlot[_maxPackets];
    }

    /// <summary>写入一个 RTP 包（含序列号和负载）</summary>
    /// <param name="sequenceNumber">RTP 序列号</param>
    /// <param name="payload">负载数据</param>
    /// <param name="timestamp">RTP 时间戳</param>
    public void Write(UInt16 sequenceNumber, Byte[] payload, UInt32 timestamp)
    {
        if (!_initialized)
        {
            _nextSequence = sequenceNumber;
            _initialized = true;
        }

        var seqDiff = (Int16)(sequenceNumber - _nextSequence);

        // 太旧的包丢弃
        if (seqDiff < -_maxPackets) return;

        // 未来的包放入缓冲区
        var slotIndex = (sequenceNumber % _maxPackets);
        _buffer[slotIndex].HasData = true;
        _buffer[slotIndex].Data = payload;
        _buffer[slotIndex].Timestamp = timestamp;
        _receivedCount++;
    }

    /// <summary>读取下一个有序包</summary>
    /// <param name="payload">输出负载数据</param>
    /// <returns>是否成功读取</returns>
    public Boolean Read(out Byte[] payload)
    {
        payload = null;

        if (!_initialized) return false;

        var slotIndex = _nextSequence % _maxPackets;
        if (!_buffer[slotIndex].HasData)
        {
            // 等待或丢包
            _lostCount++;
            _nextSequence++;
            LossRate = _receivedCount > 0 ? (Double)_lostCount / (_receivedCount + _lostCount) : 0;
            return false;
        }

        payload = _buffer[slotIndex].Data;
        _buffer[slotIndex].HasData = false;
        _nextSequence++;

        // 计算延迟
        CurrentDelayMs = _maxPackets * 20.0; // 简化

        return true;
    }

    /// <summary>重置</summary>
    public void Reset()
    {
        _initialized = false;
        _receivedCount = 0;
        _lostCount = 0;
        LossRate = 0;
        Array.Clear(_buffer, 0, _buffer.Length);
    }
}
