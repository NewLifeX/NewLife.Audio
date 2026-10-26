using NewLife.Audio.Codecs;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Tts;

/// <summary>MP3 响应解码</summary>
/// <remarks>
/// 在线服务常直接返回 MP3。解码交给内置的 Layer III 解码器，并按实际音频回填采样率与声道数，
/// 供播放器初始化；数据不是有效 MP3 时返回空音频。
/// </remarks>
internal static class TtsMp3
{
    /// <summary>把 MP3 字节解码为 16 位 PCM</summary>
    /// <param name="data">MP3 数据</param>
    /// <param name="format">输出格式，按实际音频回填采样率与声道数</param>
    /// <returns>PCM 数据，无法解码时为空数组</returns>
    public static Byte[] Decode(Byte[] data, AudioFormat format)
    {
        if (data == null || data.Length == 0) return [];

        var decoder = new Mp3Layer3Decoder();
        var pcm = decoder.Decode(data, out var sampleRate, out var channels);
        if (pcm.Length == 0) return [];

        if (sampleRate > 0) format.SampleRate = sampleRate;
        if (channels > 0) format.Channels = channels;

        return pcm;
    }
}
