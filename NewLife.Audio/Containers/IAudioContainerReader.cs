using NewLife.Data;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Containers;

/// <summary>音频容器读取器接口</summary>
public interface IAudioContainerReader : IDisposable
{
    /// <summary>音频格式</summary>
    AudioFormat Format { get; }

    /// <summary>编码类型</summary>
    AVTypes CodecType { get; }

    /// <summary>总帧数</summary>
    Int64 TotalFrames { get; }

    /// <summary>总时长（秒）</summary>
    Double Duration { get; }

    /// <summary>元数据</summary>
    AudioMetadata Metadata { get; }

    /// <summary>读取下一帧编码数据</summary>
    /// <returns>编码数据帧，null表示结束</returns>
    IPacket ReadFrame();

    /// <summary>定位到指定帧</summary>
    /// <param name="frameIndex">帧索引</param>
    void SeekFrame(Int64 frameIndex);
}
