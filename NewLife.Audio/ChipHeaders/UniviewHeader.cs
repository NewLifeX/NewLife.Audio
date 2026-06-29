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
    public Boolean TryTrim(Packet data, out Packet result)
    {
        if (data.Total >= 4)
        {
            var buf = data.ReadBytes(0, 4);
            if (buf[0] == Magic[0] && buf[1] == Magic[1] && buf[2] == Magic[2] && buf[3] == Magic[3])
            {
                result = data.ReadBytes(4, data.Total - 4);
                return true;
            }
        }

        result = data;
        return false;
    }

    /// <summary>添加宇视头</summary>
    public Boolean TryAdd(Packet data, out Packet result)
    {
        var buf = new Byte[4];
        buf[0] = 0x24;
        buf[1] = 0x01;
        buf[2] = 0x00;
        buf[3] = 0x00;

        var pk = new Packet(buf);
        pk.Append(data);

        result = pk;
        return true;
    }
}
