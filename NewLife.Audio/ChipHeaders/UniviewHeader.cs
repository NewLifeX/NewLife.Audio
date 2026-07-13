using NewLife.Buffers;
using NewLife.Data;

namespace NewLife.Audio.ChipHeaders;

/// <summary>宇视芯片音频头处理器</summary>
/// <remarks>
/// 宇视芯片编码音频格式：[0x24, 0x01, 0x00, 0x00]，共 4 字节。
/// </remarks>
public class UniviewHeader : IAudioChipHeader
{
    private static readonly Byte[] Magic = [0x24, 0x01, 0x00, 0x00];

    /// <summary>宇视头固定 4 字节</summary>
    public Int32 HeaderSize => 4;

    /// <summary>尝试去除宇视头</summary>
    public Boolean TryTrim(ReadOnlySpan<Byte> data, out IPacket result)
    {
        if (data.Length >= 4)
        {
            if (data[0] == Magic[0] && data[1] == Magic[1] && data[2] == Magic[2] && data[3] == Magic[3])
            {
                result = new ArrayPacket(data.Slice(4).ToArray());
                return true;
            }
        }

        result = ArrayPacket.Empty;
        return false;
    }

    /// <summary>添加宇视头</summary>
    public Boolean TryAdd(ReadOnlySpan<Byte> data, out IPacket result)
    {
        var total = 4 + data.Length;
        var buf = new Byte[total];
        var writer = new SpanWriter(buf); // 默认小端

        writer.WriteByte(0x24);
        writer.WriteByte(0x01);
        writer.WriteByte(0x00);
        writer.WriteByte(0x00);
        writer.Write(data);

        result = new ArrayPacket(buf);
        return true;
    }
}
