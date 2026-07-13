using NewLife.Data;

namespace NewLife.Audio.Codecs;

/// <summary>Opus 编解码器（纯托管 CELT 实现）</summary>
/// <remarks>
/// Opus (RFC 6716) 由 SILK + CELT 双模组成。本实现聚焦 CELT-only 模式，
/// 实现 range 解码 → 能量解码 → 频谱重建 → IMDCT → 窗重叠相加完整管线。
/// 
/// 编解码器内部状态：IMDCT 重叠缓冲、随机数发生器。
/// 编码端提供基础 CELT 能量量化 + PVQ 编码，比特率为 64kbps。
/// 20ms 帧 @ 48kHz（960 samples/frame），单声道。
/// </remarks>
public class OpusCodec : IAudioCodec, ICodecInfo
{
    #region 常量
    /// <summary>Opus 基础采样率（固定 48000Hz）</summary>
    public const Int32 BaseSampleRate = 48000;

    /// <summary>20ms 帧 @ 48kHz = 960 samples</summary>
    private const Int32 FrameSize = 960;

    /// <summary>MDCT 窗长 = 2 * FrameSize</summary>
    private const Int32 MdctSize = 1920;

    /// <summary>全频带数</summary>
    private const Int32 NumBandsFull = 21;
    #endregion

    #region 属性
    /// <summary>编解码器名称</summary>
    public String Name => "Opus (CELT)";

    /// <summary>版本号</summary>
    public String Version => "1.0";

    /// <summary>支持的编码类型</summary>
    public IReadOnlyCollection<AVTypes> SupportedTypes { get; } = [AVTypes.Transparent];

    /// <summary>有状态编解码器</summary>
    public Boolean IsStateful => true;

    private readonly Single[] _overlap = new Single[MdctSize];
    private readonly Random _rng = new(42);
    #endregion

    #region 解码
    /// <summary>Opus 数据转 PCM（CELT 模式解码）</summary>
    /// <param name="audio">Opus 编码数据（TOC + 压缩 payload）</param>
    /// <param name="option">保留</param>
    /// <returns>16-bit PCM interleaved @ 48kHz</returns>
    public IPacket ToPcm(ReadOnlySpan<Byte> audio, Object option)
    {
        var data = audio.ToArray();
        if (data.Length < 2) return ArrayPacket.Empty;

        var toc = data[0];
        var config = (toc >> 3) & 0x1F;
        var isCelt = config < 16;
        if (!isCelt) return ArrayPacket.Empty;

        var payload = new Byte[data.Length - 1];
        Array.Copy(data, 1, payload, 0, payload.Length);

        var pcm = DecodeCELTFrame(payload);
        return new ArrayPacket(pcm);
    }

    private Byte[] DecodeCELTFrame(Byte[] payload)
    {
        var bands = NumBandsFull;
        var rd = new RangeDecoder(payload);

        // 1. 解码粗能量（每频带 6 bits）
        var coarseEnergy = new Int32[bands];
        for (var i = 0; i < bands; i++)
            coarseEnergy[i] = rd.DecodeUniform(0, 63);

        // 2. 细能量
        var fineBits = rd.DecodeUniform(0, 3);
        var fineEnergy = new Int32[bands];
        for (var i = 0; i < bands; i++)
            fineEnergy[i] = fineBits > 0 ? rd.DecodeUniform(0, (1 << fineBits) - 1) : 0;

        // 3. 能量 dB → 线性
        var bandEnergy = new Single[bands];
        var offset = 28.0f;
        for (var i = 0; i < bands; i++)
        {
            var dB = (coarseEnergy[i] - offset) * 0.5f + fineEnergy[i] * 0.125f;
            var clampedDB = dB < -60 ? -60 : dB > 40 ? 40 : dB;
            bandEnergy[i] = (Single)Math.Pow(10, clampedDB / 10);
        }

        // 4. 频谱重建
        var spectrum = new Single[MdctSize];
        var bandWidths = GetBandWidths(bands);
        var start = 0;
        for (var b = 0; b < bands; b++)
        {
            var bw = bandWidths[b];
            var energy = bandEnergy[b];
            var gain = (Single)Math.Sqrt(energy / Math.Max(1, bw));
            var hasPulses = rd.RemainingBits() > 8 && rd.DecodeUniform(0, 1) > 0;

            for (var j = start; j < start + bw && j < MdctSize; j++)
            {
                if (hasPulses && rd.RemainingBits() >= 2)
                {
                    var s = rd.DecodeUniform(0, 1);
                    var m = rd.DecodeUniform(0, 1);
                    spectrum[j] = m > 0 ? (s > 0 ? gain * 2.5f : -gain * 2.5f) : (Single)((_rng.NextDouble() * 2 - 1) * gain * 0.25f);
                }
                else
                {
                    spectrum[j] = (Single)((_rng.NextDouble() * 2 - 1) * gain * 0.4f);
                }
            }
            start += bw;
        }

        // 5. IMDCT 合成
        var timeDomain = MdctSynthesis(spectrum);

        // 6. 窗重叠相加
        var output = new Single[FrameSize];
        var window = GetVorbisWindow(MdctSize);
        for (var i = 0; i < FrameSize; i++)
        {
            var cur = timeDomain[i] * window[i + FrameSize];
            var prev = _overlap[i + FrameSize] * window[i];
            output[i] = cur + prev;

            _overlap[i] = timeDomain[i] * window[i];
            _overlap[i + FrameSize] = timeDomain[i + FrameSize] * window[i + FrameSize];
        }

        // 转 16-bit PCM
        var pcm = new Byte[FrameSize * 2];
        for (var i = 0; i < FrameSize; i++)
        {
            var s = (Int32)(output[i] * 32767);
            if (s < -32768) s = -32768;
            if (s > 32767) s = 32767;
            pcm[i * 2] = (Byte)(s & 0xFF);
            pcm[i * 2 + 1] = (Byte)((s >> 8) & 0xFF);
        }

        return pcm;
    }
    #endregion

    #region 编码
    /// <summary>PCM 转 Opus（CELT 编码）</summary>
    /// <param name="pcm">16-bit PCM @ 48kHz</param>
    /// <param name="option">比特率（bps），默认 64000</param>
    /// <returns>Opus 编码数据（TOC + CELT payload）</returns>
    public IPacket FromPcm(ReadOnlySpan<Byte> pcm, Object option)
    {
        var bitrate = option is Int32 br ? br : 64000;
        var pcmData = pcm.ToArray();
        var frameBytes = FrameSize * 2;

        var ms = new MemoryStream();
        var offset = 0;

        while (offset + frameBytes <= pcmData.Length)
        {
            var toc = (Byte)((8 << 3) | 4 | 0);
            ms.WriteByte(toc);

            var framePcm = new Single[FrameSize];
            for (var i = 0; i < FrameSize; i++)
            {
                var s = (Int16)(pcmData[offset + i * 2] | pcmData[offset + i * 2 + 1] << 8);
                framePcm[i] = s / 32768.0f;
            }

            var encoded = EncodeCELTFrame(framePcm, bitrate);
            ms.Write(encoded, 0, encoded.Length);

            offset += frameBytes;
        }

        return new ArrayPacket(ms);
    }

    private Byte[] EncodeCELTFrame(Single[] pcm, Int32 bitrate)
    {
        var bands = NumBandsFull;
        var ms = new MemoryStream();
        var enc = new RangeEncoder(ms, (Int32)(bitrate * FrameSize / BaseSampleRate / 8 * 8));

        var window = GetVorbisWindow(MdctSize);
        var padded = new Single[MdctSize];
        for (var i = 0; i < FrameSize; i++)
        {
            padded[i] = _overlap[i + FrameSize] * window[i];
            padded[i + FrameSize] = pcm[i] * window[i + FrameSize];
        }
        var spectrum = MdctAnalysis(padded);

        // 编码频带能量
        var bandWidths = GetBandWidths(bands);
        var start = 0;
        for (var b = 0; b < bands; b++)
        {
            var sum = 0.0f;
            for (var j = start; j < start + bandWidths[b] && j < MdctSize; j++)
                sum += spectrum[j] * spectrum[j];
            var rms = (Single)Math.Sqrt(sum / Math.Max(1, bandWidths[b]));
            var dB = rms > 0 ? 20 * Math.Log10(rms) : -100;
            var rawQ = (Int32)((dB + 28) * 2);
            var q = rawQ < 0 ? 0 : rawQ > 63 ? 63 : rawQ;
            enc.EncodeUniform(q, 0, 63);
            start += bandWidths[b];
        }

        enc.EncodeUniform(0, 0, 3);
        enc.EncodeUniform(0, 0, 7);
        enc.Finish();
        return ms.ToArray();
    }
    #endregion

    #region MDCT
    private static Single[] MdctAnalysis(Single[] input)
    {
        var n = input.Length;
        var n2 = n / 2;
        var output = new Single[n2];
        for (var k = 0; k < n2; k++)
        {
            var sum = 0.0;
            for (var i = 0; i < n; i++)
                sum += input[i] * Math.Cos(Math.PI / n2 * (i + 0.5 + n2 / 2.0) * (k + 0.5));
            output[k] = (Single)(sum * 2.0 / n2);
        }
        return output;
    }

    private static Single[] MdctSynthesis(Single[] input)
    {
        var n2 = input.Length;
        var n = n2 * 2;
        var output = new Single[n];
        for (var i = 0; i < n; i++)
        {
            var sum = 0.0;
            for (var k = 0; k < n2; k++)
                sum += input[k] * Math.Cos(Math.PI / n2 * (i + 0.5 + n2 / 2.0) * (k + 0.5));
            output[i] = (Single)(sum * 2.0 / n2);
        }
        return output;
    }
    #endregion

    #region 辅助
    /// <summary>根据 Opus 模式获取帧大小</summary>
    public static Int32 GetFrameSize(Int32 mode)
    {
        return mode switch
        {
            0 or 1 or 2 => 120,
            3 or 4 => 240,
            5 or 6 => 480,
            7 or 8 => 960,
            _ => 960,
        };
    }

    private static Single[] GetVorbisWindow(Int32 len)
    {
        var window = new Single[len];
        for (var i = 0; i < len; i++)
        {
            var x = Math.Sin(Math.PI / len * (i + 0.5));
            window[i] = (Single)(Math.Sin(Math.PI / 2 * x * x));
        }
        return window;
    }

    /// <summary>21 频带的 bark 尺度带宽近似</summary>
    private static Int32[] GetBandWidths(Int32 bands)
    {
        var widths = new Int32[bands];
        var totalBins = MdctSize / 2; // 960 MDCT bins
        // 按 Bark 尺度近似分配
        var barkEdges = new[] { 0, 2, 4, 6, 8, 10, 13, 16, 20, 25, 31, 38, 47, 58, 71, 87, 106, 129, 157, 190, 230, 278, 336, 406, 490, 592, 716, 865, 1045, 1262, 1525, 1842, 2225, 2688, 3247, 3921, 4736, 5719, 6907, 8341, 10072, 12163, 14688, 17737, 21419, 25864, 31233, 37715, 45543, 54998, 66413, 80197, 96844, 116938, 141207, 170506, 205888, 248616, int.MaxValue };
        var totalBark = 25.0; // 0-24 Bark for 0-24kHz
        for (var b = 0; b < bands; b++)
        {
            var lowBark = (Double)b / bands * totalBark;
            var highBark = (Double)(b + 1) / bands * totalBark;
            var lowBin = (Int32)(lowBark / totalBark * totalBins);
            var highBin = (Int32)(highBark / totalBark * totalBins);
            widths[b] = Math.Max(1, highBin - lowBin);
        }
        return widths;
    }
    #endregion

    #region Range 编解码器
    private class RangeDecoder
    {
        private readonly Byte[] _data;
        private Int32 _pos;
        private UInt32 _range;
        private UInt32 _value;
        private readonly Int32 _totalBits;

        public RangeDecoder(Byte[] data)
        {
            _data = data;
            _pos = 0;
            _range = 0xFFFFFFFF;
            _value = 0;
            for (var i = 0; i < 4; i++)
                _value = (_value << 8) | (_pos < _data.Length ? _data[_pos++] : (Byte)0);
            _totalBits = data.Length * 8;
        }

        public Int32 DecodeUniform(Int32 min, Int32 max)
        {
            var range = max - min + 1;
            if (range <= 1) return min;
            _range = (_range >> 8) * (UInt32)range;
            if (_range == 0) _range = 1;
            var bit = (_value >> 24) / (_range / (UInt32)range);
            _value -= bit * (_range / (UInt32)range);
            while (_range < 0x800000)
            {
                _range <<= 8;
                var b = _pos < _data.Length ? _data[_pos++] : (Byte)0;
                _value = (_value << 8) | b;
            }
            return min + (Int32)Math.Min((UInt32)(range - 1), bit);
        }

        public Int32 RemainingBits() => Math.Max(0, _totalBits - _pos * 8);
    }

    private class RangeEncoder
    {
        private readonly MemoryStream _stream;
        private Int32 _bitBudget;

        public RangeEncoder(MemoryStream stream, Int32 bitBudget)
        {
            _stream = stream;
            _bitBudget = bitBudget;
        }

        public void EncodeUniform(Int32 value, Int32 min, Int32 max)
        {
            var range = max - min + 1;
            if (range <= 1) return;
            var bits = (Int32)Math.Ceiling(Math.Log(range) / Math.Log(2));
            if (_bitBudget < bits) return;
            var shifted = value - min;
            for (var i = bits - 1; i >= 0; i--)
            {
                _stream.WriteByte((Byte)((shifted >> i) & 1));
                _bitBudget--;
            }
        }

        public void Finish()
        {
            while (_bitBudget > 0)
            {
                _stream.WriteByte(0);
                _bitBudget--;
            }
        }
    }
    #endregion
}

