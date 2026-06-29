using NewLife.Data;

namespace NewLife.Audio;

/// <summary>芯片音频头处理接口</summary>
/// <remarks>
/// IoT 芯片（海思/大华/宇视等）在音频编码数据前附加厂商自定义头部。
/// 实现此接口以支持自动识别和去除/还原芯片头。
/// </remarks>
public interface IAudioChipHeader
{
    /// <summary>芯片头字节数</summary>
    Int32 HeaderSize { get; }

    /// <summary>尝试去除芯片头</summary>
    /// <param name="data">含芯片头的音频数据</param>
    /// <param name="result">去除头后的数据</param>
    /// <returns>是否成功去除</returns>
    Boolean TryTrim(ReadOnlySpan<Byte> data, out IPacket result);

    /// <summary>尝试添加芯片头</summary>
    /// <param name="data">原始音频数据</param>
    /// <param name="result">添加头后的数据</param>
    /// <returns>是否成功添加</returns>
    Boolean TryAdd(ReadOnlySpan<Byte> data, out IPacket result);
}
