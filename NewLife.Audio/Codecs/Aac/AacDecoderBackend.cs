using SharpJaad.AAC;

namespace NewLife.Audio.Codecs;

/// <summary>AAC 解码后端。封装 SharpJaad.AAC（MIT、纯托管）实现 AAC-LC / HE-AAC v1/v2 / 多声道（5.1/7.1）解码</summary>
/// <remarks>
/// SharpJaad 是 JAAD 的 C# 移植，支持 AAC-LC、HE-AAC（SBR）与 HE-AAC v2（PS），
/// 输出 16-bit PCM。每个实例只服务一路流；切换编码参数时重新创建。
/// </remarks>
internal sealed class AacDecoderBackend
{
    private readonly Decoder _decoder;
    private readonly SampleBuffer _buffer;

    /// <summary>解码输出采样率（Hz）</summary>
    public Int32 SampleRate => _buffer.SampleRate;

    /// <summary>解码输出声道数</summary>
    public Int32 Channels => _buffer.Channels;

    /// <summary>解码输出位深</summary>
    public Int32 BitsPerSample => _buffer.BitsPerSample;

    /// <summary>当前流是否检测到参数化立体声（PS）</summary>
    public Boolean IsPsPresent => _decoder.IsPSPresent;

    private AacDecoderBackend(Decoder decoder)
    {
        _decoder = decoder;
        _buffer = new SampleBuffer();
        _buffer.SetBigEndian(false);
    }

    /// <summary>从 AudioSpecificConfig 创建解码器</summary>
    /// <param name="asc">AudioSpecificConfig 字节</param>
    /// <returns>解码后端</returns>
    public static AacDecoderBackend FromAudioSpecificConfig(ReadOnlySpan<Byte> asc)
    {
        try
        {
            return new AacDecoderBackend(new Decoder(asc.ToArray()));
        }
        catch (AACException ex)
        {
            throw new InvalidDataException("Invalid AAC AudioSpecificConfig.", ex);
        }
    }

    /// <summary>从 ADTS 帧头创建解码器</summary>
    /// <param name="adts">ADTS 帧头</param>
    /// <returns>解码后端</returns>
    public static AacDecoderBackend FromAdts(AacCodec.AdtsInfo adts)
    {
        var config = new DecoderConfig();
        config.SetProfile((Profile)adts.Profile);
        config.SetSampleFrequency((SampleFrequency)adts.SampleRateIndex);
        config.SetChannelConfiguration((ChannelConfiguration)adts.Channels);
        // ADTS 没有 ASC，无法显式信令 SBR/PS，只能从码流自动检测
        config.SetSBRAutoDetect(true);

        try
        {
            return new AacDecoderBackend(new Decoder(config));
        }
        catch (AACException ex)
        {
            throw new InvalidDataException("Unsupported AAC profile or configuration.", ex);
        }
    }

    /// <summary>解码一个 AAC 原始数据块（不含 ADTS 头）</summary>
    /// <param name="rawFrame">AAC raw_data_block</param>
    /// <returns>16-bit 小端 PCM</returns>
    public Byte[] Decode(ReadOnlySpan<Byte> rawFrame)
    {
        try
        {
            _decoder.DecodeFrame(rawFrame.ToArray(), _buffer);
        }
        catch (AACException ex)
        {
            throw new InvalidDataException("Failed to decode the AAC frame: " + ex.Message, ex);
        }

        return _buffer.Data;
    }
}
