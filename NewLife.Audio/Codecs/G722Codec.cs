using System.Runtime.InteropServices;
using NewLife.Data;

namespace NewLife.Audio.Codecs;

/// <summary>G.722 宽带语音编解码器（SB-ADPCM，16kHz采样率，64kbps）</summary>
/// <remarks>
/// 子带自适应差分脉冲编码调制：把 16kHz 输入按相邻样本和/差分成两个 8kHz 子带，
/// 高子带用 2-bit ADPCM、低子带用 6-bit ADPCM，合计 64kbps。
///
/// 子带拆分采用 2 抽头哈尔滤波（和/差），是精确重建的简化 QMF，而非标准 24 阶正交镜像滤波器；
/// 加上量化与自适应表为自研参数，码流与 ITU-T G.722 并不互通，适合库内自洽的宽带语音压缩与存储。
/// 早期实现的量化器步长只增不减，往返信噪比接近 0 dB，现已换成共用的 <see cref="AdpcmQuantizer"/>。
/// </remarks>
public class G722Codec : IAudioCodec, ICodecInfo
{
    private const Int32 LowerBandBits = 6;
    private const Int32 UpperBandBits = 2;
    private const Int32 SampleRate = 16000;

    /// <summary>编解码器名称</summary>
    public String Name => "G.722 SB-ADPCM";

    /// <summary>版本号</summary>
    public String Version => "1.0";

    /// <summary>支持的编码类型</summary>
    public IReadOnlyCollection<AVTypes> SupportedTypes { get; } = [AVTypes.G722];

    /// <summary>无状态编解码器（纯算法，无内部状态累积）</summary>
    public Boolean IsStateful => false;

    /// <summary>G.722音频数据转PCM（16kHz 16-bit）</summary>
    /// <param name="audio">G.722 编码数据（每字节编码2个样本的高低子带）</param>
    /// <param name="option"></param>
    /// <returns>16kHz 16-bit PCM</returns>
    public IPacket ToPcm(ReadOnlySpan<Byte> audio, Object option)
    {
        // G.722 每字节 = 2个样本的高低子带编码值
        var sampleCount = audio.Length * 2;
        var pcm = new Byte[sampleCount * 2];

        var lowerQuantizer = new AdpcmQuantizer(LowerBandBits);
        var upperQuantizer = new AdpcmQuantizer(UpperBandBits);

        var samplesOut = MemoryMarshal.Cast<Byte, Int16>(pcm.AsSpan());
        for (Int32 i = 0, outIdx = 0; i < audio.Length; i++)
        {
            var b = audio[i];

            // 高6位为低子带，低2位为高子带
            var il = (b >> 2) & 0x3F; // 6-bit 低子带
            var ih = b & 0x03;         // 2-bit 高子带

            // ADPCM 解码
            var lowerSample = lowerQuantizer.Decode(il);
            var upperSample = upperQuantizer.Decode(ih);

            // QMF 合成（简化为直接组合）
            var sample1 = Clip16((Int32)(lowerSample + upperSample));
            var sample2 = Clip16((Int32)(lowerSample - upperSample));

            samplesOut[outIdx++] = sample1;
            samplesOut[outIdx++] = sample2;
        }

        return new ArrayPacket(pcm);
    }

    /// <summary>PCM转G.722音频数据</summary>
    /// <param name="pcm">16kHz 16-bit PCM</param>
    /// <param name="option"></param>
    /// <returns>G.722 编码数据</returns>
    public IPacket FromPcm(ReadOnlySpan<Byte> pcm, Object option)
    {
        var sampleCount = pcm.Length / 2;
        var output = new Byte[sampleCount / 2];

        var lowerQuantizer = new AdpcmQuantizer(LowerBandBits);
        var upperQuantizer = new AdpcmQuantizer(UpperBandBits);

        var samplesIn = MemoryMarshal.Cast<Byte, Int16>(pcm);
        for (Int32 i = 0, outIdx = 0; i < samplesIn.Length - 1; i += 2)
        {
            var s1 = samplesIn[i];
            var s2 = samplesIn[i + 1];

            // QMF 分析（简化为直接分量）
            var lowerSample = (s1 + s2) / 2.0;
            var upperSample = (s1 - s2) / 2.0;

            var il = lowerQuantizer.Encode((Int32)lowerSample);
            var ih = upperQuantizer.Encode((Int32)upperSample);

            output[outIdx++] = (Byte)((il << 2) | ih);
        }

        return new ArrayPacket(output);
    }

    private static Int16 Clip16(Int32 val)
    {
        if (val > 32767) return 32767;
        if (val < -32768) return -32768;
        return (Int16)val;
    }
}
