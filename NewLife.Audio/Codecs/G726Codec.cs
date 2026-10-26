using NewLife.Data;
using NewLife;

namespace NewLife.Audio.Codecs;

/// <summary>G.726 ADPCM 语音编解码器（16/24/32/40 kbps）</summary>
/// <remarks>
/// 自适应差分脉冲编码调制：2/3/4/5 bit 每样本对应 16/24/32/40 kbps。
/// 相比 G.711 提供更高压缩比，相比 IMA ADPCM 提供可配的量化级数。
/// 预测器为一阶积分器并使用简化步长自适应，码流与 ITU-T G.726 并不互通，
/// 适合库内自洽的窄带语音压缩与存储；与外部 G.726 设备互通需换成标准量化与预测表。
/// </remarks>
public class G726Codec : IAudioCodec, ICodecInfo
{
    /// <summary>编码比特率（2/3/4/5 bits per sample）</summary>
    private readonly Int32 _bitsPerSample;

    /// <summary>量化级数</summary>
    private readonly Int32 _levels;

    /// <summary>编解码器名称</summary>
    public String Name => $"G.726-{_bitsPerSample * 8}k";

    /// <summary>版本号</summary>
    public String Version => "1.0";

    /// <summary>支持的编码类型</summary>
    public IReadOnlyCollection<AVTypes> SupportedTypes { get; } = [AVTypes.G726];

    /// <summary>无状态编解码器（每次调用自建量化状态）</summary>
    public Boolean IsStateful => false;

    /// <summary>初始化 G.726 编解码器</summary>
    /// <param name="bitsPerSample">每样本比特数：2(16kbps)、3(24kbps)、4(32kbps)、5(40kbps)，默认4</param>
    public G726Codec(Int32 bitsPerSample = 4)
    {
        if (bitsPerSample < 2 || bitsPerSample > 5)
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), L.T("The bit count must be between 2 and 5."));

        _bitsPerSample = bitsPerSample;
        _levels = 1 << bitsPerSample;
    }

    /// <summary>G.726音频数据转PCM</summary>
    /// <param name="audio">G.726 编码数据</param>
    /// <param name="option"></param>
    /// <returns>16-bit PCM</returns>
    public IPacket ToPcm(ReadOnlySpan<Byte> audio, Object option)
    {
        var pcm = new Byte[audio.Length * 2 * 8 / _bitsPerSample];
        var outIdx = 0;

        // 每次调用自建量化状态，保证解码端与编码端从同一零点起步
        var quantizer = new AdpcmQuantizer(_bitsPerSample);

        Int32 buffer = 0;
        var bitsInBuffer = 0;

        for (var i = 0; i < audio.Length; i++)
        {
            buffer = (buffer << 8) | audio[i];
            bitsInBuffer += 8;

            while (bitsInBuffer >= _bitsPerSample && outIdx < pcm.Length - 1)
            {
                bitsInBuffer -= _bitsPerSample;
                var code = (buffer >> bitsInBuffer) & (_levels - 1);

                var sample = quantizer.Decode(code);
                pcm[outIdx++] = (Byte)(sample & 0xFF);
                pcm[outIdx++] = (Byte)((sample >> 8) & 0xFF);
            }
        }

        return new ArrayPacket(pcm);
    }

    /// <summary>PCM转G.726音频数据</summary>
    /// <param name="pcm">16-bit PCM</param>
    /// <param name="option"></param>
    /// <returns>G.726 编码数据</returns>
    public IPacket FromPcm(ReadOnlySpan<Byte> pcm, Object option)
    {
        var sampleCount = pcm.Length / 2;
        var outputBits = sampleCount * _bitsPerSample;
        var output = new Byte[(outputBits + 7) / 8];

        var outIdx = 0;
        var buffer = 0;
        var bitsInBuffer = 0;

        // 每次调用自建量化状态，保证编码端与解码端从同一零点起步
        var quantizer = new AdpcmQuantizer(_bitsPerSample);

        for (var i = 0; i < pcm.Length - 1; i += 2)
        {
            var sample = (Int16)(pcm[i + 1] << 8 | pcm[i]);
            var code = quantizer.Encode(sample);

            buffer = (buffer << _bitsPerSample) | code;
            bitsInBuffer += _bitsPerSample;

            if (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                output[outIdx++] = (Byte)(buffer >> bitsInBuffer);
                buffer &= (1 << bitsInBuffer) - 1;
            }
        }

        // 填充剩余位（不足一字节则右移）
        if (bitsInBuffer > 0)
        {
            buffer <<= 8 - bitsInBuffer;
            output[outIdx++] = (Byte)buffer;
        }

        return new ArrayPacket(output);
    }
}
