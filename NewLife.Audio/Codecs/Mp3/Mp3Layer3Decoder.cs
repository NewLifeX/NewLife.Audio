using System;
using System.IO;

namespace NewLife.Audio.Codecs;

/// <summary>MP3（MPEG Layer III）解码器</summary>
/// <remarks>
/// 纯托管实现，覆盖 MPEG-1 / MPEG-2 / MPEG-2.5 Layer III，单声道与立体声（含 MS 与强度立体声），
/// 长块、短块与窗口切换，CBR/VBR 均可。流程按 ISO/IEC 11172-3 与 13818-3 组织，
/// 快速 DSP 变换与公开领域实现（minimp3, CC0）保持一致。
/// <para>解码器实例持有比特池与综合滤波器状态，调用方一次传入完整码流即可；
/// 遇到非法或截断帧按帧跳过，不向调用方抛异常。</para>
/// </remarks>
internal sealed class Mp3Layer3Decoder
{
    #region 常量
    /// <summary>比特池最大字节数</summary>
    private const Int32 MaxReservoir = 511;

    /// <summary>短块类型</summary>
    private const Int32 ShortBlock = 2;

    /// <summary>MPEG-1 Layer III 比特率表（kbps），索引 1~14 有效</summary>
    private static readonly Int32[] BitratesMpeg1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];

    /// <summary>MPEG-2/2.5 Layer III 比特率表（kbps），索引 1~14 有效</summary>
    private static readonly Int32[] BitratesLsf = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];

    /// <summary>MPEG-1 采样率</summary>
    private static readonly Int32[] RatesMpeg1 = [44100, 48000, 32000, 0];

    /// <summary>MPEG-2 采样率</summary>
    private static readonly Int32[] RatesMpeg2 = [22050, 24000, 16000, 0];

    /// <summary>MPEG-2.5 采样率</summary>
    private static readonly Int32[] RatesMpeg25 = [11025, 12000, 8000, 0];

    /// <summary>缩放因子分组计数表，按「长块 / 混合块 / 短块」三行排列</summary>
    private static readonly Byte[][] ScfPartitions =
    [
        [6, 5, 5, 5, 6, 5, 5, 5, 6, 5, 7, 3, 11, 10, 0, 0, 7, 7, 7, 0, 6, 6, 6, 3, 8, 8, 5, 0],
        [8, 9, 6, 12, 6, 9, 9, 9, 6, 9, 12, 6, 15, 18, 0, 0, 6, 15, 12, 0, 6, 12, 9, 6, 6, 18, 9, 0],
        [9, 9, 6, 12, 9, 9, 9, 9, 9, 9, 12, 6, 18, 18, 0, 0, 12, 12, 12, 0, 12, 9, 9, 6, 15, 12, 9, 0],
    ];

    /// <summary>MPEG-1 scalefac_compress 到分组计数的映射</summary>
    private static readonly Byte[] ScfcDecode = [0, 1, 2, 3, 12, 5, 6, 7, 9, 10, 11, 13, 14, 15, 18, 19];

    /// <summary>LSF 缩放因子位数模数</summary>
    private static readonly Byte[] LsfMod =
    [
        5, 5, 4, 4,
        5, 5, 4, 1,
        4, 3, 1, 1,
        5, 6, 6, 1,
        4, 4, 4, 1,
        4, 3, 1, 1,
    ];

    /// <summary>预加重表，对应长块第 11~20 带</summary>
    private static readonly Byte[] PreAmp = [1, 1, 1, 1, 2, 2, 3, 3, 3, 2];

    /// <summary>强度立体声的声道间幅度比（MPEG-1 七个位置）</summary>
    private static readonly Single[] Pan =
    [
        0f, 1f, 0.21132487f, 0.78867513f, 0.36602540f, 0.63397460f, 0.5f,
        0.5f, 0.63397460f, 0.36602540f, 0.78867513f, 0.21132487f, 1f, 0f,
    ];

    /// <summary>|x|^(4/3) 快速表</summary>
    private static readonly Single[] Pow43 = BuildPow43();

    private static Single[] BuildPow43()
    {
        var table = new Single[8208];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = (Single)Math.Pow(i, 4.0 / 3.0);
        }

        return table;
    }
    #endregion

    #region 字段
    private readonly Mp3BitReader _bits = new();
    private readonly Mp3BitReader _frameBits = new();
    private readonly Byte[] _reservoir = new Byte[MaxReservoir];
    private Int32 _reservoirLen;
    private readonly Byte[] _mainData = new Byte[MaxReservoir + 2048];
    private readonly Mp3SideInfo[] _side = new Mp3SideInfo[4];
    private readonly Single[] _grbuf = new Single[1152];
    private readonly Single[] _scales = new Single[40];
    private readonly Byte[][] _istPos = [new Byte[40], new Byte[40]];
    private readonly Single[] _overlap = new Single[2 * 32 * 9];
    private readonly Single[] _qmf = new Single[15 * 64];
    private readonly Single[] _lins = new Single[(15 + 18) * 64];
    private readonly Single[] _scratch = new Single[576];



    #endregion

    #region 对外入口
    /// <summary>解码整段 MP3 数据</summary>
    /// <param name="data">MP3 码流，可带 ID3v2 标签与前置垃圾字节</param>
    /// <param name="sampleRate">解码得到的采样率</param>
    /// <param name="channels">解码得到的声道数</param>
    /// <returns>16 位小端交错 PCM，解码失败时返回空数组</returns>
    public Byte[] Decode(ReadOnlySpan<Byte> data, out Int32 sampleRate, out Int32 channels)
    {
        sampleRate = 0;
        channels = 0;
        if (data.Length < 4) return [];

        var input = data.ToArray();
        var offset = SkipId3v2(input);
        var output = new MemoryStream(input.Length * 8);
        var pcm = new Int16[1152 * 2];

        while (offset + 4 <= input.Length)
        {
            if (!TryParseHeader(input, offset, out var header))
            {
                offset++;
                continue;
            }

            if (offset + header.FrameBytes > input.Length) break;

            sampleRate = header.SampleRate;
            channels = header.Channels;

            // Xing/Info/VBRI 信息帧不承载音频：输出等长静音保持采样对齐，且不按音频解码
            if (IsInfoFrame(input, offset, header))
            {
                var infoSamples = (header.Version == Mp3Version.Mpeg1 ? 2 : 1) * 576;
                for (var i = 0; i < infoSamples * channels * 2; i++) output.WriteByte(0);
                offset += header.FrameBytes;
                continue;
            }

            Array.Clear(pcm, 0, pcm.Length);
            DecodeFrame(input, offset, header, pcm);

            var samples = (header.Version == Mp3Version.Mpeg1 ? 2 : 1) * 576;
            var count = samples * channels;
            for (var i = 0; i < count; i++)
            {
                var v = pcm[i];
                output.WriteByte((Byte)(v & 0xFF));
                output.WriteByte((Byte)((v >> 8) & 0xFF));
            }

            offset += header.FrameBytes;
        }

        return output.ToArray();
    }
    #endregion

    #region 帧头与 ID3
    /// <summary>判断是否为 Xing/Info/VBRI 信息帧（不含音频数据）</summary>
    /// <param name="data">码流</param>
    /// <param name="offset">帧起始偏移</param>
    /// <param name="header">已解析的帧头</param>
    /// <returns>信息帧返回 true</returns>
    /// <remarks>信息帧紧跟边信息存放标记：Xing/Info（LAME 的 CBR/VBR 头）或 VBRI。</remarks>
    private static Boolean IsInfoFrame(Byte[] data, Int32 offset, Mp3Header header)
    {
        // 边信息长度：MPEG-1 单声道 17 / 立体声 32；MPEG-2/2.5 单声道 9 / 立体声 17
        var mono = header.Channels == 1;
        var sideInfo = header.Version == Mp3Version.Mpeg1 ? (mono ? 17 : 32) : (mono ? 9 : 17);
        var mark = offset + 4 + (header.HasCrc ? 2 : 0) + sideInfo;
        if (mark + 4 > data.Length) return false;

        if (data[mark] == (Byte)'X' && data[mark + 1] == (Byte)'i' && data[mark + 2] == (Byte)'n' && data[mark + 3] == (Byte)'g') return true;
        if (data[mark] == (Byte)'I' && data[mark + 1] == (Byte)'n' && data[mark + 2] == (Byte)'f' && data[mark + 3] == (Byte)'o') return true;

        // VBRI 固定在帧头后 32 字节处
        var vbri = offset + 4 + 32;
        if (vbri + 4 <= data.Length && data[vbri] == (Byte)'V' && data[vbri + 1] == (Byte)'B' && data[vbri + 2] == (Byte)'R' && data[vbri + 3] == (Byte)'I') return true;

        return false;
    }

    /// <summary>跳过 ID3v2 标签</summary>
    /// <param name="data">码流</param>
    /// <returns>首个音频帧可能出现的偏移</returns>
    public static Int32 SkipId3v2(Byte[] data)
    {
        if (data.Length < 10) return 0;
        if (data[0] != 'I' || data[1] != 'D' || data[2] != '3') return 0;

        var size = (data[6] & 0x7F) << 21 | (data[7] & 0x7F) << 14 | (data[8] & 0x7F) << 7 | (data[9] & 0x7F);
        var skip = 10 + size;
        if ((data[5] & 0x10) != 0) skip += 10;

        return skip < data.Length ? skip : data.Length;
    }

    /// <summary>解析帧头</summary>
    /// <param name="data">码流</param>
    /// <param name="offset">偏移</param>
    /// <param name="header">解析结果</param>
    /// <returns>是否为合法的 Layer III 帧头</returns>
    public static Boolean TryParseHeader(Byte[] data, Int32 offset, out Mp3Header header)
    {
        header = default;
        if (offset + 4 > data.Length) return false;

        var b0 = data[offset];
        var b1 = data[offset + 1];
        var b2 = data[offset + 2];
        var b3 = data[offset + 3];
        if (b0 != 0xFF || (b1 & 0xE0) != 0xE0) return false;

        var versionBits = (b1 >> 3) & 3;
        if (versionBits == 1) return false;
        var version = versionBits switch
        {
            0 => Mp3Version.Mpeg25,
            2 => Mp3Version.Mpeg2,
            _ => Mp3Version.Mpeg1,
        };

        // 只支持 Layer III
        if (((b1 >> 1) & 3) != 1) return false;

        var bitrateIndex = (b2 >> 4) & 0x0F;
        var rateIndex = (b2 >> 2) & 3;
        if (bitrateIndex == 0 || bitrateIndex == 15 || rateIndex == 3) return false;

        var rates = version switch
        {
            Mp3Version.Mpeg1 => RatesMpeg1,
            Mp3Version.Mpeg2 => RatesMpeg2,
            _ => RatesMpeg25,
        };
        var sampleRate = rates[rateIndex];
        var bitrates = version == Mp3Version.Mpeg1 ? BitratesMpeg1 : BitratesLsf;
        var bitrate = bitrates[bitrateIndex] * 1000;

        var padding = (b2 >> 1) & 1;
        var frameBytes = version == Mp3Version.Mpeg1
            ? 144 * bitrate / sampleRate + padding
            : 72 * bitrate / sampleRate + padding;
        if (frameBytes <= 4) return false;

        var mode = (b3 >> 6) & 3;
        header = new Mp3Header
        {
            Version = version,
            BitrateIndex = bitrateIndex,
            SampleRateIndex = rateIndex,
            SampleRate = sampleRate,
            Bitrate = bitrate,
            FrameBytes = frameBytes,
            Padding = padding,
            Mode = mode,
            Channels = mode == 3 ? 1 : 2,
            HasCrc = (b1 & 1) == 0,
            ModeExtension = (b3 >> 4) & 3,
        };

        return true;
    }
    #endregion

    #region 缩放因子带
    private Byte[] _shortBands;
    private Byte[] _mixedBands;
    private Int32 _bandRate;

    /// <summary>取短块的缩放因子带宽度，每带按三个窗口重复</summary>
    /// <param name="sampleRate">采样率</param>
    /// <returns>39 个带的宽度</returns>
    private Byte[] GetShortBands(Int32 sampleRate)
    {
        if (_bandRate == sampleRate && _shortBands != null) return _shortBands;

        var widths = Mp3Tables.GetShortBands(sampleRate);
        if (widths == null) return [];

        var bands = new Byte[widths.Length * 3];
        for (var i = 0; i < widths.Length; i++)
        {
            bands[i * 3] = bands[i * 3 + 1] = bands[i * 3 + 2] = widths[i];
        }

        _bandRate = sampleRate;
        _shortBands = bands;

        return bands;
    }

    /// <summary>取混合块的缩放因子带宽度，前部为长块带、其余为短块带</summary>
    /// <param name="sampleRate">采样率</param>
    /// <param name="longBands">前部长块带数</param>
    /// <returns>带宽度表</returns>
    private Byte[] GetMixedBands(Int32 sampleRate, Int32 longBands)
    {
        if (_bandRate == sampleRate && _mixedBands != null) return _mixedBands;

        var longWidths = Mp3Tables.GetLongBands(sampleRate);
        var shortWidths = Mp3Tables.GetShortBands(sampleRate);
        if (longWidths == null || shortWidths == null) return [];

        // 前 36 条谱线按长块处理，其余按短块带从第 3 带开始展开
        var list = new System.Collections.Generic.List<Byte>(longBands + 30);
        for (var i = 0; i < longBands && i < longWidths.Length; i++) list.Add(longWidths[i]);
        for (var i = 3; i < shortWidths.Length; i++)
        {
            list.Add(shortWidths[i]);
            list.Add(shortWidths[i]);
            list.Add(shortWidths[i]);
        }

        _bandRate = sampleRate;
        _mixedBands = list.ToArray();

        return _mixedBands;
    }
    #endregion

    #region 帧解码
    private void DecodeFrame(Byte[] data, Int32 offset, Mp3Header header, Int16[] pcm)
    {
        var mpeg1 = header.Version == Mp3Version.Mpeg1;
        var sideOffset = offset + 4 + (header.HasCrc ? 2 : 0);
        var sideBytes = mpeg1
            ? (header.Channels == 1 ? 17 : 32)
            : (header.Channels == 1 ? 9 : 17);
        var mainOffset = sideOffset + sideBytes;
        var mainBytes = offset + header.FrameBytes - mainOffset;
        if (mainBytes <= 0) return;

        _frameBits.Init(data, sideOffset * 8, (offset + header.FrameBytes) * 8);
        var mainDataBegin = ReadSideInfo(header);
        if (mainDataBegin < 0) return;

        // 恢复比特池：先取上一帧遗留的主数据，再拼上本帧主数据
        var keep = Math.Min(_reservoirLen, mainDataBegin);
        if (keep > 0) Array.Copy(_reservoir, _reservoirLen - keep, _mainData, 0, keep);
        Array.Copy(data, mainOffset, _mainData, keep, mainBytes);
        var totalBytes = keep + mainBytes;
        _bits.Init(_mainData, 0, totalBytes * 8);

        // 比特池尚未攒够时本帧无法解码，但仍要保存主数据
        if (_reservoirLen < mainDataBegin)
        {
            SaveReservoir(totalBytes);
            return;
        }

        var granules = mpeg1 ? 2 : 1;
        var isStereo = header.Channels == 2;
        var msStereo = isStereo && (header.ModeExtension & 2) != 0;
        var iStereo = isStereo && mpeg1 && (header.ModeExtension & 1) != 0;

        for (var gr = 0; gr < granules; gr++)
        {
            Array.Clear(_grbuf, 0, _grbuf.Length);

            for (var ch = 0; ch < header.Channels; ch++)
            {
                var info = _side[gr * header.Channels + ch];
                var limit = _bits.Position + info.Part23Length;
                DecodeScales(info, ch, header, msStereo);
                DecodeHuffman(info, ch, limit);
            }

            if (iStereo)
                IntensityStereo(header);
            else if (msStereo)
                MidSideStereo(576);


            for (var ch = 0; ch < header.Channels; ch++)
            {
                var info = _side[gr * header.Channels + ch];
                var spectrum = ch * 576;
                var overlap = ch * 32 * 9;
                var longBands = info.MixedBlockFlag ? 2 : 0;
                var aaBands = 31;

                if (info.ShortBands > 0)
                {
                    aaBands = longBands - 1;
                    Mp3Layer3Dsp.Reorder(_grbuf, spectrum + longBands * 18, _scratch, info.Bands, info.LongBands);
                }

                Mp3Layer3Dsp.AntiAlias(_grbuf, spectrum, aaBands);
                Mp3Layer3Dsp.Imdct(_grbuf, spectrum, _overlap, overlap, info.BlockType, longBands);
                Mp3Layer3Dsp.ChangeSign(_grbuf, spectrum);
            }

            Mp3Layer3Dsp.SynthGranule(_qmf, _grbuf, header.Channels, pcm, gr * 576 * header.Channels, _lins);
        }

        SaveReservoir(totalBytes);
    }

    private void SaveReservoir(Int32 totalBytes)
    {
        var pos = (_bits.Position + 7) / 8;
        var remains = totalBytes - pos;
        if (remains > MaxReservoir)
        {
            pos += remains - MaxReservoir;
            remains = MaxReservoir;
        }
        if (remains < 0) remains = 0;

        if (remains > 0) Array.Copy(_mainData, pos, _reservoir, 0, remains);
        _reservoirLen = remains;
    }
    #endregion

    #region 边信息
    private Int32 ReadSideInfo(Mp3Header header)
    {
        var bits = _frameBits;
        var mpeg1 = header.Version == Mp3Version.Mpeg1;
        var mono = header.Channels == 1;
        var grCount = mono ? 1 : 2;
        Int32 mainDataBegin;

        if (mpeg1)
        {
            mainDataBegin = (Int32)bits.Read(9);
        }
        else
        {
            var pos = bits.Position;
            mainDataBegin = (Int32)bits.Read(8 + grCount) >> grCount;
            bits.Seek(pos + 8 + grCount);
        }

        var scfsi = new Int32[header.Channels];
        if (mpeg1)
        {
            bits.Skip(mono ? 5 : 3);
            for (var ch = 0; ch < header.Channels; ch++)
            {
                scfsi[ch] = (Int32)bits.Read(4);
            }
        }

        var granuleCount = mpeg1 ? 2 : 1;
        for (var gr = 0; gr < granuleCount; gr++)
        {
            for (var ch = 0; ch < header.Channels; ch++)
            {
                var info = _side[gr * header.Channels + ch] ??= new Mp3SideInfo();
                info.Part23Length = (Int32)bits.Read(12);
                info.BigValues = (Int32)bits.Read(9);
                if (info.BigValues > 288) return -1;
                info.GlobalGain = (Int32)bits.Read(8);
                info.ScalefacCompress = (Int32)bits.Read(mpeg1 ? 4 : 9);
                info.Scfsi = mpeg1 && gr == 1 ? scfsi[ch] : 0;
                info.Bands = Mp3Tables.GetLongBands(header.SampleRate);
                info.LongBands = 22;
                info.ShortBands = 0;

                if (bits.ReadBit() != 0)
                {
                    info.BlockType = (Int32)bits.Read(2);
                    if (info.BlockType == 0) return -1;
                    info.MixedBlockFlag = bits.ReadBit() != 0;
                    info.RegionCount[0] = 7;
                    info.RegionCount[1] = 255;
                    info.RegionCount[2] = 255;
                    if (info.BlockType == ShortBlock)
                    {
                        if (info.MixedBlockFlag)
                        {
                            info.LongBands = mpeg1 ? 8 : 6;
                            info.ShortBands = 30;
                            info.Bands = GetMixedBands(header.SampleRate, info.LongBands);
                        }
                        else
                        {
                            info.RegionCount[0] = 8;
                            info.LongBands = 0;
                            info.ShortBands = 39;
                            info.Bands = GetShortBands(header.SampleRate);
                        }
                    }

                    info.TableSelect[0] = (Int32)bits.Read(5);
                    info.TableSelect[1] = (Int32)bits.Read(5);
                    info.SubblockGain[0] = (Int32)bits.Read(3);
                    info.SubblockGain[1] = (Int32)bits.Read(3);
                    info.SubblockGain[2] = (Int32)bits.Read(3);
                }
                else
                {
                    info.BlockType = 0;
                    info.MixedBlockFlag = false;
                    info.TableSelect[0] = (Int32)bits.Read(5);
                    info.TableSelect[1] = (Int32)bits.Read(5);
                    info.TableSelect[2] = (Int32)bits.Read(5);
                    info.RegionCount[0] = (Int32)bits.Read(4);
                    info.RegionCount[1] = (Int32)bits.Read(3);
                    info.RegionCount[2] = 255;
                }

                info.Preflag = mpeg1 ? bits.ReadBit() != 0 : info.ScalefacCompress >= 500;
                info.ScalefacScale = bits.ReadBit() != 0;
                info.Count1Table = bits.ReadBit() != 0;
            }
        }

        return mainDataBegin;
    }
    #endregion

    #region 缩放因子
    private void DecodeScales(Mp3SideInfo info, Int32 channel, Mp3Header header, Boolean msStereo)
    {
        var mpeg1 = header.Version == Mp3Version.Mpeg1;
        var group = info.ShortBands > 0 ? (info.LongBands > 0 ? 1 : 2) : 0;
        var sizes = new Int32[4];
        var iscf = new Byte[40];
        var scfsi = info.Scfsi;
        var partition = ScfPartitions[group];
        var partOffset = 0;

        if (mpeg1)
        {
            var part = ScfcDecode[info.ScalefacCompress];
            sizes[0] = sizes[1] = part >> 2;
            sizes[2] = sizes[3] = part & 3;
        }
        else
        {
            var ist = (header.ModeExtension & 1) != 0 && channel > 0 ? 1 : 0;
            var sfc = info.ScalefacCompress >> ist;
            var k = ist * 12;
            while (sfc >= 0)
            {
                var modprod = 1;
                for (var i = 3; i >= 0; i--)
                {
                    sizes[i] = sfc / modprod % LsfMod[k + i];
                    modprod *= LsfMod[k + i];
                }
                sfc -= modprod;
                k += 4;
            }
            partOffset = Math.Min(k, partition.Length - 1);
            scfsi = -16;
        }

        ReadScales(iscf, _istPos[channel], sizes, partition, partOffset, scfsi);

        var scfShift = info.ScalefacScale ? 2 : 1;
        if (info.ShortBands > 0)
        {
            var sh = 3 - scfShift;
            for (var i = 0; i < info.ShortBands; i += 3)
            {
                iscf[info.LongBands + i + 0] += (Byte)(info.SubblockGain[0] << sh);
                iscf[info.LongBands + i + 1] += (Byte)(info.SubblockGain[1] << sh);
                iscf[info.LongBands + i + 2] += (Byte)(info.SubblockGain[2] << sh);
            }
        }
        else if (info.Preflag)
        {
            for (var i = 0; i < 10; i++)
            {
                iscf[11 + i] += PreAmp[i];
            }
        }

        var gainExp = info.GlobalGain - 214 - (msStereo ? 2 : 0);
        var count = info.LongBands + info.ShortBands;
        for (var i = 0; i < count && i < _scales.Length; i++)
        {
            _scales[i] = (Single)Math.Pow(2.0, (gainExp - (iscf[i] << scfShift)) / 4.0);
        }
    }

    private void ReadScales(Byte[] scf, Byte[] istPos, Int32[] sizes, Byte[] partition, Int32 partOffset, Int32 scfsi)
    {
        var offset = 0;
        for (var i = 0; i < 4 && partOffset + i < partition.Length && partition[partOffset + i] != 0; i++, scfsi *= 2)
        {
            var count = partition[partOffset + i];
            if ((scfsi & 8) != 0)
            {
                Array.Copy(istPos, offset, scf, offset, count);
            }
            else
            {
                var bits = sizes[i];
                if (bits == 0)
                {
                    Array.Clear(scf, offset, count);
                    Array.Clear(istPos, offset, count);
                }
                else
                {
                    var maxScf = scfsi < 0 ? (1 << bits) - 1 : -1;
                    for (var k = 0; k < count; k++)
                    {
                        var v = (Int32)_bits.Read(bits);
                        istPos[offset + k] = (Byte)(v == maxScf ? 0xFF : v);
                        scf[offset + k] = (Byte)v;
                    }
                }
            }
            offset += count;
        }

        if (offset + 2 < scf.Length)
        {
            scf[offset] = scf[offset + 1] = scf[offset + 2] = 0;
        }
    }
    #endregion

    #region 霍夫曼解码
    private void DecodeHuffman(Mp3SideInfo info, Int32 channel, Int32 limit)
    {
        var dst = channel * 576;
        var sfb = info.Bands;
        var band = 0;
        var scale = 0;
        var bigValues = info.BigValues;
        var region = 0;
        var one = 0f;

        while (bigValues > 0 && region < 3)
        {
            var table = info.TableSelect[region];
            var regionBands = info.RegionCount[region];
            region++;
            var offset = Mp3Huffman.GetOffset(table);
            var linbits = offset < 0 ? 0 : Mp3Huffman.LinBits[table];

            do
            {
                if (band >= sfb.Length) break;
                var pairsInBand = sfb[band] / 2;
                band++;
                var pairs = Math.Min(bigValues, pairsInBand);
                one = _scales[Math.Min(scale, _scales.Length - 1)];
                scale++;
                for (var p = 0; p < pairs; p++)
                {
                    if (offset < 0)
                    {
                        _grbuf[dst++] = 0;
                        _grbuf[dst++] = 0;
                    }
                    else
                    {
                        ReadPair(offset, linbits, one, dst);
                        dst += 2;
                    }
                }
                bigValues -= pairsInBand;
            } while (bigValues > 0 && --regionBands >= 0);
        }

        // count1 区，按比特上限终止
        var countTable = info.Count1Table ? Mp3Huffman.TableB : Mp3Huffman.TableA;
        var np = 1 - bigValues;
        while (dst < channel * 576 + 576)
        {
            var leaf = (Int32)countTable[(Int32)_bits.Peek(4)];
            if ((leaf & 8) == 0)
            {
                var extra = leaf & 3;
                var index = (leaf >> 3) + (Int32)(_bits.Peek(4 + extra) & (UInt32)((1 << extra) - 1));
                leaf = countTable[index];
            }
            _bits.Skip(leaf & 7);
            if (_bits.Position > limit) break;

            if (--np == 0)
            {
                if (band >= sfb.Length) break;
                np = sfb[band] / 2;
                band++;
                if (np == 0) break;
                one = _scales[Math.Min(scale, _scales.Length - 1)];
                scale++;
            }

            for (var s = 0; s < 2; s++)
            {
                _grbuf[dst++] = (leaf & (128 >> s)) != 0 ? ReadCount1Value(one) : 0;
            }

            if (--np == 0)
            {
                if (band >= sfb.Length) break;
                np = sfb[band] / 2;
                band++;
                if (np == 0) break;
                one = _scales[Math.Min(scale, _scales.Length - 1)];
                scale++;
            }

            for (var s = 2; s < 4; s++)
            {
                _grbuf[dst++] = (leaf & (128 >> s)) != 0 ? ReadCount1Value(one) : 0;
            }
        }

        _bits.Seek(limit);
    }

    private Single ReadCount1Value(Single one) => _bits.ReadBit() != 0 ? -one : one;

    private void ReadPair(Int32 tableOffset, Int32 linbits, Single scale, Int32 target)
    {
        var w = 5;
        var leaf = (Int32)Mp3Huffman.Tabs[tableOffset + (Int32)_bits.Peek(w)];
        while (leaf < 0)
        {
            _bits.Skip(w);
            w = leaf & 7;
            leaf = (Int32)Mp3Huffman.Tabs[tableOffset + (Int32)_bits.Peek(w) - (leaf >> 3)];
        }
        _bits.Skip(leaf >> 8);

        for (var i = 0; i < 2; i++, leaf >>= 4)
        {
            var value = leaf & 0x0F;
            if (value == 15 && linbits > 0)
            {
                value += (Int32)_bits.Read(linbits);
            }

            var negative = value != 0 && _bits.ReadBit() != 0;
            var magnitude = Pow43[Math.Min(value, Pow43.Length - 1)] * scale;
            _grbuf[target + i] = negative ? -magnitude : magnitude;
        }
    }
    #endregion

    #region 立体声
    private void MidSideStereo(Int32 count)
    {
        for (var i = 0; i < count; i++)
        {
            var a = _grbuf[i];
            var b = _grbuf[i + 576];
            _grbuf[i] = a + b;
            _grbuf[i + 576] = a - b;
        }
    }

    private void IntensityStereo(Mp3Header header)
    {
        var info = _side[0];
        var bands = info.Bands;
        var totalBands = info.LongBands + info.ShortBands;
        var istPos = _istPos[1];
        var maxBand = new Int32[3];
        maxBand[0] = maxBand[1] = maxBand[2] = -1;

        var offset = 576;
        for (var i = 0; i < totalBands && i < bands.Length; i++)
        {
            var width = bands[i];
            for (var k = 0; k < width - 1; k += 2)
            {
                if (_grbuf[offset + k] != 0 || _grbuf[offset + k + 1] != 0)
                {
                    maxBand[i % 3] = i;
                    break;
                }
            }
            offset += width;
        }

        if (info.LongBands > 0)
        {
            var m = Math.Max(Math.Max(maxBand[0], maxBand[1]), maxBand[2]);
            maxBand[0] = maxBand[1] = maxBand[2] = m;
        }

        var blocks = info.ShortBands > 0 ? 3 : 1;
        for (var i = 0; i < blocks; i++)
        {
            const Int32 defaultPos = 3;
            var top = totalBands - blocks + i;
            var prev = top - blocks;
            if (top >= 0 && top < istPos.Length)
            {
                istPos[top] = (Byte)(maxBand[i] >= prev && prev >= 0 && prev < istPos.Length ? defaultPos : istPos[Math.Max(0, prev)]);
            }
        }

        var ms = (header.ModeExtension & 2) != 0;
        offset = 0;
        for (var i = 0; i < totalBands && i < bands.Length; i++)
        {
            var width = bands[i];
            var pos = istPos[i];
            if (i > maxBand[i % 3] && pos < 7)
            {
                var s = ms ? 1.41421356f : 1f;
                var kl = Pan[2 * pos] * s;
                var kr = Pan[2 * pos + 1] * s;
                for (var k = 0; k < width; k++)
                {
                    var v = _grbuf[offset + k];
                    _grbuf[offset + k + 576] = v * kr;
                    _grbuf[offset + k] = v * kl;
                }
            }
            else if (ms)
            {
                for (var k = 0; k < width; k++)
                {
                    var a = _grbuf[offset + k];
                    var b = _grbuf[offset + k + 576];
                    _grbuf[offset + k] = a + b;
                    _grbuf[offset + k + 576] = a - b;
                }
            }
            offset += width;
        }
    }
    #endregion

    #region 内部类型
    /// <summary>边信息</summary>
    private sealed class Mp3SideInfo
    {
        public Int32 Part23Length;
        public Int32 BigValues;
        public Int32 GlobalGain;
        public Int32 ScalefacCompress;
        public Int32 BlockType;
        public Boolean MixedBlockFlag;
        public Boolean Preflag;
        public Boolean ScalefacScale;
        public Boolean Count1Table;
        public Int32 Scfsi;
        public readonly Int32[] TableSelect = new Int32[3];
        public readonly Int32[] SubblockGain = new Int32[3];
        public readonly Int32[] RegionCount = new Int32[3];
        public Byte[] Bands;
        public Int32 LongBands;
        public Int32 ShortBands;
    }
    #endregion
}

/// <summary>MPEG 版本</summary>
internal enum Mp3Version
{
    /// <summary>MPEG-2.5</summary>
    Mpeg25 = 0,

    /// <summary>MPEG-2</summary>
    Mpeg2 = 2,

    /// <summary>MPEG-1</summary>
    Mpeg1 = 3,
}

/// <summary>Layer III 帧头</summary>
internal struct Mp3Header
{
    public Mp3Version Version;
    public Int32 SampleRateIndex;
    public Int32 SampleRate;
    public Int32 BitrateIndex;
    public Int32 Bitrate;
    public Int32 FrameBytes;
    public Int32 Padding;
    public Int32 Mode;
    public Int32 ModeExtension;
    public Int32 Channels;
    public Boolean HasCrc;
}






