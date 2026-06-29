using NewLife.Data;

namespace NewLife.Audio.ChipHeaders;

/// <summary>海思芯片音频头处理器</summary>
/// <remarks>
/// 海思芯片编码音频格式：[0x00, 0x01, (dataLen/2), 0x00]，共 4 字节。
/// 常见于海思Hi3516/Hi3518等芯片的G.711/ADPCM编码输出。
/// </remarks>
public class HisiliconHeader : IAudioChipHeader
{
    private static readonly Byte[] Magic = [0x00, 0x01];

    /// <summary>海思头固定 4 字节</summary>
    public Int32 HeaderSize => 4;

    /// <summary>尝试去除海思头</summary>
    /// <param name="data">可能含海思头的音频数据</param>
    /// <param name="result">去除头后的数据</param>
    /// <returns></returns>
    public Boolean TryTrim(Packet data, out Packet result)
    {
        if (data.Total >= 4)
        {
            var buf = data.ReadBytes(0, 4);
            if (buf[0] == Magic[0] && buf[1] == Magic[1] && buf[3] == 0x00 && buf[2] == (Byte)((data.Total - 4) / 2))
            {
                result = data.ReadBytes(4, data.Total - 4);
                return true;
            }
        }

        result = data;
        return false;
    }

    /// <summary>添加海思头</summary>
    /// <param name="data">原始音频数据</param>
    /// <param name="result">添加头后的数据</param>
    /// <returns></returns>
    public Boolean TryAdd(Packet data, out Packet result)
    {
        var buf = new Byte[4];
        buf[0] = 0x00;
        buf[1] = 0x01;
        buf[2] = (Byte)(data.Total / 2);
        buf[3] = 0x00;

        var pk = new Packet(buf);
        pk.Append(data);

        result = pk;
        return true;
    }
}
