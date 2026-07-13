using NewLife.Buffers;
using NewLife.Data;

namespace NewLife.Audio.ChipHeaders;

/// <summary>大华芯片音频头处理器</summary>
/// <remarks>
/// 大华芯片编码音频格式：[0x00, 0x01, 0x01, 0x00] + [dataLen LE-2B]，共 6 字节。
/// 兼容部分大华DVR/NVR设备输出。
/// </remarks>
public class DahuaHeader : IAudioChipHeader
{
    private static readonly Byte[] Magic = [0x00, 0x01, 0x01, 0x00];

    /// <summary>大华头 6 字节</summary>
    public Int32 HeaderSize => 6;

    /// <summary>尝试去除大华头</summary>
    public Boolean TryTrim(ReadOnlySpan<Byte> data, out IPacket result)
    {
        if (data.Length >= 6)
        {
            if (data[0] == Magic[0] && data[1] == Magic[1] && data[2] == Magic[2] && data[3] == Magic[3])
            {
                result = new ArrayPacket(data.Slice(6).ToArray());
                return true;
            }
        }

        result = ArrayPacket.Empty;
        return false;
    }

    /// <summary>添加大华头</summary>
    public Boolean TryAdd(ReadOnlySpan<Byte> data, out IPacket result)
    {
        var total = 6 + data.Length;
        var buf = new Byte[total];
        var writer = new SpanWriter(buf); // 默认小端

        writer.WriteByte(0x00);
        writer.WriteByte(0x01);
        writer.WriteByte(0x01);
        writer.WriteByte(0x00);
        writer.WriteByte((Byte)(data.Length & 0xFF));
        writer.WriteByte((Byte)((data.Length >> 8) & 0xFF));
        writer.Write(data);

        result = new ArrayPacket(buf);
        return true;
    }
}
