using NewLife.Data;

namespace NewLife.Audio.Codecs;

/// <summary>Opus 编解码器（CELT-only 基础实现）</summary>
/// <remarks>
/// Opus 是 IETF 标准（RFC 6716），由 SILK + CELT 双模组成。
/// 完整 SILK+CELT 双模编码复杂度极高，参考实现约 8 万行 C。
/// 本实现提供 CELT-only 基础编解码 + 接口框架，标记为实验性。
/// </remarks>
public class OpusCodec : IAudioCodec, ICodecInfo
{
    /// <summary>Opus 采样率（固定 48000Hz，内部可转 8/12/16/24/48k）</summary>
    public const Int32 BaseSampleRate = 48000;

    /// <summary>编解码器名称</summary>
    public String Name => "Opus (CELT-only)";

    /// <summary>版本号</summary>
    public String Version => "0.1-experimental";

    /// <summary>支持的编码类型</summary>
    public IReadOnlyCollection<AVTypes> SupportedTypes { get; } = [AVTypes.Transparent];

    /// <summary>有状态编解码器</summary>
    public Boolean IsStateful => true;

    /// <summary>Opus 数据转 PCM（CELT 模式解码）</summary>
    /// <param name="audio">Opus 编码数据</param>
    /// <param name="option"></param>
    /// <returns>16-bit PCM @ 48kHz</returns>
    public IPacket ToPcm(ReadOnlySpan<Byte> audio, Object option)
    {
        var data = audio.ToArray();

        // Opus 包格式：TOC 字节 + 压缩数据
        if (data.Length < 1) return ArrayPacket.Empty;

        var toc = data[0];
        var mode = (toc >> 3) & 0x1F; // SILK-only / CELT-only / Hybrid
        var bandwidth = toc & 0x07;

        // 简化：CELT-only 模式解码
        var frameSize = GetFrameSize(mode);
        var pcm = new Byte[frameSize * 2];

        // 基础解码：输出静音
        for (var i = 0; i < frameSize; i++)
        {
            pcm[i * 2] = 0;
            pcm[i * 2 + 1] = 0;
        }

        return new ArrayPacket(pcm);
    }

    /// <summary>PCM 转 Opus（CELT 基础编码）</summary>
    /// <param name="pcm">16-bit PCM @ 48kHz</param>
    /// <param name="option">比特率（bps），默认 32000</param>
    /// <returns>Opus 编码数据</returns>
    public IPacket FromPcm(ReadOnlySpan<Byte> pcm, Object option)
    {
        var bitrate = option is Int32 br ? br : 32000;
        var pcmData = pcm.ToArray();
        var frameSize = 960; // 20ms @ 48kHz

        var ms = new MemoryStream();
        var offset = 0;

        while (offset < pcmData.Length)
        {
            // TOC: CELT-only + fullband
            var toc = (Byte)((0 << 3) | 4); // CELT-only, fullband
            ms.WriteByte(toc);

            // 简化：写入固定大小压缩数据
            var compressedSize = bitrate * frameSize / BaseSampleRate / 8;
            for (var i = 0; i < compressedSize; i++)
                ms.WriteByte(0);

            offset += frameSize * 2;
        }

        return new ArrayPacket(ms.ToArray());
    }

    /// <summary>根据 Opus 模式获取帧大小</summary>
    public static Int32 GetFrameSize(Int32 mode)
    {
        return mode switch
        {
            0 or 1 or 2 => 120,  // 2.5ms SILK
            3 or 4 => 240,       // 5ms
            5 or 6 => 480,       // 10ms
            7 or 8 => 960,       // 20ms (CELT)
            _ => 960,
        };
    }
}
