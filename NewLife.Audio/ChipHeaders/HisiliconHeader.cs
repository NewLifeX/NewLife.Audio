using NewLife.Buffers;
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
    public Boolean TryTrim(ReadOnlySpan<Byte> data, out IPacket result)
    {
        if (data.Length >= 4)
        {
            if (data[0] == Magic[0] && data[1] == Magic[1] && data[3] == 0x00 && data[2] == (Byte)((data.Length - 4) / 2))
            {
                result = new ArrayPacket(data.Slice(4).ToArray());
                return true;
            }
        }

        result = null;
        return false;
    }

    /// <summary>添加海思头</summary>
    /// <param name="data">原始音频数据</param>
    /// <param name="result">添加头后的数据</param>
    /// <returns></returns>
    public Boolean TryAdd(ReadOnlySpan<Byte> data, out IPacket result)
    {
        var total = 4 + data.Length;
        var buf = new Byte[total];
        var writer = new SpanWriter(buf); // 默认小端

        writer.WriteByte(0x00);
        writer.WriteByte(0x01);
        writer.WriteByte((Byte)(data.Length / 2));
        writer.WriteByte(0x00);
        writer.Write(data);

        result = new ArrayPacket(buf);
        return true;
    }
}
