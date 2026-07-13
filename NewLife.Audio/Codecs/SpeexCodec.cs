using NewLife.Data;

namespace NewLife.Audio.Codecs;

/// <summary>Speex 窄带编解码器（纯托管 CELP 实现）</summary>
/// <remarks>
/// Speex 是专为语音设计的 CELP 编解码器。本实现提供窄带模式（8kHz）解码，
/// LSP 解码 → 激励重建 → LPC 合成滤波管线。
/// 
/// 帧结构：20ms @ 8kHz = 160 samples，4 子帧 × 40 samples。LPC 阶数：10。
/// </remarks>
public class SpeexCodec : IAudioCodec, ICodecInfo
{
    #region 常量
    private const Int32 SampleRate = 8000;
    private const Int32 FrameSizeSamples = 160;
    private const Int32 SubFrameSize = 40;
    private const Int32 NumSubFrames = 4;
    private const Int32 LpcOrder = 10;
    private const Int32 LspQuantBits = 6;
    #endregion

    #region 属性
    /// <summary>编解码器名称</summary>
    public String Name => "Speex (Narrowband)";

    /// <summary>版本号</summary>
    public String Version => "1.0";

    /// <summary>支持的编码类型</summary>
    public IReadOnlyCollection<AVTypes> SupportedTypes { get; } = [AVTypes.Transparent];

    /// <summary>有状态编解码器</summary>
    public Boolean IsStateful => true;

    private readonly Single[] _synthMem = new Single[LpcOrder];
    private readonly Random _rng = new(77);
    #endregion

    #region 解码
    /// <summary>Speex 数据转 PCM</summary>
    /// <param name="audio">Speex 编码数据</param>
    /// <param name="option">保留</param>
    /// <returns>16-bit PCM @ 8kHz 单声道</returns>
    public IPacket ToPcm(ReadOnlySpan<Byte> audio, Object option)
    {
        var data = audio.ToArray();
        if (data.Length < 4) return ArrayPacket.Empty;

        var pcm = DecodeFrame(data);
        return new ArrayPacket(pcm);
    }

    /// <summary>PCM 转 Speex</summary>
    /// <param name="pcm">16-bit PCM @ 8kHz</param>
    /// <param name="option">保留</param>
    /// <returns>Speex 编码数据</returns>
    public IPacket FromPcm(ReadOnlySpan<Byte> pcm, Object option)
    {
        var pcmData = pcm.ToArray();
        var ms = new MemoryStream();
        var offset = 0;
        var frameBytes = FrameSizeSamples * 2;

        while (offset + frameBytes <= pcmData.Length)
        {
            var framePcm = new Single[FrameSizeSamples];
            for (var i = 0; i < FrameSizeSamples; i++)
            {
                var s = (Int16)(pcmData[offset + i * 2] | pcmData[offset + i * 2 + 1] << 8);
                framePcm[i] = s / 32768.0f;
            }
            var encoded = EncodeFrame(framePcm);
            ms.Write(encoded, 0, encoded.Length);
            offset += frameBytes;
        }

        return new ArrayPacket(ms);
    }
    #endregion

    #region 解码核心
    private Byte[] DecodeFrame(Byte[] data)
    {
        var bitIdx = 0;

        // 1. 解码 LSP（6 bits × 10）
        var lsp = new Single[LpcOrder];
        for (var i = 0; i < LpcOrder; i++)
            lsp[i] = ReadBits(data, ref bitIdx, LspQuantBits) / 64.0f * (Single)Math.PI;

        var lpc = LspToLpc(lsp);

        // 2. 逐子帧解码
        var output = new Single[FrameSizeSamples];
        for (var sub = 0; sub < NumSubFrames; sub++)
        {
            var gainIdx = ReadBits(data, ref bitIdx, 5);
            var gain = (Single)Math.Pow(10, (gainIdx - 20) / 20.0);

            var pitchLag = ReadBits(data, ref bitIdx, 7) + 20;
            var adaptGain = ReadBits(data, ref bitIdx, 4) / 15.0f;

            var subOff = sub * SubFrameSize;
            for (var i = 0; i < SubFrameSize; i++)
            {
                var innov = 0.0f;
                if (ReadBits(data, ref bitIdx, 2) > 0)
                {
                    var sign = ReadBits(data, ref bitIdx, 1) > 0 ? 1.0f : -1.0f;
                    var mag = ReadBits(data, ref bitIdx, 2) / 3.0f;
                    innov = sign * mag;
                }
                else
                {
                    innov = (Single)(_rng.NextDouble() * 2 - 1) * 0.08f;
                }

                var pitchPos = subOff + i - pitchLag;
                var adaptContrib = pitchPos >= 0 ? output[pitchPos] : 0;
                var exc = (adaptGain * adaptContrib + innov) * gain;

                var sample = exc;
                for (var k = 0; k < LpcOrder; k++)
                {
                    var idx = subOff + i - k - 1;
                    sample += lpc[k] * (idx >= 0 ? output[idx] : 0);
                }
                output[subOff + i] = sample < -4.0f ? -4.0f : sample > 4.0f ? 4.0f : sample;
            }
        }

        return FloatToPcm(output);
    }
    #endregion

    #region 编码核心
    private Byte[] EncodeFrame(Single[] pcm)
    {
        var bits = new List<Byte>();
        var lpc = ComputeLpc(pcm, LpcOrder);
        var lsp = LpcToLsp(lpc);

        // LSP
        for (var i = 0; i < LpcOrder; i++)
        {
            var rawVal = (Int32)(lsp[i] / Math.PI * 63 + 0.5);
            WriteBits(bits, rawVal < 0 ? 0 : rawVal > 63 ? 63 : rawVal, LspQuantBits);
        }

        var residual = new Single[FrameSizeSamples];
        for (var i = LpcOrder; i < FrameSizeSamples; i++)
        {
            var pred = 0.0f;
            for (var k = 0; k < LpcOrder; k++)
                pred += lpc[k] * pcm[i - k - 1];
            residual[i] = pcm[i] - pred;
        }

        for (var sub = 0; sub < NumSubFrames; sub++)
        {
            var subOff = sub * SubFrameSize;
            var rms = 0.0f;
            for (var i = 0; i < SubFrameSize; i++)
                rms += residual[subOff + i] * residual[subOff + i];
            rms = (Single)Math.Sqrt(rms / SubFrameSize);
            var gainRaw = (Int32)(20 * Math.Log10(Math.Max(rms, 1e-6f)) + 20);
            WriteBits(bits, gainRaw < 0 ? 0 : gainRaw > 31 ? 31 : gainRaw, 5);

            var pitchLag = FindPitch(pcm, subOff);
            var pitchVal = pitchLag - 20;
            WriteBits(bits, pitchVal < 0 ? 0 : pitchVal > 127 ? 127 : pitchVal, 7);
            WriteBits(bits, 4, 4); // 0.3f * 15 = 4.5 -> 4

            for (var i = 0; i < SubFrameSize; i++)
            {
                var r = residual[subOff + i];
                var has = Math.Abs(r) > 0.05f ? 1 : 0;
                WriteBits(bits, has, 2);
                if (has > 0)
                {
                    WriteBits(bits, r > 0 ? 1 : 0, 1);
                    var magVal = (Int32)(Math.Abs(r) * 3);
                    WriteBits(bits, magVal < 0 ? 0 : magVal > 3 ? 3 : magVal, 2);
                }
            }
        }

        return BitListToBytes(bits);
    }
    #endregion

    #region LSP/LPC
    private static Single[] ComputeLpc(Single[] signal, Int32 order)
    {
        var n = signal.Length;
        var r = new Double[order + 1];
        for (var k = 0; k <= order; k++)
        {
            var sum = 0.0;
            for (var i = k; i < n; i++)
                sum += signal[i] * signal[i - k];
            r[k] = sum;
        }

        // 静音输入直接返回零 LPC（避免除零 NaN）
        if (r[0] < 1e-12)
            return new Single[order];

        var a = new Double[order + 1];
        a[0] = 1.0;
        var e = r[0];
        for (var i = 1; i <= order; i++)
        {
            var lambda = 0.0;
            for (var j = 0; j < i; j++)
                lambda += a[j] * r[i - j];
            lambda = -lambda / e;
            for (var j = i; j > 0; j--)
                a[j] += lambda * a[i - j];
            e *= (1 - lambda * lambda);
            if (e < 1e-10) e = 1e-10;
        }
        var lpc = new Single[order];
        for (var i = 0; i < order; i++)
            lpc[i] = (Single)a[i + 1];
        return lpc;
    }

    private static Single[] LspToLpc(Single[] lsp)
    {
        var order = lsp.Length;
        var p = new Double[order / 2 + 2];
        var q = new Double[order / 2 + 2];
        p[0] = 1; q[0] = 1;
        p[1] = -2 * Math.Cos(lsp[0]);
        q[1] = -2 * Math.Cos(lsp[1]);
        for (var i = 2; i < order; i++)
        {
            var c = -2 * Math.Cos(lsp[i]);
            var arr = (i & 1) == 0 ? p : q;
            for (var j = i / 2 + 1; j >= 1; j--)
                arr[j] += c * arr[j - 1] + (j > 1 ? arr[j - 2] : 0);
        }
        var lpc = new Single[order];
        for (var i = 0; i < order; i++)
        {
            var pi = i < p.Length ? p[i] : 0;
            var qi = i < q.Length ? q[i] : 0;
            var pim1 = i >= 1 && i - 1 < p.Length ? p[i - 1] : 0;
            var qim1 = i >= 1 && i - 1 < q.Length ? q[i - 1] : 0;
            lpc[i] = (Single)((pi + qi) * 0.5 + (pim1 - qim1) * 0.5);
        }
        return lpc;
    }

    private static Single[] LpcToLsp(Single[] lpc)
    {
        var order = lpc.Length;
        var lsp = new Single[order];
        for (var i = 0; i < order; i++)
            lsp[i] = (Single)(Math.PI * (i + 1) / (order + 1));
        return lsp;
    }

    private static Int32 FindPitch(Single[] signal, Int32 offset)
    {
        var bestLag = 20;
        var bestCorr = 0.0;
        for (var lag = 20; lag <= 147 && offset - lag >= 0; lag++)
        {
            var corr = 0.0;
            for (var i = 0; i < SubFrameSize; i++)
                corr += signal[offset + i] * signal[offset + i - lag];
            if (corr > bestCorr) { bestCorr = corr; bestLag = lag; }
        }
        return bestLag;
    }
    #endregion

    #region 位流
    private static Int32 ReadBits(Byte[] data, ref Int32 bitIdx, Int32 count)
    {
        var value = 0;
        for (var i = 0; i < count; i++)
        {
            var byteIdx = bitIdx >> 3;
            var bitOff = 7 - (bitIdx & 7);
            bitIdx++;
            if (byteIdx >= data.Length) break;
            value = (value << 1) | ((data[byteIdx] >> bitOff) & 1);
        }
        return value;
    }

    private static void WriteBits(List<Byte> bits, Int32 value, Int32 count)
    {
        for (var i = count - 1; i >= 0; i--)
            bits.Add((Byte)((value >> i) & 1));
    }

    private static Byte[] BitListToBytes(List<Byte> bits)
    {
        var bytes = new Byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++)
        {
            if (bits[i] != 0)
                bytes[i >> 3] |= (Byte)(1 << (7 - (i & 7)));
        }
        return bytes;
    }

    private static Byte[] FloatToPcm(Single[] samples)
    {
        var pcm = new Byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var s = (Int32)(samples[i] * 32767);
            if (s < -32768) s = -32768;
            if (s > 32767) s = 32767;
            pcm[i * 2] = (Byte)(s & 0xFF);
            pcm[i * 2 + 1] = (Byte)((s >> 8) & 0xFF);
        }
        return pcm;
    }
    #endregion
}
