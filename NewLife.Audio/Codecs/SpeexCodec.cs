using NewLife.Data;

namespace NewLife.Audio.Codecs;

/// <summary>Speex 编解码器接口（暂缓实现）</summary>
/// <remarks>
/// Speex 是专为语音设计的开源编解码格式，已被 IETF Opus 替代。
/// Opus 在所有场景下均优于 Speex：更低延迟、更好的音质、更宽的比特率范围。
/// 保留此接口以支持遗留系统迁移，推荐新项目使用 OpusCodec。
/// </remarks>
public class SpeexCodec : IAudioCodec, ICodecInfo
{
    /// <summary>编解码器名称</summary>
    public String Name => "Speex (Legacy)";

    /// <summary>版本号</summary>
    public String Version => "0.1-stub";

    /// <summary>支持的编码类型</summary>
    public IReadOnlyCollection<AVTypes> SupportedTypes { get; } = Array.Empty<AVTypes>();

    /// <summary>有状态编解码器</summary>
    public Boolean IsStateful => true;

    /// <summary>Speex 解码（迁移提示）</summary>
    public Packet ToPcm(Packet audio, Object option)
        => throw new NotSupportedException("Speex 已停止维护。建议使用 Opus (OpusCodec) 替代。https://opus-codec.org/");

    /// <summary>Speex 编码（迁移提示）</summary>
    public Packet FromPcm(Packet pcm, Object option)
        => throw new NotSupportedException("Speex 已停止维护。建议使用 Opus (OpusCodec) 替代。https://opus-codec.org/");
}
