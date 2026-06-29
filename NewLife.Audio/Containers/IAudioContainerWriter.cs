using NewLife.Data;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Containers;

/// <summary>音频容器写入器接口</summary>
public interface IAudioContainerWriter : IDisposable
{
    /// <summary>音频格式</summary>
    AudioFormat Format { get; }

    /// <summary>写入元数据</summary>
    /// <param name="metadata">音频元数据</param>
    void WriteMetadata(AudioMetadata metadata);

    /// <summary>写入一帧编码数据</summary>
    /// <param name="frame">编码数据帧</param>
    void WriteFrame(Packet frame);

    /// <summary>完成写入并刷新缓冲区</summary>
    void Flush();
}
