using System;

namespace NewLife.Audio.Codecs;

/// <summary>MP3（MPEG Audio Layer III）编码器</summary>
/// <remarks>
/// 纯托管实现，覆盖 MPEG-1（32/44.1/48 kHz）、MPEG-2 LSF（16/22.05/24 kHz）与 MPEG-2.5（8/11.025/12 kHz），
/// 支持单声道与立体声、CBR 码率，长块编码（无窗口切换）。
/// <para>信号链：多相子带分析 → 36 点 MDCT → 频率反转与反混叠折叠 → 标量量化（缩放因子带）
/// → Huffman 编码（含表 16~23 的 ESC 扩展）→ 帧组装。量化采用带内等信噪比分配，并以比特预算二分搜索全局尺度；
/// 默认每帧自包含（main_data_begin=0），批量编码时调用方可设置 <see cref="ReservoirBytes"/> 借用前一帧空闲区。</para>
/// </remarks>
internal sealed class Mp3Layer3Encoder
{
    #region 常量
    private static readonly Int32[] BitratesMpeg1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    private static readonly Int32[] BitratesLsf = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];
    private static readonly Int32[] RatesMpeg1 = [44100, 48000, 32000];
    private static readonly Int32[] RatesMpeg2 = [22050, 24000, 16000];
    private static readonly Int32[] RatesMpeg25 = [11025, 12000, 8000];

    /// <summary>MPEG-1 scalefac_compress 与 (slen1, slen2) 的映射表</summary>
    private static readonly Byte[] ScfcEncode = [0, 1, 2, 3, 12, 5, 6, 7, 9, 10, 11, 13, 14, 15, 18, 19];

    /// <summary>量化幅值上限（表 16~23 的 ESC 扩展最高可表示 8206）</summary>
    private const Int32 MaxQuantizedValue = Mp3HuffmanEncoder.MaxEscapeValue;

    /// <summary>量化上限对应的最大幅度比，用于反推最小量化步长</summary>
    private static readonly Single MaxValueRatio = (Single)Math.Pow(MaxQuantizedValue, 4.0 / 3.0);

    /// <summary>掩蔽阈值的保守权重（0.5 表示只允许把步长放宽到掩蔽电平的一半）</summary>
    private const Single MaskingWeight = 0.5f;

    /// <summary>掩蔽允许的最大步长放宽倍数（2 倍，约 +6 dB）</summary>
    private const Single MaskingBoost = 2f;

    /// <summary>分析链路到标准谱线幅度的校准系数</summary>
    /// <remarks>决定解码端重建 PCM 的电平，取自有解参考信号的反向标定</remarks>
    private const Double OutputScale = 1.0 / 566.3;

    /// <summary>3/4 次幂域的校准系数</summary>
    private static readonly Single Pow75Scale = (Single)Math.Pow(OutputScale, 0.75);

    /// <summary>区域划分候选（region0_count, region1_count）</summary>
    private static readonly Int32[][] RegionSplits =
    [
        [15, 7], [12, 7], [9, 7], [6, 7], [3, 7],
        [15, 4], [12, 4], [9, 4], [6, 4], [3, 4],
        [15, 1], [12, 1], [9, 1], [6, 1], [3, 1],
    ];
    #endregion

    #region 字段
    private readonly Int32 _sampleRate;
    private readonly Int32 _channels;
    private Int32 _bitrate;
    private readonly Boolean _mpeg1;
    private readonly Int32 _versionBits;
    private readonly Int32 _sampleRateIndex;
    private Int32 _bitrateIndex;
    private readonly Int32 _granules;
    private readonly Int32 _slots;
    private readonly Int32 _frameSamples;
    private readonly Int32 _sideInfoBits;
    private readonly Byte[] _bands;

    private readonly Single[][] _pcmHistory;
    private readonly Single[][] _subband;
    private readonly Single[][] _subbandHistory;
    private readonly Single[][] _subbandMs;
    private readonly Single[][] _subbandHistoryMs;
    private readonly Single[][] _spectrum;
    private readonly Single[] _scale;
    private readonly Int32[,] _estimate;
    private Int32 _modeExtension;
    private Int32 _demandBits;
    private Boolean _useMs;

    private readonly Single[] _block = new Single[Mp3EncoderTables.BlockLength];
    private readonly Single[] _y = new Single[64];
    private readonly Single[] _pow75 = new Single[576];
    private readonly Int32[] _quant = new Int32[576];
    private readonly Single[] _bandMax;
    private readonly Single[] _bandEnergy;
    private readonly Single[] _bandRms;
    private readonly Single[] _bandMask;
    private readonly Single[] _bandFreq;
    private readonly Single _lowpass;
    private readonly Single[] _bandStep;
    private readonly Single[] _bandTilt;
    private readonly Int32[] _scales;
    private readonly Int32[] _bandRegion;
    private readonly Int32[,] _bandCost;

    private readonly Mp3BitWriter _mainWriter = new(4096);
    private readonly Mp3BitWriter _sideWriter = new(64);
    private readonly Mp3BitWriter _frameWriter = new(2048);
    private readonly GranuleInfo[][] _info;

    private Int32 _padAccumulator;
    #endregion

    #region 属性
    /// <summary>采样率（Hz）</summary>
    public Int32 SampleRate => _sampleRate;

    /// <summary>声道数</summary>
    public Int32 Channels => _channels;

    /// <summary>比特率（bps）</summary>
    /// <remarks>可在帧间赋值以支持 VBR/ABR，取值会吸附到当前 MPEG 版本的标准档位</remarks>
    public Int32 Bitrate
    {
        get => _bitrate;
        set
        {
            var bitrates = _mpeg1 ? BitratesMpeg1 : BitratesLsf;
            _bitrateIndex = FindBitrateIndex(bitrates, value / 1000);
            _bitrate = bitrates[_bitrateIndex] * 1000;
        }
    }

    /// <summary>是否写入 CRC-16 保护</summary>
    public Boolean UseCrc { get; set; }

    /// <summary>可从前一帧比特池借用的字节数</summary>
    /// <remarks>置 0 表示每帧自包含；批量编码时由调用方按上一帧未用容量设置，最大 511 字节。</remarks>
    public Int32 ReservoirBytes { get; set; }

    /// <summary>最近一次 <see cref="EncodePrepared"/> 的主数据比特数</summary>
    public Int32 LastMainDataBits { get; private set; }

    /// <summary>比特池允许的最大字节数</summary>
    /// <remarks>MPEG-1 的 main_data_begin 为 9 位，MPEG-2/2.5 为 8 位。</remarks>
    public Int32 MaxReservoirBytes => _mpeg1 ? 511 : 255;

    /// <summary>当前帧的主数据比特需求，来自最近一次 <see cref="PrepareFrame"/></summary>
    public Int32 DemandBits => _demandBits;

    /// <summary>编码器算法延迟（样本数），解码结果开头需要跳过的样本数</summary>
    /// <remarks>多相滤波器组延迟 481 样本 + MDCT 重叠 576 样本，实测与码率无关。</remarks>
    public Int32 EncoderDelay => 481 + 576;

    /// <summary>最近一次 <see cref="EncodePrepared"/> 的主数据是否超出帧容量（已被截断）</summary>
    public Boolean LastFrameTruncated { get; private set; }

    /// <summary>取下一个更高的标准码率档位</summary>
    /// <param name="bitrate">当前码率（bps）</param>
    /// <returns>更高一档的码率（bps）；已是最高档时返回原值</returns>
    public Int32 NextBitrate(Int32 bitrate)
    {
        var bitrates = _mpeg1 ? BitratesMpeg1 : BitratesLsf;
        var index = FindBitrateIndex(bitrates, bitrate / 1000);

        return index >= 14 ? bitrates[14] * 1000 : bitrates[index + 1] * 1000;
    }

    /// <summary>按比特需求挑选标准码率档位</summary>
    /// <param name="desiredBits">本帧期望的主数据比特数</param>
    /// <param name="lowest">可选最低码率（bps），小于等于 0 表示取最低档</param>
    /// <param name="highest">可选最高码率（bps），小于等于 0 表示取最高档</param>
    /// <param name="reservoirBytes">可从前一帧比特池借用的字节数</param>
    /// <returns>吸附到标准档位后的码率（bps）</returns>
    public Int32 SelectBitrate(Int32 desiredBits, Int32 lowest, Int32 highest, Int32 reservoirBytes = 0)
    {
        var bitrates = _mpeg1 ? BitratesMpeg1 : BitratesLsf;
        var lo = lowest > 0 ? FindBitrateIndex(bitrates, lowest / 1000) : 1;
        var hi = highest > 0 ? FindBitrateIndex(bitrates, highest / 1000) : 14;
        if (hi < lo) hi = lo;

        for (var i = lo; i <= hi; i++)
        {
            if (GetFrameBudget(i, reservoirBytes) >= desiredBits) return bitrates[i] * 1000;
        }

        return bitrates[hi] * 1000;
    }

    /// <summary>指定码率档位下扣除帧头、CRC 与边信息后的主数据比特预算</summary>
    /// <param name="bitrateIndex">码率档位序号</param>
    /// <param name="reservoirBytes">可额外借用的比特池字节数</param>
    /// <returns>主数据比特数</returns>
    private Int32 GetFrameBudget(Int32 bitrateIndex, Int32 reservoirBytes = 0)
    {
        var bitrates = _mpeg1 ? BitratesMpeg1 : BitratesLsf;
        var frameBytes = (_mpeg1 ? 144 : 72) * bitrates[bitrateIndex] * 1000 / _sampleRate;

        return frameBytes * 8 - 32 - _sideInfoBits - (UseCrc ? 16 : 0) + reservoirBytes * 8;
    }

    /// <summary>每帧样本数（每声道）</summary>
    public Int32 FrameSamples => _frameSamples;

    /// <summary>单帧最大字节数</summary>
    public Int32 MaxFrameBytes => 144 * 320000 / 32000 + 2;
    #endregion

    #region 构造
    /// <summary>初始化编码器</summary>
    /// <param name="sampleRate">采样率，支持 8/11.025/12/16/22.05/24/32/44.1/48 kHz</param>
    /// <param name="channels">声道数，1 或 2</param>
    /// <param name="bitrate">目标比特率（kbps）</param>
    public Mp3Layer3Encoder(Int32 sampleRate, Int32 channels, Int32 bitrate)
    {
        if (channels != 1 && channels != 2) throw new ArgumentOutOfRangeException(nameof(channels), "MP3 supports only mono or stereo");

        var (version, rateTable) = sampleRate switch
        {
            44100 or 48000 or 32000 => (3, RatesMpeg1),
            22050 or 24000 or 16000 => (2, RatesMpeg2),
            11025 or 12000 or 8000 => (0, RatesMpeg25),
            _ => throw new ArgumentOutOfRangeException(nameof(sampleRate), "Unsupported MP3 sample rate: " + sampleRate),
        };

        _sampleRate = sampleRate;
        _channels = channels;
        _mpeg1 = version == 3;
        _versionBits = version;
        _sampleRateIndex = Array.IndexOf(rateTable, sampleRate);

        var bitrates = _mpeg1 ? BitratesMpeg1 : BitratesLsf;
        _bitrateIndex = FindBitrateIndex(bitrates, bitrate);
        _bitrate = bitrates[_bitrateIndex] * 1000;

        _granules = _mpeg1 ? 2 : 1;
        _slots = _granules * 18;
        _frameSamples = _granules * 576;
        _sideInfoBits = _mpeg1
            ? (channels == 1 ? 17 : 32) * 8
            : (channels == 1 ? 9 : 17) * 8;

        var bands = Mp3Tables.GetLongBands(sampleRate);
        if (bands == null) throw new ArgumentOutOfRangeException(nameof(sampleRate), "Unsupported sample rate: " + sampleRate);
        _bands = bands;

        _pcmHistory = new Single[channels][];
        _subband = new Single[channels][];
        _subbandHistory = new Single[channels][];
        _subbandMs = new Single[channels][];
        _subbandHistoryMs = new Single[channels][];
        _spectrum = new Single[channels][];
        _scale = new Single[channels];
        _estimate = new Int32[_granules, channels];
        for (var ch = 0; ch < channels; ch++)
        {
            _pcmHistory[ch] = new Single[Mp3EncoderTables.FilterLength];
            _subband[ch] = new Single[_slots * 32];
            _subbandHistory[ch] = new Single[18 * 32];
            _subbandMs[ch] = new Single[_slots * 32];
            _subbandHistoryMs[ch] = new Single[18 * 32];
            _spectrum[ch] = new Single[576];
            _scale[ch] = 1f / 64;
        }

        _bandMax = new Single[bands.Length];
        _bandEnergy = new Single[bands.Length];
        _bandRms = new Single[bands.Length];
        _bandMask = new Single[bands.Length];
        _bandFreq = BuildBandFreq(bands);
        _lowpass = sampleRate * 0.5f;
        _bandStep = new Single[bands.Length];
        _bandTilt = BuildBandTilt(bands);
        _scales = new Int32[bands.Length];
        _bandRegion = new Int32[bands.Length];
        _bandCost = new Int32[bands.Length, Mp3HuffmanEncoder.LongTables.Length];

        _info = new GranuleInfo[_granules][];
        for (var gr = 0; gr < _granules; gr++)
        {
            _info[gr] = new GranuleInfo[channels];
            for (var ch = 0; ch < channels; ch++)
            {
                _info[gr][ch] = new GranuleInfo();
            }
        }
    }
    #endregion

    #region 编码入口
    /// <summary>编码一帧 PCM 数据</summary>
    /// <param name="pcm">16 位小端交错 PCM，长度至少为 <see cref="FrameSamples"/> × 声道数 × 2</param>
    /// <param name="offset">PCM 起始偏移</param>
    /// <param name="output">输出缓冲区，长度至少为 <see cref="MaxFrameBytes"/></param>
    /// <returns>写入的帧字节数</returns>
    public Int32 EncodeFrame(Byte[] pcm, Int32 offset, Byte[] output)
    {
        PrepareFrame(pcm, offset);

        return EncodePrepared(output);
    }

    /// <summary>分析一帧 PCM 并估算比特需求，供 VBR/ABR 在使用前决定本帧码率</summary>
    /// <param name="pcm">16 位小端交错 PCM，长度至少为 <see cref="FrameSamples"/> × 声道数 × 2</param>
    /// <param name="offset">PCM 起始偏移</param>
    /// <param name="needDemand">是否需要估算比特需求（单声道下估算会额外产生一次变换开销）</param>
    /// <param name="referenceScale">估算用的固定量化尺度，大于 0 时替代自适应尺度，使比特需求与目标质量对应</param>
    /// <returns>本帧在 L/R 与 M/S 中较优模式下的主数据比特需求；<paramref name="needDemand"/> 为 false 时返回 0</returns>
    public Int32 PrepareFrame(Byte[] pcm, Int32 offset, Boolean needDemand = false, Single referenceScale = 0)
    {
        // 子带分析
        for (var ch = 0; ch < _channels; ch++)
        {
            Analyze(pcm, offset, ch);
        }

        // 立体声联合编码：按帧选择 L/R 或 M/S（mode_extension = 2）
        var useMs = false;
        if (_channels == 2)
        {
            for (var ch = 0; ch < 2; ch++)
            {
                Array.Copy(_subband[ch], _subbandMs[ch], _subband[ch].Length);
            }
            TransformToMs(_subbandMs);

            var estimatedMs = EstimateBits(true, referenceScale);
            var estimatedLr = EstimateBits(false, referenceScale);
            useMs = estimatedMs < estimatedLr;

            // 若选中 M/S，重新估算以取得该模式下各声道的比特需求（用于预算分配）
            if (useMs) EstimateBits(true, referenceScale);
            _demandBits = useMs ? estimatedMs : estimatedLr;
        }
        else
        {
            _demandBits = needDemand ? EstimateBits(false, referenceScale) : 0;
        }
        _useMs = useMs;
        _modeExtension = useMs ? 2 : 0;

        return _demandBits;
    }

    /// <summary>复制最近一次编码的主数据字节</summary>
    /// <param name="target">目标数组</param>
    /// <param name="offset">目标起始偏移</param>
    /// <param name="byteCount">最多复制的字节数</param>
    /// <returns>主数据实际占用的字节数（向上取整）</returns>
    public Int32 CopyLastMainData(Byte[] target, Int32 offset, Int32 byteCount)
    {
        _mainWriter.CopyTo(target, offset, byteCount);

        return _mainWriter.ByteLength;
    }

    /// <summary>按当前比特率把已分析的帧编码输出</summary>
    /// <param name="output">输出缓冲区，长度至少为 <see cref="MaxFrameBytes"/></param>
    /// <returns>写入的帧字节数</returns>
    public Int32 EncodePrepared(Byte[] output)
    {
        var pad = NextPadding();
        var frameBytes = (_mpeg1 ? 144 : 72) * _bitrate / _sampleRate + pad;
        var useMs = _useMs;

        // 逐颗粒/声道编码主数据
        _mainWriter.Reset();
        var reservoir = Math.Max(0, Math.Min(MaxReservoirBytes, ReservoirBytes));
        var budget = frameBytes * 8 - 32 - _sideInfoBits - (UseCrc ? 16 : 0) + reservoir * 8;

        // 按估算需求比例为各声道分配预算，避免零需求的 side 声道浪费一半码率
        var channelBudget = new Int32[_channels];
        if (_channels == 1)
        {
            channelBudget[0] = budget;
        }
        else
        {
            var weightSum = 0;
            var weights = new Int32[_channels];
            for (var ch = 0; ch < _channels; ch++)
            {
                var weight = 0;
                for (var gr = 0; gr < _granules; gr++)
                {
                    weight += _estimate[gr, ch];
                }
                if (weight < 1) weight = 1;
                weights[ch] = weight;
                weightSum += weight;
            }
            for (var ch = 0; ch < _channels; ch++)
            {
                channelBudget[ch] = (Int32)((Int64)budget * weights[ch] / weightSum);
            }
        }

        for (var gr = 0; gr < _granules; gr++)
        {
            for (var ch = 0; ch < _channels; ch++)
            {
                var share = channelBudget[ch] / (_granules - gr);
                // part2_3_length 只有 12 位
                if (share > 4095) share = 4095;
                if (share < 0) share = 0;

                Mdct(ch, gr, useMs);
                var bits = EncodeGranuleChannel(ch, share, _info[gr][ch]);
                channelBudget[ch] -= bits;
            }
        }

        // 组装帧
        _frameWriter.Reset();
        _sideWriter.Reset();
        WriteFrameHeader(_frameWriter, pad);
        WriteSideInfo(_sideWriter);
        if (UseCrc)
        {
            var crc = ComputeCrc(_frameWriter, 16, 16, _sideWriter);
            _frameWriter.Write(crc, 16);
        }
        _frameWriter.AppendFrom(_sideWriter, 0, _sideWriter.Position);
        _frameWriter.AppendFrom(_mainWriter, 0, _mainWriter.Position);
        _frameWriter.CopyTo(output, 0, frameBytes);

        LastMainDataBits = Math.Min(_mainWriter.Position, budget);
        // 主数据超出本帧容量（含可借用比特池）时会被截断，调用方可换更高码率重编
        LastFrameTruncated = _mainWriter.Position > budget;

        // 更新子带历史（L/R 与 M/S 各自维护，供下一帧选择任一模式时使用）
        for (var ch = 0; ch < _channels; ch++)
        {
            var start = (_slots - 18) * 32;
            Array.Copy(_subband[ch], start, _subbandHistory[ch], 0, 18 * 32);
            Array.Copy(_subbandMs[ch], start, _subbandHistoryMs[ch], 0, 18 * 32);
        }

        return frameBytes;
    }

    /// <summary>把 L/R 子带样本就地转换为 M/S（mid=(L+R)/2，side=(L−R)/√2）</summary>
    private static void TransformToMs(Single[][] sub)
    {
        var left = sub[0];
        var right = sub[1];
        const Single invSqrt2 = 0.70710678f;

        for (var i = 0; i < left.Length; i++)
        {
            var l = left[i];
            var r = right[i];
            left[i] = (l + r) * 0.5f;
            right[i] = (l - r) * invSqrt2;
        }
    }

    /// <summary>用当前量化状态快速估算整帧比特数，用于 L/R 与 M/S 模式选择</summary>
    /// <param name="useMs">是否按 M/S 子带估算</param>
    /// <param name="referenceScale">参考量化尺度（相对满量程的比例），大于 0 时按帧内峰值归一化后使用</param>
    /// <returns>整帧主数据比特数估计</returns>
    private Int32 EstimateBits(Boolean useMs, Single referenceScale)
    {
        var total = 0;
        for (var gr = 0; gr < _granules; gr++)
        {
            for (var ch = 0; ch < _channels; ch++)
            {
                Mdct(ch, gr, useMs);
                PrepareSpectrum(_spectrum[ch]);

                Single scale;
                if (referenceScale > 0)
                {
                    // 按峰值归一化，使比特需求与信号绝对电平无关（只看相对复杂度和目标质量）
                    var peak = 0f;
                    for (var b = 0; b < _bandMax.Length; b++)
                    {
                        if (_bandMax[b] > peak) peak = _bandMax[b];
                    }
                    scale = peak > 1e-6f ? referenceScale / peak : 1e9f;
                }
                else
                {
                    scale = _scale[ch];
                }

                var bits = Measure(scale, _spectrum[ch], _info[gr][ch]);
                _estimate[gr, ch] = bits;
                total += bits;
            }
        }

        return total;
    }
    #endregion

    #region 子带分析
    /// <summary>多相子带分析，把当前帧的 1152 个采样变换为 <see cref="_slots"/> 个时隙的子带样本</summary>
    /// <param name="pcm">交错 PCM</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="channel">声道序号</param>
    private void Analyze(Byte[] pcm, Int32 offset, Int32 channel)
    {
        var hist = _pcmHistory[channel];
        var sub = _subband[channel];
        var window = Mp3EncoderTables.AnalysisWindow;
        var matrix = Mp3EncoderTables.Matrix;

        for (var slot = 0; slot < _slots; slot++)
        {
            Array.Copy(hist, 32, hist, 0, Mp3EncoderTables.FilterLength - 32);
            var baseSample = offset + slot * 32 * _channels * 2 + channel * 2;
            for (var i = 0; i < 32; i++)
            {
                var p = baseSample + i * _channels * 2;
                var v = (Int16)(pcm[p] | pcm[p + 1] << 8);
                hist[Mp3EncoderTables.FilterLength - 32 + i] = v / 32768f;
            }

            for (var i = 0; i < 64; i++)
            {
                var sum = 0f;
                for (var j = 0; j < 8; j++)
                {
                    // ISO 11172-3 分析窗以 X[0] 为最新采样；本项目历史缓冲最新采样在末尾，故倒序取
                    var k = i + 64 * j;
                    sum += window[k] * hist[Mp3EncoderTables.FilterLength - 1 - k];
                }
                _y[i] = sum;
            }

            var dst = slot * 32;
            for (var k = 0; k < 32; k++)
            {
                var sum = 0f;
                var row = k * 64;
                for (var i = 0; i < 64; i++)
                {
                    sum += matrix[row + i] * _y[i];
                }
                sub[dst + k] = sum;
            }

        }
    }
    #endregion

    #region 频谱构造
    /// <summary>36 点 MDCT 与反混叠折叠，得到颗粒/声道的 576 条谱线</summary>
    /// <param name="channel">声道序号</param>
    /// <param name="granule">颗粒序号</param>
    /// <param name="useMs">是否使用 M/S 联合立体声子带</param>
    private void Mdct(Int32 channel, Int32 granule, Boolean useMs)
    {
        var sub = useMs ? _subbandMs[channel] : _subband[channel];
        var hist = useMs ? _subbandHistoryMs[channel] : _subbandHistory[channel];
        var spec = _spectrum[channel];
        var window = Mp3EncoderTables.MdctWindow;
        var cos = Mp3EncoderTables.MdctCos;
        var block = _block;

        for (var sb = 0; sb < 32; sb++)
        {
            // 前 18 个样本来自上一个颗粒（granule 0 取上一帧尾部，其余取本帧前一颗粒），后 18 个为本颗粒
            if (granule > 0)
            {
                var prev = (granule - 1) * 18;
                for (var n = 0; n < 18; n++)
                {
                    block[n] = sub[(prev + n) * 32 + sb];
                }
            }
            else
            {
                for (var n = 0; n < 18; n++)
                {
                    block[n] = hist[n * 32 + sb];
                }
            }

            for (var n = 0; n < 18; n++)
            {
                block[18 + n] = sub[(granule * 18 + n) * 32 + sb];
            }

            // 奇数子带频率反转，与解码器综合后的符号翻转互逆
            if ((sb & 1) != 0)
            {
                for (var n = 1; n < 18; n += 2)
                {
                    block[n] = -block[n];
                    block[18 + n] = -block[18 + n];
                }
            }

            for (var n = 0; n < Mp3EncoderTables.BlockLength; n++)
            {
                block[n] *= window[n];
            }

            var dst = sb * 18;
            for (var k = 0; k < 18; k++)
            {
                var sum = 0f;
                var row = k * 36;
                for (var n = 0; n < 36; n++)
                {
                    sum += cos[row + n] * block[n];
                }
                spec[dst + k] = sum;
            }
        }

        // 反混叠折叠（解码器反混叠蝶形的逆变换）
        var cs = Mp3EncoderTables.AliasCs;
        var ca = Mp3EncoderTables.AliasCa;
        for (var sb = 0; sb < 31; sb++)
        {
            var baseOffset = sb * 18;
            for (var i = 0; i < 8; i++)
            {
                var u = spec[baseOffset + 18 + i];
                var d = spec[baseOffset + 17 - i];
                spec[baseOffset + 18 + i] = u * cs[i] + d * ca[i];
                spec[baseOffset + 17 - i] = -u * ca[i] + d * cs[i];
            }
        }
    }
    #endregion

    #region 量化与编码
    /// <summary>在一个颗粒/声道上完成量化、表选择与比特写入</summary>
    /// <param name="channel">声道序号</param>
    /// <param name="budget">可用主数据比特数</param>
    /// <param name="info">输出颗粒信息</param>
    /// <returns>实际写入的比特数</returns>
    private Int32 EncodeGranuleChannel(Int32 channel, Int32 budget, GranuleInfo info)
    {
        var spec = _spectrum[channel];
        var bands = _bands;

        PrepareSpectrum(spec);

        // 以全局尺度为变量二分搜索，找到不超过预算的最小量化步长
        var scale = _scale[channel];
        var lo = scale * 0.25f;
        var hi = scale * 4f;
        if (lo < 1e-9f) lo = 1e-9f;

        var bits = Measure(hi, spec, info);
        for (var i = 0; i < 20 && bits > budget; i++)
        {
            lo = hi;
            hi *= 2;
            bits = Measure(hi, spec, info);
        }

        bits = Measure(lo, spec, info);
        for (var i = 0; i < 20 && bits <= budget && lo > 1e-9f; i++)
        {
            hi = lo;
            lo *= 0.5f;
            if (lo < 1e-9f) lo = 1e-9f;
            bits = Measure(lo, spec, info);
        }

        for (var i = 0; i < 10 && hi - lo > hi * 0.002f; i++)
        {
            var mid = (Single)Math.Sqrt(lo * hi);
            if (Measure(mid, spec, info) > budget)
                lo = mid;
            else
                hi = mid;
        }

        _scale[channel] = hi;
        var total = Measure(hi, spec, info);

        // 兜底：二分可能受浮点精度影响停在上界，确保不超过颗粒比特预算
        for (var i = 0; i < 10 && total > budget; i++)
        {
            hi *= 1.5f;
            _scale[channel] = hi;
            total = Measure(hi, spec, info);
        }

        WriteGranuleData(info);



        return total;
    }

    /// <summary>准备声道的量化输入：0.75 次幂表与逐带峰值/能量</summary>
    /// <param name="spec">当前颗粒/声道的 576 条谱线</param>
    private void PrepareSpectrum(Single[] spec)
    {
        for (var i = 0; i < 576; i++)
        {
            _pow75[i] = (Single)Math.Pow(Math.Abs(spec[i]), 0.75) * Pow75Scale;
        }

        var bands = _bands;
        var offset = 0;
        var peak = 0f;
        for (var b = 0; b < bands.Length; b++)
        {
            var width = bands[b];
            var max = 0f;
            var energy = 0f;
            for (var i = 0; i < width; i++)
            {
                var v = Math.Abs(spec[offset + i]);
                if (v > max) max = v;
                energy += v * v;
            }
            _bandMax[b] = (Single)(max * OutputScale);
            _bandEnergy[b] = energy;
            _bandRms[b] = width > 0 ? (Single)(Math.Sqrt(energy / width) * OutputScale) : 0f;
            if (_bandRms[b] > peak) peak = _bandRms[b];
            offset += width;
        }

        // 绝对听阈：相对帧内峰值低于听阈的频带整带丢弃（谱线置零，量化后即为 0）
        offset = 0;
        for (var b = 0; b < bands.Length; b++)
        {
            var width = bands[b];
            if (_bandFreq[b] >= _lowpass || (peak > 0 && _bandRms[b] > 0 && _bandRms[b] < peak * AbsoluteThreshold(_bandFreq[b])))
            {
                for (var i = 0; i < width; i++) spec[offset + i] = 0f;
                _bandMax[b] = 0f;
                _bandEnergy[b] = 0f;
                _bandRms[b] = 0f;
                for (var i = offset; i < offset + width; i++) _pow75[i] = 0f;
            }

            offset += width;
        }

        UpdateMasking(bands.Length);
    }

    /// <summary>绝对听阈（相对帧内峰值的比例）：高频听阈显著抬升，低于该比例的频带可整带丢弃</summary>
    /// <param name="frequency">频带中心频率（Hz）</param>
    /// <returns>该频率的听阈（相对峰值）</returns>
    private static Single AbsoluteThreshold(Single frequency)
    {
        if (frequency < 12000) return 1e-4f;

        // 12 kHz 以上按每 4 kHz 翻倍抬升
        var rise = Math.Pow(2, (frequency - 12000) / 4000.0);

        return (Single)Math.Min(1e-4 * rise, 1e-2);
    }

    /// <summary>简化的心理声学掩蔽：按频带距离做能量扩散，得到各频带被邻近频带掩蔽的电平</summary>
    /// <param name="count">频带数</param>
    /// <remarks>
    /// 用指数衰减近似 Bark 域扩散函数，低频对高频的掩蔽更强（上坡 0.35 / 下坡 0.9 衰减系数）。
    /// 结果存入 <see cref="_bandMask"/>，供量化步长在「自身 RMS」与「被掩蔽电平」之间取较大者，
    /// 使强带附近弱带的量化噪声藏在掩蔽阈值以下，腾出的比特留给未掩蔽频带。
    /// </remarks>
    private void UpdateMasking(Int32 count)
    {
        for (var b = 0; b < count; b++)
        {
            var sum = 0f;
            for (var n = 0; n < count; n++)
            {
                if (n == b) continue;

                var rms = _bandRms[n];
                if (rms <= 0) continue;

                var d = n - b;
                var slope = d > 0 ? 0.35 : 0.9;
                var w = Math.Exp(-Math.Abs(d) * slope);
                if (w < 0.02) continue;

                sum += rms * rms * (Single)w;
            }

            _bandMask[b] = (Single)Math.Sqrt(sum) * MaskingWeight;
        }
    }

    /// <summary>按指定全局尺度量化并统计比特数</summary>
    /// <param name="scale">全局量化尺度，越大步长越大、比特越少</param>
    /// <param name="spec">当前颗粒/声道的 576 条谱线</param>
    /// <param name="info">输出颗粒信息</param>
    /// <returns>估计的主数据比特数</returns>
    private Int32 Measure(Single scale, Single[] spec, GranuleInfo info)
    {
        var bands = _bands;

        // 1. 逐带确定量化步长：以带内 RMS 为基准（比峰值更贴近听感），
        //    再用心理声学掩蔽电平放宽被强带掩蔽的弱带，并低通掉听阈以上的高频带
        var maxStep = 1e-9f;
        for (var b = 0; b < bands.Length; b++)
        {
            var max = _bandMax[b];

            // 绝对听阈：接近 Nyquist 的频带直接丢弃
            if (_bandFreq[b] >= _lowpass)
            {
                _bandStep[b] = 1e9f;
                continue;
            }

            var rms = _bandRms[b];
            var reference = rms;
            var mask = _bandMask[b];
            if (mask > reference && reference > 0)
            {
                // 掩蔽只允许有限放宽，避免测量信噪比过度下降
                reference = Math.Min(mask, reference * MaskingBoost);
            }

            var step = reference > 0 ? reference * scale * _bandTilt[b] : 1e9f;
            var minStep = max > 0 ? max / MaxValueRatio : 1e-9f;
            if (step < minStep) step = minStep;
            _bandStep[b] = step;
            if (step > maxStep && step < 1e8f) maxStep = step;
        }

        // 2. 由最大步长确定 global_gain，缩放因子按相对步长分配
        var gain = 214 + (Int32)Math.Ceiling(4 * Math.Log(maxStep, 2));
        if (gain < 0) gain = 0;
        if (gain > 255) gain = 255;

        // scalefac_scale=1 时每个缩放因子单位为 1 个倍频程（解码端按 iscf << 2 参与指数）
        const Int32 shift = 4;
        const Int32 maxScf = (1 << 4) - 1;
        for (var b = 0; b < bands.Length; b++)
        {
            var v = (Int32)Math.Round((gain - 214 - 4 * Math.Log(_bandStep[b], 2)) / shift);
            if (v < 0) v = 0;
            if (v > maxScf) v = maxScf;
            _scales[b] = v;
        }

        // 3. 选择缩放因子位宽并据此截断
        var scfBits = 0;
        Int32 compress;
        if (_mpeg1)
        {
            var maxLow = 0;
            var maxHigh = 0;
            for (var b = 0; b < 11; b++)
            {
                if (_scales[b] > maxLow) maxLow = _scales[b];
            }
            for (var b = 11; b < 21; b++)
            {
                if (_scales[b] > maxHigh) maxHigh = _scales[b];
            }

            compress = SelectCompress(BitsNeeded(maxLow), BitsNeeded(maxHigh), out var slen1, out var slen2);
            scfBits = 11 * slen1 + 10 * slen2;
            info.Slen1 = slen1;
            info.Slen2 = slen2;

            var limitLow = (1 << slen1) - 1;
            var limitHigh = (1 << slen2) - 1;
            for (var b = 0; b < 11; b++)
            {
                if (_scales[b] > limitLow) _scales[b] = limitLow;
            }
            for (var b = 11; b < 21; b++)
            {
                if (_scales[b] > limitHigh) _scales[b] = limitHigh;
            }
        }
        else
        {
            var partition = new[] { 0, 6, 11, 16, 21 };
            for (var p = 0; p < 4; p++)
            {
                var max = 0;
                for (var b = partition[p]; b < partition[p + 1]; b++)
                {
                    if (_scales[b] > max) max = _scales[b];
                }
                var limit = p < 2 ? 4 : 3;
                var size = BitsNeeded(max);
                if (size > limit) size = limit;
                info.Sizes[p] = size;
                var valueLimit = (1 << size) - 1;
                for (var b = partition[p]; b < partition[p + 1]; b++)
                {
                    if (_scales[b] > valueLimit) _scales[b] = valueLimit;
                }
            }

            compress = info.Sizes[0] * 80 + info.Sizes[1] * 16 + info.Sizes[2] * 4 + info.Sizes[3];
            scfBits = 6 * info.Sizes[0] + 5 * info.Sizes[1] + 5 * info.Sizes[2] + 5 * info.Sizes[3];
        }

        _scales[bands.Length - 1] = 0;

        // 4. 按最终缩放因子量化
        var lastPair = -1;
        var offset = 0;
        for (var b = 0; b < bands.Length; b++)
        {
            var width = bands[b];
            var exponent = (gain - 214 - _scales[b] * shift) / 4.0;
            var factor = (Single)Math.Pow(2, -0.75 * exponent);
            for (var i = 0; i < width; i++)
            {
                var q = (Int32)(_pow75[offset + i] * factor + 0.5f);
                if (q > MaxQuantizedValue) q = MaxQuantizedValue;
                if (spec[offset + i] < 0) q = -q;
                _quant[offset + i] = q;
                if (q != 0) lastPair = (offset + i) >> 1;
            }
            offset += width;
        }

        var bigValues = lastPair + 1;

        // 5. 逐带统计各候选表的代价，再挑选区域划分
        var tableCount = Mp3HuffmanEncoder.LongTables.Length;
        offset = 0;
        for (var b = 0; b < bands.Length; b++)
        {
            var width = bands[b];
            for (var t = 0; t < tableCount; t++)
            {
                var table = Mp3HuffmanEncoder.LongTables[t];
                var cost = 0;
                for (var p = 0; p < width / 2; p++)
                {
                    var pair = (offset >> 1) + p;
                    if (pair >= bigValues) break;

                    var x = Math.Abs(_quant[pair * 2]);
                    var y = Math.Abs(_quant[pair * 2 + 1]);
                    var len = Mp3HuffmanEncoder.GetBits(table, x, y);
                    if (len < 0)
                    {
                        cost = Int32.MaxValue;
                        break;
                    }
                    cost += len;
                    if (x != 0) cost++;
                    if (y != 0) cost++;
                }
                _bandCost[b, t] = cost;
            }
            offset += width;
        }

        var bestBits = Int32.MaxValue;
        var bestSplit = RegionSplits[0];
        var bestTables = new Int32[3];
        foreach (var split in RegionSplits)
        {
            var r0 = split[0];
            var r1 = split[1];
            var t0 = BestTable(0, r0 + 1, tableCount);
            var t1 = BestTable(r0 + 1, r0 + 1 + r1 + 1, tableCount);
            var t2 = BestTable(r0 + 1 + r1 + 1, bands.Length, tableCount);
            if (t0.Bits == Int32.MaxValue || t1.Bits == Int32.MaxValue || t2.Bits == Int32.MaxValue) continue;

            var sum = t0.Bits + t1.Bits + t2.Bits;
            if (sum < bestBits)
            {
                bestBits = sum;
                bestSplit = split;
                bestTables[0] = t0.Table;
                bestTables[1] = t1.Table;
                bestTables[2] = t2.Table;
            }
        }

        if (bestBits == Int32.MaxValue)
        {
            // 理论上不会出现：所有候选表都无法覆盖，退化为全零
            bestBits = 0;
            bestTables[0] = bestTables[1] = bestTables[2] = 0;
        }

        // 6. 填写颗粒信息
        info.Part23Length = scfBits + bestBits;
        info.BigValues = bigValues;
        info.GlobalGain = gain;
        info.ScalefacCompress = compress;
        info.ScaleBits = scfBits;
        info.Region0Count = bestSplit[0];
        info.Region1Count = bestSplit[1];
        info.TableSelect[0] = bestTables[0];
        info.TableSelect[1] = bestTables[1];
        info.TableSelect[2] = bestTables[2];

        Array.Copy(_scales, info.Scales, _scales.Length);

        return info.Part23Length;
    }

    private (Int32 Bits, Int32 Table) BestTable(Int32 startBand, Int32 endBand, Int32 tableCount)
    {
        var best = Int32.MaxValue;
        var table = 0;
        for (var t = 0; t < tableCount; t++)
        {
            var cost = 0;
            for (var b = startBand; b < endBand && b < _bands.Length; b++)
            {
                var c = _bandCost[b, t];
                if (c == Int32.MaxValue)
                {
                    cost = Int32.MaxValue;
                    break;
                }
                cost += c;
            }
            if (cost < best)
            {
                best = cost;
                table = Mp3HuffmanEncoder.LongTables[t];
            }
        }

        return (best, table);
    }

    /// <summary>写入颗粒主数据（缩放因子 + Huffman 码字）</summary>
    /// <param name="info">颗粒信息</param>
    private void WriteGranuleData(GranuleInfo info)
    {
        var writer = _mainWriter;
        var bands = _bands;

        // 缩放因子
        if (_mpeg1)
        {
            for (var b = 0; b < 11; b++)
            {
                writer.Write(info.Scales[b], info.Slen1);
            }
            for (var b = 11; b < 21; b++)
            {
                writer.Write(info.Scales[b], info.Slen2);
            }
        }
        else
        {
            var partition = new[] { 0, 6, 11, 16, 21 };
            for (var p = 0; p < 4; p++)
            {
                for (var b = partition[p]; b < partition[p + 1]; b++)
                {
                    writer.Write(info.Scales[b], info.Sizes[p]);
                }
            }
        }

        // Huffman 码字
        var bigValues = info.BigValues;
        if (bigValues <= 0) return;

        var region0End = info.Region0Count + 1;
        var region1End = region0End + info.Region1Count + 1;
        var pair = 0;
        for (var b = 0; b < bands.Length && pair < bigValues; b++)
        {
            var table = b < region0End ? info.TableSelect[0] : b < region1End ? info.TableSelect[1] : info.TableSelect[2];
            var pairs = bands[b] / 2;
            for (var p = 0; p < pairs && pair < bigValues; p++, pair++)
            {
                Mp3HuffmanEncoder.TryWrite(writer, table, _quant[pair * 2], _quant[pair * 2 + 1]);
            }
        }
    }
    #endregion

    #region 帧头与边信息
    private void WriteFrameHeader(Mp3BitWriter writer, Int32 padding)
    {
        var mode = _channels == 1 ? 3 : 0;
        writer.Write(0x7FF, 11);
        writer.Write(_versionBits, 2);
        writer.Write(1, 2);          // Layer III
        writer.Write(UseCrc ? 0 : 1, 1); // protection_bit：0 表示带 CRC
        writer.Write(_bitrateIndex, 4);
        writer.Write(_sampleRateIndex, 2);
        writer.Write(padding, 1);
        writer.Write(0, 1);          // private
        writer.Write(mode, 2);
        writer.Write(_modeExtension, 2);  // 0 = L/R，2 = M/S
        writer.Write(0, 1);          // copyright
        writer.Write(1, 1);          // original
        writer.Write(0, 2);          // emphasis
    }

    private void WriteSideInfo(Mp3BitWriter writer)
    {
        if (_mpeg1)
        {
            writer.Write(0, 9);                       // main_data_begin
            writer.Write(0, _channels == 1 ? 5 : 3);  // private_bits
            for (var ch = 0; ch < _channels; ch++)
            {
                writer.Write(0, 4);                   // scfsi
            }

            for (var gr = 0; gr < 2; gr++)
            {
                for (var ch = 0; ch < _channels; ch++)
                {
                    var info = _info[gr][ch];
                    writer.Write(info.Part23Length, 12);
                    writer.Write(info.BigValues, 9);
                    writer.Write(info.GlobalGain, 8);
                    writer.Write(info.ScalefacCompress, 4);
                    writer.Write(0, 1);               // window_switching_flag
                    writer.Write(info.TableSelect[0], 5);
                    writer.Write(info.TableSelect[1], 5);
                    writer.Write(info.TableSelect[2], 5);
                    writer.Write(info.Region0Count, 4);
                    writer.Write(info.Region1Count, 3);
                    writer.Write(0, 1);               // preflag
                    writer.Write(1, 1);               // scalefac_scale
                    writer.Write(0, 1);               // count1table_select
                }
            }
        }
        else
        {
            writer.Write(0, 8);                       // main_data_begin
            writer.Write(0, _channels == 1 ? 1 : 2);  // private_bits

            for (var ch = 0; ch < _channels; ch++)
            {
                var info = _info[0][ch];
                writer.Write(info.Part23Length, 12);
                writer.Write(info.BigValues, 9);
                writer.Write(info.GlobalGain, 8);
                writer.Write(info.ScalefacCompress, 9);
                writer.Write(0, 1);                   // window_switching_flag
                writer.Write(info.TableSelect[0], 5);
                writer.Write(info.TableSelect[1], 5);
                writer.Write(info.TableSelect[2], 5);
                writer.Write(info.Region0Count, 4);
                writer.Write(info.Region1Count, 3);
                writer.Write(1, 1);                   // scalefac_scale
                writer.Write(0, 1);                   // count1table_select
            }
        }
    }
    #endregion

    #region 辅助
    private static Int32 ComputeCrc(Mp3BitWriter header, Int32 startBit, Int32 bitCount, Mp3BitWriter side)
    {
        var crc = 0xFFFF;
        for (var i = 0; i < bitCount; i++) crc = UpdateCrc(crc, header.ReadBitAt(startBit + i));
        for (var i = 0; i < side.Position; i++) crc = UpdateCrc(crc, side.ReadBitAt(i));

        return crc;
    }

    private static Int32 UpdateCrc(Int32 crc, Boolean bit)
    {
        crc ^= bit ? 0x8000 : 0;
        for (var i = 0; i < 8; i++)
        {
            crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x8005) & 0xFFFF : (crc << 1) & 0xFFFF;
        }

        return crc;
    }

    private Int32 NextPadding()
    {
        var step = (_mpeg1 ? 144 : 72) * _bitrate;
        _padAccumulator += step % _sampleRate;
        if (_padAccumulator >= _sampleRate)
        {
            _padAccumulator -= _sampleRate;
            return 1;
        }

        return 0;
    }

    private static Int32 FindBitrateIndex(Int32[] table, Int32 bitrate)
    {
        var best = 1;
        var bestDiff = Int32.MaxValue;
        for (var i = 1; i < 15; i++)
        {
            var diff = Math.Abs(table[i] - bitrate);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = i;
            }
        }

        return best;
    }

    private static Int32 BitsNeeded(Int32 value)
    {
        var bits = 0;
        while (value > 0)
        {
            bits++;
            value >>= 1;
        }

        return bits;
    }

    /// <summary>选择能覆盖所需位宽的 scalefac_compress</summary>
    private static Int32 SelectCompress(Int32 needLow, Int32 needHigh, out Int32 slen1, out Int32 slen2)
    {
        var best = Int32.MaxValue;
        var bestValue = 15;
        slen1 = 4;
        slen2 = 3;
        for (var i = 0; i < ScfcEncode.Length; i++)
        {
            var v = ScfcEncode[i];
            var a = v >> 2;
            var b = v & 3;
            if (a < needLow || b < needHigh) continue;

            var total = a + b;
            if (total < best)
            {
                best = total;
                bestValue = i;
                slen1 = a;
                slen2 = b;
            }
        }

        return bestValue;
    }

    private Single[] BuildBandTilt(Byte[] bands)
    {
        var tilt = new Single[bands.Length];
        var line = 0;
        for (var b = 0; b < bands.Length; b++)
        {
            var width = bands[b];
            var freq = (line + width * 0.5) * _sampleRate / 1152.0;
            line += width;

            var excess = freq / 1000 - 4;
            tilt[b] = excess > 0 ? (Single)(1 + excess * 0.15) : 1f;
        }

        return tilt;
    }

    /// <summary>各频带的中心频率（Hz），用于绝对听阈低通与掩蔽扩散</summary>
    private Single[] BuildBandFreq(Byte[] bands)
    {
        var freqs = new Single[bands.Length];
        var line = 0;
        for (var b = 0; b < bands.Length; b++)
        {
            var width = bands[b];
            freqs[b] = (Single)((line + width * 0.5) * _sampleRate / 1152.0);
            line += width;
        }

        return freqs;
    }
    #endregion

    #region 内部类型
    /// <summary>颗粒/声道编码信息</summary>
    private sealed class GranuleInfo
    {
        public Int32 Part23Length;
        public Int32 BigValues;
        public Int32 GlobalGain;
        public Int32 ScalefacCompress;
        public Int32 Region0Count;
        public Int32 Region1Count;
        public Int32 ScaleBits;
        public Int32 Slen1;
        public Int32 Slen2;
        public Int32[] TableSelect = new Int32[3];
        public Int32[] Scales = new Int32[22];
        public Int32[] Sizes = new Int32[4];
    }
    #endregion
}





