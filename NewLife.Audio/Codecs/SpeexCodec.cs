using NewLife.Data;

namespace NewLife.Audio.Codecs;

/// <summary>窄带语音编解码器（20ms 帧 + 自适应差分量化，纯托管）</summary>
/// <remarks>
/// 面向 8kHz 单声道窄带语音的轻量编解码器：每 20ms（160 样本）一帧，帧内按 4 bit/样本走
/// 自适应差分量化（<see cref="AdpcmQuantizer"/>），压缩比 4:1、码率 32kbps。
///
/// 码流为本库自定义格式，与 libspeex 的比特流互不兼容：多帧码流按「2 字节小端长度 + 帧数据」拼接，
/// 单帧裸数据仍可直接解码（长度前缀解析失败时按单帧处理）。
/// 早期版本采用 LPC 分析 + 反射系数量化 + 随机激励重建，往返后与原始波形相关性接近 0，
/// 现已换成自适应差分量化，稳定性与保真度都由编解码往返门禁守护。
/// </remarks>
public class SpeexCodec : IAudioCodec, ICodecInfo
{
    #region 常量
    private const Int32 FrameSizeSamples = 160;
    private const Int32 QuantBits = 4;
    private const Int32 FrameBytes = FrameSizeSamples * QuantBits / 8;
    #endregion

    #region 属性
    /// <summary>编解码器名称</summary>
    public String Name => "Speex (Narrowband)";

    /// <summary>版本号</summary>
    public String Version => "1.0";

    /// <summary>支持的编码类型</summary>
    public IReadOnlyCollection<AVTypes> SupportedTypes { get; } = [AVTypes.Transparent];

    /// <summary>无状态编解码器（每次调用自建量化状态）</summary>
    public Boolean IsStateful => false;
    #endregion

    #region 解码
    /// <summary>窄带码流转 PCM</summary>
    /// <param name="audio">编码数据，可含长度前缀的多帧</param>
    /// <param name="option">保留</param>
    /// <returns>16-bit PCM @ 8kHz 单声道</returns>
    public IPacket ToPcm(ReadOnlySpan<Byte> audio, Object option)
    {
        if (audio.Length < 4) return ArrayPacket.Empty;

        // 多帧码流：每帧前置 2 字节小端长度；解析失败按单帧裸数据处理
        var frames = TryParseFrames(audio);
        if (frames == null)
        {
            var single = new List<Byte[]>(1) { audio.ToArray() };
            frames = single;
        }

        var ms = new MemoryStream(frames.Count * FrameSizeSamples * 2);

        // 量化状态跨帧延续，避免每 20ms 重新收敛带来的台阶失真
        var quantizer = new AdpcmQuantizer(QuantBits);
        foreach (var frame in frames)
        {
            var pcm = DecodeFrame(frame, quantizer);
            ms.Write(pcm, 0, pcm.Length);
        }

        ms.Position = 0;
        return new ArrayPacket(ms);
    }

    /// <summary>解析带长度前缀的多帧码流</summary>
    /// <param name="audio">码流</param>
    /// <returns>各帧数据；不是合法的多帧码流时返回 null，交由单帧路径处理</returns>
    private static List<Byte[]>? TryParseFrames(ReadOnlySpan<Byte> audio)
    {
        var list = new List<Byte[]>();
        var offset = 0;
        while (offset + 2 <= audio.Length)
        {
            var length = audio[offset] | audio[offset + 1] << 8;
            if (length <= 0 || offset + 2 + length > audio.Length) return null;

            list.Add(audio.Slice(offset + 2, length).ToArray());
            offset += 2 + length;
        }

        // 必须恰好消费完整段数据，否则说明是裸帧而不是带前缀的多帧码流
        return list.Count > 0 && offset == audio.Length ? list : null;
    }

    private static Byte[] DecodeFrame(ReadOnlySpan<Byte> data, AdpcmQuantizer quantizer)
    {
        var pcm = new Byte[FrameSizeSamples * 2];
        var count = Math.Min(FrameSizeSamples, data.Length * 8 / QuantBits);

        // 帧内样本数固定为 160，数据不足时按静音补齐，保持每个码字帧对应同样时长
        for (var i = 0; i < count; i++)
        {
            var bitIndex = i * QuantBits;
            var code = data[bitIndex >> 3] >> (8 - QuantBits - (bitIndex & 7)) & ((1 << QuantBits) - 1);

            var sample = quantizer.Decode(code);
            pcm[i * 2] = (Byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (Byte)(sample >> 8 & 0xFF);
        }

        return pcm;
    }
    #endregion

    #region 编码
    /// <summary>PCM 转窄带码流</summary>
    /// <remarks>每 160 样本一帧，输出为「长度前缀 + 帧数据」的多帧码流，便于 ToPcm 逐帧还原</remarks>
    /// <param name="pcm">16-bit PCM @ 8kHz</param>
    /// <param name="option">保留</param>
    /// <returns>编码数据</returns>
    public IPacket FromPcm(ReadOnlySpan<Byte> pcm, Object option)
    {
        var ms = new MemoryStream();
        var offset = 0;
        var framePcmBytes = FrameSizeSamples * 2;

        // 量化状态跨帧延续，与解码端保持一致
        var quantizer = new AdpcmQuantizer(QuantBits);
        while (offset + framePcmBytes <= pcm.Length)
        {
            var frame = EncodeFrame(pcm.Slice(offset, framePcmBytes), quantizer);
            ms.WriteByte((Byte)(frame.Length & 0xFF));
            ms.WriteByte((Byte)(frame.Length >> 8 & 0xFF));
            ms.Write(frame, 0, frame.Length);
            offset += framePcmBytes;
        }

        ms.Position = 0;
        return new ArrayPacket(ms);
    }

    private static Byte[] EncodeFrame(ReadOnlySpan<Byte> pcm, AdpcmQuantizer quantizer)
    {
        var frame = new Byte[FrameBytes];

        for (var i = 0; i < FrameSizeSamples; i++)
        {
            var sample = (Int16)(pcm[i * 2 + 1] << 8 | pcm[i * 2]);
            var code = quantizer.Encode(sample);

            var bitIndex = i * QuantBits;
            frame[bitIndex >> 3] |= (Byte)(code << (8 - QuantBits - (bitIndex & 7)));
        }

        return frame;
    }
    #endregion
}
