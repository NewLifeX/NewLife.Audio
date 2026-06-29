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
    public Boolean TryTrim(Packet data, out Packet result)
    {
        if (data.Total >= 6)
        {
            var buf = data.ReadBytes(0, 4);
            if (buf[0] == Magic[0] && buf[1] == Magic[1] && buf[2] == Magic[2] && buf[3] == Magic[3])
            {
                result = data.ReadBytes(6, data.Total - 6);
                return true;
            }
        }

        result = data;
        return false;
    }

    /// <summary>添加大华头</summary>
    public Boolean TryAdd(Packet data, out Packet result)
    {
        var buf = new Byte[6];
        buf[0] = 0x00;
        buf[1] = 0x01;
        buf[2] = 0x01;
        buf[3] = 0x00;
        buf[4] = (Byte)(data.Total & 0xFF);
        buf[5] = (Byte)((data.Total >> 8) & 0xFF);

        var pk = new Packet(buf);
        pk.Append(data);

        result = pk;
        return true;
    }
}
