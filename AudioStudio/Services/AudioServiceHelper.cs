using NewLife.Audio;
using NewLife.Audio.Codecs;

namespace AudioStudio.Services;

/// <summary>音频服务辅助。提供共享的编解码器注册表等全局资源</summary>
public static class AudioServiceHelper
{
    /// <summary>共享编解码器注册表。包含所有内置编解码器</summary>
    public static CodecRegistry CodecRegistry { get; }

    static AudioServiceHelper()
    {
        CodecRegistry = new CodecRegistry();

        // 注册内置编解码器（与 AudioCodecFactory 对齐）
        CodecRegistry.Register(AVTypes.ADPCMA, new ADPCMCodec());
        CodecRegistry.Register(AVTypes.G711A, new G711ACodec());
        CodecRegistry.Register(AVTypes.G711U, new G711UCodec());
        CodecRegistry.Register(AVTypes.G722, new G722Codec());
        CodecRegistry.Register(AVTypes.G726, new G726Codec());
        CodecRegistry.Register(AVTypes.MP3, new Mp3Codec());
        CodecRegistry.Register(AVTypes.MPEGAUDIO, new Mp3Codec());
        CodecRegistry.Register(AVTypes.AAC, new AacCodec());
        CodecRegistry.Register(AVTypes.AACLC, new AacCodec());
        CodecRegistry.Register(AVTypes.HEAAC, new AacCodec());
        CodecRegistry.Register(AVTypes.Transparent, new OpusCodec());
        // LPCM 无需编解码器，数据已是 PCM
    }
}
