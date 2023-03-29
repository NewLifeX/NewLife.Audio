using NewLife.Data;

namespace NewLife.Audio;

/// <summary>音频编码接口</summary>
public interface IAudioCodec
{
    /// <summary>音频数据转PCM</summary>
    /// <param name="audio"></param>
    /// <param name="option"></param>
    /// <returns></returns>
    Packet ToPcm(Packet audio, Object option);

    /// <summary>PCM转音频数据</summary>
    /// <param name="pcm"></param>
    /// <param name="option"></param>
    /// <returns></returns>
    Packet FromPcm(Packet pcm, Object option);
}