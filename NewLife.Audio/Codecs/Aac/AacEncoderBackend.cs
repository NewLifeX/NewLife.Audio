using SharpJaad.AAC.Huffman;
using SharpJaad.AAC.Syntax;

namespace NewLife.Audio.Codecs;

/// <summary>AAC-LC 编码后端（纯托管）</summary>
/// <remarks>
/// 长窗（1024 样本/帧）、单声道/立体声、16-bit PCM 输入，ADTS 或 raw 输出。
/// 量化采用按频带目标幅值 + 全局偏移搜索，保证码流合法与码率可控；
/// 不启用短窗、TNS、PNS、强度立体声与 M/S。
/// </remarks>
internal sealed class AacEncoderBackend
{
    private static readonly Int32[] SampleRates = [
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
        16000, 12000, 11025, 8000, 7350,
    ];

    private static readonly Dictionary<(Int32, Int32), (UInt32 Code, Int32 Length)>[] _signedMaps = new Dictionary<(Int32, Int32), (UInt32, Int32)>[12];
    private static readonly Dictionary<(Int32, Int32), (UInt32 Code, Int32 Length)>[] _unsignedMaps = new Dictionary<(Int32, Int32), (UInt32, Int32)>[12];
    private static readonly Dictionary<Int32, (UInt32 Code, Int32 Length)> _scaleFactorMap = [];

    private readonly Int32 _sampleRate;
    private readonly Int32 _channels;
    private readonly Int32 _bitrate;
    private readonly Boolean _adts;
    private readonly Int32 _sampleRateIndex;
    private readonly Int32 _numSwb;
    private readonly Int32 _targetMaxQuantizedMagnitude;
    private readonly Int32[] _swbOffsets;
    private readonly Double[] _window;
    private readonly Double[][] _previous;
    private readonly Double[][] _current;
    private Int32 _currentCount;

    static AacEncoderBackend()
    {
        BuildScaleFactorMap();
        BuildSpectralMaps();
    }

    /// <summary>创建编码后端</summary>
    /// <param name="options">编码选项</param>
    public AacEncoderBackend(AacEncoderOptions options)
    {
        options.Validate();

        _sampleRate = options.SampleRate;
        _channels = options.Channels;
        _bitrate = options.Bitrate;
        _adts = options.Adts;
        _targetMaxQuantizedMagnitude = options.TargetMaxQuantizedMagnitude;

        _sampleRateIndex = Array.IndexOf(SampleRates, _sampleRate);
        if (_sampleRateIndex < 0 || _sampleRateIndex > 11)
            throw new NotSupportedException($"AAC encoder does not support sample rate {_sampleRate}.");

        _numSwb = ScaleFactorBands.SWB_LONG_WINDOW_COUNT[_sampleRateIndex];
        _swbOffsets = ScaleFactorBands.SWB_OFFSET_LONG_WINDOW[_sampleRateIndex];

        _window = new Double[2048];
        for (var i = 0; i < _window.Length; i++)
            _window[i] = Math.Sin(Math.PI / _window.Length * (i + 0.5));

        _previous = new Double[_channels][];
        _current = new Double[_channels][];
        for (var ch = 0; ch < _channels; ch++)
        {
            _previous[ch] = new Double[1024];
            _current[ch] = new Double[1024];
        }
    }

    /// <summary>判断选项是否与当前实例一致</summary>
    /// <param name="options">编码选项</param>
    /// <returns>是否一致</returns>
    public Boolean Matches(AacEncoderOptions options) =>
        options != null &&
        options.SampleRate == _sampleRate &&
        options.Channels == _channels &&
        options.Bitrate == _bitrate &&
        options.Adts == _adts &&
        options.TargetMaxQuantizedMagnitude == _targetMaxQuantizedMagnitude;

    /// <summary>写入 PCM 并返回已编码的完整帧</summary>
    /// <param name="pcm">16-bit 小端 PCM（交错）</param>
    /// <param name="flush">是否补齐最后一帧</param>
    /// <returns>ADTS 或 raw AAC 数据</returns>
    public Byte[] Encode(ReadOnlySpan<Byte> pcm, Boolean flush)
    {
        var output = new MemoryStream();
        var sampleCount = pcm.Length / 2 / _channels;
        var pos = 0;

        while (pos < sampleCount)
        {
            var take = Math.Min(1024 - _currentCount, sampleCount - pos);
            for (var i = 0; i < take; i++)
            {
                for (var ch = 0; ch < _channels; ch++)
                {
                    var idx = ((pos + i) * _channels + ch) * 2;
                    _current[ch][_currentCount + i] = (Int16)(pcm[idx] | (pcm[idx + 1] << 8));
                }
            }

            _currentCount += take;
            pos += take;

            if (_currentCount == 1024)
            {
                EncodeFrame(output);
                SwapBlocks();
            }
        }

        if (flush && _currentCount > 0)
        {
            for (var ch = 0; ch < _channels; ch++)
                Array.Clear(_current[ch], _currentCount, 1024 - _currentCount);

            EncodeFrame(output);
            _currentCount = 0;

            for (var ch = 0; ch < _channels; ch++)
                Array.Clear(_previous[ch], 0, 1024);
        }

        return output.ToArray();
    }

    private void SwapBlocks()
    {
        for (var ch = 0; ch < _channels; ch++)
        {
            var tmp = _previous[ch];
            _previous[ch] = _current[ch];
            _current[ch] = tmp;
            Array.Clear(_current[ch], 0, 1024);
        }

        _currentCount = 0;
    }

    private void EncodeFrame(MemoryStream output)
    {
        var coefficients = new Double[_channels][];
        var samples = new Double[2048];

        for (var ch = 0; ch < _channels; ch++)
        {
            for (var i = 0; i < 1024; i++)
            {
                samples[i] = _previous[ch][i] * _window[i];
                samples[1024 + i] = _current[ch][i] * _window[1024 + i];
            }

            coefficients[ch] = AacMdct.Forward(samples);

            // AAC 标准解码端 IMDCT 的归一化与本实现的 MDCT 相差 2 倍，编码端补齐
            for (var i = 0; i < coefficients[ch].Length; i++)
                coefficients[ch][i] *= 2.0;
        }

        var targetBits = _bitrate > 0 ? (Int32)((Int64)_bitrate * 1024 / _sampleRate) : 0;
        var payload = targetBits > 0 ? SearchPayload(coefficients, targetBits - 56) : BuildPayload(coefficients, _targetMaxQuantizedMagnitude);

        if (_adts) WriteAdtsHeader(output, _sampleRateIndex, _channels, payload.Length);
        output.Write(payload, 0, payload.Length);
    }

    private Byte[] SearchPayload(Double[][] coefficients, Int32 targetBits)
    {
        if (targetBits <= 0) return BuildPayload(coefficients, _targetMaxQuantizedMagnitude);

        Byte[] best = null;
        var bestBits = Int32.MinValue;
        Byte[] smallest = null;
        var smallestBits = Int32.MaxValue;

        var candidates = new[] { 2, 3, 4, 6, 8, 12, 16, 24, 32 };
        foreach (var targetMaxQ in candidates)
        {
            var payload = BuildPayload(coefficients, targetMaxQ);
            var bits = payload.Length * 8;

            if (bits < smallestBits)
            {
                smallest = payload;
                smallestBits = bits;
            }

            if (bits <= targetBits && bits > bestBits)
            {
                best = payload;
                bestBits = bits;
            }
        }

        if (best != null) return best;

        // 目标码率过低：使用最小码流
        var coarsest = BuildPayload(coefficients, 2);
        return coarsest.Length * 8 < smallestBits ? coarsest : smallest;
    }

    private Int32 ComputeGlobalGain(Double[][] coefficients, Int32 targetMaxQ)
    {
        var maxSf = 0;
        var targetLinear = Math.Pow(targetMaxQ, 4.0 / 3.0);

        for (var ch = 0; ch < _channels; ch++)
        {
            for (var band = 0; band < _numSwb; band++)
            {
                var start = _swbOffsets[band];
                var end = _swbOffsets[band + 1];
                if (start >= 1024 || end > 1024 || end <= start) continue;

                var maxAbs = 0.0;
                for (var i = start; i < end; i++)
                {
                    var abs = Math.Abs(coefficients[ch][i]);
                    if (abs > maxAbs) maxAbs = abs;
                }

                if (maxAbs < 1e-9) continue;

                var sf = (Int32)Math.Round(4.0 * Log2(maxAbs / targetLinear) + 100);
                if (sf > maxSf) maxSf = sf;
            }
        }

        return Clamp(maxSf - 60, 0, 255);
    }

    private Byte[] BuildPayload(Double[][] coefficients, Int32 targetMaxQ)
    {
        var globalGain = ComputeGlobalGain(coefficients, targetMaxQ);
        var bandTypes = new Int32[_channels][];
        var quantized = new Int32[_channels][][];
        var scalefactors = new Int32[_channels][];

        for (var ch = 0; ch < _channels; ch++)
        {
            QuantizeChannel(coefficients[ch], globalGain, targetMaxQ, out var types, out var bands, out var sfs);
            bandTypes[ch] = types;
            quantized[ch] = bands;
            scalefactors[ch] = sfs;
        }

        var writer = new AacBitWriter();
        if (_channels == 1)
        {
            writer.WriteBits(0, 3); // SCE
            writer.WriteBits(0, 4); // element_instance_tag
            WriteChannel(writer, 0, bandTypes[0], quantized[0], scalefactors[0], globalGain);
        }
        else
        {
            writer.WriteBits(1, 3); // CPE
            writer.WriteBits(0, 4); // element_instance_tag
            writer.WriteBits(1, 1); // common_window
            WriteIcsInfo(writer);
            writer.WriteBits(0, 2); // ms_mask_present

            for (var ch = 0; ch < _channels; ch++)
                WriteChannel(writer, ch, bandTypes[ch], quantized[ch], scalefactors[ch], globalGain);
        }

        writer.WriteBits(7, 3); // END
        writer.ByteAlign();

        return writer.ToArray();
    }

    private void WriteIcsInfo(AacBitWriter writer)
    {
        writer.WriteBits(0, 1); // ics_reserved_bit
        writer.WriteBits(0, 2); // ONLY_LONG_SEQUENCE
        writer.WriteBits(0, 1); // sine window
        writer.WriteBits((UInt32)_numSwb, 6);
        writer.WriteBits(0, 1); // predictor_data_present
    }

    private void WriteChannel(AacBitWriter writer, Int32 channel, Int32[] bandTypes, Int32[][] quantized, Int32[] scalefactors, Int32 globalGain)
    {
        writer.WriteBits((UInt32)globalGain, 8);

        if (_channels == 1) WriteIcsInfo(writer);

        WriteSectionData(writer, bandTypes);
        WriteScaleFactors(writer, bandTypes, scalefactors, globalGain);

        writer.WriteBits(0, 1); // pulse_data_present
        writer.WriteBits(0, 1); // tns_data_present
        writer.WriteBits(0, 1); // gain_control_data_present

        WriteSpectrum(writer, bandTypes, quantized);
    }

    private void QuantizeChannel(Double[] coefficients, Int32 globalGain, Int32 targetMaxQ, out Int32[] bandTypes, out Int32[][] quantized, out Int32[] scalefactors)
    {
        bandTypes = new Int32[_numSwb];
        quantized = new Int32[_numSwb][];
        scalefactors = new Int32[_numSwb];

        var previousSf = globalGain;
        var targetLinear = Math.Pow(targetMaxQ, 4.0 / 3.0);

        // 低于全局峰值 60 dB 的频带视为噪声/泄漏，直接置零，避免把浮点泄漏量化成满幅噪声
        var globalMax = 0.0;
        for (var i = 0; i < coefficients.Length; i++)
        {
            var abs = Math.Abs(coefficients[i]);
            if (abs > globalMax) globalMax = abs;
        }
        var threshold = globalMax * 1e-3;

        for (var band = 0; band < _numSwb; band++)
        {
            var start = _swbOffsets[band];
            var end = _swbOffsets[band + 1];
            if (start >= 1024 || end > 1024 || end <= start) continue;

            var maxAbs = 0.0;
            for (var i = start; i < end; i++)
            {
                var abs = Math.Abs(coefficients[i]);
                if (abs > maxAbs) maxAbs = abs;
            }

            if (maxAbs < 1e-9 || maxAbs < threshold)
            {
                bandTypes[band] = 0;
                scalefactors[band] = previousSf;
                continue;
            }

            var desiredSf = (Int32)Math.Round(4.0 * Log2(maxAbs / targetLinear) + 100);
            var sf = Clamp(desiredSf, previousSf - 60, previousSf + 60);
            sf = Clamp(sf, 0, 255);

            var scale = Math.Pow(2.0, (sf - 100) / 4.0);
            var length = end - start;
            var q = new Int32[(length + 1) & ~1];
            var maxQ = 0;

            for (var i = 0; i < length; i++)
            {
                var value = coefficients[start + i];
                var magnitude = Math.Abs(value);
                if (magnitude < 1e-12) continue;

                var qv = (Int32)Math.Floor(Math.Pow(magnitude / scale, 0.75) + 0.4054);
                if (qv > 1 << 20) qv = 1 << 20;
                if (value < 0) qv = -qv;
                q[i] = qv;
                if (Math.Abs(qv) > maxQ) maxQ = Math.Abs(qv);
            }

            if (maxQ == 0)
            {
                bandTypes[band] = 0;
                scalefactors[band] = previousSf;
                continue;
            }

            bandTypes[band] = SelectCodebook(maxQ);
            quantized[band] = q;
            scalefactors[band] = sf;
            previousSf = sf;
        }
    }

    private static Int32 SelectCodebook(Int32 maxQ) => maxQ switch
    {
        <= 4 => 6,
        <= 7 => 8,
        <= 12 => 10,
        _ => 11,
    };

    private void WriteSectionData(AacBitWriter writer, Int32[] bandTypes)
    {
        var band = 0;
        while (band < bandTypes.Length)
        {
            var cb = bandTypes[band];
            var length = 1;
            while (band + length < bandTypes.Length && bandTypes[band + length] == cb) length++;

            writer.WriteBits((UInt32)cb, 4);

            var remaining = length;
            while (remaining >= 31)
            {
                writer.WriteBits(31, 5);
                remaining -= 31;
            }
            writer.WriteBits((UInt32)remaining, 5);

            band += length;
        }
    }

    private static void WriteScaleFactors(AacBitWriter writer, Int32[] bandTypes, Int32[] scalefactors, Int32 globalGain)
    {
        var previousSf = globalGain;
        for (var band = 0; band < bandTypes.Length; band++)
        {
            if (bandTypes[band] == 0) continue;

            var delta = Clamp(scalefactors[band] - previousSf, -60, 60);
            var code = delta + 60;
            if (_scaleFactorMap.TryGetValue(code, out var huffman))
                writer.WriteBits(huffman.Code, huffman.Length);

            previousSf += delta;
        }
    }

    private static void WriteSpectrum(AacBitWriter writer, Int32[] bandTypes, Int32[][] quantized)
    {
        for (var band = 0; band < bandTypes.Length; band++)
        {
            var cb = bandTypes[band];
            if (cb == 0) continue;

            var q = quantized[band];
            for (var i = 0; i < q.Length; i += 2)
            {
                var v0 = q[i];
                var v1 = q[i + 1];

                if (cb == 6)
                {
                    if (!_signedMaps[6].TryGetValue((v0, v1), out var entry))
                        throw new InvalidOperationException("AAC encoder: signed codebook lookup failed.");

                    writer.WriteBits(entry.Code, entry.Length);
                    continue;
                }

                var a0 = Math.Abs(v0);
                var a1 = Math.Abs(v1);
                var k0 = Math.Min(a0, 16);
                var k1 = Math.Min(a1, 16);

                if (!_unsignedMaps[cb].TryGetValue((k0, k1), out var huffman))
                    throw new InvalidOperationException("AAC encoder: unsigned codebook lookup failed.");

                writer.WriteBits(huffman.Code, huffman.Length);

                if (a0 != 0) writer.WriteBits(v0 < 0 ? 1u : 0u, 1);
                if (a1 != 0) writer.WriteBits(v1 < 0 ? 1u : 0u, 1);

                if (cb == 11)
                {
                    if (a0 >= 16) WriteEscape(writer, a0);
                    if (a1 >= 16) WriteEscape(writer, a1);
                }
            }
        }
    }

    private static void WriteEscape(AacBitWriter writer, Int32 magnitude)
    {
        var bitCount = 4;
        while ((1 << (bitCount + 1)) <= magnitude) bitCount++;

        var prefixLength = bitCount - 4;
        var suffix = magnitude - (1 << bitCount);

        writer.WriteBits((1u << prefixLength) - 1, prefixLength);
        writer.WriteBits(0, 1);
        writer.WriteBits((UInt32)suffix, bitCount);
    }

    private static void WriteAdtsHeader(MemoryStream output, Int32 sampleRateIndex, Int32 channels, Int32 payloadLength)
    {
        var frameLength = payloadLength + 7;
        output.WriteByte(0xFF);
        output.WriteByte(0xF1); // MPEG-4, layer 0, no CRC
        output.WriteByte((Byte)((1 << 6) | (sampleRateIndex << 2) | ((channels >> 2) & 0x01)));
        output.WriteByte((Byte)(((channels & 0x03) << 6) | ((frameLength >> 11) & 0x03)));
        output.WriteByte((Byte)((frameLength >> 3) & 0xFF));
        output.WriteByte((Byte)(((frameLength & 0x07) << 5) | 0x1F));
        output.WriteByte(0xFC);
    }

    private static void BuildScaleFactorMap()
    {
        foreach (var entry in Codebooks.HCB_SF)
        {
            var length = entry[0];
            var code = (UInt32)entry[1];
            var value = entry[2];
            _scaleFactorMap[value] = (code, length);
        }
    }

    private static void BuildSpectralMaps()
    {
        for (var cb = 1; cb <= 11; cb++)
        {
            var signed = cb is 1 or 2 or 5 or 6;
            var signedMap = new Dictionary<(Int32, Int32), (UInt32, Int32)>();
            var unsignedMap = new Dictionary<(Int32, Int32), (UInt32, Int32)>();

            foreach (var entry in Codebooks.CODEBOOKS[cb - 1])
            {
                if (entry.Length < 4) continue;

                var length = entry[0];
                var code = (UInt32)entry[1];
                var v0 = entry[2];
                var v1 = entry[3];

                if (signed) signedMap[(v0, v1)] = (code, length);
                else unsignedMap[(Math.Abs(v0), Math.Abs(v1))] = (code, length);
            }

            if (signed) _signedMaps[cb] = signedMap;
            else _unsignedMaps[cb] = unsignedMap;
        }
    }

    private static Int32 Clamp(Int32 value, Int32 min, Int32 max) => value < min ? min : value > max ? max : value;

    private static Double Log2(Double value) => Math.Log(value) / Math.Log(2.0);
}
